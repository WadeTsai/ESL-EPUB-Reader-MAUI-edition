// ============================================================================
// Services/EnglishDictionaryService.cs
// ============================================================================
// English → English lookups, powered by the free & keyless web API
//
//     https://api.dictionaryapi.dev/api/v2/entries/en/{word}
//
// Why this API?
//   * No API key or account required — the app works out of the box.
//   * Returns definitions, part of speech, IPA pronunciation, example
//     sentences and synonyms: exactly the information an ESL reader needs.
//
// PHRASE HANDLING:
//   The API only knows single words and a few fixed expressions. When the
//   reader selects a PHRASE ("give up", "in spite of"), we:
//     1. try the whole phrase first (catches idioms the API does know);
//     2. if that 404s, look up EACH word of the phrase individually and
//        concatenate the results, prefixing every sense with the word it
//        belongs to, so the reader can still decode the phrase word by word.
//
// RESILIENCE:
//   * 8-second timeout so a dead network never hangs the UI.
//   * Every failure path returns a result object with a human-readable
//     StatusMessage instead of throwing — the side panel always has
//     something sensible to display.
// ============================================================================

using System.Net;
using System.Net.Http;
using System.Text.Json;
using EslEpubReader.Models;

namespace EslEpubReader.Services;

public sealed class EnglishDictionaryService
{
    /// <summary>Base endpoint; the looked-up term is appended URL-escaped.</summary>
    private const string ApiBase = "https://api.dictionaryapi.dev/api/v2/entries/en/";

    /// <summary>Cap on senses per lookup so one word ("set" has dozens of
    /// meanings) cannot flood the panel and bury the Chinese results.</summary>
    private const int MaxSenses = 12;

    /// <summary>
    /// One shared HttpClient for the whole app lifetime. Creating a new
    /// HttpClient per request is a well-known anti-pattern (socket
    /// exhaustion); a single static instance reuses connections.
    /// </summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    /// <summary>
    /// Tiny in-memory cache: term -> result. Readers often re-select the
    /// same word; caching makes the second lookup instant and is polite to
    /// the free API. Unbounded growth is fine for a reading session
    /// (hundreds of entries at most).
    /// </summary>
    private readonly Dictionary<string, EnglishLookupResult> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Look up a word or phrase. NEVER throws — errors come back inside the
    /// result's StatusMessage.
    /// </summary>
    /// <param name="term">Already-normalized selection text (trimmed,
    /// punctuation stripped by the caller).</param>
    /// <param name="ct">Cancellation token — cancelled when the user selects
    /// a NEWER term before this lookup finishes, so stale results are
    /// never displayed.</param>
    public async Task<EnglishLookupResult> LookupAsync(string term, CancellationToken ct)
    {
        // Serve from cache when we already answered this exact term.
        if (_cache.TryGetValue(term, out EnglishLookupResult? cached)) return cached;

        try
        {
            // ---- Attempt 1: the whole selection as-is (word OR idiom). ----
            EnglishLookupResult? whole = await QueryApiAsync(term, ct);
            if (whole is not null)
            {
                _cache[term] = whole;
                return whole;
            }

            // ---- Attempt 2 (phrases only): decode word-by-word. -----------
            string[] words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1)
            {
                var combined = new List<EnglishSense>();
                foreach (string word in words)
                {
                    // Skip words shorter than 2 letters ("a", "I") — their
                    // definitions add noise, not help.
                    if (word.Length < 2) continue;

                    EnglishLookupResult? one = await QueryApiAsync(word, ct);
                    if (one is null) continue;

                    // Prefix each sense with its word so the reader can see
                    // which part of the phrase it explains; keep only the
                    // first few senses per word to stay compact.
                    combined.AddRange(one.Senses.Take(3).Select(s => new EnglishSense
                    {
                        PartOfSpeech = $"{word} · {s.PartOfSpeech}",
                        Definition = s.Definition,
                        Example = s.Example,
                        Synonyms = s.Synonyms,
                    }));
                }

                if (combined.Count > 0)
                {
                    var phraseResult = new EnglishLookupResult
                    {
                        Term = term,
                        Senses = combined,
                        StatusMessage = "Phrase not in dictionary — showing each word separately.",
                    };
                    _cache[term] = phraseResult;
                    return phraseResult;
                }
            }

            // ---- Nothing found at all. -------------------------------------
            var notFound = new EnglishLookupResult
            {
                Term = term,
                Senses = [],
                StatusMessage = "No English definition found.",
            };
            _cache[term] = notFound;   // cache negatives too — avoids re-querying
            return notFound;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user already selected something newer — rethrow so the
            // caller can silently drop this obsolete lookup.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Network down / API hiccup / malformed answer: tell the user
            // gently instead of crashing. NOT cached, so it retries next time.
            return new EnglishLookupResult
            {
                Term = term,
                Senses = [],
                StatusMessage = "English dictionary is unreachable (check your internet connection).",
            };
        }
    }

    /// <summary>
    /// One raw API call. Returns null for "word not found" (HTTP 404) so the
    /// caller can distinguish that from transport errors (which throw).
    /// </summary>
    private static async Task<EnglishLookupResult?> QueryApiAsync(string term, CancellationToken ct)
    {
        using HttpResponseMessage response =
            await Http.GetAsync(ApiBase + Uri.EscapeDataString(term), ct);

        // 404 is the API's documented "no such word" answer — a normal case.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();   // other errors -> HttpRequestException

        // ---- Parse the JSON. -------------------------------------------------
        // Response shape (abridged):
        // [ { "word": "...", "phonetic": "/.../",
        //     "meanings": [ { "partOfSpeech": "noun",
        //                     "definitions": [ { "definition": "...",
        //                                        "example": "...",
        //                                        "synonyms": ["..."] } ] } ] } ]
        // We use JsonDocument (not serialization classes) because we only
        // need a handful of fields and want to be tolerant of missing ones.
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        string phonetic = "";
        var senses = new List<EnglishSense>();

        foreach (JsonElement entry in doc.RootElement.EnumerateArray())
        {
            // Take the first non-empty phonetic we encounter. It may live in
            // "phonetic" directly or inside the "phonetics" array.
            if (phonetic.Length == 0)
            {
                if (entry.TryGetProperty("phonetic", out JsonElement ph) &&
                    ph.ValueKind == JsonValueKind.String)
                    phonetic = ph.GetString() ?? "";

                if (phonetic.Length == 0 &&
                    entry.TryGetProperty("phonetics", out JsonElement phArr) &&
                    phArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement p in phArr.EnumerateArray())
                    {
                        if (p.TryGetProperty("text", out JsonElement txt) &&
                            txt.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrEmpty(txt.GetString()))
                        {
                            phonetic = txt.GetString()!;
                            break;
                        }
                    }
                }
            }

            // Flatten meanings -> senses, respecting the MaxSenses cap.
            if (!entry.TryGetProperty("meanings", out JsonElement meanings) ||
                meanings.ValueKind != JsonValueKind.Array) continue;

            foreach (JsonElement meaning in meanings.EnumerateArray())
            {
                string pos = meaning.TryGetProperty("partOfSpeech", out JsonElement posEl) &&
                             posEl.ValueKind == JsonValueKind.String
                             ? posEl.GetString() ?? "" : "";

                if (!meaning.TryGetProperty("definitions", out JsonElement defs) ||
                    defs.ValueKind != JsonValueKind.Array) continue;

                foreach (JsonElement def in defs.EnumerateArray())
                {
                    if (senses.Count >= MaxSenses) break;

                    string definition = def.TryGetProperty("definition", out JsonElement d) &&
                                        d.ValueKind == JsonValueKind.String
                                        ? d.GetString() ?? "" : "";
                    if (definition.Length == 0) continue;

                    string example = def.TryGetProperty("example", out JsonElement ex) &&
                                     ex.ValueKind == JsonValueKind.String
                                     ? ex.GetString() ?? "" : "";

                    // Synonyms arrive as a string array; join the first few.
                    string synonyms = "";
                    if (def.TryGetProperty("synonyms", out JsonElement syn) &&
                        syn.ValueKind == JsonValueKind.Array)
                    {
                        synonyms = string.Join(", ",
                            syn.EnumerateArray()
                               .Where(s => s.ValueKind == JsonValueKind.String)
                               .Select(s => s.GetString())
                               .Take(5));
                    }

                    senses.Add(new EnglishSense
                    {
                        PartOfSpeech = pos,
                        Definition = definition,
                        Example = example,
                        Synonyms = synonyms,
                    });
                }
            }
        }

        // An entry with zero usable senses counts as "not found".
        if (senses.Count == 0) return null;

        return new EnglishLookupResult
        {
            Term = term,
            Phonetic = phonetic,
            Senses = senses,
        };
    }
}
