using WarThunderTelemetry.Core.Models;

namespace WarThunderTelemetry.Core.Abstractions;

/// <summary>
/// 派生指标的计算上下文。
/// <para>
/// 微分类指标（加速度、回转率）必须依赖时间序列而非单帧快照，
/// 因此这里同时提供当前快照与一段滑动历史。
/// </para>
/// </summary>
public sealed class MetricContext
{
    /// <summary>当前快照。</summary>
    public required TelemetrySnapshot Snapshot { get; init; }

    /// <summary>
    /// 滑动历史样本，按时间升序，仅保留最近约 3 秒。
    /// 每项为「时间 + 若干关键量」的轻量记录，不含完整字段集。
    /// </summary>
    public required IReadOnlyList<MetricSample> History { get; init; }

    /// <summary>当前时间。</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 历史样本：派生指标计算所需的最小数据集。
/// </summary>
/// <param name="Timestamp">采样时间。</param>
/// <param name="SpeedMs">真空速（m/s）。缺失为 <c>null</c>。</param>
/// <param name="SpeedIsEstimated">
/// 速度是否为估算值。当只有 IAS 而无 TAS 时置真，
/// 表示速度能量相关指标（如能量高度）误差较大，应降级显示。
/// </param>
/// <param name="AltitudeM">海拔高度（m）。缺失为 <c>null</c>。</param>
/// <param name="VerticalSpeedMs">垂直速度（m/s）。缺失为 <c>null</c>。</param>
/// <param name="HeadingDeg">航向（度，0~360）。缺失为 <c>null</c>。</param>
/// <param name="GLoad">过载（g）。缺失为 <c>null</c>。</param>
public readonly record struct MetricSample(
    DateTimeOffset Timestamp,
    double? SpeedMs,
    bool SpeedIsEstimated,
    double? AltitudeM,
    double? VerticalSpeedMs,
    double? HeadingDeg,
    double? GLoad);
