using System.Globalization;
using WarThunderTelemetry.Core.Abstractions;

namespace WarThunderTelemetry.Core.Parsing;

/// <summary>
/// 多别名容错字段解析器 —— 本项目最核心的组件。
/// <para>
/// 8111 接口的字段名在不同机型、不同版本间会变化甚至缺失
/// （活塞机没有 <c>M</c>、部分载具没有油温），直接字典查找极脆弱。
/// 本解析器把「规范化键 → 别名候选 → 回退顺序 → 空值降级」这套逻辑收敛在一处。
/// </para>
/// <para>
/// 匹配策略：把原始键规范化（去空格、转小写）后与别名的规范化形式比对，
/// 保证 <c>"IAS, km/h"</c> 与 <c>"ias,km/h"</c> 视为同一个键。
/// </para>
/// </summary>
public sealed class AliasResolver
{
    private readonly Dictionary<string, FieldDescriptor> _byId;
    private readonly Dictionary<string, FieldDescriptor> _byNormalizedAlias;

    /// <summary>
    /// 构造解析器。
    /// </summary>
    /// <param name="catalog">字段目录。</param>
    /// <exception cref="ArgumentException">存在重复 Id 或重复别名时抛出。</exception>
    public AliasResolver(IEnumerable<FieldDescriptor> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        _byId = new Dictionary<string, FieldDescriptor>(StringComparer.OrdinalIgnoreCase);
        _byNormalizedAlias = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal);

        foreach (var field in catalog)
        {
            if (!_byId.TryAdd(field.Id, field))
            {
                throw new ArgumentException($"字段 Id 重复：{field.Id}", nameof(catalog));
            }

            foreach (var alias in field.Aliases)
            {
                var key = Normalize(alias);
                if (key.Length == 0)
                {
                    continue;
                }

                // 先登记者优先：真实字段名排在别名列表前面，因此不会被后来的兼容名覆盖。
                _byNormalizedAlias.TryAdd(key, field);
            }
        }
    }

    /// <summary>
    /// 规范化字段键：移除全部空白字符并转小写。
    /// <para>
    /// 这样 <c>"IAS, km/h"</c>、<c>"IAS,km/h"</c>、<c>"ias, km/h"</c> 归一为同一个键。
    /// </para>
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        Span<char> buffer = raw.Length <= 128 ? stackalloc char[raw.Length] : new char[raw.Length];
        var length = 0;

        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    /// 按规范化键反查字段描述。查不到返回 <c>null</c>。
    /// </summary>
    public FieldDescriptor? ResolveDescriptor(string normalizedKey)
    {
        return _byNormalizedAlias.TryGetValue(Normalize(normalizedKey), out var field)
            ? field
            : null;
    }

    /// <summary>
    /// 按字段 Id 取描述。查不到返回 <c>null</c>。
    /// </summary>
    public FieldDescriptor? GetById(string id)
    {
        return _byId.TryGetValue(id, out var field) ? field : null;
    }

    /// <summary>
    /// 是否登记了该 Id。
    /// </summary>
    public bool ContainsId(string id) => _byId.ContainsKey(id);

    /// <summary>目录中登记的字段总数。</summary>
    public int Count => _byId.Count;

    /// <summary>目录中全部字段（按登记顺序）。</summary>
    public IReadOnlyCollection<FieldDescriptor> Fields => _byId.Values;

    /// <summary>
    /// 从给定的若干数据源中解析一个字段的值。
    /// </summary>
    /// <param name="descriptor">字段描述。</param>
    /// <param name="sources">
    /// 数据源，按优先级排列。典型顺序为 state → indicators → derived。
    /// </param>
    /// <returns>解析结果；未命中时 <see cref="FieldValue.Found"/> 为 <c>false</c>。</returns>
    public FieldValue Resolve(FieldDescriptor descriptor, params EndpointFields[] sources)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(sources);

        return descriptor.Kind switch
        {
            FieldKind.Sum => ResolveSum(descriptor, sources),
            FieldKind.Text => ResolveText(descriptor, sources),
            FieldKind.Flag => ResolveFlag(descriptor, sources),
            FieldKind.Derived => ResolveFromDerived(descriptor, sources),
            _ => ResolveNumeric(descriptor, sources),
        };
    }

    private static FieldValue ResolveNumeric(FieldDescriptor descriptor, EndpointFields[] sources)
    {
        foreach (var source in sources)
        {
            if (source.IsEmpty)
            {
                continue;
            }

            foreach (var alias in descriptor.Aliases)
            {
                if (!source.TryGet(alias, out var rawValue))
                {
                    continue;
                }

                if (TryParseNumber(rawValue, out var number))
                {
                    return FieldValue.OfNumber(number * descriptor.Multiplier);
                }
            }
        }

        return FieldValue.NotFound;
    }

    private static FieldValue ResolveFromDerived(FieldDescriptor descriptor, EndpointFields[] sources)
    {
        // 派生指标由主逻辑单独注入到一个专用数据源中，同样按别名匹配。
        foreach (var source in sources)
        {
            if (!source.IsDerived || source.IsEmpty)
            {
                continue;
            }

            foreach (var alias in descriptor.Aliases)
            {
                if (source.TryGet(alias, out var rawValue) && TryParseNumber(rawValue, out var number))
                {
                    return FieldValue.OfNumber(number * descriptor.Multiplier);
                }
            }
        }

        return FieldValue.NotFound;
    }

    private static FieldValue ResolveText(FieldDescriptor descriptor, EndpointFields[] sources)
    {
        foreach (var source in sources)
        {
            if (source.IsEmpty)
            {
                continue;
            }

            foreach (var alias in descriptor.Aliases)
            {
                if (source.TryGet(alias, out var rawValue) && !string.IsNullOrWhiteSpace(rawValue))
                {
                    return FieldValue.OfText(rawValue.Trim());
                }
            }
        }

        return FieldValue.NotFound;
    }

    private static FieldValue ResolveFlag(FieldDescriptor descriptor, EndpointFields[] sources)
    {
        foreach (var source in sources)
        {
            if (source.IsEmpty)
            {
                continue;
            }

            foreach (var alias in descriptor.Aliases)
            {
                if (!source.TryGet(alias, out var rawValue))
                {
                    continue;
                }

                if (TryParseNumber(rawValue, out var number))
                {
                    // 数值型：0~1 的小数或 0~100 的百分比都视为「已放下」的判据。
                    var normalized = number > 1.0 ? number / 100.0 : number;
                    return FieldValue.OfNumber(normalized);
                }

                // 字符串型：DOWN / UP / true / false。
                var text = rawValue.Trim();
                if (text.Equals("down", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("1", StringComparison.Ordinal))
                {
                    return FieldValue.OfNumber(1.0);
                }

                if (text.Equals("up", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                    text.Equals("0", StringComparison.Ordinal))
                {
                    return FieldValue.OfNumber(0.0);
                }
            }
        }

        return FieldValue.NotFound;
    }

    private static FieldValue ResolveSum(FieldDescriptor descriptor, EndpointFields[] sources)
    {
        // 求和字段（如弹药）：把匹配任一别名的全部数值累加。
        // 采用「前缀匹配」而非精确匹配，因为接口的多发弹药字段名形如
        // ammo1 / ammo2 / ammo3，而别名表里只登记 ammo。
        var sum = 0.0;
        var found = false;

        foreach (var source in sources)
        {
            if (source.IsEmpty)
            {
                continue;
            }

            foreach (var alias in descriptor.Aliases)
            {
                var normalizedAlias = Normalize(alias);
                if (normalizedAlias.Length == 0)
                {
                    continue;
                }

                foreach (var (key, value) in source.RawValues)
                {
                    // 命中条件：规范化后与别名完全相等，或以「别名 + 数字」形式出现。
                    var normalizedKey = Normalize(key);
                    if (!normalizedKey.StartsWith(normalizedAlias, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var suffix = normalizedKey.AsSpan(normalizedAlias.Length);
                    if (suffix.Length > 0 && !IsAllDigits(suffix))
                    {
                        continue;
                    }

                    if (TryParseNumber(value, out var number))
                    {
                        sum += number;
                        found = true;
                    }
                }
            }
        }

        return found ? FieldValue.OfNumber(sum * descriptor.Multiplier) : FieldValue.NotFound;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 解析数值，容忍前后空格与单位后缀残留。
    /// </summary>
    public static bool TryParseNumber(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.AsSpan().Trim();

        // 去掉包裹的引号（部分字段以字符串形式返回数值）。
        if (text.Length >= 2 && (text[0] == '"' && text[^1] == '"'))
        {
            text = text[1..^1].Trim();
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        // 兜底：截取开头的数字部分（应对 "850 km/h" 这类混入单位的值）。
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] is '.' or '-' or '+'))
        {
            end++;
        }

        return end > 0 &&
               double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// 解析结果。<c>Found</c> 为 <c>false</c> 时，UI 应显示 <c>–</c> 而不是留空白或报错。
/// </summary>
public readonly struct FieldValue
{
    private FieldValue(bool found, double? number, string? text)
    {
        Found = found;
        Number = number;
        Text = text;
    }

    /// <summary>是否成功解析到值。</summary>
    public bool Found { get; }

    /// <summary>数值结果（<see cref="FieldKind.Text"/> 时为 <c>null</c>）。</summary>
    public double? Number { get; }

    /// <summary>文本结果（仅 <see cref="FieldKind.Text"/> 有值）。</summary>
    public string? Text { get; }

    /// <summary>未命中。</summary>
    public static FieldValue NotFound => default;

    /// <summary>构造数值结果。</summary>
    public static FieldValue OfNumber(double value) => new(true, value, null);

    /// <summary>构造文本结果。</summary>
    public static FieldValue OfText(string value) => new(true, null, value);
}
