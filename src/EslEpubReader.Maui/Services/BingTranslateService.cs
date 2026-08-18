// ============================================================================
// Services/BingTranslateService.cs
// ============================================================================
// The whole-selection translation source ("Bing Translator" in the panel):
// machine translation of the selected text — words up to full sentences —
// into the user-selected target language via Bing's ttranslatev3 endpoint.
// (Replaced the earlier Google-based implementation.)
//
// Session/token handling and the endpoint mechanics live in BingSession;
// this class only builds requests, parses answers, and caches.
//
// RESPONSE FORMAT (success is a JSON ARRAY):
//
//   [ { "detectedLanguage": { "language": "en", ... },
//       "translations": [ { "text": "告訴我吧…", "to": "zh-Hant", ... } ] } ]
//
// Errors come back as an OBJECT: {"statusCode": 4xx/5xx, ...}.
// ============================================================================

using System.Net.Http;
using System.Text.Json;
using EslEpubReader.Models;

namespace EslEpubReader.Services;

public sealed class BingTranslateService
{
    /// <summary>
    /// Target language code, settable at runtime from the language picker in
    /// the dictionary panel (any code in LanguageCatalog — Microsoft
    /// Translator codes, e.g. "zh-Hant", "ja", "es"). Default: Traditional
    /// Chinese.
    /// </summary>
    public string TargetLanguage { get; set; } = LanguageCatalog.DefaultCode;

    /// <summary>Session cache keyed by "languageCode|text" — the language is
    /// part of the key so switching languages and back reuses earlier
    /// answers instead of returning the wrong language's translation.</summary>
    private readonly Dictionary<string, TranslationResult> _cache =
        new(StringComparer.Ordinal);   // translations ARE case-sensitive ("May" vs "may")

    /// <summary>
    /// Translate an English word, phrase, or whole sentence into the current
    /// TargetLanguage. NEVER throws for network/parse problems — failures
    /// come back in StatusMessage so the panel can always render something.
    /// </summary>
    /// <param name="text">The reader's (already normalized) selection.</param>
    /// <param name="ct">Cancelled when a newer selection supersedes this one.</param>
    public async Task<TranslationResult> TranslateAsync(string text, CancellationToken ct)
    {
        string cacheKey = $"{TargetLanguage}|{text}";
        if (_cache.TryGetValue(cacheKey, out TranslationResult? cached)) return cached;

        try
        {
            using JsonDocument doc = await BingSession.PostAsync("ttranslatev3",
                new Dictionary<string, string>
                {
                    ["fromLang"] = "en",
                    ["text"] = text,
                    ["to"] = TargetLanguage,
                }, ct);

            JsonElement root = doc.RootElement;

            // Error object (statusCode) → readable message, NOT cached so a
            // transient error retries on the next selection.
            if (root.ValueKind == JsonValueKind.Object)
            {
                int code = root.TryGetProperty("statusCode", out JsonElement sc) &&
                           sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : 0;
                return new TranslationResult
                {
                    Term = text,
                    StatusMessage = $"Bing Translator returned an error (code {code}).",
                };
            }

            // Success array → translations[0].text of the first element.
            string translated = "";
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 &&
                root[0].TryGetProperty("translations", out JsonElement translations) &&
                translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0 &&
                translations[0].TryGetProperty("text", out JsonElement textEl) &&
                textEl.ValueKind == JsonValueKind.String)
            {
                translated = (textEl.GetString() ?? "").Trim();
            }

            TranslationResult result = translated.Length > 0
                ? new TranslationResult { Term = text, TranslatedText = translated }
                : new TranslationResult
                {
                    Term = text,
                    StatusMessage = "Bing Translator returned no translation.",
                };

            _cache[cacheKey] = result;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // superseded by a newer selection — caller drops it
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new TranslationResult
            {
                Term = text,
                StatusMessage = "Bing Translator is unreachable (offline, or the service is rate-limiting).",
            };
        }
    }
}
