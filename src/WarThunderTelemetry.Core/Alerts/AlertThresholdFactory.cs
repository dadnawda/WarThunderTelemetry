using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Vehicles;

namespace WarThunderTelemetry.Core.Alerts;

/// <summary>
/// 把载具档案转换成告警阈值。
/// <para>
/// 这是「按载具自动设定告警阈值」链路的中段：
/// 载具代号 → 载具库 / 兵种兜底 → 本转换 → 规则引擎评估。
/// </para>
/// </summary>
public static class AlertThresholdFactory
{
    /// <summary>
    /// 从载具档案生成阈值集合。
    /// </summary>
    /// <param name="profile">载具档案（可能是兜底档案）。</param>
    /// <param name="isExactMatch">是否命中了库中的具体载具，用于标注来源。</param>
    public static AlertThresholds FromProfile(VehicleProfile profile, bool isExactMatch = true)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new AlertThresholds
        {
            MaxSpeedIas = profile.MaxSpeedIas,
            MachLimit = profile.MachLimit,
            GLimitPositive = profile.GLimitPositive,
            GLimitNegative = profile.GLimitNegative,
            StallSpeedIas = profile.StallSpeedIas,
            GearSpeedLimit = profile.GearSpeedLimit,
            FlapSpeedLimit = profile.FlapSpeedLimit,
            FuelLowPercent = 15.0,
            FuelCapacityKg = null,
            SourceVehicleType = isExactMatch ? profile.Type : null,
        };
    }

    /// <summary>
    /// 按载具代号与兵种直接生成阈值（内部完成库查找与兜底）。
    /// </summary>
    public static AlertThresholds Create(
        VehicleDatabase database,
        string? vehicleType,
        string? army)
    {
        ArgumentNullException.ThrowIfNull(database);

        var profile = database.ResolveOrFallback(vehicleType, army, out var isExact);
        return FromProfile(profile, isExact);
    }
}
