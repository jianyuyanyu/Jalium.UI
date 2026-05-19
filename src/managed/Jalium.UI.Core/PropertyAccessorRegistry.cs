using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Jalium.UI;

/// <summary>
/// Global registry for AOT-safe property accessors. Registered accessors are used
/// by the data binding engine and Razor value resolver instead of reflection,
/// enabling property access on types whose members would otherwise be trimmed by the linker.
/// <para>
/// All lookups also fall back to <c>Type.GetProperty</c> reflection so the
/// engine still works for types that have not opted in to typed accessors.
/// Trim/AOT-safe usage requires applications to register accessors for every
/// view-model bound from XAML — this is enforced at the lookup boundary
/// (this class) so callers do not need their own DAM annotations.
/// </para>
/// </summary>
public static class PropertyAccessorRegistry
{
    private static readonly ConcurrentDictionary<(Type, string), Func<object, object?>> Accessors = new();
    private static readonly ConcurrentDictionary<(Type, string), Action<object, object?>> Setters = new();

    /// <summary>
    /// Registers a direct property accessor for a type+property combination.
    /// </summary>
    public static void Register(Type type, string propertyName, Func<object, object?> accessor)
    {
        if (type != null && !string.IsNullOrWhiteSpace(propertyName) && accessor != null)
            Accessors[(type, propertyName)] = accessor;
    }

    /// <summary>
    /// Registers a typed property setter so the binding engine can write back to a
    /// strongly-typed property without reflection.
    /// </summary>
    public static void RegisterSetter(Type type, string propertyName, Action<object, object?> setter)
    {
        if (type != null && !string.IsNullOrWhiteSpace(propertyName) && setter != null)
            Setters[(type, propertyName)] = setter;
    }

    /// <summary>
    /// Registers property accessors for multiple properties on a type.
    /// </summary>
    public static void RegisterAll(Type type, IEnumerable<(string Name, Func<object, object?> Accessor)> accessors)
    {
        if (type == null || accessors == null) return;
        foreach (var (name, accessor) in accessors)
            Accessors[(type, name)] = accessor;
    }

    /// <summary>
    /// Tries to get a registered accessor for the given type and property.
    /// </summary>
    public static bool TryGet(Type type, string propertyName, out Func<object, object?> accessor)
    {
        return Accessors.TryGetValue((type, propertyName), out accessor!);
    }

    /// <summary>
    /// Tries to read a property value using a registered accessor, falling back to reflection.
    /// </summary>
    /// <remarks>
    /// The reflection fallback is annotated with <see cref="RequiresUnreferencedCodeAttribute"/>:
    /// trim-safe applications must register accessors via <see cref="Register"/> for every
    /// type they bind from XAML.
    /// </remarks>
    [RequiresUnreferencedCode("PropertyAccessorRegistry falls back to Type.GetProperty when no accessor is registered. Register typed accessors via Register() to opt out of reflection.")]
    public static bool TryReadProperty(object source, string propertyName, out object? value)
    {
        var type = source.GetType();
        if (TryGet(type, propertyName, out var accessor))
        {
            value = accessor(source);
            return true;
        }

        // CanRead 守卫与 TryWriteProperty 的 CanWrite 对称:set-only 属性、或 trim 后
        // PropertyInfo 元数据保留但 getter 方法被剥离的情形,GetValue 会抛
        // ArgumentException("Property Get method was not found");这里直接 fall through
        // 让 BindingExpression 用 FallbackValue,避免把绑定异常传播到 ctor/ visual tree。
        var prop = type.GetProperty(propertyName);
        if (prop != null && prop.CanRead)
        {
            value = prop.GetValue(source);
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Tries to write a property value using a registered setter, falling back to reflection.
    /// </summary>
    [RequiresUnreferencedCode("PropertyAccessorRegistry falls back to Type.GetProperty when no setter is registered. Register typed setters via RegisterSetter() to opt out of reflection.")]
    public static bool TryWriteProperty(object source, string propertyName, object? value)
    {
        var type = source.GetType();
        if (Setters.TryGetValue((type, propertyName), out var setter))
        {
            setter(source, value);
            return true;
        }

        var prop = type.GetProperty(propertyName);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(source, value);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the <see cref="PropertyInfo"/> for a property on <paramref name="source"/>'s
    /// runtime type via reflection. Used by the binding engine to set up two-way change
    /// notification subscriptions for unregistered types.
    /// </summary>
    [RequiresUnreferencedCode("PropertyAccessorRegistry.TryGetPropertyInfo uses Type.GetProperty reflection on the runtime type of source.")]
    public static PropertyInfo? TryGetPropertyInfo(object source, string propertyName)
    {
        return source.GetType().GetProperty(propertyName);
    }
}
