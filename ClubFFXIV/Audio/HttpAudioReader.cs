using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace ClubFFXIV.Audio;

/// <summary>
/// Cross-platform HTTP audio stream reader. Replaces NAudio's MediaFoundationReader,
/// which doesn't work on Wine (XIVLauncher Mac/Linux). Uses managed decoders:
///   - NLayer for MP3
///   - NVorbis for OGG/Vorbis
/// Format is detected from the response Content-Type header; falls back to MP3.
///
/// Streaming model: HTTP response stream is consumed lazily by the decoder during
/// Read(). Network hiccups will briefly block playback — for steady Icecast streams
/// this is fine. Reconnect on stream end is the caller's responsibility.
/// </summary>
public sealed class HttpAudioReader : ISampleProvider, IDisposable
{
    public WaveFormat WaveFormat { get; }

    private readonly HttpClient http;
    private readonly HttpResponseMessage response;
    private readonly Stream networkStream;
    private readonly ISampleProvider decoder;
    private readonly IDisposable decoderDisposable;
    private bool disposed;

    private HttpAudioReader(
        HttpClient http,
        HttpResponseMessage response,
        Stream networkStream,
        ISampleProvider decoder,
        IDisposable decoderDisposable)
    {
        this.http = http;
        this.response = response;
        this.networkStream = networkStream;
        this.decoder = decoder;
        this.decoderDisposable = decoderDisposable;
        WaveFormat = decoder.WaveFormat;
    }

    /// <summary>
    /// Open the URL, sniff the format, wire up the appropriate decoder.
    /// Throws on HTTP error or unsupported format. Caller should Dispose() when done.
    /// </summary>
    public static async Task<HttpAudioReader> CreateAsync(string url, CancellationToken ct = default)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // Don't accept ICY metadata interleaving — keeps the decoder happy.
        // (We don't display "Now Playing" yet; if we add that, switch to "1" and parse.)
        HttpResponseMessage? response = null;
        Stream? networkStream = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Icy-MetaData", "0");
            req.Headers.UserAgent.ParseAdd("ClubFFXIV/0.1");

            response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            // NLayer / NVorbis both call Stream.Position to track frame offsets.
            // HttpBaseStream throws NotSupportedException on Position. Wrap so it
            // works for forward-only consumption.
            networkStream = new PositionTrackingStream(
                await response.Content.ReadAsStreamAsync(ct));

            ISampleProvider decoder;
            IDisposable decoderDisposable;

            if (mediaType.Contains("ogg") || mediaType.Contains("vorbis"))
            {
                var vorbis = new NVorbis.VorbisReader(networkStream, closeOnDispose: false);
                decoder = new VorbisSampleProvider(vorbis);
                decoderDisposable = vorbis;
            }
            else
            {
                // MP3 explicitly, or unknown (Icecast often serves application/octet-stream).
                var mpeg = new NLayer.MpegFile(networkStream);
                decoder = new MpegSampleProvider(mpeg);
                decoderDisposable = mpeg;
            }

            return new HttpAudioReader(http, response, networkStream, decoder, decoderDisposable);
        }
        catch
        {
            networkStream?.Dispose();
            response?.Dispose();
            http.Dispose();
            throw;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (disposed) return 0;
        try
        {
            return decoder.Read(buffer, offset, count);
        }
        catch (Exception ex)
        {
            // Network or decoder error — signal end-of-stream so playback stops cleanly.
            Plugin.Log.Warning($"Audio decode error: {ex.Message}");
            return 0;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { decoderDisposable.Dispose(); } catch { /* ignore */ }
        try { networkStream.Dispose(); } catch { /* ignore */ }
        try { response.Dispose(); } catch { /* ignore */ }
        try { http.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Wraps a forward-only stream (HTTP response) so that decoders which expect
    /// a seekable Stream can call Position. Pretends to be seekable; throws on
    /// any actual Seek call. Decoders that only read sequentially work fine.
    /// </summary>
    private sealed class PositionTrackingStream : Stream
    {
        private readonly Stream inner;
        private long position;

        public PositionTrackingStream(Stream inner) { this.inner = inner; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => long.MaxValue;
        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            position += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var n = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
            position += n;
            return n;
        }

        public override void Flush() { /* no-op */ }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class MpegSampleProvider : ISampleProvider
    {
        private readonly NLayer.MpegFile mpeg;
        public WaveFormat WaveFormat { get; }

        public MpegSampleProvider(NLayer.MpegFile mpeg)
        {
            this.mpeg = mpeg;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mpeg.SampleRate, mpeg.Channels);
        }

        public int Read(float[] buffer, int offset, int count) =>
            mpeg.ReadSamples(buffer, offset, count);
    }

    private sealed class VorbisSampleProvider : ISampleProvider
    {
        private readonly NVorbis.VorbisReader vorbis;
        public WaveFormat WaveFormat { get; }

        public VorbisSampleProvider(NVorbis.VorbisReader vorbis)
        {
            this.vorbis = vorbis;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(vorbis.SampleRate, vorbis.Channels);
        }

        public int Read(float[] buffer, int offset, int count) =>
            vorbis.ReadSamples(buffer, offset, count);
    }
}
