using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MoeLoaderR.Core;

// Reports receive progress while HttpClient buffers the response, without a second image buffer.
public sealed class ReceiveProgressHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    public event Action<int> ProgressChanged;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.Content = new ProgressContent(response.Content, value => ProgressChanged?.Invoke(value));
        return response;
    }

    private sealed class ProgressContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action<int> _report;

        public ProgressContent(HttpContent inner, Action<int> report)
        {
            _inner = inner;
            _report = report;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context, CancellationToken cancellationToken)
        {
            var source = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var total = _inner.Headers.ContentLength;
            var buffer = new byte[16384];
            long received = 0;
            var lastPercentage = 0;
            _report(0);
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                received += count;
                var percentage = total > 0 ? (int)Math.Min(99, received * 100d / total.Value) : 0;
                if (percentage == lastPercentage) continue;
                lastPercentage = percentage;
                _report(percentage);
            }
            _report(100);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
