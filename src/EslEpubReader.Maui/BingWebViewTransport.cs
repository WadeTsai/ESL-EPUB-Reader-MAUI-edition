// ============================================================================
// BingWebViewTransport.cs — runs Bing lookups from INSIDE a hidden
// translator page (the MAUI implementation of BingSession.PageTransport;
// WebView2 on Windows, WKWebView on Mac).
//
// WHY: Bing now answers plain HttpClient calls to /ttranslatev3 and
// /tlookupv3 with 401 {"ShowCaptcha":false} — its page script sets a
// bot-check cookie that .NET cannot produce — while the very same POST
// succeeds when the page itself sends it (see BingSession). So MainPage
// hosts a 1×1, fully transparent WebView on https://www.bing.com/translator
// and this class asks the shared injected helper (window.__eslBing) to XHR
// each request with the page's own session.
//
// BRIDGE: MAUI's cross-platform WebView has no message channel (the same
// constraint MainPage's selection bridge works around), so the helper
// queues each answer and this class POLLS window.__eslBingTake(id) every
// 100 ms. Answers travel base64-wrapped, because EvaluateJavaScriptAsync's
// string escaping differs per platform.
//
// SESSION LIFETIME: reloaded when older than BingSession.PageMaxAge or when
// Bing answers statusCode 205 ("token expired"), then retried once.
//
// The same design runs on Linux via WebKitGTK (EslEpubReader.Linux's
// BingPageTransport), which is where it was verified end to end.
// ============================================================================

using System.Text;
using EslEpubReader.Services;

namespace EslEpubReader;

public sealed class BingWebViewTransport
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly WebView _view;
    private TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _loadedAt = DateTime.MinValue;
    private int _nextId;

    /// <summary>Takes over <paramref name="view"/> (which must be in the
    /// visual tree — WebView2 only initializes once it is) and starts
    /// loading the translator page right away, so the first lookup does
    /// not pay for it.</summary>
    public BingWebViewTransport(WebView view)
    {
        _view = view;
        _view.Navigated += OnNavigated;
        Reload();
    }

    /// <summary>Install as the shared BingSession transport.</summary>
    public void Install() => BingSession.PageTransport = PostAsync;

    /// <summary>BingSession.PageTransport: POST <paramref name="form"/> to
    /// /<paramref name="endpoint"/> from inside the page; returns the JSON.
    /// WebViews may only be driven from the UI thread, hence the hop.</summary>
    public Task<string> PostAsync(string endpoint, IReadOnlyDictionary<string, string> form, CancellationToken ct) =>
        MainThread.IsMainThread
            ? PostOnUiAsync(endpoint, form, ct)
            : MainThread.InvokeOnMainThreadAsync(() => PostOnUiAsync(endpoint, form, ct));

    private async Task<string> PostOnUiAsync(string endpoint, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        if (_loaded.Task.IsFaulted ||
            _loaded.Task.IsCompleted && DateTime.UtcNow - _loadedAt > BingSession.PageMaxAge)
            Reload();

        string body = await SendAsync(endpoint, form, ct);
        if (BingSession.IsTokenExpired(body))
        {
            Reload();
            body = await SendAsync(endpoint, form, ct);
        }
        return body;
    }

    private async Task<string> SendAsync(string endpoint, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        try { await _loaded.Task.WaitAsync(LoadTimeout, ct); }
        catch (TimeoutException) { throw new HttpRequestException("Bing translator page did not load in time."); }

        int id = ++_nextId;
        await RunJsAsync(BingSession.PageRequestScript(id, endpoint, form));

        DateTime deadline = DateTime.UtcNow + RequestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);
            string wrapped = Unquote(await RunJsAsync(BingSession.PageTakeScript(id)));
            if (wrapped.Length == 0) continue;   // still in flight

            string json;
            try { json = Encoding.UTF8.GetString(Convert.FromBase64String(wrapped)); }
            catch (FormatException) { throw new HttpRequestException("Bing page returned an unreadable answer."); }

            (_, bool ok, string body) = BingSession.ParsePageReply(json);
            if (!ok) throw new HttpRequestException($"Bing request failed: {body}");
            return body;
        }
        throw new HttpRequestException("Bing did not answer in time.");
    }

    /// <summary>Page finished loading: inject the helper, then wait until
    /// the page's own session globals exist before accepting requests.</summary>
    private async void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (!e.Url.Contains("bing.com/translator", StringComparison.OrdinalIgnoreCase)) return;
        if (e.Result != WebNavigationResult.Success)
        {
            _loaded.TrySetException(new HttpRequestException($"Bing translator page failed to load ({e.Result})."));
            return;
        }

        await RunJsAsync(BingSession.PageHelperScript);
        for (int i = 0; i < 50; i++)   // up to ~5 s for the page script
        {
            if (Unquote(await RunJsAsync("window.__eslBingReady && window.__eslBingReady() ? 'yes' : ''")) == "yes")
            {
                _loadedAt = DateTime.UtcNow;
                _loaded.TrySetResult();
                return;
            }
            await Task.Delay(PollInterval);
        }
        _loaded.TrySetException(new HttpRequestException("Bing translator page has no session (layout changed?)."));
    }

    private void Reload()
    {
        if (_loaded.Task.IsCompleted)
            _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _view.Source = new UrlWebViewSource { Url = BingSession.TranslatorPageUrl };
    }

    /// <summary>Run JS in the hidden page, flattened to one line like
    /// MainPage.RunJsAsync (EvaluateJavaScriptAsync's newline handling
    /// differs per platform). Null when the page is mid-navigation.</summary>
    private async Task<string?> RunJsAsync(string script)
    {
        try { return await _view.EvaluateJavaScriptAsync(script.Replace("\r", " ").Replace("\n", " ")); }
        catch { return null; }
    }

    /// <summary>EvaluateJavaScriptAsync returns strings JSON-quoted on some
    /// platforms; base64 and 'yes' only need the quotes (and any escaped
    /// slashes) removed.</summary>
    private static string Unquote(string? result) =>
        result is null or "null" ? "" : result.Trim().Trim('"').Replace("\\/", "/");
}
