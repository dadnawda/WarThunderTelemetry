using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;
using WarThunderTelemetry.Core.Weapons;
using Xunit;

namespace WarThunderTelemetry.Tests;

/// <summary>
/// 导弹参数库、发射包线计算与武器状态链路测试。
/// <para>
/// 重点覆盖三类容易出错的地方：
/// <list type="number">
/// <item><b>别名歧义</b>：<c>R-60M</c> 绝不能被 <c>R-60</c> 抢走；</item>
/// <item><b>物理走势</b>：高空射程必须大于低空、迎头必须大于尾追、
/// 过载超限必须否决发射 —— 这些方向性错误比数值误差更危险；</item>
/// <item><b>数据缺失</b>：参数不全时必须照常给出结果并标记估算，而不是抛异常或给 0。</item>
/// </list>
/// </para>
/// </summary>
public class MissileEnvelopeTests
{
    private static readonly AliasResolver Resolver = FieldCatalog.CreateResolver();
    private static readonly WeaponStatusService Service = new(Resolver);

    // ================= 数据库完整性 =================

    [Fact]
    public void 参数库_不应为空且有唯一Id()
    {
        var profiles = MissileDatabase.Profiles;

        Assert.True(profiles.Count >= 25, $"收录数量过少：{profiles.Count}");

        var ids = profiles.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void 参数库_每条都必须有正的最大射程与过载()
    {
        foreach (var profile in MissileDatabase.Profiles)
        {
            Assert.True(profile.MaxRange is > 0, $"{profile.Id} 缺最大射程");
            Assert.True(profile.MaxG is > 0, $"{profile.Id} 缺最大过载");
            Assert.True(profile.BurnTime is > 0, $"{profile.Id} 缺燃烧时间");
            Assert.True(profile.MaxSpeed is > 0, $"{profile.Id} 缺最大速度");
            Assert.True(profile.LaunchGLimit is > 0, $"{profile.Id} 缺发射过载限制");
            Assert.False(string.IsNullOrWhiteSpace(profile.DisplayNameZh), $"{profile.Id} 缺中文名");
        }
    }

    [Fact]
    public void 参数库_射程数量级应落在合理区间()
    {
        foreach (var profile in MissileDatabase.Profiles)
        {
            // 最远不过 400 km（R-37M 量级），最近不小于 2 km。
            Assert.InRange(profile.MaxRange!.Value, 2_000, 400_000);
        }
    }

    // ================= 别名解析 =================

    [Theory]
    [InlineData("R-60MK", "r60m")]
    [InlineData("R-60M", "r60m")]
    [InlineData("AIM-9L", "aim9l")]
    [InlineData("AIM-9L Sidewinder", "aim9l")]
    [InlineData("aim-9l", "aim9l")]
    [InlineData("R-3R", "r3r")]
    [InlineData("R-3S", "r3s")]
    [InlineData("R-27ER", "r27er")]
    [InlineData("AIM-7M Sparrow", "aim7m")]
    [InlineData("R550 Magic 1", "magic1")]
    [InlineData("PL-2", "pl2")]
    public void 别名解析_应命中预期导弹(string input, string expectedId)
    {
        var hit = MissileDatabase.FindByAlias(input);

        Assert.NotNull(hit);
        Assert.Equal(expectedId, hit!.Id);
    }

    [Fact]
    public void 别名解析_R60M不能被R60抢走()
    {
        var upgrade = MissileDatabase.FindByAlias("R-60M");
        var baseModel = MissileDatabase.FindByAlias("R-60");

        Assert.NotNull(upgrade);
        Assert.NotNull(baseModel);
        Assert.Equal("r60m", upgrade!.Id);
        Assert.Equal("r60", baseModel!.Id);

        // 两者确实不是同一枚弹，否则这个测试没有意义。
        Assert.NotEqual(upgrade.Id, baseModel.Id);
        Assert.True(upgrade.MaxRange > baseModel.MaxRange);
    }

    [Fact]
    public void 别名解析_无法识别时应返回空而不是瞎猜()
    {
        Assert.Null(MissileDatabase.FindByAlias("不存在的东西"));
        Assert.Null(MissileDatabase.FindByAlias(""));
        Assert.Null(MissileDatabase.FindByAlias(null));
    }

    // ================= 包线计算的物理走势 =================

    private static MissileProfile Aim9L =>
        MissileDatabase.Profiles.First(p => p.Id == "aim9l");

    private static MissileProfile Aim7M =>
        MissileDatabase.Profiles.First(p => p.Id == "aim7m");

    [Fact]
    public void 高空射程应大于低空()
    {
        var low = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 200,
            TargetDistanceM = 5_000,
            Aspect = EngagementAspect.TailOn,
        });

        var high = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 9_000,
            TargetDistanceM = 5_000,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.True(high.MaxRange > low.MaxRange,
            $"高空 {high.MaxRange:F0} m 应大于低空 {low.MaxRange:F0} m");
        Assert.True(high.EffectiveRange > low.EffectiveRange);
    }

    [Fact]
    public void 迎头射程应大于尾追()
    {
        var headOn = LaunchEnvelopeCalculator.Solve(Aim7M, new LaunchInput
        {
            OwnSpeedKmh = 1_000,
            OwnAltitudeM = 6_000,
            TargetDistanceM = 15_000,
            Aspect = EngagementAspect.HeadOn,
        });

        var tailOn = LaunchEnvelopeCalculator.Solve(Aim7M, new LaunchInput
        {
            OwnSpeedKmh = 1_000,
            OwnAltitudeM = 6_000,
            TargetDistanceM = 15_000,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.True(headOn.EffectiveRange > tailOn.EffectiveRange,
            $"迎头 {headOn.EffectiveRange:F0} m 应大于尾追 {tailOn.EffectiveRange:F0} m");
    }

    [Fact]
    public void 本机速度越高射程越远()
    {
        var slow = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 400,
            OwnAltitudeM = 3_000,
            Aspect = EngagementAspect.TailOn,
        });

        var fast = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 1_400,
            OwnAltitudeM = 3_000,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.True(fast.MaxRange > slow.MaxRange);
    }

    [Fact]
    public void 射程层级_最小小于不可逃逸小于有效小于最大()
    {
        var solution = LaunchEnvelopeCalculator.Solve(Aim7M, new LaunchInput
        {
            OwnSpeedKmh = 1_000,
            OwnAltitudeM = 6_000,
            TargetDistanceM = 20_000,
            Aspect = EngagementAspect.HeadOn,
        });

        Assert.True(solution.MinRange > 0);
        Assert.True(solution.MinRange < solution.NoEscapeRange,
            $"最小 {solution.MinRange:F0} 应小于不可逃逸 {solution.NoEscapeRange:F0}");
        Assert.True(solution.NoEscapeRange <= solution.EffectiveRange,
            $"不可逃逸 {solution.NoEscapeRange:F0} 应不大于有效 {solution.EffectiveRange:F0}");
        Assert.True(solution.EffectiveRange <= solution.MaxRange,
            $"有效 {solution.EffectiveRange:F0} 应不大于最大 {solution.MaxRange:F0}");
    }

    // ================= 建议判定 =================

    [Fact]
    public void 近距离发射应给出可发射()
    {
        var solution = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 3_000,
            OwnLoadG = 2,
            TargetDistanceM = 2_000,
            TargetSpeedKmh = 700,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.Equal(LaunchVerdict.Fire, solution.Verdict);
        Assert.True(solution.InNoEscapeZone);
        Assert.True(solution.SeekerLocked);
    }

    [Fact]
    public void 过远发射应给出过远()
    {
        var solution = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 3_000,
            TargetDistanceM = 40_000,
            Aspect = EngagementAspect.HeadOn,
        });

        Assert.Equal(LaunchVerdict.TooFar, solution.Verdict);
    }

    [Fact]
    public void 低于最小射程应给出过近()
    {
        var solution = LaunchEnvelopeCalculator.Solve(Aim7M, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 5_000,
            TargetDistanceM = 300,
            Aspect = EngagementAspect.HeadOn,
        });

        Assert.Equal(LaunchVerdict.TooClose, solution.Verdict);
    }

    [Fact]
    public void 发射过载超限应否决发射()
    {
        // R-3S 的发射过载限制只有 2 g。
        var r3s = MissileDatabase.Profiles.First(p => p.Id == "r3s");

        var solution = LaunchEnvelopeCalculator.Solve(r3s, new LaunchInput
        {
            OwnSpeedKmh = 800,
            OwnAltitudeM = 3_000,
            OwnLoadG = 6,
            TargetDistanceM = 2_000,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.Equal(LaunchVerdict.Inhibit, solution.Verdict);
        Assert.False(solution.LoadWithinLimit);
        Assert.Contains("过载", solution.Advice);
    }

    [Fact]
    public void 过载在限制内不应否决()
    {
        var r3s = MissileDatabase.Profiles.First(p => p.Id == "r3s");

        var solution = LaunchEnvelopeCalculator.Solve(r3s, new LaunchInput
        {
            OwnSpeedKmh = 800,
            OwnAltitudeM = 3_000,
            OwnLoadG = 1.5,
            TargetDistanceM = 2_000,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.True(solution.LoadWithinLimit);
        Assert.NotEqual(LaunchVerdict.Inhibit, solution.Verdict);
    }

    [Fact]
    public void 载机速度过低应否决发射()
    {
        var solution = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 120,
            OwnAltitudeM = 3_000,
            TargetDistanceM = 3_000,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.Equal(LaunchVerdict.Inhibit, solution.Verdict);
        Assert.Contains("速度", solution.Advice);
    }

    [Fact]
    public void 尾追时红外弹锁定距离应短于迎头()
    {
        var headOn = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 5_000,
            TargetDistanceM = 8_000,
            Aspect = EngagementAspect.HeadOn,
        });

        var tailOn = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 5_000,
            TargetDistanceM = 8_000,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.True(headOn.EffectiveRange > tailOn.EffectiveRange,
            "AIM-9L 迎头导引头锁定距离应显著优于尾追");
    }

    [Fact]
    public void 无距离数据时应给出可尝试或否决而非崩溃()
    {
        var solution = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 5_000,
            OwnLoadG = 1,
        });

        Assert.NotNull(solution);
        Assert.NotEqual(LaunchVerdict.TooClose, solution.Verdict);
        Assert.Null(solution.TimeToImpact);
    }

    [Fact]
    public void 缺参数导弹应标记估算而不是给零()
    {
        // 只给名字的最小档案，其余全靠引擎估算。
        var sparse = new MissileProfile
        {
            Id = "test_sparse",
            DisplayName = "Test Sparse",
            BurnTime = 3,
            MaxSpeed = 700,
            MaxG = 20,
        };

        var solution = LaunchEnvelopeCalculator.Solve(sparse, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 3_000,
            TargetDistanceM = 4_000,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.True(solution.HasEstimates);
        Assert.True(solution.MaxRange > 0);
        Assert.True(solution.EffectiveRange > 0);
    }

    [Fact]
    public void 命中时间应为正且随距离增加()
    {
        var near = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 3_000,
            TargetDistanceM = 2_000,
            TargetSpeedKmh = 800,
            Aspect = EngagementAspect.TailOn,
        });

        var far = LaunchEnvelopeCalculator.Solve(Aim9L, new LaunchInput
        {
            OwnSpeedKmh = 900,
            OwnAltitudeM = 3_000,
            TargetDistanceM = 6_000,
            TargetSpeedKmh = 800,
            Aspect = EngagementAspect.TailOn,
        });

        Assert.NotNull(near.TimeToImpact);
        Assert.NotNull(far.TimeToImpact);
        Assert.True(near.TimeToImpact > 0);
        Assert.True(far.TimeToImpact > near.TimeToImpact);
        Assert.True(far.TimeToImpact < 60, "命中时间不应超过导弹制导时长量级");
    }

    // ================= 武器状态链路 =================

    private static TelemetrySnapshot SnapshotWith(
        string? weaponName = null,
        double? ias = null,
        double? altitude = null,
        double? gLoad = null,
        double? ammo = null)
    {
        var state = new EndpointFields(6) { Endpoint = "state" };
        var indicators = new EndpointFields(2) { Endpoint = "indicators" };

        if (ias is { } iasValue) { state.Set("IAS, km/h", iasValue.ToString("R")); }
        if (altitude is { } altValue) { state.Set("H, m", altValue.ToString("R")); }
        if (gLoad is { } gValue) { state.Set("Ny", gValue.ToString("R")); }
        if (ammo is { } ammoValue) { state.Set("ammo", ammoValue.ToString("R")); }
        if (weaponName is not null) { state.Set("weapon_name", weaponName); }

        return new TelemetrySnapshot
        {
            State = state,
            Indicators = indicators,
            IsConnected = true,
        };
    }

    [Fact]
    public void 未连接应返回断开状态()
    {
        var status = Service.Build(TelemetrySnapshot.Empty);

        Assert.False(status.IsConnected);
        Assert.Null(status.Solution);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
    }

    [Fact]
    public void 识别到导弹应给出解算与建议()
    {
        var service = new WeaponStatusService(Resolver);

        var status = service.Build(SnapshotWith(
            weaponName: "AIM-9L",
            ias: 950,
            altitude: 4_000,
            gLoad: 1.5,
            ammo: 2));

        Assert.True(status.IsConnected);
        Assert.NotNull(status.Missile);
        Assert.Equal("aim9l", status.Missile!.Id);
        Assert.NotNull(status.Solution);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));

        // 本机状态要正确带过来。
        Assert.Equal(950, status.OwnSpeedKmh!.Value, 1);
        Assert.Equal(4_000, status.OwnAltitudeM!.Value, 1);
        Assert.Equal(2, status.Ammo!.Value, 1);
    }

    [Fact]
    public void 未识别武器时应给出提示而非崩溃()
    {
        var service = new WeaponStatusService(Resolver);

        var status = service.Build(SnapshotWith(weaponName: "某不存在的武器", ias: 900));

        Assert.True(status.IsConnected);
        Assert.Null(status.Missile);
        Assert.Null(status.Solution);
        Assert.Contains("未收录", status.Message);
    }

    [Fact]
    public void 接口丢字段时应保留上次识别的导弹()
    {
        var service = new WeaponStatusService(Resolver);

        // 第一次识别成功。
        var first = service.Build(SnapshotWith(weaponName: "AIM-7M", ias: 900, altitude: 6_000));
        Assert.Equal("aim7m", first.Missile!.Id);

        // 第二次接口没给武器名（游戏中切挂载的瞬间常出现）。
        var second = service.Build(SnapshotWith(ias: 900, altitude: 6_000));

        Assert.NotNull(second.Missile);
        Assert.Equal("aim7m", second.Missile!.Id);
    }

    [Fact]
    public void 手动指定导弹应覆盖自动识别()
    {
        var service = new WeaponStatusService(Resolver);
        var r60m = MissileDatabase.Profiles.First(p => p.Id == "r60m");

        var status = service.Build(
            SnapshotWith(weaponName: "AIM-9L", ias: 900, altitude: 5_000),
            overrideMissile: r60m);

        Assert.Equal("r60m", status.Missile!.Id);
    }

    [Fact]
    public void 手动目标信息应参与解算()
    {
        var service = new WeaponStatusService(Resolver);

        var withoutTarget = service.Build(SnapshotWith(
            weaponName: "AIM-9L", ias: 900, altitude: 5_000, gLoad: 1));

        var withTarget = service.Build(
            SnapshotWith(weaponName: "AIM-9L", ias: 900, altitude: 5_000, gLoad: 1),
            manualTarget: new TargetInput
            {
                DistanceM = 2_500,
                SpeedKmh = 750,
                Aspect = EngagementAspect.TailOn,
            });

        Assert.False(withoutTarget.TargetKnown);
        Assert.True(withTarget.TargetKnown);

        // 给了距离才可能算出命中时间。
        Assert.Null(withoutTarget.Solution!.TimeToImpact);
        Assert.NotNull(withTarget.Solution!.TimeToImpact);
    }

    [Fact]
    public void 从原始武器字段也能识别导弹()
    {
        var service = new WeaponStatusService(Resolver);

        var state = new EndpointFields(4) { Endpoint = "state" };
        state.Set("IAS, km/h", "900");
        state.Set("H, m", "5000");
        // 模拟游戏把挂载名放在带编号的 weapon 字段里。
        state.Set("weapon_name", "R-60M");

        var snapshot = new TelemetrySnapshot
        {
            State = state,
            Indicators = new EndpointFields(1) { Endpoint = "indicators" },
            IsConnected = true,
        };

        var status = service.Build(snapshot);

        Assert.NotNull(status.Missile);
        Assert.Equal("r60m", status.Missile!.Id);
    }
}
