using Jalium.UI.Controls.Themes;
using Jalium.UI.Desktop.Platforms.Windows;
using Jalium.UI.Notifications;

namespace Jalium.UI.Desktop;

/// <summary>
/// Public bootstrap entry point for the Jalium.UI.Desktop platform package.
/// <see cref="Jalium.UI.Controls.Themes.ThemeManager"/> reflectively invokes
/// <see cref="Initialize"/> when it loads this assembly, mirroring the same
/// pattern used by <c>ThemeLoader.Initialize</c> in <c>Jalium.UI.Xaml</c>.
///
/// We deliberately don't rely on a <c>[ModuleInitializer]</c> alone — CLR may
/// defer module-constructor execution until a type from the assembly is first
/// touched, which means a plain <c>Assembly.Load</c> isn't enough to wire the
/// platform glue. An explicit, idempotent public method is more deterministic
/// and easier to debug.
/// </summary>
public static class DesktopBootstrap
{
    private static bool s_initialized;

    /// <summary>
    /// Idempotent initializer. Wires
    /// <see cref="ThemeManager.SystemAccentResolver"/> to read the live
    /// Windows accent color (Settings → Personalization → Colors) on Windows;
    /// no-op on non-Windows hosts.
    ///
    /// Called twice: once via <c>[ModuleInitializer]</c> (fast path when CLR
    /// gets there), once via explicit reflection from
    /// <c>ThemeManager.EnsurePlatformIntegrationLoaded</c> (deterministic
    /// fallback). Subsequent calls return immediately.
    /// </summary>
    public static void Initialize()
    {
        if (s_initialized)
            return;
        s_initialized = true;

        // Don't clobber a resolver an integrator may have already supplied —
        // explicit registrations take precedence.
        ThemeManager.SystemAccentResolver ??= WindowsSystemAccent.Resolve;

        // Register the WinRT toast backend. Same precedence rule: an integrator
        // that wants to inject a custom backend (tests, fakes) can set
        // BackendFactory before bootstrap runs and we won't overwrite.
        // The Desktop assembly is already net10.0-windows; the backend itself
        // returns IsSupported=false on Windows < 10.0.10240, so the unguarded
        // factory expression is safe.
#pragma warning disable CA1416 // Validate platform compatibility
        SystemNotificationManager.BackendFactory ??= () => new WindowsNotificationBackend();
#pragma warning restore CA1416
    }
}
