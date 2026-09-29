using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.Core.Alerts.Rules;

/// <summary>
/// 失速预警：指示空速低于阈值。
/// <para>
/// 襟翼放下时会降低失速速度，因此按襟翼状态放宽阈值。
/// 关键字段缺失时返回 <c>null</c>（不误报）。
/// </para>
/// </summary>
public sealed class StallRule : AlertRuleBase
{
    /// <summary>构造。</summary>
    public StallRule(AliasResolver resolver) : base(resolver)
    {
    }

    /// <inheritdoc />
    public override string Id => "stall";

    /// <inheritdoc />
    public override string DisplayName => "失速预警";

    /// <inheritdoc />
    public override AlertSeverity DefaultSeverity => AlertSeverity.Warning;

    /// <inheritdoc />
    public override AlertItem? Evaluate(TelemetrySnapshot snapshot, AlertThresholds thresholds)
    {
        if (thresholds.StallSpeedIas is not { } stallSpeed)
        {
            return null;
        }

        var ias = Number(snapshot, "ias");
        if (ias is not { } speed)
        {
            return null;
        }

        // 襟翼放下时失速速度下移约 15%，门槛相应放宽。
        var flaps = Number(snapshot, "flaps");
        var effectiveStall = flaps is > 0.05 ? stallSpeed * 0.85 : stallSpeed;

        if (speed >= effectiveStall)
        {
            return null;
        }

        return new AlertItem(
            Id,
            "失速",
            $"表速 {speed:F0} km/h，低于失速速度 {effectiveStall:F0} km/h",
            AlertSeverity.Critical,
            speed,
            effectiveStall);
    }
}

/// <summary>
/// 超速预警：指示空速超过该机型的结构速度上限。
/// </summary>
public sealed class OverspeedRule : AlertRuleBase
{
    /// <summary>构造。</summary>
    public OverspeedRule(AliasResolver resolver) : base(resolver)
    {
    }

    /// <inheritdoc />
    public override string Id => "overspeed";

    /// <inheritdoc />
    public override string DisplayName => "超速预警";

    /// <inheritdoc />
    public override AlertSeverity DefaultSeverity => AlertSeverity.Warning;

    /// <inheritdoc />
    public override AlertItem? Evaluate(TelemetrySnapshot snapshot, AlertThresholds thresholds)
    {
        if (thresholds.MaxSpeedIas is not { } maxSpeed)
        {
            return null;
        }

        var ias = Number(snapshot, "ias");
        if (ias is not { } speed || speed <= maxSpeed)
        {
            return null;
        }

        var overshoot = speed - maxSpeed;
        var severity = overshoot > maxSpeed * 0.08
            ? AlertSeverity.Critical
            : AlertSeverity.Warning;

        return new AlertItem(
            Id,
            "超速",
            $"表速 {speed:F0} km/h，超出结构上限 {maxSpeed:F0} km/h",
            severity,
            speed,
            maxSpeed);
    }
}

/// <summary>
/// 马赫数超限：仅喷气机等有马赫读数的载具会触发。
/// </summary>
public sealed class MachLimitRule : AlertRuleBase
{
    /// <summary>构造。</summary>
    public MachLimitRule(AliasResolver resolver) : base(resolver)
    {
    }

    /// <inheritdoc />
    public override string Id => "mach";

    /// <inheritdoc />
    public override string DisplayName => "马赫超限";

    /// <inheritdoc />
    public override AlertSeverity DefaultSeverity => AlertSeverity.Warning;

    /// <inheritdoc />
    public override AlertItem? Evaluate(TelemetrySnapshot snapshot, AlertThresholds thresholds)
    {
        if (thresholds.MachLimit is not { } machLimit)
        {
            return null;
        }

        var mach = Number(snapshot, "mach");
        if (mach is not { } value || value <= machLimit)
        {
            return null;
        }

        return new AlertItem(
            Id,
            "马赫超限",
            $"马赫 {value:F2}，超出限制 {machLimit:F2}",
            AlertSeverity.Warning,
            value,
            machLimit);
    }
}

/// <summary>
/// 过载超限：正向或负向超过结构限制。
/// <para>
/// 注意：载具库中 <c>G limit</c> 原始格式为「≈ -8/13 G」，
/// 解析后正向 13、负向绝对值 8，两侧分别比较。
/// </para>
/// </summary>
public sealed class OverloadRule : AlertRuleBase
{
    /// <summary>构造。</summary>
    public OverloadRule(AliasResolver resolver) : base(resolver)
    {
    }

    /// <inheritdoc />
    public override string Id => "overload";

    /// <inheritdoc />
    public override string DisplayName => "过载超限";

    /// <inheritdoc />
    public override AlertSeverity DefaultSeverity => AlertSeverity.Warning;

    /// <inheritdoc />
    public override AlertItem? Evaluate(TelemetrySnapshot snapshot, AlertThresholds thresholds)
    {
        var g = Number(snapshot, "g_load");
        if (g is not { } load)
        {
            return null;
        }

        if (thresholds.GLimitPositive is { } positive && load > positive)
        {
            return new AlertItem(
                Id,
                "过载超限",
                $"过载 +{load:F1} g，超出结构上限 +{positive:F0} g",
                AlertSeverity.Critical,
                load,
                positive);
        }

        if (thresholds.GLimitNegative is { } negative && load < -negative)
        {
            return new AlertItem(
                Id,
                "负过载超限",
                $"过载 {load:F1} g，超出结构下限 -{negative:F0} g",
                AlertSeverity.Critical,
                load,
                -negative);
        }

        return null;
    }
}

/// <summary>
/// 低油量预警：剩余燃油百分比低于阈值。
/// <para>
/// 满油量优先取接口的 <c>Mfuel0, kg</c>；缺失时不做百分比换算，避免误报。
/// </para>
/// </summary>
public sealed class LowFuelRule : AlertRuleBase
{
    /// <summary>构造。</summary>
    public LowFuelRule(AliasResolver resolver) : base(resolver)
    {
    }

    /// <inheritdoc />
    public override string Id => "low_fuel";

    /// <inheritdoc />
    public override string DisplayName => "低油量";

    /// <inheritdoc />
    public override AlertSeverity DefaultSeverity => AlertSeverity.Caution;

    /// <inheritdoc />
    public override AlertItem? Evaluate(TelemetrySnapshot snapshot, AlertThresholds thresholds)
    {
        var remaining = Number(snapshot, "fuel");
        if (remaining is not { } fuel)
        {
            return null;
        }

        var capacity = Number(snapshot, "fuel_capacity") ?? thresholds.FuelCapacityKg;

        // 拿不到满油量就无法算百分比 —— 不猜，直接跳过。
        if (capacity is not { } full || full <= 0)
        {
            return null;
        }

        var percent = fuel / full * 100.0;
        if (percent > thresholds.FuelLowPercent)
        {
            return null;
        }

        var severity = percent <= thresholds.FuelLowPercent * 0.5
            ? AlertSeverity.Warning
            : AlertSeverity.Caution;

        return new AlertItem(
            Id,
            "低油量",
            $"剩余燃油 {fuel:F0} kg（{percent:F0}%），低于 {thresholds.FuelLowPercent:F0}%",
            severity,
            percent,
            thresholds.FuelLowPercent);
    }
}

/// <summary>
/// 起落架超速：起落架处于放下状态且空速超过其速度上限。
/// </summary>
public sealed class GearOverspeedRule : AlertRuleBase
{
    /// <summary>构造。</summary>
    public GearOverspeedRule(AliasResolver resolver) : base(resolver)
    {
    }

    /// <inheritdoc />
    public override string Id => "gear_overspeed";

    /// <inheritdoc />
    public override string DisplayName => "起落架超速";

    /// <inheritdoc />
    public override AlertSeverity DefaultSeverity => AlertSeverity.Warning;

    /// <inheritdoc />
    public override AlertItem? Evaluate(TelemetrySnapshot snapshot, AlertThresholds thresholds)
    {
        if (thresholds.GearSpeedLimit is not { } gearLimit)
        {
            return null;
        }

        // gear 字段可能是 0~1 小数，也可能是 DOWN/UP 字符串，解析器已统一为 0~1。
        var gear = Number(snapshot, "gear");
        if (gear is not > 0.05)
        {
            return null;
        }

        var ias = Number(snapshot, "ias");
        if (ias is not { } speed || speed <= gearLimit)
        {
            return null;
        }

        return new AlertItem(
            Id,
            "起落架超速",
            $"起落架放下，表速 {speed:F0} km/h 超出 {gearLimit:F0} km/h 限制",
            AlertSeverity.Warning,
            speed,
            gearLimit);
    }
}
