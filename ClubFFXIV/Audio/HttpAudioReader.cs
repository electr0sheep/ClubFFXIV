using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
        HttpResponseMessage? response = null;
        Stream? networkStream = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Ask the server to interleave Icecast/Shoutcast metadata blocks
            // ("StreamTitle='Artist - Song';...") into the audio stream every
            // icy-metaint bytes. We strip them out below before the decoder
            // sees them, and surface StreamTitle changes to TitleCache.
            req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            req.Headers.UserAgent.ParseAdd("ClubFFXIV/0.1");

            response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // Initial display title from response headers. icy-name is the
            // station's name (e.g. "SomaFM Groove Salad"); inline StreamTitle
            // updates will overwrite this as tracks change. Both headers are
            // optional — many static MP3 endpoints don't send either, in
            // which case the UI falls back to the URL.
            if (response.Headers.TryGetValues("icy-name", out var names))
            {
                var name = string.Join(" ", names).Trim();
                if (!string.IsNullOrEmpty(name))
                    TitleCache.Set(url, name);
            }

            // icy-metaint: bytes of audio between metadata blocks. Servers
            // that don't honor our Icy-MetaData: 1 request omit this header,
            // in which case the response is pure audio and we skip the
            // stripping wrapper.
            int metaInt = 0;
            if (response.Headers.TryGetValues("icy-metaint", out var miHeader)
                && int.TryParse(miHeader.FirstOrDefault(), out var mi))
            {
                metaInt = mi;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            Stream rawStream = await response.Content.ReadAsStreamAsync(ct);

            if (metaInt > 0)
            {
                rawStream = new IcyMetadataStream(rawStream, metaInt, url);
            }

            // NLayer / NVorbis both call Stream.Position to track frame offsets.
            // HttpBaseStream throws NotSupportedException on Position. Wrap so it
            // works for forward-only consumption.
            networkStream = new PositionTrackingStream(rawStream);

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
    /// Strips inline ICY/Shoutcast metadata from the HTTP audio stream so the
    /// downstream decoder only sees audio bytes. Wire format: every
    /// <c>metaint</c> bytes of audio, the server inserts a 1-byte length
    /// prefix (multiplied by 16) and that many bytes of ASCII metadata —
    /// typically <c>StreamTitle='Artist - Song';StreamUrl='...';</c> padded
    /// with NULs to a 16-byte multiple. We extract <c>StreamTitle</c> and
    /// push it into <see cref="TitleCache"/> keyed by the user's URL so the
    /// Now Playing header updates as tracks change.
    /// </summary>
    private sealed class IcyMetadataStream : Stream
    {
        private static readonly Regex StreamTitleRegex =
            new(@"StreamTitle='([^']*)'", RegexOptions.Compiled);

        private readonly Stream inner;
        private readonly int metaint;
        private readonly string url;
        private int audioBytesRemaining;
        private string? lastTitle;

        public IcyMetadataStream(Stream inner, int metaint, string url)
        {
            this.inner = inner;
            this.metaint = metaint;
            this.url = url;
            this.audioBytesRemaining = metaint;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (audioBytesRemaining == 0)
            {
                ConsumeMetadataBlock();
                audioBytesRemaining = metaint;
            }

            int toRead = Math.Min(count, audioBytesRemaining);
            int n = inner.Read(buffer, offset, toRead);
            audioBytesRemaining -= n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (audioBytesRemaining == 0)
            {
                await ConsumeMetadataBlockAsync(ct).ConfigureAwait(false);
                audioBytesRemaining = metaint;
            }

            int toRead = Math.Min(count, audioBytesRemaining);
            int n = await inner.ReadAsync(buffer.AsMemory(offset, toRead), ct).ConfigureAwait(false);
            audioBytesRemaining -= n;
            return n;
        }

        private void ConsumeMetadataBlock()
        {
            int lengthByte = inner.ReadByte();
            if (lengthByte <= 0) return;     // EOF or "no metadata this round"
            int metaLength = lengthByte * 16;

            var metaBuf = new byte[metaLength];
            int total = 0;
            while (total < metaLength)
            {
                int n = inner.Read(metaBuf, total, metaLength - total);
                if (n == 0) return;          // EOF mid-metadata; bail
                total += n;
            }
            ParseAndPublish(metaBuf, total);
        }

        private async Task ConsumeMetadataBlockAsync(CancellationToken ct)
        {
            var lengthBuf = new byte[1];
            int lr = await inner.ReadAsync(lengthBuf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (lr == 0 || lengthBuf[0] == 0) return;
            int metaLength = lengthBuf[0] * 16;

            var metaBuf = new byte[metaLength];
            int total = 0;
            while (total < metaLength)
            {
                int n = await inner.ReadAsync(metaBuf.AsMemory(total, metaLength - total), ct).ConfigureAwait(false);
                if (n == 0) return;
                total += n;
            }
            ParseAndPublish(metaBuf, total);
        }

        private void ParseAndPublish(byte[] buf, int length)
        {
            // Strip trailing NULs — servers pad metadata blocks to a 16-byte
            // multiple, even when the actual text is shorter.
            while (length > 0 && buf[length - 1] == 0) length--;
            if (length == 0) return;

            var meta = Encoding.UTF8.GetString(buf, 0, length);
            var match = StreamTitleRegex.Match(meta);
            if (!match.Success) return;

            var title = match.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(title)) return;
            // Don't republish when nothing changed — most stations re-emit
            // the same StreamTitle every metaint window during a track.
            if (title == lastTitle) return;

            lastTitle = title;
            try { TitleCache.Set(url, title); }
            catch { /* never let UI bookkeeping break audio decode */ }
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
