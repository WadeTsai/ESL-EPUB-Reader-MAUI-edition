// ============================================================================
// Services/LanguageCatalog.cs
// ============================================================================
// The list of TARGET LANGUAGES the reader can translate into.
//
// Both Bing-backed services (BingTranslateService for whole selections,
// BingDictionaryService for word lookups) accept Microsoft Translator
// language codes, so this catalog mirrors Microsoft's public language list.
// The reader picks one in the dictionary panel; the choice is persisted in
// settings.json (default: Traditional Chinese, the app's original audience).
//
// CODE QUIRKS worth knowing (Microsoft Translator conventions):
//   * Chinese needs a SCRIPT tag: "zh-Hant" (Traditional) / "zh-Hans"
//     (Simplified) — different from Google's "zh-TW"/"zh-CN". A settings
//     file persisted by an older app version may still say "zh-TW"; unknown
//     codes fall back to the default (zh-Hant) in FromCode, which maps that
//     legacy value to the right place automatically.
//   * Hebrew is "he" (Google used the legacy "iw").
//   * Norwegian is Bokmål, "nb". Portuguese "pt" = Brazilian; "pt-PT" =
//     European. Serbian/Mongolian/Inuktitut carry script suffixes.
//
// COVERAGE NOTE: the TRANSLATOR supports every language below; the
// DICTIONARY (tlookupv3) only covers ~50 of them — for the rest the panel
// shows "Bing Dict does not cover this language" and the translation
// section still works.
// ============================================================================

namespace EslEpubReader.Services;

/// <summary>
/// One selectable target language. A C# record: value-equality and a
/// compact declaration for what is effectively a data row.
/// </summary>
/// <param name="Code">The Microsoft Translator code ("zh-Hant").</param>
/// <param name="EnglishName">English display name ("Chinese, Traditional").</param>
/// <param name="NativeName">Native display name ("繁體中文"), or null when it
/// adds nothing over the English name.</param>
public sealed record TranslationLanguage(string Code, string EnglishName, string? NativeName = null)
{
    /// <summary>Full name for the ComboBox list: "繁體中文 (Chinese, Traditional)".</summary>
    public string DisplayName => NativeName is null ? EnglishName : $"{NativeName} ({EnglishName})";

    /// <summary>Compact name for section headers: prefers the native form.</summary>
    public string ShortName => NativeName ?? EnglishName;

    /// <summary>ComboBox fallback display (when no DisplayMemberPath is set).</summary>
    public override string ToString() => DisplayName;
}

/// <summary>Static catalog of every Microsoft-Translator-supported language.</summary>
public static class LanguageCatalog
{
    /// <summary>The app's default target language: Traditional Chinese.</summary>
    public const string DefaultCode = "zh-Hant";

    /// <summary>
    /// All supported target languages, sorted by English name so the
    /// ComboBox reads like an index. (Typing a letter while the ComboBox is
    /// open jumps to it — WinUI gives us that for free.)
    /// </summary>
    public static readonly IReadOnlyList<TranslationLanguage> All =
    [
        new("af", "Afrikaans"),
        new("sq", "Albanian", "Shqip"),
        new("am", "Amharic", "አማርኛ"),
        new("ar", "Arabic", "العربية"),
        new("hy", "Armenian", "Հայերեն"),
        new("as", "Assamese", "অসমীয়া"),
        new("az", "Azerbaijani", "Azərbaycan"),
        new("bn", "Bangla", "বাংলা"),
        new("ba", "Bashkir"),
        new("eu", "Basque", "Euskara"),
        new("bho", "Bhojpuri"),
        new("brx", "Bodo"),
        new("bs", "Bosnian", "Bosanski"),
        new("bg", "Bulgarian", "Български"),
        new("yue", "Cantonese, Traditional", "粵語"),
        new("ca", "Catalan", "Català"),
        new("hne", "Chhattisgarhi"),
        new("lzh", "Chinese, Literary", "文言文"),
        new("zh-Hans", "Chinese, Simplified", "简体中文"),
        new("zh-Hant", "Chinese, Traditional", "繁體中文"),
        new("hr", "Croatian", "Hrvatski"),
        new("cs", "Czech", "Čeština"),
        new("da", "Danish", "Dansk"),
        new("prs", "Dari", "دری"),
        new("dv", "Divehi", "ދިވެހި"),
        new("doi", "Dogri"),
        new("dsb", "Lower Sorbian"),
        new("nl", "Dutch", "Nederlands"),
        new("en", "English"),
        new("et", "Estonian", "Eesti"),
        new("fo", "Faroese", "Føroyskt"),
        new("fj", "Fijian"),
        new("fil", "Filipino", "Tagalog"),
        new("fi", "Finnish", "Suomi"),
        new("fr", "French", "Français"),
        new("fr-CA", "French, Canada", "Français canadien"),
        new("gl", "Galician", "Galego"),
        new("lug", "Ganda"),
        new("ka", "Georgian", "ქართული"),
        new("de", "German", "Deutsch"),
        new("el", "Greek", "Ελληνικά"),
        new("gu", "Gujarati", "ગુજરાતી"),
        new("ht", "Haitian Creole", "Kreyòl Ayisyen"),
        new("ha", "Hausa"),
        new("he", "Hebrew", "עברית"),
        new("hi", "Hindi", "हिन्दी"),
        new("mww", "Hmong Daw"),
        new("hu", "Hungarian", "Magyar"),
        new("is", "Icelandic", "Íslenska"),
        new("ig", "Igbo"),
        new("id", "Indonesian", "Bahasa Indonesia"),
        new("ikt", "Inuinnaqtun"),
        new("iu", "Inuktitut", "ᐃᓄᒃᑎᑐᑦ"),
        new("iu-Latn", "Inuktitut, Latin"),
        new("ga", "Irish", "Gaeilge"),
        new("it", "Italian", "Italiano"),
        new("ja", "Japanese", "日本語"),
        new("kn", "Kannada", "ಕನ್ನಡ"),
        new("ks", "Kashmiri", "کٲشُر"),
        new("kk", "Kazakh", "Қазақ"),
        new("km", "Khmer", "ខ្មែរ"),
        new("rw", "Kinyarwanda"),
        new("tlh-Latn", "Klingon", "tlhIngan Hol"),
        new("gom", "Konkani", "कोंकणी"),
        new("ko", "Korean", "한국어"),
        new("ku", "Kurdish, Central", "کوردیی ناوەندی"),
        new("kmr", "Kurdish, Northern", "Kurdî"),
        new("ky", "Kyrgyz", "Кыргызча"),
        new("lo", "Lao", "ລາວ"),
        new("lv", "Latvian", "Latviešu"),
        new("ln", "Lingala"),
        new("lt", "Lithuanian", "Lietuvių"),
        new("mk", "Macedonian", "Македонски"),
        new("mai", "Maithili", "मैथिली"),
        new("mg", "Malagasy"),
        new("ms", "Malay", "Bahasa Melayu"),
        new("ml", "Malayalam", "മലയാളം"),
        new("mt", "Maltese", "Malti"),
        new("mni", "Manipuri", "মৈতৈলোন্"),
        new("mi", "Maori", "Te Reo Māori"),
        new("mr", "Marathi", "मराठी"),
        new("mn-Cyrl", "Mongolian, Cyrillic", "Монгол"),
        new("mn-Mong", "Mongolian, Traditional", "ᠮᠣᠩᠭᠣᠯ"),
        new("my", "Myanmar (Burmese)", "မြန်မာ"),
        new("ne", "Nepali", "नेपाली"),
        new("nb", "Norwegian Bokmål", "Norsk"),
        new("nya", "Nyanja (Chichewa)"),
        new("or", "Odia", "ଓଡ଼ିଆ"),
        new("ps", "Pashto", "پښتو"),
        new("fa", "Persian", "فارسی"),
        new("pl", "Polish", "Polski"),
        new("pt", "Portuguese, Brazil", "Português (Brasil)"),
        new("pt-PT", "Portuguese, Portugal", "Português (Portugal)"),
        new("pa", "Punjabi", "ਪੰਜਾਬੀ"),
        new("otq", "Querétaro Otomi"),
        new("ro", "Romanian", "Română"),
        new("run", "Rundi"),
        new("ru", "Russian", "Русский"),
        new("sm", "Samoan", "Gagana Samoa"),
        new("sr-Cyrl", "Serbian, Cyrillic", "Српски"),
        new("sr-Latn", "Serbian, Latin", "Srpski"),
        new("nso", "Sesotho sa Leboa"),
        new("st", "Sesotho"),
        new("sn", "Shona"),
        new("sd", "Sindhi", "سنڌي"),
        new("si", "Sinhala", "සිංහල"),
        new("sk", "Slovak", "Slovenčina"),
        new("sl", "Slovenian", "Slovenščina"),
        new("so", "Somali", "Soomaali"),
        new("es", "Spanish", "Español"),
        new("sw", "Swahili", "Kiswahili"),
        new("sv", "Swedish", "Svenska"),
        new("ty", "Tahitian", "Reo Tahiti"),
        new("ta", "Tamil", "தமிழ்"),
        new("tt", "Tatar", "Татарча"),
        new("te", "Telugu", "తెలుగు"),
        new("th", "Thai", "ไทย"),
        new("bo", "Tibetan", "བོད་སྐད་"),
        new("ti", "Tigrinya", "ትግርኛ"),
        new("to", "Tongan", "Lea Faka-Tonga"),
        new("tr", "Turkish", "Türkçe"),
        new("tk", "Turkmen", "Türkmen"),
        new("tn", "Setswana"),
        new("uk", "Ukrainian", "Українська"),
        new("hsb", "Upper Sorbian", "Hornjoserbšćina"),
        new("ur", "Urdu", "اردو"),
        new("ug", "Uyghur", "ئۇيغۇرچە"),
        new("uz", "Uzbek", "Oʻzbek"),
        new("vi", "Vietnamese", "Tiếng Việt"),
        new("cy", "Welsh", "Cymraeg"),
        new("xh", "Xhosa", "isiXhosa"),
        new("yo", "Yoruba", "Yorùbá"),
        new("yua", "Yucatec Maya"),
        new("zu", "Zulu", "isiZulu"),
    ];

    /// <summary>
    /// Resolve a persisted language code back to its catalog entry.
    /// Unknown codes — including Google-era values like "zh-TW" from a
    /// settings file written by an older version — fall back to the default
    /// (Traditional Chinese) instead of crashing.
    /// </summary>
    public static TranslationLanguage FromCode(string code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? All.First(l => l.Code == DefaultCode);
}
