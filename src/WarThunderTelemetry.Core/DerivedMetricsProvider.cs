using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.Core;

/// <summary>
/// 性能派生指标引擎。
/// <para>
/// 游戏只给原始数据（速度、高度、航向），能量、加速度、回转率这些战术指标必须自己算。
/// 用滑动窗口做微分并做指数平滑，主进程算一次，仪表盘与悬浮窗共用同一份结果。
/// </para>
/// <para>
/// 指标定义：
/// <list type="bullet">
/// <item>加速度 <c>a = dV/dt</c>，取真空速差分</item>
/// <item>能量高度 <c>E = h + V²/(2g)</c></item>
/// <item>爬升角 <c>γ = asin(Vy/V)</c></item>
/// <item>回转率 <c>ω = dHeading/dt</c>（航向差先归一化到 ±180°）</item>
/// <item>回转半径 <c>r = V/ω</c></item>
/// <item>盘旋 360° 耗时 <c>t = 360/|ω|</c></item>
/// </list>
/// </para>
/// </summary>
public sealed class DerivedMetricsProvider : IMetricProvider
{
    /// <summary>重力加速度（m/s²）。</summary>
    private const double G = 9.81;

    /// <summary>历史窗口长度（毫秒）。</summary>
    private const int WindowMs = 3000;

    /// <summary>加速度差分的最小时间跨度（毫秒）。太短会被速度抖动放大成噪声。</summary>
    private const int AccelMinSpanMs = 1200;

    /// <summary>航向差分的最小时间跨度（毫秒）。</summary>
    private const int TurnMinSpanMs = 400;

    /// <summary>回转半径的奇异值上限（米）。超出视为噪声。</summary>
    private const double MaxTurnRadiusM = 50_000;

    /// <summary>回转率有效门槛（度/秒）。低于此值算半径无意义。</summary>
    private const double MinTurnRateDegPerSec = 3.0;

    /// <summary>历史样本最大保留数，防止极端高频下无限增长。</summary>
    private const int MaxSamples = 256;

    private readonly List<MetricSample> _history = new(64);
    private readonly AliasResolver _resolver;

    private double? _smoothedAccel;
    private double? _smoothedTurnRate;

    /// <summary>构造。</summary>
    public DerivedMetricsProvider(AliasResolver? resolver = null)
    {
        _resolver = resolver ?? FieldCatalog.CreateResolver();
    }

    /// <inheritdoc />
    public string Id => "derived.core";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, MetricDefinition> Definitions { get; } =
        new Dictionary<string, MetricDefinition>(StringComparer.Ordinal)
        {
            ["accel"] = new("accel", "加速度", "m/s²", 2),
            ["energy"] = new("energy", "能量高度", "m", 0),
            ["climb"] = new("climb", "爬升角", "°", 1),
            ["turnRate"] = new("turnRate", "回转率", "°/s", 1),
            ["turnRadius"] = new("turnRadius", "回转半径", "m", 0),
            ["turnTime"] = new("turnTime", "盘旋360°", "s", 1),
            ["pitch"] = new("pitch", "俯仰角", "°", 1),
            ["bank"] = new("bank", "坡度", "°", 1),
        };

    /// <summary>
    /// 记录一帧快照并计算派生指标。
    /// </summary>
    public IReadOnlyDictionary<string, double?> Compute(MetricContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sample = ExtractSample(context.Snapshot);
        AppendSample(sample);

        var result = new Dictionary<string, double?>(StringComparer.Ordinal);
        ComputeSpeedMetrics(sample, result);
        ComputeTurnMetrics(sample, result);

        // 俯仰与坡度直接取自地平仪，无需微分。
        result["pitch"] = _resolver
            .GetById("pitch") is { } pitchField
            ? context.Snapshot.Resolve(pitchField, _resolver).Number
            : null;

        return result;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _history.Clear();
        _smoothedAccel = null;
        _smoothedTurnRate = null;
    }

    /// <summary>
    /// 从快照中抽取计算所需的轻量样本。
    /// </summary>
    public MetricSample ExtractSample(TelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 速度源：优先真空速；缺失时退化为表速（量级有偏差但加速度趋势正确，标记为估算）。
        var tas = snapshot.Resolve(RequireField("tas"), _resolver);
        var speedKmh = tas.Number;
        var estimated = false;

        if (speedKmh is null)
        {
            var ias = snapshot.Resolve(RequireField("ias"), _resolver);
            speedKmh = ias.Number;
            estimated = speedKmh is not null;
        }

        var vertical = snapshot.Resolve(RequireField("vertical_speed"), _resolver);
        var altitude = snapshot.Resolve(RequireField("altitude"), _resolver);
        var heading = snapshot.Resolve(RequireField("heading"), _resolver);
        var gLoad = snapshot.Resolve(RequireField("g_load"), _resolver);

        return new MetricSample(
            snapshot.Timestamp,
            speedKmh is null ? null : speedKmh.Value / 3.6,
            estimated,
            altitude.Number,
            vertical.Number,
            heading.Number,
            gLoad.Number);
    }

    private FieldDescriptor RequireField(string id)
    {
        return _resolver.GetById(id)
               ?? throw new InvalidOperationException($"字段目录缺少 {id}，请检查 FieldCatalog。");
    }

    private void AppendSample(MetricSample sample)
    {
        var last = _history.Count > 0 ? _history[^1] : default;

        // 断线重连或更换载具后，旧历史已无意义 —— 直接重置，
        // 否则会跨载具算出荒谬的加速度与回转率。
        if (_history.Count > 0 && (sample.Timestamp - last.Timestamp).TotalMilliseconds > WindowMs)
        {
            _history.Clear();
            _smoothedAccel = null;
            _smoothedTurnRate = null;
        }

        _history.Add(sample);

        var cutoff = sample.Timestamp.AddMilliseconds(-WindowMs);
        var removeCount = 0;
        while (removeCount < _history.Count - 2 && _history[removeCount].Timestamp < cutoff)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            _history.RemoveRange(0, removeCount);
        }

        if (_history.Count > MaxSamples)
        {
            _history.RemoveRange(0, _history.Count - MaxSamples);
        }
    }

    private void ComputeSpeedMetrics(MetricSample current, Dictionary<string, double?> result)
    {
        result["accel"] = null;
        result["energy"] = null;
        result["climb"] = null;

        if (current.SpeedMs is not { } speed || speed <= 5)
        {
            return;
        }

        // 加速度：取足够时间跨度的旧样本做长窗口差分，再指数平滑。
        var reference = FindSampleAtLeast(AccelMinSpanMs);
        if (reference is { SpeedMs: { } oldSpeed })
        {
            var dt = (current.Timestamp - reference.Value.Timestamp).TotalSeconds;
            if (dt >= 0.8)
            {
                var raw = (speed - oldSpeed) / dt;

                // 过滤明显不合理的结果（换载具、瞬移等）。
                if (Math.Abs(raw) < 60)
                {
                    _smoothedAccel = _smoothedAccel is null
                        ? raw
                        : (_smoothedAccel.Value * 0.5) + (raw * 0.5);
                }
            }
        }

        result["accel"] = _smoothedAccel;

        // 能量高度：只有真实真空速才算，用表速估算会明显偏离。
        if (current.AltitudeM is { } altitude && !current.SpeedIsEstimated)
        {
            result["energy"] = altitude + (speed * speed / (2 * G));
        }

        // 爬升角 γ = asin(Vy / V)。
        if (current.VerticalSpeedMs is { } verticalSpeed)
        {
            var sinGamma = Math.Clamp(verticalSpeed / speed, -1.0, 1.0);
            result["climb"] = Math.Asin(sinGamma) * 180.0 / Math.PI;
        }
    }

    private void ComputeTurnMetrics(MetricSample current, Dictionary<string, double?> result)
    {
        result["turnRate"] = null;
        result["turnRadius"] = null;
        result["turnTime"] = null;

        if (current.HeadingDeg is not { } heading)
        {
            return;
        }

        var reference = FindSampleAtLeast(TurnMinSpanMs);
        if (reference is { HeadingDeg: { } oldHeading })
        {
            var dt = (current.Timestamp - reference.Value.Timestamp).TotalSeconds;
            if (dt > 0.2)
            {
                // 关键：航向跨越 0/360 时必须先归一化到 ±180°，
                // 否则从 359° 到 1° 会被算成 -358°/s 的巨大跳变。
                var delta = NormalizeDelta(heading - oldHeading);
                var raw = delta / dt;

                _smoothedTurnRate = _smoothedTurnRate is null
                    ? raw
                    : (_smoothedTurnRate.Value * 0.7) + (raw * 0.3);
            }
        }

        if (_smoothedTurnRate is not { } turnRate)
        {
            return;
        }

        result["turnRate"] = turnRate;

        // 只有回转率足够大时算半径才有意义；同时过滤奇异值。
        if (Math.Abs(turnRate) <= MinTurnRateDegPerSec || current.SpeedMs is not { } speed || speed <= 20)
        {
            return;
        }

        var omega = Math.Abs(turnRate) * Math.PI / 180.0;
        var radius = speed / omega;

        if (radius < MaxTurnRadiusM)
        {
            result["turnRadius"] = radius;
            result["turnTime"] = 360.0 / Math.Abs(turnRate);
        }
    }

    /// <summary>
    /// 查找时间差至少为 <paramref name="minSpanMs"/> 的旧样本（越接近该跨度越好）。
    /// </summary>
    private MetricSample? FindSampleAtLeast(int minSpanMs)
    {
        if (_history.Count < 2)
        {
            return null;
        }

        var current = _history[^1];

        // 从旧到新找第一个满足跨度的样本，得到最接近目标跨度的参考点。
        for (var i = 0; i < _history.Count - 1; i++)
        {
            if ((current.Timestamp - _history[i].Timestamp).TotalMilliseconds >= minSpanMs)
            {
                return _history[i];
            }
        }

        return null;
    }

    /// <summary>
    /// 把角度差归一化到 ±180°。
    /// </summary>
    public static double NormalizeDelta(double degrees)
    {
        var normalized = degrees % 360.0;
        if (normalized > 180.0)
        {
            normalized -= 360.0;
        }
        else if (normalized < -180.0)
        {
            normalized += 360.0;
        }

        return normalized;
    }

    /// <summary>当前保留的历史样本数（测试用）。</summary>
    public int HistoryCount => _history.Count;
}
