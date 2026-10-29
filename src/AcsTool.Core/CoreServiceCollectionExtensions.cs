using AcsTool.Core.Building;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Core.Util;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AcsTool.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>Registers parser, builders, the session store (as both sink and store), handler factory and settings.</summary>
    public static IServiceCollection AddAcsToolCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRandom, SystemRandom>();
        services.TryAddSingleton<SettingsHolder>();
        services.TryAddSingleton<ICwmpParser, CwmpParser>();
        services.TryAddSingleton<IMessageSummarizer, MessageSummarizer>();
        services.TryAddSingleton<ICwmpMessageBuilder, CwmpMessageBuilder>();
        services.TryAddSingleton<IInformBuilder, InformBuilder>();
        services.TryAddSingleton<IUpstreamHandlerFactory, UpstreamHandlerFactory>();
        services.TryAddSingleton<IXmlFormatter, XmlPretty>();
        services.TryAddSingleton<ISecretProtector, PlainSecretProtector>();
        services.TryAddSingleton<ISettingsStore, JsonSettingsStore>();
        services.TryAddSingleton(sp =>
        {
            var holder = sp.GetRequiredService<SettingsHolder>();
            var store = new InMemorySessionStore(holder.Current.Capture, sp.GetRequiredService<TimeProvider>());
            // Limits follow the settings (loaded after the store may have been resolved, or edited in the Settings window).
            holder.Changed += (_, _) => store.UpdateLimits(holder.Current.Capture);
            return store;
        });
        services.TryAddSingleton<ISessionStore>(sp => sp.GetRequiredService<InMemorySessionStore>());
        services.TryAddSingleton<ISessionSink>(sp => sp.GetRequiredService<InMemorySessionStore>());
        return services;
    }
}
