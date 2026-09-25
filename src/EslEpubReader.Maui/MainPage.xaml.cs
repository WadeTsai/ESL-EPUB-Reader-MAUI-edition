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

    // ---------------------------------------------------------- construction

    public MainPage()
    {
        InitializeComponent();
        _settings.Load();

        // Populate the pickers, then re-select the persisted values. The
        // SelectedIndexChanged handlers run during these assignments but
        // are inert until _settingsRestored is set.
        FontPicker.ItemsSource = ReaderPage.FontOptions.ToList();
        SpacingPicker.ItemsSource = ReaderPage.SpacingOptions.ToList();
        MarginPicker.ItemsSource = ReaderPage.MarginOptions.ToList();
        LanguagePicker.ItemsSource = LanguageCatalog.All.ToList();

        SelectByValue(FontPicker, ReaderPage.FontOptions, _settings.Current.ReaderFontFamily);
        SelectByValue(SpacingPicker, ReaderPage.SpacingOptions, _settings.Current.ReaderLineHeight);
        SelectByValue(MarginPicker, ReaderPage.MarginOptions, _settings.Current.ReaderPageMargin);
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

    private static void SelectByValue(Picker picker, IReadOnlyList<ReaderOption> options, string value) =>
        picker.SelectedIndex = ReaderPage.IndexOfValue(options, value);

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

            string term = ReaderPage.NormalizeSelection(raw);
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

        await RunJsAsync(ReaderPage.SelectionWatcherScript);
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

        string css = ReaderPage.BuildStyleSheet(_zoom, _fontFamily, _lineHeight, _pageMargin, _dualPage, _dark);
        await RunJsAsync(ReaderPage.StyleInjectionScript(css));
    }

    // ========================================================== toolbar events

    private async void StylePicker_Changed(object? sender, EventArgs e)
    {
        if (FontPicker.SelectedItem is ReaderOption f) _fontFamily = f.Value;
        if (SpacingPicker.SelectedItem is ReaderOption s) _lineHeight = s.Value;
        if (MarginPicker.SelectedItem is ReaderOption m) _pageMargin = m.Value;

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
        ReaderWebView.Focus();   // keep PgDn/PgUp working right after the click
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
        ReaderWebView.Focus();   // keep the reading keys alive after the click
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

    /// <summary>The triple lookup + read-aloud — behaviorally identical to
    /// the WinUI app (dictionaries skipped for sentence-length selections;
    /// stale lookups cancelled; refreshes don't re-speak).</summary>
    private async Task LookupAllSourcesAsync(string term, bool speakAloud = true)
    {
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _lookupCts = new CancellationTokenSource();
        CancellationToken ct = _lookupCts.Token;

        bool dictionarySized = ReaderPage.IsDictionarySized(term);

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
