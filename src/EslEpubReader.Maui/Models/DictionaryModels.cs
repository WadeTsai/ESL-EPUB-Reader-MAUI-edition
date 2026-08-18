// ============================================================================
// Models/DictionaryModels.cs
// ============================================================================
// Data classes for dictionary lookup results.
//
// The app performs TWO lookups for every word/phrase the reader selects:
//
//   1. English → English      (definitions, part of speech, examples)
//        served online by the free https://dictionaryapi.dev API
//        -> modelled by EnglishSense / EnglishLookupResult
//
//   2. English → target language (default 繁體中文)
//        served online by Bing Dict (the bilingual-dictionary data behind
//        bing.com/translator's word cards, endpoint tlookupv3)
//        -> modelled by BingDictionaryEntry / ChineseLookupResult
//
// Both results are displayed side by side in the dictionary side panel.
// The XAML binds directly to the public properties of these classes, so all
// property names below are part of the UI contract.
// ============================================================================

namespace EslEpubReader.Models;

// ---------------------------------------------------------------------------
// English → English models
// ---------------------------------------------------------------------------

/// <summary>
/// One "sense" of an English word: a part of speech plus one definition,
/// optionally with an example sentence and synonyms.
/// dictionaryapi.dev returns meanings grouped by part of speech, each with
/// several definitions; the service flattens them into this display-friendly
/// shape so the UI can show a simple numbered list.
/// </summary>
public sealed class EnglishSense
{
    /// <summary>e.g. "noun", "verb", "adjective" — shown as a small badge.</summary>
    public required string PartOfSpeech { get; init; }

    /// <summary>The definition text itself, in (relatively) simple English.</summary>
    public required string Definition { get; init; }

    /// <summary>Optional example sentence — very valuable for ESL learners,
    /// because it shows the word used in context. May be empty.</summary>
    public string Example { get; init; } = "";

    /// <summary>Comma-joined synonyms ("big, large, huge"). May be empty.</summary>
    public string Synonyms { get; init; } = "";

    // --- Helper properties used by XAML visibility bindings. ---------------
    // x:Bind cannot call methods with complex logic inline, so we expose
    // booleans the UI can bind to in order to hide empty rows.

    /// <summary>True when there is an example sentence to display.</summary>
    public bool HasExample => Example.Length > 0;

    /// <summary>True when there is a synonyms line to display.</summary>
    public bool HasSynonyms => Synonyms.Length > 0;
}

/// <summary>The complete English→English answer for one lookup.</summary>
public sealed class EnglishLookupResult
{
    /// <summary>The exact word/phrase that was looked up.</summary>
    public required string Term { get; init; }

    /// <summary>IPA pronunciation like "/həˈloʊ/", or empty if unknown.
    /// Pronunciation help is especially useful for ESL readers.</summary>
    public string Phonetic { get; init; } = "";

    /// <summary>All senses found, in the order the dictionary lists them.</summary>
    public required IReadOnlyList<EnglishSense> Senses { get; init; }

    /// <summary>Human-readable status: "" on success, otherwise an
    /// explanation such as "No English definition found." — shown in the
    /// panel instead of an empty list.</summary>
    public string StatusMessage { get; init; } = "";
}

// ---------------------------------------------------------------------------
// English → target-language models (Bing Dict)
// ---------------------------------------------------------------------------

/// <summary>
/// One Bing Dict entry: a target-language term that translates the
/// looked-up English word, together with the part of speech it belongs to
/// and the English words it maps back to. Entries arrive already ranked by
/// Bing's confidence score (most likely translation first).
/// </summary>
public sealed class BingDictionaryEntry
{
    /// <summary>Part of speech of the SOURCE word this translation belongs
    /// to — "noun", "verb", … Shown as a small badge, exactly like the
    /// English–English section does, so the two sections read alike.</summary>
    public required string PartOfSpeech { get; init; }

    /// <summary>The translation in the target language (Traditional Chinese
    /// characters for the default zh-Hant — see BingDictionaryService for
    /// how Traditional script is produced). Displayed large as the primary
    /// content.</summary>
    public required string Term { get; init; }

    /// <summary>Comma-joined English back-translations of this Chinese term
    /// ("tradition, heritage, convention"). Lets the learner confirm the
    /// right SENSE was picked before trusting the translation. May be "".</summary>
    public required string BackTranslations { get; init; }

    /// <summary>True when there are back-translations to display — used by
    /// the XAML visibility binding to hide the empty row.</summary>
    public bool HasBackTranslations => BackTranslations.Length > 0;
}

/// <summary>The complete English→target-language answer for one lookup.
/// (Named for the app's original/default Chinese audience; it carries any
/// target language.)</summary>
public sealed class ChineseLookupResult
{
    /// <summary>The exact word/phrase that was looked up.</summary>
    public required string Term { get; init; }

    /// <summary>Matching entries, most confident translation first.</summary>
    public required IReadOnlyList<BingDictionaryEntry> Entries { get; init; }

    /// <summary>"" on success; otherwise e.g. "Bing Dict is unreachable."
    /// so the panel can tell the user what happened.</summary>
    public string StatusMessage { get; init; } = "";
}

// ---------------------------------------------------------------------------
// Machine translation model (Bing Translator — the 3rd lookup source)
// ---------------------------------------------------------------------------

/// <summary>
/// Result of translating the selection with Bing Translator. Unlike the
/// two dictionaries (which explain words/phrases), this is a single fluent
/// rendering of the WHOLE selection — including full sentences.
/// </summary>
public sealed class TranslationResult
{
    /// <summary>The exact text that was sent for translation.</summary>
    public required string Term { get; init; }

    /// <summary>The target-language translation ("" when unavailable).</summary>
    public string TranslatedText { get; init; } = "";

    /// <summary>"" on success; otherwise a human-readable explanation
    /// ("service unreachable", "no translation returned", …).</summary>
    public string StatusMessage { get; init; } = "";
}
