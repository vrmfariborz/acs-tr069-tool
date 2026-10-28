using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using AcsTool.Core.Util;
using AcsTool.Presentation.Abstractions;
using AcsTool.Presentation.Detail;
using AcsTool.Presentation.Feed;
using AcsTool.Presentation.Proxy;
using AcsTool.Presentation.Settings;
using AcsTool.Presentation.Shell;
using AcsTool.Presentation.Simulator;
using AcsTool.Proxy;
using AcsTool.Simulator;
using AcsTool.Simulator.Engine;
using AcsTool.Simulator.Profiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AcsTool.Presentation;

public static class PresentationServiceCollectionExtensions
{
    /// <summary>
    /// Registers view models, the feed pump and the banner service. The host must also register IUiDispatcher,
    /// IClipboardService, IDialogService, IFileDialogService and IAppPaths (and may override ISecretProtector).
    /// </summary>
    public static IServiceCollection AddAcsToolPresentation(this IServiceCollection services)
    {
        services.AddAcsToolProxy();
        services.AddAcsToolSimulator();
        services.TryAddSingleton<IBannerService, BannerService>();
        services.TryAddSingleton<SessionFeedPump>();
        services.TryAddSingleton(sp => new ProxyViewModel(sp.GetRequiredService<ProxyServerFactory>(), sp.GetRequiredService<SettingsHolder>(),
            sp.GetRequiredService<IBannerService>(), sp.GetRequiredService<IUiDispatcher>(), NewFeed(sp, SessionSource.Proxy),
            sp.GetRequiredService<ILogger<ProxyViewModel>>()));
        services.TryAddSingleton(sp => new SimulatorViewModel(sp.GetRequiredService<IProfileStore>(), sp.GetRequiredService<SimulationEngine>(),
            sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<IBannerService>(), sp.GetRequiredService<IUiDispatcher>(),
            NewFeed(sp, SessionSource.Simulator), sp.GetRequiredService<ILogger<SimulatorViewModel>>()));
        services.TryAddSingleton<SettingsViewModel>();
        services.TryAddSingleton<MainViewModel>();
        return services;
    }

    private static SessionFeedViewModel NewFeed(IServiceProvider sp, SessionSource source)
    {
        var settings = sp.GetRequiredService<SettingsHolder>().Current;
        var detail = new SessionDetailViewModel(sp.GetRequiredService<IXmlFormatter>(), sp.GetRequiredService<IClipboardService>());
        return new SessionFeedViewModel(sp.GetRequiredService<ISessionStore>(), source, settings.Capture.MaxFeedItems, settings.Ui.NewestOnTop, detail);
    }
}
