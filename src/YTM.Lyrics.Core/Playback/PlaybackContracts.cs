namespace YTM.Lyrics.Core.Playback;

public sealed record TrackInfo(string RawTitle, string RawArtist, double DurationSec);

public sealed record PlaybackSnapshot(
    TrackInfo Track, double PositionSec, bool Playing,
    long Seq, DateTimeOffset SentAt, DateTimeOffset ArrivedAt);

public interface IPlaybackProvider
{
    event EventHandler<PlaybackSnapshot>? SnapshotReceived;
    event EventHandler? ProviderLost;          // 心跳超时 10s 触发
    Task StartAsync(CancellationToken ct = default);
}

// 状态机：对 UI 暴露"当前应显示什么"
public interface IPlaybackStateMachine
{
    PlaybackSnapshot? Current { get; }
    TimeSpan GetInterpolatedPosition();        // ADR-6 外推实现
    event EventHandler? TrackChanged;          // 曲目标识变化（驱动歌词加载）
    event EventHandler? PlayStateChanged;
}
