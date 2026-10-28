using AcsTool.Core;
using AcsTool.Core.Net;
using AcsTool.Core.Parsing;
using AcsTool.Core.Sessions;
using AcsTool.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AcsTool.Proxy;

/// <summary>Creates a fresh ProxyServer per Start from the current settings (plan §3.10).</summary>
public sealed class ProxyServerFactory(ICwmpParser parser, IMessageSummarizer summarizer, IUpstreamHandlerFactory handlers,
    ISessionSink sink, TimeProvider time, ILoggerFactory loggerFactory)
{
    public ProxyServer Create(ProxySettings settings, CaptureSettings capture)
    {
        var tracker = new CpeSessionTracker(settings, handlers, sink, time, loggerFactory.CreateLogger<CpeSessionTracker>());
        var rewriter = new HeaderRewriter(settings);
        var forwarder = new UpstreamForwarder(settings, rewriter, time);
        var handler = new ProxyRequestHandler(settings, capture, parser, summarizer, tracker, forwarder, sink, rewriter, time,
            loggerFactory.CreateLogger<ProxyRequestHandler>());
        return new ProxyServer(settings, handler, tracker, loggerFactory);
    }
}

public static class ProxyServiceCollectionExtensions
{
    public static IServiceCollection AddAcsToolProxy(this IServiceCollection services)
    {
        services.AddAcsToolCore();
        services.TryAddSingleton<ProxyServerFactory>();
        return services;
    }
}
