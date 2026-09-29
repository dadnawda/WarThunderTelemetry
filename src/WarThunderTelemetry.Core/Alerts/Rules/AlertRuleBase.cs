using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.Core.Alerts.Rules;

/// <summary>
/// 告警规则基类：收敛「按 Id 取字段值」这类重复逻辑。
/// </summary>
public abstract class AlertRuleBase : IAlertRule
{
    /// <summary>字段解析器。</summary>
    protected AliasResolver Resolver { get; }

    /// <summary>构造。</summary>
    /// <param name="resolver">字段解析器。</param>
    protected AlertRuleBase(AliasResolver resolver)
    {
        Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <inheritdoc />
    public abstract AlertSeverity DefaultSeverity { get; }

    /// <inheritdoc />
    public abstract AlertItem? Evaluate(TelemetrySnapshot snapshot, AlertThresholds thresholds);

    /// <summary>
    /// 按字段 Id 取值；字段缺失或非数值时返回 <c>null</c>。
    /// </summary>
    protected double? Number(TelemetrySnapshot snapshot, string fieldId)
    {
        var descriptor = Resolver.GetById(fieldId);
        return descriptor is null ? null : snapshot.Resolve(descriptor, Resolver).Number;
    }

    /// <summary>
    /// 按字段 Id 取文本值。
    /// </summary>
    protected string? Text(TelemetrySnapshot snapshot, string fieldId)
    {
        var descriptor = Resolver.GetById(fieldId);
        return descriptor is null ? null : snapshot.Resolve(descriptor, Resolver).Text;
    }
}
