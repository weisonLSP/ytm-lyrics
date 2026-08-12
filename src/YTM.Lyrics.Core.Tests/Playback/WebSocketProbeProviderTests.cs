using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using YTM.Lyrics.Core.Playback;
using YTM.Lyrics.Core.Playback.Providers;

namespace YTM.Lyrics.Core.Tests.Playback;

/// <summary>WebSocketProbeProvider 单测：真实 ClientWebSocket 连接覆盖握手、hello、帧发布、seq 丢弃、心跳超时、重连、error 帧。</summary>
public class WebSocketProbeProviderTests
{
    private const int TimeoutMs = 5000;

    private static async Task<(WebSocketProbeProvider provider, ClientWebSocket client)> ConnectAsync(
        TimeSpan? heartbeatTimeout = null)
    {
        var provider = new WebSocketProbeProvider(port: 0, heartbeatTimeout: heartbeatTimeout);
        await provider.StartAsync();

        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{provider.BoundPort}/"), CancellationToken.None);
        return (provider, client);
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket ws, int timeoutMs = TimeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("connection closed while reading");
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);
        return sb.ToString();
    }

    private static string StateFrame(long seq, string title, string artist, double duration, double position, bool playing, long sentAtMs)
        => $"{{\"type\":\"state\",\"seq\":{seq},\"track\":{{\"title\":\"{title}\",\"artist\":\"{artist}\",\"duration\":{duration}}},\"position\":{position},\"playing\":{(playing ? "true" : "false")},\"sentAt\":{sentAtMs}}}";

    private static async Task<PlaybackSnapshot> NextSnapshotAsync(
        WebSocketProbeProvider provider, int timeoutMs = TimeoutMs)
    {
        var tcs = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, PlaybackSnapshot snap) => tcs.TrySetResult(snap);
        provider.SnapshotReceived += Handler;
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await using var _ = cts.Token.Register(() => tcs.TrySetException(new TimeoutException("snapshot not received")));
            return await tcs.Task;
        }
        finally
        {
            provider.SnapshotReceived -= Handler;
        }
    }

    private static async Task<EventArgs> NextProviderLostAsync(WebSocketProbeProvider provider, int timeoutMs = TimeoutMs)
    {
        var tcs = new TaskCompletionSource<EventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, EventArgs e) => tcs.TrySetResult(e);
        provider.ProviderLost += Handler;
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await using var _ = cts.Token.Register(() => tcs.TrySetException(new TimeoutException("ProviderLost not raised")));
            return await tcs.Task;
        }
        finally
        {
            provider.ProviderLost -= Handler;
        }
    }

    [Fact]
    public async Task 建连_回hello且state帧发布快照()
    {
        var (provider, client) = await ConnectAsync();
        try
        {
            var hello = await ReceiveTextAsync(client);
            Assert.Contains("\"type\":\"hello\"", hello);
            Assert.Contains("\"version\":\"1\"", hello);

            var snapshotTask = NextSnapshotAsync(provider);
            await SendJsonAsync(client, StateFrame(1, "标题", "艺人", 234.5, 67.2, true, 1786531200000));

            var snap = await snapshotTask;
            Assert.Equal("标题", snap.Track.RawTitle);
            Assert.Equal("艺人", snap.Track.RawArtist);
            Assert.Equal(234.5, snap.Track.DurationSec, 3);
            Assert.Equal(67.2, snap.PositionSec, 3);
            Assert.True(snap.Playing);
            Assert.Equal(1, snap.Seq);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1786531200000), snap.SentAt);
        }
        finally
        {
            client.Dispose();
            provider.Dispose();
        }
    }

    [Fact]
    public async Task seq乱序_小于等于已收最大seq的帧被丢弃()
    {
        var (provider, client) = await ConnectAsync();
        try
        {
            await ReceiveTextAsync(client); // hello

            var received = new List<long>();
            void Handler(object? s, PlaybackSnapshot snap) => received.Add(snap.Seq);
            provider.SnapshotReceived += Handler;

            await SendJsonAsync(client, StateFrame(5, "t", "a", 100, 1, true, 1));
            await SendJsonAsync(client, StateFrame(3, "t", "a", 100, 2, true, 1)); // 乱序：丢弃
            await SendJsonAsync(client, StateFrame(5, "t", "a", 100, 3, true, 1)); // 重复：丢弃
            await SendJsonAsync(client, StateFrame(4, "t", "a", 100, 4, true, 1)); // 乱序：丢弃
            await SendJsonAsync(client, StateFrame(6, "t", "a", 100, 5, true, 1));

            await Task.Delay(500);
            Assert.Equal(new long[] { 5, 6 }, received);
            provider.SnapshotReceived -= Handler;
        }
        finally
        {
            client.Dispose();
            provider.Dispose();
        }
    }

    [Fact]
    public async Task 心跳超时_触发ProviderLost()
    {
        var (provider, client) = await ConnectAsync(heartbeatTimeout: TimeSpan.FromMilliseconds(500));
        try
        {
            await ReceiveTextAsync(client); // hello
            var lost = NextProviderLostAsync(provider);
            await lost; // 不发任何帧，500ms 后超时
        }
        finally
        {
            client.Dispose();
            provider.Dispose();
        }
    }

    [Fact]
    public async Task 探针重连_seq从新序列开始不被丢弃()
    {
        var (provider, client1) = await ConnectAsync();
        try
        {
            await ReceiveTextAsync(client1); // hello
            await SendJsonAsync(client1, StateFrame(3, "t", "a", 100, 1, true, 1));
            await Task.Delay(200);
            client1.Dispose(); // 断线
            await Task.Delay(200);

            // 重连：新连接 seq 从 1 开始，不应被旧连接的 maxSeq=3 挡住
            var client2 = new ClientWebSocket();
            await client2.ConnectAsync(new Uri($"ws://127.0.0.1:{provider.BoundPort}/"), CancellationToken.None);
            try
            {
                var hello2 = await ReceiveTextAsync(client2);
                Assert.Contains("\"type\":\"hello\"", hello2);

                var snapTask = NextSnapshotAsync(provider);
                await SendJsonAsync(client2, StateFrame(1, "t2", "a2", 200, 10, true, 1));
                var snap = await snapTask;
                Assert.Equal(1, snap.Seq);
                Assert.Equal("t2", snap.Track.RawTitle);
            }
            finally
            {
                client2.Dispose();
            }
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public async Task 断线触发ProviderLost_重连后seq从新序列开始()
    {
        // TASK-403 Part C：补齐「断线 → ProviderLost → 重连 → 新序列 seq」整合链路。
        // 客户端直接 Dispose（无 Close 握手，模拟浏览器崩溃）→ 服务端收连接重置（IOException）→ ProviderLost；
        // 随后新连接 seq=1 应正常发布，不被旧连接 maxSeq 隔离。
        var (provider, client1) = await ConnectAsync();
        try
        {
            await ReceiveTextAsync(client1); // hello
            await SendJsonAsync(client1, StateFrame(1, "t", "a", 100, 1, true, 1));
            await Task.Delay(100); // 让快照发布完成

            var lostTask = NextProviderLostAsync(provider);
            client1.Dispose(); // 暴力断开
            await lostTask; // 5s 内必须触发 ProviderLost

            var client2 = new ClientWebSocket();
            await client2.ConnectAsync(new Uri($"ws://127.0.0.1:{provider.BoundPort}/"), CancellationToken.None);
            try
            {
                var hello2 = await ReceiveTextAsync(client2);
                Assert.Contains("\"type\":\"hello\"", hello2);

                var snapTask = NextSnapshotAsync(provider);
                await SendJsonAsync(client2, StateFrame(1, "t2", "a2", 200, 10, true, 1));
                var snap = await snapTask;
                Assert.Equal(1, snap.Seq); // 新连接独立 maxSeq：seq 从新序列开始
                Assert.Equal("t2", snap.Track.RawTitle);
            }
            finally
            {
                client2.Dispose();
            }
        }
        finally
        {
            provider.Dispose();
        }
    }

    [Fact]
    public async Task error帧_仅记日志不影响状态机()
    {
        var (provider, client) = await ConnectAsync();
        try
        {
            await ReceiveTextAsync(client); // hello

            var snapshotFired = false;
            var lostFired = false;
            void OnSnap(object? s, PlaybackSnapshot snap) => snapshotFired = true;
            void OnLost(object? s, EventArgs e) => lostFired = true;
            provider.SnapshotReceived += OnSnap;
            provider.ProviderLost += OnLost;

            await SendJsonAsync(client, "{\"type\":\"error\",\"code\":\"E1\",\"message\":\"boom\"}");
            await Task.Delay(300);

            Assert.False(snapshotFired);
            Assert.False(lostFired);
            provider.SnapshotReceived -= OnSnap;
            provider.ProviderLost -= OnLost;
        }
        finally
        {
            client.Dispose();
            provider.Dispose();
        }
    }

    [Fact]
    public async Task 非法帧_缺字段帧被丢弃不发布()
    {
        var (provider, client) = await ConnectAsync();
        try
        {
            await ReceiveTextAsync(client); // hello

            var snapshotFired = false;
            void OnSnap(object? s, PlaybackSnapshot snap) => snapshotFired = true;
            provider.SnapshotReceived += OnSnap;

            await SendJsonAsync(client, "not json at all");
            await SendJsonAsync(client, "{\"type\":\"state\"}");                              // 缺 seq/track
            await SendJsonAsync(client, "{\"type\":\"state\",\"seq\":1}");                    // 缺 track
            await SendJsonAsync(client, "{\"type\":\"unknown\",\"seq\":1}");                  // 未知类型
            await Task.Delay(300);

            Assert.False(snapshotFired);
            provider.SnapshotReceived -= OnSnap;
        }
        finally
        {
            client.Dispose();
            provider.Dispose();
        }
    }
}
