namespace WarThunderTelemetry.Core.Abstractions;

/// <summary>
/// 派生指标提供者。
/// <para>
/// 游戏只给原始数据（速度、高度、航向），能量、加速度、回转率这类战术指标
/// 必须本地计算。每个提供者以滑动窗口为输入，输出若干具名指标。
/// </para>
/// <para>
/// 后续扩展示例：能量机动评分、最佳缠斗速度、推力估算、燃油续航预估。
/// </para>
/// </summary>
public interface IMetricProvider
{
    /// <summary>提供者的唯一标识（用于日志与去重）。</summary>
    string Id { get; }

    /// <summary>
    /// 该提供者产出的指标键的显示元数据。键为指标 Id，值为显示信息。
    /// </summary>
    IReadOnlyDictionary<string, MetricDefinition> Definitions { get; }

    /// <summary>
    /// 传入最新快照与历史窗口，返回本次计算出的指标值。
    /// 返回字典的键须出现在 <see cref="Definitions"/> 中。
    /// 无法计算时返回 <c>null</c> 值或省略该键，由上层降级显示为 <c>–</c>。
    /// </summary>
    /// <param name="context">计算上下文，含当前快照与 3 秒滑动历史。</param>
    IReadOnlyDictionary<string, double?> Compute(MetricContext context);

    /// <summary>
    /// 重置内部状态。断线重连或更换载具时调用，避免跨载具算出错误值。
    /// </summary>
    void Reset();
}

/// <summary>指标的显示元数据。</summary>
/// <param name="Id">指标键。</param>
/// <param name="DisplayName">中文显示名。</param>
/// <param name="Unit">单位符号，无量纲时为空串。</param>
/// <param name="Decimals">建议小数位。</param>
public readonly record struct MetricDefinition(
    string Id,
    string DisplayName,
    string Unit,
    int Decimals = 1);
