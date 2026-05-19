using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace Jalium.UI.Controls;

/// <summary>
/// Aggregate counters + detailed phase/type/resource breakdown for jalxaml
/// deserialization, populated by <c>Jalium.UI.Markup.XamlReader</c> via
/// <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>
/// from Jalium.UI.Controls.csproj.
///
/// 两层数据:
///  - 基础聚合 <see cref="LoadCallCount"/> / <see cref="LoadTotalTicks"/> 永远开,
///    给 Window.Show 的 startup trace 摘要用,仅两个 Interlocked 增量,production 零成本。
///  - 详细分桶 (per-resource / per-type / per-phase) 由 <see cref="Enabled"/> 守门,
///    关闭时所有插桩点是一个 ldsfld + brfalse,JIT 会预测掉,没有任何分配或锁。
///    通过 <c>JALIUM_XAML_PROFILE=1</c> 或 <c>JALIUM_STARTUP_TRACE=1</c> 在进程启动时启用。
///
/// 启用后窗口完全打开 (Window.Show 走完 OnLoaded/OnContentRendered) 会调一次
/// <see cref="DumpTopN"/>,输出 Top-N 慢资源、Top-N 慢类型、各阶段累加耗时。
///
/// 位于 Jalium.UI.Controls (被 Jalium.UI.Xaml 引用,反向引用通过 InternalsVisibleTo)
/// 让 Window.cs 可以读取计数器而无需循环依赖。
/// </summary>
internal static class XamlLoadStartupTrace
{
    // === Basic aggregate counters (always-on, ~2 Interlocked increments per LoadComponent) ===
    internal static long LoadCallCount;
    internal static long LoadTotalTicks;

    // type-resolve cache 验证(临时诊断,后续可以摘掉)。XamlReader.ResolveType 累加。
    internal static long TypeCacheHits;
    internal static long TypeCacheMisses;

    // === Detailed profiling (gated by Enabled) ===
    internal static volatile bool Enabled;

    internal sealed class LoadRecord
    {
        public string ResourceName = "";
        public long TotalTicks;
        public int ElementCount;
    }

    internal sealed class TypeStat
    {
        public long Count;
        public long Ticks;
    }

    private static readonly ConcurrentBag<LoadRecord> s_records = new();
    private static readonly ConcurrentDictionary<string, TypeStat> s_typeStats = new();

    internal const int PhaseTypeResolve = 0;
    internal const int PhaseInstantiate = 1;
    internal const int PhaseParseAttributes = 2;
    internal const int PhaseTemplateDefer = 3;
    internal const int PhaseWireNamed = 4;
    internal const int PhaseSetterPostprocess = 5;
    internal const int PhaseCount = 6;

    private static readonly long[] s_phaseTicks = new long[PhaseCount];
    private static readonly string[] s_phaseNames = new[]
    {
        "type-resolve", "instantiate", "parse-attributes",
        "template-defer", "wire-named", "setter-postprocess",
    };

    // 单 LoadComponent 调用内 element 计数 — 走 thread-static,每个 InitializeComponent
    // 在自己线程上独立累加,不引入锁。XamlReader 会在 LoadComponentCore 入口/出口里
    // BeginLoadScope / EndLoadScope 一对调用,中间嵌套调用(模板 ParseAttributes 中
    // 可能再创对象)继续累加,出口拿到本次 load 的总元素数。
    [ThreadStatic] private static int t_currentLoadElements;
    [ThreadStatic] private static int t_loadScopeDepth;

    private static int s_dumped;

    static XamlLoadStartupTrace()
    {
        // 默认开启:每次 ParseElement 多 ~2 个 GetTimestamp + 1 个 dict lookup,
        // 在 ms 级的 LoadComponent 里占比 < 1%,production 下基本无感。
        // 显式关闭:JALIUM_XAML_PROFILE=0
        // 显式开启(强制):JALIUM_XAML_PROFILE=1 / JALIUM_STARTUP_TRACE=1
        Enabled = true;

        var profile = Environment.GetEnvironmentVariable("JALIUM_XAML_PROFILE");
        var startupTrace = Environment.GetEnvironmentVariable("JALIUM_STARTUP_TRACE");
        if (profile == "0")
        {
            Enabled = false;
        }
        else if (profile == "1" || startupTrace == "1")
        {
            Enabled = true;
        }
    }

    /// <summary>
    /// XamlReader.LoadComponentCore 入口调,清零本线程 element 计数器(只在最外层调用清零,
    /// 嵌套 LoadComponent 调用不会重置,以保证内层 element 也算到外层 record 上)。
    /// 返回的句柄需传给 EndLoadScope。
    /// </summary>
    internal static int BeginLoadScope()
    {
        if (!Enabled) return 0;
        var depth = ++t_loadScopeDepth;
        if (depth == 1)
        {
            t_currentLoadElements = 0;
        }
        return depth;
    }

    /// <summary>
    /// XamlReader.LoadComponentCore 出口调。当 depth==1 时(最外层退出)真正写一条 record。
    /// </summary>
    internal static void EndLoadScope(int depth, string resourceName, long totalTicks)
    {
        if (!Enabled) return;
        t_loadScopeDepth = Math.Max(0, t_loadScopeDepth - 1);
        if (depth != 1) return;

        s_records.Add(new LoadRecord
        {
            ResourceName = resourceName,
            TotalTicks = totalTicks,
            ElementCount = t_currentLoadElements,
        });
    }

    internal static void AddTypeInstantiate(Type type, long ticks)
    {
        if (!Enabled) return;
        var key = type.FullName ?? type.Name;
        var stat = s_typeStats.GetOrAdd(key, static _ => new TypeStat());
        Interlocked.Increment(ref stat.Count);
        Interlocked.Add(ref stat.Ticks, ticks);
        t_currentLoadElements++;
    }

    internal static void AddPhase(int phaseId, long ticks)
    {
        if (!Enabled) return;
        if ((uint)phaseId >= (uint)PhaseCount) return;
        Interlocked.Add(ref s_phaseTicks[phaseId], ticks);
    }

    /// <summary>
    /// 输出到 VS "输出 → 调试" 窗口 / DebugView (走 OutputDebugString)。
    /// GUI 应用没有 console，Trace 是默认可见渠道。
    /// 同时若环境变量 <c>JALIUM_STARTUP_TRACE_FILE</c> 指向可写文件，则附加一行到该文件 ——
    /// 这条出口让 Process.StartProcess(redirectStdErr) / 命令行测量脚本无 debugger
    /// attach 时也能拿到完整 phase 分解，用于回归性能基线对比。
    /// </summary>
    internal static void Emit(string line)
    {
        Trace.WriteLine(line);

        var traceFile = Environment.GetEnvironmentVariable("JALIUM_STARTUP_TRACE_FILE");
        if (!string.IsNullOrEmpty(traceFile))
        {
            try
            {
                File.AppendAllText(traceFile, line + Environment.NewLine);
            }
            catch
            {
                // Trace must never crash the app — file IO failure (locked, no perm,
                // disk full) silently degrades to debug-output-only.
            }
        }
    }

    /// <summary>
    /// 输出 Top-N 慢资源 / Top-N 慢类型 / 各阶段累加耗时。线程安全,每进程只输出一次。
    /// </summary>
    internal static void DumpTopN()
    {
        if (!Enabled) return;
        if (Interlocked.Exchange(ref s_dumped, 1) != 0) return;

        int topN = 10;
        if (int.TryParse(Environment.GetEnvironmentVariable("JALIUM_XAML_PROFILE_TOPN"), out var n) && n > 0)
        {
            topN = n;
        }

        static double TicksToMs(long t) => Stopwatch.GetElapsedTime(0, t).TotalMilliseconds;

        long aggregateMs = 0;
        for (int i = 0; i < PhaseCount; i++) aggregateMs += s_phaseTicks[i];

        long totalLoadTicks = Interlocked.Read(ref LoadTotalTicks);
        long totalLoadCalls = Interlocked.Read(ref LoadCallCount);

        Emit("[Jalium.UI xaml-profile] === XAML load profile ===");
        Emit(
            $"[Jalium.UI xaml-profile] {totalLoadCalls} LoadComponent calls, " +
            $"total {TicksToMs(totalLoadTicks):F1}ms wall, " +
            $"{TicksToMs(aggregateMs):F1}ms in instrumented phases");

        // type-resolve cache 验证(临时诊断)
        long hits = Interlocked.Read(ref TypeCacheHits);
        long misses = Interlocked.Read(ref TypeCacheMisses);
        long total = hits + misses;
        double hitPct = total > 0 ? (hits * 100.0 / total) : 0;
        Emit(
            $"[Jalium.UI xaml-profile] type-cache: {hits} hits / {misses} misses " +
            $"({hitPct:F1}% hit rate, total {total} ResolveType calls)");

        // Phases (绝对时间 + 相对占比)
        Emit("[Jalium.UI xaml-profile] phase breakdown:");
        double totalPhaseMs = TicksToMs(aggregateMs);
        for (int i = 0; i < PhaseCount; i++)
        {
            var phaseMs = TicksToMs(s_phaseTicks[i]);
            var pct = totalPhaseMs > 0 ? (phaseMs * 100.0 / totalPhaseMs) : 0;
            Emit($"[Jalium.UI xaml-profile]   {s_phaseNames[i],-20} {phaseMs,8:F1}ms  ({pct,5:F1}%)");
        }

        // Top resources
        var resources = s_records.ToArray();
        Array.Sort(resources, static (a, b) => b.TotalTicks.CompareTo(a.TotalTicks));
        var resourcesShown = Math.Min(topN, resources.Length);
        Emit($"[Jalium.UI xaml-profile] top {resourcesShown} slowest LoadComponent calls (out of {resources.Length}):");
        for (int i = 0; i < resourcesShown; i++)
        {
            var rec = resources[i];
            Emit(
                $"[Jalium.UI xaml-profile]   {TicksToMs(rec.TotalTicks),8:F1}ms  " +
                $"({rec.ElementCount,4} elements)  {rec.ResourceName}");
        }

        // Top types (cumulative instantiate ticks)
        var types = new List<KeyValuePair<string, TypeStat>>(s_typeStats);
        types.Sort(static (a, b) => b.Value.Ticks.CompareTo(a.Value.Ticks));
        var typesShown = Math.Min(topN, types.Count);
        Emit($"[Jalium.UI xaml-profile] top {typesShown} most-expensive types (cumulative Activator.CreateInstance):");
        for (int i = 0; i < typesShown; i++)
        {
            var kvp = types[i];
            var ms = TicksToMs(kvp.Value.Ticks);
            var avgUs = kvp.Value.Count > 0 ? (ms * 1000.0 / kvp.Value.Count) : 0;
            Emit(
                $"[Jalium.UI xaml-profile]   {ms,8:F1}ms  ({kvp.Value.Count,5}x, avg {avgUs,7:F1}us)  {kvp.Key}");
        }

        Emit("[Jalium.UI xaml-profile] === end ===");
    }
}
