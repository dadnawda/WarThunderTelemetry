namespace WarThunderTelemetry.Core.Weapons;

/// <summary>
/// 导弹制导方式。决定能不能「发射后不管」、需不需要持续照射。
/// </summary>
public enum MissileGuidance
{
    /// <summary>未知，只按通用模型估算。</summary>
    Unknown = 0,

    /// <summary>红外（IR），追尾/侧后方锁定，射后不理。</summary>
    Ir = 1,

    /// <summary>半主动雷达（SARH），需母机持续照射目标。</summary>
    Sarh = 2,

    /// <summary>主动雷达（ARH），中段惯导 + 末段自开雷达，射后不理。</summary>
    Arh = 3,

    /// <summary>指令线导（MCLOS/SACLOS），需全程手动/半自动操纵。</summary>
    Command = 4,

    /// <summary>反辐射（ARM），追踪雷达波源。</summary>
    AntiRadiation = 5,
}

/// <summary>
/// 导弹类型，用于分类展示与默认假设。
/// </summary>
public enum MissileKind
{
    /// <summary>空空导弹。</summary>
    AirToAir = 0,

    /// <summary>空地导弹 / 反坦克导弹。</summary>
    AirToGround = 1,

    /// <summary>反舰导弹。</summary>
    AntiShip = 2,

    /// <summary>反辐射导弹。</summary>
    AntiRadiation = 3,
}

/// <summary>
/// 一枚导弹的公开性能参数。
/// <para>
/// 数据取自《战争雷霆》公开资料（官方 wiki 的导弹条目、游戏内挂载面板、
/// 以及社区整理的数据表）。数值均为 <b>公开近似值</b>，用于估算发射包线，
/// 不代表游戏内精确实现。
/// </para>
/// <para>
/// <b>缺数据的字段一律留 null，由计算引擎走保守假设并标注「估算」</b>
/// —— 编造一个看起来合理的数字，比承认不知道更危险。
/// </para>
/// </summary>
public sealed record MissileProfile
{
    /// <summary>稳定标识（英文小写下划线），用于代码引用。</summary>
    public required string Id { get; init; }

    /// <summary>显示名，如 <c>AIM-9L Sidewinder</c>。</summary>
    public required string DisplayName { get; init; }

    /// <summary>中文名／别名，用于从游戏挂载名反查。</summary>
    public string? DisplayNameZh { get; init; }

    /// <summary>导弹类型。</summary>
    public MissileKind Kind { get; init; } = MissileKind.AirToAir;

    /// <summary>制导方式。</summary>
    public MissileGuidance Guidance { get; init; } = MissileGuidance.Unknown;

    /// <summary>所属国家（仅用于展示与分组）。</summary>
    public string? Nation { get; init; }

    /// <summary>
    /// 最大射程（m）。迎头、高空、高速条件下的理论最大射程。
    /// 对应公开资料里的 "maximum launch range"。
    /// </summary>
    public double? MaxRange { get; init; }

    /// <summary>
    /// 动力射程（m）。发动机工作期间能飞出的距离；
    /// 超过此距离后导弹只能靠惯性滑翔，能量迅速衰减。
    /// </summary>
    public double? PoweredRange { get; init; }

    /// <summary>
    /// 不可逃逸区（m）。目标即便做出最大规避机动，导弹仍能命中的距离上限。
    /// <c>null</c> 时按 <see cref="MaxRange"/> 的 35% 保守估算。
    /// </summary>
    public double? NoEscapeRange { get; init; }

    /// <summary>发动机工作时间（s）。用于判断末段是否还有推力。</summary>
    public double? BurnTime { get; init; }

    /// <summary>最大飞行速度（m/s，通常为马赫数的换算值）。</summary>
    public double? MaxSpeed { get; init; }

    /// <summary>
    /// 可用过载（g）。决定导弹的转向能力，也是「能不能跟住目标规避」的关键。
    /// </summary>
    public double? MaxG { get; init; }

    /// <summary>
    /// 导引头锁定距离（m）。迎头 / 尾追差异很大的导弹，
    /// 用 <see cref="Seekers"/> 精细描述；此字段是通用值。
    /// </summary>
    public double? SeekerRange { get; init; }

    /// <summary>
    /// 导引头分场景锁定距离。键为 <c>head-on</c> / <c>tail-on</c> / <c>side-on</c>。
    /// </summary>
    public IReadOnlyDictionary<string, double>? Seekers { get; init; }

    /// <summary>
    /// 发射时载机允许的最大过载（g）。超过这个值发射，
    /// 导弹可能脱离挂架失败或直接丢失目标。
    /// </summary>
    public double? LaunchGLimit { get; init; }

    /// <summary>
    /// 发射时的高度上限（m）。超过后导弹性能急剧下降。
    /// </summary>
    public double? MaxLaunchAltitude { get; init; }

    /// <summary>最小发射距离（m）。低于此距离导弹来不及解锁引信/完成转向。</summary>
    public double? MinRange { get; init; }

    /// <summary>
    /// 是否需要母机持续照射直到命中（SARH、指令线导为 true）。
    /// </summary>
    public bool RequiresIllumination => Guidance is MissileGuidance.Sarh or MissileGuidance.Command;

    /// <summary>是否射后不理。</summary>
    public bool IsFireAndForget => Guidance is MissileGuidance.Ir or MissileGuidance.Arh;

    /// <summary>数据来源标注（如 <c>wt-wiki</c> / <c>community</c>）。</summary>
    public string? Source { get; init; }

    /// <summary>
    /// 按交战几何取导引头锁定距离。
    /// </summary>
    /// <param name="aspect">交战态势：<c>head-on</c> / <c>tail-on</c> / <c>side-on</c>。</param>
    /// <param name="fallback">整体回退值（通常取 <see cref="SeekerRange"/>）。</param>
    public double ResolveSeekerRange(string aspect, double fallback)
    {
        if (Seekers is { Count: > 0 } seekers &&
            seekers.TryGetValue(aspect, out var value) &&
            value > 0)
        {
            return value;
        }

        return SeekerRange is { } r && r > 0 ? r : fallback;
    }
}
