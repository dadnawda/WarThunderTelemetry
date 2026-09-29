using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.Core.Models;

/// <summary>
/// 某一时刻的完整遥测快照。
/// <para>
/// 采用不可变记录：每次数据更新生成新实例，通过事件推出，
/// 采集线程与 UI 线程之间无需加锁。
/// </para>
/// </summary>
public sealed record TelemetrySnapshot
{
    /// <summary>空快照（尚未收到任何数据时使用）。</summary>
    public static TelemetrySnapshot Empty { get; } = new();

    /// <summary>快照生成时间。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary><c>/state</c> 端点字段。</summary>
    public EndpointFields State { get; init; } = EndpointFields.Empty;

    /// <summary><c>/indicators</c> 端点字段。</summary>
    public EndpointFields Indicators { get; init; } = EndpointFields.Empty;

    /// <summary>派生指标字段（由 <c>IMetricProvider</c> 计算）。</summary>
    public EndpointFields Derived { get; init; } = EndpointFields.Empty;

    /// <summary>是否已连接（有数据流入）。</summary>
    public bool IsConnected { get; init; }

    /// <summary>当前载具代号（来自 <c>indicators.type</c>），未知时为 <c>null</c>。</summary>
    public string? VehicleType =>
        Indicators.GetText("type") ?? Indicators.GetText("vehicle_type");

    /// <summary>当前兵种（<c>air</c> / <c>tank</c>），未知时为 <c>null</c>。</summary>
    public string? Army => Indicators.GetText("army");

    /// <summary>
    /// 按 state → indicators → derived 顺序解析一个字段。
    /// </summary>
    public FieldValue Resolve(FieldDescriptor descriptor, AliasResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(resolver);

        // 派生字段只从 Derived 取，避免与游戏原始字段串味。
        if (descriptor.Kind == FieldKind.Derived)
        {
            return resolver.Resolve(descriptor, Derived);
        }

        // 指定了优先端点的字段（如 state 独有的 Nfuel）先查该端点。
        if (!string.IsNullOrEmpty(descriptor.PreferredEndpoint))
        {
            var preferred = descriptor.PreferredEndpoint switch
            {
                "state" => State,
                "indicators" => Indicators,
                "derived" => Derived,
                _ => EndpointFields.Empty,
            };

            var hit = resolver.Resolve(descriptor, preferred);
            if (hit.Found)
            {
                return hit;
            }
        }

        return resolver.Resolve(descriptor, State, Indicators);
    }
}
