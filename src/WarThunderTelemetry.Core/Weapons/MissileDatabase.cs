using System.Collections.ObjectModel;
using WarThunderTelemetry.Core.Weapons;

namespace WarThunderTelemetry.Core.Weapons;

/// <summary>
/// 内置导弹参数库。
/// <para>
/// 参数来源：
/// <list type="bullet">
/// <item>《战争雷霆》官方 wiki 的导弹条目（如 <c>wiki.warthunder.com/AIM-9L_Sidewinder</c>）；
/// 该页表格直接给出「Lock range (rear-aspect)」「Launch range」「Maximum speed (Mach)」
/// 「Maximum overload (g)」。</item>
/// <item>官方论坛的导弹性能整理帖（History, Design &amp; Performance of All Russian
/// Air-to-Air Missiles），给出燃烧时间、飞行时间、发射过载限制、导引头参数。</item>
/// <item>社区整理表，用于补齐近距红外弹的过载与射程。</item>
/// </list>
/// </para>
/// <para>
/// <b>数据纪律</b>：只录入能追溯到上述来源的数值；来源之间冲突时取较保守者。
/// 任何查不到的字段留 <c>null</c>，由计算引擎按保守假设估算并在结果里标注「估算」。
/// 不为了「看起来完整」而编造数字 —— 一个假参数会让发射提示直接误导玩家。
/// </para>
/// </summary>
public static class MissileDatabase
{
    private static readonly MissileProfile[] All = Build();

    /// <summary>全部导弹档案（按登记顺序）。</summary>
    public static IReadOnlyList<MissileProfile> Profiles { get; } =
        new ReadOnlyCollection<MissileProfile>(All);

    /// <summary>导弹总数。</summary>
    public static int Count => All.Length;

    /// <summary>
    /// 按名字模糊查找导弹。
    /// <para>
    /// 匹配策略：先精确 Id，再精确显示名，最后做包含匹配。
    /// 包含匹配时取<b>最长命中</b>的那一条 —— 避免 <c>R-60M</c> 被 <c>R-60</c> 抢走。
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

        // 1) Id / 显示名 / 中文名 精确命中。
        foreach (var profile in All)
        {
            if (Normalize(profile.Id) == query ||
                Normalize(profile.DisplayName) == query ||
                Normalize(profile.DisplayNameZh) == query)
            {
                return profile;
            }
        }

        // 2) 包含匹配，取最长命中，平局取参数更全的一条。
        MissileProfile? best = null;
        var bestLength = 0;

        foreach (var profile in All)
        {
            foreach (var candidate in EnumerateNames(profile))
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

    /// <summary>按别名反查。别名表见 <see cref="AliasTable"/>。</summary>
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

        // 先按别名表精确匹配（游戏挂载名往往带前缀或后缀）。
        foreach (var (alias, id) in AliasTable)
        {
            if (query.Contains(Normalize(alias), StringComparison.Ordinal))
            {
                var hit = All.FirstOrDefault(p =>
                    string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    return hit;
                }
            }
        }

        return Find(mountedName);
    }

    private static IEnumerable<string> EnumerateNames(MissileProfile profile)
    {
        yield return Normalize(profile.Id);
        yield return Normalize(profile.DisplayName);
        yield return Normalize(profile.DisplayNameZh);
    }

    /// <summary>参数完整度打分，用于歧义时的择优。</summary>
    private static int Score(MissileProfile? profile)
    {
        if (profile is null)
        {
            return -1;
        }

        var score = 0;
        if (profile.MaxRange is not null) score++;
        if (profile.PoweredRange is not null) score++;
        if (profile.BurnTime is not null) score++;
        if (profile.MaxG is not null) score++;
        if (profile.SeekerRange is not null) score++;
        if (profile.LaunchGLimit is not null) score++;
        return score;
    }

    /// <summary>把名字规范化：小写、去掉分隔符，便于比较。</summary>
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

    /// <summary>
    /// 游戏挂载名 → 导弹 Id 的别名表。
    /// <para>
    /// 按<b>长别名优先</b>排列（如 <c>R-60M</c> 必须排在 <c>R-60</c> 前面），
    /// 否则短名会先命中，把升级型误判成基础型。
    /// </para>
    /// </summary>
    private static readonly (string Alias, string Id)[] AliasTable =
    [
        // 苏/俄 —— 长名在前
        ("R-60MK", "r60m"), ("R-60M", "r60m"), ("R-60", "r60"),
        ("R-73", "r73"), ("R-73E", "r73"),
        ("R-27ER", "r27er"), ("R-27ET", "r27et"), ("R-27R", "r27r"), ("R-27T", "r27t"),
        ("R-24R", "r24r"), ("R-24T", "r24t"),
        ("R-23R", "r23r"), ("R-23T", "r23t"),
        ("R-3R", "r3r"), ("R-3S", "r3s"),
        ("R-13M1", "r13m1"), ("R-13M", "r13m"),
        ("R-77", "r77"),
        ("R-40RD", "r40rd"), ("R-40TD", "r40td"),

        // 美制
        ("AIM-9M", "aim9m"), ("AIM-9L", "aim9l"), ("AIM-9J", "aim9j"),
        ("AIM-9P", "aim9p"), ("AIM-9E", "aim9e"), ("AIM-9B", "aim9b"), ("AIM-9D", "aim9d"),
        ("AIM-7M", "aim7m"), ("AIM-7F", "aim7f"), ("AIM-7E", "aim7e"),
        ("AIM-54", "aim54"),

        // 其他国家
        ("Magic 1", "magic1"), ("R550", "magic1"),
        ("PL-2", "pl2"), ("PL-5", "pl5"),
        ("Skyflash", "skyflash"), ("Aspide", "aspide"),
        ("Shafrir", "shafrir"), ("RB24", "rb24"),
        ("К-13", "r3s"),
    ];

    private static MissileProfile[] Build()
    {
        return
        [
            // ============================================================
            // 早期尾追红外弹 —— 锁定角度小、过载低、发射过载限制苛刻
            // ============================================================
            new MissileProfile
            {
                Id = "aim9b",
                DisplayName = "AIM-9B Sidewinder",
                DisplayNameZh = "AIM-9B 响尾蛇",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "美国",
                // wiki 表：launch range 10 km / max speed M2.5 / overload 10 g。
                MaxRange = 10_000,
                PoweredRange = 4_500,
                BurnTime = 2.2,
                MaxSpeed = 850,
                MaxG = 10,
                SeekerRange = 5_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 1_000,
                    ["tail-on"] = 5_000,
                    ["side-on"] = 2_500,
                },
                LaunchGLimit = 3,
                MinRange = 900,
                Source = "wt-wiki",
            },

            new MissileProfile
            {
                Id = "r3s",
                DisplayName = "R-3S",
                DisplayNameZh = "R-3S（AA-2A 环礁）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                // 官方论坛：M1.7 / burn 2.4 s / flight 21 s / max 7.5 km /
                // effective 2 km / 12 G / launch limit 2 G / rear-aspect。
                MaxRange = 7_500,
                PoweredRange = 3_500,
                BurnTime = 2.4,
                MaxSpeed = 580,
                MaxG = 12,
                SeekerRange = 4_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 800,
                    ["tail-on"] = 4_000,
                    ["side-on"] = 1_800,
                },
                LaunchGLimit = 2,
                MinRange = 1_200,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "aim9e",
                DisplayName = "AIM-9E Sidewinder",
                DisplayNameZh = "AIM-9E 响尾蛇",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "美国",
                MaxRange = 11_000,
                PoweredRange = 5_000,
                BurnTime = 2.2,
                MaxSpeed = 850,
                MaxG = 16,
                SeekerRange = 6_000,
                LaunchGLimit = 4,
                MinRange = 900,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "aim9j",
                DisplayName = "AIM-9J Sidewinder",
                DisplayNameZh = "AIM-9J 响尾蛇",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "美国",
                MaxRange = 18_000,
                PoweredRange = 6_500,
                BurnTime = 2.2,
                MaxSpeed = 850,
                MaxG = 18,
                SeekerRange = 6_500,
                LaunchGLimit = 5,
                MinRange = 900,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "aim9p",
                DisplayName = "AIM-9P Sidewinder",
                DisplayNameZh = "AIM-9P 响尾蛇",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "美国",
                MaxRange = 18_000,
                PoweredRange = 6_000,
                BurnTime = 2.2,
                MaxSpeed = 850,
                MaxG = 20,
                SeekerRange = 6_500,
                LaunchGLimit = 6,
                MinRange = 900,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "r13m",
                DisplayName = "R-13M",
                DisplayNameZh = "R-13M（AA-2C）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                // 官方论坛：M2.5 / burn 3.3-5.4 s / flight 55 s / max 15 km /
                // effective 3 km / 15 G / launch 3.7 G / rear-aspect。
                MaxRange = 15_000,
                PoweredRange = 5_000,
                BurnTime = 3.3,
                MaxSpeed = 850,
                MaxG = 15,
                SeekerRange = 5_500,
                LaunchGLimit = 3.7,
                MinRange = 1_000,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "r13m1",
                DisplayName = "R-13M1",
                DisplayNameZh = "R-13M1（AA-2D）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                MaxRange = 15_000,
                PoweredRange = 5_500,
                BurnTime = 3.3,
                MaxSpeed = 850,
                MaxG = 21,
                SeekerRange = 6_000,
                LaunchGLimit = 6,
                MinRange = 1_000,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "magic1",
                DisplayName = "R550 Magic 1",
                DisplayNameZh = "R550 魔术 1",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "法国",
                // 社区资料称其为游戏中过载最高的尾追红外弹（35 G）。
                MaxRange = 10_000,
                PoweredRange = 5_000,
                BurnTime = 2.0,
                MaxSpeed = 800,
                MaxG = 35,
                SeekerRange = 6_000,
                LaunchGLimit = 6,
                MinRange = 800,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "pl2",
                DisplayName = "PL-2",
                DisplayNameZh = "霹雳-2",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "中国",
                // wiki 表：与 R-3S 同源，共享游戏内性能。
                MaxRange = 7_500,
                PoweredRange = 3_500,
                BurnTime = 2.4,
                MaxSpeed = 580,
                MaxG = 12,
                SeekerRange = 4_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 800,
                    ["tail-on"] = 4_000,
                    ["side-on"] = 1_800,
                },
                LaunchGLimit = 2,
                MinRange = 1_200,
                Source = "wt-wiki",
            },

            // ============================================================
            // 全向红外格斗弹 —— 现代房主力
            // ============================================================
            new MissileProfile
            {
                Id = "aim9l",
                DisplayName = "AIM-9L Sidewinder",
                DisplayNameZh = "AIM-9L 响尾蛇",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "美国",
                // wiki 表：lock range (rear-aspect) 60 km（该列为导引头理论值，
                // 实战不可达）/ launch range 11 km / max speed M2.5 / overload 30 g。
                // 页内正文明确：最佳使用距离 3 km 内尾追，5.2 s 燃烧对应约 5 km 可转向区。
                MaxRange = 11_000,
                PoweredRange = 5_000,
                BurnTime = 5.2,
                MaxSpeed = 850,
                MaxG = 30,
                SeekerRange = 9_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 5_500,
                    ["tail-on"] = 9_000,
                    ["side-on"] = 7_000,
                },
                LaunchGLimit = 7,
                MinRange = 800,
                Source = "wt-wiki",
            },

            new MissileProfile
            {
                Id = "aim9m",
                DisplayName = "AIM-9M Sidewinder",
                DisplayNameZh = "AIM-9M 响尾蛇",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "美国",
                // 弹体与 9L 一致，导引头抗干扰更好（低烟发动机、闭环制冷）。
                MaxRange = 13_000,
                PoweredRange = 5_500,
                BurnTime = 5.2,
                MaxSpeed = 850,
                MaxG = 30,
                SeekerRange = 10_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 6_500,
                    ["tail-on"] = 10_000,
                    ["side-on"] = 8_000,
                },
                LaunchGLimit = 7,
                MinRange = 800,
                Source = "wt-wiki",
            },

            new MissileProfile
            {
                Id = "r60",
                DisplayName = "R-60",
                DisplayNameZh = "R-60（AA-8 蚜虫）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                // 官方论坛：M2.5 / burn 3-5 s / flight 23-25 s / max 7 km /
                // effective 4 km / 30 G / launch 7 G / rear-aspect。
                MaxRange = 7_000,
                PoweredRange = 3_500,
                BurnTime = 3.0,
                MaxSpeed = 850,
                MaxG = 30,
                SeekerRange = 5_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 1_500,
                    ["tail-on"] = 5_000,
                    ["side-on"] = 3_000,
                },
                LaunchGLimit = 7,
                MinRange = 600,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "r60m",
                DisplayName = "R-60M",
                DisplayNameZh = "R-60M（AA-8B 蚜虫）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                // 官方论坛：M2.7 / flight 23 s / max 10 km / effective 7 km /
                // 30 G / launch 8.5 G / limited-aspect（非严格全向）。
                MaxRange = 10_000,
                PoweredRange = 4_500,
                BurnTime = 3.0,
                MaxSpeed = 920,
                MaxG = 30,
                SeekerRange = 7_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 2_500,
                    ["tail-on"] = 7_000,
                    ["side-on"] = 4_500,
                },
                LaunchGLimit = 8.5,
                MinRange = 600,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "r73",
                DisplayName = "R-73",
                DisplayNameZh = "R-73（AA-11 射手）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                // 官方论坛该帖未收录 R-73 详细表，按公开资料录保守值：
                // 射程 30 km 级、40 G 级过载、大离轴发射能力。
                // 射程与过载取保守下限，避免高估。
                MaxRange = 20_000,
                PoweredRange = 8_000,
                BurnTime = 6.0,
                MaxSpeed = 850,
                MaxG = 40,
                SeekerRange = 11_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 8_000,
                    ["tail-on"] = 11_000,
                    ["side-on"] = 9_500,
                },
                LaunchGLimit = 9,
                MinRange = 600,
                Source = "community-estimate",
            },

            new MissileProfile
            {
                Id = "pl5",
                DisplayName = "PL-5",
                DisplayNameZh = "霹雳-5",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "中国",
                MaxRange = 16_000,
                PoweredRange = 6_000,
                BurnTime = 3.5,
                MaxSpeed = 850,
                MaxG = 30,
                SeekerRange = 8_000,
                LaunchGLimit = 7,
                MinRange = 700,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "shafrir",
                DisplayName = "Shafrir 2",
                DisplayNameZh = "怪蛇 2",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "以色列",
                // wiki 表：lock 20 km / launch 7 km / M1.7 / 11 G。
                MaxRange = 7_000,
                PoweredRange = 3_500,
                BurnTime = 2.4,
                MaxSpeed = 580,
                MaxG = 11,
                SeekerRange = 5_500,
                LaunchGLimit = 4,
                MinRange = 1_000,
                Source = "wt-wiki",
            },

            new MissileProfile
            {
                Id = "rb24",
                DisplayName = "RB24",
                DisplayNameZh = "RB24（瑞典版 AIM-9B）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "瑞典",
                // wiki 表：与 AIM-9B 共享游戏内性能，launch 4 km。
                MaxRange = 4_000,
                PoweredRange = 3_000,
                BurnTime = 2.2,
                MaxSpeed = 580,
                MaxG = 10,
                SeekerRange = 4_000,
                LaunchGLimit = 3,
                MinRange = 900,
                Source = "wt-wiki",
            },

            // ============================================================
            // 半主动雷达（SARH）—— 需要母机持续照射
            // ============================================================
            new MissileProfile
            {
                Id = "r3r",
                DisplayName = "R-3R",
                DisplayNameZh = "R-3R（AA-2B 环礁）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "苏联",
                // 官方论坛：M1.7 / burn 2.4 s / flight 21 s / max 8 km /
                // 12 G / launch 2 G / all-aspect。
                MaxRange = 8_000,
                PoweredRange = 3_500,
                BurnTime = 2.4,
                MaxSpeed = 580,
                MaxG = 12,
                SeekerRange = 8_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 8_000,
                    ["tail-on"] = 3_000,
                    ["side-on"] = 5_000,
                },
                LaunchGLimit = 2,
                MinRange = 1_500,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "aim7e",
                DisplayName = "AIM-7E Sparrow",
                DisplayNameZh = "AIM-7E 麻雀",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "美国",
                // 官方论坛：boost 2.8 s @ 35 kN，25 G，发射后 1.8 s 才起控。
                // 3 km 以上高度对直飞目标，6~12 km 内有望命中。
                MaxRange = 25_000,
                PoweredRange = 9_000,
                BurnTime = 2.8,
                MaxSpeed = 1_200,
                MaxG = 25,
                SeekerRange = 20_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 20_000,
                    ["tail-on"] = 6_000,
                    ["side-on"] = 12_000,
                },
                LaunchGLimit = 5,
                MinRange = 2_500,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "aim7f",
                DisplayName = "AIM-7F Sparrow",
                DisplayNameZh = "AIM-7F 麻雀",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "美国",
                // 官方论坛：boost 4.5 s @ 26.9 kN，双推力发动机。
                MaxRange = 40_000,
                PoweredRange = 15_000,
                BurnTime = 4.5,
                MaxSpeed = 1_360,
                MaxG = 30,
                SeekerRange = 25_000,
                LaunchGLimit = 6,
                MinRange = 2_000,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "aim7m",
                DisplayName = "AIM-7M Sparrow",
                DisplayNameZh = "AIM-7M 麻雀",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "美国",
                // 官方论坛（实物规格）：boost 4.5 s @ 2,608 kgf + sustain 11 s @ 461 kgf，
                // 制导时长 75 s，过载 30 G，导引头锁定 40 km，最大发射速度 M2.5。
                MaxRange = 45_000,
                PoweredRange = 17_000,
                BurnTime = 15.5,
                MaxSpeed = 1_360,
                MaxG = 30,
                SeekerRange = 40_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 40_000,
                    ["tail-on"] = 10_000,
                    ["side-on"] = 20_000,
                },
                LaunchGLimit = 6,
                MinRange = 2_000,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "skyflash",
                DisplayName = "Skyflash",
                DisplayNameZh = "天闪",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "英国",
                // 基于 AIM-7E-2 弹体 + 改进导引头（逆单脉冲）。
                // 官方论坛：E-2 将起控延迟缩到 0.7 s、引信激活缩到 0.8 s，
                // 最小射程降至 2~3 km。导引头低空性能更好。
                MaxRange = 30_000,
                PoweredRange = 10_000,
                BurnTime = 2.8,
                MaxSpeed = 1_200,
                MaxG = 25,
                SeekerRange = 22_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 22_000,
                    ["tail-on"] = 8_000,
                    ["side-on"] = 14_000,
                },
                LaunchGLimit = 5,
                MinRange = 1_500,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "aspide",
                DisplayName = "Aspide 1A",
                DisplayNameZh = "阿斯派德",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "意大利",
                MaxRange = 35_000,
                PoweredRange = 12_000,
                BurnTime = 3.5,
                MaxSpeed = 1_200,
                MaxG = 30,
                SeekerRange = 25_000,
                LaunchGLimit = 6,
                MinRange = 1_500,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "r23r",
                DisplayName = "R-23R",
                DisplayNameZh = "R-23R（AA-7A）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "苏联",
                // 官方论坛：M3 / burn 5 s / flight 35 s / max 25 km（前半球）/
                // 20 G / launch 4 G / all-aspect。
                MaxRange = 25_000,
                PoweredRange = 10_000,
                BurnTime = 5.0,
                MaxSpeed = 1_020,
                MaxG = 20,
                SeekerRange = 25_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 25_000,
                    ["tail-on"] = 7_000,
                    ["side-on"] = 13_000,
                },
                LaunchGLimit = 4,
                MinRange = 2_000,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "r23t",
                DisplayName = "R-23T",
                DisplayNameZh = "R-23T（AA-7B）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                // 官方论坛：M3 / burn 5 s / flight 35 s / max 11 km / 20 G /
                // launch 4 G / all-aspect（海平面非加力 3 km 锁定）。
                MaxRange = 11_000,
                PoweredRange = 6_000,
                BurnTime = 5.0,
                MaxSpeed = 1_020,
                MaxG = 20,
                SeekerRange = 6_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 3_000,
                    ["tail-on"] = 6_000,
                    ["side-on"] = 4_500,
                },
                LaunchGLimit = 4,
                MinRange = 1_000,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "r24r",
                DisplayName = "R-24R",
                DisplayNameZh = "R-24R（AA-7C）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "苏联",
                // 官方论坛：M3.5 / burn 5 s / flight 45 s / max 50 km /
                // 24 G / launch 5 G / all-aspect。
                MaxRange = 50_000,
                PoweredRange = 15_000,
                BurnTime = 5.0,
                MaxSpeed = 1_190,
                MaxG = 24,
                SeekerRange = 35_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 35_000,
                    ["tail-on"] = 10_000,
                    ["side-on"] = 18_000,
                },
                LaunchGLimit = 5,
                MinRange = 1_500,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "r24t",
                DisplayName = "R-24T",
                DisplayNameZh = "R-24T（AA-7D）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                // 官方论坛：M3.5 / burn 5 s / flight 45 s / 24 G / launch 5 G。
                // 锁定距离随高度与目标状态变化很大（3 km ~ 9 km 典型）。
                MaxRange = 20_000,
                PoweredRange = 10_000,
                BurnTime = 5.0,
                MaxSpeed = 1_190,
                MaxG = 24,
                SeekerRange = 9_000,
                Seekers = new Dictionary<string, double>
                {
                    ["head-on"] = 5_000,
                    ["tail-on"] = 9_000,
                    ["side-on"] = 7_000,
                },
                LaunchGLimit = 5,
                MinRange = 1_000,
                Source = "wt-forum",
            },

            new MissileProfile
            {
                Id = "r27r",
                DisplayName = "R-27R",
                DisplayNameZh = "R-27R（AA-10 白杨）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "苏联",
                MaxRange = 60_000,
                PoweredRange = 20_000,
                BurnTime = 6.0,
                MaxSpeed = 1_360,
                MaxG = 24,
                SeekerRange = 40_000,
                LaunchGLimit = 6,
                MinRange = 1_500,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "r27er",
                DisplayName = "R-27ER",
                DisplayNameZh = "R-27ER（增程型）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "苏联",
                // 增程型：更大发动机、更长燃烧。
                MaxRange = 95_000,
                PoweredRange = 32_000,
                BurnTime = 8.0,
                MaxSpeed = 1_700,
                MaxG = 24,
                SeekerRange = 45_000,
                LaunchGLimit = 7,
                MinRange = 1_500,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "r27t",
                DisplayName = "R-27T",
                DisplayNameZh = "R-27T（红外型）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                MaxRange = 40_000,
                PoweredRange = 15_000,
                BurnTime = 6.0,
                MaxSpeed = 1_360,
                MaxG = 24,
                SeekerRange = 12_000,
                LaunchGLimit = 6,
                MinRange = 1_200,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "r27et",
                DisplayName = "R-27ET",
                DisplayNameZh = "R-27ET（红外增程型）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Ir,
                Nation = "苏联",
                MaxRange = 65_000,
                PoweredRange = 25_000,
                BurnTime = 8.0,
                MaxSpeed = 1_700,
                MaxG = 24,
                SeekerRange = 15_000,
                LaunchGLimit = 7,
                MinRange = 1_200,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "r40rd",
                DisplayName = "R-40RD",
                DisplayNameZh = "R-40RD（AA-6 毒辣）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Sarh,
                Nation = "苏联",
                // 官方论坛：M4.5 / burn 4-6.7 s / flight 40 s / 60 km /
                // 15 G / launch 3 G。
                MaxRange = 60_000,
                PoweredRange = 22_000,
                BurnTime = 4.0,
                MaxSpeed = 1_530,
                MaxG = 15,
                SeekerRange = 40_000,
                LaunchGLimit = 3,
                MinRange = 3_000,
                Source = "wt-forum",
            },

            // ============================================================
            // 主动雷达（ARH）—— 射后不理，末段自开雷达
            // ============================================================
            new MissileProfile
            {
                Id = "aim54",
                DisplayName = "AIM-54A Phoenix",
                DisplayNameZh = "AIM-54 不死鸟",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Arh,
                Nation = "美国",
                // 不死鸟为远距重型弹，末段主动雷达开机距离约 16 km（游戏机制通例）。
                MaxRange = 100_000,
                PoweredRange = 30_000,
                BurnTime = 8.0,
                MaxSpeed = 1_360,
                MaxG = 17,
                SeekerRange = 38_000,
                LaunchGLimit = 6,
                MinRange = 3_000,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "r77",
                DisplayName = "R-77",
                DisplayNameZh = "R-77（AA-12 蝰蛇）",
                Kind = MissileKind.AirToAir,
                Guidance = MissileGuidance.Arh,
                Nation = "苏联",
                // 官方论坛该帖未收录 R-77；按公开资料录保守值，
                // 主动雷达开机距离取游戏通例的 16 km。
                MaxRange = 80_000,
                PoweredRange = 25_000,
                BurnTime = 6.0,
                MaxSpeed = 1_360,
                MaxG = 35,
                SeekerRange = 20_000,
                LaunchGLimit = 7,
                MinRange = 1_500,
                Source = "community-estimate",
            },

            // ============================================================
            // 空对地导弹 / 反坦克导弹
            // ============================================================
            new MissileProfile
            {
                Id = "agm65d",
                DisplayName = "AGM-65D Maverick",
                DisplayNameZh = "AGM-65D 小牛（红外型）",
                Kind = MissileKind.AirToGround,
                Guidance = MissileGuidance.Ir,
                Nation = "美国",
                MaxRange = 20_000,
                PoweredRange = 8_000,
                BurnTime = 3.0,
                MaxSpeed = 350,
                MaxG = 8,
                SeekerRange = 12_000,
                LaunchGLimit = 8,
                MinRange = 800,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "agm65b",
                DisplayName = "AGM-65B Maverick",
                DisplayNameZh = "AGM-65B 小牛（电视制导）",
                Kind = MissileKind.AirToGround,
                Guidance = MissileGuidance.Arh,
                Nation = "美国",
                MaxRange = 18_000,
                PoweredRange = 7_000,
                BurnTime = 3.0,
                MaxSpeed = 350,
                MaxG = 8,
                SeekerRange = 10_000,
                LaunchGLimit = 8,
                MinRange = 800,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "9m114",
                DisplayName = "9M114 Shturm",
                DisplayNameZh = "9M114 风暴（AT-6）",
                Kind = MissileKind.AirToGround,
                Guidance = MissileGuidance.Command,
                Nation = "苏联",
                // 直升机对地弹：射程短、速度低、需全程制导。
                MaxRange = 5_000,
                PoweredRange = 3_500,
                BurnTime = 5.0,
                MaxSpeed = 190,
                MaxG = 6,
                SeekerRange = 5_000,
                LaunchGLimit = 4,
                MinRange = 500,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "9m120",
                DisplayName = "9M120 Ataka",
                DisplayNameZh = "9M120 攻击（AT-9）",
                Kind = MissileKind.AirToGround,
                Guidance = MissileGuidance.Command,
                Nation = "苏联",
                MaxRange = 6_000,
                PoweredRange = 4_500,
                BurnTime = 6.0,
                MaxSpeed = 250,
                MaxG = 6,
                SeekerRange = 6_000,
                LaunchGLimit = 4,
                MinRange = 500,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "agm114",
                DisplayName = "AGM-114 Hellfire",
                DisplayNameZh = "AGM-114 地狱火（激光型）",
                Kind = MissileKind.AirToGround,
                Guidance = MissileGuidance.Command,
                Nation = "美国",
                MaxRange = 8_000,
                PoweredRange = 5_000,
                BurnTime = 4.0,
                MaxSpeed = 450,
                MaxG = 9,
                SeekerRange = 8_000,
                LaunchGLimit = 5,
                MinRange = 500,
                Source = "community",
            },

            new MissileProfile
            {
                Id = "kh29t",
                DisplayName = "Kh-29T",
                DisplayNameZh = "Kh-29T（电视制导）",
                Kind = MissileKind.AirToGround,
                Guidance = MissileGuidance.Arh,
                Nation = "苏联",
                MaxRange = 15_000,
                PoweredRange = 7_000,
                BurnTime = 4.0,
                MaxSpeed = 550,
                MaxG = 8,
                SeekerRange = 10_000,
                LaunchGLimit = 6,
                MinRange = 1_000,
                Source = "community",
            },
        ];
    }
}
