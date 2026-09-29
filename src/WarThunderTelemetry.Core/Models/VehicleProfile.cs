namespace WarThunderTelemetry.Core.Models;

/// <summary>
/// 载具档案：告警阈值的来源。
/// <para>
/// 数据取自官方 wiki 的性能参数区（如 <c>https://wiki.warthunder.com/unit/la-9</c>），
/// 对应页面上的 <c>Max Speed Limit (IAS)</c>、<c>Mach Number Limit</c>、
/// <c>G limit</c>、<c>Flap Speed Limit (IAS)</c>、<c>Gear Speed Limit (IAS)</c>、
/// <c>Rate of Climb</c>、<c>Turn time</c>、<c>Max altitude</c>。
/// </para>
/// <para>
/// 全部字段可空。<b>未能核实的数据一律留 null 走兵种兜底，不编造数值</b>
/// —— 错误的阈值比没有阈值更危险。
/// </para>
/// </summary>
public sealed record VehicleProfile
{
    /// <summary>
    /// 接口返回的载具代号（<c>indicators.type</c>），如 <c>la_9</c>。
    /// <para>
    /// 从 JSON 加载时由字典键自动填入，因此数据文件中无需重复书写该字段，
    /// 也不能标记为 <c>required</c>（否则缺省时会反序列化失败）。
    /// </para>
    /// </summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// 显示名，如 <c>La-9</c>。缺省时由加载逻辑回退为载具代号。
    /// </summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>兵种：<c>air</c> / <c>tank</c>。</summary>
    public string Army { get; init; } = "air";

    /// <summary>角色：<c>fighter</c> / <c>attacker</c> / <c>bomber</c> / <c>helicopter</c>。</summary>
    public string Role { get; init; } = "fighter";

    /// <summary>所属国家（仅用于展示与分组）。</summary>
    public string? Nation { get; init; }

    /// <summary>超速阈值（km/h），源自 Max Speed Limit (IAS)。</summary>
    public double? MaxSpeedIas { get; init; }

    /// <summary>马赫数上限，源自 Mach Number Limit。活塞机通常为 null。</summary>
    public double? MachLimit { get; init; }

    /// <summary>正向过载上限（g），源自 G limit「≈ -8/13 G」中的 13。</summary>
    public double? GLimitPositive { get; init; }

    /// <summary>负向过载上限（g，取绝对值），源自同一字段中的 8。</summary>
    public double? GLimitNegative { get; init; }

    /// <summary>失速速度（km/h）。官方参数区不直接给出，按兵种估算或留空。</summary>
    public double? StallSpeedIas { get; init; }

    /// <summary>起落架放下时的速度上限（km/h），源自 Gear Speed Limit (IAS)。</summary>
    public double? GearSpeedLimit { get; init; }

    /// <summary>襟翼放下时的速度上限（km/h，取战斗档 C 值）。</summary>
    public double? FlapSpeedLimit { get; init; }

    /// <summary>爬升率（m/s），源自 Rate of Climb。仅用于展示参考，不参与告警。</summary>
    public double? RateOfClimb { get; init; }

    /// <summary>盘旋一周耗时（s），源自 Turn time。</summary>
    public double? TurnTime { get; init; }

    /// <summary>实用升限（m），源自 Max altitude。</summary>
    public double? MaxAltitude { get; init; }

    /// <summary>数据来源标注（如 <c>wiki-rb</c>）。</summary>
    public string? Source { get; init; }
}
