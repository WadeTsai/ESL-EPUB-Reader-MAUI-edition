# ESL EPUB Reader (MAUI)

The cross-platform port of [ESL EPUB Reader](https://github.com/WadeTsai/ESL-EPUB-Reader),
rebuilt on **.NET MAUI** (.NET 10 / C# 14) so one code base runs on
**Windows** (via WinUI 3) and **macOS** (via Mac Catalyst) — plus a
**Linux** front end (GTK 4 + WebKitGTK) that shares the same services.

An ePub reader for English learners: select any word, phrase, or sentence
while reading and instantly see an English–English dictionary entry, a
bilingual **Bing Dict** entry (part of speech + back-translations), and a
full **Bing Translator** rendering — into Traditional Chinese by default or
any of 130+ languages. Selections are read aloud by the system voice.
Day/night themes, single/dual-page layouts with strict page-pair
pagination, adjustable fonts/spacing/margins with a responsive text column,
and automatic resume are all carried over from the WinUI original.

## Download

Prebuilt macOS binaries (universal — Apple Silicon and Intel, macOS 15.0+) are
on the [releases page](https://github.com/WadeTsai/ESL-EPUB-Reader-MAUI-edition/releases/latest).
They are ad-hoc signed rather than notarized, so on first launch right-click the
app and choose **Open** — see [macOS](#macos-on-a-mac--required) below for details.

Windows builds are not yet published; build from source with the steps below.

## Building

### Windows (on Windows)

```bash
dotnet workload install maui-windows
dotnet publish src/EslEpubReader.Maui/EslEpubReader.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release
```

Output: `src/EslEpubReader.Maui/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/`
(run `EslEpubReader.Maui.exe`; the app is unpackaged — no installer).

### Linux

.NET MAUI has no Linux target, so Linux gets its own thin front end,
`src/EslEpubReader.Linux`: a plain `net10.0` app that renders with
**GTK 4 + WebKitGTK 6.0** through the [GirCore](https://gircore.github.io/)
bindings and compiles the MAUI project's `Services/` and `Models/` in
directly. No workloads are needed.

One-time setup (installs the native libraries with your package manager —
apt, dnf, pacman or zypper — and the .NET 10 SDK into `~/.dotnet` if you
don't have one; re-runnable):

```bash
scripts/linux/setup-dev.sh                        # needs sudo for the system packages
scripts/linux/setup-dev.sh --no-system-packages   # libraries already installed / no sudo
```

The native runtime dependencies it installs are GTK 4 (`libgtk-4-1`),
WebKitGTK 6.0 (`libwebkitgtk-6.0-4`), speech-dispatcher with the espeak-ng
module (read-aloud), and the base/good GStreamer plugins.

Run from source (any arguments go to the app):

```bash
scripts/linux/run.sh [book.epub]
```

Install for the current user — a self-contained Release build in
`~/.local/lib/eslepubreader`, an `eslepubreader` launcher in `~/.local/bin`,
an app-menu entry that also offers "Open With" for `.epub` files, and the
icon (`--uninstall` removes it all again):

```bash
scripts/linux/install.sh
```

Or publish by hand:

```bash
dotnet publish src/EslEpubReader.Linux -c Release
```

Output: `src/EslEpubReader.Linux/bin/Release/net10.0/linux-x64/publish/eslepubreader`.
Settings live in `~/.config/EslEpubReader/settings.json` (same format as the
other builds). Debug builds enable WebKit's inspector (right-click → Inspect
Element) for working on the injected page script.

Linux-specific differences: the selection bridge uses a real WebKit
script-message handler (lookups fire instantly instead of on a 700 ms poll),
the side panels have draggable splitters again (`GtkPaned`), and read-aloud
goes through `spd-say` (falling back to `espeak-ng`), and Bing requests are
sent from a hidden Bing Translator page (Bing now rejects them from plain
HTTP clients). Bing Dict has no Traditional Chinese data; Windows converts
its Simplified results with the OS converter, while Linux and macOS convert
them with one extra Bing Translator call (zh-Hans → zh-Hant).

### macOS (on a Mac — required)

Mac Catalyst apps can only be linked and bundled by Apple's toolchain,
which exists only on macOS.

Prerequisites:

- **.NET 10 SDK.**
- **Full Xcode** — the Command Line Tools alone are *not* enough. The build
  needs the Mac Catalyst SDKs and `actool` (which generates the app icon and
  splash from the SVGs), and both ship only inside `Xcode.app`. The current
  `Microsoft.MacCatalyst.Sdk.net10.0_26.5` pack expects Xcode 26.6.
- **`xcode-select` pointing at Xcode**, not at the Command Line Tools.
  Check it with `xcode-select -p`; if it prints
  `/Library/Developer/CommandLineTools`, fix it with:

  ```bash
  sudo xcode-select -s /Applications/Xcode.app/Contents/Developer
  ```

Then:

```bash
dotnet workload install maui
dotnet publish src/EslEpubReader.Maui/EslEpubReader.Maui.csproj -f net10.0-maccatalyst -c Release
```

Output, under `src/EslEpubReader.Maui/bin/Release/net10.0-maccatalyst/`:

- `ESL EPUB Reader.app` — universal (`x86_64` + `arm64`) app bundle
- `publish/EslEpubReader.Maui-<version>.pkg` — installer
- `maccatalyst-arm64/`, `maccatalyst-x64/` — the per-architecture builds

The result is **ad-hoc signed** (no Apple Developer ID, not notarized), so
Gatekeeper blocks it on any Mac other than the one that built it. To open it
elsewhere, right-click the app and choose **Open**, or strip the quarantine
flag:

```bash
xattr -dr com.apple.quarantine "/Applications/ESL EPUB Reader.app"
```

#### Building without changing `xcode-select`

If you can't or don't want to run `sudo xcode-select -s` (for example on a
shared or managed Mac), pass Xcode's location to the build instead:

```bash
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer \
dotnet publish src/EslEpubReader.Maui/EslEpubReader.Maui.csproj \
  -f net10.0-maccatalyst -c Release \
  -p:MD_APPLE_SDK_ROOT=/Applications/Xcode.app
```

Without one of these, the build fails with
`A valid Xcode installation was not found at the configured location`.

Note that `xcode-select -p` honors the `DEVELOPER_DIR` environment variable,
so it reports the override rather than the real system setting when that
variable is exported. Use `env -u DEVELOPER_DIR xcode-select -p` to read the
actual value.

## Port notes (vs. the WinUI original)

- The `Services/` and `Models/` layers are the same plain-.NET code, with
  two portability touches: settings persist via MAUI's `AppDataDirectory`,
  and the Windows-only Simplified→Traditional character converter
  (`LCMapStringEx`) is replaced by a Bing Translator zh-Hans → zh-Hant call
  on macOS and Linux.
- WebView2-specific APIs were replaced by portable equivalents: chapters
  load as `file://` URLs (no virtual host), scripts inject after load
  (`EvaluateJavaScriptAsync`), and the page↔native bridge polls a JS
  variable instead of `postMessage`.
- Text-to-speech uses MAUI's `TextToSpeech` (system voices on both OSes)
  instead of `Windows.Media.SpeechSynthesis`.
- The page-side logic — injected selection/pagination script, reader
  stylesheet, option lists, selection normalization — lives in
  `Services/ReaderPage.cs`, shared by the MAUI and Linux front ends.
- Not carried over: draggable pane splitters (MAUI has no GridSplitter —
  panels toggle instead; the Linux build has them), Mica/custom title bar, and `.epub` file
  association.
