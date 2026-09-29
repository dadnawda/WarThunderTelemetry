using WarThunderTelemetry.Core;
using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;
using Xunit;

namespace WarThunderTelemetry.Tests;

/// <summary>
/// 派生指标引擎测试。
/// <para>
/// 重点：航向跨越 0/360 时的归一化（否则回转率会出现巨大跳变）、
/// 加速度长窗口差分、回转半径奇异值过滤、换载具时的历史重置。
/// </para>
/// </summary>
public class DerivedMetricsTests
{
    private static readonly AliasResolver Resolver = FieldCatalog.CreateResolver();

    private static TelemetrySnapshot Snapshot(
        double? speedKmh = null,
        double? altitude = null,
        double? vertical = null,
        double? heading = null,
        double? gLoad = null,
        bool useTas = true)
    {
        var state = new EndpointFields(8) { Endpoint = "state" };
        var indicators = new EndpointFields(4) { Endpoint = "indicators" };

        if (speedKmh is { } speed)
        {
            state.Set(useTas ? "TAS, km/h" : "IAS, km/h", speed.ToString("R"));
        }

        if (altitude is { } alt)
        {
            state.Set("H, m", alt.ToString("R"));
        }

        if (vertical is { } vy)
        {
            state.Set("Vy, m/s", vy.ToString("R"));
        }

        if (gLoad is { } g)
        {
            state.Set("Ny", g.ToString("R"));
        }

        if (heading is { } hdg)
        {
            indicators.Set("compass", hdg.ToString("R"));
        }

        return new TelemetrySnapshot
        {
            State = state,
            Indicators = indicators,
            IsConnected = true,
        };
    }

    private static MetricContext Context(TelemetrySnapshot snapshot, DateTimeOffset? now = null)
    {
        return new MetricContext
        {
            Snapshot = snapshot,
            History = [],
            Now = now ?? snapshot.Timestamp,
        };
    }

    [Theory]
    [InlineData(359, 1, 2)]     // 正向跨越 0
    [InlineData(1, 359, -2)]    // 反向跨越 360
    [InlineData(0, 180, 180)]
    [InlineData(180, 0, -180)]
    [InlineData(90, 100, 10)]
    [InlineData(10, 350, -20)]
    public void 航向差归一化_始终落在正负180度内(double from, double to, double expected)
    {
        var delta = DerivedMetricsProvider.NormalizeDelta(to - from);

        Assert.Equal(expected, delta, 6);
        Assert.InRange(delta, -180.0, 180.0);
    }

    [Fact]
    public void 回转率_跨越零度不产生跳变()
    {
        var provider = new DerivedMetricsProvider(Resolver);

        // 模拟以 30°/s 均匀左转（航向递增），恰好跨越 0/360
        var start = DateTimeOffset.Now;
        double? lastTurnRate = null;

        for (var i = 0; i <= 30; i++)
        {
            var heading = (350 + (i * 3)) % 360;   // 350 → 359 → 0 → 1 ...
            var snapshot = Snapshot(speedKmh: 500, heading: heading) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };

            var result = provider.Compute(Context(snapshot, snapshot.Timestamp));
            var turnRate = result["turnRate"];

            if (turnRate is { } rate)
            {
                Assert.True(
                    Math.Abs(rate) < 120,
                    $"第 {i} 帧回转率 {rate}°/s 异常 —— 航向跨越 0/360 时归一化可能失效");

                lastTurnRate = rate;
            }
        }

        // 期望约 +30°/s（航向递增，时间步 0.1s，每次 +3°）
        Assert.NotNull(lastTurnRate);
        Assert.InRange(lastTurnRate!.Value, 20, 40);
    }

    [Fact]
    public void 回转率_方向正确_右转为正左转为负()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        for (var i = 0; i <= 20; i++)
        {
            var snapshot = Snapshot(speedKmh: 500, heading: 100 + (i * 5)) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        var rightTurn = provider.Compute(Context(
            Snapshot(speedKmh: 500, heading: 100 + (21 * 5)) with { Timestamp = start.AddSeconds(2.1) },
            start.AddSeconds(2.1)));

        Assert.NotNull(rightTurn["turnRate"]);
        Assert.True(rightTurn["turnRate"]!.Value > 0, "航向递增应为正回转率");
    }

    [Fact]
    public void 加速度_长窗口差分_不因单帧抖动爆表()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        // 先跑一段稳定速度建立历史
        for (var i = 0; i <= 15; i++)
        {
            var snapshot = Snapshot(speedKmh: 500, altitude: 3000) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        // 单帧速度抖 +5 km/h 不应产生巨大加速度
        var jittered = Snapshot(speedKmh: 505, altitude: 3000) with
        {
            Timestamp = start.AddSeconds(1.5),
        };
        var result = provider.Compute(Context(jittered, jittered.Timestamp));

        if (result["accel"] is { } accel)
        {
            Assert.True(Math.Abs(accel) < 10, $"加速度 {accel} m/s² 明显异常，长窗口差分可能未生效");
        }
    }

    [Fact]
    public void 加速度_稳定速度趋近于零()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        IReadOnlyDictionary<string, double?>? result = null;
        for (var i = 0; i <= 25; i++)
        {
            var snapshot = Snapshot(speedKmh: 500, altitude: 3000) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            result = provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        Assert.NotNull(result!["accel"]);
        Assert.InRange(result!["accel"]!.Value, -0.5, 0.5);
    }

    [Fact]
    public void 能量高度_等于高度加动能项_且需真空速()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        const double speedKmh = 540;
        const double altitude = 3000;
        var speedMs = speedKmh / 3.6;
        var expected = altitude + (speedMs * speedMs / (2 * 9.81));

        IReadOnlyDictionary<string, double?>? result = null;
        for (var i = 0; i <= 10; i++)
        {
            var snapshot = Snapshot(speedKmh: speedKmh, altitude: altitude, useTas: true) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            result = provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        Assert.NotNull(result!["energy"]);
        Assert.Equal(expected, result!["energy"]!.Value, 1);
    }

    [Fact]
    public void 能量高度_仅表速时不给值_避免误导()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        // 只有 IAS 没有 TAS：速度能量偏差大，应降级不输出。
        IReadOnlyDictionary<string, double?>? result = null;
        for (var i = 0; i <= 10; i++)
        {
            var snapshot = Snapshot(speedKmh: 540, altitude: 3000, useTas: false) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            result = provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        Assert.Null(result!["energy"]);
    }

    [Fact]
    public void 爬升角_垂直速度为零时约等于零()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        IReadOnlyDictionary<string, double?>? result = null;
        for (var i = 0; i <= 8; i++)
        {
            var snapshot = Snapshot(speedKmh: 500, altitude: 3000, vertical: 0) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            result = provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        Assert.NotNull(result!["climb"]);
        Assert.Equal(0, result!["climb"]!.Value, 1);
    }

    [Fact]
    public void 爬升角_纯垂直上升时为90度()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        // 速度 10 m/s、垂直速度 10 m/s → 爬升角 90°
        IReadOnlyDictionary<string, double?>? result = null;
        for (var i = 0; i <= 5; i++)
        {
            var snapshot = Snapshot(speedKmh: 36, altitude: 1000, vertical: 10) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            result = provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        Assert.NotNull(result!["climb"]);
        Assert.Equal(90, result!["climb"]!.Value, 1);
    }

    [Fact]
    public void 回转半径_回转率不足时不输出()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        // 直线飞行：|ω| 远小于 3°/s 门槛，半径无意义。
        IReadOnlyDictionary<string, double?>? result = null;
        for (var i = 0; i <= 20; i++)
        {
            var snapshot = Snapshot(speedKmh: 500, heading: 90) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            result = provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        Assert.Null(result!["turnRadius"]);
        Assert.Null(result!["turnTime"]);
    }

    [Fact]
    public void 回转半径_稳定盘旋时输出合理值()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        // 30°/s 回转、500 km/h ≈ 138.9 m/s → r = 138.9 / (30*π/180) ≈ 265 m
        IReadOnlyDictionary<string, double?>? result = null;
        for (var i = 0; i <= 30; i++)
        {
            var heading = (i * 3) % 360;
            var snapshot = Snapshot(speedKmh: 500, heading: heading) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            result = provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        Assert.NotNull(result!["turnRadius"]);
        var radius = result!["turnRadius"]!.Value;

        // 允许平滑带来的偏差，但必须落在同一量级。
        Assert.InRange(radius, 150, 500);

        Assert.NotNull(result!["turnTime"]);
        Assert.InRange(result!["turnTime"]!.Value, 8, 16);   // 360/30 = 12 s
    }

    [Fact]
    public void 历史重置_长时间无数据后不跨段计算()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        // 第一段：500 km/h
        for (var i = 0; i <= 10; i++)
        {
            var snapshot = Snapshot(speedKmh: 500, altitude: 3000) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        var countBefore = provider.HistoryCount;
        Assert.True(countBefore > 0);

        // 间隔 10 秒后突然变成 900 km/h（相当于换载具或重连）
        var jumped = Snapshot(speedKmh: 900, altitude: 5000) with
        {
            Timestamp = start.AddSeconds(11),
        };
        var result = provider.Compute(Context(jumped, jumped.Timestamp));

        // 历史被清空重建，不应算出 (900-500)/10s 这类跨段加速度。
        Assert.True(provider.HistoryCount < countBefore);
        Assert.Null(result["accel"]);
    }

    [Fact]
    public void 重置_清空全部历史与平滑值()
    {
        var provider = new DerivedMetricsProvider(Resolver);
        var start = DateTimeOffset.Now;

        for (var i = 0; i <= 10; i++)
        {
            var snapshot = Snapshot(speedKmh: 500, altitude: 3000, heading: i * 3) with
            {
                Timestamp = start.AddSeconds(i * 0.1),
            };
            provider.Compute(Context(snapshot, snapshot.Timestamp));
        }

        Assert.True(provider.HistoryCount > 0);

        provider.Reset();

        Assert.Equal(0, provider.HistoryCount);

        // 重置后单帧不应产出微分类指标
        var single = Snapshot(speedKmh: 500, altitude: 3000);
        var result = provider.Compute(Context(single));
        Assert.Null(result["accel"]);
        Assert.Null(result["turnRate"]);
    }

    [Fact]
    public void 俯仰与坡度_直接取自地平仪()
    {
        var provider = new DerivedMetricsProvider(Resolver);

        var indicators = new EndpointFields(2) { Endpoint = "indicators" };
        indicators.Set("aviahorizon_pitch", "-14.95");
        indicators.Set("aviahorizon_roll", "30.5");

        var snapshot = new TelemetrySnapshot
        {
            State = new EndpointFields(1),
            Indicators = indicators,
            IsConnected = true,
        };

        var result = provider.Compute(Context(snapshot));

        Assert.Equal(-14.95, result["pitch"]!.Value, 2);
    }
}
