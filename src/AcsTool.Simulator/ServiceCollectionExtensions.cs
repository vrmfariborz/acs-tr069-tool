using AcsTool.Core;
using AcsTool.Core.Building;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Core.Util;
using AcsTool.Simulator.Engine;
using AcsTool.Simulator.Profiles;
using AcsTool.Simulator.Rpc;
using AcsTool.Simulator.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AcsTool.Simulator;

public static class SimulatorServiceCollectionExtensions
{
    /// <summary>Registers the simulator (engine, transports, RPC dispatcher, profile store). Requires an IAppPaths registration for profiles.</summary>
    public static IServiceCollection AddAcsToolSimulator(this IServiceCollection services)
    {
        services.AddAcsToolCore();
        services.TryAddSingleton<ICwmpTransportFactory, HttpCwmpTransportFactory>();
        services.TryAddSingleton(_ => RpcDispatcher.CreateDefault());
        services.TryAddSingleton<IProfileStore, JsonProfileStore>();
        services.TryAddSingleton(sp => new CpeServices(sp.GetRequiredService<ICwmpTransportFactory>(), sp.GetRequiredService<RpcDispatcher>(),
            sp.GetRequiredService<ICwmpParser>(), sp.GetRequiredService<IMessageSummarizer>(), sp.GetRequiredService<ICwmpMessageBuilder>(),
            sp.GetRequiredService<IInformBuilder>(), sp.GetRequiredService<ISessionSink>(), sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IRandom>(), sp.GetRequiredService<ILoggerFactory>()));
        services.TryAddSingleton<SimulationEngine>();
        return services;
    }
}
