using YTM.Lyrics.Core.Playback;
using YTM.Lyrics.Core.Playback.Providers;

// TASK-102 端到端冒烟宿主：真实 WebSocketProbeProvider + PlaybackStateMachine 全链路。
// 配合 tools/probe_smoke.py 推送模拟探针帧序列，验证 hello/快照/乱序丢弃/暂停冻结/切歌/error。
Console.OutputEncoding = System.Text.Encoding.UTF8;

var provider = new WebSocketProbeProvider(); // 默认 17865
var sm = new PlaybackStateMachine(provider);

provider.ProviderLost += (_, _) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ProviderLost | 位置冻结: {sm.GetInterpolatedPosition().TotalSeconds:F2}s");
provider.SnapshotReceived += (_, s) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Snapshot    | seq={s.Seq,-3} pos={s.PositionSec,6:F2} playing={s.Playing,-5} track={s.Track.RawTitle}");
sm.TrackChanged += (_, _) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] TrackChanged| {sm.Current?.Track.RawTitle} - {sm.Current?.Track.RawArtist} ({sm.Current?.Track.DurationSec}s)");
sm.PlayStateChanged += (_, _) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] PlayChanged | playing={sm.Current?.Playing}");

await provider.StartAsync();
Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] listening on ws://127.0.0.1:{provider.BoundPort}");

// 每秒打印一次外推位置，展示 ADR-6 外推/冻结行为
using var timer = new Timer(_ =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}]   [tick] interpolated={sm.GetInterpolatedPosition().TotalSeconds,6:F2}s"),
    null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

await Task.Delay(15_000);
Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] smoke host exiting");
