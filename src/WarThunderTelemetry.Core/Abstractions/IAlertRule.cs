using WarThunderTelemetry.Core.Models;

namespace WarThunderTelemetry.Core.Abstractions;

/// <summary>
/// 告警规则。
/// <para>
/// 每条规则拿「当前快照 + 当前生效阈值」自行判断是否触发。
/// 阈值来源链：载具专属参数 → 兵种缺省值，规则本身不关心阈值从哪来。
/// </para>
/// </summary>
public interface IAlertRule
{
    /// <summary>规则唯一标识。</summary>
    string Id { get; }

    /// <summary>规则显示名（如「失速预警」）。</summary>
    string DisplayName { get; }

    /// <summary>触发时的默认严重级别，规则内部可覆盖。</summary>
    AlertSeverity DefaultSeverity { get; }

    /// <summary>
    /// 评估当前快照。
    /// </summary>
    /// <param name="snapshot">当前遥测快照。</param>
    /// <param name="thresholds">当前生效的阈值集合（已按载具归一）。</param>
    /// <returns>
    /// 触发时返回告警项，未触发返回 <c>null</c>。
    /// 关键字段缺失时应返回 <c>null</c>（不误报），而不是猜测。
    /// </returns>
    AlertItem? Evaluate(TelemetrySnapshot snapshot, AlertThresholds thresholds);
}

/// <summary>告警严重级别。</summary>
public enum AlertSeverity
{
    /// <summary>正常／提示。</summary>
    Info = 0,

    /// <summary>注意（接近临界）。</summary>
    Caution = 1,

    /// <summary>警告（已超限）。</summary>
    Warning = 2,

    /// <summary>危险（需立即处置）。</summary>
    Critical = 3,
}

/// <summary>一条告警。</summary>
/// <param name="RuleId">触发规则的 Id。</param>
/// <param name="Title">短标题（如「超速」）。</param>
/// <param name="Detail">细节描述（如「IAS 912 km/h，超出限制 850 km/h」）。</param>
/// <param name="Severity">严重级别。</param>
/// <param name="Value">触发时的实测值，无则为 <c>null</c>。</param>
/// <param name="Threshold">触发时的阈值，无则为 <c>null</c>。</param>
public readonly record struct AlertItem(
    string RuleId,
    string Title,
    string Detail,
    AlertSeverity Severity,
    double? Value = null,
    double? Threshold = null);
