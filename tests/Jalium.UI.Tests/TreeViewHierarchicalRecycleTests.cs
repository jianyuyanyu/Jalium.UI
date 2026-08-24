using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Data;

namespace Jalium.UI.Tests;

/// <summary>
/// 回归：绑定 <see cref="HierarchicalDataTemplate"/> 的 <see cref="TreeView"/>，
/// 在 ItemsSource 集合 Clear() + Add() 之后整棵子树被渲染两遍。
///
/// 成因链：<see cref="ItemContainerGenerator"/> 的 Reset 是「分层回收」——把已实例化的容器
/// ClearContainerForItem 后推进 _recyclePool（而不是像 RemoveAll 那样连池一起丢），
/// 下一次实例化从池里 pop 回同一个 TreeViewItem 实例重新 Prepare。而
/// TreeViewItem 的层级状态（模板生成的子容器 + 延迟实例化状态机）此前没有对称的清理点：
/// ApplyHierarchicalDataTemplate 会把 _childrenRealized 重置为 false 并重新 arm 子集合，
/// EnsureChildrenRealized 于是把新一批子容器 AddRange 到还留着旧子容器的 Items 上。
/// </summary>
public sealed class TreeViewHierarchicalRecycleTests
{
    [Fact]
    public void CollectionResetThenRefillDoesNotDuplicateRealizedSubtree()
    {
        var roots = new ObservableCollection<Node>();
        var tree = new TreeView
        {
            ItemTemplate = new HierarchicalDataTemplate { ItemsSource = new Binding(nameof(Node.Children)) },
            ItemsSource = roots,
        };
        VirtualizingPanel.SetIsVirtualizing(tree, true);

        roots.Add(BuildSolution());
        var first = RealizeFirstContainer(tree);
        AssertSolutionShape(first);

        // 这两行就是 OpenedProjectViewModel.BuildSolutionTreeFrom 做的事：
        // SolutionRoots.Clear() 之后重新 Add 一棵结构相同的新树。
        roots.Clear();
        roots.Add(BuildSolution());

        var second = RealizeFirstContainer(tree);
        Assert.Same(first, second); // 容器确实来自回收池 —— 否则这条用例根本没覆盖 bug 路径
        AssertSolutionShape(second);
    }

    [Fact]
    public void RePreparingTheSameContainerDoesNotAppendASecondSubtree()
    {
        var tree = new TestTreeView
        {
            ItemTemplate = new HierarchicalDataTemplate { ItemsSource = new Binding(nameof(Node.Children)) },
        };
        var container = new TreeViewItem();

        tree.Prepare(container, BuildSolution());
        AssertSolutionShape(container);

        // 没有中间的 Clear 也必须幂等：Prepare 是可以被重复调用的（模板换绑、
        // 池中容器复用），不能靠调用方保证只调一次。
        tree.Prepare(container, BuildSolution());
        AssertSolutionShape(container);
    }

    [Fact]
    public void ClearContainerForItemReleasesSubtreeWithoutMutatingTheDataNode()
    {
        var tree = new TestTreeView
        {
            ItemTemplate = new HierarchicalDataTemplate { ItemsSource = new Binding(nameof(Node.Children)) },
        };
        var container = new TreeViewItem();
        var solution = BuildSolution();
        var src = solution.Children[0];

        tree.Prepare(container, solution);
        Assert.NotEmpty(container.Items);

        tree.Clear(container, solution);

        Assert.Empty(container.Items);
        Assert.Null(container.ParentTreeView);
        Assert.False(container.IsExpanded);

        // 解桥必须发生在清 IsExpanded / IsSelected 之前，否则 DP 回调会把 false
        // 回写进仍在使用中的数据节点，用户的展开状态被容器回收顺手抹掉。
        Assert.True(solution.IsExpanded);
        Assert.True(src.IsExpanded);

        // 桥接解除后，数据节点的后续变更不应再驱动这个已回收的容器。
        solution.IsExpanded = false;
        Assert.False(container.IsExpanded);
        solution.IsExpanded = true;
        Assert.False(container.IsExpanded);
    }

    private static TreeViewItem RealizeFirstContainer(TreeView tree)
    {
        var generator = tree.ItemContainerGenerator;
        DependencyObject? container;
        using (generator.StartAt(generator.GeneratorPositionFromIndex(0), GeneratorDirection.Forward))
        {
            container = generator.GenerateNext(out _);
        }

        var item = Assert.IsType<TreeViewItem>(container);
        generator.PrepareItemContainer(item);
        return item;
    }

    /// <summary>断言「解决方案 → src → 4 个项目」这棵树每层都只出现一次。</summary>
    private static void AssertSolutionShape(TreeViewItem root)
    {
        var src = Assert.IsType<TreeViewItem>(Assert.Single(root.Items));
        Assert.Equal("src", ((Node)src.Header!).Name);

        Assert.Equal(4, src.Items.Count);
        Assert.Equal(
            new[] { "Entities", "UseCases", "Adapters", "Frameworks" },
            src.Items.OfType<TreeViewItem>().Select(i => ((Node)i.Header!).Name).ToArray());

        foreach (var project in src.Items.OfType<TreeViewItem>())
        {
            var file = Assert.IsType<TreeViewItem>(Assert.Single(project.Items));
            Assert.Equal("Class1.cs", ((Node)file.Header!).Name);
        }
    }

    private static Node BuildSolution()
    {
        var src = new Node("src");
        foreach (var name in new[] { "Entities", "UseCases", "Adapters", "Frameworks" })
        {
            var project = new Node(name);
            project.Children.Add(new Node("Class1.cs") { IsExpanded = false });
            src.Children.Add(project);
        }

        var solution = new Node("解决方案");
        solution.Children.Add(src);
        return solution;
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
