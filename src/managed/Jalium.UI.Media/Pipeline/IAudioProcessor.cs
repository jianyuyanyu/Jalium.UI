namespace Jalium.UI.Media.Pipeline;

/// <summary>
/// PCM 后处理器(time-stretch、pitch-shift、EQ 等)的统一接口。<see cref="AudioPlayer"/>
/// 在 pump 循环中把 decoder 输出喂给 processor,再把 processor 输出送进
/// <see cref="INativeAudioDevice"/>。
/// </summary>
/// <remarks>
/// <para><b>线程</b>:同一实例只允许 pump worker 一根线程访问(对齐
/// <see cref="INativeAudioDecoder"/> 的 single-threaded 契约)。</para>
/// <para><b>输入/输出长度关系</b>:processor 内部 stateful,Process 一次喂入 src 不
/// 一定立刻产出等量(或等比例)输出 —— 它会先把不够一帧的输入缓存,凑够帧再产出。
/// EOS 时调用 <see cref="Drain"/> 把内部 buffer 倾倒干净。</para>
/// <para><b>dst 容量</b>:实现侧承诺产出量不超过 <c>src.Length * ceil(1 / minSpeed)</c>
/// (典型场景 SpeedRatio &gt;= 0.1,所以 caller 备 src.Length * 10 足够)。</para>
/// </remarks>
public interface IAudioProcessor
{
    /// <summary>当前播放倍速;1.0 = 原速,&gt; 1 加速,&lt; 1 减速。pitch 保持不变。</summary>
    double SpeedRatio { get; set; }

    /// <summary>
    /// 把 <paramref name="src"/> 全部喂入内部 buffer,把当前可输出的 PCM 写到 <paramref name="dst"/>。
    /// 返回写入 <paramref name="dst"/> 的 sample 数(interleaved 计;0 表示 buffer 还在填充)。
    /// </summary>
    int Process(System.ReadOnlySpan<float> src, System.Span<float> dst);

    /// <summary>EOS 后调,把内部残留 buffer 排空到 <paramref name="dst"/>。返回写入样本数。</summary>
    int Drain(System.Span<float> dst);

    /// <summary>Seek / Stop / 速率剧变后调,清空 buffer 与 OLA 尾。</summary>
    void Reset();
}
