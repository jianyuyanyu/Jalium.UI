using System;

namespace Jalium.UI.Media.Pipeline;

/// <summary>
/// SOLA(Synchronous Overlap-Add)+ 互相关搜索的 time-stretcher。让音频按
/// <see cref="IAudioProcessor.SpeedRatio"/> 加速/减速,**保持音高不变**。
/// </summary>
/// <remarks>
/// <para>
/// 算法摘要(Roucos &amp; Wilgus 1985,业界俗称 WSOLA = "weighted similarity overlap-add"):
/// <list type="number">
///   <item>把输入流切成长度 F 的帧,相邻帧重叠 O = F/2。</item>
///   <item>每次产出新帧之前,在输入 buffer 中以 SpeedRatio 决定的步长附近
///         (± searchRange)做互相关搜索,找出与上一帧 tail 最匹配的位置 ——
///         这一步消除 OLA 的相位 cancel。</item>
///   <item>把"对齐后的新帧 head" 与 "上一帧 tail" 用 Hann² 窗(w_rise² + w_fall² ≡ 1)做 OLA,
///         得到 O 个 finalized 样本写入输出。</item>
///   <item>新帧 [O..F] 区段成为下次 OLA 的 tail。</item>
/// </list>
/// 输出步长 hopOut = O 与 SpeedRatio 无关;输入步长 hopIn = round(hopOut * SpeedRatio),
/// 因此 SpeedRatio &gt; 1 加速、&lt; 1 减速。 SpeedRatio 在调用之间可变。
/// </para>
/// <para>
/// 帧长按采样率自适应取 ~23 ms(2 的整次幂便于 SIMD 后续优化),
/// search range 取 ~6 ms。这套参数沿用业界经验值,在 0.5–2.0 倍速区间内
/// 主观伪影较少。
/// </para>
/// </remarks>
public sealed class WsolaSpeedProcessor : IAudioProcessor
{
    private const double MinSpeed = 0.1;
    private const double MaxSpeed = 10.0;

    private readonly int _channels;
    private readonly int _frameLen;        // F: per-channel samples in one analysis frame
    private readonly int _overlapLen;      // O: F / 2
    private readonly int _hopOut;          // = F - O = O (50% overlap)
    private readonly int _searchRange;     // ± per-channel samples around nominal hopIn

    private readonly float[] _windowRise;  // Hann² rising,长 _overlapLen
    private readonly float[] _windowFall;  // Hann² falling
    private readonly float[] _tail;        // length: _overlapLen * channels
    private bool _tailInitialized;

    // 输入累积 buffer。简单的 List<float>;典型一次循环只装几十毫秒数据,
    // 不会发生 large allocation。未来若成为热点可换 ring buffer + SIMD。
    private readonly System.Collections.Generic.List<float> _input = new();

    private double _speedRatio = 1.0;

    /// <summary>构造,绑定固定的 <paramref name="sampleRate"/> 与 <paramref name="channels"/>。</summary>
    public WsolaSpeedProcessor(int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0)   throw new ArgumentOutOfRangeException(nameof(channels));

        _channels = channels;
        _frameLen = ChooseFrameLen(sampleRate);
        _overlapLen = _frameLen / 2;
        _hopOut = _frameLen - _overlapLen;          // = _overlapLen (50% overlap)
        _searchRange = Math.Max(8, _frameLen / 8);  // ~6 ms @ 44.1 kHz w/ frameLen=1024

        _windowRise = new float[_overlapLen];
        _windowFall = new float[_overlapLen];
        for (int i = 0; i < _overlapLen; i++)
        {
            // w_rise = sin²(π/2 · i/O), w_fall = cos²(π/2 · i/O), and w_rise + w_fall ≡ 1.
            double a = Math.Sin(Math.PI * 0.5 * i / _overlapLen);
            double b = Math.Cos(Math.PI * 0.5 * i / _overlapLen);
            _windowRise[i] = (float)(a * a);
            _windowFall[i] = (float)(b * b);
        }

        _tail = new float[_overlapLen * channels];
    }

    /// <inheritdoc />
    public double SpeedRatio
    {
        get => _speedRatio;
        set => _speedRatio = Math.Clamp(value, MinSpeed, MaxSpeed);
    }

    /// <summary>当前内部累积的输入帧数(per channel),诊断用。</summary>
    public int BufferedFrames => _input.Count / _channels;

    /// <inheritdoc />
    public int Process(ReadOnlySpan<float> src, Span<float> dst)
    {
        // 1.0x passthrough:速比恰好 1 时省去整个 SOLA 流程,直接拷贝。
        if (Math.Abs(_speedRatio - 1.0) < 1e-6 && !_tailInitialized && _input.Count == 0)
        {
            int n = Math.Min(src.Length, dst.Length);
            src[..n].CopyTo(dst);
            return n;
        }

        // 把 src 累积进 _input。List<float>.AddRange 接 Span 没有重载,手动循环。
        if (!src.IsEmpty)
        {
            _input.EnsureCapacity(_input.Count + src.Length);
            for (int i = 0; i < src.Length; i++) _input.Add(src[i]);
        }

        int written = 0;
        int hopIn = Math.Max(1, (int)Math.Round(_hopOut * _speedRatio));
        int needFrames = hopIn + _searchRange + _frameLen; // per-channel samples required to read one frame
        int needSamples = needFrames * _channels;
        int hopOutSamples = _hopOut * _channels;

        while (_input.Count >= needSamples && written + hopOutSamples <= dst.Length)
        {
            int bestOffset = FindBestOffset(hopIn);
            int frameStart = (hopIn + bestOffset) * _channels;

            EmitFrame(frameStart, dst.Slice(written, hopOutSamples));
            written += hopOutSamples;

            // 消耗 hopIn + bestOffset 帧。bestOffset 可能负,前序保证 hopIn + bestOffset >= 0。
            int consume = (hopIn + bestOffset) * _channels;
            _input.RemoveRange(0, consume);
        }

        return written;
    }

    /// <inheritdoc />
    public int Drain(Span<float> dst)
    {
        // 排空时把 _tail 当成最后一帧的"尾巴"直接吐出去,不再 search 不再 OLA。
        if (!_tailInitialized) return 0;
        int n = Math.Min(_tail.Length, dst.Length);
        new ReadOnlySpan<float>(_tail, 0, n).CopyTo(dst);
        _tailInitialized = false;
        Array.Clear(_tail, 0, _tail.Length);
        return n;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _input.Clear();
        Array.Clear(_tail, 0, _tail.Length);
        _tailInitialized = false;
    }

    // -----------------------------------------------------------------------
    // 内部:互相关搜索 + OLA 帧合成
    // -----------------------------------------------------------------------

    private int FindBestOffset(int hopIn)
    {
        // 第一帧没 tail 可参考,直接走 nominal hop。
        if (!_tailInitialized) return 0;

        int bestOff = 0;
        float bestCorr = float.NegativeInfinity;

        // 收紧 search 范围,避免索引越界。
        int minOff = -Math.Min(_searchRange, hopIn);  // hopIn + off >= 0
        int maxOff = _searchRange;

        // 互相关只对第一个声道做(_tail 与 input 都按声道交错排列,只看 ch=0
        // 已经足够定位相位 —— 多声道相位通常一致或近一致)。
        for (int off = minOff; off <= maxOff; off++)
        {
            int startSample = (hopIn + off) * _channels;
            int endSample = startSample + _overlapLen * _channels;
            if (startSample < 0 || endSample > _input.Count) continue;

            float corr = 0f;
            for (int i = 0; i < _overlapLen; i++)
            {
                int idx = i * _channels; // ch == 0
                corr += _tail[idx] * _input[startSample + idx];
            }
            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestOff = off;
            }
        }

        return bestOff;
    }

    private void EmitFrame(int frameStart, Span<float> dst)
    {
        // 1) OLA 前 _overlapLen 个样本与 _tail。
        if (_tailInitialized)
        {
            for (int i = 0; i < _overlapLen; i++)
            {
                float wRise = _windowRise[i];
                float wFall = _windowFall[i];
                int dstBase = i * _channels;
                int srcBase = frameStart + dstBase;
                for (int ch = 0; ch < _channels; ch++)
                {
                    dst[dstBase + ch] =
                        _tail[dstBase + ch] * wFall + _input[srcBase + ch] * wRise;
                }
            }
        }
        else
        {
            // 第一帧:_tail 还没填,直接把帧头拷贝出去(等价于 fade-from-self,无静音)。
            int n = _overlapLen * _channels;
            for (int i = 0; i < n; i++) dst[i] = _input[frameStart + i];
        }

        // 2) 把帧的 [_overlapLen .. _frameLen] 区段存进 _tail,留给下次 OLA。
        int tailStartInInput = frameStart + _overlapLen * _channels;
        int tailLen = _overlapLen * _channels;
        for (int i = 0; i < tailLen; i++)
        {
            _tail[i] = _input[tailStartInInput + i];
        }
        _tailInitialized = true;
    }

    // -----------------------------------------------------------------------
    // 参数选择
    // -----------------------------------------------------------------------

    private static int ChooseFrameLen(int sampleRate)
    {
        // 目标 ~23 ms;选最接近的 2^n 以便后续 SIMD/对齐。
        // 44.1k -> 1024  (~23.2 ms)
        // 48k   -> 1024  (~21.3 ms)
        // 22.05k-> 512   (~23.2 ms)
        // 96k   -> 2048  (~21.3 ms)
        double target = sampleRate * 0.023;
        int len = 1;
        while (len * 2 < target) len <<= 1;
        if (len < 256) len = 256;
        if (len > 8192) len = 8192;
        return len;
    }
}
