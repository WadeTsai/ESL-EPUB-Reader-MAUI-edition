// ============================================================================
// SpeechService.cs — read-aloud on Linux.
//
// Selections are read by Google Translate's voice: the shared
// Services/GoogleTtsService fetches the clip as MP3 and a command-line
// player (whichever is installed) plays it:
//
//   1. mpv, else
//   2. ffplay (FFmpeg), else
//   3. gst-play-1.0 / gst-launch-1.0 playbin (GStreamer — the plugins the
//      setup script installs for WebKit media already decode MP3).
//
// When Google cannot be reached (offline) — or no player is installed —
// speech falls back to the desktop's own stack, as before:
//
//   1. spd-say  (speech-dispatcher — the standard Linux TTS service; the
//                same one screen readers use), else
//   2. espeak-ng (direct synthesis, when speech-dispatcher is absent).
//
// Each new utterance cancels the previous one so rapid selections never
// overlap audibly — the same behavior as the Windows/Mac builds.
// ============================================================================

using System.Diagnostics;
using EslEpubReader.Services;

namespace EslEpubReader;

public sealed class SpeechService
{
    private readonly GoogleTtsService _google = new();

    private readonly string? _mpv = FindOnPath("mpv");
    private readonly string? _ffplay = FindOnPath("ffplay");
    private readonly string? _gstPlay = FindOnPath("gst-play-1.0");
    private readonly string? _gstLaunch = FindOnPath("gst-launch-1.0");

    private readonly string? _spdSay = FindOnPath("spd-say");
    private readonly string? _espeak = FindOnPath("espeak-ng") ?? FindOnPath("espeak");

    private Process? _current;

    /// <summary>True when an MP3 player is installed (Google voice usable).</summary>
    public bool HasAudioPlayer =>
        _mpv is not null || _ffplay is not null || _gstPlay is not null || _gstLaunch is not null;

    /// <summary>True when a local speech engine is installed (the fallback).</summary>
    public bool HasLocalVoice => _spdSay is not null || _espeak is not null;

    /// <summary>True when some way of speaking exists.</summary>
    public bool IsAvailable => HasAudioPlayer || HasLocalVoice;

    /// <summary>The Google Translate pronunciation clip (MP3) for
    /// <paramref name="text"/> — what the Anki card attaches as audio.
    /// Throws when Google cannot be reached.</summary>
    public Task<byte[]> FetchClipAsync(string text, CancellationToken ct) => _google.SynthesizeAsync(text, ct);

    /// <summary>Speak English text, replacing anything still being spoken.
    /// Completes when the utterance ends (or is cancelled).</summary>
    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        Stop();
        if (!IsAvailable)
            throw new InvalidOperationException(
                "no way to play speech — install mpv (or ffmpeg / gstreamer tools), or speech-dispatcher / espeak-ng");

        if (HasAudioPlayer)
        {
            try
            {
                byte[] mp3 = await _google.SynthesizeAsync(text, ct);
                await PlayMp3Async(mp3, ct);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested && HasLocalVoice)
            {
                // Google Translate unreachable — the local voice still works.
            }
        }

        if (!HasLocalVoice)
            throw new InvalidOperationException(
                "Google Translate is unreachable and no local voice is installed (speech-dispatcher / espeak-ng)");

        // "--" ends option parsing, so text can never be read as a flag.
        ProcessStartInfo psi = _spdSay is not null
            ? new ProcessStartInfo(_spdSay) { ArgumentList = { "--wait", "-l", "en", "--", text } }
            : new ProcessStartInfo(_espeak!) { ArgumentList = { "-v", "en-us", "--", text } };
        await RunAsync(psi, ct);
    }

    /// <summary>Write the clip to a temp file and hand it to the first
    /// available player; the file is removed when playback ends.</summary>
    private async Task PlayMp3Async(byte[] mp3, CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"eslepubreader-tts-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, mp3, ct);
        try
        {
            ProcessStartInfo psi =
                _mpv is not null ? new ProcessStartInfo(_mpv) { ArgumentList = { "--no-video", "--no-terminal", "--really-quiet", "--", path } }
                : _ffplay is not null ? new ProcessStartInfo(_ffplay) { ArgumentList = { "-nodisp", "-autoexit", "-loglevel", "quiet", path } }
                : _gstPlay is not null ? new ProcessStartInfo(_gstPlay) { ArgumentList = { "--quiet", path } }
                : new ProcessStartInfo(_gstLaunch!) { ArgumentList = { "-q", "playbin", $"uri={new Uri(path).AbsoluteUri}" } };
            await RunAsync(psi, ct);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp file — best effort */ }
        }
    }

    /// <summary>Run a speech/player process as the current utterance and
    /// wait for it; cancellation kills it.</summary>
    private async Task RunAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;

        Process process = Process.Start(psi)!;
        _current = process;
        using (ct.Register(Stop))
            await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(psi.FileName)} exited with code {process.ExitCode}");
    }

    /// <summary>Silence the current utterance, if any.</summary>
    public void Stop()
    {
        Process? process = Interlocked.Exchange(ref _current, null);
        if (process is null) return;
        try
        {
            bool wasSpdSay = process.StartInfo.FileName == _spdSay;
            if (!process.HasExited) process.Kill();
            // Killing the spd-say CLIENT does not stop the speech-dispatcher
            // SERVER mid-sentence; ask it to cancel the queued speech too.
            if (wasSpdSay)
                Process.Start(new ProcessStartInfo(_spdSay!, "--cancel") { UseShellExecute = false })?.WaitForExit(1000);
        }
        catch { /* already gone */ }
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exe))
            .FirstOrDefault(File.Exists);
}
