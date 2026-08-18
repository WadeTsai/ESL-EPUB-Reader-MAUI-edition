# ESL EPUB Reader (MAUI)

The cross-platform port of [ESL EPUB Reader](https://github.com/WadeTsai/ESL-EPUB-Reader),
rebuilt on **.NET MAUI** (.NET 10 / C# 14) so one code base runs on
**Windows** (via WinUI 3) and **macOS** (via Mac Catalyst).

An ePub reader for English learners: select any word, phrase, or sentence
while reading and instantly see an English–English dictionary entry, a
bilingual **Bing Dict** entry (part of speech + back-translations), and a
full **Bing Translator** rendering — into Traditional Chinese by default or
any of 130+ languages. Selections are read aloud by the system voice.
Day/night themes, single/dual-page layouts with strict page-pair
pagination, adjustable fonts/spacing/margins with a responsive text column,
and automatic resume are all carried over from the WinUI original.

## Building

### Windows (on Windows)

```bash
dotnet workload install maui-windows
dotnet publish src/EslEpubReader.Maui/EslEpubReader.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release
```

Output: `src/EslEpubReader.Maui/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`
(run `EslEpubReader.Maui.exe`; the app is unpackaged — no installer).

### macOS (on a Mac — required)

Mac Catalyst apps can only be linked and bundled by Apple's toolchain,
which exists only on macOS. On any Mac with .NET 10 and Xcode installed:

```bash
dotnet workload install maui
dotnet publish src/EslEpubReader.Maui/EslEpubReader.Maui.csproj -f net10.0-maccatalyst -c Release
```

Output: the `.app` bundle under
`src/EslEpubReader.Maui/bin/Release/net10.0-maccatalyst/`.

## Port notes (vs. the WinUI original)

- The `Services/` and `Models/` layers are the same plain-.NET code, with
  two portability touches: settings persist via MAUI's `AppDataDirectory`,
  and the Windows-only Simplified→Traditional character converter
  (`LCMapStringEx`) falls back to Simplified output on macOS.
- WebView2-specific APIs were replaced by portable equivalents: chapters
  load as `file://` URLs (no virtual host), scripts inject after load
  (`EvaluateJavaScriptAsync`), and the page↔native bridge polls a JS
  variable instead of `postMessage`.
- Text-to-speech uses MAUI's `TextToSpeech` (system voices on both OSes)
  instead of `Windows.Media.SpeechSynthesis`.
- Not carried over: draggable pane splitters (MAUI has no GridSplitter —
  panels toggle instead), Mica/custom title bar, and `.epub` file
  association.
