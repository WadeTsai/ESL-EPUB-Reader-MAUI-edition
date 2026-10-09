// ============================================================================
// Services/GoogleTtsService.cs
// ============================================================================
// Read-aloud audio from Google Translate's text-to-speech endpoint — the
// same natural-sounding voice the "listen" button on translate.google.com
// plays:
//
//     https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob
//                                   &tl=en&q={text}&total={n}&idx={i}&textlen={len}
//
// Why this source?
//   * One consistent, clear English voice on every platform — the OS voices
//     differ wildly (and some machines have no English voice at all).
//   * No API key or account; it answers a plain GET with an MP3 body.
//
// This class only FETCHES the audio (MP3 bytes). Playing it is the front
// end's job — the MAUI head decodes through the OS media stack (Mp3Player),
// the Linux head through a command-line player (SpeechService) — and both
// keep their local system voice as the fallback when this service cannot
// be reached (offline, blocked network).
//
// CHUNKING:
//   The endpoint caps one request at ~200 characters and refuses longer
//   text, so sentences are split at punctuation/word boundaries into
//   chunks of at most MaxChunkChars, fetched in parallel, and the MP3
//   bodies are concatenated — MPEG audio frames are self-contained, so a
//   straight byte concatenation plays as one continuous clip.
//
// RESILIENCE:
//   * 10-second timeout so a dead network never hangs the UI.
//   * Small in-memory cache: the Speak button re-reads the last selection
//     and readers re-select the same word — the second play is instant
//     and the endpoint is not asked again.
// ============================================================================

using System.Net.Http;

namespace EslEpubReader.Services;

public sealed class GoogleTtsService
{
    private const string Endpoint = "https://translate.google.com/translate_tts";

    /// <summary>Longest text the endpoint accepts in one request.</summary>
    private const int MaxChunkChars = 200;

    /// <summary>Cache cap (entries); each clip is a few tens of KB.</summary>
    private const int MaxCacheEntries = 200;

    /// <summary>One shared HttpClient for the app lifetime (see the note in
    /// EnglishDictionaryService on per-request clients).</summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // The endpoint serves browsers; a browser-like User-Agent keeps it
        // answering if it ever starts filtering generic clients.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Referrer = new Uri("https://translate.google.com/");
        return client;
    }

    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly Lock _cacheLock = new();

    /// <summary>Fetch the English pronunciation of <paramref name="text"/>
    /// as MP3 bytes. Throws on network/endpoint failure so the caller can
    /// fall back to a local voice; respects cancellation.</summary>
    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0) return [];

        lock (_cacheLock)
            if (_cache.TryGetValue(text, out byte[]? cached)) return cached;

        List<string> chunks = SplitIntoChunks(text, MaxChunkChars);
        byte[][] parts = await Task.WhenAll(
            chunks.Select((chunk, i) => FetchChunkAsync(chunk, i, chunks.Count, ct)));

        byte[] mp3 = parts.Length == 1 ? parts[0] : Concat(parts);

        lock (_cacheLock)
        {
            if (_cache.Count >= MaxCacheEntries) _cache.Clear();   // crude, but ample for a session
            _cache[text] = mp3;
        }
        return mp3;
    }

    private static async Task<byte[]> FetchChunkAsync(string chunk, int index, int total, CancellationToken ct)
    {
        string url = $"{Endpoint}?ie=UTF-8&client=tw-ob&tl=en&q={Uri.EscapeDataString(chunk)}" +
                     $"&total={total}&idx={index}&textlen={chunk.Length}";

        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Anything but audio (an HTML "unusual traffic" page, say) is a
        // failure — let the caller fall back rather than feed it to a decoder.
        string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Google Translate answered {mediaType} instead of audio");

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
        if (body.Length == 0)
            throw new InvalidOperationException("Google Translate returned an empty clip");
        return body;
    }

    /// <summary>Split text into pieces of at most <paramref name="max"/>
    /// characters, preferring to break after sentence punctuation, then at
    /// any whitespace, and only as a last resort mid-word. Pieces are
    /// trimmed; empty pieces are dropped.</summary>
    internal static List<string> SplitIntoChunks(string text, int max)
    {
        var chunks = new List<string>();
        ReadOnlySpan<char> rest = text.AsSpan().Trim();

        while (rest.Length > 0)
        {
            if (rest.Length <= max)
            {
                chunks.Add(rest.ToString());
                break;
            }

            ReadOnlySpan<char> window = rest[..max];
            int cut = LastIndexOfAny(window, ".!?;:") + 1;      // after the punctuation
            if (cut <= 0) cut = LastIndexOfAny(window, " \t\r\n");
            if (cut <= 0) cut = max;                              // one huge "word"

            ReadOnlySpan<char> piece = rest[..cut].Trim();
            if (piece.Length > 0) chunks.Add(piece.ToString());
            rest = rest[cut..].TrimStart();
        }
        return chunks;
    }

    private static int LastIndexOfAny(ReadOnlySpan<char> span, string chars)
    {
        for (int i = span.Length - 1; i >= 0; i--)
            if (chars.Contains(span[i])) return i;
        return -1;
    }

    private static byte[] Concat(byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] p in parts)
        {
            Buffer.BlockCopy(p, 0, all, offset, p.Length);
            offset += p.Length;
        }
        return all;
    }
}
