using WarThunderTelemetry.Core.Models;

namespace WarThunderTelemetry.Core.Vehicles;

/// <summary>
/// 兵种级缺省阈值。
/// <para>
/// 当载具库中查不到具体机型时（新载具、非常见型号、库未收录），
/// 按兵种给一套保守的通用阈值，保证告警依然可用。
/// </para>
/// <para>
/// 这些值是<b>保守估计</b>而非精确数据，仅用于兜底 —— 目的是「接近临界时提醒」，
/// 而不是精确复现该机型的真实限制。命中具体载具时一律以载具库为准。
/// </para>
/// </summary>
public static class FallbackProfiles
{
    /// <summary>战斗机缺省阈值。</summary>
    public static VehicleProfile Fighter { get; } = new()
    {
        Type = "__fallback_fighter",
        DisplayName = "未知战斗机",
        Army = "air",
        Role = "fighter",
        MaxSpeedIas = 850,
        MachLimit = 0.85,
        GLimitPositive = 12,
        GLimitNegative = 6,
        StallSpeedIas = 150,
        GearSpeedLimit = 300,
        FlapSpeedLimit = 450,
        Source = "fallback",
    };

    /// <summary>攻击机缺省阈值。</summary>
    public static VehicleProfile Attacker { get; } = new()
    {
        Type = "__fallback_attacker",
        DisplayName = "未知攻击机",
        Army = "air",
        Role = "attacker",
        MaxSpeedIas = 750,
        MachLimit = 0.80,
        GLimitPositive = 9,
        GLimitNegative = 5,
        StallSpeedIas = 150,
        GearSpeedLimit = 300,
        FlapSpeedLimit = 400,
        Source = "fallback",
    };

    /// <summary>轰炸机缺省阈值。</summary>
    public static VehicleProfile Bomber { get; } = new()
    {
        Type = "__fallback_bomber",
        DisplayName = "未知轰炸机",
        Army = "air",
        Role = "bomber",
        MaxSpeedIas = 650,
        MachLimit = 0.75,
        GLimitPositive = 6,
        GLimitNegative = 3,
        StallSpeedIas = 160,
        GearSpeedLimit = 280,
        FlapSpeedLimit = 350,
        Source = "fallback",
    };

    /// <summary>直升机缺省阈值。</summary>
    public static VehicleProfile Helicopter { get; } = new()
    {
        Type = "__fallback_helicopter",
        DisplayName = "未知直升机",
        Army = "air",
        Role = "helicopter",
        MaxSpeedIas = 350,
        MachLimit = null,
        GLimitPositive = 4,
        GLimitNegative = 2,
        StallSpeedIas = null,
        GearSpeedLimit = null,
        FlapSpeedLimit = null,
        Source = "fallback",
    };

    /// <summary>地面载具缺省阈值。</summary>
    public static VehicleProfile Tank { get; } = new()
    {
        Type = "__fallback_tank",
        DisplayName = "未知地面载具",
        Army = "tank",
        Role = "tank",
        Source = "fallback",
    };

    /// <summary>
    /// 按兵种（与可选的机型代号线索）生成兜底档案。
    /// </summary>
    /// <param name="vehicleType">载具代号，可从中推断机型线索；可为 <c>null</c>。</param>
    /// <param name="army">兵种（<c>air</c> / <c>tank</c>）；可为 <c>null</c>。</param>
    public static VehicleProfile Create(string? vehicleType, string? army)
    {
        if (!string.IsNullOrWhiteSpace(army) &&
            army.Trim().Equals("tank", StringComparison.OrdinalIgnoreCase))
        {
            return Tank;
        }

        return GuessFromTypeName(vehicleType);
    }

    /// <summary>
    /// 从载具代号推断机型类别。
    /// <para>
    /// 载具代号常含可辨识片段（<c>bomber</c>、<c>heli</c>、<c>ah-</c>、<c>mi-</c> 等），
    /// 借此在无精确数据时给出更贴合的兜底值。判断不出则按战斗机处理（最常见）。
    /// </para>
    /// <para>
    /// 注意代号里连字符与下划线混用（<c>b-17</c> 与 <c>b_17_e</c> 都会出现），
    /// 因此统一把 <c>_</c> 归一为 <c>-</c> 后再比较。
    /// </para>
    /// </summary>
    private static VehicleProfile GuessFromTypeName(string? vehicleType)
    {
        if (string.IsNullOrWhiteSpace(vehicleType))
        {
            return Fighter;
        }

        var type = vehicleType.Trim().ToLowerInvariant().Replace('_', '-');

        if (type.Contains("bomber", StringComparison.Ordinal) ||
            type.Contains("-b-", StringComparison.Ordinal) ||
            type.StartsWith("b-", StringComparison.Ordinal) ||
            type.StartsWith("pe-", StringComparison.Ordinal) ||
            type.StartsWith("tu-", StringComparison.Ordinal) ||
            type.StartsWith("il-4", StringComparison.Ordinal) ||
            type.StartsWith("ar-", StringComparison.Ordinal) ||
            type.StartsWith("sb-", StringComparison.Ordinal) ||
            type.StartsWith("yer-", StringComparison.Ordinal))
        {
            return Bomber;
        }

        if (type.Contains("heli", StringComparison.Ordinal) ||
            type.StartsWith("ah-", StringComparison.Ordinal) ||
            type.StartsWith("uh-", StringComparison.Ordinal) ||
            type.StartsWith("oh-", StringComparison.Ordinal) ||
            type.StartsWith("ch-", StringComparison.Ordinal) ||
            type.StartsWith("mi-", StringComparison.Ordinal) ||
            type.StartsWith("ka-", StringComparison.Ordinal) ||
            type.StartsWith("lynx", StringComparison.Ordinal) ||
            type.StartsWith("gazelle", StringComparison.Ordinal))
        {
            return Helicopter;
        }

        if (type.Contains("attacker", StringComparison.Ordinal) ||
            type.Contains("-a-", StringComparison.Ordinal) ||
            type.StartsWith("a-", StringComparison.Ordinal) ||
            type.StartsWith("ad-", StringComparison.Ordinal) ||
            type.StartsWith("il-2", StringComparison.Ordinal) ||
            type.StartsWith("il-10", StringComparison.Ordinal) ||
            type.StartsWith("su-6", StringComparison.Ordinal) ||
            type.StartsWith("hs-129", StringComparison.Ordinal))
        {
            return Attacker;
        }

        return Fighter;
    }
}
