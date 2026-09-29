namespace WarThunderTelemetry.Core.Abstractions;

/// <summary>
/// 遥测数据来源抽象。
/// <para>
/// 第一期实现为 8111 HTTP 轮询（<c>HttpTelemetrySource</c>），
/// 后续可扩展为录像回放、日志文件重放、模拟数据等，UI 层无需改动。
/// </para>
/// </summary>
public interface ITelemetrySource : IAsyncDisposable
{
    /// <summary>来源的显示名，用于状态栏展示（如 "127.0.0.1:8111"）。</summary>
    string DisplayName { get; }

    /// <summary>当前是否已连接／可提供数据。</summary>
    bool IsConnected { get; }

    /// <summary>
    /// 连接状态变化。参数为新的连接状态。
    /// 注意：可能在任意线程触发，订阅方需自行切回 UI 线程。
    /// </summary>
    event EventHandler<bool>? ConnectionChanged;

    /// <summary>
    /// 收到某个端点的原始数据。参数 1 为端点键（见 <see cref="TelemetryEndpoint"/>），
    /// 参数 2 为该端点返回的、已解析为「扁平字段字典」的数据。
    /// </summary>
    event EventHandler<EndpointDataEventArgs>? DataReceived;

    /// <summary>开始采集。</summary>
    void Start();

    /// <summary>停止采集。</summary>
    void Stop();
}

/// <summary>端点标识常量，避免字符串散落各处。</summary>
public static class TelemetryEndpoint
{
    /// <summary>飞机原始状态（速度、高度、舵面、油量、发动机）。</summary>
    public const string State = "state";

    /// <summary>仪表盘数据（航向、过载、起落架、俯仰滚转、机型）。</summary>
    public const string Indicators = "indicators";

    /// <summary>地图元数据（地图名、模式、玩家列表）。</summary>
    public const string MapInfo = "map_info";

    /// <summary>任务目标与进度。</summary>
    public const string Mission = "mission";

    /// <summary>地图上全部单位的位置与运动信息。</summary>
    public const string MapObjects = "map_obj";

    /// <summary>击杀／伤害事件流（增量）。</summary>
    public const string HudMsg = "hudmsg";

    /// <summary>战斗聊天（增量）。</summary>
    public const string GameChat = "gamechat";
}

/// <summary>
/// 端点数据到达事件参数。
/// </summary>
/// <param name="Endpoint">端点键，取值见 <see cref="TelemetryEndpoint"/>。</param>
/// <param name="Fields">
/// 扁平化后的字段字典：键为规范化字段名，值为原始 JSON 标量。
/// 使用规范键是为了让上层不必关心 <c>"IAS, km/h"</c> 这类含空格逗号的原始键名。
/// </param>
/// <param name="Timestamp">数据到达时间。</param>
public readonly record struct EndpointDataEventArgs(
    string Endpoint,
    IReadOnlyDictionary<string, string> Fields,
    DateTimeOffset Timestamp);
