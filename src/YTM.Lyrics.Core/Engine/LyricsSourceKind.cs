namespace YTM.Lyrics.Core.Engine;

/// <summary>
/// 歌词来源枚举（TDD §3.3 冻结）。Cache 层以 (int) 形式持久化到 LiteDB，
/// 新增来源只能追加枚举值，禁止重排或改值，否则旧缓存反序列化会错位。
/// </summary>
public enum LyricsSourceKind
{
    QQMusic,
    Netease,
    Kugou,
    Soda,
    AppleMusic,
    Musixmatch,
}
