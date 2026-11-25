using Microsoft.Maui.Storage;

namespace Strybok.Maui;

/// <summary>
/// Keeps the two values of the deferred-link check (an attempt counter and a "done" flag) in
/// MAUI <c>Preferences</c>: <c>SharedPreferences</c> on Android, <c>NSUserDefaults</c> on iOS.
/// Set as the default by <see cref="StrybokMauiExtensions.AddStrybok"/>.
/// </summary>
/// <remarks>
/// Preferences are removed with the app's data. They can be part of a device backup; a restored
/// "done" flag only means that the restored installation does not look for a deferred link.
/// </remarks>
public sealed class PreferencesDeferredLinkStore : IDeferredLinkStore
{
    /// <inheritdoc />
    public string? Read(string key) => Preferences.Default.Get<string?>(key, null);

    /// <inheritdoc />
    public void Write(string key, string value) => Preferences.Default.Set<string>(key, value);
}
