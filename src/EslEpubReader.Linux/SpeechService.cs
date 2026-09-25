// ============================================================================
// SpeechService.cs — read-aloud on Linux.
//
// MAUI's TextToSpeech has no Linux implementation, so this speaks through
// the desktop's speech stack:
//
//   1. spd-say  (speech-dispatcher — the standard Linux TTS service; the
//                same one screen readers use), else
//   2. espeak-ng (direct synthesis, when speech-dispatcher is absent).
//
// Each new utterance cancels the previous one so rapid selections never
// overlap audibly — the same behavior as the Windows/Mac builds.
// ============================================================================

using System.Diagnostics;

namespace EslEpubReader;

public sealed class SpeechService
{
    private readonly string? _spdSay = FindOnPath("spd-say");
    private readonly string? _espeak = FindOnPath("espeak-ng") ?? FindOnPath("espeak");
    private Process? _current;

    /// <summary>True when some speech backend is installed.</summary>
    public bool IsAvailable => _spdSay is not null || _espeak is not null;

    /// <summary>Speak English text, replacing anything still being spoken.
    /// Completes when the utterance ends (or is cancelled).</summary>
    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        Stop();
        if (!IsAvailable)
            throw new InvalidOperationException("no speech engine found — install speech-dispatcher or espeak-ng");

        // "--" ends option parsing, so text can never be read as a flag.
        ProcessStartInfo psi = _spdSay is not null
            ? new ProcessStartInfo(_spdSay) { ArgumentList = { "--wait", "-l", "en", "--", text } }
            : new ProcessStartInfo(_espeak!) { ArgumentList = { "-v", "en-us", "--", text } };
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;

        Process process = Process.Start(psi)!;
        _current = process;
        using (ct.Register(Stop))
            await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Silence the current utterance, if any.</summary>
    public void Stop()
    {
        Process? process = Interlocked.Exchange(ref _current, null);
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill();
            // Killing the spd-say CLIENT does not stop the speech-dispatcher
            // SERVER mid-sentence; ask it to cancel the queued speech too.
            if (_spdSay is not null)
                Process.Start(new ProcessStartInfo(_spdSay, "--cancel") { UseShellExecute = false })?.WaitForExit(1000);
        }
        catch { /* already gone */ }
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exe))
            .FirstOrDefault(File.Exists);
}
