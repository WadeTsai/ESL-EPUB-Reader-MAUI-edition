// ============================================================================
// BingPageTransport.cs — runs Bing lookups from INSIDE a hidden translator
// page (the Linux implementation of BingSession.PageTransport).
//
// WHY: Bing now answers plain HttpClient calls to /ttranslatev3 and
// /tlookupv3 with 401 {"ShowCaptcha":false} — its page script sets a
// bot-check cookie that .NET cannot produce. The very same POST succeeds
// when the page itself sends it. So this class keeps one off-screen
// WebKitWebView on https://www.bing.com/translator and, per lookup, asks the
// shared injected helper (BingSession.PageHelperScript, window.__eslBing)
// to XHR the endpoint with the page's own session (IG, IID, anti-abuse
// token, cookies). The response text comes back through the "eslBing"
// script-message handler. (The MAUI builds do the same with a hidden MAUI
// WebView — BingWebViewTransport — polling instead of messaging.)
//
// SESSION LIFETIME: the page's token lives ~1 hour. The page is reloaded
// when it is older than PageMaxAge, or when Bing answers statusCode 205
// ("token expired") — then the request is retried once.
//
// THREADING: WebKit may only be driven from the GTK thread; calls arriving
// from elsewhere are marshalled onto it.
// ============================================================================

using System.Text.Json;
using EslEpubReader.Services;

namespace EslEpubReader;

public sealed class BingPageTransport
{
    private const string MessageHandler = "eslBing";
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    private readonly SynchronizationContext _ui;
    private readonly WebKit.WebView _view;
    private readonly Dictionary<int, TaskCompletionSource<string>> _pending = [];
    private int _nextId;

    private TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTime _loadedAt = DateTime.MinValue;

    /// <summary>Create on the GTK thread; starts loading the translator page
    /// right away so the first lookup does not pay for it.</summary>
    public BingPageTransport()
    {
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("create BingPageTransport on the GTK thread");

        _view = WebKit.WebView.New();
        _view.IsMuted = true;
        WebKit.UserContentManager content = _view.GetUserContentManager();
        content.AddScript(WebKit.UserScript.New(BingSession.PageHelperScript,
            WebKit.UserContentInjectedFrames.TopFrame, WebKit.UserScriptInjectionTime.End, null, null));
        content.RegisterScriptMessageHandler(MessageHandler, null);
        content.OnScriptMessageReceived += OnMessage;

        _view.OnLoadChanged += (_, e) =>
        {
            if (e.LoadEvent != WebKit.LoadEvent.Finished) return;
            _loadedAt = DateTime.UtcNow;
            _loaded.TrySetResult();
        };
        _view.OnLoadFailed += (_, e) =>
        {
            _loaded.TrySetException(new HttpRequestException($"Bing translator page failed to load: {e.Error.Message}"));
            return false;
        };

        Reload();
    }

    /// <summary>Install as the shared BingSession transport.</summary>
    public void Install() => BingSession.PageTransport = PostAsync;

    /// <summary>BingSession.PageTransport: POST <paramref name="form"/> to
    /// /<paramref name="endpoint"/> from inside the page; returns the JSON.</summary>
    public Task<string> PostAsync(string endpoint, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        if (SynchronizationContext.Current == _ui) return PostOnUiAsync(endpoint, form, ct);

        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async _ =>
        {
            try { result.TrySetResult(await PostOnUiAsync(endpoint, form, ct)); }
            catch (Exception ex) { result.TrySetException(ex); }
        }, null);
        return result.Task;
    }

    private async Task<string> PostOnUiAsync(string endpoint, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        if (_loaded.Task.IsFaulted || DateTime.UtcNow - _loadedAt > BingSession.PageMaxAge && _loaded.Task.IsCompleted)
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
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await _view.EvaluateJavascriptAsync(BingSession.PageRequestScript(id, endpoint, form));
            return await reply.Task.WaitAsync(RequestTimeout, ct);
        }
        catch (TimeoutException) { throw new HttpRequestException("Bing did not answer in time."); }
        catch (GLib.GException ex) { throw new HttpRequestException($"Bing page script failed: {ex.Message}"); }
        finally { _pending.Remove(id); }
    }

    private void OnMessage(WebKit.UserContentManager sender,
                           WebKit.UserContentManager.ScriptMessageReceivedSignalArgs e)
    {
        try
        {
            (int id, bool ok, string body) = BingSession.ParsePageReply(e.Value.ToString() ?? "");
            if (!_pending.TryGetValue(id, out TaskCompletionSource<string>? reply)) return;

            if (ok) reply.TrySetResult(body);
            else reply.TrySetException(new HttpRequestException($"Bing request failed: {body}"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            /* not one of ours — ignore */
        }
    }

    private void Reload()
    {
        if (_loaded.Task.IsCompleted)
            _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _view.LoadUri(BingSession.TranslatorPageUrl);
    }
}
