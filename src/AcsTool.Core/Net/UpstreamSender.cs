using System.Buffers;
using System.Net.Http;
using AcsTool.Core.Sessions;

namespace AcsTool.Core.Net;

public sealed record UpstreamSendResult(int? StatusCode, IReadOnlyList<KeyValuePair<string, string>> Headers,
    ReadOnlyMemory<byte> Body, UpstreamError? Error, ExchangeTimings Timings)
{
    /// <summary>True when a response (any status) was received and fully read.</summary>
    public bool HasResponse => StatusCode is not null && Error is null or { Kind: UpstreamErrorKind.HttpStatus };
}

/// <summary>
/// Sends one request over a per-session invoker with our own timeout, reads the body with a size guard,
/// and classifies failures (shared by the proxy forwarder and the simulator transport; plan D10).
/// </summary>
public static class UpstreamSender
{
    public static async Task<UpstreamSendResult> SendAsync(HttpMessageInvoker invoker, HttpRequestMessage request,
        PhaseTracker tracker, TimeSpan timeout, int maxBodyBytes, TimeProvider time, CancellationToken ct,
        Func<string?>? lastTlsPolicyErrors = null)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(time);
        tracker.Reset();
        var started = time.GetUtcNow();
        var ts = time.GetTimestamp();
        var isHttps = request.RequestUri?.Scheme == Uri.UriSchemeHttps;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var resp = await invoker.SendAsync(request, cts.Token).ConfigureAwait(false);
            var toHeaders = time.GetElapsedTime(ts);
            tracker.MarkHeaders(toHeaders);
            var headers = CollectHeaders(resp);
            var (body, tooLarge) = await ReadBodyAsync(resp.Content, maxBodyBytes, cts.Token).ConfigureAwait(false);
            var timings = new ExchangeTimings(started, tracker.DnsDuration, tracker.ConnectDuration, toHeaders, time.GetElapsedTime(ts));
            if (tooLarge)
            {
                return new UpstreamSendResult((int)resp.StatusCode, headers, ReadOnlyMemory<byte>.Empty,
                    new UpstreamError(UpstreamErrorKind.BodyTooLarge, RequestPhase.ReadingBody, $"Response body exceeds {maxBodyBytes} bytes"), timings);
            }
            return new UpstreamSendResult((int)resp.StatusCode, headers, body, ErrorClassifier.FromStatus((int)resp.StatusCode), timings);
        }
#pragma warning disable CA1031 // every failure is classified, never thrown (NFR-07)
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            var canceledByCaller = ct.IsCancellationRequested;
            var timedOut = !canceledByCaller && cts.IsCancellationRequested;
            var error = ErrorClassifier.Classify(ex, tracker, isHttps, timedOut, canceledByCaller, timeout, lastTlsPolicyErrors?.Invoke());
            var timings = new ExchangeTimings(started, tracker.DnsDuration, tracker.ConnectDuration, tracker.ToHeadersDuration, time.GetElapsedTime(ts));
            return new UpstreamSendResult(null, [], ReadOnlyMemory<byte>.Empty, error, timings);
        }
    }

    public static List<KeyValuePair<string, string>> CollectHeaders(HttpResponseMessage resp)
    {
        ArgumentNullException.ThrowIfNull(resp);
        var list = new List<KeyValuePair<string, string>>();
        foreach (var h in resp.Headers.NonValidated)
        {
            foreach (var v in h.Value)
            {
                list.Add(new(h.Key, v));
            }
        }
        foreach (var h in resp.Content.Headers.NonValidated)
        {
            foreach (var v in h.Value)
            {
                list.Add(new(h.Key, v));
            }
        }
        return list;
    }

    private static async Task<(byte[] Body, bool TooLarge)> ReadBodyAsync(HttpContent content, int max, CancellationToken ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var ms = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + read > max)
                    {
                        return ([], true);
                    }
                    ms.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return (ms.ToArray(), false);
        }
    }
}
