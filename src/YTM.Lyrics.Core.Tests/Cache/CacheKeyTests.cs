using YTM.Lyrics.Core.Cache;

namespace YTM.Lyrics.Core.Tests.Cache;

/// <summary>
/// CacheKey 单测（TDD §3.5 / PRD F6）：
/// 同输入同键、大小写不敏感、±3s 内同桶、±4s 不同桶、边界抖动不串桶。
/// </summary>
public class CacheKeyTests
{
    private static string Key(string title, string artist, double durationSec)
        => CacheKey.Compute(title, artist, durationSec);

    [Fact]
    public void 同输入_同键_确定性()
    {
        var a = Key("Hello World", "Artist Name", 234.0);
        var b = Key("Hello World", "Artist Name", 234.0);
        Assert.Equal(a, b);
        Assert.Equal(40, a.Length); // sha1 hex = 40 字符
    }

    [Fact]
    public void 大小写不敏感_同键()
    {
        Assert.Equal(Key("Hello", "Artist", 100), Key("hello", "artist", 100));
        Assert.Equal(Key("Hello", "Artist", 100), Key("HELLO", "ARTIST", 100));
    }

    [Fact]
    public void 不同曲目或艺人_不同键()
    {
        Assert.NotEqual(Key("A", "B", 100), Key("C", "B", 100));
        Assert.NotEqual(Key("A", "B", 100), Key("A", "C", 100));
    }

    [Fact]
    public void 正负3秒内_同桶_任务卡示例()
    {
        // 任务卡示例：233.9 与 234.1 同桶（差 0.2s，桶 78 覆盖 [232.5, 235.5)）
        Assert.Equal(Key("T", "A", 233.9), Key("T", "A", 234.1));
        // 远离边界的典型 ±3s 内抖动：233.0 与 235.4（差 2.4s）同桶
        Assert.Equal(Key("T", "A", 233.0), Key("T", "A", 235.4));
    }

    [Fact]
    public void 正负4秒_不同桶()
    {
        Assert.NotEqual(Key("T", "A", 233.9), Key("T", "A", 237.9)); // 差 4.0s
        Assert.NotEqual(Key("T", "A", 233.9), Key("T", "A", 250.0)); // 差 16.1s
    }

    [Fact]
    public void 边界抖动_不串桶()
    {
        // 同一曲目围绕上报值 234.0 抖动 ±0.2s：键稳定
        Assert.Equal(Key("T", "A", 234.0), Key("T", "A", 233.8));
        Assert.Equal(Key("T", "A", 234.0), Key("T", "A", 234.2));
        // 围绕 235.0 抖动 ±0.1s（边界在 235.5）：键稳定
        Assert.Equal(Key("T", "A", 235.0), Key("T", "A", 235.1));
        Assert.Equal(Key("T", "A", 235.0), Key("T", "A", 234.9));
    }

    [Fact]
    public void 桶边界固有属性_差3秒内可跨桶_与注释一致()
    {
        // 分桶在边界两侧存在"差 < 3s 却跨桶"的固有情形（CacheKey 注释已说明）：
        // 235.49 → 桶 78，235.51 → 桶 79。锁定该行为，防止舍入策略被静默改动。
        Assert.NotEqual(Key("T", "A", 235.49), Key("T", "A", 235.51));
    }

    [Fact]
    public void 时长跨桶_即使标题艺人相同_不同键()
    {
        Assert.NotEqual(Key("T", "A", 234.0), Key("T", "A", 244.0)); // 桶 78 vs 桶 81
    }
}
