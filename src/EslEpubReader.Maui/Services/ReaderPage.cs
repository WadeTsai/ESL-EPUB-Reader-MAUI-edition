// ============================================================================
// Services/ReaderPage.cs
// ============================================================================
// Everything the reader does INSIDE the chapter web page, as plain .NET so
// every front end shares it byte-for-byte:
//
//   * the MAUI app (MainPage — WebView2 on Windows, WKWebView on Mac), and
//   * the Linux app (src/EslEpubReader.Linux — GTK 4 + WebKitGTK).
//
// Contents: the toolbar option lists (Value strings are persisted in
// settings.json, so they must stay identical across front ends), the
// injected selection/pagination script, the reader stylesheet builder, the
// base64 style-injection wrapper, and selection normalization.
//
// The injected JS is kept //-comment-free and ';'-terminated because the
// MAUI front end flattens it to one line before EvaluateJavaScriptAsync.
// ============================================================================

using System.Globalization;
using System.Text;

namespace EslEpubReader.Services;

/// <summary>Simple display/value pair for the toolbar pickers. The Value
/// strings are IDENTICAL to the WinUI ComboBox Tags, so an existing
/// settings.json restores cleanly on every front end.</summary>
public sealed record ReaderOption(string Display, string Value)
{
    public override string ToString() => Display;
}

public static class ReaderPage
{
    public const int MaxDictionaryTermLength = 60;
    public const int MaxDictionaryTermWords = 5;
    public const int MaxTranslationLength = 500;

    /// <summary>Name of the WebKit script-message handler a front end MAY
    /// register (the Linux app does). When it is absent — MAUI on Windows
    /// and Mac — the script falls back to the window.__eslPendingSel queue
    /// that native code polls.</summary>
    public const string SelectionMessageHandler = "eslSelection";

    public static readonly IReadOnlyList<ReaderOption> FontOptions =
    [
        new("Book default font", ""),
        new("Segoe UI / SF Pro", "'Segoe UI', -apple-system, sans-serif"),
        new("Verdana", "Verdana, sans-serif"),
        new("Arial", "Arial, sans-serif"),
        new("Georgia", "Georgia, serif"),
        new("Times New Roman", "'Times New Roman', serif"),
        new("Courier / mono", "Consolas, Menlo, monospace"),
    ];

    public static readonly IReadOnlyList<ReaderOption> SpacingOptions =
    [
        new("Default spacing", ""),
        new("Spacing 1.4", "1.4"),
        new("Spacing 1.6", "1.6"),
        new("Spacing 1.8", "1.8"),
        new("Spacing 2.0", "2.0"),
        new("Spacing 2.5", "2.5"),
    ];

    /// <summary>"margin|columnCap" pairs — see BuildStyleSheet; identical
    /// semantics to the WinUI app.</summary>
    public static readonly IReadOnlyList<ReaderOption> MarginOptions =
    [
        new("Default margins", ""),
        new("Extra-narrow margins", "0.25|none"),
        new("Narrow margins", "1|60"),
        new("Medium margins", "3|38"),
        new("Wide margins", "5|36"),
        new("Extra-wide margins", "8|32"),
    ];

    /// <summary>Index of <paramref name="value"/> in <paramref name="options"/>,
    /// or 0 (the "default" entry) when it is not there.</summary>
    public static int IndexOfValue(IReadOnlyList<ReaderOption> options, string value)
    {
        for (int i = 0; i < options.Count; i++)
            if (options[i].Value == value) return i;
        return 0;
    }

    /// <summary>
    /// The injected page script — the same behavior as the WinUI app's
    /// SelectionWatcherScript, expressed bridge-portably:
    ///   * selection reporting (mouseup/dblclick/Shift+arrows): posted to the
    ///     eslSelection message handler when the host registered one,
    ///     otherwise queued in window.__eslPendingSel for native polling;
    ///   * strict dual-page pagination: flipPage snaps to whole page pairs;
    ///     wheel = exactly one pair per gesture; PgDn/PgUp/Space/arrows/
    ///     Home/End; resize re-aligns to the remembered fraction and forces
    ///     a repaint (stale column-rule fix);
    ///   * __eslFraction/__eslRestore: position helpers polled/called from
    ///     native code.
    /// The __eslInit guard makes re-injection (every Navigated) a no-op.
    /// </summary>
    public const string SelectionWatcherScript =
        """
        (function () {
            if (window.__eslInit) return; window.__eslInit = true;
            var last = '';
            function report() {
                var s = window.getSelection ? String(window.getSelection()) : '';
                s = s.trim();
                if (!s) { last = ''; return; }
                if (s === last || s.length > 500) return;
                last = s;
                window.__eslPendingSel = s;
                try { window.webkit.messageHandlers.eslSelection.postMessage(s); window.__eslPendingSel = ''; } catch (e) { }
            }
            document.addEventListener('mouseup', function () { setTimeout(report, 0); });
            document.addEventListener('dblclick', function () { setTimeout(report, 0); });
            document.addEventListener('keyup', function (e) {
                if (e.key === 'Shift' || (e.key && e.key.indexOf('Arrow') === 0)) setTimeout(report, 0);
            });
            var lastDualFraction = 0;
            function step() {
                var cs = getComputedStyle(document.body);
                return document.body.clientWidth - (parseFloat(cs.paddingLeft) || 0)
                     - (parseFloat(cs.paddingRight) || 0) + (parseFloat(cs.columnGap) || 0);
            }
            function flip(d) {
                var b = document.body, st = step(), m = b.scrollWidth - b.clientWidth;
                var t = Math.round(b.scrollLeft / st) * st + d * st;
                b.scrollLeft = Math.max(0, Math.min(t, m));
                lastDualFraction = m > 0 ? b.scrollLeft / m : 0;
            }
            window.__eslFraction = function () {
                var b = document.body;
                if (b && b.scrollWidth > b.clientWidth + 1) {
                    var m = b.scrollWidth - b.clientWidth;
                    var f = m > 0 ? b.scrollLeft / m : 0;
                    lastDualFraction = f; return f;
                }
                var m2 = document.documentElement.scrollHeight - window.innerHeight;
                return m2 > 0 ? Math.min(1, Math.max(0, window.scrollY / m2)) : 0;
            };
            window.__eslRestore = function (f) {
                var b = document.body;
                if (b && b.scrollWidth > b.clientWidth + 1) {
                    var st = step(), m = b.scrollWidth - b.clientWidth;
                    var t = Math.round((f * m) / st) * st;
                    b.scrollLeft = Math.max(0, Math.min(t, m));
                    lastDualFraction = f; return;
                }
                var m2 = document.documentElement.scrollHeight - window.innerHeight;
                window.scrollTo(0, m2 > 0 ? m2 * f : 0);
            };
            var wa = 0, lw = 0;
            window.addEventListener('wheel', function (e) {
                var b = document.body;
                if (!b || b.scrollWidth <= b.clientWidth + 1 || e.ctrlKey) return;
                e.preventDefault();
                var n = Date.now();
                if (n - lw < 250) return;
                wa += e.deltaY;
                if (Math.abs(wa) < 40) return;
                flip(wa > 0 ? 1 : -1); wa = 0; lw = n;
            }, { passive: false });
            window.addEventListener('keydown', function (e) {
                var b = document.body;
                if (!b || b.scrollWidth <= b.clientWidth + 1) return;
                if (e.ctrlKey || e.altKey || e.metaKey) return;
                if (e.key === 'Home') { b.scrollLeft = 0; e.preventDefault(); return; }
                if (e.key === 'End') { b.scrollLeft = b.scrollWidth; e.preventDefault(); return; }
                var d = 0;
                if (e.key === 'PageDown' || e.key === 'ArrowRight' || (e.key === ' ' && !e.shiftKey)) d = 1;
                else if (e.key === 'PageUp' || e.key === 'ArrowLeft' || (e.key === ' ' && e.shiftKey)) d = -1;
                if (d === 0 || (e.shiftKey && e.key !== ' ')) return;
                flip(d); e.preventDefault();
            }, true);
            var rt = null;
            window.addEventListener('resize', function () {
                var b = document.body;
                if (!b || b.scrollWidth <= b.clientWidth + 1) return;
                if (rt) clearTimeout(rt);
                rt = setTimeout(function () {
                    var st = step(), m = b.scrollWidth - b.clientWidth;
                    var t = Math.round((lastDualFraction * m) / st) * st;
                    b.scrollLeft = Math.max(0, Math.min(t, m));
                    b.style.visibility = 'hidden'; void b.offsetHeight; b.style.visibility = '';
                }, 150);
            });
        })();
        """;

    /// <summary>
    /// Build the reader stylesheet — a direct port of the WinUI
    /// ApplyReaderStyleAsync: zoom, font, line spacing, page margins with
    /// per-level column caps (responsive via vw ÷ zoom), the book-page
    /// frame, strict dual-page columns, and night-reading mode.
    /// </summary>
    public static string BuildStyleSheet(double zoom, string fontFamily, string lineHeight,
                                         string pageMargin, bool dualPage, bool dark)
    {
        var css = new StringBuilder();
        css.Append($"body {{ zoom: {zoom.ToString(CultureInfo.InvariantCulture)}; }}\n");
        if (fontFamily.Length > 0)
            css.Append($"body, body * {{ font-family: {fontFamily} !important; }}\n");
        if (lineHeight.Length > 0)
            css.Append($"body, body * {{ line-height: {lineHeight} !important; }}\n");

        // Margin level "margin|columnCap" (see MarginOptions).
        string[] marginParts = pageMargin.Split('|');
        string sideMargin = marginParts[0].Length > 0 ? marginParts[0] : "2.5";
        string columnCap = marginParts.Length > 1 ? marginParts[1] : "40";
        string usableWidth = $"calc({(100.0 / zoom).ToString("0.####", CultureInfo.InvariantCulture)}vw)";
        string maxWidth = columnCap == "none" ? usableWidth : $"min({columnCap}em, {usableWidth})";

        if (!dualPage)
        {
            css.Append($"body {{ max-width: {maxWidth} !important; margin: 0 auto !important; " +
                       $"padding: 3em {sideMargin}em 6em {sideMargin}em !important; box-sizing: border-box !important; }}\n");
            css.Append("h1,h2,h3,h4,h5,h6,hgroup,header { margin-top: 2em !important; margin-bottom: 1.5em !important; }\n");
            css.Append("hgroup > * { margin-top: 0 !important; margin-bottom: 0 !important; }\n");
            css.Append("img, svg { max-width: 100%; height: auto; }\n");
        }
        else
        {
            string pageHeightVh = (100.0 / zoom).ToString("0.##", CultureInfo.InvariantCulture);
            string spineColor = dark ? "#4a4a4a" : "#d9d9d9";
            css.Append("html { height: 100%; overflow: hidden !important; }\n");
            css.Append($"body {{ height: {pageHeightVh}vh !important; margin: 0 !important; " +
                       $"box-sizing: border-box !important; padding: 2.5em {sideMargin}em !important; " +
                       $"column-count: 2; column-gap: 5em; column-fill: auto; " +
                       $"column-rule: 1px solid {spineColor}; overflow-y: hidden !important; overflow-x: auto !important; }}\n");
            css.Append("body::-webkit-scrollbar { display: none; }\n");
            css.Append("img, svg { max-width: 100%; height: auto; }\n");
            // FRAGMENTATION FIX: some publishers style paragraphs as
            // inline-blocks (e.g. Standard Ebooks' "bridgehead" summaries).
            // Inline-blocks are ATOMIC — they cannot split across column
            // pages — so one taller than a page overflows past the fold and
            // the spread shows a cut-off page. Forcing block display makes
            // every paragraph fragmentable; in a paginated view that is
            // exactly what a paragraph should be.
            css.Append("body p, body blockquote { display: block !important; max-width: 100% !important; }\n");
        }

        if (dark)
        {
            css.Append("html, body { background-color: #1e1e1e !important; }\n");
            css.Append("body, body * { color: #e8e6e3 !important; background-color: transparent !important; border-color: #555 !important; }\n");
            css.Append("a, a * { color: #6cb2f7 !important; }\n");
            css.Append("img, svg { opacity: 0.85; }\n");
        }
        return css.ToString();
    }

    /// <summary>
    /// Wrap a stylesheet in a script that installs (or replaces) it in the
    /// page. TRANSPORT ROBUSTNESS — the CSS travels as BASE64, decoded in
    /// the page (the classic UTF-8-safe atob incantation). MAUI's
    /// EvaluateJavaScriptAsync re-escapes/evals scripts on some platforms,
    /// which corrupted embedded string escapes (a JSON "\n" became a raw
    /// newline inside a JS literal = silent SyntaxError and no styling).
    /// Base64's alphabet is inert through every escaping layer.
    /// createElementNS + the documentElement fallback keep the injection
    /// working across the wildly varying XHTML strictness of real ePubs.
    /// </summary>
    public static string StyleInjectionScript(string css)
    {
        string cssB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(css));
        return
            $"(function(){{ try {{ var s=document.getElementById('esl-style'); " +
            $"if(!s){{ s=document.createElementNS('http://www.w3.org/1999/xhtml','style'); s.id='esl-style'; " +
            $"(document.head || document.documentElement).appendChild(s); }} " +
            $"s.textContent = decodeURIComponent(escape(window.atob('{cssB64}'))); " +
            $"return 'S-OK'; }} catch(e) {{ return 'S-ERR:'+String(e); }} }})();";
    }

    /// <summary>Collapse whitespace, trim surrounding punctuation, and reject
    /// selections that are empty, too long, or contain no Latin letters.
    /// Returns "" when the selection should not be looked up.</summary>
    public static string NormalizeSelection(string raw)
    {
        string[] parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string text = string.Join(' ', parts);

        int start = 0, end = text.Length - 1;
        while (start <= end && !char.IsLetterOrDigit(text[start])) start++;
        while (end >= start && !char.IsLetterOrDigit(text[end])) end--;
        if (start > end) return "";
        text = text[start..(end + 1)];

        if (text.Length is 0 or > MaxTranslationLength) return "";
        if (!text.Any(char.IsAsciiLetter)) return "";
        return text;
    }

    /// <summary>True when a selection is short enough for the dictionaries;
    /// longer selections (sentences) only go to the translator.</summary>
    public static bool IsDictionarySized(string term) =>
        term.Length <= MaxDictionaryTermLength &&
        term.Split(' ').Length <= MaxDictionaryTermWords;
}
