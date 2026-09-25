// ============================================================================
// MainWindow.cs — the reader screen for LINUX, a GTK 4 + WebKitGTK port of
// the MAUI MainPage (same layout, same behavior, same settings.json):
//
//   ┌──────────────────────────────────────────────────────────────┐
//   │ TOOLBAR: open · nav · zoom · font/spacing/margins · toggles  │
//   ├───────────────┬───────────────────────────┬──────────────────┤
//   │ CHAPTERS      │  READER (WebKitWebView)   │ DICTIONARY panel │
//   ├───────────────┴───────────────────────────┴──────────────────┤
//   │ STATUS                                                       │
//   └──────────────────────────────────────────────────────────────┘
//
// WHAT DIFFERS FROM THE MAUI FRONT END:
//
//   * WebView bridge — WebKitGTK has real script-message handlers, so the
//     shared page script POSTS each selection to "eslSelection" instead of
//     queueing it for a 700ms poll: lookups fire instantly.
//
//   * Script injection — the selection/paging script is a WebKitUserScript
//     installed once and run by WebKit on every chapter load; only the
//     reader stylesheet is (re)injected after load, via the same base64
//     wrapper MAUI uses.
//
//   * Panels — GtkPaned gives back the WinUI original's DRAGGABLE splitters
//     (MAUI has no GridSplitter); the toolbar toggles still hide/unhide.
//
//   * Text-to-speech — speech-dispatcher / espeak-ng (see SpeechService).
//
// Everything page-side — the pagination script, the stylesheet, selection
// normalization, option lists — is the shared Services/ReaderPage.cs.
// ============================================================================

using System.Globalization;
using EslEpubReader.Models;
using EslEpubReader.Services;

namespace EslEpubReader;

public sealed class MainWindow
{
    private const int DefaultChaptersWidth = 240;
    private const int DefaultDictionaryWidth = 380;
    private const int WindowWidth = 1500;
    private const int WindowHeight = 900;

    // ------------------------------------------------------------- services

    private readonly EpubParserService _epubParser = new();
    private readonly EnglishDictionaryService _englishDict = new();
    private readonly BingDictionaryService _bingDict = new();
    private readonly BingTranslateService _translator = new();
    private readonly SettingsService _settings = new();
    private readonly SpeechService _speech = new();

    /// <summary>Bing now only answers requests sent from its own page; this
    /// hidden translator page carries them (see BingPageTransport).</summary>
    private readonly BingPageTransport _bingTransport = new();

    // ---------------------------------------------------------------- state

    private EpubBook? _book;
    private int _chapterIndex = -1;
    private CancellationTokenSource? _lookupCts;
    private CancellationTokenSource? _ttsCts;

    private double _zoom = 1.0;
    private string _fontFamily = "";
    private string _lineHeight = "";
    private string _pageMargin = "";
    private bool _dualPage;
    private bool _dark;
    private bool _readAloud = true;
    private bool _chaptersVisible = true;
    private bool _dictVisible = true;

    private string _lastLookedUpTerm = "";
    private double? _pendingScrollFraction;
    private double _currentScrollFraction;
    private DateTime _lastScrollSave = DateTime.MinValue;

    /// <summary>Blocks settings writes until the persisted state has been
    /// re-applied to the controls (dropdown notify fires during restore).</summary>
    private bool _settingsRestored;

    /// <summary>Set when the app was launched with a file, so startup does
    /// not ALSO reopen the remembered book over it.</summary>
    private bool _openedFromCommandLine;

    // --------------------------------------------------------------- widgets

    private readonly Gtk.ApplicationWindow _window;
    private readonly WebKit.WebView _webView;
    private readonly Gtk.ListBox _chapterList;
    private readonly Gtk.Widget _chaptersPanel, _dictPanel;
    private readonly Gtk.Button _prevBtn, _nextBtn, _themeBtn, _speakBtn;
    private readonly Gtk.Button _dualPageBtn, _readAloudBtn, _chaptersBtn, _dictBtn;
    private readonly Gtk.DropDown _fontDrop, _spacingDrop, _marginDrop, _languageDrop;
    private readonly Gtk.Label _zoomLabel, _bookTitleLabel, _statusLabel;
    private readonly Gtk.Label _termLabel, _phoneticLabel;
    private readonly Gtk.Label _translateHeader, _translationStatus, _translationLabel;
    private readonly Gtk.Label _englishStatus, _dictHeader, _chineseStatus;
    private readonly Gtk.Box _englishList, _chineseList;

    /// <summary>GTK CSS for the handful of looks MAUI expressed as XAML
    /// attributes. Colors come from the active (light/dark) GTK theme.</summary>
    private const string AppCss =
        """
        .esl-toolbar { padding: 3px 8px; }
        .esl-toolbar button, .esl-toolbar dropdown > button { min-height: 26px; padding: 2px 10px; }
        .esl-panel-title { font-weight: bold; padding: 10px 12px 6px 12px; }
        .esl-chapter { padding: 6px 12px; }
        .esl-caption { font-size: 8pt; letter-spacing: 2px; opacity: 0.6; }
        .esl-term { font-size: 16pt; font-weight: bold; }
        .esl-accent { color: @accent_color; }
        .esl-italic { font-style: italic; }
        .esl-small { font-size: 9.5pt; }
        .esl-large { font-size: 12.5pt; }
        .esl-dim { opacity: 0.65; }
        .esl-section { font-weight: bold; margin-top: 8px; }
        .esl-status { font-size: 9pt; opacity: 0.75; padding: 4px 12px; }
        .esl-footer { font-size: 8pt; opacity: 0.55; padding: 6px 0; }
        """;

    // ---------------------------------------------------------- construction

    public MainWindow(Gtk.Application app)
    {
        _settings.Load();
        _bingTransport.Install();

        var display = Gdk.Display.GetDefault()!;
        var css = Gtk.CssProvider.New();
        css.LoadFromString(AppCss);
        Gtk.StyleContext.AddProviderForDisplay(display, css, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION);

        // Dev runs: find the icon shipped next to the binary (installed
        // builds find it in the hicolor theme via the install script).
        Gtk.IconTheme.GetForDisplay(display).AddSearchPath(Path.Combine(AppContext.BaseDirectory, "icons"));
        Gtk.Window.SetDefaultIconName(Program.AppId);

        _window = Gtk.ApplicationWindow.New(app);
        _window.SetTitle("ESL EPUB Reader");
        _window.SetDefaultSize(WindowWidth, WindowHeight);
        _window.SetIconName(Program.AppId);

        // ═════════════════════════ TOOLBAR ═════════════════════════
        // Scrollable main controls + a PINNED theme button that can never
        // scroll out of sight on narrow windows (same as MAUI).
        var tools = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        tools.AddCssClass("esl-toolbar");

        tools.Append(Button("Open ePub", OnOpenClicked));
        _prevBtn = Button("◀", () => MoveChapter(-1));
        _nextBtn = Button("▶", () => MoveChapter(+1));
        _prevBtn.Sensitive = _nextBtn.Sensitive = false;
        _prevBtn.TooltipText = "Previous chapter";
        _nextBtn.TooltipText = "Next chapter";
        tools.Append(_prevBtn);
        tools.Append(_nextBtn);

        tools.Append(Button("−", () => ChangeZoom(-0.1)));
        _zoomLabel = Gtk.Label.New("100%");
        _zoomLabel.WidthChars = 5;
        tools.Append(_zoomLabel);
        tools.Append(Button("+", () => ChangeZoom(+0.1)));

        _fontDrop = OptionDrop(ReaderPage.FontOptions, _settings.Current.ReaderFontFamily);
        _spacingDrop = OptionDrop(ReaderPage.SpacingOptions, _settings.Current.ReaderLineHeight);
        _marginDrop = OptionDrop(ReaderPage.MarginOptions, _settings.Current.ReaderPageMargin);
        tools.Append(_fontDrop);
        tools.Append(_spacingDrop);
        tools.Append(_marginDrop);

        _dualPageBtn = Button("Dual page", OnDualPageClicked);
        _readAloudBtn = Button("Read aloud", OnReadAloudClicked);
        _chaptersBtn = Button("Chapters", () => { _chaptersVisible = !_chaptersVisible; ApplyPanelVisibility(); });
        _dictBtn = Button("Dictionary", () => { _dictVisible = !_dictVisible; ApplyPanelVisibility(); });
        tools.Append(_dualPageBtn);
        tools.Append(_readAloudBtn);
        tools.Append(_chaptersBtn);
        tools.Append(_dictBtn);

        _bookTitleLabel = Gtk.Label.New("Open an .epub file to start reading");
        _bookTitleLabel.AddCssClass("heading");
        _bookTitleLabel.MarginStart = 6;
        tools.Append(_bookTitleLabel);

        var toolScroll = Gtk.ScrolledWindow.New();
        toolScroll.SetPolicy(Gtk.PolicyType.Automatic, Gtk.PolicyType.Never);
        toolScroll.PropagateNaturalHeight = true;
        toolScroll.Hexpand = true;
        toolScroll.SetChild(tools);

        _themeBtn = Button("🌙", OnThemeClicked);
        _themeBtn.TooltipText = "Switch between day and night theme";
        _themeBtn.MarginStart = 6;
        _themeBtn.MarginEnd = 8;
        _themeBtn.Valign = Gtk.Align.Center;

        var toolbar = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        toolbar.Append(toolScroll);
        toolbar.Append(_themeBtn);

        // ═════════════════════════ CHAPTERS ════════════════════════
        _chapterList = Gtk.ListBox.New();
        _chapterList.SelectionMode = Gtk.SelectionMode.Single;
        _chapterList.OnRowSelected += OnChapterRowSelected;
        var chapterScroll = Gtk.ScrolledWindow.New();
        chapterScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        chapterScroll.Vexpand = true;
        chapterScroll.SetChild(_chapterList);

        var chaptersBox = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        chaptersBox.Append(Text("Chapters", "esl-panel-title"));
        chaptersBox.Append(chapterScroll);
        chaptersBox.SetSizeRequest(160, -1);
        _chaptersPanel = Framed(chaptersBox);

        // ═════════════════════════ READER ══════════════════════════
        _webView = WebKit.WebView.New();
        _webView.Hexpand = _webView.Vexpand = true;
        WebKit.Settings webSettings = _webView.GetSettings();
        webSettings.AllowFileAccessFromFileUrls = true;
#if DEBUG
        webSettings.EnableDeveloperExtras = true;   // right-click → Inspect Element
#endif
        WebKit.UserContentManager content = _webView.GetUserContentManager();
        content.AddScript(WebKit.UserScript.New(ReaderPage.SelectionWatcherScript,
            WebKit.UserContentInjectedFrames.TopFrame, WebKit.UserScriptInjectionTime.End, null, null));
        content.RegisterScriptMessageHandler(ReaderPage.SelectionMessageHandler, null);
        content.OnScriptMessageReceived += OnScriptMessage;
        _webView.OnLoadChanged += OnLoadChanged;
        _webView.OnDecidePolicy += OnDecidePolicy;
        var readerFrame = Framed(_webView);
        readerFrame.SetSizeRequest(300, -1);

        // ═════════════════════════ DICTIONARY ══════════════════════
        _languageDrop = Gtk.DropDown.NewFromStrings(LanguageCatalog.All.Select(l => l.DisplayName).ToArray());
        _languageDrop.Selected = (uint)Math.Max(0, IndexOf(LanguageCatalog.All,
            LanguageCatalog.FromCode(_settings.Current.TargetLanguageCode)));
        _languageDrop.Halign = Gtk.Align.End;
        _languageDrop.Hexpand = true;

        var captionRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        Gtk.Label caption = Text("DICTIONARY", "esl-caption");
        caption.Wrap = false;
        caption.Valign = Gtk.Align.Center;
        captionRow.Append(caption);
        captionRow.Append(_languageDrop);

        _termLabel = Text("Select a word or phrase in the book", "esl-term");
        _termLabel.Hexpand = true;
        _termLabel.Selectable = true;
        _speakBtn = Button("🔊", SpeakLastTerm);
        _speakBtn.Sensitive = false;
        _speakBtn.Valign = Gtk.Align.Start;
        _speakBtn.TooltipText = "Read aloud";
        var termRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        termRow.Append(_termLabel);
        termRow.Append(_speakBtn);

        _phoneticLabel = Text("", "esl-italic", "esl-accent");

        _translateHeader = Text("Bing Translator (繁體中文)", "esl-section");
        _translationStatus = Text("", "esl-small", "esl-dim");
        _translationLabel = Text("", "esl-large");
        _translationLabel.Selectable = true;
        _englishStatus = Text("", "esl-small", "esl-dim");
        _englishList = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        _dictHeader = Text("Bing Dict (繁體中文)", "esl-section");
        _chineseStatus = Text("", "esl-small", "esl-dim");
        _chineseList = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        _translationStatus.Visible = _englishStatus.Visible = _chineseStatus.Visible = false;

        var results = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        results.MarginTop = results.MarginBottom = 6;
        results.Append(_translateHeader);
        results.Append(_translationStatus);
        results.Append(_translationLabel);
        results.Append(Text("English – English", "esl-section"));
        results.Append(_englishStatus);
        results.Append(_englishList);
        results.Append(_dictHeader);
        results.Append(_chineseStatus);
        results.Append(_chineseList);
        var resultScroll = Gtk.ScrolledWindow.New();
        resultScroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        resultScroll.Vexpand = true;
        resultScroll.SetChild(results);

        var footer = Text("Powered by Microsoft Bing and .NET 10.0", "esl-footer");
        footer.Xalign = 0.5f;

        var dictBox = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        dictBox.MarginStart = dictBox.MarginEnd = 12;
        dictBox.MarginTop = dictBox.MarginBottom = 8;
        dictBox.Append(captionRow);
        dictBox.Append(termRow);
        dictBox.Append(_phoneticLabel);
        dictBox.Append(resultScroll);
        dictBox.Append(footer);
        dictBox.SetSizeRequest(240, -1);
        _dictPanel = Framed(dictBox);

        // ═════════════════════════ CONTENT ═════════════════════════
        // (chapters | reader) | dictionary — the reader absorbs window
        // resizes; the side panels keep their width unless dragged.
        var innerPaned = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        innerPaned.SetStartChild(_chaptersPanel);
        innerPaned.SetEndChild(readerFrame);
        innerPaned.ResizeStartChild = false;
        innerPaned.ShrinkStartChild = innerPaned.ShrinkEndChild = false;
        innerPaned.Position = DefaultChaptersWidth;

        var outerPaned = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        outerPaned.SetStartChild(innerPaned);
        outerPaned.SetEndChild(_dictPanel);
        outerPaned.ResizeEndChild = false;
        outerPaned.ShrinkStartChild = outerPaned.ShrinkEndChild = false;
        outerPaned.Position = WindowWidth - DefaultDictionaryWidth - 16;
        outerPaned.Vexpand = true;
        outerPaned.MarginStart = outerPaned.MarginEnd = 8;

        _statusLabel = Text("Ready.", "esl-status");
        _statusLabel.Wrap = false;
        _statusLabel.Ellipsize = Pango.EllipsizeMode.End;

        var root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        root.Append(toolbar);
        root.Append(outerPaned);
        root.Append(_statusLabel);
        _window.SetChild(root);

        // ═════════════════════════ RESTORE ═════════════════════════
        _fontFamily = SelectedValue(_fontDrop, ReaderPage.FontOptions);
        _lineHeight = SelectedValue(_spacingDrop, ReaderPage.SpacingOptions);
        _pageMargin = SelectedValue(_marginDrop, ReaderPage.MarginOptions);
        ApplyLanguage(LanguageCatalog.All[(int)_languageDrop.Selected]);

        _zoom = Math.Clamp(_settings.Current.ReaderZoom, 0.5, 3.0);
        _zoomLabel.SetText($"{Math.Round(_zoom * 100)}%");

        // Theme: "" = follow the desktop; "Dark"/"Light" = remembered choice.
        _dark = _settings.Current.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => DesktopPrefersDark(),
        };
        ApplyTheme();

        foreach (Gtk.DropDown drop in new[] { _fontDrop, _spacingDrop, _marginDrop })
            drop.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") OnStyleChanged(); };
        _languageDrop.OnNotify += (_, e) => { if (e.Pspec.GetName() == "selected") OnLanguageChanged(); };

        _settingsRestored = true;

        // Reading-position poll (~3s), same cadence as the MAUI build.
        GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 3000, () =>
        {
            _ = PollScrollFractionAsync();
            return true;
        });

        _window.OnCloseRequest += (_, _) =>
        {
            SaveReadingPosition();
            _speech.Stop();
            if (_book is not null) EpubParserService.TryCleanupExtractedFolder(_book.ExtractedFolder);
            return false;
        };

        // "Continue where you left off" — deferred to the first idle so a
        // command-line file (OnOpen, which runs first) can take precedence.
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            if (!_openedFromCommandLine) _ = ReopenLastBookAsync();
            return false;
        });
    }

    public void Present() => _window.Present();

    public void OpenFromCommandLine(string path)
    {
        _openedFromCommandLine = true;
        _ = OpenBookAsync(path);
    }

    private async Task ReopenLastBookAsync()
    {
        string lastBook = _settings.Current.LastBookPath;
        if (lastBook.Length > 0 && File.Exists(lastBook))
            await OpenBookAsync(lastBook);
        else if (lastBook.Length > 0)
        {
            _settings.Current.LastBookPath = "";   // file moved/deleted — forget it
            _settings.Save();
        }
    }

    // ============================================================= book open

    private async void OnOpenClicked()
    {
        try
        {
            var filter = Gtk.FileFilter.New();
            filter.Name = "ePub books";
            filter.AddSuffix("epub");
            filter.AddMimeType("application/epub+zip");
            var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
            filters.Append(filter);

            var dialog = Gtk.FileDialog.New();
            dialog.Title = "Open an ePub book";
            dialog.SetFilters(filters);
            dialog.SetDefaultFilter(filter);

            Gio.File? file = await dialog.OpenAsync(_window);
            string? path = file?.GetPath();
            if (path is not null) await OpenBookAsync(path);
        }
        catch (GLib.GException ex) when (ex.Message.Contains("dismissed", StringComparison.OrdinalIgnoreCase))
        {
            /* user cancelled */
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the picker: {ex.Message}");
        }
    }

    private async Task OpenBookAsync(string path)
    {
        SetStatus($"Opening {Path.GetFileName(path)}…");
        try
        {
            EpubBook book = await _epubParser.ParseAsync(path);

            if (_book is not null)
                EpubParserService.TryCleanupExtractedFolder(_book.ExtractedFolder);
            _book = book;

            // Resume support — identical rules to the other builds: the SAME
            // file reopens at the remembered chapter + position; any other
            // file starts from the beginning.
            bool isRemembered = path == _settings.Current.LastBookPath;
            int startChapter = isRemembered
                ? Math.Clamp(_settings.Current.LastChapterIndex, 0, book.Chapters.Count - 1)
                : 0;
            _pendingScrollFraction = isRemembered && _settings.Current.LastScrollFraction > 0
                ? _settings.Current.LastScrollFraction
                : null;

            _settings.Current.LastBookPath = path;
            _settings.Save();

            _chapterIndex = -1;
            _chapterList.RemoveAll();
            foreach (EpubChapter chapter in book.Chapters)
            {
                Gtk.Label row = Text(chapter.Title, "esl-chapter");
                row.Wrap = false;
                row.Ellipsize = Pango.EllipsizeMode.End;
                row.TooltipText = chapter.Title;
                _chapterList.Append(row);
            }
            SelectChapter(startChapter);

            _bookTitleLabel.SetText($"{book.Title} — {book.Author}");
            _window.SetTitle($"{book.Title} — ESL EPUB Reader");
            _prevBtn.Sensitive = _nextBtn.Sensitive = true;
            SetStatus(isRemembered
                ? $"Welcome back to “{book.Title}” — continuing where you left off."
                : $"Opened “{book.Title}” ({book.Chapters.Count} chapters). Select any word to look it up.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open this ePub: {ex.Message}");
        }
    }

    // ====================================================== chapter navigation

    private void SelectChapter(int index)
    {
        if (_chapterList.GetRowAtIndex(index) is { } row)
            _chapterList.SelectRow(row);   // → OnChapterRowSelected
    }

    private void OnChapterRowSelected(Gtk.ListBox sender, Gtk.ListBox.RowSelectedSignalArgs e)
    {
        if (_book is null || e.Row is null) return;
        int index = e.Row.GetIndex();
        if (index < 0 || index >= _book.Chapters.Count || index == _chapterIndex) return;
        _chapterIndex = index;
        EpubChapter chapter = _book.Chapters[index];

        string fullPath = Path.Combine(_book.ExtractedFolder,
            chapter.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        _webView.LoadUri(new Uri(fullPath).AbsoluteUri);

        _currentScrollFraction = _pendingScrollFraction ?? 0;
        SaveReadingPosition();
        SetStatus($"Chapter {chapter.SpineIndex + 1} of {_book.Chapters.Count}: {chapter.Title}");
    }

    private void MoveChapter(int delta)
    {
        if (_book is null || _chapterIndex < 0) return;
        int target = _chapterIndex + delta;
        if (target >= 0 && target < _book.Chapters.Count)
            SelectChapter(target);
        _webView.GrabFocus();
    }

    // ========================================================= WebView bridge

    /// <summary>The reader shows books, it does not browse: any navigation
    /// that leaves the extracted book (web links, custom schemes) is
    /// dropped, like the MAUI Navigating handler does.</summary>
    private bool OnDecidePolicy(WebKit.WebView sender, WebKit.WebView.DecidePolicySignalArgs e)
    {
        if (e.DecisionType is not (WebKit.PolicyDecisionType.NavigationAction
                                   or WebKit.PolicyDecisionType.NewWindowAction))
            return false;
        if (e.Decision is not WebKit.NavigationPolicyDecision navigation) return false;

        string uri = navigation.NavigationAction.GetRequest().GetUri() ?? "";
        if (uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return false;   // default handling

        navigation.Ignore();
        return true;
    }

    /// <summary>The SELECTION bridge: the shared page script posts every new
    /// selection to the eslSelection handler registered above.</summary>
    private void OnScriptMessage(WebKit.UserContentManager sender,
                                 WebKit.UserContentManager.ScriptMessageReceivedSignalArgs e)
    {
        if (_book is null) return;
        string term = ReaderPage.NormalizeSelection(e.Value.ToString() ?? "");
        if (term.Length > 0) _ = LookupAllSourcesAsync(term);
    }

    /// <summary>Chapter finished loading (the user script has already run):
    /// apply the reader stylesheet and restore any pending position.</summary>
    private async void OnLoadChanged(WebKit.WebView sender, WebKit.WebView.LoadChangedSignalArgs e)
    {
        if (e.LoadEvent != WebKit.LoadEvent.Finished) return;

        await ApplyReaderStyleAsync();

        if (_pendingScrollFraction is double fraction)
        {
            _pendingScrollFraction = null;
            _currentScrollFraction = fraction;
            string f = fraction.ToString(CultureInfo.InvariantCulture);
            await RunJsAsync($"window.__eslRestore && window.__eslRestore({f});");
        }
    }

    private async Task<JavaScriptCore.Value?> RunJsAsync(string script)
    {
        try { return await _webView.EvaluateJavascriptAsync(script); }
        catch { return null; /* page mid-navigation — the next load re-applies */ }
    }

    // ======================================================= reader stylesheet

    private async Task ApplyReaderStyleAsync()
    {
        _zoomLabel.SetText($"{Math.Round(_zoom * 100)}%");
        string css = ReaderPage.BuildStyleSheet(_zoom, _fontFamily, _lineHeight, _pageMargin, _dualPage, _dark);
        await RunJsAsync(ReaderPage.StyleInjectionScript(css));
    }

    // ========================================================== toolbar events

    private async void OnStyleChanged()
    {
        _fontFamily = SelectedValue(_fontDrop, ReaderPage.FontOptions);
        _lineHeight = SelectedValue(_spacingDrop, ReaderPage.SpacingOptions);
        _pageMargin = SelectedValue(_marginDrop, ReaderPage.MarginOptions);

        if (_settingsRestored)
        {
            _settings.Current.ReaderFontFamily = _fontFamily;
            _settings.Current.ReaderLineHeight = _lineHeight;
            _settings.Current.ReaderPageMargin = _pageMargin;
            _settings.Save();
        }
        await ApplyReaderStyleAsync();
    }

    private async void ChangeZoom(double delta)
    {
        _zoom = Math.Clamp(_zoom + delta, 0.5, 3.0);
        if (_settingsRestored)
        {
            _settings.Current.ReaderZoom = _zoom;
            _settings.Save();
        }
        await ApplyReaderStyleAsync();
    }

    private async void OnDualPageClicked()
    {
        _dualPage = !_dualPage;
        UpdateToggleVisuals();
        await ApplyReaderStyleAsync();
        _webView.GrabFocus();   // keep PgDn/PgUp working right after the click
    }

    private void OnReadAloudClicked()
    {
        _readAloud = !_readAloud;
        if (!_readAloud) _ttsCts?.Cancel();   // stop any speech immediately
        UpdateToggleVisuals();
    }

    /// <summary>Hide/unhide the side panels; GtkPaned gives the space to the
    /// reader and the page script's resize handler re-aligns dual pages.</summary>
    private void ApplyPanelVisibility()
    {
        _chaptersPanel.Visible = _chaptersVisible;
        _dictPanel.Visible = _dictVisible;
        UpdateToggleVisuals();
        _webView.GrabFocus();   // keep the reading keys alive after the click
    }

    /// <summary>Toggle buttons: the theme's accent ("suggested-action") = ON.</summary>
    private void UpdateToggleVisuals()
    {
        static void Paint(Gtk.Button b, bool on)
        {
            if (on) b.AddCssClass("suggested-action");
            else b.RemoveCssClass("suggested-action");
        }
        Paint(_dualPageBtn, _dualPage);
        Paint(_readAloudBtn, _readAloud);
        Paint(_chaptersBtn, _chaptersVisible);
        Paint(_dictBtn, _dictVisible);
    }

    private async void OnThemeClicked()
    {
        _dark = !_dark;
        ApplyTheme();
        _settings.Current.Theme = _dark ? "Dark" : "Light";
        _settings.Save();
        await ApplyReaderStyleAsync();   // night-reading CSS on/off
    }

    private void ApplyTheme()
    {
        Gtk.Settings.GetDefault()!.GtkApplicationPreferDarkTheme = _dark;
        _themeBtn.SetLabel(_dark ? "☀️" : "🌙");   // shows the theme you'd switch TO

        // Paint the web view's own backdrop to match, so chapter loads do
        // not flash white in night mode before the stylesheet lands.
        var backdrop = new Gdk.RGBA();
        backdrop.Parse(_dark ? "#1e1e1e" : "#ffffff");
        _webView.SetBackgroundColor(backdrop);
        UpdateToggleVisuals();
    }

    /// <summary>The desktop's light/dark preference (GNOME and most
    /// GTK-based desktops publish it as org.gnome.desktop.interface
    /// color-scheme). Looked up defensively: creating Gio.Settings for a
    /// schema that is not installed aborts the process.</summary>
    private static bool DesktopPrefersDark()
    {
        try
        {
            const string schemaId = "org.gnome.desktop.interface";
            Gio.SettingsSchema? schema = Gio.SettingsSchemaSource.GetDefault()?.Lookup(schemaId, true);
            if (schema is null || !schema.HasKey("color-scheme")) return false;
            return Gio.Settings.New(schemaId).GetString("color-scheme") == "prefer-dark";
        }
        catch { return false; }
    }

    // ==================================================== language selection

    private void OnLanguageChanged()
    {
        TranslationLanguage language = LanguageCatalog.All[(int)_languageDrop.Selected];
        ApplyLanguage(language);

        if (_settingsRestored)
        {
            _settings.Current.TargetLanguageCode = language.Code;
            _settings.Save();
        }
        if (_lastLookedUpTerm.Length > 0)
            _ = LookupAllSourcesAsync(_lastLookedUpTerm, speakAloud: false);
    }

    private void ApplyLanguage(TranslationLanguage language)
    {
        _translator.TargetLanguage = language.Code;
        _bingDict.TargetLanguage = language.Code;
        _translateHeader.SetText($"Bing Translator ({language.ShortName})");
        _dictHeader.SetText($"Bing Dict ({language.ShortName})");
    }

    // ============================================================== lookups

    /// <summary>The triple lookup + read-aloud — behaviorally identical to
    /// the other builds (dictionaries skipped for sentence-length
    /// selections; stale lookups cancelled; refreshes don't re-speak).</summary>
    private async Task LookupAllSourcesAsync(string term, bool speakAloud = true)
    {
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = new CancellationTokenSource();
        CancellationToken ct = _lookupCts.Token;

        bool dictionarySized = ReaderPage.IsDictionarySized(term);

        _lastLookedUpTerm = term;
        _speakBtn.Sensitive = true;
        _termLabel.SetText(term);
        _phoneticLabel.SetText("");
        ShowStatus(_englishStatus, dictionarySized
            ? "Looking up…" : "Selection is a sentence — see the Bing Translator section above.");
        ShowStatus(_chineseStatus, dictionarySized ? "Looking up…" : "");
        ShowStatus(_translationStatus, "Translating…");
        _translationLabel.SetText("");
        Clear(_englishList);
        Clear(_chineseList);

        if (speakAloud && _readAloud)
            _ = SpeakAsync(term);

        try
        {
            Task<EnglishLookupResult>? englishTask =
                dictionarySized ? _englishDict.LookupAsync(term, ct) : null;
            Task<ChineseLookupResult>? chineseTask =
                dictionarySized ? _bingDict.LookupAsync(term, ct) : null;
            Task<TranslationResult> translateTask = _translator.TranslateAsync(term, ct);

            await Task.WhenAll(new Task?[] { englishTask, chineseTask, translateTask }.OfType<Task>());
            if (ct.IsCancellationRequested) return;

            // Back on the GTK thread (GLibSynchronizationContext).
            if (englishTask is not null)
            {
                EnglishLookupResult r = englishTask.Result;
                _phoneticLabel.SetText(r.Phonetic);
                foreach (EnglishSense sense in r.Senses)
                {
                    var item = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
                    item.Append(Text(sense.PartOfSpeech, "esl-small", "esl-italic", "esl-accent"));
                    item.Append(Text(sense.Definition, selectable: true));
                    if (sense.HasExample) item.Append(Text(sense.Example, "esl-small", "esl-italic", "esl-dim"));
                    if (sense.HasSynonyms) item.Append(Text(sense.Synonyms, "esl-small", "esl-dim"));
                    _englishList.Append(item);
                }
                ShowStatus(_englishStatus, r.StatusMessage);
            }
            if (chineseTask is not null)
            {
                ChineseLookupResult r = chineseTask.Result;
                foreach (BingDictionaryEntry entry in r.Entries)
                {
                    var item = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
                    item.Append(Text(entry.PartOfSpeech, "esl-small", "esl-italic", "esl-accent"));
                    item.Append(Text(entry.Term, "esl-large", selectable: true));
                    if (entry.HasBackTranslations) item.Append(Text(entry.BackTranslations, "esl-small", "esl-dim"));
                    _chineseList.Append(item);
                }
                ShowStatus(_chineseStatus, r.StatusMessage);
            }
            TranslationResult t = translateTask.Result;
            _translationLabel.SetText(t.TranslatedText);
            ShowStatus(_translationStatus, t.StatusMessage);
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
    }

    // ============================================================ text-to-speech

    private async Task SpeakAsync(string text)
    {
        try
        {
            _ttsCts?.Cancel();
            _ttsCts?.Dispose();
            _ttsCts = new CancellationTokenSource();
            await _speech.SpeakAsync(text, _ttsCts.Token);
        }
        catch (OperationCanceledException) { /* replaced by a newer utterance */ }
        catch (Exception ex)
        {
            SetStatus($"Text-to-speech failed: {ex.Message}");
        }
    }

    private void SpeakLastTerm()
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
        JavaScriptCore.Value? result =
            await RunJsAsync("window.__eslFraction ? window.__eslFraction() : 0");
        if (result is null || !result.IsNumber()) return;

        _currentScrollFraction = Math.Clamp(result.ToDouble(), 0, 1);
        if ((DateTime.UtcNow - _lastScrollSave).TotalSeconds > 5)
        {
            _lastScrollSave = DateTime.UtcNow;
            SaveReadingPosition();
        }
    }

    private void SaveReadingPosition()
    {
        if (_book is null || _chapterIndex < 0) return;
        _settings.Current.LastChapterIndex = _book.Chapters[_chapterIndex].SpineIndex;
        _settings.Current.LastScrollFraction = _currentScrollFraction;
        _settings.Save();
    }

    // ============================================================ UI helpers

    private void SetStatus(string text) => _statusLabel.SetText(text);

    private static Gtk.Button Button(string label, Action onClick)
    {
        var button = Gtk.Button.NewWithLabel(label);
        button.OnClicked += (_, _) => onClick();
        return button;
    }

    /// <summary>A left-aligned, word-wrapping label with optional CSS classes.</summary>
    private static Gtk.Label Text(string text, params string[] cssClasses) => Text(text, false, cssClasses);

    private static Gtk.Label Text(string text, string cssClass, bool selectable) => Text(text, selectable, cssClass);

    private static Gtk.Label Text(string text, bool selectable, params string[] cssClasses)
    {
        var label = Gtk.Label.New(text);
        label.Xalign = 0;
        label.Wrap = true;
        label.WrapMode = Pango.WrapMode.WordChar;
        label.Selectable = selectable;
        foreach (string c in cssClasses) label.AddCssClass(c);
        return label;
    }

    private static void ShowStatus(Gtk.Label label, string text)
    {
        label.SetText(text);
        label.Visible = text.Length > 0;
    }

    private static void Clear(Gtk.Box box)
    {
        while (box.GetFirstChild() is { } child) box.Remove(child);
    }

    /// <summary>A bordered panel, the GTK counterpart of the MAUI Borders.</summary>
    private static Gtk.Frame Framed(Gtk.Widget child)
    {
        var frame = Gtk.Frame.New(null);
        frame.SetChild(child);
        return frame;
    }

    private static Gtk.DropDown OptionDrop(IReadOnlyList<ReaderOption> options, string value)
    {
        var drop = Gtk.DropDown.NewFromStrings(options.Select(o => o.Display).ToArray());
        drop.Selected = (uint)ReaderPage.IndexOfValue(options, value);
        drop.Valign = Gtk.Align.Center;
        return drop;
    }

    private static string SelectedValue(Gtk.DropDown drop, IReadOnlyList<ReaderOption> options) =>
        drop.Selected < options.Count ? options[(int)drop.Selected].Value : "";

    private static int IndexOf<T>(IReadOnlyList<T> list, T item)
    {
        for (int i = 0; i < list.Count; i++)
            if (EqualityComparer<T>.Default.Equals(list[i], item)) return i;
        return -1;
    }
}
