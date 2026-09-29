namespace WarThunderTelemetry.Core.Parsing;

/// <summary>
/// 某个端点返回的字段集合。
/// <para>
/// 内部维护规范化键索引，使 <c>"IAS, km/h"</c> 这类含空格逗号的键可以稳定查询。
/// 同时保留原始键值对，供「全字段表格」原样展示游戏给的一切字段。
/// </para>
/// </summary>
public sealed class EndpointFields
{
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, string> _byNormalizedKey;

    /// <summary>空集合（尚未收到数据时使用）。</summary>
    public static EndpointFields Empty { get; } = new(0);

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="capacity">预估字段数。</param>
    public EndpointFields(int capacity)
    {
        _values = new Dictionary<string, string>(capacity, StringComparer.Ordinal);
        _byNormalizedKey = new Dictionary<string, string>(capacity, StringComparer.Ordinal);
    }

    /// <summary>由原始键值对构造。</summary>
    public EndpointFields(IEnumerable<KeyValuePair<string, string>> pairs)
        : this(pairs is ICollection<KeyValuePair<string, string>> collection ? collection.Count : 32)
    {
        foreach (var pair in pairs)
        {
            Set(pair.Key, pair.Value);
        }
    }

    /// <summary>数据来源端点键（见 <c>TelemetryEndpoint</c>）。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>是否为派生指标源（而非游戏接口）。</summary>
    public bool IsDerived { get; init; }

    /// <summary>是否为空集合。</summary>
    public bool IsEmpty => _values.Count == 0;

    /// <summary>字段数量。</summary>
    public int Count => _values.Count;

    /// <summary>原始键值对（保留游戏原样字段名）。</summary>
    public IReadOnlyDictionary<string, string> RawValues => _values;

    /// <summary>
    /// 写入一个字段。
    /// </summary>
    public void Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        _values[key] = value;

        var normalized = AliasResolver.Normalize(key);
        if (normalized.Length > 0)
        {
            _byNormalizedKey[normalized] = value;
        }
    }

    /// <summary>
    /// 按别名查询。别名同样会被规范化，因此无需在别名表里写死空格与大小写。
    /// </summary>
    public bool TryGet(string alias, out string value)
    {
        return _byNormalizedKey.TryGetValue(AliasResolver.Normalize(alias), out value!);
    }

    /// <summary>
    /// 取数值；不存在或非数值时返回 <c>null</c>。
    /// </summary>
    public double? GetNumber(string alias)
    {
        return TryGet(alias, out var raw) && AliasResolver.TryParseNumber(raw, out var number)
            ? number
            : null;
    }

    /// <summary>
    /// 取文本；不存在或为空时返回 <c>null</c>。
    /// </summary>
    public string? GetText(string alias)
    {
        return TryGet(alias, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;
    }
}
