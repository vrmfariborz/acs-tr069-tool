using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace AcsTool.Core.Net;

/// <summary>Builds a SocketsHttpHandler ConnectCallback that records DNS/connect phases and enforces a connect timeout.</summary>
public static class ConnectCallbackFactory
{
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Create(
        TimeSpan connectTimeout, Func<PhaseTracker> trackerAccessor)
    {
        ArgumentNullException.ThrowIfNull(trackerAccessor);
        return async (context, ct) =>
        {
            var tracker = trackerAccessor();
            tracker.BeginConnect();
            var host = context.DnsEndPoint.Host;
            var port = context.DnsEndPoint.Port;
            var sw = Stopwatch.StartNew();

            IPAddress[] addresses;
            if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            {
                addresses = [literal];
                tracker.MarkDnsDone(TimeSpan.Zero, addresses);
            }
            else
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    throw new HttpRequestException(HttpRequestError.NameResolutionError, $"DNS lookup for '{host}' failed: {ex.SocketErrorCode}", ex);
                }
                if (addresses.Length == 0)
                {
                    throw new HttpRequestException(HttpRequestError.NameResolutionError, $"DNS lookup for '{host}' returned no addresses",
                        new SocketException((int)SocketError.HostNotFound));
                }
                tracker.MarkDnsDone(sw.Elapsed, addresses);
            }

            var connectSw = Stopwatch.StartNew();
            Exception? last = null;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(connectTimeout);
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);
                    tracker.MarkConnected(connectSw.Elapsed, socket.RemoteEndPoint as IPEndPoint);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    socket.Dispose();
                    throw new HttpRequestException(HttpRequestError.ConnectionError,
                        $"connect to {address}:{port} timed out after {connectTimeout.TotalSeconds:0.#}s",
                        new SocketException((int)SocketError.TimedOut));
                }
                catch (SocketException ex)
                {
                    socket.Dispose();
                    last = ex;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            throw new HttpRequestException(HttpRequestError.ConnectionError,
                $"connect to {host}:{port} failed: {(last as SocketException)?.SocketErrorCode}", last);
        };
    }
}
