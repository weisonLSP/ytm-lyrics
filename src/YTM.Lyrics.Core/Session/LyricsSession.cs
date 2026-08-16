using System.Diagnostics;
using YTM.Lyrics.Core.Cache;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.Playback;
using YTM.Lyrics.Core.Timeline;

namespace YTM.Lyrics.Core.Session;

/// <summary>
/// 渲染状态快照（ADR-13：窗口只订阅 <see cref="LyricsSession.LyricsChanged"/> 拿此快照渲染）。
/// 语义对齐 <see cref="LyricsResult"/>；<paramref name="Error"/> 非空表示加载异常
/// （与"无词（Confidence=None）"区分：无词是可接受结果，异常是链路失败，窗口提示不同）。
/// </summary>
/// <param name="Document">统一歌词模型；None/异常时可能为空文档。</param>
/// <param name="Source">命中来源；None 时为 null。</param>
/// <param name="Confidence">命中置信度。</param>
/// <param name="FromCache">是否来自缓存。</param>
/// <param name="UserBound">是否用户手动绑定（F9）。</param>
/// <param name="Error">加载异常信息；非空时其余字段无意义（窗口显示失败态）。</param>
public sealed record LyricsRenderState(
    LyricDocument? Document,
    LyricsSourceKind? Source,
    MatchConfidence Confidence,
    bool FromCache,
    bool UserBound,
    string? Error = null);

/// <summary>
/// 会话编排层（ADR-13 落地，TASK-602）：持有「播放订阅、TrackSearchDeferrer 延迟搜索、
/// 歌词加载、代际/CTS 切歌竞态、重载、F9 绑定/解绑、引擎/播放链路换绑」。
/// 零 UI 依赖（Core 层）：延迟搜索轮询用 <see cref="System.Threading.Timer"/>（线程池），
/// 不用 DispatcherTimer；所有内部状态在 <see cref="_gate"/> 锁内访问。
///
/// 事件（均在触发线程上抛出，窗口负责 marshal 回 UI 线程）：
/// - <see cref="TrackChanged"/>：曲目切换透传（窗口清空旧渲染 / 显示加载中或占位）。
/// - <see cref="LyricsChanged"/>：歌词加载完成/失败/无词（窗口只消费此事件渲染）。
///
/// 窗口仍直接订阅 IPlaybackStateMachine 拿 position 做 60fps 定位（渲染职责，不在本类）。
/// </summary>
public sealed class LyricsSession : IDisposable
{
    /// <summary>延迟搜索轮询间隔（duration=0 首帧时长无效时，250ms 一档等有效时长/3s 超时）。</summary>
    public static readonly TimeSpan DeferPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ITitleCleaner _cleaner;
    private readonly ILyricsCache _cache;
    private readonly object _gate = new();
    private readonly TrackSearchDeferrer _deferrer = new();

    private IPlaybackStateMachine _machine;
    private ILyricsEngine _engine;
    private Timer? _deferTimer;             // 延迟搜索轮询（线程池，无 UI 依赖）
    private int _gen;                        // 代际序号：切歌/重载竞态守卫
    private CancellationTokenSource? _cts;   // 在途加载取消

    public LyricsSession(
        IPlaybackStateMachine machine,
        ITitleCleaner cleaner,
        ILyricsEngine engine,
        ILyricsCache cache)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _machine.TrackChanged += OnMachineTrackChanged;
    }

    /// <summary>歌词加载完成/失败/无词。窗口只订阅此事件渲染歌词面板/来源栏/状态。</summary>
    public event EventHandler<LyricsRenderState>? LyricsChanged;

    /// <summary>曲目切换透传（窗口清空旧渲染）。</summary>
    public event EventHandler? TrackChanged;

    /// <summary>
    /// 启动一次歌词加载（代际守卫 + CTS）。一般由内部 TrackChanged 路径驱动；
    /// 外部需要立即重载当前曲目时用 <see cref="ReloadAsync"/>。
    /// </summary>
    public void StartLoad(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        StartLoadCore(track);
    }

    /// <summary>
    /// 重载当前曲目（⑪ 手动绑定/解除绑定联动场景：App 收到 ManualLyricsChanged 后调用）。
    /// 结果经 <see cref="LyricsChanged"/> 广播到所有订阅窗口；无当前曲目时静默跳过。
    /// </summary>
    public Task ReloadAsync()
    {
        var current = _machine.Current;
        if (current is null || string.IsNullOrWhiteSpace(current.Track.RawTitle))
        {
            Trace.WriteLine("[LyricsSession] reload skipped: no current track");
            return Task.CompletedTask;
        }
        StartLoadCore(current.Track);
        return Task.CompletedTask;
    }

    /// <summary>F9 手动搜索候选（转发引擎；窗口渲染候选列表）。</summary>
    public Task<IReadOnlyList<SourceCandidateGroup>> SearchCandidatesAsync(
        string keyword, CancellationToken ct = default)
        => _engine.SearchCandidatesAsync(keyword, ct);

    /// <summary>
    /// F9 绑定：对当前曲目做 Clean + <see cref="ILyricsEngine.BindLyricsAsync"/>。
    /// 返回 Confidence=None 表示取词/解析失败（调用方据此提示）；绑定成功由调用方渲染
    /// 并经 ⑪ 事件链触发 <see cref="ReloadAsync"/> 联动其他窗口。
    /// </summary>
    public async Task<LyricsResult> BindLyricsAsync(
        SourceSearchResult selected, CancellationToken ct = default)
    {
        var current = _machine.Current;
        if (current is null)
        {
            return new LyricsResult(null, null, MatchConfidence.None, false, false);
        }
        var cleaned = await Task.Run(() => _cleaner.Clean(current.Track)).ConfigureAwait(false);
        return await _engine.BindLyricsAsync(cleaned, current.Track.DurationSec, selected, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// F9 解绑/清除缓存重搜：删除当前曲目缓存条目（正文+负缓存，UnbindAsync 语义不变）。
    /// 返回 false 表示失败（调用方提示）；成功后重载由调用方经 <see cref="ReloadAsync"/> 完成。
    /// </summary>
    public async Task<bool> UnbindAsync()
    {
        var current = _machine.Current;
        if (current is null) return true; // 无曲目：无事可做
        try
        {
            var cleaned = await Task.Run(() => _cleaner.Clean(current.Track)).ConfigureAwait(false);
            var key = CacheKey.Compute(cleaned, current.Track.DurationSec);
            Trace.WriteLine($"[LyricsSession] unbind cache: key={key}");
            await _cache.UnbindAsync(key).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LyricsSession] unbind failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 引擎换绑（设置 lyric_source_priority 变更 / 取证引擎重建）：换引用并立即用新引擎重载当前曲目。
    /// </summary>
    public void ReplaceEngine(ILyricsEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (_gate) { _engine = engine; }

        var current = _machine.Current;
        if (current is not null && !string.IsNullOrWhiteSpace(current.Track.RawTitle))
        {
            StartLoadCore(current.Track);
        }
        Trace.WriteLine($"[LyricsSession] engine replaced: priority=[{string.Join(", ", engine.SourcePriority)}]");
    }

    /// <summary>
    /// 播放链路换绑（ws_port 变更后 provider/machine 重建）：退订旧 machine、订阅新 machine，
    /// 取消在途加载并重置延迟搜索等待态。
    /// </summary>
    public void ReplacePlayback(IPlaybackStateMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        lock (_gate)
        {
            _machine.TrackChanged -= OnMachineTrackChanged;
            _machine = machine;
            _machine.TrackChanged += OnMachineTrackChanged;
            StopDeferredPollingLocked();
            _deferrer.Reset();
            CancelInFlightLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _machine.TrackChanged -= OnMachineTrackChanged;
            StopDeferredPollingLocked();
            CancelInFlightLocked();
        }
    }

    // ---------------- 内部：TrackChanged → deferrer 决策 → 延迟轮询 / 立即加载 ----------------

    private void OnMachineTrackChanged(object? sender, EventArgs e)
    {
        TrackInfo? loadNow = null;
        var startPolling = false;

        lock (_gate)
        {
            // 新曲目到达：作废旧等待（决策器 + 轮询 timer）、取消在途加载（切歌竞态）
            StopDeferredPollingLocked();
            _deferrer.Reset();
            CancelInFlightLocked();

            var current = _machine.Current;
            if (current is not null)
            {
                var decision = _deferrer.OnTrackChanged(current.Track, DateTimeOffset.UtcNow);
                if (decision == TrackSearchDecision.Wait)
                {
                    startPolling = true; // duration 无效 → 延迟搜索
                }
                else
                {
                    loadNow = current.Track;
                }
            }
        }

        TrackChanged?.Invoke(this, EventArgs.Empty); // 透传（锁外，窗口清空旧渲染）

        if (startPolling) StartDeferredPolling();
        else if (loadNow is not null) StartLoadCore(loadNow);
    }

    /// <summary>延迟搜索轮询（250ms）：duration&gt;0 快照到达或 3s 超时 → Ready → 用最新快照曲目加载。</summary>
    private void StartDeferredPolling()
    {
        lock (_gate)
        {
            StopDeferredPollingLocked();
            _deferTimer = new Timer(_ => DeferredPoll(), null, DeferPollInterval, DeferPollInterval);
        }
    }

    private void DeferredPoll()
    {
        TrackInfo? readyTrack = null;

        lock (_gate)
        {
            if (_deferTimer is null) return; // 已被切歌/换绑停掉，本 tick 作废

            var current = _machine.Current;
            if (current is null)
            {
                StopDeferredPollingLocked();
                return;
            }

            switch (_deferrer.OnSnapshot(current.Track, DateTimeOffset.UtcNow))
            {
                case TrackSearchDecision.Wait:
                    return; // 时长仍无效且未超时：继续等
                case TrackSearchDecision.Ready:
                    StopDeferredPollingLocked();
                    readyTrack = current.Track; // 用最新（时长已有效）的快照曲目
                    break;
                default: // Immediate：等待态已被清除（如切歌竞态），停 timer 防泄漏
                    StopDeferredPollingLocked();
                    return;
            }
        }

        if (readyTrack is not null) StartLoadCore(readyTrack);
    }

    /// <summary>锁内调用：停轮询 timer（dispose + 置 null；回调自身 dispose 安全）。</summary>
    private void StopDeferredPollingLocked()
    {
        _deferTimer?.Dispose();
        _deferTimer = null;
    }

    /// <summary>锁内调用：取消并释放在途加载 CTS。</summary>
    private void CancelInFlightLocked()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>启动一次加载（代际守卫 + CTS），fire-and-forget（内部全捕获）。</summary>
    private void StartLoadCore(TrackInfo track)
    {
        CancellationTokenSource cts;
        int gen;
        lock (_gate)
        {
            CancelInFlightLocked();
            gen = ++_gen;
            _cts = new CancellationTokenSource();
            cts = _cts;
        }

        Trace.WriteLine($"[LyricsSession] StartLoad: title='{track.RawTitle}' gen={gen}");
        _ = LoadLyricsAsync(track, cts.Token, gen);
    }

    private async Task LoadLyricsAsync(TrackInfo raw, CancellationToken ct, int gen)
    {
        LyricsRenderState state;
        try
        {
            var cleaned = await Task.Run(() => _cleaner.Clean(raw), ct).ConfigureAwait(false); // 纯函数，线程池执行
            var result = await _engine.GetLyricsAsync(cleaned, raw.DurationSec, ct).ConfigureAwait(false);
            state = new LyricsRenderState(result.Document, result.Source, result.Confidence,
                result.FromCache, result.UserBound);
        }
        catch (OperationCanceledException)
        {
            return; // 已被切歌/重载取消，静默退出
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LyricsSession] lyrics load failed: {ex.GetType().Name}: {ex.Message}");
            state = new LyricsRenderState(null, null, MatchConfidence.None, false, false, ex.Message);
        }

        lock (_gate)
        {
            if (gen != _gen) return; // 期间又切了歌/重载，丢弃本结果
        }

        LyricsChanged?.Invoke(this, state); // 锁外触发，订阅者可回调本类方法
    }
}
