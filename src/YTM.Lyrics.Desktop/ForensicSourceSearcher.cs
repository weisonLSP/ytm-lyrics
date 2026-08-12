using YTM.Lyrics.Core.Cleaning;
using YTM.Lyrics.Core.Engine;

namespace YTM.Lyrics.Desktop;

/// <summary>
/// TASK-401 取证用假搜索源（沙箱 Schannel 死（ADR-11），真实源搜索不可验；不参与生产路径）。
///
/// 配合 --mock-engine=manual 注入到真实 LyricsEngine：Core 全部代码路径真实
/// （缓存读写/打分/绑定/解析缝），唯一假的环节是"网络"——本类固定返回：
/// - 搜索：与查询标题一致的单候选（自动搜索打分可达 High，手动搜索过歌名硬门槛）；
/// - 取词：预置 QRC 样本原文（真实解析缝解析）。
/// 自测证据（手动搜索→绑定→重播命中）由此完整走真实引擎逻辑链。
/// </summary>
internal sealed class ForensicSourceSearcher : ISourceSearcher
{
    private readonly string _sampleQrc;

    public ForensicSourceSearcher(string sampleQrc) => _sampleQrc = sampleQrc;

    public LyricsSourceKind Source => LyricsSourceKind.QQMusic;

    public Task<SourceSearchOutcome> SearchAsync(CleanedTrack track, double durationSec, CancellationToken ct = default)
    {
        // 候选标题 = 查询标题：自动搜索（清洗后曲目）→ 歌名/艺人/时长全匹配 → High；
        // 手动搜索（keyword 作标题、无艺人、durationSec=0）→ 歌名 Perfect 过硬门槛、时长维 0 分。
        var title = string.IsNullOrWhiteSpace(track.Title) ? "Forensic Track" : track.Title;
        var artist = track.ArtistCandidates.Count > 0 ? track.ArtistCandidates[0] : "Forensic Artist";
        var duration = durationSec > 0 ? durationSec : 240; // 手动搜索无时长信息 → 展示用占位时长
        var candidate = new SourceSearchResult(
            Source, "forensic-id", title, new[] { artist }, null, duration);
        return Task.FromResult(new SourceSearchOutcome(true, new[] { candidate }, null));
    }

    public Task<SourceLyricsResult> GetLyricsAsync(string sourceId, CancellationToken ct = default)
        => Task.FromResult(new SourceLyricsResult(true, LyricsRawFormat.Qrc, _sampleQrc, null));
}
