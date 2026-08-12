using System.Diagnostics;

namespace YTM.Lyrics.Core.Playback;

/// <summary>
/// 播放状态机：订阅 IPlaybackProvider，对外暴露"当前应显示什么"。
/// - 位置外推（ADR-6）：Playing 时 position = snapshot.Position + (now − ArrivedAt)；暂停/断线冻结
/// - 漂移校正：新快照与当前外推值偏差 &gt; 0.8s 时接受快照值（seek/跳变），否则保持外推连续性
/// - TrackChanged：TrackInfo 三字段（RawTitle/RawArtist/DurationSec）任一变化触发
/// 线程安全：状态变更在同一把锁内完成；事件在锁外触发，订阅者可在回调中任意调用本机方法。
/// </summary>
public sealed class PlaybackStateMachine : IPlaybackStateMachine
{
    private readonly IPlaybackProvider _provider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly double _driftThresholdSec;
    private readonly object _lock = new();

    private PlaybackSnapshot? _base;      // 外推基准（漂移较小时保持旧基准，保证位置连续）
    private bool _providerAlive;

    public PlaybackStateMachine(
        IPlaybackProvider provider,
        Func<DateTimeOffset>? clock = null,
        double driftThresholdSec = 0.8)
    {
        _provider = provider;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _driftThresholdSec = driftThresholdSec;
        _provider.SnapshotReceived += OnSnapshotReceived;
        _provider.ProviderLost += OnProviderLost;
    }

    /// <summary>最新收到的快照（可能尚未被选为外推基准）。</summary>
    public PlaybackSnapshot? Current { get; private set; }

    public event EventHandler? TrackChanged;
    public event EventHandler? PlayStateChanged;

    public TimeSpan GetInterpolatedPosition()
    {
        lock (_lock)
        {
            return GetInterpolatedPositionLocked();
        }
    }

    private void OnProviderLost(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            Trace.WriteLine("[PlaybackStateMachine] provider lost, position frozen");
            _providerAlive = false;
        }
    }

    private void OnSnapshotReceived(object? sender, PlaybackSnapshot snapshot)
    {
        bool trackChanged;
        bool playStateChanged;

        lock (_lock)
        {
            // 先按旧状态计算当前显示位置，再决定是否接受新快照值（校正）
            var previousDisplay = GetInterpolatedPositionLocked();
            var previous = Current;

            Current = snapshot;
            _providerAlive = true;

            if (_base is null)
            {
                _base = snapshot; // 首帧直接作为基准
            }
            else if (previous is null || previous.Playing != snapshot.Playing)
            {
                _base = snapshot; // 播放状态切换（暂停/恢复）：位置锚点重置
            }
            else if (Math.Abs(previousDisplay.TotalSeconds - snapshot.PositionSec) > _driftThresholdSec)
            {
                // TASK-205H drift 观测（不改逻辑）：附加 sentAt→ArrivedAt 传输延迟，诊断
                // 探针滞后（sentAt 打帧延迟）/ 传输滞后（WS 延迟）/ 多实例竞争（seq 乱序）三类根因，
                // 数据留待 M3 裁决。传输延迟为负说明探针本地时钟快于桌面端，如实记录。
                var transportMs = (snapshot.ArrivedAt - snapshot.SentAt).TotalMilliseconds;
                Trace.WriteLine($"[PlaybackStateMachine] drift {Math.Abs(previousDisplay.TotalSeconds - snapshot.PositionSec):F2}s > {_driftThresholdSec:F1}s, accept snapshot (seek correction) [transport={transportMs:F0}ms, seq={snapshot.Seq}]");
                _base = snapshot;
            }
            // 否则：漂移在阈值内，保持旧基准外推，位置连续不跳变

            trackChanged = previous is null
                || previous.Track.RawTitle != snapshot.Track.RawTitle
                || previous.Track.RawArtist != snapshot.Track.RawArtist
                || previous.Track.DurationSec != snapshot.Track.DurationSec;
            playStateChanged = previous is null || previous.Playing != snapshot.Playing;
        }

        if (trackChanged)
            TrackChanged?.Invoke(this, EventArgs.Empty);
        if (playStateChanged)
            PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>锁内调用：基于当前状态计算应显示位置。
    /// 播放状态看最新快照（Current）；外推基准在播放中保持连续，暂停/断线冻结于最新位置。</summary>
    private TimeSpan GetInterpolatedPositionLocked()
    {
        if (_base is null) return TimeSpan.Zero;
        var current = Current ?? _base;
        if (!_providerAlive || !current.Playing)
            return TimeSpan.FromSeconds(current.PositionSec);

        var delta = _clock() - _base.ArrivedAt;
        var position = _base.PositionSec + Math.Max(0, delta.TotalSeconds);
        return TimeSpan.FromSeconds(position);
    }
}
