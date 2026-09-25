// ============================================================================
// Services/BingDictionaryService.cs
// ============================================================================
// The bilingual dictionary source ("Bing Dict" in the panel): ranked
// translations of a single English word/short phrase into the target
// language, with part of speech and back-translations — served by Bing's
// tlookupv3 endpoint, the same data behind the word cards on
// bing.com/translator. (Replaced the earlier Google-based implementation.)
//
// RESPONSE FORMAT (success is a JSON ARRAY):
//
//   [ { "normalizedSource": "goddess", "displaySource": "goddess",
//       "translations": [
//         { "displayTarget": "女神",         <- the translated term
//           "posTag": "NOUN",                <- part of speech (uppercase)
//           "confidence": 0.65,              <- results arrive ranked by this
//           "backTranslations": [ { "displayText": "goddess", ... }, ... ] },
//         ... ] } ]
//
// Errors are OBJECTS: {"statusCode":400} notably means the LANGUAGE PAIR is
// not supported — Bing's dictionary covers ~50 languages, fewer than its
// translator.
//
// TRADITIONAL CHINESE SPECIAL CASE (the app's default!):
//   tlookupv3 supports zh-Hans but NOT zh-Hant. For Traditional targets the
//   lookup is performed in zh-Hans and the returned terms are converted to
//   Traditional characters:
//     * on Windows with the built-in converter (kernel32!LCMapStringEx,
//       LCMAP_TRADITIONAL_CHINESE) — offline, no mapping tables to ship;
//     * elsewhere (Linux, macOS) with ONE extra Bing Translator call,
//       zh-Hans → zh-Hant, carrying every term of the lookup at once (one
//       per line). This also localizes vocabulary (软件 → 軟體), not just
//       characters. If that call fails or its lines don't line up with the
//       input, the Simplified terms are shown rather than failing the lookup.
// ============================================================================

using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EslEpubReader.Models;

namespace EslEpubReader.Services;

public sealed partial class BingDictionaryService
{
    /// <summary>Maximum entries returned per lookup — keeps the panel readable.</summary>
    private const int MaxEntries = 15;

    /// <summary>
    /// Target language code (Microsoft Translator codes, from
    /// LanguageCatalog), settable at runtime from the language picker.
    /// </summary>
    public string TargetLanguage { get; set; } = LanguageCatalog.DefaultCode;

    /// <summary>Session cache keyed by "languageCode|word" (see
    /// BingTranslateService for the rationale).</summary>
    private readonly Dictionary<string, ChineseLookupResult> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Look up an English word/short phrase in Bing Dict and return ranked
    /// translations in the current TargetLanguage. NEVER throws for
    /// network/parse problems — failures come back in StatusMessage.
    /// </summary>
    /// <param name="term">Normalized selection text (the caller already
    /// filters out sentence-length selections).</param>
    /// <param name="ct">Cancelled when a newer selection supersedes this one.</param>
    public async Task<ChineseLookupResult> LookupAsync(string term, CancellationToken ct)
    {
        string cacheKey = $"{TargetLanguage}|{term}";
        if (_cache.TryGetValue(cacheKey, out ChineseLookupResult? cached)) return cached;

        // Traditional-Chinese detour (see class comment): ask the endpoint
        // for Simplified, convert the terms afterwards. "yue" (Cantonese,
        // written in Traditional) gets the same treatment.
        bool convertToTraditional = TargetLanguage is "zh-Hant" or "yue" or "lzh";
        string lookupLanguage = convertToTraditional ? "zh-Hans" : TargetLanguage;

        try
        {
            using JsonDocument doc = await BingSession.PostAsync("tlookupv3",
                new Dictionary<string, string>
                {
                    ["from"] = "en",
                    ["text"] = term,
                    ["to"] = lookupLanguage,
                }, ct);

            JsonElement root = doc.RootElement;

            // statusCode 400 = language pair not in Bing Dict's coverage.
            if (root.ValueKind == JsonValueKind.Object)
            {
                int code = root.TryGetProperty("statusCode", out JsonElement sc) &&
                           sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : 0;
                ChineseLookupResult unsupported = new()
                {
                    Term = term,
                    Entries = [],
                    StatusMessage = code == 400
                        ? "Bing Dict does not cover this language — see the Bing Translator section above."
                        : $"Bing Dict returned an error (code {code}).",
                };
                if (code == 400) _cache[cacheKey] = unsupported;   // coverage won't change mid-session
                return unsupported;
            }

            // ---- Parse the entry list (defensively — undocumented API). ----
            var entries = new List<BingDictionaryEntry>();
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 &&
                root[0].TryGetProperty("translations", out JsonElement translations) &&
                translations.ValueKind == JsonValueKind.Array)
            {
                // Bing pre-sorts by confidence — keep its order.
                foreach (JsonElement t in translations.EnumerateArray())
                {
                    if (entries.Count >= MaxEntries) break;

                    string target = t.TryGetProperty("displayTarget", out JsonElement dt) &&
                                    dt.ValueKind == JsonValueKind.String ? dt.GetString() ?? "" : "";
                    if (target.Length == 0) continue;
                    if (convertToTraditional && OperatingSystem.IsWindows())
                        target = ToTraditionalChinese(target);

                    // posTag arrives UPPERCASE ("NOUN") — lowercase it to
                    // match the English–English section's badge style.
                    string pos = t.TryGetProperty("posTag", out JsonElement pt) &&
                                 pt.ValueKind == JsonValueKind.String
                                 ? (pt.GetString() ?? "").ToLowerInvariant() : "";

                    string backTranslations = "";
                    if (t.TryGetProperty("backTranslations", out JsonElement bts) &&
                        bts.ValueKind == JsonValueKind.Array)
                    {
                        backTranslations = string.Join(", ",
                            bts.EnumerateArray()
                               .Select(b => b.TryGetProperty("displayText", out JsonElement dtx) &&
                                            dtx.ValueKind == JsonValueKind.String ? dtx.GetString() : null)
                               .Where(s => !string.IsNullOrEmpty(s))
                               .Take(6));
                    }

                    entries.Add(new BingDictionaryEntry
                    {
                        PartOfSpeech = pos,
                        Term = target,
                        BackTranslations = backTranslations,
                    });
                }
            }

            if (convertToTraditional && !OperatingSystem.IsWindows() && entries.Count > 0)
                entries = await ToTraditionalViaTranslatorAsync(entries, ct);

            ChineseLookupResult result = entries.Count > 0
                ? new ChineseLookupResult { Term = term, Entries = entries }
                : new ChineseLookupResult
                {
                    Term = term,
                    Entries = [],
                    StatusMessage = "No dictionary entry — see the Bing Translator section above.",
                };

            _cache[cacheKey] = result;   // cache "not found" too: it won't change
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // superseded by a newer selection — caller drops it
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ChineseLookupResult
            {
                Term = term,
                Entries = [],
                StatusMessage = "Bing Dict is unreachable (check your internet connection).",
            };
        }
    }

    // ------------------------------------------------ zh-Hans → zh-Hant

    /// <summary>
    /// Non-Windows conversion (see the class comment): translate all entry
    /// terms zh-Hans → zh-Hant in one Bing Translator request, one term per
    /// line. Returns the entries unchanged when the call fails or the
    /// answer's line count does not match — never worse than Simplified.
    /// </summary>
    private static async Task<List<BingDictionaryEntry>> ToTraditionalViaTranslatorAsync(
        List<BingDictionaryEntry> entries, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = await BingSession.PostAsync("ttranslatev3",
                new Dictionary<string, string>
                {
                    ["fromLang"] = "zh-Hans",
                    ["text"] = string.Join('\n', entries.Select(e => e.Term)),
                    ["to"] = "zh-Hant",
                }, ct);

            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 ||
                !root[0].TryGetProperty("translations", out JsonElement translations) ||
                translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() == 0 ||
                !translations[0].TryGetProperty("text", out JsonElement text) ||
                text.ValueKind != JsonValueKind.String)
                return entries;

            string[] lines = (text.GetString() ?? "").Split('\n')
                .Select(l => l.Trim()).ToArray();
            if (lines.Length != entries.Count || lines.Any(l => l.Length == 0))
                return entries;

            // Distinct Simplified words can meet in one Traditional word
            // (网络/网路 → 網路); keep the first, highest-ranked of each.
            return entries.Select((e, i) => new BingDictionaryEntry
            {
                PartOfSpeech = e.PartOfSpeech,
                Term = lines[i],
                BackTranslations = e.BackTranslations,
            }).DistinctBy(e => (e.PartOfSpeech, e.Term)).ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return entries;
        }
    }

    /// <summary>Windows NLS mapping flag: convert Chinese characters to
    /// their Traditional forms (per-character).</summary>
    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

    // Classic DllImport (not LibraryImport): the source-generated marshaller
    // would force AllowUnsafeBlocks on for the whole project — not worth it
    // for one cold-path call.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(
        string localeName, uint mapFlags, string src, int srcLen,
        [Out] char[] dest, int destLen, IntPtr versionInfo, IntPtr reserved, IntPtr sortHandle);

    /// <summary>
    /// Convert Simplified Chinese text to Traditional using the OS's own
    /// converter — e.g. "记忆" → "記憶". Falls back to the input unchanged
    /// if the call fails (never worse than showing Simplified).
    /// </summary>
    private static string ToTraditionalChinese(string simplified)
    {
        if (simplified.Length == 0) return simplified;

        // LCMapStringEx is a WINDOWS API; other platforms convert through
        // ToTraditionalViaTranslatorAsync instead and never get here.
        if (!OperatingSystem.IsWindows()) return simplified;
        var buffer = new char[simplified.Length * 2];   // headroom; 1:1 in practice
        int written = LCMapStringEx(
            "zh-CN", LCMAP_TRADITIONAL_CHINESE, simplified, simplified.Length,
            buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return written > 0 ? new string(buffer, 0, written) : simplified;
    }
}
