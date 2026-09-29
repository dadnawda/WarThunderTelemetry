using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.Core.Alerts;

/// <summary>
/// 告警引擎：用当前快照逐条评估全部已注册规则。
/// <para>
/// 阈值来自载具档案（见 <see cref="AlertThresholdFactory"/>），
/// 引擎本身不关心阈值来源，只负责调度与结果汇总。
/// </para>
/// </summary>
public sealed class AlertEngine
{
    private readonly List<IAlertRule> _rules = new();
    private readonly AliasResolver _resolver;

    /// <summary>构造。</summary>
    /// <param name="resolver">字段解析器。</param>
    public AlertEngine(AliasResolver? resolver = null)
    {
        _resolver = resolver ?? FieldCatalog.CreateResolver();
    }

    /// <summary>已注册的规则。</summary>
    public IReadOnlyList<IAlertRule> Rules => _rules;

    /// <summary>
    /// 注册一条规则。
    /// </summary>
    public AlertEngine Register(IAlertRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _rules.Add(rule);
        return this;
    }

    /// <summary>
    /// 注册内置的 6 条规则。
    /// </summary>
    public AlertEngine RegisterBuiltInRules()
    {
        Register(new Rules.StallRule(_resolver));
        Register(new Rules.OverspeedRule(_resolver));
        Register(new Rules.MachLimitRule(_resolver));
        Register(new Rules.OverloadRule(_resolver));
        Register(new Rules.LowFuelRule(_resolver));
        Register(new Rules.GearOverspeedRule(_resolver));
        return this;
    }

    /// <summary>
    /// 评估全部规则。
    /// </summary>
    /// <returns>
    /// 触发的告警，按严重级别从高到低排序。无触发时返回空列表。
    /// </returns>
    public IReadOnlyList<AlertItem> Evaluate(TelemetrySnapshot snapshot, AlertThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(thresholds);

        if (!snapshot.IsConnected)
        {
            return [];
        }

        List<AlertItem>? hits = null;

        foreach (var rule in _rules)
        {
            AlertItem? item;
            try
            {
                item = rule.Evaluate(snapshot, thresholds);
            }
            catch (Exception)
            {
                // 单条规则异常不得影响整体告警评估。
                continue;
            }

            if (item is { } hit)
            {
                (hits ??= new List<AlertItem>(4)).Add(hit);
            }
        }

        if (hits is null)
        {
            return [];
        }

        hits.Sort(static (a, b) => b.Severity.CompareTo(a.Severity));
        return hits;
    }

    /// <summary>取当前最高的告警级别（无告警时为 <see cref="AlertSeverity.Info"/>）。</summary>
    public AlertSeverity MaxSeverity(IReadOnlyList<AlertItem> alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);

        var max = AlertSeverity.Info;
        foreach (var alert in alerts)
        {
            if (alert.Severity > max)
            {
                max = alert.Severity;
            }
        }

        return max;
    }
}
