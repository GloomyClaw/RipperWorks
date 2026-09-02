using System.Net;
using System.Net.Http.Headers;
using RipperWorks.Core;

namespace RipperWorks.Downloader;

public sealed class HttpDownloadTransport(HttpClient client)
    : IDownloadTransport
{
    public async Task<DownloadTransportResponse> OpenReadAsync(
        Uri uri,
        long offset,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0)
            request.Headers.Range = new RangeHeaderValue(offset, null);
        var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var rangeAccepted =
            response.StatusCode == HttpStatusCode.PartialContent;
        var total = response.Content.Headers.ContentRange?.Length ??
            (response.Content.Headers.ContentLength is { } length
                ? length + (rangeAccepted ? offset : 0)
                : null);
        var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken).ConfigureAwait(false);
        var responseFileName =
            response.Content.Headers.ContentDisposition?.FileNameStar ??
            response.Content.Headers.ContentDisposition?.FileName;
        responseFileName = responseFileName?
            .Trim()
            .Trim('"');
        return new(
            new ResponseOwnedStream(stream, response),
            total,
            rangeAccepted,
            responseFileName);
    }

    private sealed class ResponseOwnedStream(
        Stream inner,
        HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);
        public override Task FlushAsync(CancellationToken token) =>
            inner.FlushAsync(token);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken token = default) =>
            inner.ReadAsync(buffer, token);
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
