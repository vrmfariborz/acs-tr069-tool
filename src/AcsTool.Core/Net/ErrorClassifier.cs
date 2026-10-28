using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AcsTool.Core.Net;

/// <summary>Maps upstream exceptions to <see cref="UpstreamErrorKind"/> (plan §3.5.5).</summary>
public static class ErrorClassifier
{
    public static UpstreamError Classify(Exception ex, PhaseTracker tracker, bool isHttps, bool timedOutByUs, bool canceledByCaller,
        TimeSpan? timeout = null, string? lastTlsPolicyErrors = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(tracker);
        var type = ex.GetType().Name;
        var phase = tracker.CurrentPhase(isHttps);

        // 1/2: cancellations
        if (ex is OperationCanceledException || ex is TimeoutException)
        {
            if (canceledByCaller)
            {
                return new UpstreamError(UpstreamErrorKind.Canceled, phase, "Request canceled", type);
            }
            if (timedOutByUs || ex is TimeoutException)
            {
                var msg = timeout is { } t ? $"Timed out in phase {phase} after {t.TotalSeconds:0.#} s" : $"Timed out in phase {phase}";
                return new UpstreamError(UpstreamErrorKind.Timeout, phase, msg, type);
            }
            return new UpstreamError(UpstreamErrorKind.Canceled, phase, ex.Message, type);
        }

        var chain = Chain(ex).ToList();
        var socket = chain.OfType<SocketException>().LastOrDefault();
        var socketError = socket?.SocketErrorCode.ToString();
        var message = Innermost(ex).Message;

        // 3: HttpRequestError (any level; SocketsHttpHandler wraps ConnectCallback exceptions)
        var errors = chain.OfType<HttpRequestException>().Select(h => h.HttpRequestError).ToList();
        if (errors.Contains(HttpRequestError.NameResolutionError) || socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData)
        {
            return new UpstreamError(UpstreamErrorKind.Dns, RequestPhase.Dns, message, type, socketError);
        }
        if (errors.Contains(HttpRequestError.SecureConnectionError) || chain.OfType<AuthenticationException>().Any())
        {
            var auth = chain.OfType<AuthenticationException>().FirstOrDefault();
            return new UpstreamError(UpstreamErrorKind.Tls, RequestPhase.Tls, auth?.Message ?? message, type, socketError,
                TlsDetail: lastTlsPolicyErrors);
        }
        if (errors.Contains(HttpRequestError.ConnectionError))
        {
            // A connection error after the connection was established (reset while awaiting the response) is a protocol-level drop.
            if (phase is RequestPhase.AwaitingResponse or RequestPhase.ReadingBody && socket is null)
            {
                return new UpstreamError(UpstreamErrorKind.Protocol, phase, message, type);
            }
            return new UpstreamError(UpstreamErrorKind.Connect, RequestPhase.Connect, message, type, socketError);
        }
        if (errors.Any(e => e is HttpRequestError.ResponseEnded or HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError
                or HttpRequestError.ConfigurationLimitExceeded or HttpRequestError.VersionNegotiationError))
        {
            return new UpstreamError(UpstreamErrorKind.Protocol, phase, message, type, socketError);
        }

        // 4: inner-exception walk
        if (socket is not null)
        {
            return phase is RequestPhase.Dns or RequestPhase.Connect
                ? new UpstreamError(UpstreamErrorKind.Connect, RequestPhase.Connect, message, type, socketError)
                : new UpstreamError(UpstreamErrorKind.Protocol, phase, message, type, socketError);
        }
        if (chain.OfType<IOException>().Any())
        {
            return new UpstreamError(UpstreamErrorKind.Protocol, phase, message, type);
        }
        return new UpstreamError(UpstreamErrorKind.Unknown, phase, message, type);
    }

    /// <summary>Status ≥ 400 and ≠ 401 → HttpStatus; otherwise null (401 is an auth challenge, not an error).</summary>
    public static UpstreamError? FromStatus(int status) =>
        status >= 400 && status != 401
            ? new UpstreamError(UpstreamErrorKind.HttpStatus, RequestPhase.AwaitingResponse, $"Upstream returned HTTP {status}", HttpStatus: status)
            : null;

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }

    private static Exception Innermost(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null)
        {
            e = e.InnerException;
        }
        return e;
    }
}
