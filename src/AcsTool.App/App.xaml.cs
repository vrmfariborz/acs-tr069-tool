using System.IO;
using System.Windows;
using System.Windows.Threading;
using AcsTool.App.Services;
using AcsTool.App.Views;
using AcsTool.Core.Settings;
using AcsTool.Presentation;
using AcsTool.Presentation.Abstractions;
using AcsTool.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace AcsTool.App;

/// <summary>Composition root (plan §3.10): Generic Host + DI + Serilog, global exception handlers, orderly shutdown.</summary>
public partial class App : Application
{
    private IHost? _host;
    private Microsoft.Extensions.Logging.ILogger? _log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smokeExit = e.Args.Any(a => string.Equals(a, "--smoke-exit", StringComparison.OrdinalIgnoreCase));
        var paths = new WindowsAppPaths();
        Directory.CreateDirectory(paths.LogsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting", LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(paths.LogsDir, "acstool-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:o} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}", formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Logging.ClearProviders();
            builder.Services.AddSerilog(dispose: true);
            builder.Services.AddSingleton<IAppPaths>(paths);
            builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
            builder.Services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher(Dispatcher));
            builder.Services.AddSingleton<IDialogService, WpfDialogService>();
            builder.Services.AddSingleton<IFileDialogService, WpfFileDialogService>();
            builder.Services.AddSingleton<IClipboardService, WpfClipboardService>();
            builder.Services.AddAcsToolPresentation();
            builder.Services.AddSingleton<MainWindow>();
            _host = builder.Build();
            _log = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AcsTool.App");
            HookGlobalHandlers();
            await _host.StartAsync();

            var main = _host.Services.GetRequiredService<MainViewModel>();
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            await main.InitializeAsync();
            _log.LogInformation(9100, "AcsTool started{Smoke}", smokeExit ? " (smoke test)" : "");
            if (smokeExit)
            {
                await main.ShutdownAsync();
                await StopHostAsync();
                Shutdown(0);
                return;
            }
            window.Show();
        }
#pragma warning disable CA1031 // last-chance startup failure → message + exit code 1
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Fatal(ex, "Startup failed");
            if (!smokeExit)
            {
                MessageBox.Show(ex.ToString(), "AcsTool failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            await Log.CloseAndFlushAsync();
            Shutdown(1);
        }
    }

    /// <summary>Called by MainWindow after MainViewModel.ShutdownAsync completed.</summary>
    internal async Task StopHostAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
            _host = null;
        }
        await Log.CloseAndFlushAsync();
    }

    private void HookGlobalHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.LogError(9002, args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            _log?.LogCritical(9003, args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
            Log.CloseAndFlush();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogError(9004, e.Exception, "Unhandled UI exception");
        if (e.Exception is OutOfMemoryException or StackOverflowException)
        {
            return;
        }
        _host?.Services.GetService<IBannerService>()?.Show(BannerKind.Error, "Unexpected error: " + e.Exception.Message, "unhandled");
        e.Handled = true;
    }
}
