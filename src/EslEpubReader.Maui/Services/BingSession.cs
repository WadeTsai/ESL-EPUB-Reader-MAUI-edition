// ============================================================================
// Services/BingSession.cs
// ============================================================================
// Shared session/transport layer for the two Bing-backed services
// (BingTranslateService and BingDictionaryService).
//
// HOW BING'S WEB TRANSLATOR API WORKS
// -----------------------------------
// The endpoints behind https://www.bing.com/translator
//
//     POST /ttranslatev3   — translation
//     POST /tlookupv3      — bilingual dictionary ("Bing Dict")
//
// are not open: every call must carry a short-lived session obtained from
// the translator PAGE itself. Loading the page yields four values:
//
//   IG    — a per-visit id, embedded as        IG:"…"
//   IID   — an instrumentation id, embedded as data-iid="translator.NNNN"
//   key   — the token's issue time (epoch ms), and
//   token — an anti-abuse token; both embedded as
//           params_AbusePreventionHelper = [key, "token", lifetimeMs]
//
// IG/IID go into the query string, key/token into the form body, and the
// page's cookies must accompany every call (hence one shared HttpClient
// with a cookie container). Tokens live ~1 hour (lifetimeMs); an expired
// token makes the endpoint answer {"statusCode":205}, which PostAsync
// handles by refreshing the session once and retrying.
//
// This class serializes session refreshes (SemaphoreSlim) so concurrent
// lookups from the UI never fetch the page twice in parallel.
//
// STATUS NOTE: like the previous Google backend, these are the endpoints of
// Bing's own web app — free and key-less, but unofficial. The official,
// SLA-backed alternative is the Azure Translator API (needs a key).
// ============================================================================

using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EslEpubReader.Services;

/// <summary>The four per-session values scraped from the translator page.</summary>
internal sealed record BingSessionInfo(string Ig, string Iid, string Key, string Token);

internal static partial class BingSession
{
    /// <summary>One HttpClient for all Bing traffic. UseCookies + a shared
    /// CookieContainer keeps the cookies from the page load attached to the
    /// API calls — without them the endpoints reject requests.</summary>
    internal static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = true })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        // A browser-like User-Agent: the page (and its embedded markers)
        // is only served to things that look like browsers.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        return client;
    }

    // --- compiled regexes for the three markers (see class comment) --------
    [GeneratedRegex("IG:\"([A-Za-z0-9]+)\"")]
    private static partial Regex IgRegex();

    [GeneratedRegex("data-iid=\"([^\"]+)\"")]
    private static partial Regex IidRegex();

    [GeneratedRegex("params_AbusePreventionHelper\\s*=\\s*\\[([0-9]+),\\s*\"([^\"]+)\",\\s*([0-9]+)\\]")]
    private static partial Regex AbuseHelperRegex();

    private static BingSessionInfo? _current;
    private static DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Return a valid session, loading/reloading the translator page only
    /// when the cached one is missing, expired, or a forced refresh is
    /// requested (after a statusCode-205 "token expired" response).
    /// </summary>
    private static async Task<BingSessionInfo> GetAsync(bool forceRefresh, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _current is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _current;

            string html = await Http.GetStringAsync("https://www.bing.com/translator", ct);

            Match ig = IgRegex().Match(html);
            Match iid = IidRegex().Match(html);
            Match abuse = AbuseHelperRegex().Match(html);
            if (!ig.Success || !iid.Success || !abuse.Success)
                throw new HttpRequestException(
                    "Bing translator session markers not found — the page layout may have changed.");

            _current = new BingSessionInfo(
                Ig: ig.Groups[1].Value,
                Iid: iid.Groups[1].Value,
                Key: abuse.Groups[1].Value,
                Token: abuse.Groups[2].Value);

            // The key doubles as the token's ISSUE TIMESTAMP (epoch ms);
            // group 3 is its lifetime. Renew two minutes early so a token
            // never expires mid-request.
            long issuedMs = long.Parse(abuse.Groups[1].Value);
            long lifetimeMs = long.Parse(abuse.Groups[3].Value);
            _expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(issuedMs + lifetimeMs)
                         - TimeSpan.FromMinutes(2);

            return _current;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// POST a form to one of the Bing endpoints ("ttranslatev3" or
    /// "tlookupv3") with the session attached, parsing the JSON answer.
    /// Success responses are ARRAYS; error responses are OBJECTS carrying a
    /// "statusCode". A 205 (token expired) triggers ONE automatic session
    /// refresh + retry; every other outcome is returned to the caller to
    /// interpret. The caller owns (disposes) the returned document.
    /// </summary>
    internal static async Task<JsonDocument> PostAsync(
        string endpoint, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            BingSessionInfo session = await GetAsync(forceRefresh: attempt > 0, ct);

            // Session credentials ride along with the caller's fields.
            var fields = new Dictionary<string, string>(form)
            {
                ["token"] = session.Token,
                ["key"] = session.Key,
            };

            using var content = new FormUrlEncodedContent(fields);
            using HttpResponseMessage response = await Http.PostAsync(
                $"https://www.bing.com/{endpoint}?isVertical=1&&IG={session.Ig}&IID={session.Iid}",
                content, ct);

            string body = await response.Content.ReadAsStringAsync(ct);
            JsonDocument doc = JsonDocument.Parse(body);

            // {"statusCode":205} = token expired → refresh the session and
            // retry exactly once; a second failure falls through to caller.
            if (attempt == 0 &&
                doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("statusCode", out JsonElement sc) &&
                sc.ValueKind == JsonValueKind.Number && sc.GetInt32() == 205)
            {
                doc.Dispose();
                continue;
            }

            return doc;
        }
    }
}
