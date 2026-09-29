using System.Net;
using System.Text;
using WarThunderTelemetry.Core.Abstractions;

namespace WarThunderTelemetry.Core;

/// <summary>
/// 内置模拟 8111 服务器。
/// <para>
/// 输出字段名<b>严格对齐真实接口</b>（<c>H, m</c>、<c>IAS, km/h</c>、<c>Vy, m/s</c>、
/// <c>Ny</c>、<c>M</c>、<c>Mfuel, kg</c>、<c>throttle N, %</c>、<c>compass</c>、
/// <c>g_meter</c>、<c>gears</c>、<c>vario</c>、<c>type</c>），
/// 使无游戏时也能完整开发与调试全部界面。
/// </para>
/// <para>
/// 生成的数据带飞行动力学近似：高度与垂直速度联动、速度随时间积累、
/// 航向持续回转、燃油随油门消耗、过载随机波动，便于验证派生指标与告警逻辑。
/// </para>
/// </summary>
public sealed class MockTelemetryServer : ITelemetrySource
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _connected;
    private bool _disposed;

    // 模拟飞行状态
    private double _t;
    private double _altitude = 2400;
    private double _verticalSpeed;
    private double _speed = 480;
    private double _heading;
    private double _fuel = 750;
    private double _throttlePercent = 85;
    private double _gLoad = 1.0;
    private bool _overspeedDemo;
    private bool _lowFuelDemo;

    /// <summary>构造。</summary>
    /// <param name="port">监听端口，默认 8111。注意不要与真实游戏同时占用。</param>
    /// <param name="vehicleType">模拟的载具代号，默认 <c>la_9</c>（库中已收录）。</param>
    public MockTelemetryServer(int port = 8111, string vehicleType = "la_9")
    {
        _port = port;
        VehicleType = vehicleType;
    }

    /// <summary>模拟的载具代号。</summary>
    public string VehicleType { get; set; }

    /// <summary>
    /// 启动失败的原因；<c>null</c> 表示正常。<para>
    /// 最常见的情况是端口已被真实游戏占用 —— 这不算故障，此时应当改用真实数据源。
    /// </para>
    /// </summary>
    public Exception? StartupError { get; private set; }

    /// <summary>启动是否失败。</summary>
    public bool HasStartupError => StartupError is not null;

    /// <inheritdoc />
    public string DisplayName => $"mock://127.0.0.1:{_port}";

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
            if (_cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
        }

        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");

        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            // 端口被占用（最常见：真实游戏正在运行）不是致命错误 ——
            // 此时游戏自己的 8111 接口本来就能用，模拟源没必要也不该把程序拖崩。
            // 记下原因、标记为未连接，静默降级，由调用方决定是否提示用户。
            lock (_gate)
            {
                _cts?.Dispose();
                _cts = null;
            }

            StartupError = ex;
            return;
        }

        StartupError = null;
        _loop = Task.Run(() => ServeLoopAsync(_cts.Token));
        SetConnected(true);
    }

    /// <inheritdoc />
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();

        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch (Exception)
        {
            // 停止阶段的异常可忽略。
        }

        cts.Dispose();
        SetConnected(false);
    }

    private void SetConnected(bool value)
    {
        bool changed;
        lock (_gate)
        {
            changed = _connected != value;
            _connected = value;
        }

        if (changed)
        {
            ConnectionChanged?.Invoke(this, value);
        }
    }

    private async Task ServeLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 监听器被停止时跳出。
                break;
            }

            _ = Task.Run(() => HandleRequest(context, token), token);
        }
    }

    private void HandleRequest(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            Advance(0.15);

            var path = context.Request.Url?.AbsolutePath ?? "/";
            var json = path switch
            {
                "/state" => BuildStateJson(),
                "/indicators" => BuildIndicatorsJson(),
                "/map_info.json" => BuildMapInfoJson(),
                _ => null,
            };

            if (json is null)
            {
                context.Response.StatusCode = 404;
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);

                // 同时通过事件推出，让模拟源与真实源行为一致。
                var endpoint = path switch
                {
                    "/state" => TelemetryEndpoint.State,
                    "/indicators" => TelemetryEndpoint.Indicators,
                    _ => TelemetryEndpoint.MapInfo,
                };

                Publish(endpoint, json);
            }
        }
        catch (Exception)
        {
            // 单次请求异常忽略。
        }
        finally
        {
            try
            {
                context.Response.OutputStream.Close();
                context.Response.Close();
            }
            catch (Exception)
            {
                // 忽略关闭异常。
            }
        }
    }

    /// <summary>
    /// 推进模拟飞行状态。
    /// </summary>
    private void Advance(double dt)
    {
        _t += dt;

        // 每 40 秒一轮演示周期，分别触发超速与低油量告警，便于验证告警链路。
        var phase = _t % 120;
        _overspeedDemo = phase is > 30 and < 45;
        _lowFuelDemo = phase > 90;

        // 爬升/下降交替
        _verticalSpeed = Math.Sin(_t * 0.25) * 12;
        _altitude = Math.Clamp(_altitude + _verticalSpeed * dt, 200, 9000);

        // 速度：受油门与俯仰影响，超速演示阶段强制推高
        var targetSpeed = _overspeedDemo ? 920 : 480 + Math.Sin(_t * 0.15) * 60;
        _speed += (targetSpeed - _speed) * 0.08;

        // 航向持续回转，跨越 0/360 用于验证归一化逻辑
        _heading = (_heading + (25 * dt)) % 360;

        // 过载随坡度变化，超速阶段拉高以触发过载告警
        _gLoad = _overspeedDemo
            ? 1 + Math.Abs(Math.Sin(_t * 1.5)) * 8
            : 1 + Math.Abs(Math.Sin(_t * 0.6)) * 2.5;

        // 燃油随时间消耗，低油量演示阶段加速耗尽
        var burnRate = _lowFuelDemo ? 8.0 : 0.35;
        _fuel = Math.Max(20, _fuel - burnRate * dt);

        _throttlePercent = _overspeedDemo ? 100 : 85;
    }

    private string BuildStateJson()
    {
        var mach = _speed / 1225.0 * 0.82;
        var rpm = (int)(2100 + _throttlePercent * 6);
        var pitch = Math.Sin(_t * 0.3) * 8;

        return $$"""
        {
          "valid": true,
          "aileron, %": {{Math.Sin(_t * 0.4) * 15:F0}},
          "elevator, %": {{Math.Sin(_t * 0.25) * 10:F0}},
          "rudder, %": 0,
          "flaps, %": 0,
          "H, m": {{_altitude:F1}},
          "TAS, km/h": {{_speed * 1.06:F1}},
          "IAS, km/h": {{_speed:F1}},
          "M": {{mach:F3}},
          "AoA, deg": {{2 + Math.Sin(_t * 0.5) * 3:F2}},
          "AoS, deg": 0.2,
          "Ny": {{_gLoad:F2}},
          "Vy, m/s": {{_verticalSpeed:F2}},
          "Wx, deg/s": {{Math.Sin(_t * 0.7) * 20:F1}},
          "Mfuel, kg": {{_fuel:F1}},
          "Mfuel0, kg": 1200,
          "throttle 1, %": {{_throttlePercent:F0}},
          "RPM 1": {{rpm}},
          "oil temp 1, C": {{78 + Math.Sin(_t * 0.1) * 6:F1}},
          "water temp 1, C": {{95 + Math.Sin(_t * 0.12) * 8:F1}},
          "pitch 1, deg": {{pitch:F1}},
          "power 1, hp": {{1500 + Math.Sin(_t * 0.2) * 200:F0}}
        }
        """;
    }

    private string BuildIndicatorsJson()
    {
        return $$"""
        {
          "valid": true,
          "type": "{{VehicleType}}",
          "army": "air",
          "speed": "{{_speed:F1}}",
          "mach": {{_speed / 1225.0 * 0.82:F3}},
          "vario": "{{_verticalSpeed:F1}}",
          "compass": {{_heading:F2}},
          "aviahorizon_pitch": "{{Math.Sin(_t * 0.3) * 8:F1}}",
          "aviahorizon_roll": "{{Math.Sin(_t * 0.22) * 35:F1}}",
          "bank": {{Math.Sin(_t * 0.22) * 0.6:F3}},
          "turn": {{25 * Math.PI / 180:F4}},
          "g_meter": "{{_gLoad:F2}}",
          "gears": 0,
          "flaps": 0,
          "throttle": {{_throttlePercent / 100.0:F2}},
          "fuel": "{{_fuel:F0}}",
          "altitude_hour": {{_altitude:F1}},
          "altitude_min": {{_altitude:F1}},
          "rpm": {{2100 + _throttlePercent * 6:F0}}
        }
        """;
    }

    private static string BuildMapInfoJson()
    {
        return """
        {
          "valid": true,
          "Name": "模拟地图 mock",
          "environment": "Clear",
          "hour": 12,
          "playerType": "Aircraft",
          "players": 13,
          "current": { "mission": "模拟空战 [Dom]", "type": "Domination" },
          "map": { "max_x": 80000, "max_z": 80000 }
        }
        """;
    }

    /// <summary>
    /// 把生成的 JSON 直接解析为字段字典并推出（与真实源走同一条链路）。
    /// </summary>
    private void Publish(string endpoint, string json)
    {
        var fields = ParseFlat(json);
        if (fields.IsEmpty)
        {
            return;
        }

        DataReceived?.Invoke(this, new EndpointDataEventArgs(
            endpoint,
            fields.RawValues,
            DateTimeOffset.Now));
    }

    private static Parsing.EndpointFields ParseFlat(string json)
    {
        var fields = new Parsing.EndpointFields(64);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        Flatten(document.RootElement, null, fields, 0);
        return fields;
    }

    private static void Flatten(
        System.Text.Json.JsonElement element,
        string? prefix,
        Parsing.EndpointFields target,
        int depth)
    {
        if (depth > 4)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = prefix is null ? property.Name : $"{prefix}.{property.Name}";
                    Flatten(property.Value, name, target, depth + 1);
                }

                break;

            case System.Text.Json.JsonValueKind.Number:
            case System.Text.Json.JsonValueKind.String:
                if (prefix is not null)
                {
                    target.Set(prefix, element.ValueKind == System.Text.Json.JsonValueKind.String
                        ? element.GetString() ?? string.Empty
                        : element.GetRawText());
                }

                break;

            case System.Text.Json.JsonValueKind.True:
            case System.Text.Json.JsonValueKind.False:
                if (prefix is not null)
                {
                    target.Set(prefix, element.GetBoolean() ? "true" : "false");
                }

                break;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        Stop();

        try
        {
            _listener.Close();
        }
        catch (Exception)
        {
            // 忽略关闭异常。
        }

        return ValueTask.CompletedTask;
    }
}
