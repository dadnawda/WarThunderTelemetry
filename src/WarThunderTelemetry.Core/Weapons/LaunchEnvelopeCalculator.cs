using WarThunderTelemetry.Core.Weapons;

namespace WarThunderTelemetry.Core.Weapons;

/// <summary>
/// 交战态势：目标相对本机的航向关系。决定导引头能不能锁上、导弹有没有能量追上。
/// </summary>
public enum EngagementAspect
{
    /// <summary>无法判断。</summary>
    Unknown = 0,

    /// <summary>迎头 —— 双方对飞。雷达弹最有利，红外弹最不利。</summary>
    HeadOn = 1,

    /// <summary>尾追 —— 目标背向逃离。红外弹最有利。</summary>
    TailOn = 2,

    /// <summary>侧向 —— 正交或斜交。</summary>
    SideOn = 3,
}

/// <summary>
/// 发射建议等级。这是给玩家的最终结论。
/// </summary>
public enum LaunchVerdict
{
    /// <summary>数据不足，无法给出建议。</summary>
    Unknown = 0,

    /// <summary>可以发射 —— 在不可逃逸区内，命中把握高。</summary>
    Fire = 1,

    /// <summary>可以尝试 —— 在有效射程内但出了不可逃逸区，目标规避可能脱靶。</summary>
    Marginal = 2,

    /// <summary>距离过远 —— 目标有充足时间规避，别浪费导弹。</summary>
    TooFar = 3,

    /// <summary>距离过近 —— 导弹来不及解锁/起控。</summary>
    TooClose = 4,

    /// <summary>发射条件不允许 —— 过载超限、速度不足或需要照射但雷达没锁。</summary>
    Inhibit = 5,
}

/// <summary>
/// 计算输入：本机与目标的当前状态。
/// <para>
/// 单位统一：速度 km/h，高度 m，距离 m，角度为度。
/// </para>
/// </summary>
public sealed record LaunchInput
{
    /// <summary>本机指示空速（km/h）。</summary>
    public double? OwnSpeedKmh { get; init; }

    /// <summary>本机高度（m）。</summary>
    public double? OwnAltitudeM { get; init; }

    /// <summary>本机当前过载（g）。用于核对发射过载限制。</summary>
    public double? OwnLoadG { get; init; }

    /// <summary>目标速度（km/h）。未知时按交战态势取默认值。</summary>
    public double? TargetSpeedKmh { get; init; }

    /// <summary>目标高度（m）。</summary>
    public double? TargetAltitudeM { get; init; }

    /// <summary>目标距离（m）。未知则无法给出射程建议。</summary>
    public double? TargetDistanceM { get; init; }

    /// <summary>交战态势。</summary>
    public EngagementAspect Aspect { get; init; } = EngagementAspect.Unknown;

    /// <summary>目标是否在做规避机动（游戏中表现为持续大过载盘旋）。</summary>
    public bool TargetManeuvering { get; init; }
}

/// <summary>
/// 计算输出：完整的发射决策依据。
/// </summary>
public sealed record LaunchSolution
{
    /// <summary>导弹显示名。</summary>
    public string MissileName { get; init; } = string.Empty;

    /// <summary>最终建议。</summary>
    public LaunchVerdict Verdict { get; init; } = LaunchVerdict.Unknown;

    /// <summary>建议的一句话说明。</summary>
    public string Advice { get; init; } = string.Empty;

    /// <summary>最小可用发射距离（m）。</summary>
    public double MinRange { get; init; }

    /// <summary>不可逃逸区上限（m）。这个距离内发射，目标躲不掉。</summary>
    public double NoEscapeRange { get; init; }

    /// <summary>有效射程上限（m）。动力射程与导引头锁定距离里较小的那个。</summary>
    public double EffectiveRange { get; init; }

    /// <summary>理论最大射程（m）。仅供参照，实战很难达到。</summary>
    public double MaxRange { get; init; }

    /// <summary>预期命中时间（s）。距离/平均速度的粗估。</summary>
    public double? TimeToImpact { get; init; }

    /// <summary>命中时导弹剩余速度（m/s）。负数表示已失速，追不上。</summary>
    public double? TerminalSpeed { get; init; }

    /// <summary>是否在不可逃逸区内。</summary>
    public bool InNoEscapeZone { get; init; }

    /// <summary>导引头能否锁定（距离 + 态势共同决定）。</summary>
    public bool SeekerLocked { get; init; }

    /// <summary>发射过载是否超限。</summary>
    public bool LoadWithinLimit { get; init; } = true;

    /// <summary>计算中使用了估算值（数据不全），提示玩家谨慎参考。</summary>
    public bool HasEstimates { get; init; }

    /// <summary>本机与目标的高度差（m）。正数表示本机更高。</summary>
    public double? AltitudeAdvantage { get; init; }
}

/// <summary>
/// 导弹发射包线计算引擎。
/// <para>
/// <b>模型说明</b>：采用公开资料整理的「二段式推力 + 大气阻力」简化弹道估算，
/// 而非游戏内部精确弹道。计算流程：
/// <list type="number">
/// <item>把导弹的射程参数按当前高度、本机速度、目标速度做能量修正
/// —— 高空空气稀薄、阻力小，射程显著增加；低空大阻力则缩水。</item>
/// <item>用发射过载限制、速度门槛、导引头锁定距离逐项做「能不能射」的否决判断。</item>
/// <item>算出不可逃逸区、有效射程、命中时间，给出最终建议。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么是估算而不是精确复现</b>：游戏内弹道是服务器权威的，
/// 任何外部工具都拿不到精确的推重比与阻力系数曲线。
/// 本引擎的目标是给出「相对可靠的相对判断」——
/// 什么时候能射、什么时候别浪费导弹，而不是复现小数点后两位。
/// 因此结果里始终带 <see cref="LaunchSolution.HasEstimates"/> 标记。
/// </para>
/// </summary>
public static class LaunchEnvelopeCalculator
{
    /// <summary>海平面标准空气密度下的射程修正基准高度（m）。</summary>
    private const double ReferenceAltitude = 3_000;

    /// <summary>高度每上升这么多米，射程修正系数增加一档（空气稀薄效应）。</summary>
    private const double AltitudeGainPerMeter = 0.000_022;

    /// <summary>射程修正系数的上下限。避免高空的夸张外推。</summary>
    private const double MinRangeFactor = 0.55;
    private const double MaxRangeFactor = 1.75;

    /// <summary>重力加速度。</summary>
    private const double G = 9.806_65;

    /// <summary>
    /// 求解一次发射。
    /// </summary>
    /// <param name="profile">导弹参数。</param>
    /// <param name="input">本机与目标状态。</param>
    /// <returns>发射解算结果；参数不足以判断时返回 <see cref="LaunchVerdict.Unknown"/>。</returns>
    public static LaunchSolution Solve(MissileProfile profile, LaunchInput input)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(input);

        var estimates = false;

        // ---------- 1. 补齐缺失参数（保守估算并标记） ----------
        var nominalMax = profile.MaxRange ?? EstimateMaxRange(profile, ref estimates);
        var burnTime = profile.BurnTime ?? EstimateBurnTime(profile, ref estimates);
        var maxG = profile.MaxG ?? 15.0;
        if (profile.MaxG is null)
        {
            estimates = true;
        }

        // ---------- 2. 高度与速度带来的能量修正 ----------
        var ownAlt = input.OwnAltitudeM ?? 1_000;
        var altFactor = ComputeAltitudeFactor(ownAlt);

        // 本机速度对射程有直接影响：发射时越快，导弹初始能量越高。
        // 以 900 km/h 为基准，速度每差 100 km/h 调整约 4%。
        var ownSpeed = input.OwnSpeedKmh ?? 800;
        var speedFactor = Math.Clamp(1.0 + ((ownSpeed - 900) / 100.0 * 0.04), 0.70, 1.40);

        // 目标速度：迎头时目标迎面而来，实际需要的导弹飞行距离短，
        // 等效于射程变长；尾追时目标在跑，等效射程缩短。
        var targetSpeed = input.TargetSpeedKmh ?? DefaultTargetSpeed(input.Aspect);
        var (closureFactor, closureRate) = ComputeClosureFactor(ownSpeed, targetSpeed, input.Aspect);

        var rangeFactor = altFactor * speedFactor;

        var maxRange = nominalMax * rangeFactor;
        var noEscape = (profile.NoEscapeRange ?? nominalMax * 0.35) * rangeFactor * closureFactor;
        var minRange = ResolveMinRange(profile, maxG);

        // ---------- 3. 导引头锁定距离 ----------
        var seekerAspectKey = AspectKey(input.Aspect);
        var seekerNominal = profile.ResolveSeekerRange(seekerAspectKey, maxRange);
        // 导引头同样受高度影响，但比弹体射程敏感度低。
        var seekerRange = seekerNominal * Math.Clamp(altFactor, 0.75, 1.35);

        // 有效射程 = 动力射程、导引头锁定距离、最大射程三者取小。
        // 动力射程缺失时用最大射程的 55% 估算。
        var powered = profile.PoweredRange ?? nominalMax * 0.55;
        var effectiveRange = Math.Min(maxRange, Math.Min(powered * rangeFactor * closureFactor, seekerRange));

        // ---------- 4. 是否允许发射 ----------
        var loadLimit = profile.LaunchGLimit;
        var loadOk = loadLimit is null || input.OwnLoadG is null ||
                     input.OwnLoadG.Value <= loadLimit.Value + 0.001;

        // 导弹有最低载机速度要求：太慢时舵面效率不足，发射即失控。
        var minLaunchSpeed = 250.0;
        var speedOk = ownSpeed >= minLaunchSpeed;

        var distance = input.TargetDistanceM;
        var seekerLocked = distance is null || distance.Value <= seekerRange;

        // ---------- 5. 命中时间与末段速度 ----------
        double? timeToImpact = null;
        double? terminalSpeed = null;

        if (distance is { } d && d > 0)
        {
            var avgSpeed = EstimateAverageSpeed(profile, burnTime, d, closureRate);
            if (avgSpeed > 0)
            {
                timeToImpact = d / avgSpeed;
                terminalSpeed = EstimateTerminalSpeed(profile, burnTime, d, timeToImpact.Value, closureRate);
            }
        }

        // ---------- 6. 结论 ----------
        var verdict = Decide(
            distance, minRange, noEscape, effectiveRange,
            loadOk, speedOk, seekerLocked, terminalSpeed);

        var advice = BuildAdvice(
            verdict, profile, distance, minRange, noEscape, effectiveRange,
            loadOk, loadLimit, input.OwnLoadG, speedOk, seekerLocked, input.Aspect);

        return new LaunchSolution
        {
            MissileName = profile.DisplayNameZh ?? profile.DisplayName,
            Verdict = verdict,
            Advice = advice,
            MinRange = minRange,
            NoEscapeRange = noEscape,
            EffectiveRange = effectiveRange,
            MaxRange = maxRange,
            TimeToImpact = timeToImpact,
            TerminalSpeed = terminalSpeed,
            InNoEscapeZone = distance is { } v && v <= noEscape && v >= minRange,
            SeekerLocked = seekerLocked,
            LoadWithinLimit = loadOk,
            HasEstimates = estimates,
            AltitudeAdvantage = input.TargetAltitudeM is { } ta ? ownAlt - ta : null,
        };
    }

    // ================= 内部计算 =================

    /// <summary>高度修正系数：越高空气越稀薄，导弹飞得越远。</summary>
    private static double ComputeAltitudeFactor(double altitudeM)
    {
        // 以 3000 m 为基准（系数 1.0）。
        var factor = 1.0 + ((altitudeM - ReferenceAltitude) * AltitudeGainPerMeter);

        // 低空额外惩罚：贴地飞行时阻力大、发射条件差。
        if (altitudeM < 500)
        {
            factor *= 0.88;
        }

        return Math.Clamp(factor, MinRangeFactor, MaxRangeFactor);
    }

    /// <summary>
    /// 交战态势带来的等效射程修正。
    /// <para>
    /// 迎头时目标迎面接近，导弹只需飞完「距离」的一小部分就能交汇，等效射程放大；
    /// 尾追时目标同向逃离，导弹必须追完全程还要补上速度差，等效射程收缩。
    /// </para>
    /// </summary>
    /// <returns>射程修正系数与接近率（m/s，正数表示距离在缩短）。</returns>
    private static (double Factor, double ClosureRate) ComputeClosureFactor(
        double ownSpeedKmh, double targetSpeedKmh, EngagementAspect aspect)
    {
        var own = ownSpeedKmh / 3.6;
        var target = targetSpeedKmh / 3.6;

        return aspect switch
        {
            // 迎头：接近率 = 两机速度之和。
            EngagementAspect.HeadOn => (1.45, own + target),

            // 尾追：接近率 = 速度差（导弹要追上目标）。
            EngagementAspect.TailOn => (0.65, Math.Max(5, own - target)),

            // 侧向：只有部分分量参与接近。
            EngagementAspect.SideOn => (1.00, Math.Max(20, (own + target) * 0.45)),

            _ => (1.00, Math.Max(20, (own + target) * 0.5)),
        };
    }

    /// <summary>未知目标速度时按态势给一个合理默认值。</summary>
    private static double DefaultTargetSpeed(EngagementAspect aspect) => aspect switch
    {
        EngagementAspect.HeadOn => 900,
        EngagementAspect.TailOn => 800,
        EngagementAspect.SideOn => 750,
        _ => 800,
    };

    /// <summary>把态势映射成导引头参数表的键。</summary>
    private static string AspectKey(EngagementAspect aspect) => aspect switch
    {
        EngagementAspect.HeadOn => "head-on",
        EngagementAspect.TailOn => "tail-on",
        EngagementAspect.SideOn => "side-on",
        _ => "tail-on",
    };

    /// <summary>最小发射距离：导弹需要时间完成解锁与转向。</summary>
    private static double ResolveMinRange(MissileProfile profile, double maxG)
    {
        if (profile.MinRange is { } explicitMin && explicitMin > 0)
        {
            return explicitMin;
        }

        // 按导引头类型给缺省：红外弹可以很近发射，雷达弹需要制导建立时间。
        double baseMin = profile.Guidance switch
        {
            MissileGuidance.Ir => 600,
            MissileGuidance.Arh => 1_500,
            MissileGuidance.Sarh => 2_000,
            MissileGuidance.Command => 400,
            _ => 1_000,
        };

        // 转向能力越差，需要的解锁距离越长。
        if (maxG < 12)
        {
            baseMin *= 1.6;
        }

        return baseMin;
    }

    /// <summary>估算最大射程（参数缺失时）。</summary>
    private static double EstimateMaxRange(MissileProfile profile, ref bool estimates)
    {
        estimates = true;

        // 用燃烧时间与最大速度粗估：动力段飞出的距离 + 滑翔段。
        var speed = profile.MaxSpeed ?? 700;
        var burn = profile.BurnTime ?? 3.0;

        // 动力段平均速度约为最大速度的 70%。
        var powered = speed * 0.7 * burn;

        // 滑翔段约为动力段的 60%。
        return powered * 1.6;
    }

    /// <summary>估算燃烧时间（参数缺失时）。</summary>
    private static double EstimateBurnTime(MissileProfile profile, ref bool estimates)
    {
        estimates = true;

        return profile.MaxRange switch
        {
            > 50_000 => 8.0,
            > 25_000 => 5.0,
            > 10_000 => 3.5,
            _ => 2.5,
        };
    }

    /// <summary>
    /// 估算导弹飞完全程的平均速度。
    /// <para>
    /// 动力段按最大速度的 70%，滑翔段按快速衰减处理。
    /// </para>
    /// </summary>
    private static double EstimateAverageSpeed(
        MissileProfile profile, double burnTime, double distance, double closureRate)
    {
        var maxSpeed = profile.MaxSpeed ?? 700;

        // 导弹实际要飞的距离：迎头时因目标接近而缩短。
        var flightDistance = distance;
        if (closureRate > 0)
        {
            // 目标在导弹飞行期间也在移动，粗略按接近率折减。
            flightDistance = Math.Max(distance * 0.6, distance - closureRate * 3);
        }

        var poweredDistance = maxSpeed * 0.7 * burnTime;

        if (flightDistance <= poweredDistance)
        {
            // 全程在动力段内。
            return maxSpeed * 0.7;
        }

        // 超出动力段，滑翔段平均速度按 45% 计。
        var glideDistance = flightDistance - poweredDistance;
        var poweredTime = burnTime;
        var glideSpeed = Math.Max(120, maxSpeed * 0.45);
        var glideTime = glideDistance / glideSpeed;
        var totalTime = poweredTime + glideTime;

        return totalTime > 0 ? flightDistance / totalTime : maxSpeed * 0.7;
    }

    /// <summary>估算命中瞬间的导弹剩余速度（相对目标的追击余速）。</summary>
    private static double EstimateTerminalSpeed(
        MissileProfile profile, double burnTime, double distance, double timeToImpact, double closureRate)
    {
        var maxSpeed = profile.MaxSpeed ?? 700;

        if (timeToImpact <= burnTime)
        {
            // 还在动力段：速度持续增加，取当前时刻的近似值。
            var progress = timeToImpact / Math.Max(0.1, burnTime);
            return maxSpeed * (0.4 + 0.6 * progress);
        }

        // 已过动力段：按指数衰减估算，衰减常数取燃烧时间量级。
        var glideTime = timeToImpact - burnTime;
        var decay = Math.Exp(-glideTime / Math.Max(2.0, burnTime * 2.5));
        return maxSpeed * 0.95 * decay;
    }

    /// <summary>汇总所有否决条件，给出最终建议。</summary>
    private static LaunchVerdict Decide(
        double? distance,
        double minRange,
        double noEscape,
        double effectiveRange,
        bool loadOk,
        bool speedOk,
        bool seekerLocked,
        double? terminalSpeed)
    {
        // 硬性否决优先：这些条件下发射基本是浪费导弹。
        if (!loadOk || !speedOk)
        {
            return LaunchVerdict.Inhibit;
        }

        if (distance is not { } d)
        {
            // 没有距离数据，只能给出「能不能射」的判断。
            return seekerLocked ? LaunchVerdict.Marginal : LaunchVerdict.Inhibit;
        }

        if (d < minRange)
        {
            return LaunchVerdict.TooClose;
        }

        if (!seekerLocked)
        {
            return LaunchVerdict.TooFar;
        }

        // 末速过低说明追不上，即便距离在射程内也没意义。
        if (terminalSpeed is { } ts && ts < 150)
        {
            return LaunchVerdict.TooFar;
        }

        if (d <= noEscape)
        {
            return LaunchVerdict.Fire;
        }

        if (d <= effectiveRange)
        {
            return LaunchVerdict.Marginal;
        }

        return LaunchVerdict.TooFar;
    }

    /// <summary>生成中文建议文本。</summary>
    private static string BuildAdvice(
        LaunchVerdict verdict,
        MissileProfile profile,
        double? distance,
        double minRange,
        double noEscape,
        double effectiveRange,
        bool loadOk,
        double? loadLimit,
        double? ownLoad,
        bool speedOk,
        bool seekerLocked,
        EngagementAspect aspect)
    {
        var km = (double? v) => v is null ? "—" : $"{v.Value / 1000.0:F1} km";

        return verdict switch
        {
            LaunchVerdict.Fire =>
                $"可发射。距离 {km(distance)}，在不可逃逸区（≤{km(noEscape)}）内，命中把握高。",

            LaunchVerdict.Marginal =>
                $"可尝试。距离 {km(distance)}，超出不可逃逸区（{km(noEscape)}）但在有效射程（{km(effectiveRange)}）内，" +
                "目标若及时规避可能脱靶；建议再逼近或等目标能量下降。",

            LaunchVerdict.TooFar when !seekerLocked =>
                $"导引头未锁定。该态势下锁定距离约 {km(profile.ResolveSeekerRange(AspectKey(aspect), effectiveRange))}，" +
                $"当前 {km(distance)} 过远。{(aspect == EngagementAspect.TailOn ? "红外弹尾追锁定距离最短，可尝试绕到目标后半球或拉近距离。" : "继续接近再射。")}",

            LaunchVerdict.TooFar =>
                $"距离过远。距离 {km(distance)}，有效射程约 {km(effectiveRange)}，" +
                "目标有充足时间规避，不建议浪费导弹。",

            LaunchVerdict.TooClose =>
                $"距离过近。距离 {km(distance)}，最小发射距离约 {km(minRange)}，导弹来不及解锁与起控。" +
                "等拉开距离再射。",

            LaunchVerdict.Inhibit when !loadOk =>
                $"发射过载超限。当前 {ownLoad:G} g，该弹允许 {loadLimit:G} g。" +
                "先把杆松一点、待过载回落后再发射。",

            LaunchVerdict.Inhibit when !speedOk =>
                "载机速度过低，导弹舵面效率不足。加速后再发射。",

            _ => "数据不足，无法给出建议。",
        };
    }
}
