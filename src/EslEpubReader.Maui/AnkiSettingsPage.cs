// ============================================================================
// AnkiSettingsPage.cs — the Anki Connect settings sheet (the ⚙ next to the
// "+ Anki" button): deck, note type, tags, endpoint and API key, with a
// "Test connection" probe. Built in code rather than XAML — five rows do
// not justify a second XAML page. Saved values go to settings.json
// (ReaderSettings.Anki*) and are pushed into the live AnkiConnectService.
// ============================================================================

using EslEpubReader.Services;

namespace EslEpubReader;

public sealed class AnkiSettingsPage : ContentPage
{
    private readonly SettingsService _settings;
    private readonly AnkiConnectService _anki;
    private readonly Entry _deck, _model, _tags, _url, _key;
    private readonly Label _status;

    public AnkiSettingsPage(SettingsService settings, AnkiConnectService anki)
    {
        _settings = settings;
        _anki = anki;
        Title = "Anki Connect";
        this.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F3F3F3"), Color.FromArgb("#1C1C1C"));

        ReaderSettings s = settings.Current;
        _deck = Field(s.AnkiDeckName, AnkiConnectService.DefaultDeck);
        _model = Field(s.AnkiModelName, AnkiConnectService.DefaultModel);
        _tags = Field(s.AnkiTags, AnkiConnectService.DefaultTags);
        _url = Field(s.AnkiConnectUrl, AnkiConnectService.DefaultUrl);
        _key = Field(s.AnkiConnectKey, "(none)");
        _key.IsPassword = true;

        var grid = new Grid
        {
            RowSpacing = 10,
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
        };
        int row = 0;
        AddRow(grid, row++, "Deck", _deck);
        AddRow(grid, row++, "Note type", _model);
        AddRow(grid, row++, "Tags", _tags);
        AddRow(grid, row++, "AnkiConnect URL", _url);
        AddRow(grid, row++, "API key", _key);

        var testBtn = new Button { Text = "Test connection" };
        testBtn.Clicked += async (_, _) => await TestAsync();
        var cancelBtn = new Button { Text = "Cancel" };
        cancelBtn.Clicked += async (_, _) => await Navigation.PopModalAsync();
        var saveBtn = new Button { Text = "Save", BackgroundColor = Color.FromArgb("#0F6CBD"), TextColor = Colors.White };
        saveBtn.Clicked += async (_, _) => await SaveAsync();

        _status = Text("", 13);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 20),
                Spacing = 14,
                MaximumWidthRequest = 640,
                HorizontalOptions = LayoutOptions.Center,
                Children =
                {
                    Text("Anki Connect", 20, bold: true),
                    Text("“+ Anki” sends the looked-up word, the sentence it was selected in, " +
                         "the dictionary results and the pronunciation clip to Anki. Anki must be " +
                         "running with the AnkiConnect add-on installed (Tools ▸ Add-ons ▸ Get Add-ons, " +
                         "code 2055492159). Cards are cloze notes: the sentence with the word blanked " +
                         "out. The deck and the default note type are created when missing; an existing " +
                         "note type (e.g. Anki's built-in “Cloze”) works too — fields are matched by name.", 13, dim: true),
                    grid,
                    new HorizontalStackLayout { Spacing = 8, Children = { testBtn, cancelBtn, saveBtn } },
                    _status,
                },
            },
        };
    }

    private async Task TestAsync()
    {
        _status.Text = "Connecting…";
        var probe = new AnkiConnectService();
        probe.Configure(Snapshot());
        try
        {
            int version = await probe.PingAsync(CancellationToken.None);
            _status.Text = $"Connected — AnkiConnect API version {version} at {probe.Url}.";
        }
        catch (AnkiConnectException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        ReaderSettings s = _settings.Current;
        ReaderSettings edited = Snapshot();
        s.AnkiDeckName = edited.AnkiDeckName;
        s.AnkiModelName = edited.AnkiModelName;
        s.AnkiTags = edited.AnkiTags;
        s.AnkiConnectUrl = edited.AnkiConnectUrl;
        s.AnkiConnectKey = edited.AnkiConnectKey;
        _settings.Save();
        _anki.Configure(s);
        await Navigation.PopModalAsync();
    }

    /// <summary>The form as a settings object (empty = service default).</summary>
    private ReaderSettings Snapshot() => new()
    {
        AnkiDeckName = _deck.Text?.Trim() ?? "",
        AnkiModelName = _model.Text?.Trim() ?? "",
        AnkiTags = _tags.Text?.Trim() ?? "",
        AnkiConnectUrl = _url.Text?.Trim() ?? "",
        AnkiConnectKey = _key.Text?.Trim() ?? "",
    };

    // --------------------------------------------------------------- widgets

    private static Entry Field(string value, string placeholder)
    {
        var entry = new Entry { Text = value, Placeholder = placeholder, FontSize = 14 };
        entry.SetAppThemeColor(Entry.TextColorProperty, Color.FromArgb("#1A1A1A"), Color.FromArgb("#F0F0F0"));
        entry.SetAppThemeColor(Entry.PlaceholderColorProperty, Color.FromArgb("#888888"), Color.FromArgb("#999999"));
        entry.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#252525"));
        return entry;
    }

    private static Label Text(string text, double size, bool bold = false, bool dim = false)
    {
        var label = new Label
        {
            Text = text,
            FontSize = size,
            FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        if (dim)
            label.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#666666"), Color.FromArgb("#AAAAAA"));
        else
            label.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#1A1A1A"), Color.FromArgb("#F0F0F0"));
        return label;
    }

    private static void AddRow(Grid grid, int row, string caption, Entry entry)
    {
        Label label = Text(caption, 14);
        label.VerticalOptions = LayoutOptions.Center;
        grid.Add(label, 0, row);
        grid.Add(entry, 1, row);
    }
}
