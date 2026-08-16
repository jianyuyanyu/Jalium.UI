using Jalium.UI;

namespace Jalium.UI.Tests;

/// <summary>
/// 钉死 <see cref="ResourceDictionary"/> 合并子树查找缓存（含 negative 结果）的不变式。
///
/// <para>该缓存的存在意义是性能：隐式样式查找绝大多数是穿透整棵 merged/theme 字典树的全
/// miss，而整页视图替换时这条路径按 元素数 x 类型继承链长度 反复走。但缓存一旦失效不及时
/// 就会变成"改了资源却不生效""切了主题却不换色"这类极难定位的问题，所以每条失效路径都必须
/// 有测试钉住。</para>
///
/// <para><see cref="ResourceDictionary.CurrentThemeKey"/> 是进程级静态状态，故与 Application
/// 测试同集合串行执行，避免并行用例互相污染主题键。</para>
/// </summary>
[Collection("Application")]
public class MergedLookupCacheInvariantTests
{
    [Fact]
    public void NegativeResult_IsInvalidated_WhenMergedChildGainsKey()
    {
        var root = new ResourceDictionary();
        var merged = new ResourceDictionary();
        root.MergedDictionaries.Add(merged);

        // 先查一次形成 negative 缓存条目。
        Assert.False(root.TryGetValue("late-arrival", out _));

        merged.Add("late-arrival", "value");

        Assert.True(root.TryGetValue("late-arrival", out var value));
        Assert.Equal("value", value);
    }

    [Fact]
    public void PositiveResult_IsInvalidated_WhenMergedChildLosesKey()
    {
        var root = new ResourceDictionary();
        var merged = new ResourceDictionary();
        merged.Add("doomed", "value");
        root.MergedDictionaries.Add(merged);

        Assert.True(root.TryGetValue("doomed", out var value));
        Assert.Equal("value", value);

        merged.Remove("doomed");

        Assert.False(root.TryGetValue("doomed", out _));
    }

    [Fact]
    public void PositiveResult_IsInvalidated_WhenMergedChildValueIsReplaced()
    {
        var root = new ResourceDictionary();
        var merged = new ResourceDictionary();
        merged.Add("accent", "before");
        root.MergedDictionaries.Add(merged);

        Assert.True(root.TryGetValue("accent", out var before));
        Assert.Equal("before", before);

        merged["accent"] = "after";

        Assert.True(root.TryGetValue("accent", out var after));
        Assert.Equal("after", after);
    }

    [Fact]
    public void NegativeResult_IsInvalidated_WhenMergedDictionaryIsAdded()
    {
        var root = new ResourceDictionary();
        Assert.False(root.TryGetValue("from-newcomer", out _));

        var newcomer = new ResourceDictionary();
        newcomer.Add("from-newcomer", "value");
        root.MergedDictionaries.Add(newcomer);

        Assert.True(root.TryGetValue("from-newcomer", out var value));
        Assert.Equal("value", value);
    }

    [Fact]
    public void PositiveResult_IsInvalidated_WhenMergedDictionaryIsRemoved()
    {
        var root = new ResourceDictionary();
        var merged = new ResourceDictionary();
        merged.Add("departing", "value");
        root.MergedDictionaries.Add(merged);

        Assert.True(root.TryGetValue("departing", out _));

        root.MergedDictionaries.Remove(merged);

        Assert.False(root.TryGetValue("departing", out _));
    }

    /// <summary>
    /// 后加入的 merged dictionary 覆盖先加入的（倒序遍历语义）。缓存不得把先前的胜者钉死。
    /// </summary>
    [Fact]
    public void LaterMergedDictionary_WinsAfterBeingAdded()
    {
        var root = new ResourceDictionary();
        var first = new ResourceDictionary();
        first.Add("brush", "first");
        root.MergedDictionaries.Add(first);

        Assert.True(root.TryGetValue("brush", out var initial));
        Assert.Equal("first", initial);

        var second = new ResourceDictionary();
        second.Add("brush", "second");
        root.MergedDictionaries.Add(second);

        Assert.True(root.TryGetValue("brush", out var overridden));
        Assert.Equal("second", overridden);
    }

    /// <summary>
    /// 切换 <see cref="ResourceDictionary.CurrentThemeKey"/> 必须让缓存失效。
    /// 若 setter 漏掉失效调用，本用例会拿到上一个主题的值——正是"切了主题却不换色"的复现。
    /// </summary>
    [Fact]
    public void ThemeKeySwitch_Invalidates_CachedThemeLookup()
    {
        var root = new ResourceDictionary();

        var light = new ResourceDictionary();
        light.Add("accent", "light-accent");
        var dark = new ResourceDictionary();
        dark.Add("accent", "dark-accent");

        root.ThemeDictionaries["MergedLookupLight"] = light;
        root.ThemeDictionaries["MergedLookupDark"] = dark;

        var original = ResourceDictionary.CurrentThemeKey;
        try
        {
            ResourceDictionary.CurrentThemeKey = "MergedLookupLight";
            Assert.True(root.TryGetValue("accent", out var lightValue));
            Assert.Equal("light-accent", lightValue);

            ResourceDictionary.CurrentThemeKey = "MergedLookupDark";
            Assert.True(root.TryGetValue("accent", out var darkValue));
            Assert.Equal("dark-accent", darkValue);
        }
        finally
        {
            ResourceDictionary.CurrentThemeKey = original;
        }
    }

    /// <summary>
    /// theme dictionary 优先于 merged dictionary，且该优先级不被缓存打乱。
    /// </summary>
    [Fact]
    public void ThemeDictionary_TakesPrecedenceOverMerged_AcrossRepeatedLookups()
    {
        var root = new ResourceDictionary();

        var merged = new ResourceDictionary();
        merged.Add("token", "from-merged");
        root.MergedDictionaries.Add(merged);

        var themed = new ResourceDictionary();
        themed.Add("token", "from-theme");
        root.ThemeDictionaries["MergedLookupPrecedence"] = themed;

        var original = ResourceDictionary.CurrentThemeKey;
        try
        {
            ResourceDictionary.CurrentThemeKey = "MergedLookupPrecedence";

            Assert.True(root.TryGetValue("token", out var first));
            Assert.Equal("from-theme", first);

            // 第二次走缓存，结果必须一致。
            Assert.True(root.TryGetValue("token", out var second));
            Assert.Equal("from-theme", second);
        }
        finally
        {
            ResourceDictionary.CurrentThemeKey = original;
        }
    }

    /// <summary>
    /// 中间层字典被顶层查找「路过」之后，它自己的独立查询结果必须仍然正确。
    ///
    /// <para>这条对应缓存实现里「只在顶层查找（环检测链为空）时读写缓存」的限制：嵌套查找的
    /// 结果是上下文相关的，不能当作该字典的独立结果记下来。</para>
    /// </summary>
    [Fact]
    public void MiddleDictionary_KeepsCorrectResult_AfterBeingTraversedByRoot()
    {
        var root = new ResourceDictionary();
        var mid = new ResourceDictionary();
        var leaf = new ResourceDictionary();

        leaf.Add("shared", "from-leaf");
        root.MergedDictionaries.Add(mid);
        mid.MergedDictionaries.Add(leaf);

        // 顶层查 root：root → mid（嵌套）→ leaf 命中。
        Assert.True(root.TryGetValue("shared", out var viaRoot));
        Assert.Equal("from-leaf", viaRoot);

        // mid 刚才是以嵌套身份被走过的；它自己的独立查询必须同样命中。
        Assert.True(mid.TryGetValue("shared", out var viaMid));
        Assert.Equal("from-leaf", viaMid);

        // 反向顺序同样成立（先独立查 mid 形成其自身缓存，再查 root）。
        var root2 = new ResourceDictionary();
        var mid2 = new ResourceDictionary();
        var leaf2 = new ResourceDictionary();
        leaf2.Add("shared2", "from-leaf2");
        root2.MergedDictionaries.Add(mid2);
        mid2.MergedDictionaries.Add(leaf2);

        Assert.True(mid2.TryGetValue("shared2", out var midFirst));
        Assert.Equal("from-leaf2", midFirst);
        Assert.True(root2.TryGetValue("shared2", out var rootSecond));
        Assert.Equal("from-leaf2", rootSecond);
    }

    // 注：循环 merged dictionary（含直接自引用 root.Add(root) 与间接环 A→B→A）这里刻意不覆盖——
    // 它在**建环的那一刻**就栈溢出，与查找缓存无关：OnMergedDictionaryAdded 让两本字典互相订阅
    // Changed，紧接着的 OnChanged → RaiseChanged → Changed?.Invoke → OnMergedDictionaryChanged
    // → OnChanged 无限往返。查找路径有 t_lookupChain 环检测，变更通知路径没有对应保护——这个
    // 不对称是独立的预存缺陷，需单独修。也正因为环在当前框架下根本无法构造，缓存实现里
    // 「只在顶层读写」的限制在实践中不可达，但仍作为防御保留：它是零成本的（顶层就是热路径）。

    /// <summary>
    /// 本地条目优先于 merged，且本地条目后加入时能立刻胜出。
    /// </summary>
    [Fact]
    public void LocalEntry_OverridesMerged_AfterBeingAdded()
    {
        var root = new ResourceDictionary();
        var merged = new ResourceDictionary();
        merged.Add("token", "from-merged");
        root.MergedDictionaries.Add(merged);

        Assert.True(root.TryGetValue("token", out var fromMerged));
        Assert.Equal("from-merged", fromMerged);

        root.Add("token", "from-local");

        Assert.True(root.TryGetValue("token", out var fromLocal));
        Assert.Equal("from-local", fromLocal);
    }

    /// <summary>
    /// 深层嵌套（root → mid → leaf）的变更同样要穿透失效。
    /// </summary>
    [Fact]
    public void DeeplyNestedChange_IsVisibleThroughRoot()
    {
        var root = new ResourceDictionary();
        var mid = new ResourceDictionary();
        var leaf = new ResourceDictionary();

        root.MergedDictionaries.Add(mid);
        mid.MergedDictionaries.Add(leaf);

        Assert.False(root.TryGetValue("deep", out _));

        leaf.Add("deep", "value");

        Assert.True(root.TryGetValue("deep", out var value));
        Assert.Equal("value", value);
    }

    /// <summary>
    /// Clear() 必须让此前缓存的命中结果失效。
    /// </summary>
    [Fact]
    public void Clear_InvalidatesCachedHit()
    {
        var root = new ResourceDictionary();
        var merged = new ResourceDictionary();
        merged.Add("token", "value");
        root.MergedDictionaries.Add(merged);

        Assert.True(root.TryGetValue("token", out _));

        merged.Clear();

        Assert.False(root.TryGetValue("token", out _));
    }
}
