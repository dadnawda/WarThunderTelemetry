namespace WarThunderTelemetry.Core.Parsing;

/// <summary>
/// 字段值的取值方式。
/// </summary>
public enum FieldKind
{
    /// <summary>普通数值，直取。</summary>
    Numeric = 0,

    /// <summary>多字段求和。典型例子：<c>ammo</c> 要累加所有 <c>ammo*</c> 字段。</summary>
    Sum = 1,

    /// <summary>字符串值（如机型代号 <c>type</c>）。</summary>
    Text = 2,

    /// <summary>布尔／状态值（如起落架放下与否）。</summary>
    Flag = 3,

    /// <summary>派生指标，来自 <c>IMetricProvider</c> 而非游戏接口。</summary>
    Derived = 4,
}

/// <summary>
/// 字段分类，用于分组显示与默认勾选策略。
/// </summary>
public enum FieldCategory
{
    /// <summary>核心飞行数据（速度、高度、姿态）。</summary>
    Flight = 0,

    /// <summary>动力系统（油门、转速、温度）。</summary>
    Engine = 1,

    /// <summary>操纵面（副翼、升降舵、方向舵、襟翼、起落架）。</summary>
    Control = 2,

    /// <summary>武器与弹药。</summary>
    Weapon = 3,

    /// <summary>燃油。</summary>
    Fuel = 4,

    /// <summary>载具信息（机型、兵种）。</summary>
    Vehicle = 5,

    /// <summary>本地计算的派生指标。</summary>
    Derived = 6,
}

/// <summary>
/// 单个字段的描述元数据。
/// <para>
/// 8111 接口的字段名极不稳定且含空格逗号（如 <c>"IAS, km/h"</c>、<c>"throttle 1, %"</c>），
/// 因此每个字段登记一组候选别名，按顺序匹配，命中即止。
/// </para>
/// </summary>
public sealed record FieldDescriptor
{
    /// <summary>字段稳定标识（英文小写下划线），用于配置持久化与代码引用。</summary>
    public required string Id { get; init; }

    /// <summary>中文显示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>单位符号，无量纲时为空串。</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>分类。</summary>
    public FieldCategory Category { get; init; } = FieldCategory.Flight;

    /// <summary>取值方式。</summary>
    public FieldKind Kind { get; init; } = FieldKind.Numeric;

    /// <summary>
    /// 候选别名（原始字段名）。按顺序尝试，第一个命中者生效。
    /// 真实字段名须排在前面，兼容用的旧名／异名排后面。
    /// 匹配时忽略大小写与空格，因此这里的写法只需保证可读。
    /// </summary>
    public required IReadOnlyList<string> Aliases { get; init; }

    /// <summary>是否默认勾选显示。</summary>
    public bool DefaultSelected { get; init; }

    /// <summary>
    /// 值是否需要乘以系数。默认 1。
    /// <para>
    /// 注意：8111 的 <c>throttle N, %</c> 本身就是百分比，<b>不要</b>乘 100；
    /// 而 <c>/indicators</c> 的 <c>throttle</c> 是 0~1 小数，此时才需要乘 100。
    /// </para>
    /// </summary>
    public double Multiplier { get; init; } = 1.0;

    /// <summary>展示时的小数位数。小于 0 表示按数值大小自动决定。</summary>
    public int Decimals { get; init; } = -1;

    /// <summary>数值超过该量级时以「k」为单位缩写（用于 RPM 等）。</summary>
    public bool AbbreviateLarge { get; init; }

    /// <summary>
    /// 该字段优先从哪个端点取。为空则不限制，按 state → indicators → derived 顺序回退。
    /// </summary>
    public string? PreferredEndpoint { get; init; }

    /// <summary>是否属于按发动机编号展开的多值字段（如 <c>throttle N, %</c>）。</summary>
    public bool IsPerEngine { get; init; }
}
