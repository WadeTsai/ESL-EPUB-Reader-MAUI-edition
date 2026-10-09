// ============================================================================
// AnkiSettingsWindow.cs — the Anki Connect settings dialog on Linux (the ⚙
// next to the "+ Anki" button), the GTK counterpart of the MAUI
// AnkiSettingsPage: deck, note type, tags, endpoint and API key, with a
// "Test connection" probe. Saved values go to settings.json and into the
// live AnkiConnectService.
// ============================================================================

using EslEpubReader.Services;

namespace EslEpubReader;

public static class AnkiSettingsWindow
{
    public static void Show(Gtk.Window parent, SettingsService settings, AnkiConnectService anki)
    {
        var window = Gtk.Window.New();
        window.SetTitle("Anki Connect");
        window.SetTransientFor(parent);
        window.Modal = true;
        window.SetDefaultSize(560, -1);

        ReaderSettings s = settings.Current;
        var grid = Gtk.Grid.New();
        grid.RowSpacing = 8;
        grid.ColumnSpacing = 12;
        Gtk.Entry deck = Row(grid, 0, "Deck", s.AnkiDeckName, AnkiConnectService.DefaultDeck);
        Gtk.Entry model = Row(grid, 1, "Note type", s.AnkiModelName, AnkiConnectService.DefaultModel);
        Gtk.Entry tags = Row(grid, 2, "Tags", s.AnkiTags, AnkiConnectService.DefaultTags);
        Gtk.Entry url = Row(grid, 3, "AnkiConnect URL", s.AnkiConnectUrl, AnkiConnectService.DefaultUrl);
        Gtk.Entry key = Row(grid, 4, "API key", s.AnkiConnectKey, "(none)");
        key.Visibility = false;   // password-style

        var intro = Gtk.Label.New(
            "“+ Anki” sends the looked-up word, the sentence it was selected in, the dictionary " +
            "results and the pronunciation clip to Anki. Anki must be running with the AnkiConnect " +
            "add-on installed (Tools ▸ Add-ons ▸ Get Add-ons, code 2055492159). Cards are cloze " +
            "notes: the sentence with the word blanked out. The deck and the default note type are " +
            "created when missing; an existing note type (e.g. Anki's built-in “Cloze”) works too — " +
            "fields are matched by name.");
        intro.Xalign = 0;
        intro.Wrap = true;
        intro.WrapMode = Pango.WrapMode.WordChar;
        intro.AddCssClass("esl-small");
        intro.AddCssClass("esl-dim");

        var status = Gtk.Label.New("");
        status.Xalign = 0;
        status.Wrap = true;
        status.WrapMode = Pango.WrapMode.WordChar;
        status.AddCssClass("esl-small");

        ReaderSettings Snapshot() => new()
        {
            AnkiDeckName = TextOf(deck),
            AnkiModelName = TextOf(model),
            AnkiTags = TextOf(tags),
            AnkiConnectUrl = TextOf(url),
            AnkiConnectKey = TextOf(key),
        };

        var testBtn = Gtk.Button.NewWithLabel("Test connection");
        testBtn.OnClicked += async (_, _) =>
        {
            status.SetText("Connecting…");
            var probe = new AnkiConnectService();
            probe.Configure(Snapshot());
            try
            {
                int version = await probe.PingAsync(CancellationToken.None);
                status.SetText($"Connected — AnkiConnect API version {version} at {probe.Url}.");
            }
            catch (AnkiConnectException ex)
            {
                status.SetText(ex.Message);
            }
        };

        var cancelBtn = Gtk.Button.NewWithLabel("Cancel");
        cancelBtn.OnClicked += (_, _) => window.Close();

        var saveBtn = Gtk.Button.NewWithLabel("Save");
        saveBtn.AddCssClass("suggested-action");
        saveBtn.OnClicked += (_, _) =>
        {
            ReaderSettings edited = Snapshot();
            s.AnkiDeckName = edited.AnkiDeckName;
            s.AnkiModelName = edited.AnkiModelName;
            s.AnkiTags = edited.AnkiTags;
            s.AnkiConnectUrl = edited.AnkiConnectUrl;
            s.AnkiConnectKey = edited.AnkiConnectKey;
            settings.Save();
            anki.Configure(s);
            window.Close();
        };

        var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        buttons.Halign = Gtk.Align.End;
        buttons.Append(testBtn);
        buttons.Append(cancelBtn);
        buttons.Append(saveBtn);

        var root = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        root.MarginStart = root.MarginEnd = root.MarginTop = root.MarginBottom = 16;
        root.Append(intro);
        root.Append(grid);
        root.Append(status);
        root.Append(buttons);

        window.SetChild(root);
        window.Present();
    }

    private static Gtk.Entry Row(Gtk.Grid grid, int row, string caption, string value, string placeholder)
    {
        var label = Gtk.Label.New(caption);
        label.Xalign = 1;
        label.Halign = Gtk.Align.End;

        var entry = Gtk.Entry.New();
        entry.Hexpand = true;
        entry.PlaceholderText = placeholder;
        entry.GetBuffer().SetText(value, -1);

        grid.Attach(label, 0, row, 1, 1);
        grid.Attach(entry, 1, row, 1, 1);
        return entry;
    }

    private static string TextOf(Gtk.Entry entry) => entry.GetBuffer().GetText().Trim();
}
