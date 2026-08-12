namespace YTM.Lyrics.Core.Timeline;

/// <summary>
/// 当前行二分定位（TDD §3.4 冻结，TDD §3.5 语义）。
/// </summary>
public static class TimelineNavigator
{
    /// <summary>
    /// 返回 position 时刻"应高亮显示"的歌词行索引。
    /// </summary>
    /// <remarks>
    /// 边界语义（与 UI 渲染约定）：
    /// <list type="bullet">
    /// <item>空文档 → 返回 -1，UI 显示待机/无歌词。</item>
    /// <item>首行前（position &lt; Lines[0].Start，含歌曲未开始）→ 返回 -1，UI 显示待机。</item>
    /// <item>行间空隙（Lines[i].End &lt; position &lt; Lines[i+1].Start；End 为 null 时即
    /// Lines[i].Start ≤ position &lt; Lines[i+1].Start）→ 保持返回 i：上一行持续显示到下一行开始。</item>
    /// <item>末行后（position ≥ Lines[^1].Start，含歌曲已结束）→ 返回末行索引，停留最后一行不回落。</item>
    /// <item>position 恰好等于某行 Start → 命中该行（含与上一行 End 重合的接缝，取后一行）。</item>
    /// </list>
    /// 前置条件：doc.Lines 已按 Start 升序排列（见 <see cref="LyricDocument"/>），否则结果未定义。
    /// 实现：二分查找第一个 Start &gt; position 的行（upper_bound），返回其前一索引，O(log n)。
    /// </remarks>
    public static int FindCurrentLineIndex(LyricDocument doc, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var lines = doc.Lines;
        int lo = 0, hi = lines.Count; // [lo, hi)
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (lines[mid].Start <= position)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        // lo == 第一个 Start > position 的索引；lo == 0 → 首行前 → -1；lo == Count → 末行后 → 末行
        return lo - 1;
    }
}
