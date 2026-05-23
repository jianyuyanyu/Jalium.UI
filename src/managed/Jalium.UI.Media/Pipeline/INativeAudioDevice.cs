namespace Jalium.UI.Media.Pipeline;

/// <summary>
/// 平台原生音频输出设备抽象。v1 由 miniaudio 单后端实现（Windows WASAPI /
/// Linux ALSA+PulseAudio / macOS CoreAudio / iOS AVAudioSession /
/// Android AAudio+OpenSL ES），跨平台保持同一份调用方契约。
/// </summary>
/// <remarks>
/// <para>
/// <b>输出格式</b>：interleaved 32-bit float。采样率与声道数在构造时通过
/// <see cref="INativeAudioDeviceFactory.Create"/> 指定，<see cref="AudioPlayer"/>
/// 当前固定 44.1 kHz / 2 ch（与旧 SoundFlow 契约一致）。
/// </para>
/// <para>
/// <b>线程模型</b>：
/// <list type="bullet">
///   <item><c>Start</c>/<c>Stop</c>/<c>Submit</c>/<c>Flush</c>/<c>SetVolume</c>
///   /<c>SignalEndOfStream</c>/<c>Position</c> 从任意非音频回调线程调用安全；
///   <c>Submit</c> 走 lock-free ring，不阻塞音频回调。</item>
///   <item><see cref="PlaybackEnded"/> 由 native 端的 miniaudio data-callback
///   线程触发；事件订阅者必须立即跳离回调线程（实现侧通常已 marshal 到
///   ThreadPool，但订阅者仍应避免长任务）。</item>
/// </list>
/// </para>
/// </remarks>
public interface INativeAudioDevice : IDisposable
{
    /// <summary>启动音频回调（开始消费 ring 中的 PCM）。</summary>
    void Start();

    /// <summary>停止音频回调，但保留 ring 内容；再次 <see cref="Start"/> 从同一位置继续。</summary>
    void Stop();

    /// <summary>
    /// 把 interleaved float PCM 复制进 ring。返回实际接受的帧数；当 ring 已满时
    /// 返回值可能小于 <c>pcm.Length / channels</c>，调用方需要保留余下帧、稍后重试。
    /// </summary>
    /// <param name="pcm">interleaved float 样本，长度须为声道数的整数倍。</param>
    /// <returns>已写入 ring 的帧数。</returns>
    int Submit(System.ReadOnlySpan<float> pcm);

    /// <summary>
    /// 声明此后不再 <see cref="Submit"/>。下次 ring 排空时 <see cref="PlaybackEnded"/>
    /// 触发一次；在此之前 ring 短暂为空只视为 underrun。再次 <c>Submit</c> 后该标记
    /// 自动清除。
    /// </summary>
    void SignalEndOfStream();

    /// <summary>丢弃 ring 内所有未播放帧（用于 Seek 前清空）。</summary>
    void Flush();

    /// <summary>设置线性音量；0.0 = 静音，1.0 = 单位增益。超出范围将被夹紧。</summary>
    void SetVolume(float linear);

    /// <summary>已送进音频 HAL 的播放位置（自 <see cref="Start"/> 起累计）。</summary>
    System.TimeSpan Position { get; }

    /// <summary>
    /// ring 在 <see cref="SignalEndOfStream"/> 后排空时触发，每次"声明 EOS + 排空"
    /// 周期至多触发一次。订阅者应假定回调来自音频线程或 ThreadPool，不要在事件
    /// 处理器内做长时间工作。
    /// </summary>
    event System.EventHandler? PlaybackEnded;
}

/// <summary>
/// <see cref="INativeAudioDevice"/> 工厂。注入到 <see cref="AudioPlayer"/>。
/// </summary>
public interface INativeAudioDeviceFactory
{
    /// <summary>
    /// 以指定采样率 / 声道数打开默认输出设备。声音格式恒为 32-bit float
    /// interleaved。
    /// </summary>
    INativeAudioDevice Create(int sampleRate, int channels);
}
