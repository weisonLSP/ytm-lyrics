using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// 取证用 mock 引擎（TASK-205 沙箱自测注入，不参与生产路径）。
///
/// 沙箱 Schannel 死（ADR-11），真实源搜索不可验 → 本卡自测走"预置数据直灌"路线：
/// 真实场景（缓存命中 / 负缓存）由 LiteDbLyricsCache 直灌 + 真实 LyricsEngine 完成；
/// 唯一无法走真实链路的是"低置信结果"（真实引擎只在缓存回放时给 High，Low 只来自真实搜索打分），
/// 故提供本 mock：固定返回指定置信度的 LyricsResult，文档用真实解析缝
/// （LyricsDocumentParserBridge）解析预置 QRC 样本，保证歌词内容链路仍然真实。
/// </summary>
internal sealed class ForensicLyricsEngine : ILyricsEngine
{
    private readonly MatchConfidence _confidence;
    private readonly LyricsSourceKind _source;
    private readonly ILyricsDocumentParser _parser;
    private readonly string _sampleQrc;

    public ForensicLyricsEngine(
        MatchConfidence confidence,
        LyricsSourceKind source,
        ILyricsDocumentParser parser,
        string sampleQrc)
    {
        _confidence = confidence;
        _source = source;
        _parser = parser;
        _sampleQrc = sampleQrc;
    }

    public IReadOnlyList<LyricsSourceKind> SourcePriority => SourcePriorityDefaults.Adr9;

    public Task<LyricsResult> GetLyricsAsync(CleanedTrack track, double durationSec, CancellationToken ct = default)
    {
        var doc = _parser.Parse(_sampleQrc, _source, LyricsRawFormat.Qrc);
        return Task.FromResult(new LyricsResult(
            doc,
            _source,
            _confidence,
            FromCache: false,
            UserBound: false,
            OffsetSec: 0));
    }

    // F9 手动搜索/绑定在取证模式下不可用（mock 引擎不接真实搜索源），空结果/失败语义即可。
    public Task<IReadOnlyList<SourceCandidateGroup>> SearchCandidatesAsync(string keyword, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SourceCandidateGroup>>(Array.Empty<SourceCandidateGroup>());

    public Task<LyricsResult> BindLyricsAsync(CleanedTrack track, double durationSec, SourceSearchResult selected, CancellationToken ct = default)
        => Task.FromResult(new LyricsResult(null, null, MatchConfidence.None, FromCache: false, UserBound: false, OffsetSec: 0));
}
