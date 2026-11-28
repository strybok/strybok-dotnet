using System;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.ApplicationModel;
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
    /// callback may override it. Set <see cref="StrybokOptions.ClientKey"/> there as well to use
    /// <see cref="StrybokClient.CreateLinkAsync"/> ("Share" links), and
    /// <see cref="StrybokOptions.ParameterLinks"/> for links without an API call.
    /// <para>
    /// For deferred links (<see cref="StrybokOptions.DeferredLinks"/>, off by default) these are
    /// pre-filled as well, and the callback may replace any of them:
    /// <see cref="StrybokOptions.DeferredLinkStore"/> with a <see cref="PreferencesDeferredLinkStore"/>,
    /// <see cref="StrybokOptions.AndroidPackageName"/> and <see cref="StrybokOptions.IosBundleId"/>
    /// from <c>AppInfo.PackageName</c>, <see cref="StrybokOptions.DeviceFamily"/> from
    /// <c>DeviceInfo</c> and <see cref="StrybokOptions.Locale"/> from
    /// <c>CultureInfo.CurrentUICulture</c>. The install referrer sources (Android) and the paste
    /// control (iOS) are your app's: this package ships no store integration and never reads the
    /// pasteboard.
    /// </para>
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
            ApplyDeferredLinkDefaults(options);
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

    /// <summary>
    /// The device family for iOS probabilistic matching: <c>iphone</c>, <c>ipad</c> or
    /// <c>ipod</c> from <c>DeviceInfo.Idiom</c> and <c>DeviceInfo.Model</c>; <c>null</c> for
    /// anything else (Mac, TV, watch) and outside a MAUI app.
    /// </summary>
    public static string? DetectDeviceFamily()
    {
        try
        {
            var idiom = DeviceInfo.Idiom;
            if (idiom == DeviceIdiom.Tablet) return "ipad";
            if (idiom == DeviceIdiom.Phone)
            {
                var model = DeviceInfo.Model;
                return model is not null && model.StartsWith("iPod", StringComparison.Ordinal) ? "ipod" : "iphone";
            }
        }
        catch (Exception)
        {
            // DeviceInfo is not implemented outside a MAUI app.
        }
        return null;
    }

    /// <summary>
    /// The language for iOS probabilistic matching: the name of
    /// <c>CultureInfo.CurrentUICulture</c>, e.g. <c>en-GB</c>; <c>null</c> when it has none.
    /// </summary>
    /// <remarks>
    /// The server compares only the language part with the first language of the browser that
    /// opened the link. The UI culture can differ from the device's first preferred language;
    /// for the exact value set <see cref="StrybokOptions.Locale"/> to
    /// <c>NSLocale.PreferredLanguages[0]</c> from your iOS platform code.
    /// </remarks>
    public static string? DetectLocale()
    {
        try
        {
            var name = CultureInfo.CurrentUICulture.Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ApplyDeferredLinkDefaults(StrybokOptions options)
    {
        options.DeferredLinkStore = new PreferencesDeferredLinkStore();
        options.DeviceFamily = DetectDeviceFamily;
        options.Locale = DetectLocale;
        try
        {
            // The application ID on Android, the bundle identifier on iOS.
            var identifier = AppInfo.PackageName;
            options.AndroidPackageName = identifier;
            options.IosBundleId = identifier;
        }
        catch (Exception)
        {
            // AppInfo is not implemented outside a MAUI app; deferred links then stay disabled
            // unless the app sets the identifier itself.
        }
    }
}
