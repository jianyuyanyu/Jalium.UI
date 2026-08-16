using System.Reflection;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Themes;
using Jalium.UI.Data;
using Xunit.Abstractions;

namespace Jalium.UI.Tests;

/// <summary>
/// 量化「整页视图替换」在 UI 线程上的成本——即 <c>PageHost.Content = new SomeView(vm)</c>
/// 这一步要付的树构建 + attach 重算开销。不涉及 GPU / present，因此可在无窗口环境下重复测量。
///
/// <para>为什么需要它：框架对这段是零仪表的（FrameHistory 的计时起点在 RenderFrame 入口，
/// 换页的绝大部分开销发生在那之前），DevTools 的帧面板在默认 render-thread 配置下也拿不到
/// 样本。没有这个基准就只能盲改。</para>
///
/// <para>★测量口径统一走 <see cref="BenchmarkHarness"/>：自适应轮数 + 剔除 GC 污染样本 +
/// 报告半四分位距。每条输出末尾的 ±X% 就是该次测量的分辨率，小于它的差异一律不算数。
/// 判断优化收益时**必须**跑 Release（<c>-c Release</c>）——Debug 下的绝对值不可用于对外结论。</para>
///
/// <para>断言阈值刻意放得很宽（只挡住数量级退化），真正的用途是把耗时打进测试输出供对比。</para>
/// </summary>
[Collection("Application")]
public class PageSwapCostBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public PageSwapCostBenchmarkTests(ITestOutputHelper output) => _output = output;

    private static void ResetApplicationState()
    {
        var currentField = typeof(Application).GetField("_current", BindingFlags.NonPublic | BindingFlags.Static);
        currentField?.SetValue(null, null);

        var resetMethod = typeof(ThemeManager).GetMethod("Reset", BindingFlags.NonPublic | BindingFlags.Static);
        resetMethod?.Invoke(null, null);
    }

#if DEBUG
    private const string ConfigurationNote =
        "★ 当前是 Debug 构建：绝对值偏高，只可与同为 Debug 的历史数据比较。判断优化收益请用 -c Release。";
#else
    private const string ConfigurationNote = "构建配置: Release";
#endif

    private void WriteHeader()
    {
        _output.WriteLine(ConfigurationNote);
        _output.WriteLine(new string('-', 100));
    }

    /// <summary>
    /// 按源生成器的 bottom-up 顺序构树：先把子树建完，最后才挂到父上。
    /// 这一点很关键——正是它让 attach 期的子树重算变成 O(n x depth)。
    /// </summary>
    private static Border BuildPageTree(int cardCount)
    {
        var stack = new StackPanel();

        for (var i = 0; i < cardCount; i++)
        {
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = "title" });
            texts.Children.Add(new TextBlock { Text = "description" });
            texts.Children.Add(new TextBlock { Text = "tag" });

            var inner = new Border { Child = texts };

            var grid = new Grid();
            grid.Children.Add(inner);

            var card = new Border { Child = grid };
            stack.Children.Add(card);
        }

        return new Border { Child = stack };
    }

    private static int CountVisualDescendants(Visual root)
    {
        var total = 1;
        for (var i = 0; i < root.VisualChildrenCount; i++)
        {
            if (root.GetVisualChild(i) is Visual child)
            {
                total += CountVisualDescendants(child);
            }
        }

        return total;
    }

    [Fact]
    public void PageSwap_BuildAndAttach_StaysWithinBudget()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();
            var host = new ContentControl();
            var elementCount = CountVisualDescendants(BuildPageTree(30));

            var stats = BenchmarkHarness.Measure(
                timedBody: () => host.Content = BuildPageTree(30),
                warmupRounds: 5,
                minRounds: 15);

            _output.WriteLine($"page tree elements (visual): {elementCount}");
            _output.WriteLine(stats.Format("整页替换 (30 cards)"));
            _output.WriteLine($"每元素: {stats.Best * 1000 / elementCount:F2} us");

            // 只挡数量级退化：一次换页的纯 UI 线程构建/挂载不该逼近秒级。
            Assert.True(
                stats.Median < 400d,
                $"整页替换的中位耗时 {stats.Median:F2} ms 超出预算——attach 期的子树重算或资源查找可能又退化了。");
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// 造一棵「元素总数固定、深度可调」的树，用来把 depth 因子从 n 里分离出来。
    /// 深度 d 时构造成一条 d 级的嵌套链，剩余元素平摊挂在最内层。
    /// </summary>
    private static Border BuildTreeWithDepth(int totalElements, int depth)
    {
        var chainRoot = new Border();
        var chainCount = 1;

        var chain = new List<Border> { chainRoot };
        while (chainCount < depth)
        {
            var next = new Border();
            chain.Add(next);
            chainCount++;
        }

        var leafHost = new StackPanel();
        var leafCount = Math.Max(0, totalElements - chainCount - 1);
        for (var i = 0; i < leafCount; i++)
        {
            leafHost.Children.Add(new TextBlock { Text = "leaf" });
        }

        // bottom-up 组装：最内层先成形，再逐级往外挂。
        chain[^1].Child = leafHost;
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            chain[i].Child = chain[i + 1];
        }

        return chain[0];
    }

    /// <summary>
    /// 固定元素总数、只改深度。若 attach 期的子树重算是 O(n x depth)，深树会显著更慢；
    /// 若已经是 O(n)，两者应当接近。
    /// </summary>
    [Fact]
    public void PageSwap_DepthFactor_IsMeasured()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();
            var host = new ContentControl();
            const int total = 800;

            var shallow = BenchmarkHarness.Measure(
                timedBody: () => host.Content = BuildTreeWithDepth(total, 4),
                warmupRounds: 4,
                minRounds: 13);

            var deep = BenchmarkHarness.Measure(
                timedBody: () => host.Content = BuildTreeWithDepth(total, 40),
                warmupRounds: 4,
                minRounds: 13);

            _output.WriteLine(shallow.Format($"elements={total} depth=4"));
            _output.WriteLine(deep.Format($"elements={total} depth=40"));
            _output.WriteLine(BenchmarkHarness.FormatRatio("deep/shallow", shallow, deep));
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// 贴近真实页面的规模（WelcomeView 运行时约 1200-2500 个可视元素）。
    /// </summary>
    [Fact]
    public void PageSwap_RealisticScale_IsMeasured()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();
            var host = new ContentControl();

            foreach (var cards in new[] { 30, 100, 200 })
            {
                var localCards = cards;
                var stats = BenchmarkHarness.Measure(
                    timedBody: () => host.Content = BuildPageTree(localCards),
                    warmupRounds: 3,
                    minRounds: 11);

                var elements = CountVisualDescendants(BuildPageTree(cards));
                _output.WriteLine(stats.Format($"cards={cards,3} elements={elements,5}"));
                _output.WriteLine($"    -> {stats.Best * 1000 / elements:F2} us/element");
            }
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// 把每元素成本拆成两段，判断优化该落在哪里：
    ///   construct —— 只 new 元素，不建立任何父子关系（DependencyObject / DP 系统的底价）
    ///   parent    —— 再把它们组装成树（UIElementCollection + AddLogicalChild + AddVisualChild
    ///                 + attach 期的各趟子树重算）
    /// </summary>
    [Fact]
    public void PageSwap_PerElementCostBreakdown_IsMeasured()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();
            const int cards = 100;
            var elements = cards * 6;

            // 两段共享同一批实例：先量构造，再量组装。用字段在两次测量间传递。
            Border[]? borders = null;
            Grid[]? grids = null;
            StackPanel[]? stacks = null;
            TextBlock[]? texts = null;

            var construct = BenchmarkHarness.Measure(
                timedBody: () =>
                {
                    borders = new Border[cards];
                    grids = new Grid[cards];
                    stacks = new StackPanel[cards];
                    texts = new TextBlock[cards * 3];
                    for (var i = 0; i < cards; i++)
                    {
                        borders[i] = new Border();
                        grids[i] = new Grid();
                        stacks[i] = new StackPanel();
                        texts[i * 3 + 0] = new TextBlock { Text = "title" };
                        texts[i * 3 + 1] = new TextBlock { Text = "description" };
                        texts[i * 3 + 2] = new TextBlock { Text = "tag" };
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            // 组装那一步必须拿到「全新且未组装」的实例，所以每轮在计时窗口外重建一批。
            var parenting = BenchmarkHarness.Measure(
                beforeEachRound: () =>
                {
                    borders = new Border[cards];
                    grids = new Grid[cards];
                    stacks = new StackPanel[cards];
                    texts = new TextBlock[cards * 3];
                    for (var i = 0; i < cards; i++)
                    {
                        borders[i] = new Border();
                        grids[i] = new Grid();
                        stacks[i] = new StackPanel();
                        texts[i * 3 + 0] = new TextBlock { Text = "title" };
                        texts[i * 3 + 1] = new TextBlock { Text = "description" };
                        texts[i * 3 + 2] = new TextBlock { Text = "tag" };
                    }
                },
                timedBody: () =>
                {
                    var root = new StackPanel();
                    for (var i = 0; i < cards; i++)
                    {
                        stacks![i].Children.Add(texts![i * 3 + 0]);
                        stacks[i].Children.Add(texts[i * 3 + 1]);
                        stacks[i].Children.Add(texts[i * 3 + 2]);
                        var inner = new Border { Child = stacks[i] };
                        grids![i].Children.Add(inner);
                        borders![i].Child = grids[i];
                        root.Children.Add(borders[i]);
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            _output.WriteLine($"elements ~= {elements}");
            _output.WriteLine(construct.Format("construct only"));
            _output.WriteLine($"    -> {construct.Best * 1000 / elements:F2} us/element");
            _output.WriteLine(parenting.Format("parenting"));
            _output.WriteLine($"    -> {parenting.Best * 1000 / elements:F2} us/element");
            _output.WriteLine(
                $"parenting share: {parenting.Best / (construct.Best + parenting.Best) * 100:F0}%");
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// 对照实验：同样的树形状，叶子分别用**无模板**元素（TextBlock）和**带 ControlTemplate**
    /// 的控件（Button）。真实页面充满带模板的控件，而合成基准此前只用无模板元素——两者的差距
    /// 就是模板展开路径（Control.ApplyTemplateCore 在常规 attach 之上额外叠加的全子树递归）
    /// 的成本。这是本轮优化的主判据。
    /// </summary>
    [Fact]
    public void TemplatedVersusPlainLeaves_IsCompared()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();
            var host = new ContentControl();
            const int cards = 50;

            static Border Build(int cards, bool templated)
            {
                var stack = new StackPanel();
                for (var i = 0; i < cards; i++)
                {
                    var leaves = new StackPanel();
                    for (var k = 0; k < 3; k++)
                    {
                        if (templated)
                        {
                            leaves.Children.Add(new Button { Content = "x" });
                        }
                        else
                        {
                            leaves.Children.Add(new TextBlock { Text = "x" });
                        }
                    }

                    var grid = new Grid();
                    grid.Children.Add(new Border { Child = leaves });
                    stack.Children.Add(new Border { Child = grid });
                }

                return new Border { Child = stack };
            }

            BenchmarkHarness.Stats Measure(bool templated) => BenchmarkHarness.Measure(
                timedBody: () =>
                {
                    var tree = Build(cards, templated);
                    host.Content = tree;
                    tree.Measure(new Size(1600, 1000));
                },
                warmupRounds: 4,
                minRounds: 15);

            var plain = Measure(templated: false);
            var templatedStats = Measure(templated: true);

            _output.WriteLine(plain.Format("plain leaves (TextBlock)"));
            _output.WriteLine(templatedStats.Format("templated leaves (Button)"));
            _output.WriteLine(BenchmarkHarness.FormatRatio("templated / plain", plain, templatedStats));
            _output.WriteLine(
                $"模板展开净增量: {templatedStats.Best - plain.Best:F2} ms / {cards * 3} 个 Button " +
                $"= {(templatedStats.Best - plain.Best) * 1000 / (cards * 3):F1} us 每次展开");
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// ★把一次 ControlTemplate 展开拆成各个阶段，直接指出成本落在哪一趟递归上。
    ///
    /// <para>做法：绕开 <see cref="FrameworkElement.ApplyTemplate"/>，自己按
    /// <c>Control.ApplyTemplateCore</c> 的顺序逐段调用它调用的那些 internal 方法，
    /// 每段测「累计到此为止」的耗时，相邻两段相减即得该趟递归的边际成本。
    /// 最后再测一次完整的 ApplyTemplate，差额就是 AddVisualChild
    /// （及其触发的 OnVisualParentChanged 全子树重算）+ 触发器 + OnApplyTemplate。</para>
    ///
    /// <para>每个实例都需要**独立的宿主控件**：SetTemplatedParentRecursive 会把模板里的
    /// x:Name 注册到宿主的 namescope，同一个宿主注册两次同名元素会抛异常。</para>
    /// </summary>
    [Fact]
    public void TemplateExpansion_PhaseBreakdown_IsMeasured()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();

            // 取一份真实的主题 ControlTemplate。Button 是页面上最常见的带模板控件。
            var host = new ContentControl();
            var probe = new Button { Content = "x" };
            host.Content = probe;
            probe.ApplyTemplate();

            var template = probe.Template;
            Assert.NotNull(template);

            var sampleRoot = template!.LoadContent();
            Assert.NotNull(sampleRoot);
            var perInstanceElements = CountVisualDescendants(sampleRoot!);

            const int instances = 200;
            var owners = new Button[instances];
            var roots = new FrameworkElement?[instances];

            // ★不用「累计到此为止再相减」的口径。相减法要求两次独立测量各自足够稳，实测
            // 相邻两次进程运行里同一段能差 30%，相减后完全不可读（曾出现负的边际成本）。
            // 改成：把前置步骤全部放进计时窗口**之外**的 beforeEachRound，窗口里只跑被测那一趟。
            void PrepareRoots(int stages)
            {
                for (var i = 0; i < instances; i++)
                {
                    owners[i] = new Button();
                    var root = template!.LoadContent();
                    roots[i] = root;
                    if (root == null || stages == 0)
                    {
                        continue;
                    }

                    Control.SetTemplatedParentRecursive(root, owners[i]);
                    if (stages >= 2)
                    {
                        Control.PromoteTemplateLocalValuesRecursive(root);
                    }
                }
            }

            var loadContent = BenchmarkHarness.Measure(
                beforeEachRound: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        owners[i] = new Button();
                        roots[i] = null;
                    }
                },
                timedBody: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        roots[i] = template.LoadContent();
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            var setTemplatedParent = BenchmarkHarness.Measure(
                beforeEachRound: () => PrepareRoots(0),
                timedBody: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        if (roots[i] is { } root)
                        {
                            Control.SetTemplatedParentRecursive(root, owners[i]);
                        }
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            var promote = BenchmarkHarness.Measure(
                beforeEachRound: () => PrepareRoots(1),
                timedBody: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        if (roots[i] is { } root)
                        {
                            Control.PromoteTemplateLocalValuesRecursive(root);
                        }
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            var reactivate = BenchmarkHarness.Measure(
                beforeEachRound: () => PrepareRoots(2),
                timedBody: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        if (roots[i] is { } root)
                        {
                            Control.ReactivateBindingsRecursive(root);
                        }
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            // 完整展开：含 AddVisualChild → OnVisualParentChanged 全子树重算、触发器 Attach、
            // OnApplyTemplate、InvalidateMeasure。
            //
            // ★协议要点：Control.OnTemplateChanged（Control.cs:1016-1037）在 Template 变化时
            // **当场**调 ApplyTemplate()。所以对已展开的控件再调一次 ApplyTemplate() 只会撞上
            // _templateApplied==true 直接返回 false，测出来是空转。计时窗口外先把 Template
            // 清成 null，窗口内赋值触发恰好一次完整展开。
            var fullApply = BenchmarkHarness.Measure(
                beforeEachRound: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        var owner = new Button();
                        owner.Template = null;
                        owners[i] = owner;
                    }
                },
                timedBody: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        owners[i].Template = template;
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            double Per(double totalMs) => totalMs * 1000 / instances;

            _output.WriteLine(
                $"新建 Button 构造完毕时 Template 是否已就位: {(new Button().Template != null ? "是" : "否（主题模板要等隐式样式生效才装上）")}");
            _output.WriteLine($"Button 模板一份实例的可视元素数: {perInstanceElements}，每轮展开 {instances} 份");
            _output.WriteLine(string.Empty);
            _output.WriteLine(loadContent.Format("LoadContent"));
            _output.WriteLine(setTemplatedParent.Format("SetTemplatedParentRecursive"));
            _output.WriteLine(promote.Format("PromoteTemplateLocalValues"));
            _output.WriteLine(reactivate.Format("ReactivateBindingsRecursive"));
            _output.WriteLine(fullApply.Format("完整 ApplyTemplateCore"));
            _output.WriteLine(string.Empty);
            _output.WriteLine("每次模板展开的成本（us，各项独立测量、非相减）:");
            _output.WriteLine($"  LoadContent                 : {Per(loadContent.Best),8:F2}");
            _output.WriteLine($"  SetTemplatedParentRecursive : {Per(setTemplatedParent.Best),8:F2}   <- Control.cs:545");
            _output.WriteLine($"  PromoteTemplateLocalValues  : {Per(promote.Best),8:F2}   <- Control.cs:560");
            _output.WriteLine($"  ReactivateBindingsRecursive : {Per(reactivate.Best),8:F2}   <- Control.cs:564");
            _output.WriteLine($"  ---- 三趟递归合计            : {Per(setTemplatedParent.Best + promote.Best + reactivate.Best),8:F2}");
            _output.WriteLine($"  完整 ApplyTemplateCore       : {Per(fullApply.Best),8:F2}");
            _output.WriteLine(
                $"  其中三趟递归占比             : {(setTemplatedParent.Best + promote.Best + reactivate.Best) / fullApply.Best * 100,7:F1}%");
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// ★本轮优化的判据：模板展开期的「绑定重算去重」开 / 关，在**同一个进程里交替测量**。
    ///
    /// <para>为什么必须交替：这些基准的绝对值在两次进程运行之间能漂 30%（主题字典缓存冷热、
    /// GC 堆形态、JIT 分层都会变），而本优化的量级小于那个漂移。分别跑两个进程再比中位数，
    /// 得到的差值几乎全是漂移。交替测量让两条路径共享完全相同的进程状态，比值才可信。</para>
    ///
    /// <para>被测的重复是：一次模板展开里同一个元素的 ReactivateBindings 会被调三遍——
    /// SetTemplatedParentRecursive 一遍、AddVisualChild 触发的 OnVisualParentChanged 一遍、
    /// 末尾的 ReactivateBindingsRecursive 再一遍。后两遍对已激活的绑定只是重复 UpdateTarget()。</para>
    /// </summary>
    [Fact]
    public void TemplateExpansion_BindingReactivationDedup_IsCompared()
    {
        ResetApplicationState();
        var app = new Application();
        var originalDedup = DependencyObject.TemplateExpansionDedupEnabled;

        try
        {
            WriteHeader();

            var probeHost = new ContentControl();
            var probe = new Button { Content = "x" };
            probeHost.Content = probe;
            probe.ApplyTemplate();
            var template = probe.Template;
            Assert.NotNull(template);

            const int instances = 200;
            var owners = new Button[instances];

            void FreshOwners()
            {
                for (var i = 0; i < instances; i++)
                {
                    var owner = new Button();
                    owner.Template = null;
                    owners[i] = owner;
                }
            }

            BenchmarkHarness.Stats MeasureWith(bool dedup) => BenchmarkHarness.Measure(
                beforeEachRound: () =>
                {
                    DependencyObject.TemplateExpansionDedupEnabled = dedup;
                    FreshOwners();
                },
                timedBody: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        owners[i].Template = template;
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            // 交替三轮，取每轮的比值——单轮比值受进程状态影响仍有波动，三轮一致才作数。
            var ratios = new List<double>();
            for (var pass = 0; pass < 3; pass++)
            {
                var off = MeasureWith(dedup: false);
                var on = MeasureWith(dedup: true);
                ratios.Add(on.Best / off.Best);

                _output.WriteLine($"pass {pass + 1}:");
                _output.WriteLine("  " + off.Format("去重关（优化前）"));
                _output.WriteLine("  " + on.Format("去重开（优化后）"));
                _output.WriteLine(
                    $"  每次展开: {off.Best * 1000 / instances:F2} us -> {on.Best * 1000 / instances:F2} us   " +
                    $"({(1 - on.Best / off.Best) * 100:F1}% 更快)");
            }

            ratios.Sort();
            var median = ratios[ratios.Count / 2];
            _output.WriteLine(string.Empty);
            _output.WriteLine($"三轮比值: {string.Join(", ", ratios.Select(r => r.ToString("F3")))}");
            _output.WriteLine($"中位比值: {median:F3}x  =>  节省 {(1 - median) * 100:F1}%");

            // 只挡「去重把展开搞慢了」这种明显退化；具体收益看输出。
            Assert.True(
                median < 1.10d,
                $"开启绑定重算去重后模板展开反而慢了 {median:F2}x——剪枝判据可能失效或引入了额外开销。");
        }
        finally
        {
            DependencyObject.TemplateExpansionDedupEnabled = originalDedup;
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// ★量化主题模板里 <c>&lt;ContentPresenter.Resources&gt;</c> 这一块在**每次模板实例化**时的代价。
    ///
    /// <para>先查明主题 ControlTemplate 到底走 <see cref="FrameworkTemplate.LoadContent"/> 的哪条分支
    /// （编译期 factory / TemplateContent / 运行时重新解析 XAML），再回答两件事：
    /// (1) 每次实例化是否真的新建一本 ResourceDictionary（而不是共享）；
    /// (2) 这一块占一次实例化成本的多少。</para>
    ///
    /// <para>成本口径与机制无关：拿真实 Button 主题模板的 <c>LoadContent()</c> 作分母，
    /// 拿「重建一本等价资源字典（1 本字典 + 2 条 Style + 4 个 Setter）」作分子。</para>
    /// </summary>
    [Fact]
    public void TemplateLoadContent_LocalResourcesCost_IsAblated()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();

            // 主题 .jalxaml 是懒加载的：先把一个 Button 挂进树里逼隐式样式生效。
            var probeHost = new ContentControl();
            var probe = new Button { Content = "x" };
            probeHost.Content = probe;
            probe.ApplyTemplate();
            var template = probe.Template;
            Assert.NotNull(template);

            // ★查明 LoadContent 走哪条分支——FrameworkTemplate 的三个私有字段哪个非 null。
            static string DescribeBackingStore(FrameworkTemplate t)
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var type = typeof(FrameworkTemplate);
                var factory = type.GetField("_visualTreeFactory", flags)?.GetValue(t);
                var content = type.GetField("_template", flags)?.GetValue(t);
                var xaml = type.GetField("_visualTreeXaml", flags)?.GetValue(t) as string;

                if (factory != null) return "编译期 factory（源生成器 SetVisualTree）";
                if (content != null) return "TemplateContent（deferred loader 委托）";
                if (!string.IsNullOrEmpty(xaml)) return $"运行时重新解析 XAML（{xaml!.Length} 字符）";
                return "无内容";
            }

            _output.WriteLine($"Button 主题 ControlTemplate 的内容来源: {DescribeBackingStore(template!)}");
            _output.WriteLine($"ControlTemplate.XamlParser 是否已挂上: {ControlTemplate.XamlParser != null}");

            static ContentPresenter? FindPresenter(Visual root)
            {
                if (root is ContentPresenter presenter)
                {
                    return presenter;
                }

                for (var i = 0; i < root.VisualChildrenCount; i++)
                {
                    if (root.GetVisualChild(i) is Visual child && FindPresenter(child) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            }

            // ★「每实例重建」的直接证据：两次 LoadContent 得到的 presenter 是否共享同一本字典。
            var firstPresenter = FindPresenter(template!.LoadContent()!);
            var secondPresenter = FindPresenter(template.LoadContent()!);
            Assert.NotNull(firstPresenter);
            Assert.NotNull(secondPresenter);

            var firstResources = firstPresenter!.ResourcesOrNull;
            var secondResources = secondPresenter!.ResourcesOrNull;
            _output.WriteLine(
                $"presenter 是否带本地 Resources: {firstPresenter.HasResources} " +
                $"(条目数 {firstResources?.Count ?? 0})");
            _output.WriteLine(
                $"两次 LoadContent 的 Resources 是否同一实例: {ReferenceEquals(firstResources, secondResources)}");
            if (firstResources is { Count: > 0 } && secondResources is { Count: > 0 })
            {
                var key = firstResources.Keys.Cast<object>().First();
                _output.WriteLine(
                    $"字典里的 Style 对象是否同一实例: {ReferenceEquals(firstResources[key], secondResources[key])}");
            }

            const int instances = 200;
            var sink = new object?[instances];

            // 分母：真实主题模板的一次实例化。
            var wholeTemplate = BenchmarkHarness.Measure(
                timedBody: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        sink[i] = template.LoadContent();
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            // 分子：重建一本等价的资源字典（Button 模板里那块正是 1 本字典 + 2 条 Style + 4 个 Setter）。
            var resourceBlock = BenchmarkHarness.Measure(
                timedBody: () =>
                {
                    for (var i = 0; i < instances; i++)
                    {
                        var dictionary = new ResourceDictionary();

                        var textStyle = new Style(typeof(TextBlock));
                        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                        textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
                        dictionary[typeof(TextBlock)] = textStyle;

                        var accessStyle = new Style(typeof(AccessText));
                        accessStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                        accessStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
                        dictionary[typeof(AccessText)] = accessStyle;

                        sink[i] = dictionary;
                    }
                },
                warmupRounds: 3,
                minRounds: 11);

            double Per(BenchmarkHarness.Stats s) => s.Best * 1000 / instances;

            _output.WriteLine(string.Empty);
            _output.WriteLine(wholeTemplate.Format("真实 Button 模板 LoadContent"));
            _output.WriteLine(resourceBlock.Format("单独重建那本资源字典"));
            _output.WriteLine(string.Empty);
            _output.WriteLine($"一次实例化              : {Per(wholeTemplate),7:F2} us");
            _output.WriteLine($"其中资源字典重建        : {Per(resourceBlock),7:F2} us " +
                              $"({Per(resourceBlock) / Per(wholeTemplate) * 100:F0}%)");

            GC.KeepAlive(sink);
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// ★二阶效应：模板里的 <c>&lt;ContentPresenter.Resources&gt;</c> 让 presenter 变成一个
    /// 「带资源的祖先」，而 <c>FrameworkElement.RescopeSubtreeAfterAttach</c> 的剪枝判据是
    /// 「祖先链上无本地资源才可剪」（FrameworkElement.cs 里 chainHasResources 一旦为 true
    /// 就对整棵剩余子树保持 true）。于是 Button 的**内容子树**永远无法被剪枝。
    ///
    /// <para>本用例把两套形状完全相同、只差那一块 Resources 的模板装到 Button 上，各自挂一棵
    /// 内容子树，然后按源生成器的 bottom-up 顺序整页构建 + Measure，量这个差。</para>
    /// </summary>
    [Fact]
    public void ContentPresenterLocalResources_RescopeBlastRadius_IsCompared()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();

            // None        —— presenter 完全没有本地 Resources（对照基线）
            // MatchingStyles —— 真实主题模板的写法：两条按类型匹配的隐式 Style
            // UnrelatedKey —— 同样是「带资源的祖先」，但字典里只有一个匹配不到任何内容元素的键。
            //                 用它把「成为资源祖先」的固定代价，和「隐式样式真的命中并应用」
            //                 的功能性代价分开——后者是这块资源本来就该干的活，不算浪费。
            // mode 3 用的共享字典：只在这里填一次，之后每次展开都挂同一个实例。
            // 它仍然让 presenter 成为「带资源的祖先」，但不再每次展开都写条目，
            // 因此不会每次都 bump 全局 ResourceDictionary.ContentGeneration。
            var sharedDictionary = new ResourceDictionary
            {
                ["JaliumBenchUnrelatedKey"] =
                    new Jalium.UI.Media.SolidColorBrush(Jalium.UI.Media.Colors.Red),
            };

            ControlTemplate MakeButtonTemplate(int mode)
            {
                var template = new ControlTemplate(typeof(Button));
                template.SetVisualTree(() =>
                {
                    var presenter = new ContentPresenter();
                    if (mode == 3)
                    {
                        presenter.Resources = sharedDictionary;
                    }
                    else if (mode == 2)
                    {
                        var unrelated = new ResourceDictionary
                        {
                            ["JaliumBenchUnrelatedKey"] =
                                new Jalium.UI.Media.SolidColorBrush(Jalium.UI.Media.Colors.Red),
                        };
                        presenter.Resources = unrelated;
                    }
                    else if (mode == 1)
                    {
                        var dictionary = new ResourceDictionary();

                        var textStyle = new Style(typeof(TextBlock));
                        textStyle.Setters.Add(
                            new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                        textStyle.Setters.Add(
                            new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
                        dictionary[typeof(TextBlock)] = textStyle;

                        var accessStyle = new Style(typeof(AccessText));
                        accessStyle.Setters.Add(
                            new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                        accessStyle.Setters.Add(
                            new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
                        dictionary[typeof(AccessText)] = accessStyle;

                        presenter.Resources = dictionary;
                    }

                    return new Border { Child = presenter };
                });
                return template;
            }

            const int cards = 40;

            // bottom-up 构树：每张卡片里放一个 Button，Button 的 Content 是一棵可调大小的子树。
            static Border BuildPage(int cards, int contentLeaves, ControlTemplate buttonTemplate)
            {
                var stack = new StackPanel();
                for (var i = 0; i < cards; i++)
                {
                    var content = new StackPanel();
                    for (var k = 0; k < contentLeaves; k++)
                    {
                        content.Children.Add(new TextBlock { Text = "t" });
                    }

                    var button = new Button { Template = buttonTemplate, Content = content };
                    var grid = new Grid();
                    grid.Children.Add(new Border { Child = button });
                    stack.Children.Add(new Border { Child = grid });
                }

                return new Border { Child = stack };
            }

            var host = new ContentControl();

            BenchmarkHarness.Stats MeasurePage(int mode, int contentLeaves)
            {
                // 每轮换一份新模板实例，避免触发器/所有者状态跨轮累积。
                ControlTemplate? template = null;
                return BenchmarkHarness.Measure(
                    beforeEachRound: () => template = MakeButtonTemplate(mode),
                    timedBody: () =>
                    {
                        var page = BuildPage(cards, contentLeaves, template!);
                        host.Content = page;
                        page.Measure(new Size(1600, 1200));
                    },
                    warmupRounds: 3,
                    minRounds: 13);
            }

            // ★三次交替取比值：单次测量受进程状态影响能差 3 倍，交替后比值才稳。
            // 同时扫 Content 子树大小——若开销确实来自「presenter 之下每个元素的隐式样式查找
            // 走不了缓存」，那么净增量应当随内容元素数线性增长。
            _output.WriteLine($"每页 {cards} 个 Button；下列为每个 Button 相对「无本地 Resources」的净增量 (us)");
            _output.WriteLine(
                $"{"内容叶子数",10} {"每次新建字典",14} {"共享同一本字典",16} {"真实两条Style",16}");

            foreach (var leaves in new[] { 1, 4, 12 })
            {
                var perInstanceDeltas = new List<double>();
                var sharedDeltas = new List<double>();
                var styleDeltas = new List<double>();

                for (var pass = 0; pass < 3; pass++)
                {
                    var none = MeasurePage(0, leaves);
                    var perInstance = MeasurePage(2, leaves);
                    var shared = MeasurePage(3, leaves);
                    var styles = MeasurePage(1, leaves);

                    perInstanceDeltas.Add((perInstance.Best - none.Best) * 1000 / cards);
                    sharedDeltas.Add((shared.Best - none.Best) * 1000 / cards);
                    styleDeltas.Add((styles.Best - none.Best) * 1000 / cards);
                }

                perInstanceDeltas.Sort();
                sharedDeltas.Sort();
                styleDeltas.Sort();
                _output.WriteLine(
                    $"{leaves,10} {perInstanceDeltas[1],14:F1} {sharedDeltas[1],16:F1} {styleDeltas[1],16:F1}");
            }

            _output.WriteLine(string.Empty);
            _output.WriteLine("「每次新建字典」减「共享同一本字典」= 每次展开写条目触发的全局缓存失效代价；");
            _output.WriteLine("「共享同一本字典」本身 = 仅仅成为「带资源的祖先」的代价（剪枝失效 + 隐式样式缓存失效）。");
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// ★复杂度判据：模板**深度**对一次展开成本的影响。
    ///
    /// <para>背景：<c>PromoteTemplateLocalValuesRecursive</c> 与 <c>ReactivateBindingsRecursive</c>
    /// 除了遍历可视子节点，还额外对 <c>Border.Child</c> / <c>Panel.Children</c> /
    /// <c>ContentControl.Content</c> 各再走一遍——而这些在模板展开的这一刻**已经**是可视子节点
    /// 了。于是每下一层，同一个子树就被重复访问一次，整趟是 O(2^depth) 而不是 O(n)。
    /// （<c>SetTemplatedParentRecursive</c> 一直有环检测集合兜着，所以只有另外两趟中招。）</para>
    ///
    /// <para>本用例造一条深度可调的 Border 嵌套链当模板内容，开/关去重各测一次：
    /// 关掉时耗时应随深度指数上升，开启后应近似线性。</para>
    /// </summary>
    [Fact]
    public void TemplateExpansion_DepthScaling_IsCompared()
    {
        ResetApplicationState();
        var app = new Application();
        var originalDedup = DependencyObject.TemplateExpansionDedupEnabled;

        try
        {
            WriteHeader();

            // 深度 d 的模板内容：Border 套 Border ... 最内层放一个 ContentPresenter。
            // 元素总数 = d + 1，与深度同阶，因此若成本呈指数增长只可能来自重复访问。
            static ControlTemplate MakeChainTemplate(int depth)
            {
                var template = new ControlTemplate(typeof(Button));
                template.SetVisualTree(() =>
                {
                    FrameworkElement current = new ContentPresenter();
                    for (var i = 0; i < depth; i++)
                    {
                        current = new Border { Child = current };
                    }

                    return current;
                });
                return template;
            }

            const int instances = 40;
            var owners = new Button[instances];

            BenchmarkHarness.Stats MeasureDepth(int depth, bool dedup)
            {
                ControlTemplate? template = null;
                return BenchmarkHarness.Measure(
                    beforeEachRound: () =>
                    {
                        DependencyObject.TemplateExpansionDedupEnabled = dedup;
                        template = MakeChainTemplate(depth);
                        for (var i = 0; i < instances; i++)
                        {
                            var owner = new Button();
                            owner.Template = null;
                            owners[i] = owner;
                        }
                    },
                    timedBody: () =>
                    {
                        for (var i = 0; i < instances; i++)
                        {
                            owners[i].Template = template;
                        }
                    },
                    warmupRounds: 2,
                    minRounds: 9);
            }

            _output.WriteLine("每次模板展开的耗时 (us)，模板内容是 depth 层 Border 嵌套链 + 1 个 ContentPresenter:");
            _output.WriteLine($"{"depth",6} {"元素数",6} {"去重关",12} {"去重开",12} {"倍数",8}");

            foreach (var depth in new[] { 2, 4, 6, 8, 10 })
            {
                var off = MeasureDepth(depth, dedup: false);
                var on = MeasureDepth(depth, dedup: true);
                var offUs = off.Best * 1000 / instances;
                var onUs = on.Best * 1000 / instances;
                _output.WriteLine(
                    $"{depth,6} {depth + 1,6} {offUs,12:F1} {onUs,12:F1} {offUs / Math.Max(onUs, 0.0001),7:F1}x");
            }

            // 深模板下去重必须明显更快；否则说明环检测没有真的接上。
            var deepOff = MeasureDepth(10, dedup: false);
            var deepOn = MeasureDepth(10, dedup: true);
            Assert.True(
                deepOn.Best < deepOff.Best,
                $"depth=10 时去重没有带来收益（{deepOff.Best:F2} -> {deepOn.Best:F2} ms），环检测可能没接上。");
        }
        finally
        {
            DependencyObject.TemplateExpansionDedupEnabled = originalDedup;
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// ★特征消融：同样 2 个元素的模板形状，逐项加上模板真正会用到的特征，看每一项各自要多少钱。
    /// 阶段拆分（PhaseBreakdown）回答「哪一趟递归慢」，这个回答「慢在模板里的什么东西上」——
    /// 后者才决定该改哪行代码。
    ///
    /// <para>基线取自真实的 Button 主题模板：2 个元素（Border &gt; ContentPresenter）、
    /// 1 个 x:Name、7 处 {TemplateBinding}、ContentPresenter 上一本含 2 条 Style 的本地
    /// Resources、4 条模板触发器。</para>
    /// </summary>
    [Fact]
    public void TemplateExpansion_FeatureAblation_IsMeasured()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();

            const int instances = 200;
            var owners = new Button[instances];

            static ControlTemplate Make(
                Action<Border, ContentPresenter> configureTree,
                Action<ControlTemplate>? configureTemplate = null)
            {
                var template = new ControlTemplate(typeof(Button));
                template.SetVisualTree(() =>
                {
                    var presenter = new ContentPresenter();
                    var border = new Border { Child = presenter };
                    configureTree(border, presenter);
                    return border;
                });
                configureTemplate?.Invoke(template);
                return template;
            }

            BenchmarkHarness.Stats MeasureTemplate(Func<ControlTemplate> templateFactory)
            {
                ControlTemplate? template = null;
                return BenchmarkHarness.Measure(
                    beforeEachRound: () =>
                    {
                        // 每轮换一份新模板实例：触发器把 per-element 状态记在
                        // ConditionalWeakTable 里，共用一份模板会让状态表随轮数增长而污染测量。
                        template = templateFactory();
                        for (var i = 0; i < instances; i++)
                        {
                            var owner = new Button();
                            owner.Template = null;   // 清掉隐式样式已经展开的主题模板
                            owners[i] = owner;
                        }
                    },
                    timedBody: () =>
                    {
                        for (var i = 0; i < instances; i++)
                        {
                            owners[i].Template = template;
                        }
                    },
                    warmupRounds: 3,
                    minRounds: 11);
            }

            var bare = MeasureTemplate(() => Make(static (_, _) => { }));

            var named = MeasureTemplate(() => Make(static (border, _) => border.Name = "RootBorder"));

            var locals = MeasureTemplate(() => Make(static (border, _) =>
            {
                border.Background = new Jalium.UI.Media.SolidColorBrush(Jalium.UI.Media.Colors.White);
                border.BorderBrush = new Jalium.UI.Media.SolidColorBrush(Jalium.UI.Media.Colors.Gray);
                border.BorderThickness = new Thickness(1);
                border.CornerRadius = new CornerRadius(10);
                border.MinWidth = 60;
            }));

            var templateBindings = MeasureTemplate(() => Make(static (border, presenter) =>
            {
                border.SetTemplateBinding(Border.BackgroundProperty, Control.BackgroundProperty);
                border.SetTemplateBinding(Border.BorderBrushProperty, Control.BorderBrushProperty);
                border.SetTemplateBinding(Border.BorderThicknessProperty, Control.BorderThicknessProperty);
                border.SetTemplateBinding(Border.CornerRadiusProperty, Control.CornerRadiusProperty);
                presenter.SetTemplateBinding(FrameworkElement.MarginProperty, Control.PaddingProperty);
                presenter.SetTemplateBinding(
                    FrameworkElement.HorizontalAlignmentProperty, Control.HorizontalContentAlignmentProperty);
                presenter.SetTemplateBinding(
                    FrameworkElement.VerticalAlignmentProperty, Control.VerticalContentAlignmentProperty);
            }));

            var localResources = MeasureTemplate(() => Make(static (_, presenter) =>
            {
                var dictionary = new ResourceDictionary();
                var textStyle = new Style(typeof(TextBlock));
                textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
                dictionary[typeof(TextBlock)] = textStyle;
                presenter.Resources = dictionary;
            }));

            var triggers = MeasureTemplate(() => Make(
                static (border, _) => border.Name = "RootBorder",
                static template =>
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var multi = new MultiTrigger();
                        multi.Conditions.Add(new Condition(UIElement.IsMouseOverProperty, true));
                        multi.Conditions.Add(new Condition(UIElement.IsEnabledProperty, true));
                        multi.Setters.Add(new Setter(Border.BackgroundProperty,
                            new Jalium.UI.Media.SolidColorBrush(Jalium.UI.Media.Colors.Red))
                        { TargetName = "RootBorder" });
                        template.Triggers.Add(multi);
                    }

                    var single = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
                    single.Setters.Add(new Setter(Border.BorderBrushProperty,
                        new Jalium.UI.Media.SolidColorBrush(Jalium.UI.Media.Colors.Gray))
                    { TargetName = "RootBorder" });
                    template.Triggers.Add(single);
                }));

            // 真实主题模板：把上面所有特征叠在一起，用来校验各项之和是否解释得了总量。
            // ★新建的 Button 身上还没有 Template——主题模板要等隐式样式生效（挂进树里）才装上。
            var probeHost = new ContentControl();
            var probe = new Button { Content = "x" };
            probeHost.Content = probe;
            probe.ApplyTemplate();
            var realTemplate = probe.Template;
            Assert.NotNull(realTemplate);
            var real = MeasureTemplate(() => realTemplate!);

            double Per(BenchmarkHarness.Stats s) => s.Best * 1000 / instances;

            _output.WriteLine($"每轮 {instances} 次模板展开；下列为**每次展开**的耗时(us)");
            _output.WriteLine(string.Empty);
            _output.WriteLine($"  1 bare (Border>ContentPresenter)        : {Per(bare),7:F2}");
            _output.WriteLine($"  2 + x:Name                             : {Per(named),7:F2}   (+{Per(named) - Per(bare):F2})");
            _output.WriteLine($"  3 + 5 个字面量本地值                     : {Per(locals),7:F2}   (+{Per(locals) - Per(bare):F2})");
            _output.WriteLine($"  4 + 7 个 TemplateBinding                : {Per(templateBindings),7:F2}   (+{Per(templateBindings) - Per(bare):F2})");
            _output.WriteLine($"  5 + ContentPresenter 本地 Resources     : {Per(localResources),7:F2}   (+{Per(localResources) - Per(bare):F2})");
            _output.WriteLine($"  6 + 4 条模板触发器                       : {Per(triggers),7:F2}   (+{Per(triggers) - Per(bare):F2})");
            _output.WriteLine($"  7 真实 Button 主题模板（全部叠加）        : {Per(real),7:F2}");
            _output.WriteLine(string.Empty);
            _output.WriteLine(bare.Format("bare"));
            _output.WriteLine(named.Format("+name"));
            _output.WriteLine(locals.Format("+locals"));
            _output.WriteLine(templateBindings.Format("+templatebindings"));
            _output.WriteLine(localResources.Format("+resources"));
            _output.WriteLine(triggers.Format("+triggers"));
            _output.WriteLine(real.Format("real theme template"));

            GC.KeepAlive(probe);
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    private sealed class BenchViewModel
    {
        public string Title => "title";
        public string Description => "description";
    }

    /// <summary>
    /// 带绑定的树：真实页面里每个叶子多半都挂着 {Binding}，而 attach 期的
    /// ReactivateBindingsRecursive 对**已激活**的绑定不是跳过、而是跑完整的 UpdateTarget()。
    /// 用它量化绑定在换页成本里的占比。
    /// </summary>
    private static Border BuildBoundPageTree(int cardCount, object dataContext)
    {
        var stack = new StackPanel { DataContext = dataContext };

        for (var i = 0; i < cardCount; i++)
        {
            var texts = new StackPanel();

            var title = new TextBlock();
            title.SetBinding(TextBlock.TextProperty, new Binding("Title"));
            texts.Children.Add(title);

            var desc = new TextBlock();
            desc.SetBinding(TextBlock.TextProperty, new Binding("Description"));
            texts.Children.Add(desc);

            var tag = new TextBlock();
            tag.SetBinding(TextBlock.TextProperty, new Binding("Title"));
            texts.Children.Add(tag);

            var inner = new Border { Child = texts };
            var grid = new Grid();
            grid.Children.Add(inner);
            stack.Children.Add(new Border { Child = grid });
        }

        return new Border { Child = stack };
    }

    [Fact]
    public void PageSwap_WithBindings_IsMeasured()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();
            var host = new ContentControl();
            var vm = new BenchViewModel();

            var unbound = BenchmarkHarness.Measure(
                timedBody: () => host.Content = BuildPageTree(30),
                warmupRounds: 4,
                minRounds: 13);

            var bound = BenchmarkHarness.Measure(
                timedBody: () => host.Content = BuildBoundPageTree(30, vm),
                warmupRounds: 4,
                minRounds: 13);

            _output.WriteLine(unbound.Format("unbound (30 cards)"));
            _output.WriteLine(bound.Format("bound   (30 cards)"));
            _output.WriteLine(BenchmarkHarness.FormatRatio("binding overhead", unbound, bound));
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }

    /// <summary>
    /// 分离「纯构树」与「挂载到 host」两段成本，便于判断优化该落在哪一侧。
    /// </summary>
    [Fact]
    public void PageSwap_BuildVersusAttach_Breakdown()
    {
        ResetApplicationState();
        var app = new Application();

        try
        {
            WriteHeader();
            var host = new ContentControl();
            Border? pending = null;

            var build = BenchmarkHarness.Measure(
                timedBody: () => pending = BuildPageTree(30),
                warmupRounds: 4,
                minRounds: 13);

            // attach 那一步要的是「刚建好、尚未挂载」的树，所以构树放在计时窗口外。
            var attach = BenchmarkHarness.Measure(
                beforeEachRound: () => pending = BuildPageTree(30),
                timedBody: () => host.Content = pending,
                warmupRounds: 4,
                minRounds: 13);

            _output.WriteLine(build.Format("build (bottom-up 构树)"));
            _output.WriteLine(attach.Format("attach (整棵页面根挂到 host)"));

            Assert.True(build.Median < 400d);
            Assert.True(attach.Median < 400d);
        }
        finally
        {
            ResetApplicationState();
            GC.KeepAlive(app);
        }
    }
}
