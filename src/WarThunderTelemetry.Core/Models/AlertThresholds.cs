namespace WarThunderTelemetry.Core.Models;

/// <summary>
/// 阈值集合：告警规则据此判断是否触发。
/// <para>
/// 由 <c>VehicleDatabase</c> 或 <c>FallbackProfiles</c> 产出，
/// 数值单位统一为 km/h（速度）、g（过载）、米（高度）、秒（时间）。
/// <c>null</c> 表示该载具无此限制或数据未能核实 —— 规则碰到 null 应跳过而不是猜测。
/// </para>
/// </summary>
public sealed record AlertThresholds
{
    /// <summary>超速阈值：指示空速上限（km/h），源自官方 wiki 的 Max Speed Limit (IAS)。</summary>
    public double? MaxSpeedIas { get; init; }

    /// <summary>马赫数上限，源自 Mach Number Limit。活塞机通常为 null。</summary>
    public double? MachLimit { get; init; }

    /// <summary>正向过载上限（g），源自 G limit 形如「≈ -8/13 G」中的 13。</summary>
    public double? GLimitPositive { get; init; }

    /// <summary>负向过载上限（g，取绝对值），源自同一字段中的 8。</summary>
    public double? GLimitNegative { get; init; }

    /// <summary>失速速度（km/h）。官方参数区不直接给出，按机型/兵种估算，仅作参考。</summary>
    public double? StallSpeedIas { get; init; }

    /// <summary>起落架放下时的速度上限（km/h），源自 Gear Speed Limit (IAS)。</summary>
    public double? GearSpeedLimit { get; init; }

    /// <summary>襟翼放下时的速度上限（km/h，取战斗档），源自 Flap Speed Limit (IAS) 的 C 值。</summary>
    public double? FlapSpeedLimit { get; init; }

    /// <summary>低油量告警阈值：剩余燃油百分比（0~100）。</summary>
    public double FuelLowPercent { get; init; } = 15.0;

    /// <summary>满油量（kg），用于换算剩余百分比。源自 Mfuel0, kg 或载具库。</summary>
    public double? FuelCapacityKg { get; init; }

    /// <summary>
    /// 这些阈值来自哪个载具档案。<c>null</c> 表示走了兵种兜底。
    /// </summary>
    public string? SourceVehicleType { get; init; }
}
