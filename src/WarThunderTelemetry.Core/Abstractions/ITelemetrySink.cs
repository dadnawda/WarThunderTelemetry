using WarThunderTelemetry.Core.Models;

namespace WarThunderTelemetry.Core.Abstractions;

/// <summary>
/// 遥测数据落盘／导出。
/// <para>
/// 预留扩展点，第一期不实现。后续可加：CSV 记录、飞行日志、
/// 用于 AI 复盘的二进制轨迹、对接外部系统的实时转发。
/// </para>
/// </summary>
public interface ITelemetrySink : IAsyncDisposable
{
    /// <summary>落盘目标的标识（用于日志）。</summary>
    string Id { get; }

    /// <summary>是否处于写入状态。</summary>
    bool IsActive { get; }

    /// <summary>开始记录。</summary>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>写入一帧快照。</summary>
    ValueTask WriteAsync(TelemetrySnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>停止记录并释放资源。</summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 可插拔的显示面板。
/// <para>
/// 让后续新面板（数据曲线图、雷达图、能量机动分析）无需改动主窗口即可注册进来。
/// </para>
/// </summary>
public interface IDashboardPanel
{
    /// <summary>面板唯一标识。</summary>
    string Id { get; }

    /// <summary>面板在导航中显示的名称。</summary>
    string DisplayName { get; }

    /// <summary>排序权重，越小越靠前。</summary>
    int Order { get; }
}
