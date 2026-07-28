using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Jalium.UI.Hosting;

/// <summary>
/// In-memory registry of <c>(ViewType -&gt; ViewModelType)</c> mappings produced by
/// <c>AddView&lt;TView, TViewModel&gt;()</c> and consumed by
/// <see cref="IViewFactory"/>/<see cref="ViewModelLocator"/>.
/// </summary>
/// <remarks>
/// Registered as a singleton on <c>IServiceCollection</c>. Concurrent-safe
/// because views may be registered from builder-time (single thread) but
/// resolved from multiple threads during rendering.
/// </remarks>
public sealed class ViewRegistry
{
    private readonly ConcurrentDictionary<Type, Type> _viewToViewModel = new();

    /// <summary>
    /// Registers that <paramref name="viewType"/> should have an instance of
    /// <paramref name="viewModelType"/> set as its <c>DataContext</c> when
    /// created through <see cref="IViewFactory"/>.
    /// </summary>
    public void Register(
        Type viewType,
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.PublicFields)]
        Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(viewType);
        ArgumentNullException.ThrowIfNull(viewModelType);
        _viewToViewModel[viewType] = viewModelType;
    }

    /// <summary>
    /// Looks up the ViewModel type paired with <paramref name="viewType"/>.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2068:UnrecognizedReflectionPattern",
        Justification =
            "ViewModel types enter this registry through AddView<TView,TViewModel>, " +
            "AotTypeRegistry-backed discovery, or the annotated Register method; " +
            "all three preserve their public constructors.")]
    public bool TryGetViewModelType(
        Type viewType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        out Type? viewModelType)
    {
        var found = _viewToViewModel.TryGetValue(viewType, out var vm);
        viewModelType = vm;
        return found;
    }

    /// <summary>
    /// Returns <see langword="true"/> if any ViewModel has been registered for
    /// <paramref name="viewType"/>.
    /// </summary>
    public bool IsRegistered(Type viewType) => _viewToViewModel.ContainsKey(viewType);
}
