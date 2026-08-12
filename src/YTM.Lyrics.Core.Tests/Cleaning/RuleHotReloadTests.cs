using System.Diagnostics;
using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Playback;

namespace YTM.Lyrics.Core.Tests.Cleaning;

/// <summary>
/// 规则表热加载测试（TDD §3.2：规则表外置 JSON，改动后无需重编译即生效）。
/// 临时规则文件写在测试输出目录（工作区内，沙箱可写）。
/// </summary>
public class RuleHotReloadTests
{
    private const string V1Json = """
        {
          "version": 1,
          "brackets": [
            { "open": "(", "close": ")" },
            { "open": "【", "close": "】" },
            { "open": "[", "close": "]" }
          ],
          "featKeywords": ["feat.", "feat", "featuring", "ft.", "ft", "with"],
          "featNoiseTokens": ["lyrics", "lyric", "video", "official", "audio", "mv", "visualizer", "字幕", "歌词"],
          "suffixes": ["HD"],
          "prefixes": [],
          "decorations": ["★"],
          "separators": [" - "],
          "artistSplitTokens": ["&", " and "]
        }
        """;

    private const string V2Json = """
        {
          "version": 2,
          "brackets": [
            { "open": "(", "close": ")" },
            { "open": "【", "close": "】" },
            { "open": "[", "close": "]" }
          ],
          "featKeywords": ["feat.", "feat", "featuring", "ft.", "ft", "with"],
          "featNoiseTokens": ["lyrics", "lyric", "video", "official", "audio", "mv", "visualizer", "字幕", "歌词"],
          "suffixes": ["HD", "Official MV"],
          "prefixes": [],
          "decorations": ["★"],
          "separators": [" - "],
          "artistSplitTokens": ["&", " and "]
        }
        """;

    [Fact]
    public async Task 外部规则文件_修改后热加载_无需重编译即生效()
    {
        using var tmp = new TempRulesFile(V1Json);
        using var cleaner = TitleCleaner.FromRulesFile(tmp.Path);

        // v1：仅 HD 后缀
        Assert.Equal("Song", cleaner.Clean(new TrackInfo("Song HD", "A", 100)).Title);
        Assert.Equal("Song Official MV", cleaner.Clean(new TrackInfo("Song Official MV", "A", 100)).Title);

        // 改写规则文件追加 "Official MV" 后缀 → 同实例无需重建即可用新规则
        tmp.Write(V2Json);
        var ok = await WaitUntilAsync(
            () => cleaner.Clean(new TrackInfo("Song Official MV", "A", 100)).Title == "Song");
        Assert.True(ok, "热加载应在超时前生效（FileSystemWatcher + 300ms 防抖）");

        // 旧规则仍然生效，实例可继续使用
        Assert.Equal("Song", cleaner.Clean(new TrackInfo("Song HD", "A", 100)).Title);
    }

    [Fact]
    public async Task 规则文件解析失败_保留旧规则不崩溃_恢复后新规则生效()
    {
        using var tmp = new TempRulesFile(V1Json);
        using var cleaner = TitleCleaner.FromRulesFile(tmp.Path);

        Assert.Equal("Song", cleaner.Clean(new TrackInfo("Song HD", "A", 100)).Title);

        // 写入非法 JSON：重载失败应保留旧规则，不抛异常
        tmp.Write("{ this is not valid json !!!");
        // TASK-403：固定延时提到 1500ms（原 800ms），留足 watcher 事件延迟 + 300ms 防抖的余量，
        // 慢机器/CI 下确保失败重载路径确实被走到；断言本身不依赖重载是否发生（失败也保留旧规则）
        await Task.Delay(1500);
        Assert.Equal("Song", cleaner.Clean(new TrackInfo("Song HD", "A", 100)).Title);
        Assert.Equal("Song Official MV", cleaner.Clean(new TrackInfo("Song Official MV", "A", 100)).Title);

        // 恢复合法规则 → 新规则生效
        tmp.Write(V2Json);
        var ok = await WaitUntilAsync(
            () => cleaner.Clean(new TrackInfo("Song Official MV", "A", 100)).Title == "Song");
        Assert.True(ok, "恢复合法规则后热加载应生效");
    }

    [Fact]
    public void 规则文件不存在_构造即失败()
    {
        var missing = System.IO.Path.Combine(AppContext.BaseDirectory, "no-such-rules.json");
        Assert.Throws<FileNotFoundException>(() => TitleCleaner.FromRulesFile(missing));
    }

    // TASK-403：偶发失败修复。FileSystemWatcher 事件在慢机器/CI 上可能延迟数秒才到达（
    // NTFS 事件队列堆积），原 10s 超时 + 100ms 轮询在极端情况下不足。
    // ① 超时 10s → 30s；② 轮询 100ms → 50ms（更快探测新规则生效）。
    // 断言本身已是「重试直到超时」模式，配合更宽裕的超时窗口覆盖事件延迟。
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>临时规则文件：建在测试输出目录（工作区内），随实例释放清理。</summary>
    private sealed class TempRulesFile : IDisposable
    {
        public string Path { get; }

        public TempRulesFile(string json)
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "hotreload-tmp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, "cleaning-rules.json");
            Write(json);
        }

        public void Write(string json) => File.WriteAllText(Path, json);

        public void Dispose()
        {
            try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true); }
            catch { /* 尽力清理，失败不影响测试结论 */ }
        }
    }
}
