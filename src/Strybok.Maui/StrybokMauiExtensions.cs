using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Devices;

namespace Strybok.Maui;

/// <summary>.NET MAUI glue for the Strybok SDK.</summary>
public static class StrybokMauiExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="ManualLinkSource"/> and a singleton
    /// <see cref="StrybokClient"/> that reads from it.
    /// </summary>
    /// <remarks>
    /// Feed the link source from <c>Application.OnAppLinkRequestReceived(Uri)</c>:
    /// <code>
    /// protected override void OnAppLinkRequestReceived(Uri uri)
    /// {
    ///     base.OnAppLinkRequestReceived(uri);
    ///     _linkSource.Push(uri);
    /// }
    /// </code>
    /// <see cref="StrybokOptions.Platform"/> is pre-filled from <c>DeviceInfo.Platform</c>; the
    /// callback may override it.
    /// </remarks>
    public static IServiceCollection AddStrybok(this IServiceCollection services, Action<StrybokOptions> configure)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        services.TryAddSingleton<ManualLinkSource>();
        services.TryAddSingleton<ILinkSource>(provider => provider.GetRequiredService<ManualLinkSource>());
        services.TryAddSingleton<StrybokClient>(provider =>
        {
            var options = new StrybokOptions { Platform = DetectPlatform() };
            configure(options);
            return new StrybokClient(options, provider.GetRequiredService<ILinkSource>());
        });
        return services;
    }

    /// <summary>Maps <c>DeviceInfo.Platform</c> to the platform reported with <c>app_open</c> events.</summary>
    public static StrybokPlatform DetectPlatform()
    {
        try
        {
            var platform = DeviceInfo.Platform;
            if (platform == DevicePlatform.iOS) return StrybokPlatform.iOS;
            if (platform == DevicePlatform.Android) return StrybokPlatform.Android;
        }
        catch (Exception)
        {
            // DeviceInfo is not implemented outside a MAUI app (unit tests, plain .NET).
        }
        return StrybokPlatform.Unknown;
    }
}
