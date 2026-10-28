using AcsTool.Core.Building;
using AcsTool.Core.Cwmp;
using AcsTool.Core.Model;
using AcsTool.Simulator.Parameters;
using AcsTool.Simulator.Profiles;
using Microsoft.Extensions.Logging;

namespace AcsTool.Simulator.Rpc;

public sealed record RpcContext(ParameterStore Store, CpeProfile Profile, ILogger Log);

public sealed record RpcResult(CwmpMessage? Response, CwmpFault? Fault, FollowUp? FollowUp)
{
    /// <summary>Set for the NoResponse override: the CPE hangs for this long, then aborts the session.</summary>
    public TimeSpan? Hang { get; init; }

    public static RpcResult Ok(CwmpMessage response, FollowUp? followUp = null) => new(response, null, followUp);

    public static RpcResult Failed(uint code, string? text = null) => new(null, CwmpMessageBuilder.CreateFault(code, text), null);

    public static RpcResult FromFault(CwmpFault fault) => new(null, fault, null);
}

public abstract record FollowUp
{
    public sealed record Reboot(string CommandKey) : FollowUp;

    public sealed record FactoryReset : FollowUp;

    public sealed record Download(DownloadMessage Request) : FollowUp;

    public sealed record ScheduleInform(uint DelaySeconds, string CommandKey) : FollowUp;
}

public interface IRpcHandler
{
    CwmpMessageKind Handles { get; }

    RpcResult Handle(CwmpEnvelope request, RpcContext ctx);
}

/// <summary>Routes ACS requests to handlers, applying per-method <see cref="RpcBehavior"/> overrides first (plan §3.7.3).</summary>
public sealed class RpcDispatcher
{
    private readonly Dictionary<CwmpMessageKind, IRpcHandler> _handlers;

    public RpcDispatcher(IEnumerable<IRpcHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToDictionary(h => h.Handles);
    }

    /// <summary>The MVP handler set: GetRPCMethods, GetParameterNames, GetParameterValues, SetParameterValues.</summary>
    public static RpcDispatcher CreateDefault()
    {
        var core = new List<IRpcHandler> { new GetParameterNamesHandler(), new GetParameterValuesHandler(), new SetParameterValuesHandler() };
        core.Add(new GetRpcMethodsHandler(core.Select(h => MethodName(h.Handles)).Prepend("GetRPCMethods").ToList()));
        return new RpcDispatcher(core);
    }

    public IReadOnlyCollection<CwmpMessageKind> Supported => _handlers.Keys;

    public RpcResult Dispatch(CwmpEnvelope req, RpcContext ctx)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(ctx);
        var method = string.IsNullOrEmpty(req.MethodLocalName) ? req.Kind.ToString() : req.MethodLocalName;
        if (ctx.Profile.RpcBehaviors.TryGetValue(method, out var behavior))
        {
            switch (behavior.Kind)
            {
                case RpcBehaviorKind.Fault:
                    ctx.Log.LogInformation(3101, "Simulated fault {Code} for {Method}", behavior.FaultCode, method);
                    return RpcResult.Failed(behavior.FaultCode);
                case RpcBehaviorKind.NoResponse:
                    return new RpcResult(null, null, null) { Hang = TimeSpan.FromSeconds(Math.Max(0, behavior.DelaySeconds)) };
            }
        }
        if (req.Message is not null && _handlers.TryGetValue(req.Kind, out var handler))
        {
            try
            {
                return handler.Handle(req, ctx);
            }
#pragma warning disable CA1031 // a handler bug must turn into 9002, never kill the CPE loop
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ctx.Log.LogError(3102, ex, "RPC handler for {Method} failed", method);
                return RpcResult.Failed(FaultCodes.InternalError);
            }
        }
        ctx.Log.LogWarning(3103, "Method not supported by the simulator: {Method}", method);
        return RpcResult.Failed(FaultCodes.MethodNotSupported);
    }

    internal static string MethodName(CwmpMessageKind k) => k.ToString();
}

internal sealed class GetRpcMethodsHandler(IReadOnlyList<string> methods) : IRpcHandler
{
    public CwmpMessageKind Handles => CwmpMessageKind.GetRPCMethods;

    public RpcResult Handle(CwmpEnvelope request, RpcContext ctx) => RpcResult.Ok(new GetRpcMethodsResponseMessage(methods));
}

internal sealed class GetParameterNamesHandler : IRpcHandler
{
    public CwmpMessageKind Handles => CwmpMessageKind.GetParameterNames;

    public RpcResult Handle(CwmpEnvelope request, RpcContext ctx)
    {
        var m = (GetParameterNamesMessage)request.Message!;
        var r = ctx.Store.GetNames(m.ParameterPath, m.NextLevel);
        return r.IsFault ? RpcResult.FromFault(r.Fault!) : RpcResult.Ok(new GetParameterNamesResponseMessage(r.Value!));
    }
}

internal sealed class GetParameterValuesHandler : IRpcHandler
{
    public CwmpMessageKind Handles => CwmpMessageKind.GetParameterValues;

    public RpcResult Handle(CwmpEnvelope request, RpcContext ctx)
    {
        var m = (GetParameterValuesMessage)request.Message!;
        var r = ctx.Store.GetValues(m.Names);
        return r.IsFault ? RpcResult.FromFault(r.Fault!) : RpcResult.Ok(new GetParameterValuesResponseMessage(r.Value!));
    }
}

internal sealed class SetParameterValuesHandler : IRpcHandler
{
    public CwmpMessageKind Handles => CwmpMessageKind.SetParameterValues;

    public RpcResult Handle(CwmpEnvelope request, RpcContext ctx)
    {
        var m = (SetParameterValuesMessage)request.Message!;
        var r = ctx.Store.TrySetMany(m.Values, m.ParameterKey);
        return r.IsFault ? RpcResult.FromFault(r.Fault!) : RpcResult.Ok(new SetParameterValuesResponseMessage(r.Value));
    }
}
