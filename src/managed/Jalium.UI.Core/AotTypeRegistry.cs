using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Jalium.UI;

/// <summary>
/// Stores the source-generated type catalog used by Jalium's reflection-shaped
/// features when an application is trimmed or compiled with NativeAOT.
/// </summary>
/// <remarks>
/// Application code does not need to populate this registry. The JALXAML source
/// generator emits a module initializer that registers every referenceable
/// binding type declared by the consuming assembly. The annotations on
/// <see cref="Register(Type)"/> preserve the constructors and public members needed
/// by MVVM discovery and string-path data binding.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class AotTypeRegistry
{
    private static readonly ConcurrentDictionary<Assembly, AssemblyCatalog> Catalogs = new();

    /// <summary>
    /// Marks an assembly as source-generator aware, including assemblies that
    /// do not declare any registrable types.
    /// </summary>
    public static void RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Catalogs.GetOrAdd(assembly, static _ => new AssemblyCatalog());
    }

    /// <summary>
    /// Registers and preserves a type declared in a consuming assembly.
    /// </summary>
    public static void Register(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.PublicFields)]
        Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Catalogs
            .GetOrAdd(type.Assembly, static _ => new AssemblyCatalog())
            .Types.TryAdd(type, 0);
    }

    /// <summary>
    /// Strongly typed convenience overload for explicit registrations.
    /// </summary>
    public static void Register<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.PublicFields)] T>()
    {
        Register(typeof(T));
    }

    /// <summary>
    /// Returns the generated catalog for <paramref name="assembly"/>.
    /// </summary>
    public static bool TryGetTypes(
        Assembly assembly,
        [NotNullWhen(true)] out IReadOnlyList<Type>? types)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (!Catalogs.TryGetValue(assembly, out var catalog))
        {
            types = null;
            return false;
        }

        types = catalog.Types.Keys
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private sealed class AssemblyCatalog
    {
        public ConcurrentDictionary<Type, byte> Types { get; } = new();
    }
}
