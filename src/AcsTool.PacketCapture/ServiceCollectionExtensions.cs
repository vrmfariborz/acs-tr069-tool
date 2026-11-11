using AcsTool.PacketCapture.Backend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AcsTool.PacketCapture;

public static class PacketCaptureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the packet-capture services (PT1.1: <see cref="ICaptureBackend"/>). Registration and resolution load no native
    /// code; the backend is probed lazily when capture is first needed (design §5.2/§5.4).
    /// </summary>
    public static IServiceCollection AddAcsToolPacketCapture(this IServiceCollection services)
    {
        services.TryAddSingleton<ICaptureBackend, SharpPcapBackend>();
        return services;
    }
}
