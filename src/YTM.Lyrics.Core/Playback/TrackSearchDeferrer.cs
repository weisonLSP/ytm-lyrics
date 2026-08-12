using System.Diagnostics;

namespace YTM.Lyrics.Core.Playback;

/// <summary>
/// duration=0 延迟搜索决策器（TASK-205F 杂项 4）。
///
/// 背景：YTM 探针首帧可能尚未携带有效时长（DurationSec=0/负）。此时若直接触发歌词搜索：
/// 1. 用 duration=0 计算缓存键（bucket=0）→ 与正时长键不同 → 等正时长到达后再次搜索，
///    产生无效搜索 + 缓存键漂移（同一首歌两个键，第二次搜索结果与第一次互相不可见）；
/// 2. 搜索打分时长维必失分。
///
/// 决策语义（纯状态机，无定时器依赖，由调用方驱动）：
/// - <see cref="OnTrackChanged"/>：duration &gt; 0 → Immediate（照常立即搜索）；duration ≤ 0 →
///   进入等待（记录曲目与等待起点）→ Wait（调用方延迟触发搜索）。
/// - <see cref="OnSnapshot"/>（等待期间由调用方周期性/事件驱动调用）：满足任一即 Ready：
///   a. 等待超时（默认 3s，兜底照常搜）；b. 首个 duration &gt; 0 且与等待曲目一致（三字段全等）
///   的快照到达（拿到真实时长，缓存键不再漂移）。
/// - 其余状态返回 Wait。
/// - <see cref="Reset"/>：切歌/放弃时清空等待态（调用方取消在途加载时调用）。
///
/// 线程纪律：本类无共享可变状态依赖外部线程，调用方保证单线程驱动即可（MainWindow UI 线程）。
/// </summary>
public enum TrackSearchDecision
{
    /// <summary>立即触发搜索（duration 已有效或未处于等待态）。</summary>
    Immediate,

    /// <summary>继续等待（duration 仍无效且未超时）。</summary>
    Wait,

    /// <summary>现在触发搜索（首个有效时长快照或超时兜底）。</summary>
    Ready,
}

public sealed class TrackSearchDeferrer
{
    /// <summary>超时兜底：等待超时后照常以当前（可能仍为 0 的）时长搜索。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _timeout;
    private TrackInfo? _waitingTrack;
    private DateTimeOffset? _waitStartedAt;

    public TrackSearchDeferrer(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// 曲目变化（TrackChanged）时调用。duration &gt; 0 立即搜索；否则进入等待。
    /// 新曲目到达自动覆盖旧等待态（切歌竞态由调用方的代际守卫负责取消在途加载）。
    /// </summary>
    public TrackSearchDecision OnTrackChanged(TrackInfo track, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.DurationSec > 0)
        {
            _waitingTrack = null;
            _waitStartedAt = null;
            return TrackSearchDecision.Immediate;
        }

        Trace.WriteLine($"[TrackSearchDeferrer] track duration invalid ({track.DurationSec:F1}s), defer search: '{track.RawTitle}'");
        _waitingTrack = track;
        _waitStartedAt = now;
        return TrackSearchDecision.Wait;
    }

    /// <summary>
    /// 等待期间的新快照到达时调用（调用方轮询/事件驱动）。
    /// 返回 Ready 的条件：超时，或首个 duration &gt; 0 且与等待曲目一致的快照。
    /// 非等待态防御性返回 Immediate。
    /// </summary>
    public TrackSearchDecision OnSnapshot(TrackInfo currentTrack, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentTrack);

        if (_waitingTrack is null || _waitStartedAt is null)
        {
            return TrackSearchDecision.Immediate; // 未在等待：无需延迟
        }

        // a. 超时兜底（3s 内没拿到有效时长 → 照常搜，避免歌词永久不加载）
        if (now - _waitStartedAt >= _timeout)
        {
            Trace.WriteLine($"[TrackSearchDeferrer] wait timeout {_timeout.TotalSeconds:F1}s, search anyway (duration still {currentTrack.DurationSec:F1}s)");
            Reset();
            return TrackSearchDecision.Ready;
        }

        // b. 首个 duration>0 且曲目标识一致（标题+艺人）的快照：缓存键不再漂移，立即搜。
        //    DurationSec 不参与比较（等待中的曲目 duration 无效，比较它必然不等）。
        //    切歌必触发新 TrackChanged（三字段任一变化），此处防御性判断曲目未变。
        if (currentTrack.DurationSec > 0
            && currentTrack.RawTitle == _waitingTrack.RawTitle
            && currentTrack.RawArtist == _waitingTrack.RawArtist)
        {
            Trace.WriteLine($"[TrackSearchDeferrer] duration resolved ({currentTrack.DurationSec:F1}s), search now: '{currentTrack.RawTitle}'");
            Reset();
            return TrackSearchDecision.Ready;
        }

        return TrackSearchDecision.Wait;
    }

    /// <summary>清空等待态（切歌/放弃在途加载）。</summary>
    public void Reset()
    {
        _waitingTrack = null;
        _waitStartedAt = null;
    }
}
