using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Jalium.UI.Controls;
using Jalium.UI.Data;

namespace Jalium.UI.Tests;

/// <summary>
/// <see cref="HierarchicalDataTemplate"/> 绑定的子集合在运行时增删，必须反映到已实例化的子容器上
/// （WPF 下 HDT 绑 ObservableCollection 本就是这个行为）。
///
/// <para>此前 <c>TreeViewItem</c> 只在 <c>EnsureChildrenRealized</c> 那一刻对子集合快照一次，
/// 之后集合再怎么变 UI 都不动 —— 只有整棵树换 <c>ItemsSource</c>（走 ItemsControl 自己的
/// 集合订阅）才会重建。于是"节点展开时才异步把真实子项填进 Children"这类懒加载
/// （解决方案资源管理器展开 .cs 文件解析出类型 / 方法）永远停在占位项上。</para>
/// </summary>
public sealed class TreeViewLazyChildrenTests
{
    [Fact]
    public void ReplacingChildrenAfterRealizationRefreshesTheContainers()
    {
        var (tree, container, node) = Prepare(expanded: true);

        // 占位子项已实例化成容器。
        var placeholder = Assert.IsType<TreeViewItem>(Assert.Single(container.Items));
        Assert.Equal("正在解析…", ((Node)placeholder.Header!).Name);

        // 懒加载落地：整批换掉占位。
        node.Children.Clear();
        node.Children.Add(new Node("IReportService"));
        node.Children.Add(new Node("ReportService"));

        Assert.Equal(
            new[] { "IReportService", "ReportService" },
            container.Items.OfType<TreeViewItem>().Select(i => ((Node)i.Header!).Name).ToArray());

        GC.KeepAlive(tree);
    }

    [Fact]
    public void AppendingToAnEmptyChildCollectionMakesTheExpanderAppear()
    {
        var node = new Node("Program.cs");           // 建时无子项 —— 连延迟状态机都没 arm
        var (tree, container, _) = Prepare(expanded: true, node);

        Assert.False(container.HasItems);

        node.Children.Add(new Node("Main(string[])"));

        Assert.True(container.HasItems);
        var child = Assert.IsType<TreeViewItem>(Assert.Single(container.Items));
        Assert.Equal("Main(string[])", ((Node)child.Header!).Name);

        GC.KeepAlive(tree);
    }

    [Fact]
    public void ClearingChildrenRemovesTheContainersAndTheExpander()
    {
        var (tree, container, node) = Prepare(expanded: true);
        Assert.True(container.HasItems);

        // 解析失败 / 文件里没有任何声明：子项清空，展开箭头也该跟着消失。
        node.Children.Clear();

        Assert.Empty(container.Items);
        Assert.False(container.HasItems);

        GC.KeepAlive(tree);
    }

    [Fact]
    public void CollapsedNodesTrackTheCollectionWithoutRealizingContainersEarly()
    {
        var (tree, container, node) = Prepare(expanded: false);

        // 收起状态：箭头在（延迟状态机已 arm），但不该提前造子容器。
        Assert.True(container.HasItems);
        Assert.Empty(container.Items);

        node.Children.Clear();
        node.Children.Add(new Node("Reporting"));
        Assert.Empty(container.Items);

        container.IsExpanded = true;

        var child = Assert.IsType<TreeViewItem>(Assert.Single(container.Items));
        Assert.Equal("Reporting", ((Node)child.Header!).Name);

        GC.KeepAlive(tree);
    }

    [Fact]
    public void RefillingOneItemAtATimeKeepsOrderAndReusesExistingContainers()
    {
        var (tree, container, node) = Prepare(expanded: true);
        node.Children.Clear();

        // 懒加载回填就是这个形态：Clear() 之后逐个 Add。每次 Add 都整批重建的话，
        // 第 i 次要造 i 个容器（O(n²)），几百个成员的文件展开时肉眼可见地卡。
        var names = new[] { "A", "B", "C", "D", "E" };
        var containersAfterEachAdd = new List<TreeViewItem[]>();
        foreach (var name in names)
        {
            node.Children.Add(new Node(name));
            containersAfterEachAdd.Add(container.Items.OfType<TreeViewItem>().ToArray());
        }

        Assert.Equal(names, container.Items.OfType<TreeViewItem>().Select(i => ((Node)i.Header!).Name).ToArray());

        // 增量的判据：每一步都只是在末尾追加，前面那些容器实例原封不动。
        for (var step = 1; step < containersAfterEachAdd.Count; step++)
        {
            var previous = containersAfterEachAdd[step - 1];
            var current = containersAfterEachAdd[step];
            Assert.Equal(previous.Length + 1, current.Length);
            for (var i = 0; i < previous.Length; i++)
            {
                Assert.Same(previous[i], current[i]);
            }
        }

        GC.KeepAlive(tree);
    }

    [Fact]
    public void RemovingOneChildDropsOnlyThatContainer()
    {
        var (tree, container, node) = Prepare(expanded: true);
        node.Children.Clear();
        foreach (var name in new[] { "A", "B", "C" })
        {
            node.Children.Add(new Node(name));
        }

        var before = container.Items.OfType<TreeViewItem>().ToArray();
        node.Children.RemoveAt(1);

        var after = container.Items.OfType<TreeViewItem>().ToArray();
        Assert.Equal(new[] { "A", "C" }, after.Select(i => ((Node)i.Header!).Name).ToArray());
        Assert.Same(before[0], after[0]);
        Assert.Same(before[2], after[1]);

        GC.KeepAlive(tree);
    }

    [Fact]
    public void InsertingInTheMiddleLandsAtTheRightIndex()
    {
        var (tree, container, node) = Prepare(expanded: true);
        node.Children.Clear();
        node.Children.Add(new Node("A"));
        node.Children.Add(new Node("C"));

        node.Children.Insert(1, new Node("B"));

        Assert.Equal(
            new[] { "A", "B", "C" },
            container.Items.OfType<TreeViewItem>().Select(i => ((Node)i.Header!).Name).ToArray());

        GC.KeepAlive(tree);
    }

    [Fact]
    public void RecycledContainersStopTrackingTheOldDataNodesCollection()
    {
        var tree = new TestTreeView
        {
            ItemTemplate = new HierarchicalDataTemplate { ItemsSource = new Binding(nameof(Node.Children)) },
        };
        var container = new TreeViewItem();
        var node = NewFileNode();

        tree.Prepare(container, node);
        tree.Clear(container, node);

        // 回收后旧数据节点仍可能继续被后台懒加载写入 —— 那些写入绝不能再驱动这个容器
        // （既是错绑，也会让旧 VM 通过集合订阅把容器 root 住）。
        node.Children.Clear();
        node.Children.Add(new Node("IReportService"));

        Assert.Empty(container.Items);
    }

    [Fact]
    public void RebindingAContainerDropsTheSubscriptionToThePreviousNode()
    {
        var tree = new TestTreeView
        {
            ItemTemplate = new HierarchicalDataTemplate { ItemsSource = new Binding(nameof(Node.Children)) },
        };
        var container = new TreeViewItem();
        var first = NewFileNode();
        var second = NewFileNode();

        tree.Prepare(container, first);
        tree.Prepare(container, second);

        first.Children.Clear();
        first.Children.Add(new Node("来自旧节点"));

        // 容器已改绑 second，旧节点的集合变更不能再改写它。
        var only = Assert.IsType<TreeViewItem>(Assert.Single(container.Items));
        Assert.Equal("正在解析…", ((Node)only.Header!).Name);

        second.Children.Clear();
        second.Children.Add(new Node("来自新节点"));

        var refreshed = Assert.IsType<TreeViewItem>(Assert.Single(container.Items));
        Assert.Equal("来自新节点", ((Node)refreshed.Header!).Name);
    }

    private static (TestTreeView Tree, TreeViewItem Container, Node Node) Prepare(
        bool expanded, Node? node = null)
    {
        var tree = new TestTreeView
        {
            ItemTemplate = new HierarchicalDataTemplate { ItemsSource = new Binding(nameof(Node.Children)) },
        };
        var container = new TreeViewItem();
        node ??= NewFileNode();
        node.IsExpanded = expanded;

        tree.Prepare(container, node);
        return (tree, container, node);
    }

    /// <summary>一个刚建好的 .cs 文件节点：只有占位子项，真实成员等展开后才填。</summary>
    private static Node NewFileNode()
    {
        var node = new Node("IReportService.cs");
        node.Children.Add(new Node("正在解析…"));
        return node;
    }

    private sealed class Node : INotifyPropertyChanged
    {
        private bool _isExpanded = true;
        private bool _isSelected;

        public Node(string name) => Name = name;

        public string Name { get; }

        public ObservableCollection<Node> Children { get; } = new();

        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private sealed class TestTreeView : TreeView
    {
        public void Prepare(TreeViewItem container, object item) => PrepareContainerForItem(container, item);

        public void Clear(TreeViewItem container, object item) => ClearContainerForItem(container, item);
    }
}
