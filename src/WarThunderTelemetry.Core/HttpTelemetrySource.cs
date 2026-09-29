using System.Net.Http;
using System.Text.Json;
using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.Core;

/// <summary>
/// 8111 本地接口的轮询设置。
/// </summary>
public sealed record TelemetryClientOptions
{
    /// <summary>主机，默认本机。</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>端口，战雷默认为 8111。</summary>
    public int Port { get; init; } = 8111;

    /// <summary>断线重探间隔（毫秒）。</summary>
    public int ReconnectIntervalMs { get; init; } = 2000;

    /// <summary>单次请求超时（毫秒）。</summary>
    public int RequestTimeoutMs { get; init; } = 1500;

    /// <summary>各端点的轮询间隔（毫秒）。键见 <see cref="TelemetryEndpoint"/>。</summary>
    public IReadOnlyDictionary<string, int> Intervals { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [TelemetryEndpoint.State] = 150,
            [TelemetryEndpoint.Indicators] = 250,
            [TelemetryEndpoint.MapInfo] = 1000,
        };

    /// <summary>基础地址。</summary>
    public string BaseAddress => $"http://{Host}:{Port}";
}

/// <summary>
/// 8111 轮询引擎。
/// <para>
/// 设计要点（沿用已验证的工程结论）：
/// <list type="bullet">
/// <item>各端点<b>独立定时器、独立容错</b> —— 任一失败不影响其他端点。</item>
/// <item>用 <c>/map_info.json</c> 作为连接探针，断线后按间隔重探，恢复即续传。</item>
/// <item>解析全部防御性处理，游戏更新导致字段增减也不会抛异常。</item>
/// </list>
/// </para>
/// </summary>
public sealed class HttpTelemetrySource : ITelemetrySource
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// 连续探针失败多少次才判定断线。<para>
    /// 单次失败往往只是游戏瞬时忙/切场景，若立刻断开会把正在跑的数据轮询一起停掉，
    /// 表现为「数据一顿一顿的」。留 3 次容错（约 6 秒）即可平滑跳过抖动。
    /// </para>
    /// </summary>
    private const int ProbeFailureThreshold = 3;

    private readonly TelemetryClientOptions _options;
    private readonly HttpClient _http;
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private int _probeFailures;
    private bool _connected;
    private bool _started;
    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="options">轮询设置。</param>
    /// <param name="httpClient">可注入的 HttpClient（测试用）。不传则内部创建。</param>
    public HttpTelemetrySource(TelemetryClientOptions? options = null, HttpClient? httpClient = null)
    {
        _options = options ?? new TelemetryClientOptions();
        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(_options.RequestTimeoutMs * 3),
        };
    }

    /// <inheritdoc />
    public string DisplayName => _options.BaseAddress;

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _connected;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<bool>? ConnectionChanged;

    /// <inheritdoc />
    public event EventHandler<EndpointDataEventArgs>? DataReceived;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        // 探针先跑一次，连上后自动启动各端点轮询。
        _ = ProbeAsync();
        RegisterTimer("__probe", _options.ReconnectIntervalMs, _ => _ = ProbeAsync());
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            _started = false;

            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }

        SetConnected(false);
    }

    private async Task ProbeAsync()
    {
        if (!IsRunning())
        {
            return;
        }

        try
        {
            var fields = await FetchFieldsAsync($"{_options.BaseAddress}/map_info.json").ConfigureAwait(false);

            // 空响应（游戏切场景/加载中）不代表断线 —— 保留连接，
            // 只是这一轮没有新数据可推。
            if (fields is null)
            {
                CountProbeFailure();
                return;
            }

            if (!fields.IsEmpty)
            {
                Publish(TelemetryEndpoint.MapInfo, fields);
            }

            ResetProbeFailure();
            SetConnected(true);
        }
        catch (Exception)
        {
            // 单次请求失败（超时/游戏瞬时无响应）属预期情况，
            // 连续失败达到阈值才判定断线，避免数据流被反复打断。
            CountProbeFailure();
        }
    }

    /// <summary>连续探针失败次数达到阈值才真正断线。</summary>
    private void CountProbeFailure()
    {
        int failures;
        lock (_gate)
        {
            failures = ++_probeFailures;
        }

        if (failures >= ProbeFailureThreshold)
        {
            SetConnected(false);
        }
    }

    private void ResetProbeFailure()
    {
        lock (_gate)
        {
            _probeFailures = 0;
        }
    }

    private bool IsRunning()
    {
        lock (_gate)
        {
            return _started && !_disposed;
        }
    }

    private void SetConnected(bool value)
    {
        bool changed;
        lock (_gate)
        {
            changed = _connected != value;
            _connected = value;
        }

        if (!changed)
        {
            return;
        }

        if (value)
        {
            StartEndpointPollers();
        }
        else
        {
            StopEndpointPollers();
        }

        ConnectionChanged?.Invoke(this, value);
    }

    private void StartEndpointPollers()
    {
        foreach (var (endpoint, interval) in _options.Intervals)
        {
            var path = EndpointPath(endpoint);
            if (path is null)
            {
                continue;
            }

            RegisterTimer(endpoint, interval, _ => _ = PollOnceAsync(endpoint, path), onlyIfAbsent: true);
            _ = PollOnceAsync(endpoint, path);
        }
    }

    private void StopEndpointPollers()
    {
        lock (_gate)
        {
            foreach (var endpoint in _options.Intervals.Keys)
            {
                if (_timers.Remove(endpoint, out var timer))
                {
                    timer.Dispose();
                }
            }
        }
    }

    private static string? EndpointPath(string endpoint) => endpoint switch
    {
        TelemetryEndpoint.State => "/state",
        TelemetryEndpoint.Indicators => "/indicators",
        TelemetryEndpoint.MapInfo => "/map_info.json",
        TelemetryEndpoint.Mission => "/mission.json",
        TelemetryEndpoint.MapObjects => "/map_obj.json",
        TelemetryEndpoint.GameChat => "/gamechat",
        TelemetryEndpoint.HudMsg => "/hudmsg",
        _ => null,
    };

    private void RegisterTimer(string key, int intervalMs, TimerCallback callback, bool onlyIfAbsent = false)
    {
        var interval = Math.Max(50, intervalMs);

        lock (_gate)
        {
            if (_disposed || !_started)
            {
                return;
            }

            if (onlyIfAbsent && _timers.ContainsKey(key))
            {
                return;
            }

            if (_timers.Remove(key, out var existing))
            {
                existing.Dispose();
            }

            _timers[key] = new Timer(callback, null, interval, interval);
        }
    }

    private async Task PollOnceAsync(string endpoint, string path)
    {
        if (!IsConnected || !IsRunning())
        {
            return;
        }

        try
        {
            var fields = await FetchFieldsAsync($"{_options.BaseAddress}{path}").ConfigureAwait(false);
            if (fields is not null)
            {
                Publish(endpoint, fields);
            }
        }
        catch (Exception)
        {
            // 单次失败忽略，下一轮重试。
        }
    }

    /// <summary>
    /// 拉取端点并扁平化为字段字典。
    /// <para>
    /// 不同端点的 JSON 结构不同（对象 / 数组 / 嵌套），这里统一处理：
    /// 对象取全部标量属性；嵌套对象递归展开为 <c>父.子</c> 键；
    /// 数组与非标量忽略（第一期的字段都在顶层标量里）。
    /// </para>
    /// </summary>
    private async Task<EndpointFields?> FetchFieldsAsync(string url)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseContentRead)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            // 空响应在游戏切场景时是常态，不算错误。
            return new EndpointFields(0);
        }

        using var document = JsonDocument.Parse(text, JsonOptions);
        var fields = new EndpointFields(64);
        Flatten(document.RootElement, prefix: null, fields, depth: 0);
        return fields;
    }

    private static void Flatten(JsonElement element, string? prefix, EndpointFields target, int depth)
    {
        // 防御过深嵌套，避免异常结构导致栈问题。
        if (depth > 4)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = prefix is null ? property.Name : $"{prefix}.{property.Name}";
                    Flatten(property.Value, name, target, depth + 1);
                }

                break;

            case JsonValueKind.Number:
            case JsonValueKind.String:
                if (prefix is not null)
                {
                    target.Set(prefix, element.ValueKind == JsonValueKind.String
                        ? element.GetString() ?? string.Empty
                        : element.GetRawText());
                }

                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                if (prefix is not null)
                {
                    target.Set(prefix, element.GetBoolean() ? "true" : "false");
                }

                break;

            default:
                // 数组与 null 忽略。
                break;
        }
    }

    private void Publish(string endpoint, EndpointFields fields)
    {
        if (fields.IsEmpty)
        {
            return;
        }

        DataReceived?.Invoke(this, new EndpointDataEventArgs(
            endpoint,
            fields.RawValues,
            DateTimeOffset.Now));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _started = false;

            foreach (var timer in _timers.Values)
            {
                timer.Dispose();
            }

            _timers.Clear();
        }

        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
