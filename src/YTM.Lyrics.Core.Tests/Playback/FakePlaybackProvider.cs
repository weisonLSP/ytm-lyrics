using YTM.Lyrics.Core.Playback;

namespace YTM.Lyrics.Core.Tests.Playback;

/// <summary>测试辅助：手动驱动的 IPlaybackProvider，不依赖 WebSocket。</summary>
internal sealed class FakePlaybackProvider : IPlaybackProvider
{
    public event EventHandler<PlaybackSnapshot>? SnapshotReceived;
    public event EventHandler? ProviderLost;

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Push(PlaybackSnapshot snapshot) => SnapshotReceived?.Invoke(this, snapshot);

    public void Lose() => ProviderLost?.Invoke(this, EventArgs.Empty);
}
