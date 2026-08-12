using System.Reflection;
using YTM.Lyrics.Core.Engine;
using YTM.Lyrics.Core.External.Sources;

namespace YTM.Lyrics.Core.Tests.External.Sources;

/// <summary>
/// QQ 源适配器搜索路径单测（TASK-205F 根因修复验证）。
///
/// 背景：Helper 的 QQMusic.Api.Search（musicu.fcg DoSearchForQQMusicDesktop）模块级失效
/// （2026-08-12 Python/OpenSSL 真实请求取证：req_1.code=2001/ein=20/sum=0/song.list=[]），
/// 适配器改走 client_search_cp 老接口。本测试用**真实响应样本**（抓取于 2026-08-12，
/// tools/t205f/qq_client_search_cp_sample.json，EmbeddedResource 打包）mock 验证解析路径，
/// 并覆盖空响应/结构缺失/ID 回退等防御分支。请求构造（BuildSearchUrl）与解析（ParseSearchResponse）
/// 均为 internal 纯函数，不发起真实网络。
/// </summary>
public class QqMusicSourceSearcherTests
{
    private const LyricsSourceKind Source = LyricsSourceKind.QQMusic;

    private static string LoadRealSample()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("YTM.Lyrics.Core.Tests.Resources.qq_client_search_cp_sample.json")
            ?? throw new InvalidOperationException("真实响应样本缺失（EmbeddedResource）");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void 真实样本_解析出20条候选_字段映射正确()
    {
        var json = LoadRealSample();

        var outcome = QqMusicSourceSearcher.ParseSearchResponse(json, Source);

        Assert.True(outcome.Success);
        Assert.Null(outcome.ErrorMessage);
        Assert.NotNull(outcome.Results);
        Assert.Equal(20, outcome.Results.Count);

        // 首条 = 晴天/周杰伦（走查日志同一首歌）
        var first = outcome.Results[0];
        Assert.Equal("97773", first.SourceId);             // songid → SourceId（取词 musicid 同键）
        Assert.Equal("晴天", first.Title);
        Assert.Equal(new[] { "周杰伦" }, first.Artists);
        Assert.Equal("叶惠美", first.Album);
        Assert.Equal(269, first.DurationSec);              // interval 单位秒
        Assert.Equal(Source, first.Source);
    }

    [Fact]
    public void 真实样本_无重复ID_可全部用于取词()
    {
        var json = LoadRealSample();

        var outcome = QqMusicSourceSearcher.ParseSearchResponse(json, Source);

        var ids = outcome.Results!.Select(r => r.SourceId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count()); // 无空 ID（取词路径全可用）
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id)));
    }

    [Fact]
    public void 空响应_失败无数据()
    {
        var outcome = QqMusicSourceSearcher.ParseSearchResponse("", Source);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Results);
    }

    [Fact]
    public void 非JSON响应_失败并带解析错误()
    {
        var outcome = QqMusicSourceSearcher.ParseSearchResponse("<html>blocked</html>", Source);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Results);
        Assert.Contains("解析失败", outcome.ErrorMessage);
    }

    [Fact]
    public void 结构缺失song_list_失败无数据()
    {
        var outcome = QqMusicSourceSearcher.ParseSearchResponse("{\"code\":0,\"data\":{}}", Source);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Results);
    }

    [Fact]
    public void song_list为空_失败无数据()
    {
        var outcome = QqMusicSourceSearcher.ParseSearchResponse(
            "{\"code\":0,\"data\":{\"song\":{\"list\":[]}}}", Source);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Results);
    }

    [Fact]
    public void 顶层code非零_失败()
    {
        var outcome = QqMusicSourceSearcher.ParseSearchResponse(
            "{\"code\":-30000,\"data\":{\"song\":{\"list\":[{\"songid\":1}]}}}", Source);

        Assert.False(outcome.Success);
        Assert.Contains("-30000", outcome.ErrorMessage);
    }

    [Fact]
    public void songid缺失_回退songmid作为SourceId()
    {
        var json = "{\"code\":0,\"data\":{\"song\":{\"list\":[" +
                   "{\"songmid\":\"0039MnYb0qxYhV\",\"songname\":\"晴天\",\"singer\":[{\"name\":\"周杰伦\"}],\"interval\":269}]}}}";

        var outcome = QqMusicSourceSearcher.ParseSearchResponse(json, Source);

        var result = Assert.Single(outcome.Results!);
        Assert.Equal("0039MnYb0qxYhV", result.SourceId);   // songid 缺失 → songmid
        Assert.Equal("晴天", result.Title);
        Assert.Equal(new[] { "周杰伦" }, result.Artists);
        Assert.Equal(269, result.DurationSec);
    }

    [Fact]
    public void songid与songmid都缺失_该条目被跳过()
    {
        var json = "{\"code\":0,\"data\":{\"song\":{\"list\":[" +
                   "{\"songname\":\"无ID条目\"},{\"songid\":5,\"songname\":\"正常条目\"}]}}}";

        var outcome = QqMusicSourceSearcher.ParseSearchResponse(json, Source);

        var result = Assert.Single(outcome.Results!);
        Assert.Equal("5", result.SourceId);
        Assert.Equal("正常条目", result.Title);
    }

    [Fact]
    public void BuildSearchUrl_关键词URL编码()
    {
        var url = QqMusicSourceSearcher.BuildSearchUrl("晴天 周杰伦");

        Assert.StartsWith("https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w=", url);
        Assert.Contains("%E6%99%B4%E5%A4%A9%20%E5%91%A8%E6%9D%B0%E4%BC%A6", url); // 晴天 周杰伦 UTF-8 编码
        Assert.Contains("t=0&p=1&n=20&format=json&cr=1&aggr=1", url);
    }
}
