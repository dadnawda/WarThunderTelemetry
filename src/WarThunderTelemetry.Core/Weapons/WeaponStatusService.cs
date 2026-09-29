using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.Core.Weapons;

/// <summary>
/// 武器参数服务：把实时遥测接到<a href="LaunchEnvelopeCalculator">包线计算引擎</a>上。
/// <para>
/// 职责有三：
/// <list type="number">
/// <item>从遥测快照里认出「当前选中的是哪种武器」；</item>
/// <item>从快照里取出本机状态（速度、高度、过载）；</item>
/// <item>调用计算引擎得到发射建议。</item>
/// </list>
/// </para>
/// <para>
/// <b>关于目标数据</b>：8111 接口不提供雷达锁定目标的距离、速度、航向 ——
/// 那属于服务器权威的战斗数据，不会暴露给外部工具。
/// 因此本服务返回 <see cref="WeaponStatus.TargetKnown"/> 为 <c>false</c> 的解算，
/// 只给出「本弹在当前高度速度下的射程区间」与「能不能射」的判断；
/// 目标相关字段需要玩家在界面上手动输入或由其他面板补全。
/// 这比编造一个假的「目标距离 5.2 km」诚实得多。
/// </para>
/// </summary>
public sealed class WeaponStatusService
{
    private readonly AliasResolver _resolver;

    /// <summary>构造。</summary>
    /// <param name="resolver">字段解析器，用于从快照读取标准字段。</param>
    public WeaponStatusService(AliasResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>上一次成功识别的导弹（接口短暂丢字段时不至于清空显示）。</summary>
    public MissileProfile? LastKnownMissile { get; private set; }

    /// <summary>
    /// 从快照构建武器状态。
    /// </summary>
    /// <param name="snapshot">遥测快照。</param>
    /// <param name="overrideMissile">界面手动指定的导弹（优先于自动识别）。</param>
    /// <param name="manualTarget">界面手动输入的目标信息。</param>
    public WeaponStatus Build(
        TelemetrySnapshot? snapshot,
        MissileProfile? overrideMissile = null,
        TargetInput? manualTarget = null)
    {
        if (snapshot is null || !snapshot.IsConnected)
        {
            return WeaponStatus.Disconnected;
        }

        var missile = overrideMissile ?? ResolveMissile(snapshot);
        if (missile is not null)
        {
            LastKnownMissile = missile;
        }
        else
        {
            missile = LastKnownMissile;
        }

        // 本机状态：直接复用已有字段目录，不另建一套解析。
        var ownSpeed = ReadNumber(snapshot, "ias");
        var ownAltitude = ReadNumber(snapshot, "altitude");
        var ownLoad = ReadNumber(snapshot, "g_load");
        var verticalSpeed = ReadNumber(snapshot, "vertical_speed");

        // 弹药余量：用于判断这门武器还有没有弹。
        var ammo = ReadNumber(snapshot, "ammo");

        var weaponName = ReadText(snapshot, "weapon_name") ?? ReadRawWeaponName(snapshot);

        if (missile is null)
        {
            return new WeaponStatus
            {
                IsConnected = true,
                Missile = null,
                WeaponName = weaponName,
                Ammo = ammo,
                OwnSpeedKmh = ownSpeed,
                OwnAltitudeM = ownAltitude,
                OwnLoadG = ownLoad,
                VerticalSpeedMs = verticalSpeed,
                TargetKnown = false,
                Message = string.IsNullOrWhiteSpace(weaponName)
                    ? "当前未挂载可识别的导弹。"
                    : $"当前武器「{weaponName}」未收录参数，无法解算发射包线。",
            };
        }

        // 目标信息：接口不提供，只能用玩家手动输入的。
        var target = manualTarget ?? TargetInput.Unknown;

        var input = new LaunchInput
        {
            OwnSpeedKmh = ownSpeed,
            OwnAltitudeM = ownAltitude,
            OwnLoadG = ownLoad,
            TargetSpeedKmh = target.SpeedKmh,
            TargetAltitudeM = target.AltitudeM,
            TargetDistanceM = target.DistanceM,
            Aspect = target.Aspect,
            TargetManeuvering = target.Maneuvering,
        };

        var solution = LaunchEnvelopeCalculator.Solve(missile, input);

        return new WeaponStatus
        {
            IsConnected = true,
            Missile = missile,
            WeaponName = weaponName ?? missile.DisplayNameZh ?? missile.DisplayName,
            Ammo = ammo,
            OwnSpeedKmh = ownSpeed,
            OwnAltitudeM = ownAltitude,
            OwnLoadG = ownLoad,
            VerticalSpeedMs = verticalSpeed,
            Solution = solution,
            TargetKnown = target.IsKnown,
            Aspect = target.Aspect,
            Message = solution.Advice,
        };
    }

    /// <summary>
    /// 从快照识别当前导弹。
    /// <para>
    /// 优先读标准字段 <c>weapon_name</c>；读不到则退到 <c>/state</c> 里的
    /// 原始武器字段（<c>weapon2</c> / <c>weapon3</c> 等编号字段，
    /// 游戏用它们表示当前选中的挂载类型）。
    /// </para>
    /// </summary>
    private MissileProfile? ResolveMissile(TelemetrySnapshot snapshot)
    {
        var name = ReadText(snapshot, "weapon_name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            var hit = MissileDatabase.FindByAlias(name);
            if (hit is not null)
            {
                return hit;
            }
        }

        // 退路：扫描原始字段里任何看起来像武器名的值。
        return ScanRawWeaponFields(snapshot);
    }

    /// <summary>扫描原始键值对，找形似导弹名称的字段值。</summary>
    private static MissileProfile? ScanRawWeaponFields(TelemetrySnapshot snapshot)
    {
        foreach (var source in new[] { snapshot.State, snapshot.Indicators })
        {
            if (source.IsEmpty)
            {
                continue;
            }

            foreach (var (key, value) in source.RawValues)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                // 只处理键名与武器相关的字段，避免把机型名误当导弹。
                var keyLooksLikeWeapon =
                    key.Contains("weapon", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("missile", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("ammo", StringComparison.OrdinalIgnoreCase);

                if (!keyLooksLikeWeapon)
                {
                    continue;
                }

                // 纯数字是数量，不是名字。
                if (double.TryParse(value, out _))
                {
                    continue;
                }

                var hit = MissileDatabase.FindByAlias(value);
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        return null;
    }

    /// <summary>按字段 Id 读数字。</summary>
    private double? ReadNumber(TelemetrySnapshot snapshot, string fieldId)
    {
        var descriptor = _resolver.GetById(fieldId);
        return descriptor is null ? null : snapshot.Resolve(descriptor, _resolver).Number;
    }

    /// <summary>按字段 Id 读文本。</summary>
    private string? ReadText(TelemetrySnapshot snapshot, string fieldId)
    {
        var descriptor = _resolver.GetById(fieldId);
        return descriptor is null ? null : snapshot.Resolve(descriptor, _resolver).Text;
    }

    /// <summary>直接从原始字段里找武器名（字段目录里没有 weapon_name 时的退路）。</summary>
    private static string? ReadRawWeaponName(TelemetrySnapshot snapshot)
    {
        foreach (var source in new[] { snapshot.State, snapshot.Indicators })
        {
            foreach (var alias in new[] { "weapon_name", "weaponName", "weapon", "current_weapon" })
            {
                if (source.TryGet(alias, out var value) &&
                    !string.IsNullOrWhiteSpace(value) &&
                    !double.TryParse(value, out _))
                {
                    return value;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// 玩家手动输入的目标信息。
/// <para>
/// 8111 接口不提供目标数据，因此这部分只能由玩家给出（或后续接其他面板）。
/// 全部字段可空：给得越全，建议越准。
/// </para>
/// </summary>
public sealed record TargetInput
{
    /// <summary>全未知的目标信息。</summary>
    public static TargetInput Unknown { get; } = new();

    /// <summary>目标距离（m）。</summary>
    public double? DistanceM { get; init; }

    /// <summary>目标速度（km/h）。</summary>
    public double? SpeedKmh { get; init; }

    /// <summary>目标高度（m）。</summary>
    public double? AltitudeM { get; init; }

    /// <summary>交战态势。</summary>
    public EngagementAspect Aspect { get; init; } = EngagementAspect.Unknown;

    /// <summary>目标是否在做规避机动。</summary>
    public bool Maneuvering { get; init; }

    /// <summary>是否提供过至少一项有效信息。</summary>
    public bool IsKnown =>
        DistanceM is not null || SpeedKmh is not null || AltitudeM is not null ||
        Aspect != EngagementAspect.Unknown;
}

/// <summary>
/// 武器面板的一次完整状态。
/// </summary>
public sealed record WeaponStatus
{
    /// <summary>未连接时的空状态。</summary>
    public static WeaponStatus Disconnected { get; } = new()
    {
        IsConnected = false,
        Message = "未连接游戏。请在游戏内开启「War Thunder 接口」。",
    };

    /// <summary>是否已连上游戏。</summary>
    public bool IsConnected { get; init; }

    /// <summary>识别出的导弹；未识别为 <c>null</c>。</summary>
    public MissileProfile? Missile { get; init; }

    /// <summary>游戏里显示的武器名（可能比库里的名字更原始）。</summary>
    public string? WeaponName { get; init; }

    /// <summary>剩余弹药数。</summary>
    public double? Ammo { get; init; }

    /// <summary>本机指示空速（km/h）。</summary>
    public double? OwnSpeedKmh { get; init; }

    /// <summary>本机高度（m）。</summary>
    public double? OwnAltitudeM { get; init; }

    /// <summary>本机当前过载（g）。</summary>
    public double? OwnLoadG { get; init; }

    /// <summary>本机垂直速度（m/s）。</summary>
    public double? VerticalSpeedMs { get; init; }

    /// <summary>发射解算结果；未识别导弹时为 <c>null</c>。</summary>
    public LaunchSolution? Solution { get; init; }

    /// <summary>是否已知目标信息（决定建议的准确度）。</summary>
    public bool TargetKnown { get; init; }

    /// <summary>当前使用的交战态势。</summary>
    public EngagementAspect Aspect { get; init; }

    /// <summary>给玩家的一句话状态／建议。</summary>
    public string Message { get; init; } = string.Empty;
}
