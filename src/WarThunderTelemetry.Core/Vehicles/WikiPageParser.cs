using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WarThunderTelemetry.Core.Models;

namespace WarThunderTelemetry.Core.Vehicles;

/// <summary>
/// wiki 载具页解析器。
/// <para>
/// 把 <c>wiki.warthunder.com</c> 的 unit 页面 HTML 解析成 <see cref="VehicleProfile"/>。
/// 解析逻辑与 <c>tools/scrape_vehicles.py</c> 保持一致 —— 两处必须同步修改，
/// 否则离线批量建库与运行时单架抓取会得出不同的结果。
/// </para>
/// <para>
/// <b>为什么不用正则一次抓完</b>：wiki 的参数行有三种形态（单值 / 多档 / 标签+值分列），
/// 且 header 偶尔还套一层 tooltip span，因此必须按行块切分后逐块处理。
/// </para>
/// </summary>
public static partial class WikiPageParser
{
    /// <summary>wiki 站点根。</summary>
    public const string BaseUrl = "https://wiki.warthunder.com";

    /// <summary>载具列表页 —— 一次性给出全部载具的真实 slug。</summary>
    public const string AviationUrl = BaseUrl + "/aviation";

    // ------------------------------------------------------------ 正则

    /// <summary>从 /aviation 页抽 slug（含片段标识符，后续再剥）。</summary>
    [GeneratedRegex(@"href=""/unit/([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex SlugRegex();

    /// <summary>页面内嵌的载具 JSON（含 gameId，即接口的 indicators.type）。</summary>
    [GeneratedRegex(
        @"<textarea id=""game-unit-initial""[^>]*>(.*?)</textarea>",
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex InitialJsonRegex();

    /// <summary>显示名。</summary>
    [GeneratedRegex(
        @"class=""game-unit_name"">(.*?)</div>",
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex NameRegex();

    /// <summary>页面标题（显示名的兜底来源）。</summary>
    [GeneratedRegex(@"<title>([^<|]+)", RegexOptions.Compiled)]
    private static partial Regex TitleRegex();

    /// <summary>参数行块。</summary>
    [GeneratedRegex(
        @"<div class=""game-unit_chars-line"">(.*?)</div>\s*(?=<div class=""game-unit_chars-line"">|</div>)",
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex LineBlockRegex();

    /// <summary>参数名（可含嵌套标签）。</summary>
    [GeneratedRegex(
        @"class=""game-unit_chars-header""[^>]*>(.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex HeaderRegex();

    /// <summary>参数值（可含嵌套的档位 span）。</summary>
    [GeneratedRegex(
        @"class=""game-unit_chars-value""[^>]*>(.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex ValueRegex();

    /// <summary>Collections 条目。</summary>
    [GeneratedRegex(
        @"<a href=""/collections/([\w-]+)/([\w-]+)""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex CollectionRegex();

    /// <summary>Collection 显示名。</summary>
    [GeneratedRegex(@"class=""name"">([^<]*)<", RegexOptions.Compiled)]
    private static partial Regex CollectionNameRegex();

    /// <summary>任意标签。</summary>
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex TagRegex();

    /// <summary>连续空白。</summary>
    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    /// <summary>数值（含负号与小数）。</summary>
    [GeneratedRegex(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled)]
    private static partial Regex NumberRegex();

    /// <summary>G 限值形如 <c>≈ -8/13 G</c>。</summary>
    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\s*/\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled)]
    private static partial Regex GLimitRegex();

    // ------------------------------------------------------------ 公开解析

    /// <summary>
    /// 从 /aviation 页解析全部载具 slug。
    /// </summary>
    public static IReadOnlyList<string> ParseSlugs(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in SlugRegex().Matches(html))
        {
            var slug = TrimUrlTail(match.Groups[1].Value);
            if (slug.Length > 0)
            {
                set.Add(slug);
            }
        }

        return [.. set.OrderBy(static s => s, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 去掉 URL 尾部的片段标识符与查询串。
    /// <para>
    /// 页面里会出现 <c>/unit/b-17e#specification</c> 这类锚点链接，
    /// 直接拿去拼 URL 会 404，必须先剥干净。
    /// </para>
    /// </summary>
    private static string TrimUrlTail(string value)
    {
        var cut = value.AsSpan().IndexOfAny('#', '?', '/');
        return (cut >= 0 ? value[..cut] : value).Trim();
    }

    /// <summary>
    /// 从载具页取 <c>gameId</c>。
    /// <para>
    /// 这是唯一能与游戏接口 <c>indicators.type</c> 对上号的字段，
    /// 必须从页面内嵌 JSON 里读，不能靠 slug 或标题猜。
    /// </para>
    /// </summary>
    public static string? ParseGameId(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var match = InitialJsonRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(match.Groups[1].Value);
            if (document.RootElement.TryGetProperty("gameId", out var element) &&
                element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }
        catch (JsonException)
        {
            // 页面结构变了 —— 当作解析失败，由调用方回退。
        }

        return null;
    }

    /// <summary>
    /// 从载具页解析性能档案。
    /// </summary>
    /// <param name="html">页面 HTML。</param>
    /// <param name="slug">该页在 wiki 上的 slug，用于回填展示名与来源标记。</param>
    /// <returns>解析成功返回档案；页面无参数区时返回 <c>null</c>。</returns>
    public static VehicleProfile? ParseProfile(string html, string slug)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(slug);

        var chars = ParseCharTable(html);
        if (chars.Count == 0)
        {
            return null;
        }

        var (positive, negative) = ParseGLimit(Get(chars, "G limit"));

        return new VehicleProfile
        {
            Type = slug,
            DisplayName = ParseDisplayName(html) ?? slug,
            Army = ParseArmy(html),
            Role = ParseRole(html),
            Nation = ParseNation(html),
            MaxSpeedIas = FirstNumber(Get(chars, "Max Speed Limit (IAS)")),
            MachLimit = FirstNumber(Get(chars, "Mach Number Limit")),
            GLimitPositive = positive,
            GLimitNegative = negative,
            StallSpeedIas = null,
            GearSpeedLimit = FirstNumber(Get(chars, "Gear Speed Limit (IAS)")),
            FlapSpeedLimit = ParseFlap(Get(chars, "Flap Speed Limit (IAS)")),
            RateOfClimb = FirstNumber(Get(chars, "Rate of Climb")),
            TurnTime = FirstNumber(Get(chars, "Turn time")),
            MaxAltitude = FirstNumber(Get(chars, "Max altitude")),
            Source = "wiki-web",
        };
    }

    // ------------------------------------------------------------ 内部实现

    private static string? Get(IReadOnlyDictionary<string, string> table, string key) =>
        table.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// 解析参数表：把 <c>game-unit_chars-line</c> 块拆成「参数名 → 值」字典。
    /// </summary>
    private static Dictionary<string, string> ParseCharTable(string html)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match block in LineBlockRegex().Matches(html))
        {
            var body = block.Groups[1].Value;

            var header = HeaderRegex().Match(body);
            if (!header.Success)
            {
                continue;
            }

            // header 有时还套一层 tooltip span（如 G limit），剥标签后再用。
            var name = StripTags(header.Groups[1].Value);
            if (name.Length == 0)
            {
                continue;
            }

            var values = ValueRegex().Matches(body);
            if (values.Count == 0)
            {
                continue;
            }

            var text = string.Join(' ', values.Select(v => StripTags(v.Groups[1].Value)));
            table.TryAdd(name, CollapseWhitespace(text));
        }

        return table;
    }

    private static string ParseDisplayName(string html)
    {
        var match = NameRegex().Match(html);
        if (match.Success)
        {
            var text = StripTags(match.Groups[1].Value);

            // 国旗有两种残留形态：私用区字符，以及没渲染出来的占位方块（如 U+2584）。
            text = text
                .Where(static c => c is not (>= '\uE000' and <= '\uF8FF'))
                .Aggregate(new System.Text.StringBuilder(), static (sb, c) => sb.Append(c))
                .ToString()
                .TrimStart('▄', '▀', '█', '▓', '▒', '░', '■', '□', '▪', '▫', '*', '※', '·', '•')
                .Trim();

            if (text.Length > 0)
            {
                return text;
            }
        }

        var title = TitleRegex().Match(html);
        return title.Success ? title.Groups[1].Value.Trim() : null!;
    }

    private static string ParseArmy(string html)
    {
        // 页面开头的面包屑会标明类别；地面载具与航空器分属不同分支。
        var head = html.Length > 6000 ? html[..6000] : html;
        return head.Contains("Ground vehicles", StringComparison.Ordinal) ? "tank" : "air";
    }

    private static string ParseRole(string html)
    {
        var raw = ParseCollection(html, "game_roles").Name?.ToLowerInvariant();

        return raw switch
        {
            "fighter" => "fighter",
            "attacker" => "attacker",
            "bomber" => "bomber",
            "bomber/attacker" => "attacker",
            "helicopter" => "helicopter",
            "utility helicopter" => "helicopter",
            "attack helicopter" => "helicopter",
            "tank" => "tank",
            _ => "fighter",
        };
    }

    private static string? ParseNation(string html)
    {
        var (slug, name) = ParseCollection(html, "operator");
        if (slug is null)
        {
            return null;
        }

        var key = slug.StartsWith("country_", StringComparison.Ordinal) ? slug[8..] : slug;

        return key switch
        {
            "ussr" => "USSR",
            "usa" => "USA",
            "germany" => "Germany",
            "britain" => "Britain",
            "japan" => "Japan",
            "china" => "China",
            "italy" => "Italy",
            "france" => "France",
            "sweden" => "Sweden",
            "israel" => "Israel",
            _ => name,
        };
    }

    private static (string? Slug, string? Name) ParseCollection(string html, string category)
    {
        foreach (Match match in CollectionRegex().Matches(html))
        {
            if (!match.Groups[1].Value.Equals(category, StringComparison.Ordinal))
            {
                continue;
            }

            var nameMatch = CollectionNameRegex().Match(match.Groups[3].Value);
            var name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : null;
            return (match.Groups[2].Value, name);
        }

        return (null, null);
    }

    private static double? FirstNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = NumberRegex().Match(text.Replace(",", string.Empty, StringComparison.Ordinal));
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(
            match.Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    /// <summary>解析 <c>≈ -8/13 G</c> → (正向 13, 负向绝对值 8)。</summary>
    private static (double? Positive, double? Negative) ParseGLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var match = GLimitRegex().Match(text);
        if (!match.Success)
        {
            return (null, null);
        }

        var first = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var second = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        // wiki 恒为「负值/正值」，但万一写反了也不至于给出负的上限。
        return (Math.Abs(second), Math.Abs(first));
    }

    /// <summary>
    /// 解析襟翼限速 <c>290 / 451 / 490 km/h</c>（L/T/C 三档），取战斗档（最后一个）。
    /// </summary>
    private static double? ParseFlap(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var matches = NumberRegex().Matches(text.Replace(",", string.Empty, StringComparison.Ordinal));
        if (matches.Count == 0)
        {
            return null;
        }

        return double.TryParse(
            matches[^1].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static string StripTags(string html) =>
        CollapseWhitespace(TagRegex().Replace(html, " "));

    private static string CollapseWhitespace(string text) =>
        WhitespaceRegex().Replace(text, " ").Trim();
}
