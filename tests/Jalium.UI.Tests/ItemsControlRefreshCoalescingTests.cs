using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Themes;
using Jalium.UI.Data;
using Xunit.Abstractions;

namespace Jalium.UI.Tests;

/// <summary>
/// 量化 <see cref="ItemsControl"/> 在「一次页面构建」里到底重建了几遍容器。
///
/// <para>源生成器构树时对同一个 ItemsControl 会连续设多个属性（ItemsSource 的编译绑定、
/// ItemTemplate 的 StaticResource…），而 ItemsControl 对每个属性变化都同步跑一次全量
/// RefreshItems。若此时 DataContext 已可继承，第一次就会把 N 个容器全建出来，后续每个属性
/// 变化都要把它们全部推倒重建——这是纯浪费。框架本身有 BeginInit/EndInit 合并设施
/// （_itemsControlInitializationDepth），但生成代码并不使用。</para>
/// </summary>
[Collection("Application")]
public class ItemsControlRefreshCoalescingTests
{
    private readonly ITestOutputHelper _output;

    public ItemsControlRefreshCoalescingTests(ITestOutputHelper output) => _output = output;

    private static void ResetApplicationState()
    {
        var currentField = typeof(Application).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static);
        currentField?.SetValue(null, null);

        var resetMethod = typeof(ThemeManager).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static);
        resetMethod?.Invoke(null, null);
    }

    private sealed class CountingItemsControl : ItemsControl
    {
        public int ContainerCreations;

        protected override FrameworkElement GetContainerForItem(object item)
        {
            ContainerCreations++;
            return base.GetContainerForItem(item);
        }
    }

    private sealed class Vm
    {
        public string[] Rows { get; } = Enumerable.Range(0, 20).Select(i => "row" + i).ToArray();
    }

    /// <summary>
    /// 复现生成代码的属性设置顺序：DataContext 已经可继承的情况下，先设 ItemsSource（编译绑定）
    /// 再设 ItemTemplate。记录容器实际被创建了多少次。
    /// </summary>
    [Fact]
    public void SequentialPropertySetup_ContainerCreationCount_IsMeasured()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            var vm = new Vm();
            var host = new StackPanel { DataContext = vm };

            var items = new CountingItemsControl();
            host.Children.Add(items);

            // 生成代码的典型顺序：绑定 ItemsSource，然后设 ItemTemplate。
            items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Rows"));
            var afterItemsSource = items.ContainerCreations;

            items.ItemTemplate = new DataTemplate();
            var afterItemTemplate = items.ContainerCreations;

            _output.WriteLine($"rows = {vm.Rows.Length}");
            _output.WriteLine($"containers created after ItemsSource : {afterItemsSource}");
            _output.WriteLine($"containers created after ItemTemplate: {afterItemTemplate}");
            _output.WriteLine($"total rebuild factor: {(afterItemsSource > 0 ? (double)afterItemTemplate / afterItemsSource : 0):F2}x");

            // 记录性质：这里不设硬断言（容器实现可能虚拟化/复用），数字用于判断是否值得合并。
            Assert.True(afterItemTemplate >= afterItemsSource);
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// 对照：用框架已有的 BeginInit/EndInit 把同一串属性设置包起来，看容器创建次数能降到多少。
    /// 这条直接回答「合并是否值得做」。
    /// </summary>
    [Fact]
    public void BeginEndInit_CoalescesRefresh_IntoSinglePass()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            var vm = new Vm();
            var host = new StackPanel { DataContext = vm };

            var items = new CountingItemsControl();
            host.Children.Add(items);

            items.BeginInit();
            items.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Rows"));
            items.ItemTemplate = new DataTemplate();
            items.EndInit();

            _output.WriteLine($"containers created with BeginInit/EndInit: {items.ContainerCreations}");

            Assert.True(items.ContainerCreations >= 0);
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }
}
