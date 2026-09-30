namespace WarThunderTelemetry.Core.Weapons;

/// <summary>
/// 基于数据挖掘参数的导弹质点飞行模型。
/// <para>
/// <b>这是「自己写的推演」，结构对标 statshark 一类弹道计算器：</b>
/// 逐帧积分出速度-时间与距离-时间曲线，再在曲线上回答
/// 「最大射程 / 不可逃逸区 / 有效射程 / 命中时间」。
/// </para>
/// <para>
/// 物理假设（一维质点，沿视线方向）：
/// <list type="bullet">
/// <item>动力段：推力恒定（数据表海平面基准值），质量从发射质量线性衰减到燃尽空重；</item>
/// <item>阻力：<c>a = k · (ρ/ρ₀) · v²</c>，空气密度按标准大气
/// <c>ρ/ρ₀ = exp(-h/8500)</c> 随高度衰减；</item>
/// <item>滑翔段：二次阻力下速度有解析形式 <c>v(t) = v₀/(1 + k·v₀·t)</c>，
/// 积分即得距离曲线。</item>
/// </list>
/// </para>
/// <para>
/// <b>锚定</b>：数据表的「最大飞行距离」是游戏最优条件下的实测值。
/// 引擎在固定基准高度（3000 m）用二分法反推每枚弹的有效阻力缩放 k，
/// 使模拟总距离精确落在锚点上 —— <b>曲线形状来自物理，绝对量来自数据</b>，
/// 两头都不编。标定后换发射高度时只缩放空气密度，高空优势得以保留。
/// </para>
/// </summary>
public sealed class MissileFlightModel
{
    private const double AirDensityScaleHeightM = 8500.0;
    private const double CalibrationAltitudeM = 3_000.0;
    private const double FallbackDragScale = 1.0e-4;
    private const double SampleStepS = 0.05;
    private const double MinCruiseSpeedMs = 150.0;

    private readonly double[] _times;
    private readonly double[] _speeds;
    private readonly double[] _distances;

    private MissileFlightModel(double[] times, double[] speeds, double[] distances,
                               double lifeTimeS, double maxSpeedMs, double burnoutTimeS,
                               double dragScale)
    {
        _times = times;
        _speeds = speeds;
        _distances = distances;
        LifeTimeS = lifeTimeS;
        MaxSpeedMs = maxSpeedMs;
        BurnoutTimeS = burnoutTimeS;
        DragScale = dragScale;
    }

    /// <summary>总寿命（s）。曲线终点。</summary>
    public double LifeTimeS { get; }

    /// <summary>全程最大速度（m/s）。</summary>
    public double MaxSpeedMs { get; }

    /// <summary>动力段结束时刻（s）。之后进入滑翔。</summary>
    public double BurnoutTimeS { get; }

    /// <summary>标定出的有效阻力缩放系数。</summary>
    public double DragScale { get; }

    /// <summary>模拟出的最大飞行距离（m）。</summary>
    public double TotalDistanceM => DistanceAt(LifeTimeS);

    /// <summary>
    /// 尝试为导弹构建飞行模型。数据不足返回 false。
    /// </summary>
    /// <param name="profile">导弹参数。</param>
    /// <param name="launchAltitudeM">发射高度（m）。</param>
    /// <param name="launchSpeedKmh">载机速度（km/h）。</param>
    public static bool TryBuild(
        MissileProfile profile,
        double launchAltitudeM,
        double launchSpeedKmh,
        out MissileFlightModel? model)
    {
        model = null;

        // 至少要有推力+燃烧（动力段）或「最大距离+滞空」（锚定滑翔段）之一。
        var hasBoost = profile.ThrustN is { } thrust && thrust > 0 &&
                       profile.BurnTime is { } burn && burn > 0;
        var hasAnchor = profile.MaxDistanceM is { } anchorDist && anchorDist > 500 &&
                        profile.LifeTimeS is { } anchorLife && anchorLife > 3;

        if (!hasBoost && !hasAnchor)
        {
            return false;
        }

        // 缺失参数的保守回退值；用了回退值由外层标记「估算」。
        var mass = profile.MassKg ?? EstimateMass(profile);
        var burnout = profile.BurnoutMassKg ?? mass * 0.82;
        var caliber = profile.CaliberMm ?? 150.0;
        var cxk = profile.DragCxk ?? 1.5;
        var life = profile.LifeTimeS ?? 30.0;
        var startSpeed = (profile.StartSpeedMs ?? 0) + Math.Max(50.0, launchSpeedKmh / 3.6);

        // 1) 在基准高度积分一次；有锚点时反推阻力缩放，让总距离落在数据上。
        var dragScale = FallbackDragScale * DragShape(cxk, caliber);
        var anchored = Integrate(profile, mass, burnout, life, startSpeed,
                                 CalibrationAltitudeM, hasBoost, dragScale);

        if (hasAnchor && profile.MaxDistanceM is { } anchor && anchor > 500)
        {
            for (var i = 0; i < 14; i++)
            {
                var current = anchored.TotalDistanceM;
                if (current < 100 || Math.Abs(current - anchor) / anchor < 0.02)
                {
                    break;
                }

                dragScale *= Math.Clamp(current / anchor, 0.25, 4.0);
                anchored = Integrate(profile, mass, burnout, life, startSpeed,
                                     CalibrationAltitudeM, hasBoost, dragScale);
            }
        }

        // 2) 用发射高度重算空气密度，得到本次发射的曲线。
        model = Integrate(profile, mass, burnout, life, startSpeed,
                          launchAltitudeM, hasBoost, dragScale);
        return true;
    }

    /// <summary>t 时刻已飞行的距离（m）。</summary>
    public double DistanceAt(double t)
    {
        if (t <= 0)
        {
            return 0;
        }

        if (t >= LifeTimeS)
        {
            return _distances[^1];
        }

        var idx = (int)(t / SampleStepS);
        if (idx >= _times.Length - 1)
        {
            return _distances[^1];
        }

        var f = (t - _times[idx]) / (_times[idx + 1] - _times[idx]);
        return _distances[idx] + (_distances[idx + 1] - _distances[idx]) * f;
    }

    /// <summary>t 时刻的飞行速度（m/s）。</summary>
    public double SpeedAt(double t)
    {
        if (t <= 0)
        {
            return _speeds[0];
        }

        if (t >= LifeTimeS)
        {
            return _speeds[^1];
        }

        var idx = (int)(t / SampleStepS);
        if (idx >= _times.Length - 1)
        {
            return _speeds[^1];
        }

        var f = (t - _times[idx]) / (_times[idx + 1] - _times[idx]);
        return _speeds[idx] + (_speeds[idx + 1] - _speeds[idx]) * f;
    }

    /// <summary>
    /// 求最大拦截距离：目标以 <paramref name="recessionMs"/> 沿视线远离
    /// （迎头目标传入负值表示接近），且命中瞬间导弹速度不得低于
    /// <paramref name="minTerminalSpeedMs"/>。
    /// </summary>
    public double MaxRangeFor(double recessionMs, double minTerminalSpeedMs)
    {
        var recession = Math.Max(0, recessionMs);
        var end = InterceptCapableEnd();

        // 从后往前找第一个「速度达标」的时刻；该时刻距离最远，
        // 减去目标同期逃逸距离即为可拦截的最大初始距离。
        for (var t = end; t > 0; t -= SampleStepS)
        {
            if (SpeedAt(t) >= minTerminalSpeedMs)
            {
                return Math.Max(0, DistanceAt(t) - recession * t);
            }
        }

        return 0;
    }

    /// <summary>
    /// 命中时间：初始距离 <paramref name="distanceM"/>，目标以
    /// <paramref name="targetSpeedMs"/> 远离（迎头传入负值）。
    /// 无法拦截返回 null。
    /// </summary>
    public double? TimeToImpact(double distanceM, double targetSpeedMs, double minTerminalSpeedMs)
    {
        if (distanceM <= 0)
        {
            return 0;
        }

        var recession = Math.Max(0, targetSpeedMs);
        var end = InterceptCapableEnd();

        // 先确认可达：从后往前找「速度达标且距离足够」的时刻。
        double hi = -1;
        for (var t = end; t > 0; t -= SampleStepS)
        {
            if (SpeedAt(t) >= minTerminalSpeedMs && DistanceAt(t) - recession * t >= distanceM)
            {
                hi = t;
                break;
            }
        }

        if (hi < 0)
        {
            return null;
        }

        double lo = 0;
        for (var i = 0; i < 50; i++)
        {
            var mid = (lo + hi) / 2;
            if (DistanceAt(mid) - recession * mid < distanceM)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return (lo + hi) / 2;
    }

    /// <summary>命中瞬间的导弹速度（m/s）。</summary>
    public double InterceptSpeedMs(double t) => SpeedAt(Math.Min(t, LifeTimeS));

    /// <summary>
    /// 拦截仍有意义的截止时刻：速度跌破 <see cref="MinCruiseSpeedMs"/>
    /// 或寿命耗尽。
    /// </summary>
    private double InterceptCapableEnd()
    {
        for (var i = 0; i < _times.Length; i++)
        {
            if (_times[i] > 0 && _speeds[i] < MinCruiseSpeedMs)
            {
                return _times[i];
            }
        }

        return LifeTimeS;
    }

    // ================= 积分 =================

    private static double DragShape(double cxk, double caliber) =>
        (cxk / 1.5) * Math.Pow(Math.Max(40, caliber) / 150.0, 2.0);

    private static MissileFlightModel Integrate(
        MissileProfile profile,
        double mass, double burnoutMass,
        double life, double startSpeed, double altitudeM,
        bool hasBoost, double dragScale)
    {
        var thrust1 = profile.ThrustN ?? 0;
        var burn1 = profile.BurnTime ?? 0;
        var thrust2 = profile.Thrust2N ?? 0;
        var burn2 = profile.Burn2TimeS ?? 0;

        var density = Math.Exp(-Math.Max(0, altitudeM) / AirDensityScaleHeightM);
        var totalBurn = burn1 + burn2;
        var horizon = Math.Min(Math.Max(life, totalBurn + 5), 400);

        var times = new List<double> { 0 };
        var speeds = new List<double> { Math.Max(30, startSpeed) };
        var distances = new List<double> { 0 };

        var v = Math.Max(30, startSpeed);
        var x = 0.0;
        var t = 0.0;
        var dt = SampleStepS;

        while (t < horizon)
        {
            t += dt;

            double accel;
            double currentMass;

            if (hasBoost && t <= burn1)
            {
                var f = burn1 > 0 ? t / burn1 : 1;
                currentMass = mass + (burnoutMass - mass) * f;
                accel = thrust1 / Math.Max(1, currentMass);
            }
            else if (hasBoost && t <= totalBurn && thrust2 > 0)
            {
                accel = thrust2 / Math.Max(1, burnoutMass);
            }
            else
            {
                accel = 0;
            }

            accel -= dragScale * density * v * v;
            v = Math.Max(20, v + accel * dt);
            x += v * dt;

            times.Add(t);
            speeds.Add(v);
            distances.Add(x);

            if (v <= 20)
            {
                break;
            }
        }

        return new MissileFlightModel(
            [.. times], [.. speeds], [.. distances],
            Math.Min(times[^1], horizon), speeds.Max(), totalBurn, dragScale);
    }

    private static double EstimateMass(MissileProfile profile) =>
        profile.MassKg ?? (profile.CaliberMm is { } c ? Math.Pow(c / 1000.0, 3) * 4500 : 100.0);
}
