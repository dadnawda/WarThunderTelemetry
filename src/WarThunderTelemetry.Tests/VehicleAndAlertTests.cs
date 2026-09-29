using WarThunderTelemetry.Core;
using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Alerts;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;
using WarThunderTelemetry.Core.Vehicles;
using Xunit;

namespace WarThunderTelemetry.Tests;

/// <summary>
/// 载具库与告警链路测试。
/// <para>
/// 覆盖「载具代号 → 阈值 → 规则触发」整条链路，含兵种兜底与边界值。
/// </para>
/// </summary>
public class VehicleAndAlertTests
{
    private static readonly AliasResolver Resolver = FieldCatalog.CreateResolver();

    private static TelemetrySnapshot Snapshot(
        double? ias = null,
        double? mach = null,
        double? gLoad = null,
        double? fuel = null,
        double? fuelCapacity = null,
        double? gear = null,
        double? flaps = null)
    {
        var state = new EndpointFields(6) { Endpoint = "state" };
        var indicators = new EndpointFields(2) { Endpoint = "indicators" };

        if (ias is { } iasValue) { state.Set("IAS, km/h", iasValue.ToString("R")); }
        if (mach is { } machValue) { state.Set("M", machValue.ToString("R")); }
        if (gLoad is { } gValue) { state.Set("Ny", gValue.ToString("R")); }
        if (fuel is { } fuelValue) { state.Set("Mfuel, kg", fuelValue.ToString("R")); }
        if (fuelCapacity is { } capacityValue) { state.Set("Mfuel0, kg", capacityValue.ToString("R")); }
        if (gear is { } gearValue) { state.Set("gears", gearValue.ToString("R")); }
        if (flaps is { } flapsValue) { state.Set("flaps, %", flapsValue.ToString("R")); }

        return new TelemetrySnapshot
        {
            State = state,
            Indicators = indicators,
            IsConnected = true,
        };
    }

    // ===== 载具库 =====

    [Fact]
    public void 内置载具库_可正常加载()
    {
        var database = VehicleDatabase.LoadDefault();

        Assert.True(database.Count > 0, "内置载具库不应为空");
    }

    [Fact]
    public void 载具库_按代号查找_大小写不敏感()
    {
        var database = VehicleDatabase.LoadDefault();

        Assert.NotNull(database.TryGet("la_9"));
        Assert.NotNull(database.TryGet("LA_9"));
        Assert.NotNull(database.TryGet("  la_9  "));
        Assert.Null(database.TryGet("nonexistent_vehicle_xyz"));
        Assert.Null(database.TryGet(null));
    }

    [Fact]
    public void 载具库_La9_参数与官方wiki一致()
    {
        var database = VehicleDatabase.LoadDefault();
        var profile = database.TryGet("la_9");

        Assert.NotNull(profile);
        Assert.Equal(850, profile.MaxSpeedIas);
        Assert.Equal(0.8, profile.MachLimit);
        Assert.Equal(13, profile.GLimitPositive);
        Assert.Equal(8, profile.GLimitNegative);
        Assert.Equal(320, profile.GearSpeedLimit);
    }

    [Fact]
    public void 载具库_命中时标记为精确匹配()
    {
        var database = VehicleDatabase.LoadDefault();
        var _ = database.ResolveOrFallback("la_9", "air", out var isExact);

        Assert.True(isExact);
    }

    [Fact]
    public void 载具库_未命中时回退兵种兜底()
    {
        var database = VehicleDatabase.LoadDefault();
        var profile = database.ResolveOrFallback("some_new_jet_2030", "air", out var isExact);

        Assert.False(isExact);
        Assert.Equal("fallback", profile.Source);
        Assert.NotNull(profile.MaxSpeedIas);
    }

    [Fact]
    public void 兜底_地面载具识别正确()
    {
        var profile = FallbackProfiles.Create("t_34_85", "tank");

        Assert.Equal("tank", profile.Army);
        Assert.Equal("tank", profile.Role);
    }

    [Theory]
    [InlineData("b_17_e", "bomber")]
    [InlineData("pe-8", "bomber")]
    [InlineData("ah-1f", "helicopter")]
    [InlineData("mi-24v", "helicopter")]
    [InlineData("il-2_1941", "attacker")]
    [InlineData("la_9", "fighter")]
    [InlineData(null, "fighter")]
    public void 兜底_从代号推断机型类别(string? type, string expectedRole)
    {
        var profile = FallbackProfiles.Create(type, "air");

        Assert.Equal(expectedRole, profile.Role);
    }

    // ===== 阈值生成 =====

    [Fact]
    public void 阈值生成_从载具档案正确映射()
    {
        var database = VehicleDatabase.LoadDefault();
        var thresholds = AlertThresholdFactory.Create(database, "la_9", "air");

        Assert.Equal(850, thresholds.MaxSpeedIas);
        Assert.Equal(0.8, thresholds.MachLimit);
        Assert.Equal(13, thresholds.GLimitPositive);
        Assert.Equal(8, thresholds.GLimitNegative);
        Assert.Equal("la_9", thresholds.SourceVehicleType);
    }

    [Fact]
    public void 阈值生成_兜底时来源标注为空()
    {
        var database = VehicleDatabase.LoadDefault();
        var thresholds = AlertThresholdFactory.Create(database, "unknown_vehicle", "air");

        Assert.Null(thresholds.SourceVehicleType);
        Assert.NotNull(thresholds.MaxSpeedIas);
    }

    // ===== 告警规则 =====

    private static AlertEngine Engine() => new AlertEngine(Resolver).RegisterBuiltInRules();

    private static AlertThresholds La9Thresholds() =>
        AlertThresholdFactory.Create(VehicleDatabase.LoadDefault(), "la_9", "air");

    [Fact]
    public void 超速_低于阈值不触发()
    {
        var alerts = Engine().Evaluate(Snapshot(ias: 700), La9Thresholds());

        Assert.DoesNotContain(alerts, a => a.RuleId == "overspeed");
    }

    [Fact]
    public void 超速_恰好等于阈值不触发()
    {
        // 边界：等于阈值不算超限。
        var alerts = Engine().Evaluate(Snapshot(ias: 850), La9Thresholds());

        Assert.DoesNotContain(alerts, a => a.RuleId == "overspeed");
    }

    [Fact]
    public void 超速_略超阈值触发警告()
    {
        var alerts = Engine().Evaluate(Snapshot(ias: 860), La9Thresholds());

        var hit = Assert.Single(alerts, a => a.RuleId == "overspeed");
        Assert.Equal(AlertSeverity.Warning, hit.Severity);
        Assert.Equal(850, hit.Threshold);
    }

    [Fact]
    public void 超速_大幅超出升级为危险()
    {
        var alerts = Engine().Evaluate(Snapshot(ias: 950), La9Thresholds());

        var hit = Assert.Single(alerts, a => a.RuleId == "overspeed");
        Assert.Equal(AlertSeverity.Critical, hit.Severity);
    }

    [Fact]
    public void 过载_正向超限触发()
    {
        var alerts = Engine().Evaluate(Snapshot(gLoad: 14), La9Thresholds());

        var hit = Assert.Single(alerts, a => a.RuleId == "overload");
        Assert.Contains("+", hit.Detail);
    }

    [Fact]
    public void 过载_负向超限触发()
    {
        // La-9 负向限制为 8g，-9g 应触发。
        var alerts = Engine().Evaluate(Snapshot(gLoad: -9), La9Thresholds());

        var hit = Assert.Single(alerts, a => a.RuleId == "overload");
        Assert.Contains("-", hit.Detail);
    }

    [Fact]
    public void 过载_正向边界内不触发()
    {
        var alerts = Engine().Evaluate(Snapshot(gLoad: 12.9), La9Thresholds());

        Assert.DoesNotContain(alerts, a => a.RuleId == "overload");
    }

    [Fact]
    public void 过载_负向边界内不触发()
    {
        var alerts = Engine().Evaluate(Snapshot(gLoad: -7.9), La9Thresholds());

        Assert.DoesNotContain(alerts, a => a.RuleId == "overload");
    }

    [Fact]
    public void 马赫_超限触发()
    {
        var alerts = Engine().Evaluate(Snapshot(mach: 0.9), La9Thresholds());

        Assert.Contains(alerts, a => a.RuleId == "mach");
    }

    [Fact]
    public void 马赫_活塞机无读数时不误报()
    {
        // La-9 有 0.8 限制，但若接口没给 M 值则不应触发。
        var alerts = Engine().Evaluate(Snapshot(ias: 500), La9Thresholds());

        Assert.DoesNotContain(alerts, a => a.RuleId == "mach");
    }

    [Fact]
    public void 低油量_百分比低于阈值触发()
    {
        // 100 / 1200 ≈ 8.3%，低于 15%
        var alerts = Engine().Evaluate(Snapshot(fuel: 100, fuelCapacity: 1200), La9Thresholds());

        Assert.Contains(alerts, a => a.RuleId == "low_fuel");
    }

    [Fact]
    public void 低油量_油量充足不触发()
    {
        var alerts = Engine().Evaluate(Snapshot(fuel: 900, fuelCapacity: 1200), La9Thresholds());

        Assert.DoesNotContain(alerts, a => a.RuleId == "low_fuel");
    }

    [Fact]
    public void 低油量_无双油量数据时不误报()
    {
        // 拿不到满油量就无法算百分比，必须跳过而不是猜测。
        var alerts = Engine().Evaluate(Snapshot(fuel: 100), La9Thresholds());

        Assert.DoesNotContain(alerts, a => a.RuleId == "low_fuel");
    }

    [Fact]
    public void 起落架超速_放下且超速触发()
    {
        var alerts = Engine().Evaluate(Snapshot(ias: 400, gear: 1), La9Thresholds());

        Assert.Contains(alerts, a => a.RuleId == "gear_overspeed");
    }

    [Fact]
    public void 起落架超速_收起时不触发()
    {
        var alerts = Engine().Evaluate(Snapshot(ias: 400, gear: 0), La9Thresholds());

        Assert.DoesNotContain(alerts, a => a.RuleId == "gear_overspeed");
    }

    [Fact]
    public void 起落架超速_放下但未超速不触发()
    {
        var alerts = Engine().Evaluate(Snapshot(ias: 250, gear: 1), La9Thresholds());

        Assert.DoesNotContain(alerts, a => a.RuleId == "gear_overspeed");
    }

    [Fact]
    public void 失速_低于失速速度触发危险()
    {
        // 兜底档案才带 StallSpeedIas；La-9 库中该项为 null（未核实），
        // 因此使用兜底阈值验证规则本身。
        var thresholds = AlertThresholdFactory.FromProfile(FallbackProfiles.Fighter, false);
        var alerts = Engine().Evaluate(Snapshot(ias: 100), thresholds);

        var hit = Assert.Single(alerts, a => a.RuleId == "stall");
        Assert.Equal(AlertSeverity.Critical, hit.Severity);
    }

    [Fact]
    public void 失速_恰好等于失速速度不触发()
    {
        var thresholds = AlertThresholdFactory.FromProfile(FallbackProfiles.Fighter, false);
        var alerts = Engine().Evaluate(Snapshot(ias: 150), thresholds);

        Assert.DoesNotContain(alerts, a => a.RuleId == "stall");
    }

    [Fact]
    public void 失速_襟翼放下时阈值放宽()
    {
        var thresholds = AlertThresholdFactory.FromProfile(FallbackProfiles.Fighter, false);
        var engine = Engine();

        // 150 * 0.85 = 127.5：默认 140 会触发失速，襟翼放下后 140 > 127.5 不触发。
        var clean = engine.Evaluate(Snapshot(ias: 140, flaps: 0), thresholds);
        Assert.Contains(clean, a => a.RuleId == "stall");

        var flapsDown = engine.Evaluate(Snapshot(ias: 140, flaps: 100), thresholds);
        Assert.DoesNotContain(flapsDown, a => a.RuleId == "stall");
    }

    [Fact]
    public void 未连接时不产出任何告警()
    {
        var snapshot = Snapshot(ias: 1000) with { IsConnected = false };
        var alerts = Engine().Evaluate(snapshot, La9Thresholds());

        Assert.Empty(alerts);
    }

    [Fact]
    public void 告警按严重级别降序排列()
    {
        // 同时触发超速(危险)与低油量(注意)
        var alerts = Engine().Evaluate(
            Snapshot(ias: 950, fuel: 100, fuelCapacity: 1200),
            La9Thresholds());

        Assert.True(alerts.Count >= 2);

        for (var i = 1; i < alerts.Count; i++)
        {
            Assert.True(
                alerts[i - 1].Severity >= alerts[i].Severity,
                "告警未按严重级别降序排列");
        }
    }

    [Fact]
    public void 阈值全空时_规则全部优雅跳过()
    {
        // 极端情况：载具库与兜底都拿不到任何阈值。
        var empty = new AlertThresholds();
        var alerts = Engine().Evaluate(Snapshot(ias: 9999, gLoad: 50, mach: 3), empty);

        Assert.Empty(alerts);
    }

    [Fact]
    public void 最高严重级别_正确汇总()
    {
        var engine = Engine();

        var none = engine.Evaluate(Snapshot(ias: 500), La9Thresholds());
        Assert.Equal(AlertSeverity.Info, engine.MaxSeverity(none));

        var warning = engine.Evaluate(Snapshot(ias: 900), La9Thresholds());
        Assert.Equal(AlertSeverity.Warning, engine.MaxSeverity(warning));
    }
}
