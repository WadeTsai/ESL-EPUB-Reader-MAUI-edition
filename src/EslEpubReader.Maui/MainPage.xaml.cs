// ============================================================================
// MainPage.xaml.cs — the reader logic, ported from the WinUI 3 app's
// MainWindow to .NET MAUI so ONE code base serves Windows and macOS.
//
// WHAT CHANGED IN THE PORT (the WinUI original is referenced throughout):
//
//   * WebView bridge — WinUI's WebView2 offered postMessage/WebMessageReceived.
//     MAUI's cross-platform WebView has no message channel, so the injected
//     script reports selections by navigating to a custom scheme
//     ("eslreader://selection/…") which native code intercepts in the
//     Navigating event and CANCELS — a classic, portable bridge pattern
//     (works on WebView2 and WKWebView alike).
//
//   * Script injection — no AddScriptToExecuteOnDocumentCreatedAsync in
//     MAUI; scripts are injected AFTER load from the Navigated event via
//     EvaluateJavaScriptAsync. Injected JS is kept //-comment-free and is
//     flattened to a single line before sending (newline handling in
//     EvaluateJavaScriptAsync differs per platform).
//
//   * Scroll tracking — instead of postMessage-per-scroll, native code
//     POLLS a JS helper (window.__eslFraction) on a 3-second timer; the
//     same helper family restores positions and realigns dual-page mode.
//
//   * Text-to-speech — Windows.Media.SpeechSynthesis is Windows-only;
//     MAUI's TextToSpeech.Default speaks on both platforms.
//
//   * Virtual host — WebView2's SetVirtualHostNameToFolderMapping is
//     Windows-only; chapters load as plain file:// URLs, which both
//     WebView2 and WKWebView serve with relative CSS/images intact.
//
// Everything else — the ePub parser, the three lookup services, the
// settings model, the reader stylesheet, strict dual-page pagination —
// is the same code and the same behavior as the WinUI app.
// ============================================================================

using System.Globalization;
using System.Text;
using System.Text.Json;
using EslEpubReader.Models;
using EslEpubReader.Services;

namespace EslEpubReader;

public partial class MainPage : ContentPage
{
    // ------------------------------------------------------------ constants

    /// <summary>Custom scheme the injected JS "navigates" to in order to
    /// send the native side a message; see ReaderWebView_Navigating.</summary>
    private const string BridgeScheme = "eslreader://";

    private const int MaxDictionaryTermLength = 60;
    private const int MaxDictionaryTermWords = 5;
    private const int MaxTranslationLength = 500;

    // ------------------------------------------------------------- services

    private readonly EpubParserService _epubParser = new();
    private readonly EnglishDictionaryService _englishDict = new();
    private readonly BingDictionaryService _bingDict = new();
    private readonly BingTranslateService _translator = new();
    private readonly SettingsService _settings = new();

    // ---------------------------------------------------------------- state

    private EpubBook? _book;
    private CancellationTokenSource? _lookupCts;
    private CancellationTokenSource? _ttsCts;
    private Locale? _englishVoice;

    private double _zoom = 1.0;
    private string _fontFamily = "";
    private string _lineHeight = "";
    private string _pageMargin = "";
    private bool _dualPage;
    private bool _dark;

    private string _lastLookedUpTerm = "";
    private double? _pendingScrollFraction;
    private double _currentScrollFraction;
    private DateTime _lastScrollSave = DateTime.MinValue;

    /// <summary>Blocks settings writes until the persisted state has been
    /// re-applied to the controls (same guard as the WinUI app — picker
    /// change events fire during restore).</summary>
    private bool _settingsRestored;

    private bool _appeared;

    /// <summary>Simple display/value pair for the toolbar Pickers. The
    /// Value strings are IDENTICAL to the WinUI ComboBox Tags, so an
    /// existing settings.json restores cleanly.</summary>
    private sealed record OptionItem(string Display, string Value)
    {
        public override string ToString() => Display;
    }

    private static readonly List<OptionItem> FontOptions =
    [
        new("Book default font", ""),
        new("Segoe UI / SF Pro", "'Segoe UI', -apple-system, sans-serif"),
        new("Verdana", "Verdana, sans-serif"),
        new("Arial", "Arial, sans-serif"),
        new("Georgia", "Georgia, serif"),
        new("Times New Roman", "'Times New Roman', serif"),
        new("Courier / mono", "Consolas, Menlo, monospace"),
    ];

    private static readonly List<OptionItem> SpacingOptions =
    [
        new("Default spacing", ""),
        new("Spacing 1.4", "1.4"),
        new("Spacing 1.6", "1.6"),
        new("Spacing 1.8", "1.8"),
        new("Spacing 2.0", "2.0"),
        new("Spacing 2.5", "2.5"),
    ];

    /// <summary>"margin|columnCap" pairs — see the reader stylesheet
    /// builder; identical semantics to the WinUI app.</summary>
    private static readonly List<OptionItem> MarginOptions =
    [
        new("Default margins", ""),
        new("Extra-narrow margins", "0.25|none"),
        new("Narrow margins", "1|60"),
        new("Medium margins", "3|38"),
        new("Wide margins", "5|36"),
        new("Extra-wide margins", "8|32"),
    ];

    // ---------------------------------------------------------- construction

    public MainPage()
    {
        InitializeComponent();
        _settings.Load();

        // Populate the pickers, then re-select the persisted values. The
        // SelectedIndexChanged handlers run during these assignments but
        // are inert until _settingsRestored is set.
        FontPicker.ItemsSource = FontOptions;
        SpacingPicker.ItemsSource = SpacingOptions;
        MarginPicker.ItemsSource = MarginOptions;
        LanguagePicker.ItemsSource = LanguageCatalog.All.ToList();

        SelectByValue(FontPicker, FontOptions, _settings.Current.ReaderFontFamily);
        SelectByValue(SpacingPicker, SpacingOptions, _settings.Current.ReaderLineHeight);
        SelectByValue(MarginPicker, MarginOptions, _settings.Current.ReaderPageMargin);
        LanguagePicker.SelectedItem = LanguageCatalog.FromCode(_settings.Current.TargetLanguageCode);

        _zoom = Math.Clamp(_settings.Current.ReaderZoom, 0.5, 3.0);
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";

        // Theme: "" = follow the OS; "Dark"/"Light" = the remembered choice.
        _dark = _settings.Current.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => Application.Current!.RequestedTheme == AppTheme.Dark,
        };
        ApplyTheme();

        _settingsRestored = true;

        // ONE fast poll timer drives both bridges (see class comment on why
        // polling replaces the WinUI message bridge): every 700ms it asks
        // the page for a pending SELECTION (snappy lookups), and every 4th
        // tick (~3s) also for the reading POSITION.
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(700);
        int tick = 0;
        timer.Tick += async (_, _) =>
        {
            await PollSelectionAsync();
            if (++tick % 4 == 0) await PollScrollFractionAsync();
        };
        timer.Start();
    }

    /// <summary>First appearance: pick an English TTS voice and reopen the
    /// last session's book ("continue where you left off").</summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_appeared) return;
        _appeared = true;

        try
        {
            // Prefer an en-US voice; the OS default may be a non-English
            // voice that mangles English text (same logic as the WinUI app).
            var locales = await TextToSpeech.Default.GetLocalesAsync();
            _englishVoice =
                locales.FirstOrDefault(l => l.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                                            && (l.Country ?? "").Contains("US", StringComparison.OrdinalIgnoreCase))
                ?? locales.FirstOrDefault(l => l.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        }
        catch { /* no voices — lookups still work, just silently */ }

        string lastBook = _settings.Current.LastBookPath;
        if (lastBook.Length > 0 && File.Exists(lastBook))
            await OpenBookAsync(lastBook);
        else if (lastBook.Length > 0)
        {
            _settings.Current.LastBookPath = "";   // file moved/deleted — forget it
            _settings.Save();
        }
    }

    private static void SelectByValue(Picker picker, List<OptionItem> options, string value)
    {
        int index = options.FindIndex(o => o.Value == value);
        picker.SelectedIndex = index >= 0 ? index : 0;
    }

    // ============================================================= book open

    private async void OpenBtn_Clicked(object? sender, EventArgs e)
    {
        try
        {
            // Platform-correct file filters: extension on Windows, UTType on
            // Mac Catalyst (ePub has a registered system type there).
            var epubType = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                { DevicePlatform.WinUI, new[] { ".epub" } },
                { DevicePlatform.MacCatalyst, new[] { "org.idpf.epub-container", "epub" } },
            });
            FileResult? file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Open an ePub book",
                FileTypes = epubType,
            });
            if (file is null) return;   // user cancelled

            await OpenBookAsync(file.FullPath);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not open the picker: {ex.Message}";
        }
    }

    private async Task OpenBookAsync(string path)
    {
        StatusLabel.Text = $"Opening {Path.GetFileName(path)}…";
        try
        {
            EpubBook book = await _epubParser.ParseAsync(path);

            if (_book is not null)
                EpubParserService.TryCleanupExtractedFolder(_book.ExtractedFolder);
            _book = book;

            // Resume support — identical rules to the WinUI app: the SAME
            // file reopens at the remembered chapter + position; any other
            // file starts from the beginning.
            bool isRemembered = string.Equals(path, _settings.Current.LastBookPath,
                                              StringComparison.OrdinalIgnoreCase);
            int startChapter = isRemembered
                ? Math.Clamp(_settings.Current.LastChapterIndex, 0, book.Chapters.Count - 1)
                : 0;
            _pendingScrollFraction = isRemembered && _settings.Current.LastScrollFraction > 0
                ? _settings.Current.LastScrollFraction
                : null;

            _settings.Current.LastBookPath = path;
            _settings.Save();

            ChapterList.ItemsSource = book.Chapters;
            ChapterList.SelectedItem = book.Chapters[startChapter];

            BookTitleLabel.Text = $"{book.Title} — {book.Author}";
            PrevBtn.IsEnabled = NextBtn.IsEnabled = true;
            StatusLabel.Text = isRemembered
                ? $"Welcome back to “{book.Title}” — continuing where you left off."
                : $"Opened “{book.Title}” ({book.Chapters.Count} chapters). Select any word to look it up.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not open this ePub: {ex.Message}";
        }
    }

    // ====================================================== chapter navigation

    private void ChapterList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_book is null || e.CurrentSelection.FirstOrDefault() is not EpubChapter chapter) return;

        // Chapters load as plain file:// URLs — both WebView2 and WKWebView
        // resolve the chapter's relative CSS/image links from there.
        string fullPath = Path.Combine(_book.ExtractedFolder,
            chapter.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        ReaderWebView.Source = new UrlWebViewSource { Url = new Uri(fullPath).AbsoluteUri };

        _currentScrollFraction = _pendingScrollFraction ?? 0;
        SaveReadingPosition();
        StatusLabel.Text = $"Chapter {chapter.SpineIndex + 1} of {_book.Chapters.Count}: {chapter.Title}";
    }

    private void PrevBtn_Clicked(object? sender, EventArgs e) => MoveChapter(-1);
    private void NextBtn_Clicked(object? sender, EventArgs e) => MoveChapter(+1);

    private void MoveChapter(int delta)
    {
        if (_book is null || ChapterList.SelectedItem is not EpubChapter current) return;
        int target = current.SpineIndex + delta;
        if (target >= 0 && target < _book.Chapters.Count)
            ChapterList.SelectedItem = _book.Chapters[target];
    }

    // ========================================================= WebView bridge

    /// <summary>
    /// Safety net only: the app never expects custom-scheme navigations, but
    /// if page content ever tries one (or an external link), cancel it —
    /// the reader shows books, it does not browse.
    /// </summary>
    private void ReaderWebView_Navigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url.StartsWith(BridgeScheme, StringComparison.OrdinalIgnoreCase) ||
            e.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
        }
    }

    /// <summary>
    /// The SELECTION bridge: the injected script queues the latest selection
    /// in window.__eslPendingSel; this poll (every 700ms) collects and
    /// clears it, then fires the triple lookup. Polling is used instead of
    /// scheme-navigation tricks because it behaves identically on WebView2
    /// and WKWebView.
    /// </summary>
    private async Task PollSelectionAsync()
    {
        if (_book is null) return;
        try
        {
            string? result = await ReaderWebView.EvaluateJavaScriptAsync(
                "(function(){ var s = window.__eslPendingSel || ''; window.__eslPendingSel = ''; return s; })()");
            if (string.IsNullOrEmpty(result) || result == "null") return;

            // EvaluateJavaScriptAsync returns a JSON-ish encoded string on
            // some platforms — unescape conservatively.
            string raw = result.Trim('"');
            raw = raw.Replace("\\u0027", "'").Replace("\\\"", "\"").Replace("\\\\", "\\");

            string term = NormalizeSelection(raw);
            if (term.Length == 0) return;

            _ = LookupAllSourcesAsync(term);
        }
        catch { /* page mid-navigation — try again next tick */ }
    }

    /// <summary>Chapter finished loading: inject the selection/paging script,
    /// apply the reader stylesheet, and restore any pending position.</summary>
    private async void ReaderWebView_Navigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success) return;

        await RunJsAsync(SelectionWatcherScript);
        await ApplyReaderStyleAsync();

        if (_pendingScrollFraction is double fraction)
        {
            _pendingScrollFraction = null;
            _currentScrollFraction = fraction;
            string f = fraction.ToString(CultureInfo.InvariantCulture);
            await RunJsAsync($"window.__eslRestore && window.__eslRestore({f});");
        }
    }

    /// <summary>
    /// Run JS in the chapter. The script is FLATTENED to one line first —
    /// EvaluateJavaScriptAsync's newline handling differs per platform, so
    /// the injected sources avoid //-comments and rely on ';' terminators.
    /// </summary>
    private async Task RunJsAsync(string script)
    {
        try
        {
            string flat = script.Replace("\r", " ").Replace("\n", " ");
            await ReaderWebView.EvaluateJavaScriptAsync(flat);
        }
        catch { /* page mid-navigation — harmless, the next event re-injects */ }
    }

    /// <summary>
    /// The injected page script — the same behavior as the WinUI app's
    /// SelectionWatcherScript, expressed bridge-portably:
    ///   * selection reporting (mouseup/dblclick/Shift+arrows) via the
    ///     eslreader:// scheme;
    ///   * strict dual-page pagination: flipPage snaps to whole page pairs;
    ///     wheel = exactly one pair per gesture; PgDn/PgUp/Space/arrows/
    ///     Home/End; resize re-aligns to the remembered fraction and forces
    ///     a repaint (stale column-rule fix);
    ///   * __eslFraction/__eslRestore: position helpers polled/called from
    ///     native code.
    /// The __eslInit guard makes re-injection (every Navigated) a no-op.
    /// </summary>
    private const string SelectionWatcherScript =
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

    // ======================================================= reader stylesheet

    /// <summary>
    /// Build and inject the reader stylesheet — a direct port of the WinUI
    /// ApplyReaderStyleAsync: zoom, font, line spacing, page margins with
    /// per-level column caps (responsive via vw ÷ zoom), the book-page
    /// frame, strict dual-page columns, and night-reading mode.
    /// </summary>
    private async Task ApplyReaderStyleAsync()
    {
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";

        var css = new StringBuilder();
        css.Append($"body {{ zoom: {_zoom.ToString(CultureInfo.InvariantCulture)}; }}\n");
        if (_fontFamily.Length > 0)
            css.Append($"body, body * {{ font-family: {_fontFamily} !important; }}\n");
        if (_lineHeight.Length > 0)
            css.Append($"body, body * {{ line-height: {_lineHeight} !important; }}\n");

        // Margin level "margin|columnCap" (see MarginOptions).
        string[] marginParts = _pageMargin.Split('|');
        string sideMargin = marginParts[0].Length > 0 ? marginParts[0] : "2.5";
        string columnCap = marginParts.Length > 1 ? marginParts[1] : "40";
        string usableWidth = $"calc({(100.0 / _zoom).ToString("0.####", CultureInfo.InvariantCulture)}vw)";
        string maxWidth = columnCap == "none" ? usableWidth : $"min({columnCap}em, {usableWidth})";

        if (!_dualPage)
        {
            css.Append($"body {{ max-width: {maxWidth} !important; margin: 0 auto !important; " +
                       $"padding: 3em {sideMargin}em 6em {sideMargin}em !important; box-sizing: border-box !important; }}\n");
            css.Append("h1,h2,h3,h4,h5,h6,hgroup,header { margin-top: 2em !important; margin-bottom: 1.5em !important; }\n");
            css.Append("hgroup > * { margin-top: 0 !important; margin-bottom: 0 !important; }\n");
            css.Append("img, svg { max-width: 100%; height: auto; }\n");
        }
        else
        {
            string pageHeightVh = (100.0 / _zoom).ToString("0.##", CultureInfo.InvariantCulture);
            string spineColor = _dark ? "#4a4a4a" : "#d9d9d9";
            css.Append("html { height: 100%; overflow: hidden !important; }\n");
            css.Append($"body {{ height: {pageHeightVh}vh !important; margin: 0 !important; " +
                       $"box-sizing: border-box !important; padding: 2.5em {sideMargin}em !important; " +
                       $"column-count: 2; column-gap: 5em; column-fill: auto; " +
                       $"column-rule: 1px solid {spineColor}; overflow-y: hidden !important; overflow-x: auto !important; }}\n");
            css.Append("body::-webkit-scrollbar { display: none; }\n");
            css.Append("img, svg { max-width: 100%; height: auto; }\n");
        }

        if (_dark)
        {
            css.Append("html, body { background-color: #1e1e1e !important; }\n");
            css.Append("body, body * { color: #e8e6e3 !important; background-color: transparent !important; border-color: #555 !important; }\n");
            css.Append("a, a * { color: #6cb2f7 !important; }\n");
            css.Append("img, svg { opacity: 0.85; }\n");
        }

        // TRANSPORT ROBUSTNESS — the CSS travels as BASE64, decoded in the
        // page (the classic UTF-8-safe atob incantation). MAUI's
        // EvaluateJavaScriptAsync re-escapes/evals scripts on some
        // platforms, which corrupted embedded string escapes (a JSON "\n"
        // became a raw newline inside a JS literal = silent SyntaxError and
        // no styling). Base64's alphabet is inert through every escaping
        // layer. createElementNS + the documentElement fallback keep the
        // injection working across the wildly varying XHTML strictness of
        // real-world ePubs.
        string cssB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(css.ToString()));
        await RunJsAsync(
            $"(function(){{ try {{ var s=document.getElementById('esl-style'); " +
            $"if(!s){{ s=document.createElementNS('http://www.w3.org/1999/xhtml','style'); s.id='esl-style'; " +
            $"(document.head || document.documentElement).appendChild(s); }} " +
            $"s.textContent = decodeURIComponent(escape(window.atob('{cssB64}'))); " +
            $"return 'S-OK'; }} catch(e) {{ return 'S-ERR:'+String(e); }} }})();");
    }

    // ========================================================== toolbar events

    private async void StylePicker_Changed(object? sender, EventArgs e)
    {
        if (FontPicker.SelectedItem is OptionItem f) _fontFamily = f.Value;
        if (SpacingPicker.SelectedItem is OptionItem s) _lineHeight = s.Value;
        if (MarginPicker.SelectedItem is OptionItem m) _pageMargin = m.Value;

        if (_settingsRestored)
        {
            _settings.Current.ReaderFontFamily = _fontFamily;
            _settings.Current.ReaderLineHeight = _lineHeight;
            _settings.Current.ReaderPageMargin = _pageMargin;
            _settings.Save();
        }
        await ApplyReaderStyleAsync();
    }

    private async void ZoomInBtn_Clicked(object? sender, EventArgs e)
    {
        _zoom = Math.Min(3.0, _zoom + 0.1);
        SaveZoom();
        await ApplyReaderStyleAsync();
    }

    private async void ZoomOutBtn_Clicked(object? sender, EventArgs e)
    {
        _zoom = Math.Max(0.5, _zoom - 0.1);
        SaveZoom();
        await ApplyReaderStyleAsync();
    }

    private void SaveZoom()
    {
        if (!_settingsRestored) return;
        _settings.Current.ReaderZoom = _zoom;
        _settings.Save();
    }

    // Toggle-button states (visuals painted by UpdateToggleVisuals). MAUI's
    // CheckBoxes were replaced with these compact buttons — the wide native
    // checkboxes pushed the rest of the toolbar out of view.
    private bool _readAloud = true;
    private bool _chaptersVisible = true;
    private bool _dictVisible = true;

    private async void DualPageBtn_Clicked(object? sender, EventArgs e)
    {
        _dualPage = !_dualPage;
        UpdateToggleVisuals();
        await ApplyReaderStyleAsync();
    }

    private void ReadAloudBtn_Clicked(object? sender, EventArgs e)
    {
        _readAloud = !_readAloud;
        if (!_readAloud) _ttsCts?.Cancel();   // stop any speech immediately
        UpdateToggleVisuals();
    }

    private void ChaptersBtn_Clicked(object? sender, EventArgs e)
    {
        _chaptersVisible = !_chaptersVisible;
        ApplyPanelVisibility();
    }

    private void DictBtn_Clicked(object? sender, EventArgs e)
    {
        _dictVisible = !_dictVisible;
        ApplyPanelVisibility();
    }

    /// <summary>Hide/unhide the side panels by zeroing their grid columns —
    /// the star-sized reader column absorbs the space, and the injected
    /// resize handler re-aligns dual-page mode to whole pairs.</summary>
    private void ApplyPanelVisibility()
    {
        ChaptersPanel.IsVisible = _chaptersVisible;
        ContentGrid.ColumnDefinitions[0].Width = _chaptersVisible ? new GridLength(240) : new GridLength(0);
        DictPanel.IsVisible = _dictVisible;
        ContentGrid.ColumnDefinitions[2].Width = _dictVisible ? new GridLength(380) : new GridLength(0);
        UpdateToggleVisuals();
    }

    /// <summary>Paint the four toggle buttons: accent background = ON,
    /// subtle neutral = OFF (recomputed on theme change too).</summary>
    private void UpdateToggleVisuals()
    {
        Color onBg = Color.FromArgb("#0F6CBD");
        Color offBg = _dark ? Color.FromArgb("#3A3A3A") : Color.FromArgb("#E4E4E4");
        Color onText = Colors.White;
        Color offText = _dark ? Colors.White : Colors.Black;

        void Paint(Button b, bool on)
        {
            b.BackgroundColor = on ? onBg : offBg;
            b.TextColor = on ? onText : offText;
        }
        Paint(DualPageBtn, _dualPage);
        Paint(ReadAloudBtn, _readAloud);
        Paint(ChaptersBtn, _chaptersVisible);
        Paint(DictBtn, _dictVisible);
    }

    private async void ThemeBtn_Clicked(object? sender, EventArgs e)
    {
        _dark = !_dark;
        ApplyTheme();
        _settings.Current.Theme = _dark ? "Dark" : "Light";
        _settings.Save();
        await ApplyReaderStyleAsync();   // night-reading CSS on/off
    }

    private void ApplyTheme()
    {
        Application.Current!.UserAppTheme = _dark ? AppTheme.Dark : AppTheme.Light;
        ThemeBtn.Text = _dark ? "☀️" : "🌙";   // shows the theme you'd switch TO
        UpdateToggleVisuals();                  // toggle colors are theme-aware
    }

    // ==================================================== language selection

    private void LanguagePicker_Changed(object? sender, EventArgs e)
    {
        if (LanguagePicker.SelectedItem is not TranslationLanguage language) return;

        _translator.TargetLanguage = language.Code;
        _bingDict.TargetLanguage = language.Code;

        if (_settingsRestored)
        {
            _settings.Current.TargetLanguageCode = language.Code;
            _settings.Save();
        }

        TranslateHeader.Text = $"Bing Translator ({language.ShortName})";
        DictHeader.Text = $"Bing Dict ({language.ShortName})";

        if (_lastLookedUpTerm.Length > 0)
            _ = LookupAllSourcesAsync(_lastLookedUpTerm, speakAloud: false);
    }

    // ============================================================== lookups

    private static string NormalizeSelection(string raw)
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

    /// <summary>The triple lookup + read-aloud — behaviorally identical to
    /// the WinUI app (dictionaries skipped for sentence-length selections;
    /// stale lookups cancelled; refreshes don't re-speak).</summary>
    private async Task LookupAllSourcesAsync(string term, bool speakAloud = true)
    {
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = new CancellationTokenSource();
        CancellationToken ct = _lookupCts.Token;

        bool dictionarySized = term.Length <= MaxDictionaryTermLength &&
                               term.Split(' ').Length <= MaxDictionaryTermWords;

        _lastLookedUpTerm = term;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            SpeakBtn.IsEnabled = true;
            TermLabel.Text = term;
            PhoneticLabel.Text = "";
            EnglishStatusLabel.Text = dictionarySized
                ? "Looking up…" : "Selection is a sentence — see the Bing Translator section above.";
            EnglishStatusLabel.IsVisible = true;
            ChineseStatusLabel.Text = dictionarySized ? "Looking up…" : "";
            ChineseStatusLabel.IsVisible = dictionarySized;
            TranslationStatusLabel.Text = "Translating…";
            TranslationStatusLabel.IsVisible = true;
            TranslationLabel.Text = "";
            BindableLayout.SetItemsSource(EnglishList, null);
            BindableLayout.SetItemsSource(ChineseList, null);
        });

        if (speakAloud && _readAloud)
            _ = SpeakAsync(term);

        try
        {
            Task<EnglishLookupResult>? englishTask =
                dictionarySized ? _englishDict.LookupAsync(term, ct) : null;
            Task<ChineseLookupResult>? chineseTask =
                dictionarySized ? _bingDict.LookupAsync(term, ct) : null;
            Task<TranslationResult> translateTask = _translator.TranslateAsync(term, ct);

            await Task.WhenAll(new Task[] { englishTask!, chineseTask!, translateTask }
                               .Where(t => t is not null));
            if (ct.IsCancellationRequested) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (englishTask is not null)
                {
                    EnglishLookupResult r = englishTask.Result;
                    PhoneticLabel.Text = r.Phonetic;
                    BindableLayout.SetItemsSource(EnglishList, r.Senses);
                    EnglishStatusLabel.Text = r.StatusMessage;
                    EnglishStatusLabel.IsVisible = r.StatusMessage.Length > 0;
                }
                if (chineseTask is not null)
                {
                    ChineseLookupResult r = chineseTask.Result;
                    BindableLayout.SetItemsSource(ChineseList, r.Entries);
                    ChineseStatusLabel.Text = r.StatusMessage;
                    ChineseStatusLabel.IsVisible = r.StatusMessage.Length > 0;
                }
                TranslationResult t = translateTask.Result;
                TranslationLabel.Text = t.TranslatedText;
                TranslationStatusLabel.Text = t.StatusMessage;
                TranslationStatusLabel.IsVisible = t.StatusMessage.Length > 0;
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
    }

    // ============================================================ text-to-speech

    /// <summary>MAUI's cross-platform speech (Windows voices / macOS voices).
    /// Cancelling the previous utterance keeps rapid selections from
    /// overlapping audibly, mirroring the WinUI MediaPlayer behavior.</summary>
    private async Task SpeakAsync(string text)
    {
        try
        {
            _ttsCts?.Cancel();
            _ttsCts?.Dispose();
            _ttsCts = new CancellationTokenSource();
            await TextToSpeech.Default.SpeakAsync(text,
                new SpeechOptions { Locale = _englishVoice }, _ttsCts.Token);
        }
        catch (OperationCanceledException) { /* replaced by a newer utterance */ }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                StatusLabel.Text = $"Text-to-speech failed: {ex.Message}");
        }
    }

    private void SpeakBtn_Clicked(object? sender, EventArgs e)
    {
        if (_lastLookedUpTerm.Length > 0)
            _ = SpeakAsync(_lastLookedUpTerm);
    }

    // ======================================================= position tracking

    /// <summary>Timer tick: ask the page where the reader is (0..1) and
    /// persist it (throttled), powering "continue where you left off".</summary>
    private async Task PollScrollFractionAsync()
    {
        if (_book is null) return;
        try
        {
            string? result = await ReaderWebView.EvaluateJavaScriptAsync(
                "window.__eslFraction ? window.__eslFraction().toString() : '0'");
            if (result is null) return;
            result = result.Trim('"', '\\', ' ');
            if (!double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction))
                return;

            _currentScrollFraction = Math.Clamp(fraction, 0, 1);
            if ((DateTime.UtcNow - _lastScrollSave).TotalSeconds > 5)
            {
                _lastScrollSave = DateTime.UtcNow;
                SaveReadingPosition();
            }
        }
        catch { /* page mid-navigation — try again next tick */ }
    }

    private void SaveReadingPosition()
    {
        if (_book is null || ChapterList.SelectedItem is not EpubChapter chapter) return;
        _settings.Current.LastChapterIndex = chapter.SpineIndex;
        _settings.Current.LastScrollFraction = _currentScrollFraction;
        _settings.Save();
    }
}
