using Jalium.UI.Notifications;

namespace Jalium.UI;

/// <summary>
/// Public bootstrap entry point for the Jalium.UI.Android platform package.
/// Mirrors <c>Jalium.UI.Desktop.DesktopBootstrap</c>: an explicit, idempotent
/// <see cref="Initialize"/> method that
/// <see cref="SystemNotificationManager.EnsurePlatformBackendLoaded"/>
/// invokes reflectively on first access.
/// <para>
/// We deliberately don't rely on <c>[ModuleInitializer]</c> alone — CLR may
/// defer module-constructor execution until a type from the assembly is first
/// touched, so a plain <c>Assembly.Load</c> isn't enough to wire the platform
/// glue. An explicit, idempotent public method is more deterministic.
/// </para>
/// </summary>
public static class AndroidBootstrap
{
    private static bool s_initialized;

    /// <summary>
    /// Idempotent initializer. Registers
    /// <see cref="AndroidNotificationBackend"/> as the
    /// <see cref="SystemNotificationManager.BackendFactory"/> implementation
    /// on Android. Subsequent calls are no-ops.
    /// </summary>
    public static void Initialize()
    {
        if (s_initialized) return;
        s_initialized = true;

        // Backend itself probes for the native helper; the factory expression
        // never touches Android.* APIs at construction time, so the platform
        // gate would just add noise.
#pragma warning disable CA1416 // Validate platform compatibility
        SystemNotificationManager.BackendFactory ??= () => new AndroidNotificationBackend();
#pragma warning restore CA1416
    }
}
