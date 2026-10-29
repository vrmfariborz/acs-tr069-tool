using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Export;
using AcsTool.Core.Model;
using AcsTool.Core.Parsing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace AcsTool.Testing;

public sealed record FakeAcsRequest(int Index, string Method, string PathAndQuery, IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body, string ConnectionId, ParseResult Parsed, int SessionNumber, DateTimeOffset ReceivedAt)
{
    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public bool HasHeader(string name) => Headers.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    public CwmpMessageKind Kind => Parsed.Kind;
}

public sealed class FakeAcsResponse
{
    public int Status { get; init; } = 200;
    public byte[]? Body { get; init; }
    public List<KeyValuePair<string, string>> Headers { get; init; } = [];
    public TimeSpan Delay { get; init; }
    public bool Abort { get; init; }

    public static FakeAcsResponse Soap(byte[] body, TimeSpan delay = default) => new()
    {
        Body = body,
        Delay = delay,
        Headers = [new("Content-Type", "text/xml; charset=\"utf-8\"")],
    };
}

public sealed class FakeAcsSession(int number)
{
    public int Number { get; } = number;
    internal int StepIndex { get; set; }
    internal int SentCounter { get; set; }
    public string? LastSentId { get; internal set; }
    public string? LastConnectionId { get; internal set; }
    public bool Ended { get; internal set; }
    public bool Authenticated { get; internal set; }
    public List<FakeAcsRequest> Requests { get; } = [];
}

/// <summary>Fluent script executed once per CWMP session (a session starts on each Inform).</summary>
public sealed class FakeAcsScript
{
    internal sealed record Step(string Description, Func<FakeAcsRequest, bool> Expect,
        Func<FakeAcsSession, FakeAcsRequest, FakeAcsResponse> Respond);

    private readonly CwmpMessageBuilder _builder = new();
    private (string Desc, Func<FakeAcsRequest, bool> Pred)? _expect;
    private TimeSpan _delay;

    internal List<Step> Steps { get; } = [];

    public CwmpVersion Version { get; set; } = CwmpVersion.V1_0;

    public FakeAcsScript ExpectInform() => Expect("Inform", r => r.Kind == CwmpMessageKind.Inform);

    public FakeAcsScript ExpectEmpty() => Expect("empty POST", r => r.Kind == CwmpMessageKind.Empty);

    public FakeAcsScript ExpectResponse<T>() where T : CwmpMessage => Expect(typeof(T).Name, r => r.Parsed.Message is T);

    public FakeAcsScript ExpectFault(uint? code = null) =>
        Expect("Fault " + code, r => r.Parsed.Message is CwmpFault f && (code is null || f.FaultCode == code));

    public FakeAcsScript ExpectAny() => Expect("any", _ => true);

    public FakeAcsScript Expect(string description, Func<FakeAcsRequest, bool> predicate)
    {
        _expect = (description, predicate);
        return this;
    }

    public FakeAcsScript Delay(TimeSpan delay)
    {
        _delay = delay;
        return this;
    }

    public FakeAcsScript RespondInformResponse(bool? holdRequests = null) => Add((s, r) =>
        FakeAcsResponse.Soap(_builder.Build(new InformResponseMessage(1), Version,
            new CwmpHeader(r.Parsed.Envelope?.Header.Id ?? "1", HoldRequests: holdRequests))));

    /// <summary>Responds with an ACS request (RPC); the cwmp:ID is generated and later checked against the CPE's echo.</summary>
    public FakeAcsScript Send(CwmpMessage message, bool? holdRequests = null) => Add((s, _) =>
    {
        var id = $"acs-{s.Number}-{++s.SentCounter}";
        s.LastSentId = id;
        return FakeAcsResponse.Soap(_builder.Build(message, Version, new CwmpHeader(id, HoldRequests: holdRequests)));
    });

    /// <summary>Responds with a message echoing the request's cwmp:ID (e.g. TransferCompleteResponse or an ACS Fault).</summary>
    public FakeAcsScript Respond(CwmpMessage message) => Add((_, r) =>
        FakeAcsResponse.Soap(_builder.Build(message, Version, new CwmpHeader(r.Parsed.Envelope?.Header.Id ?? "1"))));

    public FakeAcsScript RespondRaw(int status, string? body = null, string? contentType = "text/xml") => Add((_, _) => new FakeAcsResponse
    {
        Status = status,
        Body = body is null ? null : Encoding.UTF8.GetBytes(body),
        Headers = body is null || contentType is null ? [] : [new("Content-Type", contentType)],
    });

    public FakeAcsScript RespondStatus(int status) => RespondRaw(status);

    public FakeAcsScript End204() => Add((_, _) => new FakeAcsResponse { Status = 204 });

    public FakeAcsScript End200Empty() => Add((_, _) => new FakeAcsResponse { Status = 200, Body = [] });

    public FakeAcsScript AbortConnection() => Add((_, _) => new FakeAcsResponse { Abort = true });

    /// <summary>Responds with an arbitrary response (custom headers/body bytes).</summary>
    public FakeAcsScript RespondWith(Func<FakeAcsSession, FakeAcsRequest, FakeAcsResponse> respond) => Add(respond);

    private FakeAcsScript Add(Func<FakeAcsSession, FakeAcsRequest, FakeAcsResponse> respond)
    {
        var (desc, pred) = _expect ?? ("any", _ => true);
        var delay = _delay;
        Steps.Add(new Step(desc, pred, (s, r) =>
        {
            var resp = respond(s, r);
            return delay > TimeSpan.Zero ? new FakeAcsResponse { Status = resp.Status, Body = resp.Body, Headers = resp.Headers, Abort = resp.Abort, Delay = delay } : resp;
        }));
        _expect = null;
        _delay = TimeSpan.Zero;
        return this;
    }
}

public sealed class FakeAcsOptions
{
    public Action<FakeAcsScript>? Script { get; set; }
    public (string User, string Password)? Basic { get; set; }
    public (string User, string Password, string Realm)? Digest { get; set; }

    /// <summary>Set-Cookie value sent with the InformResponse; "{n}" is replaced with the session number.</summary>
    public string? SetCookie { get; set; }

    /// <summary>Raw override evaluated before the script; return null to continue with the script.</summary>
    public Func<FakeAcsRequest, FakeAcsResponse?>? Override { get; set; }
}

/// <summary>
/// Scriptable ACS on a real Kestrel listener at 127.0.0.1:0 (plan T2.1). Records every request, the connection it came on,
/// and script violations. Supports Basic and Digest (MD5, qop=auth, uri validated against the request-target).
/// </summary>
public sealed class FakeAcs : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<FakeAcsRequest> _received = new();
    private readonly ConcurrentQueue<string> _violations = new();
    private readonly object _lock = new();
    private readonly List<FakeAcsSession> _sessions = [];
    private readonly Dictionary<string, FakeAcsSession> _byConnection = [];
    private readonly HashSet<string> _nonces = [];
    private int _index;
    private int _active;
    private int _maxActive;

    private FakeAcs(WebApplication app, FakeAcsOptions options)
    {
        _app = app;
        Options = options;
    }

    public FakeAcsOptions Options { get; }

    public Uri BaseUri { get; private set; } = null!;

    public int Port => BaseUri.Port;

    public string Url(string path = "/acs") => $"http://127.0.0.1:{Port}{path}";

    public IReadOnlyList<FakeAcsRequest> Received => [.. _received];

    public IReadOnlyList<string> Violations => [.. _violations];

    public IReadOnlyList<FakeAcsSession> Sessions
    {
        get { lock (_lock) { return [.. _sessions]; } }
    }

    public int MaxConcurrentSessions => Volatile.Read(ref _maxActive);

    public static async Task<FakeAcs> StartAsync(Action<FakeAcsOptions>? configure = null)
    {
        var options = new FakeAcsOptions();
        configure?.Invoke(options);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(IPAddress.Loopback, 0);
        });
        var app = builder.Build();
        var acs = new FakeAcs(app, options);
        app.Run(acs.HandleAsync);
        await app.StartAsync();
        acs.BaseUri = new Uri(app.Urls.First());
        return acs;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var body = ms.ToArray();
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var h in ctx.Request.Headers)
        {
            foreach (var v in h.Value)
            {
                headers.Add(new(h.Key, v ?? string.Empty));
            }
        }
        var target = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget ?? (ctx.Request.Path + ctx.Request.QueryString).ToString();
        var parsed = new CwmpParser().Parse(body, ctx.Request.ContentType);
        var connectionId = ctx.Connection.Id;

        FakeAcsResponse response;
        FakeAcsRequest request;
        lock (_lock)
        {
            var session = FindSession(parsed, headers, connectionId);
            request = new FakeAcsRequest(Interlocked.Increment(ref _index), ctx.Request.Method, target, headers, body, connectionId, parsed,
                session?.Number ?? 0, DateTimeOffset.UtcNow);
            _received.Enqueue(request);
            response = Options.Override?.Invoke(request) ?? Decide(session, request, ctx.Request.Method);
        }

        if (response.Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(response.Delay, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        if (response.Abort)
        {
            ctx.Abort();
            return;
        }
        ctx.Response.StatusCode = response.Status;
        foreach (var (k, v) in response.Headers)
        {
            ctx.Response.Headers.Append(k, v);
        }
        if (response.Body is { Length: > 0 } b)
        {
            ctx.Response.ContentLength = b.Length;
            await ctx.Response.Body.WriteAsync(b, ctx.RequestAborted);
        }
        else if (response.Status != 204 && response.Status != 304)
        {
            ctx.Response.ContentLength = 0;
        }
    }

    private FakeAcsSession? FindSession(ParseResult parsed, List<KeyValuePair<string, string>> headers, string connectionId)
    {
        if (parsed.Kind == CwmpMessageKind.Inform)
        {
            return null; // decided in Decide (auth first)
        }
        var cookie = headers.FirstOrDefault(h => h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)).Value;
        if (cookie is not null)
        {
            foreach (var part in cookie.Split(';', StringSplitOptions.TrimEntries))
            {
                if (part.StartsWith("fakeacs-sid=", StringComparison.Ordinal)
                    && int.TryParse(part["fakeacs-sid=".Length..], out var n)
                    && _sessions.FirstOrDefault(s => s.Number == n) is { } byCookie)
                {
                    return byCookie;
                }
            }
        }
        if (_byConnection.TryGetValue(connectionId, out var byConn))
        {
            return byConn;
        }
        return _sessions.LastOrDefault();
    }

    private FakeAcsResponse Decide(FakeAcsSession? session, FakeAcsRequest request, string method)
    {
        if (request.Kind == CwmpMessageKind.Inform)
        {
            var challenge = CheckAuth(request, method);
            if (challenge is not null)
            {
                return challenge;
            }
            session = new FakeAcsSession(_sessions.Count + 1) { Authenticated = true };
            _sessions.Add(session);
            var now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, now, seen) != seen)
            {
            }
        }
        if (session is null)
        {
            _violations.Enqueue($"#{request.Index}: {request.Kind} without a session");
            return new FakeAcsResponse { Status = 400 };
        }
        session.Requests.Add(request);
        session.LastConnectionId = request.ConnectionId;
        _byConnection[request.ConnectionId] = session;

        var script = new FakeAcsScript();
        if (Options.Script is null)
        {
            script.ExpectInform().RespondInformResponse().ExpectEmpty().End204();
        }
        else
        {
            Options.Script(script);
        }
        if (session.StepIndex >= script.Steps.Count)
        {
            _violations.Enqueue($"#{request.Index}: {request.Kind} after end of script (session {session.Number})");
            return new FakeAcsResponse { Status = 204 };
        }
        var step = script.Steps[session.StepIndex++];
        if (!step.Expect(request))
        {
            _violations.Enqueue($"#{request.Index}: expected {step.Description} but got {request.Kind} (session {session.Number})");
            return new FakeAcsResponse { Status = 400 };
        }
        if (session.LastSentId is not null && request.Parsed.Envelope is { } env && env.Kind != CwmpMessageKind.Inform
            && (env.Kind.IsResponse() || env.Kind == CwmpMessageKind.Fault) && env.Header.Id != session.LastSentId)
        {
            _violations.Enqueue($"#{request.Index}: cwmp:ID '{env.Header.Id}' does not echo '{session.LastSentId}'");
        }
        var resp = step.Respond(session, request);
        if (request.Kind == CwmpMessageKind.Inform && Options.SetCookie is { } sc)
        {
            resp.Headers.Add(new("Set-Cookie", sc.Replace("{n}", session.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)));
        }
        if (session.StepIndex >= script.Steps.Count && !session.Ended)
        {
            session.Ended = true;
            Interlocked.Decrement(ref _active);
        }
        return resp;
    }

    private FakeAcsResponse? CheckAuth(FakeAcsRequest request, string method)
    {
        var auth = request.Header("Authorization");
        if (Options.Basic is { } basic)
        {
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{basic.User}:{basic.Password}"));
            if (auth != expected)
            {
                return new FakeAcsResponse { Status = 401, Headers = [new("WWW-Authenticate", "Basic realm=\"FakeAcs\"")] };
            }
        }
        if (Options.Digest is { } digest)
        {
            if (auth is null || !ValidateDigest(auth, method, request.PathAndQuery, digest.User, digest.Password, digest.Realm))
            {
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                _nonces.Add(nonce);
                return new FakeAcsResponse
                {
                    Status = 401,
                    Headers = [new("WWW-Authenticate", $"Digest realm=\"{digest.Realm}\", qop=\"auth\", nonce=\"{nonce}\", opaque=\"fakeacs\", algorithm=MD5")],
                };
            }
        }
        return null;
    }

    /// <summary>Digest MD5 with qop=auth; the uri parameter must equal the request-target received.</summary>
    public bool ValidateDigest(string authorization, string method, string requestTarget, string user, string password, string realm)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!authorization.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var p = authorization[7..];
        string? P(string n) => AuthRedactor.DigestParam(p, n);
        var nonce = P("nonce");
        if (P("username") != user || P("realm") != realm || nonce is null || !_nonces.Contains(nonce) || P("uri") != requestTarget)
        {
            return false;
        }
#pragma warning disable CA5351 // MD5 is what HTTP Digest uses
        static string H(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
#pragma warning restore CA5351
        var ha1 = H($"{user}:{realm}:{password}");
        var ha2 = H($"{method}:{P("uri")}");
        var expected = P("qop") is { } qop
            ? H($"{ha1}:{nonce}:{P("nc")}:{P("cnonce")}:{qop}:{ha2}")
            : H($"{ha1}:{nonce}:{ha2}");
        return string.Equals(expected, P("response"), StringComparison.OrdinalIgnoreCase);
    }
}
