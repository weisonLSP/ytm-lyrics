using System.Text.Json;

namespace YTM.Lyrics.Core.Cleaning;

/// <summary>
/// 规则表加载：内嵌资源（默认）或外部文件（热加载覆盖）。解析失败抛异常，由调用方决定保留旧规则。
/// </summary>
public static class CleaningRuleLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>从 Core 程序集内嵌资源加载默认规则表（csproj EmbeddedResource: Cleaning/Rules/cleaning-rules.json）。</summary>
    public static CleaningRuleTable LoadEmbedded()
    {
        var asm = typeof(CleaningRuleLoader).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("cleaning-rules.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("内嵌规则表 cleaning-rules.json 缺失（检查 csproj EmbeddedResource）");
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"内嵌资源读取失败: {name}");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>从外部文件加载规则表（不存在则抛 FileNotFoundException）。</summary>
    public static CleaningRuleTable LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"清洗规则表文件不存在: {path}", path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>解析并校验规则表 JSON。</summary>
    public static CleaningRuleTable Parse(string json)
    {
        var table = JsonSerializer.Deserialize<CleaningRuleTable>(json, JsonOptions)
            ?? throw new InvalidDataException("规则表 JSON 解析结果为空");
        table.Validate();
        return table;
    }

    internal static CompiledRules LoadEmbeddedCompiled() => CompiledRules.Compile(LoadEmbedded());

    internal static CompiledRules LoadFileCompiled(string path) => CompiledRules.Compile(LoadFile(path));
}
