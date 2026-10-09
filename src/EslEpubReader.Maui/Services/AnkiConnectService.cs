// ============================================================================
// Services/AnkiConnectService.cs
// ============================================================================
// "Add to Anki" — sends the word the reader just looked up to a running
// Anki through the AnkiConnect add-on (https://git.sr.ht/~foosoft/anki-connect,
// add-on code 2055492159), the way the Saladict browser extension does
// (https://saladict.crimx.com/anki).
//
// AnkiConnect is a tiny JSON-RPC server inside Anki, listening on
// http://127.0.0.1:8765 by default: POST {"action", "version", "params"},
// get back {"result", "error"}. No browser Origin header is sent by this
// app, so AnkiConnect's CORS allow-list does not apply; an optional API
// key (the add-on's "apiKey" config) is honored when the user sets one.
//
// NOTE TYPE — CLOZE, SALADICT LAYOUT:
//   Cards are real Anki CLOZE notes: the sentence the word was selected in
//   goes into ContextCloze with the word as a {{c1::…}} deletion, so Anki
//   asks for the word in its context. The default note type,
//   "ESL EPUB Reader Cloze", is created on first use (isCloze) with
//   Saladict's fields, card template and styling — Saladict's own
//   "Saladict Word" is a standard type that merely uses cloze syntax; this
//   is the same card as a genuine cloze type:
//
//     Date          epoch milliseconds (when it was added)
//     Text          the looked-up word/phrase (the duplicate check key)
//     Translation   HTML: Bing Translator + English–English (with IPA)
//                   + Bing Dict
//     Context       the plain sentence
//     ContextCloze  "Like the {{c1::outbreak}} of some new killer virus…"
//                   ("" when the sentence is unknown — the template then
//                   shows the word as a heading, like Saladict)
//     Note          empty — the learner's own notes, edited in Anki
//     Title         "Book title — Chapter title"
//     Url           file:// URI of the .epub
//     Favicon       empty (no favicon for a local book)
//     Audio         [sound:…] — the Google Translate pronunciation clip
//
//   ANY note type works, though: the fields are matched BY NAME to what the
//   configured note type has (others are skipped). A cloze type WITHOUT a
//   ContextCloze field gets the cloze sentence in Text instead, so Anki's
//   built-in "Cloze" (Text + Back Extra — the extras go to Back Extra)
//   works too, as does Saladict's "Saladict Word".
//
// DUPLICATES: a word already in the target deck (same note type) is not
// added again; the result says so and the caller reports it.
//
// Every failure surfaces as an AnkiConnectException with a human-readable
// message ("Anki is not running…", "wrong API key…") for the status bar.
// ============================================================================

using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EslEpubReader.Models;

namespace EslEpubReader.Services;

/// <summary>Everything the reader knows about one looked-up word, ready to
/// be turned into an Anki note (see AnkiConnectService.BuildFields).</summary>
public sealed class AnkiCard
{
    /// <summary>The looked-up word or phrase.</summary>
    public required string Text { get; init; }

    /// <summary>IPA from the English dictionary, "" when unknown.</summary>
    public string Phonetic { get; init; } = "";

    /// <summary>The sentence the word was selected in, "" when unknown.</summary>
    public string Context { get; init; } = "";

    /// <summary>Bing Translator's rendering of the selection.</summary>
    public string Translation { get; init; } = "";

    /// <summary>Display name of the translation target ("繁體中文").</summary>
    public string TargetLanguage { get; init; } = "";

    public IReadOnlyList<EnglishSense> Senses { get; init; } = [];
    public IReadOnlyList<BingDictionaryEntry> Entries { get; init; } = [];

    /// <summary>"Book title — Chapter title".</summary>
    public string Title { get; init; } = "";

    /// <summary>file:// URI of the book.</summary>
    public string Url { get; init; } = "";

    /// <summary>Pronunciation clip (MP3) to attach, or null for none.</summary>
    public byte[]? AudioMp3 { get; init; }
}

public sealed class AnkiAddResult
{
    public required long NoteId { get; init; }

    /// <summary>True when the word was already in the deck and nothing was
    /// added; NoteId is then the existing note.</summary>
    public bool AlreadyExisted { get; init; }
}

public sealed class AnkiConnectException(string message) : Exception(message);

/// <summary>What the configured note type looks like in Anki — decides how
/// a card's data is spread over its fields.</summary>
public sealed record AnkiModelInfo(bool IsCloze, IReadOnlyList<string> Fields)
{
    public bool Has(string field) => Fields.Any(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));
}

public sealed class AnkiConnectService
{
    public const string DefaultUrl = "http://127.0.0.1:8765";
    public const string DefaultDeck = "ESL EPUB Reader";
    public const string DefaultModel = "ESL EPUB Reader Cloze";
    public const string DefaultTags = "esl-epub-reader";
    private const int ApiVersion = 6;

    // ------------------------------------------------------------- settings

    /// <summary>AnkiConnect endpoint (the add-on's webBindAddress/Port).</summary>
    public string Url { get; set; } = DefaultUrl;

    /// <summary>The add-on's "apiKey" config value; "" when it has none.</summary>
    public string ApiKey { get; set; } = "";

    public string DeckName { get; set; } = DefaultDeck;
    public string ModelName { get; set; } = DefaultModel;

    /// <summary>Space-separated tags put on every note.</summary>
    public string Tags { get; set; } = DefaultTags;

    /// <summary>Copy the persisted settings into this service.</summary>
    public void Configure(ReaderSettings settings)
    {
        Url = settings.AnkiConnectUrl.Trim().Length > 0 ? settings.AnkiConnectUrl.Trim() : DefaultUrl;
        ApiKey = settings.AnkiConnectKey.Trim();
        DeckName = settings.AnkiDeckName.Trim().Length > 0 ? settings.AnkiDeckName.Trim() : DefaultDeck;
        ModelName = settings.AnkiModelName.Trim().Length > 0 ? settings.AnkiModelName.Trim() : DefaultModel;
        Tags = settings.AnkiTags.Trim();
        _prepared = "";
        _model = null;
    }

    // ------------------------------------------------------------ transport

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record AnkiRequest(
        string action,
        int version,
        string? key,
        [property: JsonPropertyName("params")] object? parameters);

    /// <summary>"url|deck|model" for which the deck and note type have been
    /// verified this session (saves round trips per card), and what that
    /// note type looks like.</summary>
    private string _prepared = "";
    private AnkiModelInfo? _model;

    /// <summary>Ping AnkiConnect; returns its API version. Throws
    /// AnkiConnectException when Anki is not reachable.</summary>
    public async Task<int> PingAsync(CancellationToken ct)
    {
        JsonElement result = await InvokeAsync("version", null, ct);
        return result.ValueKind == JsonValueKind.Number ? result.GetInt32() : 0;
    }

    /// <summary>Add one card to the configured deck (creating the deck and
    /// the default note type when missing). Throws AnkiConnectException
    /// with a user-facing message on any failure.</summary>
    public async Task<AnkiAddResult> AddAsync(AnkiCard card, CancellationToken ct)
    {
        string word = card.Text.Trim();
        if (word.Length == 0)
            throw new AnkiConnectException("nothing to add — look up a word first.");

        AnkiModelInfo model = await EnsureDeckAndModelAsync(ct);

        // Same word already in this deck → do not add it twice. Keyed on the
        // field that holds the bare word; a note type without one is
        // searched as free text.
        string wordField = model.Has("Word") ? "Word" : !TextIsCloze(model) && model.Has("Text") ? "Text" : "";
        string query = $"\"deck:{EscapeSearch(DeckName)}\" \"note:{EscapeSearch(ModelName)}\" " +
                       (wordField.Length > 0 ? $"\"{wordField}:{EscapeSearch(word)}\"" : $"\"{EscapeSearch(word)}\"");
        JsonElement found = await InvokeAsync("findNotes", new { query }, ct);
        if (found.ValueKind == JsonValueKind.Array && found.GetArrayLength() > 0)
            return new AnkiAddResult { NoteId = found[0].GetInt64(), AlreadyExisted = true };

        // The clip's [sound:…] tag goes to the first of these the note type has.
        string? audioField = model.Has("Audio") ? "Audio" : model.Has("Back Extra") ? "Back Extra" : null;
        object? audio = card.AudioMp3 is { Length: > 0 } mp3 && audioField is not null
            ? new[]
            {
                new
                {
                    data = Convert.ToBase64String(mp3),
                    filename = AudioFileName(word),
                    fields = new[] { audioField },
                },
            }
            : null;

        var note = new
        {
            deckName = DeckName,
            modelName = ModelName,
            fields = BuildFields(card, model),
            // The Word check above is the duplicate rule that matters.
            // duplicateScope "deck" is load-bearing: any other scope makes
            // AnkiConnect run Anki's full field check, which rejects {{c1::…}}
            // in a non-cloze note type ("cannot create note for unknown
            // reason") — e.g. Saladict's standard-type "Saladict Word".
            options = new { allowDuplicate = true, duplicateScope = "deck" },
            tags = TagList,
            audio,
        };
        JsonElement id = await InvokeAsync("addNote", new { note }, ct);
        if (id.ValueKind != JsonValueKind.Number)
            throw new AnkiConnectException("Anki did not return a note id.");
        return new AnkiAddResult { NoteId = id.GetInt64() };
    }

    private string[] TagList =>
        Tags.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Make sure the deck and the note type exist (creating the
    /// default cloze note type when missing) and learn the note type's
    /// shape; cached per configuration.</summary>
    private async Task<AnkiModelInfo> EnsureDeckAndModelAsync(CancellationToken ct)
    {
        string key = $"{Url}|{DeckName}|{ModelName}";
        if (_prepared == key && _model is not null) return _model;

        // createDeck is idempotent ("will not overwrite a deck that exists").
        await InvokeAsync("createDeck", new { deck = DeckName }, ct);

        JsonElement models = await InvokeAsync("modelNames", null, ct);
        bool exists = models.ValueKind == JsonValueKind.Array &&
                      models.EnumerateArray().Any(m => m.GetString() == ModelName);
        if (!exists)
        {
            await InvokeAsync("createModel", new
            {
                modelName = ModelName,
                inOrderFields = ClozeModelFields,
                css = ModelCss,
                isCloze = true,
                cardTemplates = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["Name"] = "Cloze",
                        ["Front"] = ClozeFront,
                        ["Back"] = ClozeBack,
                    },
                },
            }, ct);
        }

        // Note type shape: "type" 1 = cloze; "flds" = its fields in order.
        JsonElement info = await InvokeAsync("findModelsByName", new { modelNames = new[] { ModelName } }, ct);
        if (info.ValueKind != JsonValueKind.Array || info.GetArrayLength() == 0)
            throw new AnkiConnectException($"Anki has no note type named “{ModelName}”.");
        JsonElement m = info[0];
        bool isCloze = m.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.Number && type.GetInt32() == 1;
        List<string> fields = m.TryGetProperty("flds", out JsonElement flds) && flds.ValueKind == JsonValueKind.Array
            ? flds.EnumerateArray().Select(f => f.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "").Where(n => n.Length > 0).ToList()
            : [];

        _model = new AnkiModelInfo(isCloze, fields);
        _prepared = key;
        return _model;
    }

    private async Task<JsonElement> InvokeAsync(string action, object? parameters, CancellationToken ct)
    {
        var request = new AnkiRequest(action, ApiVersion, ApiKey.Length > 0 ? ApiKey : null, parameters);
        string json = JsonSerializer.Serialize(request, JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await Http.PostAsync(Url, new StringContent(json, Encoding.UTF8, "application/json"), ct);
        }
        catch (HttpRequestException)
        {
            throw new AnkiConnectException(
                $"Anki is not reachable at {Url} — is Anki running with the AnkiConnect add-on (code 2055492159) installed?");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AnkiConnectException($"Anki did not answer at {Url} (timed out).");
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or ArgumentException)
        {
            throw new AnkiConnectException($"“{Url}” is not a valid AnkiConnect address.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new AnkiConnectException("AnkiConnect refused the request (403 Forbidden).");

            string body = await response.Content.ReadAsStringAsync(ct);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException)
            {
                throw new AnkiConnectException($"{Url} did not answer like AnkiConnect — is something else listening there?");
            }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new AnkiConnectException($"{Url} did not answer like AnkiConnect.");
                if (doc.RootElement.TryGetProperty("error", out JsonElement error) &&
                    error.ValueKind == JsonValueKind.String)
                    throw new AnkiConnectException(FriendlyError(error.GetString() ?? "unknown error"));
                return doc.RootElement.TryGetProperty("result", out JsonElement result)
                    ? result.Clone()
                    : default;
            }
        }
    }

    private static string FriendlyError(string error)
    {
        if (error.Contains("api key", StringComparison.OrdinalIgnoreCase))
            return "AnkiConnect rejected the API key — it must match the add-on's \"apiKey\" setting (Tools ▸ Add-ons ▸ AnkiConnect ▸ Config).";
        if (error.Contains("collection is not available", StringComparison.OrdinalIgnoreCase))
            return "Anki has no collection open — open a profile in Anki first.";
        return error;
    }

    // --------------------------------------------------------- note content

    /// <summary>True when the note type's Text field is the cloze field — a
    /// cloze type without a ContextCloze field (Anki's built-in "Cloze").
    /// Otherwise Text holds the word and ContextCloze the cloze sentence
    /// (the Saladict layout, which the default note type uses).</summary>
    private static bool TextIsCloze(AnkiModelInfo model) => model.IsCloze && !model.Has("ContextCloze");

    /// <summary>Assemble the note's fields for the given note type: every
    /// field name the note type has gets its value; the rest are skipped.</summary>
    internal static Dictionary<string, string> BuildFields(AnkiCard card, AnkiModelInfo model)
    {
        string word = card.Text.Trim();
        string context = card.Context.Trim();
        string cloze = MakeCloze(context, word);
        // IPA rides inside the English section unless the type has its own field.
        string translation = BuildTranslationHtml(card, includePhonetic: !model.Has("Phonetic"));

        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Text"] = TextIsCloze(model) ? (cloze.Length > 0 ? cloze : "{{c1::" + H(word) + "}}") : H(word),
            ["Word"] = H(word),
            ["Phonetic"] = H(card.Phonetic),
            ["Translation"] = translation,
            ["Context"] = H(context),
            ["ContextCloze"] = cloze,
            ["Note"] = "",
            ["Title"] = H(card.Title),
            ["Url"] = H(card.Url),
            ["Favicon"] = "",
            ["Audio"] = "",   // AnkiConnect appends [sound:…] when a clip is attached
            ["Date"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            // Anki's built-in "Cloze" type: everything else goes to Back Extra.
            ["Back Extra"] = BuildBackExtraHtml(card, translation),
        };

        var fields = new Dictionary<string, string>();
        foreach (string name in model.Fields)
            if (all.TryGetValue(name, out string? value)) fields[name] = value;
        return fields;
    }

    /// <summary>Context with the first occurrence of the word turned into a
    /// {{c1::…}} cloze deletion — "" when the word is not in the context.</summary>
    internal static string MakeCloze(string context, string text)
    {
        if (context.Length == 0 || text.Length == 0) return "";
        int at = context.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return "";
        return H(context[..at]) + "{{c1::" + H(context.Substring(at, text.Length)) + "}}" + H(context[(at + text.Length)..]);
    }

    /// <summary>The three lookup sections in Saladict's own markup
    /// (.trans / .trans_title / .trans_content), so the note type's CSS
    /// styles them exactly like Saladict's cards.</summary>
    internal static string BuildTranslationHtml(AnkiCard card, bool includePhonetic)
    {
        var html = new StringBuilder("<div class=\"trans\">");
        string lang = card.TargetLanguage.Length > 0 ? $" ({H(card.TargetLanguage)})" : "";

        if (card.Translation.Trim().Length > 0)
            Section(html, "Bing Translator" + lang, H(card.Translation.Trim()));

        bool phonetic = includePhonetic && card.Phonetic.Length > 0;
        if (card.Senses.Count > 0 || phonetic)
        {
            var body = new StringBuilder();
            if (phonetic) body.Append($"<div><i>{H(card.Phonetic)}</i></div>");
            foreach (EnglishSense sense in card.Senses)
            {
                body.Append("<div>");
                if (sense.PartOfSpeech.Length > 0) body.Append($"<i>{H(sense.PartOfSpeech)}</i> ");
                body.Append(H(sense.Definition));
                if (sense.HasExample) body.Append($"<br><small><i>{H(sense.Example)}</i></small>");
                if (sense.HasSynonyms) body.Append($"<br><small>{H(sense.Synonyms)}</small>");
                body.Append("</div>");
            }
            Section(html, "English – English", body.ToString());
        }

        if (card.Entries.Count > 0)
        {
            var body = new StringBuilder();
            foreach (BingDictionaryEntry entry in card.Entries)
            {
                body.Append("<div>");
                if (entry.PartOfSpeech.Length > 0) body.Append($"<i>{H(entry.PartOfSpeech)}</i> ");
                body.Append($"<b>{H(entry.Term)}</b>");
                if (entry.HasBackTranslations) body.Append($" <small>{H(entry.BackTranslations)}</small>");
                body.Append("</div>");
            }
            Section(html, "Bing Dict" + lang, body.ToString());
        }

        html.Append("</div>");
        return html.ToString();
    }

    /// <summary>For note types with a single "extra" field (Anki's built-in
    /// Cloze): word, phonetic, the lookups and the source in one block.</summary>
    private static string BuildBackExtraHtml(AnkiCard card, string translationHtml)
    {
        var html = new StringBuilder();
        html.Append($"<div class=\"word\">{H(card.Text.Trim())}");
        if (card.Phonetic.Length > 0) html.Append($" <span class=\"phonetic\">{H(card.Phonetic)}</span>");
        html.Append("</div>");
        html.Append(translationHtml);
        if (card.Title.Length > 0)
            html.Append($"<div class=\"tsource\"><a href=\"{H(card.Url)}\">{H(card.Title)}</a></div>");
        return html.ToString();
    }

    private static void Section(StringBuilder html, string title, string contentHtml) =>
        html.Append($"<span class=\"trans_title\">{H(title)}</span><div class=\"trans_content\">{contentHtml}</div>");

    /// <summary>Minimal HTML escape (&amp; &lt; &gt;) — quotes stay readable
    /// in Anki's editor.</summary>
    private static string H(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Escape a value for Anki's search syntax inside "…".</summary>
    private static string EscapeSearch(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("*", "\\*").Replace("_", "\\_").Replace(":", "\\:");

    /// <summary>Media file name for the pronunciation clip — the word with
    /// anything unsafe for a file name replaced.</summary>
    private static string AudioFileName(string text)
    {
        var safe = new StringBuilder();
        foreach (char c in text.Trim().ToLowerInvariant())
            safe.Append(char.IsLetterOrDigit(c) ? c : '_');
        string name = safe.ToString().Trim('_');
        if (name.Length == 0) name = "word";
        if (name.Length > 60) name = name[..60];
        return $"esl-epub-reader-{name}.mp3";
    }

    // ------------------------------------------- the default cloze note type
    // A real cloze type (isCloze) with Saladict's fields, card template and
    // CSS, verbatim: the front shows the sentence with the word blanked (or
    // the word as a heading when there is no sentence) plus the
    // translations; the back reveals the answer and the note. The
    // {{tts …}} line reads the sentence with the AwesomeTTS add-on when it
    // is installed, exactly as Saladict's cards do.

    internal static readonly string[] ClozeModelFields =
        ["Date", "Text", "Translation", "Context", "ContextCloze", "Note", "Title", "Url", "Favicon", "Audio"];

    internal const string ClozeFront =
        """
        {{#ContextCloze}}
        <section>{{cloze:ContextCloze}}</section>
        <section>{{type:cloze:ContextCloze}}</section>
        {{#Translation}}
        <section>{{Translation}}</section>
        {{/Translation}}
        {{/ContextCloze}}

        {{^ContextCloze}}
        <h1>{{Text}}</h1>
        {{#Translation}}
        <section>{{Translation}}</section>
        {{/Translation}}
        {{/ContextCloze}}

        {{#Note}}
        <section>{{hint:Note}}</section>
        {{/Note}}

        {{#Title}}
        <section class="tsource">
        <hr />
        {{#Favicon}}
        <span class="favicon" style="background-image:url({{Favicon}})"></span>
        {{/Favicon}}
        <a href="{{Url}}">{{Title}}</a>
        </section>
        {{/Title}}

        {{tts en_US voices=AwesomeTTS:Context}}
        """;

    internal const string ClozeBack =
        """
        {{#ContextCloze}}
        <section>{{cloze:ContextCloze}}</section>
        <section>{{type:cloze:ContextCloze}}</section>
        {{#Translation}}
        <section>{{Translation}}</section>
        {{/Translation}}
        {{/ContextCloze}}

        {{^ContextCloze}}
        <h1>{{Text}}</h1>
        {{#Translation}}
        <section>{{Translation}}</section>
        {{/Translation}}
        {{/ContextCloze}}

        {{#Note}}
        <section>{{Note}}</section>
        {{/Note}}

        {{#Title}}
        <section class="tsource">
        <hr />
        {{#Favicon}}
        <span class="favicon" style="background-image:url({{Favicon}})"></span>
        {{/Favicon}}
        <a href="{{Url}}">{{Title}}</a>
        </section>
        {{/Title}}
        """;

    /// <summary>Saladict's card CSS (plus a word/phonetic line for the
    /// Back Extra of Anki's built-in Cloze type).</summary>
    internal const string ModelCss =
        """
        .card {
          font-family: arial;
          font-size: 20px;
          text-align: center;
          color: #333;
          background-color: white;
        }

        a {
          color: #5caf9e;
        }

        input {
          border: 1px solid #eee;
        }

        section {
          margin: 1em 0;
        }

        .word {
          font-size: 1.2em;
          font-weight: bold;
        }

        .phonetic {
          font-weight: normal;
          font-size: 0.8em;
          color: #888;
        }

        .trans {
          border: 1px solid #eee;
          padding: 0.5em;
        }

        .trans_title {
          display: block;
          font-size: 0.9em;
          font-weight: bold;
        }

        .trans_content {
          margin-bottom: 0.5em;
        }

        .cloze {
          font-weight: bold;
          color: #f9690e;
        }

        .tsource {
          position: relative;
          font-size: .8em;
        }

        .tsource img {
          height: .7em;
        }

        .tsource a {
          text-decoration: none;
        }

        .typeGood {
          color: #fff;
          background: #1EBC61;
        }

        .typeBad {
          color: #fff;
          background: #F75C4C;
        }

        .typeMissed {
          color: #fff;
          background: #7C8A99;
        }

        .favicon {
          display: inline-block;
          width: 1em;
          height: 1em;
          background: center/cover no-repeat;
        }
        """;
}
