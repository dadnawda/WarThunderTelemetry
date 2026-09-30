using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using WarThunderTelemetry.Core.Weapons;

namespace WarThunderTelemetry.Core.Weapons;

/// <summary>
/// 内置导弹参数库。
/// <para>
/// 数据来源：社区数据挖掘表《WT导弹与设备性能表》（B站 @库撒的幽灵 @苍之古叶 整理，
/// 版本 2.44.0.23），由 <c>tools/extract-missiles.py</c> 从原始 Excel 提取为
/// <c>Weapons/missiles.json</c> 嵌入资源。表内为<b>游戏真实数值</b>
/// （质量 / 推力 / 燃烧时间 / 阻力系数 CXk / 滞空时间 / 最大飞行距离等），
/// 覆盖全部空空红外弹与空空雷达弹。
/// </para>
/// <para>
/// <b>数据纪律</b>：只收录核心飞行参数齐全的导弹（质量、推力、燃烧、
/// 极速、最大距离、滞空时间、增速至少占三项）；提取时无法对齐的列一律丢弃，
/// <b>绝不猜测</b>。字段缺失时计算引擎回退保守估算并标注「估算」。
/// </para>
/// </summary>
public static class MissileDatabase
{
    private const string ResourceId = "WarThunderTelemetry.Core.Weapons.missiles.json";

    private static readonly Lazy<MissileProfile[]> LazyAll = new(Load);

    private static MissileProfile[] All => LazyAll.Value;

    /// <summary>全部导弹档案。</summary>
    public static IReadOnlyList<MissileProfile> Profiles { get; } =
        new ReadOnlyCollection<MissileProfile>(LazyAll.Value);

    /// <summary>导弹总数。</summary>
    public static int Count => All.Length;

    /// <summary>
    /// 按名字模糊查找导弹。
    /// <para>
    /// 匹配策略：先精确 Id / 显示名，再包含匹配取<b>最长命中</b> ——
    /// 避免 <c>R-60M</c> 被 <c>R-60</c> 抢走。
    /// </para>
    /// </summary>
    public static MissileProfile? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var query = Normalize(name);
        if (query.Length < 2)
        {
            return null;
        }

        // 1) Id / 显示名 / 别名 精确命中。
        foreach (var profile in All)
        {
            if (Normalize(profile.Id) == query || Normalize(profile.DisplayName) == query)
            {
                return profile;
            }

            if (profile.AllNames.Contains(query))
            {
                return profile;
            }
        }

        // 2) 包含匹配：别名越长越优先（长名代表更具体的型号）。
        MissileProfile? best = null;
        var bestLength = 0;

        foreach (var profile in All)
        {
            foreach (var candidate in profile.AllNames)
            {
                if (candidate.Length < 2 || !query.Contains(candidate, StringComparison.Ordinal))
                {
                    continue;
                }

                if (candidate.Length > bestLength ||
                    (candidate.Length == bestLength && Score(profile) > Score(best)))
                {
                    best = profile;
                    bestLength = candidate.Length;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// 按游戏挂载名反查。挂载名常带前后缀（如 <c>R-60M_L</c>、<c>AIM-120C5</c>），
    /// 所以用「查询串包含别名」的方向匹配，且<b>长别名优先</b>。
    /// </summary>
    public static MissileProfile? FindByAlias(string? mountedName)
    {
        if (string.IsNullOrWhiteSpace(mountedName))
        {
            return null;
        }

        var query = Normalize(mountedName);
        if (query.Length < 2)
        {
            return null;
        }

        MissileProfile? best = null;
        var bestLength = 0;

        foreach (var profile in All)
        {
            foreach (var alias in profile.AllNames)
            {
                if (alias.Length < 3 || !query.Contains(alias, StringComparison.Ordinal))
                {
                    continue;
                }

                if (alias.Length > bestLength ||
                    (alias.Length == bestLength && Score(profile) > Score(best)))
                {
                    best = profile;
                    bestLength = alias.Length;
                }
            }
        }

        return best ?? Find(mountedName);
    }

    /// <summary>参数完整度打分，用于歧义时的择优。</summary>
    private static int Score(MissileProfile? profile)
    {
        if (profile is null)
        {
            return -1;
        }

        var score = 0;
        if (profile.ThrustN is not null) score += 2;
        if (profile.BurnTime is not null) score += 2;
        if (profile.MaxDistanceM is not null) score += 2;
        if (profile.DragCxk is not null) score++;
        if (profile.MaxSpeed is not null) score++;
        if (profile.MaxG is not null) score++;
        return score;
    }

    /// <summary>把名字规范化：小写、只留字母数字。</summary>
    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[raw.Length];
        var length = 0;

        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[length++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer[..length]);
    }

    // ================= JSON 装载 =================

    private static MissileProfile[] Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceId)
            ?? throw new InvalidOperationException($"内嵌资源 {ResourceId} 不存在。");

        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        if (!root.TryGetProperty("missiles", out var array))
        {
            return [];
        }

        var list = new List<MissileProfile>();

        foreach (var el in array.EnumerateArray())
        {
            var profile = MapProfile(el);
            if (profile is not null)
            {
                list.Add(profile);
            }
        }

        return [.. list.OrderBy(p => p.Kind).ThenBy(p => p.Id)];
    }

    private static MissileProfile? MapProfile(JsonElement el)
    {
        // 数值参数都嵌在 "params" 子对象里（提取脚本的结构），
        // 顶层只有 id / displayName / kind / guidance / sheet / aliases / texts。
        var Params = el.TryGetProperty("params", out var p) &&
                     p.ValueKind == JsonValueKind.Object
            ? p
            : default;

        string? S(string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

        double? N(string name) =>
            Params.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble()
                : null;

        string? TextParam(string name, string key) =>
            el.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Object &&
            t.TryGetProperty(key, out var kv) && kv.ValueKind == JsonValueKind.String
                ? kv.GetString()
                : null;

        var id = S("id");
        var displayName = S("displayName");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var kind = S("kind") is { } k &&
                   Enum.TryParse<MissileKind>(k, ignoreCase: true, out var parsedKind)
            ? parsedKind
            : MissileKind.AirToAir;

        var guidance = S("guidance") is { } g &&
                       Enum.TryParse<MissileGuidance>(g, ignoreCase: true, out var parsedGuidance)
            ? parsedGuidance
            : MissileGuidance.Unknown;

        // 名称变体（各参数分区的写法差异）都作为别名，供挂载名反查。
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { displayName };
        if (el.TryGetProperty("aliases", out var aliasArr) &&
            aliasArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in aliasArr.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { } s && s.Length > 1)
                {
                    names.Add(s);
                }
            }
        }

        // 数据表里的射程单位是米；导引头距离原本是千米，提取时已换算。
        var maxDistance = N("max_distance_m");
        var seekerAll = N("seeker_all_m");
        var seekerRear = N("seeker_rear_m");

        // 迎头 / 尾追导引头距离映射到既有 Seekers 字典。
        Dictionary<string, double>? seekers = null;
        if (seekerAll is not null || seekerRear is not null)
        {
            seekers = [];
            if (seekerRear is { } rear) seekers["tail-on"] = rear;
            if (seekerAll is { } all) seekers["head-on"] = all;
            if (seekerAll is { } side) seekers["side-on"] = side;
        }

        return new MissileProfile
        {
            Id = id,
            DisplayName = displayName,
            Kind = kind,
            Guidance = guidance,
            Source = "wt-datamine-2.44.0.23",

            // 旧字段（沿用既有语义，保证兼容）
            MaxRange = maxDistance,
            BurnTime = N("burn_time_s"),
            MaxSpeed = N("max_speed_ms"),
            MaxG = N("max_g"),
            Seekers = seekers,
            MinRange = null,

            // 数据挖掘实测字段
            MassKg = N("mass_kg"),
            BurnoutMassKg = N("burnout_mass_kg"),
            CaliberMm = N("caliber_mm"),
            LengthM = N("length_m"),
            ThrustN = N("thrust_n"),
            Thrust2N = N("thrust2_n"),
            Burn2TimeS = N("burn2_time_s"),
            StartSpeedMs = N("start_speed_ms"),
            DragCxk = N("drag_cxk"),
            LifeTimeS = N("life_time_s"),
            MaxDistanceM = maxDistance,
            BoostDv1Ms = N("boost_dv1_ms"),
            ManeuverDelayS = N("maneuver_delay_s"),
            WarmupS = N("warmup_s"),
            OffBoresightDeg = N("offboresight_deg"),
            SeekerAllAspectM = seekerAll,
            SeekerRearM = seekerRear,
            Loft = N("loft"),
            ExplosiveMassKg = N("explosive_mass_kg"),
            TntEquivalentKg = N("tnt_equivalent_kg"),
            GuidanceText = TextParam("texts", "制导类型"),
            AllNames = new HashSet<string>(names.Select(Normalize).Where(n => n.Length >= 2)),
        };
    }
}
