// ============================================================================
// Mp3Player.cs — plays an in-memory MP3 clip (the Google Translate speech
// fetched by Services/GoogleTtsService) through the OS media stack.
//
// MAUI has no built-in audio player, and the toolkit MediaElement is a
// heavy dependency for "play one clip", so this is a thin per-platform
// wrapper over what each OS already ships:
//
//   * Windows      — Windows.Media.Playback.MediaPlayer, the same player the
//                    WinUI original used for its synthesized speech stream.
//   * macOS        — AVFoundation's AVAudioPlayer, fed the bytes directly.
//   * net10.0      — (the Linux compile check of the MAUI project) no audio;
//                    PlayAsync throws so the caller falls back to the system
//                    voice. The real Linux app plays through SpeechService.
//
// PlayAsync completes when the clip ends, throws OperationCanceledException
// when cancelled, and Stop() cuts playback immediately — the same contract
// as TextToSpeech.SpeakAsync, so MainPage can treat both alike.
// ============================================================================

#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.Playback;
#elif MACCATALYST
using AVFoundation;
using Foundation;
#endif

namespace EslEpubReader;

public sealed class Mp3Player : IDisposable
{
    /// <summary>Completion of the clip currently playing (null when idle).</summary>
    private TaskCompletionSource? _done;

#if WINDOWS
    private readonly MediaPlayer _player = new() { AutoPlay = false };

    public Mp3Player()
    {
        _player.MediaEnded += (_, _) => _done?.TrySetResult();
        _player.MediaFailed += (_, e) =>
            _done?.TrySetException(new InvalidOperationException(e.ErrorMessage));
    }

    public async Task PlayAsync(byte[] mp3, CancellationToken ct)
    {
        Stop();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _done = done;

        // Assigning a new Source replaces (and stops) whatever was loaded;
        // the previous MediaSource is released explicitly.
        MediaSource? previous = _player.Source as MediaSource;
        _player.Source = MediaSource.CreateFromStream(
            new MemoryStream(mp3, writable: false).AsRandomAccessStream(), "audio/mpeg");
        previous?.Dispose();
        _player.Play();

        using (ct.Register(() => done.TrySetCanceled(ct)))
            await done.Task;
    }

    public void Stop()
    {
        _done?.TrySetCanceled();
        _done = null;
        _player.Pause();
    }

    public void Dispose()
    {
        Stop();
        (_player.Source as MediaSource)?.Dispose();
        _player.Dispose();
    }

#elif MACCATALYST
    private AVAudioPlayer? _player;

    public async Task PlayAsync(byte[] mp3, CancellationToken ct)
    {
        Stop();
        AVAudioPlayer? player = AVAudioPlayer.FromData(NSData.FromArray(mp3), out NSError? error);
        if (player is null)
            throw new InvalidOperationException(error?.LocalizedDescription ?? "could not decode the audio clip");

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FinishedPlaying += (_, _) => done.TrySetResult();
        player.DecoderError += (_, e) =>
            done.TrySetException(new InvalidOperationException(e.Error?.LocalizedDescription ?? "audio decode error"));

        _player = player;
        _done = done;
        player.PrepareToPlay();
        if (!player.Play())
            throw new InvalidOperationException("audio playback could not start");

        using (ct.Register(() => done.TrySetCanceled(ct)))
            await done.Task;
    }

    public void Stop()
    {
        _done?.TrySetCanceled();
        _done = null;
        AVAudioPlayer? player = Interlocked.Exchange(ref _player, null);
        if (player is null) return;
        player.Stop();
        player.Dispose();
    }

    public void Dispose() => Stop();

#else
    public Task PlayAsync(byte[] mp3, CancellationToken ct) =>
        throw new PlatformNotSupportedException("no audio playback on this target");

    public void Stop() { }

    public void Dispose() { }
#endif
}
