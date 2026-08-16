using System.Diagnostics;
using System.Runtime;

namespace Jalium.UI.Tests;

/// <summary>
/// 性能基准的公共测量设施。
///
/// <para>存在的理由：先前的换页基准直接用「跑 5 轮取中位数」，实测样本在 126-214 ms 之间摆动
/// （相对离散度 ±25%），这种精度分辨不了 10% 量级的改动——改完根本判断不出是收益还是噪声。
/// 本类把三个主要噪声源逐个消掉：</para>
/// <list type="number">
///   <item>GC 落在测量窗口里。一次 Gen2 回收就是十几毫秒，直接把一个样本抬成离群点。
///         对策：每轮计时前在窗口外强制一次压缩式 Gen2 回收（让 Gen2 预算重新变满），
///         并在计时前后比对 <see cref="GC.CollectionCount(int)"/>，窗口内发生 Gen2 的样本判为污染样本剔除。</item>
///   <item>样本量太小。对策：采到「相对离散度达标」为止（自适应轮数），而不是固定 5 轮。</item>
///   <item>调度抖动。对策：测量期间抬高进程/线程优先级，结束后还原。</item>
/// </list>
///
/// <para>报告的比较口径是**中位数 + 半四分位距占比**。后者就是这套基准的分辨率：
/// 只有当两次测量的差值明显超过这个数，结论才成立。所有基准输出都会打印它。</para>
/// </summary>
internal static class BenchmarkHarness
{
    internal readonly record struct Stats(
        int CleanSamples,
        int DiscardedSamples,
        double Min,
        double P10,
        double P25,
        double Median,
        double P75,
        double Max,
        double[] AllSamples)
    {
        /// <summary>
        /// ★比较用的主统计量：第 10 百分位，而不是中位数。
        ///
        /// <para>实测这些基准里存在一个**双峰慢档**——同一段代码同一进程内，部分轮次会慢一倍以上
        /// （典型证据：median 15.52 ms 而 min 5.98 ms，且 p25/p75 分居两侧）。它来自框架自身的
        /// 进程级缓存周期性失效（如 ResourceLookup memo 的 4096 条硬上限被填满后整体清空），
        /// 出现频率取决于「缓存容量 vs 本轮工作量」，而不取决于被测改动。中位数会被这个慢档
        /// 拖着走，导致同一份代码在两次进程运行间就能差 30%，根本无法判断 10% 级的优化。</para>
        ///
        /// <para>取 P10 而非 Min：Min 只有一个样本、对单次幸运值敏感；P10 保留了「无干扰路径」
        /// 的语义同时还有一定统计量。两者都打印出来供交叉核对。</para>
        /// </summary>
        internal double Best => P10;

        /// <summary>下四分位区间占 P10 的比例——本次测量在「快档」内部的分辨率。</summary>
        internal double RelativeSpread => Best > 0 ? (P25 - Min) / Best : 0d;

        /// <summary>慢档相对快档的倍数。明显大于 1 表示样本双峰，此时中位数不可用于比较。</summary>
        internal double BimodalFactor => Best > 0 ? Median / Best : 1d;

        internal string Format(string label) =>
            $"{label,-34} P10 {Best,8:F2} ms  ±{RelativeSpread * 100,4:F1}%  " +
            $"[min {Min:F2} / p25 {P25:F2} / median {Median:F2} / p75 {P75:F2} / max {Max:F2}]  " +
            $"双峰x{BimodalFactor:F2}  n={CleanSamples}" +
            (DiscardedSamples > 0 ? $" (+{DiscardedSamples} GC-污染样本已剔除)" : string.Empty);
    }

    /// <summary>
    /// 采样直到相对离散度达标或轮数用尽。
    /// </summary>
    /// <param name="timedBody">被测量的动作。</param>
    /// <param name="beforeEachRound">每轮计时窗口**之外**的准备工作（构造 ViewModel、重置状态等）。</param>
    /// <param name="warmupRounds">预热轮数，不计入统计（JIT、主题字典解析、各级缓存首填）。</param>
    /// <param name="minRounds">最少采集的干净样本数。</param>
    /// <param name="maxRounds">最多尝试的轮数（含被剔除的污染样本）。</param>
    /// <param name="targetRelativeSpread">达到该相对离散度即可提前结束。</param>
    internal static Stats Measure(
        Action timedBody,
        Action? beforeEachRound = null,
        int warmupRounds = 5,
        int minRounds = 15,
        int maxRounds = 60,
        double targetRelativeSpread = 0.03d)
    {
        ArgumentNullException.ThrowIfNull(timedBody);

        for (var i = 0; i < warmupRounds; i++)
        {
            beforeEachRound?.Invoke();
            timedBody();
        }

        var previousLatency = GCSettings.LatencyMode;
        var thread = Thread.CurrentThread;
        var previousThreadPriority = thread.Priority;
        var process = Process.GetCurrentProcess();
        ProcessPriorityClass? previousProcessPriority = null;

        try
        {
            previousProcessPriority = process.PriorityClass;
            process.PriorityClass = ProcessPriorityClass.High;
        }
        catch
        {
            // 权限不足就算了——只是少一层降噪，不影响正确性。
        }

        try { thread.Priority = ThreadPriority.Highest; } catch { }
        try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; } catch { }

        var clean = new List<double>(minRounds);
        var contaminated = new List<double>();

        try
        {
            for (var round = 0; round < maxRounds; round++)
            {
                beforeEachRound?.Invoke();

                // 把 GC 赶出测量窗口：先在窗口外把该回收的都回收掉，Gen2 预算重置为满，
                // 于是紧接着的单轮测量落进 Gen2 的概率被压到很低。
                Collect();

                var gen2Before = GC.CollectionCount(2);
                var sw = Stopwatch.StartNew();
                timedBody();
                sw.Stop();
                var gen2After = GC.CollectionCount(2);

                var elapsed = sw.Elapsed.TotalMilliseconds;
                if (gen2After != gen2Before)
                {
                    contaminated.Add(elapsed);
                    continue;
                }

                clean.Add(elapsed);

                // 收敛判据看的是「快档内部」的离散度（P25 相对 P10），而不是含慢档的全域离散度——
                // 否则一旦出现双峰就永远收不敛，白跑满 maxRounds。
                if (clean.Count >= minRounds && Summarize(clean, contaminated.Count).RelativeSpread <= targetRelativeSpread)
                {
                    break;
                }
            }
        }
        finally
        {
            try { GCSettings.LatencyMode = previousLatency; } catch { }
            try { thread.Priority = previousThreadPriority; } catch { }
            if (previousProcessPriority is { } restore)
            {
                try { process.PriorityClass = restore; } catch { }
            }
        }

        // 极端情况：每一轮都触发了 Gen2（被测动作本身分配巨大）。此时剔除策略无从下手，
        // 退回到「全部样本」并如实报告，而不是拿零样本去做统计。
        if (clean.Count == 0)
        {
            clean.AddRange(contaminated);
            contaminated.Clear();
        }

        return Summarize(clean, contaminated.Count);
    }

    private static void Collect()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static Stats Summarize(List<double> samples, int discarded)
    {
        var sorted = samples.ToArray();
        Array.Sort(sorted);

        return new Stats(
            CleanSamples: sorted.Length,
            DiscardedSamples: discarded,
            Min: sorted[0],
            P10: Quantile(sorted, 0.10d),
            P25: Quantile(sorted, 0.25d),
            Median: Quantile(sorted, 0.50d),
            P75: Quantile(sorted, 0.75d),
            Max: sorted[^1],
            AllSamples: sorted);
    }

    /// <summary>线性插值分位数（样本量小时比「取第 n/2 个」稳定得多）。</summary>
    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = q * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    /// <summary>
    /// 两次测量的比值，并给出「这个比值是否可信」的判据：只有当差异超过两边分辨率之和时，
    /// 才能说方向性结论成立。
    /// </summary>
    internal static string FormatRatio(string label, Stats baseline, Stats candidate)
    {
        var ratio = baseline.Best > 0 ? candidate.Best / baseline.Best : 0d;
        var noiseFloor = baseline.RelativeSpread + candidate.RelativeSpread;
        var delta = Math.Abs(ratio - 1d);
        var verdict = delta > noiseFloor
            ? "显著"
            : $"落在噪声内（噪声下限 ±{noiseFloor * 100:F1}%），不可据此下结论";

        return $"{label}: {ratio:F2}x  ({verdict})";
    }
}
