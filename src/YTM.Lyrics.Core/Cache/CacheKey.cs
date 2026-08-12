using System.Security.Cryptography;
using System.Text;
using YTM.Lyrics.Core.Cleaning;

namespace YTM.Lyrics.Core.Cache;

/// <summary>
/// 缓存键计算（TDD §3.5 冻结契约 / PRD F6）。
///
/// 键 = sha1(Title.ToLowerInvariant + "|" + 主艺人.ToLowerInvariant + "|" + durationBucket)
///
/// durationBucket 归并策略（冻结，PRD F6「±3s 归并」）：
///   bucket = (int)Math.Round(durationSec / 3.0, MidpointRounding.AwayFromZero)
/// - 桶宽 3s，桶边界位于 3k ± 1.5 处；同一桶内任意两值相差 &lt; 3s。
/// - 同一曲目上报抖动（如 233.9 / 234.1，差 0.2s）落在同一桶，键稳定；±4s 必然跨桶。
/// - 边界取整显式 AwayFromZero：边界值归入上桶，规避 Math.Round 默认银行家舍入造成的
///   偶/奇桶抖动（对同一输入必须得到确定唯一的桶）。
/// 注：任何无重叠分桶在边界两侧都存在"差 &lt; 3s 却跨桶"的必然情形（235.49→78 / 235.51→79），
/// 这是 ±3s 分桶的固有属性；YTM 时长来自曲目元数据、逐次播放为确定值，实际不产生边界抖动。
///
/// 主艺人 = 清洗后艺人候选列表的首位（TASK-201 Cleaning 落地后，
/// 由 CacheKey.Compute(CleanedTrack, double) 转发重载取 ArtistCandidates[0]）。
/// </summary>
public static class CacheKey
{
    /// <summary>计算缓存键。入参必须是清洗后（CleanedTrack 语义）的标题与主艺人。</summary>
    public static string Compute(string cleanTitle, string primaryArtist, double durationSec)
    {
        var bucket = (int)Math.Round(durationSec / 3.0, MidpointRounding.AwayFromZero);
        var raw = string.Concat(cleanTitle.ToLowerInvariant(), "|",
                                primaryArtist.ToLowerInvariant(), "|",
                                bucket.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 转发重载（TASK-203 补齐 TDD §3.5 预留的口子）：主艺人 = 清洗后艺人候选列表首位
    /// （ArtistCandidates[0]，为空则按空串处理）。Engine 消费 Cleaning 产物时的标准入口。
    /// </summary>
    public static string Compute(CleanedTrack track, double durationSec)
    {
        ArgumentNullException.ThrowIfNull(track);
        var primary = track.ArtistCandidates.Count > 0 ? track.ArtistCandidates[0] : string.Empty;
        return Compute(track.Title, primary, durationSec);
    }
}
