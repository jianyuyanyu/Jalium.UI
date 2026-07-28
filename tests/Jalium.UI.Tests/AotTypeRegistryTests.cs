using System.ComponentModel;
using Jalium.UI.Controls;
using Jalium.UI.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jalium.UI.Tests;

public sealed class AotTypeRegistryTests
{
    [Fact]
    public void GeneratedCatalog_DrivesConventionBasedMvvmDiscovery()
    {
        AotTypeRegistry.Register<GeneratedCatalogPage>();
        AotTypeRegistry.Register<GeneratedCatalogViewModel>();

        var services = new ServiceCollection();
        services.AddViewsAndViewModels(typeof(GeneratedCatalogPage).Assembly);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ViewRegistry>();

        Assert.True(
            registry.TryGetViewModelType(
                typeof(GeneratedCatalogPage),
                out var viewModelType));
        Assert.Equal(typeof(GeneratedCatalogViewModel), viewModelType);
        Assert.NotNull(provider.GetService<GeneratedCatalogPage>());
        Assert.NotNull(provider.GetService<GeneratedCatalogViewModel>());
    }

    [Fact]
    public void RegisteredTypes_AreExposedByDeclaringAssembly()
    {
        AotTypeRegistry.Register<GeneratedCatalogViewModel>();

        Assert.True(
            AotTypeRegistry.TryGetTypes(
                typeof(GeneratedCatalogViewModel).Assembly,
                out var types));
        Assert.Contains(typeof(GeneratedCatalogViewModel), types);
    }

    public sealed class GeneratedCatalogPage : Page
    {
    }

    public sealed class GeneratedCatalogViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public string Title { get; set; } = "AOT";
    }
}
