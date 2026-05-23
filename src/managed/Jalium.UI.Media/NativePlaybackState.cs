namespace Jalium.UI.Media;

/// <summary>
/// 音频/媒体播放器的运行态。与 WPF <c>System.Windows.Media.MediaPlayer</c> 的
/// 三态语义对齐，并额外区分 <see cref="Ended"/>（自然 EOS）与 <see cref="Stopped"/>
/// （未打开或显式 Stop）。
/// </summary>
/// <remarks>
/// 取代之前从 <c>SoundFlow.Enums.PlaybackState</c> 漏出来的同名枚举，作为
/// <see cref="AudioPlayer.State"/> 的稳定公共类型。
/// </remarks>
public enum NativePlaybackState
{
    /// <summary>未打开、刚 Close，或显式 Stop 后位置归零。</summary>
    Stopped = 0,

    /// <summary>正在播放。</summary>
    Playing = 1,

    /// <summary>已暂停；调用 <c>Play</c> 可从当前位置继续。</summary>
    Paused = 2,

    /// <summary>已自然播放到末尾；Position == Duration。</summary>
    Ended = 3,
}
