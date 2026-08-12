using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YTM.Lyrics.Core.Playback.Providers;

/// <summary>
/// 监听 ws://127.0.0.1:17865 的油猴探针服务端。
/// - 宿主：TcpListener + 手写 RFC6455 升级握手 + WebSocket.CreateFromStream（零第三方依赖，无 http.sys 依赖）
/// - 协议（TDD §4）：JSON 文本帧；建连即回 hello；seq 单调递增，丢弃 ≤ 已收最大 seq 的帧（每连接独立）；
///   error 帧仅记日志；心跳超时（默认 10s，可注入）触发 ProviderLost；连接断开后继续 Accept，探针重连自动恢复。
/// </summary>
public sealed class WebSocketProbeProvider : IPlaybackProvider, IDisposable
{
    public const int DefaultPort = 17865;
    private const string HelloJson = "{\"type\":\"hello\",\"version\":\"1\"}";
    private static readonly byte[] HelloBytes = Encoding.UTF8.GetBytes(HelloJson);

    private readonly TcpListener _listener;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;
    private bool _started;
    private bool _disposed;

    public event EventHandler<PlaybackSnapshot>? SnapshotReceived;
    public event EventHandler? ProviderLost;

    /// <summary>实际绑定的端口（port=0 时由系统分配，供测试用）。</summary>
    public int BoundPort { get; private set; }

    public WebSocketProbeProvider(int port = DefaultPort, TimeSpan? heartbeatTimeout = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _started = true;
        Trace.WriteLine($"[WebSocketProbeProvider] listening on ws://127.0.0.1:{BoundPort}, heartbeat timeout {_heartbeatTimeout.TotalSeconds}s");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(ct), CancellationToken.None);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { _cts.Dispose(); } catch { /* ignore */ }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Trace.WriteLine($"[WebSocketProbeProvider] accept failed: {ex.Message}");
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();

            var headers = await ReadHttpHeadersAsync(stream, ct);
            if (headers is null)
            {
                Trace.WriteLine("[WebSocketProbeProvider] client closed before headers");
                return;
            }
            if (!TryParseHandshake(headers, out var webSocketKey, out var reason))
            {
                Trace.WriteLine($"[WebSocketProbeProvider] handshake rejected: {reason}");
                await WriteHttpResponseAsync(stream, "400 Bad Request", reason, ct);
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(webSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
            await stream.FlushAsync(ct);

            using var ws = WebSocket.CreateFromStream(
                stream, isServer: true, subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(15));

            await ws.SendAsync(new ArraySegment<byte>(HelloBytes), WebSocketMessageType.Text, true, ct);
            Trace.WriteLine("[WebSocketProbeProvider] connection established, hello sent");

            await ReceiveLoopAsync(ws, ct);
        }
        catch (OperationCanceledException) { /* 关闭流程 */ }
        catch (WebSocketException ex) { OnConnectionLost($"websocket: {ex.Message}"); }
        catch (IOException ex) { OnConnectionLost($"io: {ex.Message}"); }
        catch (Exception ex) { OnConnectionLost($"unexpected: {ex.Message}"); }
        finally
        {
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws, CancellationToken ct)
    {
        long maxSeq = long.MinValue;                 // seq 丢弃判定，每连接独立（探针重连后 seq 从新序列开始）
        var receiveCts = new CancellationTokenSource();
        receiveCts.CancelAfter(_heartbeatTimeout);
        var buffer = new byte[64 * 1024];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, receiveCts.Token);

                switch (result.MessageType)
                {
                    case WebSocketMessageType.Close:
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        return;

                    // 注：Ping/Pong 控制帧由 WebSocket 实现层自动应答，上层 ReceiveAsync 不会返回

                    case WebSocketMessageType.Binary:
                        receiveCts.CancelAfter(_heartbeatTimeout); // 二进制帧忽略，但算活跃
                        break;

                    case WebSocketMessageType.Text:
                        var text = await ReadCompleteTextAsync(ws, buffer, result, receiveCts.Token);
                        receiveCts.CancelAfter(_heartbeatTimeout);
                        ProcessFrame(text, ref maxSeq);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Trace.WriteLine("[WebSocketProbeProvider] heartbeat timeout, probe lost");
            ProviderLost?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            receiveCts.Dispose();
        }
    }

    private void OnConnectionLost(string reason)
    {
        Trace.WriteLine($"[WebSocketProbeProvider] connection lost ({reason}), probe lost");
        ProviderLost?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按 EndOfMessage 聚合分片文本帧；非文本分片视为协议错误，返回空串丢弃。</summary>
    private static async Task<string> ReadCompleteTextAsync(
        WebSocket ws, byte[] buffer, WebSocketReceiveResult first, CancellationToken ct)
    {
        var sb = new StringBuilder(Encoding.UTF8.GetString(buffer, 0, first.Count));
        var result = first;
        while (!result.EndOfMessage)
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType != WebSocketMessageType.Text)
                return string.Empty; // 协议错误：文本帧中出现非文本分片
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        return sb.ToString();
    }

    private void ProcessFrame(string json, ref long maxSeq)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            Trace.WriteLine($"[WebSocketProbeProvider] malformed json frame dropped: {ex.Message}");
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                Trace.WriteLine("[WebSocketProbeProvider] frame without type dropped");
                return;
            }

            switch (typeEl.GetString())
            {
                case "state":
                    HandleStateFrame(doc.RootElement, ref maxSeq);
                    break;

                case "error":
                    // TDD §4：error 帧仅记录日志，不改变状态机
                    Trace.WriteLine($"[WebSocketProbeProvider] error frame received: {json}");
                    break;

                default:
                    Trace.WriteLine($"[WebSocketProbeProvider] unknown frame type '{typeEl.GetString()}' ignored");
                    break;
            }
        }
    }

    private void HandleStateFrame(JsonElement root, ref long maxSeq)
    {
        try
        {
            // seq：缺失或非法 → 丢弃；≤ 已收最大 seq → 乱序/重复，丢弃
            if (!root.TryGetProperty("seq", out var seqEl) || seqEl.ValueKind != JsonValueKind.Number)
            {
                Trace.WriteLine("[WebSocketProbeProvider] state frame missing seq, dropped");
                return;
            }
            var seq = seqEl.GetInt64();
            if (seq <= maxSeq)
            {
                Trace.WriteLine($"[WebSocketProbeProvider] stale seq {seq} <= {maxSeq}, dropped");
                return;
            }
            maxSeq = seq;

            if (!root.TryGetProperty("track", out var trackEl) || trackEl.ValueKind != JsonValueKind.Object
                || !trackEl.TryGetProperty("title", out var titleEl) || titleEl.ValueKind != JsonValueKind.String
                || !trackEl.TryGetProperty("artist", out var artistEl) || artistEl.ValueKind != JsonValueKind.String
                || !trackEl.TryGetProperty("duration", out var durationEl) || durationEl.ValueKind != JsonValueKind.Number
                || !root.TryGetProperty("position", out var positionEl) || positionEl.ValueKind != JsonValueKind.Number
                || !root.TryGetProperty("playing", out var playingEl) || playingEl.ValueKind != JsonValueKind.True && playingEl.ValueKind != JsonValueKind.False)
            {
                Trace.WriteLine("[WebSocketProbeProvider] state frame missing required fields, dropped");
                return;
            }

            var sentAt = root.TryGetProperty("sentAt", out var sentEl) && sentEl.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(sentEl.GetInt64())
                : DateTimeOffset.UtcNow;

            var snapshot = new PlaybackSnapshot(
                new TrackInfo(titleEl.GetString()!, artistEl.GetString()!, durationEl.GetDouble()),
                positionEl.GetDouble(), playingEl.GetBoolean(),
                seq, sentAt, DateTimeOffset.UtcNow);

            SnapshotReceived?.Invoke(this, snapshot);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            Trace.WriteLine($"[WebSocketProbeProvider] state frame parse failed: {ex.Message}, dropped");
        }
    }

    private static async Task<byte[]?> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var headers = new MemoryStream();
        while (headers.Length < 16 * 1024)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) return null; // 连接在完整头部前关闭
            headers.Write(buffer, 0, n);
            var text = Encoding.ASCII.GetString(headers.GetBuffer(), 0, (int)headers.Length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || text.Contains("\n\n", StringComparison.Ordinal))
                return headers.ToArray();
        }
        return null; // 头部超限
    }

    private static bool TryParseHandshake(byte[] headers, out string webSocketKey, out string reason)
    {
        webSocketKey = string.Empty;
        reason = string.Empty;

        var text = Encoding.ASCII.GetString(headers);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = headerEnd >= 0 ? text[..headerEnd] : text;
        var lines = head.Split("\r\n", StringSplitOptions.None);

        if (lines.Length == 0 || !lines[0].StartsWith("GET ", StringComparison.Ordinal))
        {
            reason = "request line is not GET";
            return false;
        }

        var upgrade = false;
        var connection = false;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            switch (name.ToLowerInvariant())
            {
                case "upgrade" when value.Equals("websocket", StringComparison.OrdinalIgnoreCase):
                    upgrade = true;
                    break;
                case "connection" when value.Contains("upgrade", StringComparison.OrdinalIgnoreCase):
                    connection = true;
                    break;
                case "sec-websocket-key":
                    webSocketKey = value;
                    break;
                case "sec-websocket-version" when value != "13":
                    reason = $"unsupported websocket version {value}";
                    return false;
            }
        }

        if (!upgrade || !connection)
        {
            reason = "missing upgrade/connection headers";
            return false;
        }
        if (string.IsNullOrEmpty(webSocketKey))
        {
            reason = "missing Sec-WebSocket-Key";
            return false;
        }
        return true;
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        var text = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct);
    }
}
