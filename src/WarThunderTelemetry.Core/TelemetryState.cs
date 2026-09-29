using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Alerts;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;
using WarThunderTelemetry.Core.Vehicles;

namespace WarThunderTelemetry.Core;

/// <summary>
/// 遥测状态容器：把数据源、派生指标、载具库、告警引擎串成一条链路。
/// <para>
/// UI 只需订阅 <see cref="Updated"/>，就能拿到「快照 + 派生指标 + 告警 + 当前阈值」的完整状态。
/// </para>
/// <para>
/// 线程模型：数据源可能在任何线程回调，本类用锁保护内部状态，
/// 并通过 <see cref="Updated"/> 把新状态推出；订阅方负责切回 UI 线程。
/// </para>
/// </summary>
public sealed class TelemetryState : IAsyncDisposable
{
    private readonly ITelemetrySource _source;
    private readonly AliasResolver _resolver;
    private readonly DerivedMetricsProvider _derivedMetrics;
    private readonly AlertEngine _alertEngine;
    private readonly OnlineVehicleResolver _vehicles;
    private readonly Lock _gate = new();

    private EndpointFields _stateFields = EndpointFields.Empty;
    private EndpointFields _indicatorsFields = EndpointFields.Empty;
    private EndpointFields _derivedFields = EndpointFields.Empty;

    private string? _currentVehicleType;
    private AlertThresholds _thresholds = new();

    /// <summary>构造。</summary>
    /// <param name="source">数据源。</param>
    /// <param name="vehicles">载具参数来源。不传则用内置库 + 联网抓取。</param>
    public TelemetryState(ITelemetrySource source, OnlineVehicleResolver? vehicles = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _resolver = FieldCatalog.CreateResolver();
        _derivedMetrics = new DerivedMetricsProvider(_resolver);
        _alertEngine = new AlertEngine(_resolver).RegisterBuiltInRules();
        _vehicles = vehicles ?? new OnlineVehicleResolver();

        _source.ConnectionChanged += OnConnectionChanged;
        _source.DataReceived += OnDataReceived;
    }

    /// <summary>最近一次完整状态。</summary>
    public TelemetryStatus Current { get; private set; } = TelemetryStatus.Empty;

    /// <summary>状态更新事件。可能在非 UI 线程触发。</summary>
    public event EventHandler<TelemetryStatus>? Updated;

    /// <summary>字段解析器（供 UI 显示字段名等使用）。</summary>
    public AliasResolver Resolver => _resolver;

    /// <summary>载具参数来源（内置库 + 联网抓取 + 缓存）。</summary>
    public OnlineVehicleResolver Vehicles => _vehicles;

    /// <summary>开始采集。</summary>
    public void Start() => _source.Start();

    /// <summary>停止采集。</summary>
    public void Stop() => _source.Stop();

    private void OnConnectionChanged(object? sender, bool connected)
    {
        if (!connected)
        {
            // 断线时清空派生指标历史，避免重连后算出跨载具的荒谬值。
            _derivedMetrics.Reset();
        }

        Rebuild(connected);
    }

    private void OnDataReceived(object? sender, EndpointDataEventArgs e)
    {
        lock (_gate)
        {
            switch (e.Endpoint)
            {
                case TelemetryEndpoint.State:
                    _stateFields = ToFields(e.Fields, TelemetryEndpoint.State, isDerived: false);
                    break;

                case TelemetryEndpoint.Indicators:
                    _indicatorsFields = ToFields(e.Fields, TelemetryEndpoint.Indicators, isDerived: false);
                    break;

                default:
                    // 第一期只消费 state 与 indicators；其余端点为二期预留。
                    return;
            }
        }

        Rebuild(isConnected: true);
    }

    private static EndpointFields ToFields(
        IReadOnlyDictionary<string, string> values,
        string endpoint,
        bool isDerived)
    {
        var fields = new EndpointFields(values.Count)
        {
            Endpoint = endpoint,
            IsDerived = isDerived,
        };

        foreach (var (key, value) in values)
        {
            fields.Set(key, value);
        }

        return fields;
    }

    /// <summary>
    /// 用当前字段重组完整状态：算派生指标 → 解析载具 → 生成阈值 → 评估告警。
    /// </summary>
    private void Rebuild(bool isConnected)
    {
        TelemetryStatus status;

        lock (_gate)
        {
            var baseSnapshot = new TelemetrySnapshot
            {
                Timestamp = DateTimeOffset.Now,
                State = _stateFields,
                Indicators = _indicatorsFields,
                Derived = _derivedFields,
                IsConnected = isConnected,
            };

            // 载具切换时重置派生指标 —— 不同机型的能量与回转特性不可混算。
            var vehicleType = baseSnapshot.VehicleType;
            var army = baseSnapshot.Army;
            var vehicleChanged = !string.Equals(vehicleType, _currentVehicleType, StringComparison.Ordinal);

            if (vehicleChanged)
            {
                _currentVehicleType = vehicleType;
                _derivedMetrics.Reset();
            }

            // 派生指标：以滑动窗口为输入计算，产出写回快照供解析器统一取值。
            var context = new MetricContext
            {
                Snapshot = baseSnapshot,
                History = [],
                Now = baseSnapshot.Timestamp,
            };

            var metrics = _derivedMetrics.Compute(context);
            _derivedFields = BuildDerivedFields(metrics);

            var snapshot = baseSnapshot with { Derived = _derivedFields };

            // 载具库命中则用专属阈值，否则按兵种兜底。
            // 未收录的载具会在这里触发一次后台联网抓取，抓到后下次刷新生效。
            var profile = _vehicles.Resolve(vehicleType, army, out var isExact);
            _thresholds = AlertThresholdFactory.FromProfile(profile, isExact);

            var alerts = _alertEngine.Evaluate(snapshot, _thresholds);

            status = new TelemetryStatus
            {
                Snapshot = snapshot,
                Thresholds = _thresholds,
                Alerts = alerts,
                VehicleProfile = profile,
                IsExactVehicleMatch = isExact,
                MaxSeverity = _alertEngine.MaxSeverity(alerts),
            };
        }

        Current = status;
        Updated?.Invoke(this, status);
    }

    private static EndpointFields BuildDerivedFields(IReadOnlyDictionary<string, double?> metrics)
    {
        var fields = new EndpointFields(metrics.Count) { IsDerived = true };

        foreach (var (key, value) in metrics)
        {
            // 空值不写入，让解析器自然地落到「未命中」→ UI 显示 –。
            if (value is { } number)
            {
                fields.Set(key, number.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return fields;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _source.ConnectionChanged -= OnConnectionChanged;
        _source.DataReceived -= OnDataReceived;
        await _vehicles.DisposeAsync().ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 某一时刻的完整状态，供 UI 一次性读取。
/// </summary>
public sealed record TelemetryStatus
{
    /// <summary>空状态。</summary>
    public static TelemetryStatus Empty { get; } = new();

    /// <summary>遥测快照。</summary>
    public TelemetrySnapshot Snapshot { get; init; } = TelemetrySnapshot.Empty;

    /// <summary>当前生效的告警阈值。</summary>
    public AlertThresholds Thresholds { get; init; } = new();

    /// <summary>当前触发的告警（按严重级别降序）。</summary>
    public IReadOnlyList<AlertItem> Alerts { get; init; } = [];

    /// <summary>当前载具档案（可能是兵种兜底档案）。</summary>
    public VehicleProfile? VehicleProfile { get; init; }

    /// <summary>是否精确命中了载具库中的具体机型。</summary>
    public bool IsExactVehicleMatch { get; init; }

    /// <summary>最高告警级别。</summary>
    public AlertSeverity MaxSeverity { get; init; } = AlertSeverity.Info;
}
