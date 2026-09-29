using System.Collections.ObjectModel;

namespace WarThunderTelemetry.Core.Parsing;

/// <summary>
/// 全部字段的登记清单。
/// <para>
/// 别名按「真实字段名优先、兼容旧名其次」排列。字段名依据官方接口文档
/// （lucasvmx/WarThunder-localhost-documentation）与真实抓包校准，
/// 注意 <c>/state</c> 的键含空格与逗号，如 <c>"IAS, km/h"</c>、<c>"throttle 1, %"</c>。
/// </para>
/// <para>
/// 默认勾选的 7 项：表速、高度、垂直速度、油门、过载、燃油、航向。
/// </para>
/// </summary>
public static class FieldCatalog
{
    /// <summary>默认勾选的字段 Id。</summary>
    public static readonly IReadOnlySet<string> DefaultSelectedIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ias", "altitude", "vertical_speed", "throttle", "g_load", "fuel", "heading",
        };

    private static readonly FieldDescriptor[] Catalog = BuildCatalog();

    /// <summary>全部字段（按登记顺序）。</summary>
    public static IReadOnlyList<FieldDescriptor> All { get; } =
        new ReadOnlyCollection<FieldDescriptor>(Catalog);

    /// <summary>按 Id 建立索引的解析器。</summary>
    public static AliasResolver CreateResolver() => new(Catalog);

    private static FieldDescriptor[] BuildCatalog()
    {
        return
        [
            // ===== 飞行核心数据 =====
            new FieldDescriptor
            {
                Id = "ias",
                DisplayName = "表速",
                Unit = "km/h",
                Category = FieldCategory.Flight,
                DefaultSelected = true,
                PreferredEndpoint = "state",
                Aliases = ["IAS, km/h", "ias, km/h", "ias", "indicated airspeed"],
            },
            new FieldDescriptor
            {
                Id = "tas",
                DisplayName = "真空速",
                Unit = "km/h",
                Category = FieldCategory.Flight,
                PreferredEndpoint = "state",
                Aliases = ["TAS, km/h", "tas, km/h", "tas", "true airspeed"],
            },
            new FieldDescriptor
            {
                Id = "altitude",
                DisplayName = "海拔高度",
                Unit = "m",
                Category = FieldCategory.Flight,
                DefaultSelected = true,
                PreferredEndpoint = "state",
                Decimals = 0,
                Aliases = ["H, m", "h, m", "altitude", "altitude_hour"],
            },
            new FieldDescriptor
            {
                Id = "radar_altitude",
                DisplayName = "相对高度",
                Unit = "m",
                Category = FieldCategory.Flight,
                Decimals = 0,
                Aliases = ["altitude_min", "altitude_10k"],
            },
            new FieldDescriptor
            {
                Id = "vertical_speed",
                DisplayName = "垂直速度",
                Unit = "m/s",
                Category = FieldCategory.Flight,
                DefaultSelected = true,
                Aliases = ["Vy, m/s", "vy, m/s", "vy", "vario", "vertical speed"],
            },
            new FieldDescriptor
            {
                Id = "mach",
                DisplayName = "马赫",
                Unit = "M",
                Category = FieldCategory.Flight,
                Decimals = 2,
                Aliases = ["M", "m", "mach"],
            },
            new FieldDescriptor
            {
                Id = "aoa",
                DisplayName = "迎角",
                Unit = "°",
                Category = FieldCategory.Flight,
                PreferredEndpoint = "state",
                Aliases = ["AoA, deg", "aoa, deg", "aoa"],
            },
            new FieldDescriptor
            {
                Id = "aos",
                DisplayName = "侧滑角",
                Unit = "°",
                Category = FieldCategory.Flight,
                PreferredEndpoint = "state",
                Aliases = ["AoS, deg", "aos, deg", "aos"],
            },
            new FieldDescriptor
            {
                Id = "roll_rate",
                DisplayName = "滚转率",
                Unit = "°/s",
                Category = FieldCategory.Flight,
                PreferredEndpoint = "state",
                Aliases = ["Wx, deg/s", "wx, deg/s", "wx"],
            },
            new FieldDescriptor
            {
                Id = "heading",
                DisplayName = "航向",
                Unit = "°",
                Category = FieldCategory.Flight,
                DefaultSelected = true,
                Decimals = 0,
                // 真实字段名是 compass，不是 heading
                Aliases = ["compass", "compass1", "compass2", "heading"],
            },
            new FieldDescriptor
            {
                Id = "pitch",
                DisplayName = "俯仰角",
                Unit = "°",
                Category = FieldCategory.Flight,
                Aliases = ["aviahorizon_pitch"],
            },
            new FieldDescriptor
            {
                Id = "bank",
                DisplayName = "坡度",
                Unit = "°",
                Category = FieldCategory.Flight,
                Aliases = ["aviahorizon_roll", "bank"],
            },
            new FieldDescriptor
            {
                Id = "turn_rate",
                DisplayName = "回转率",
                Unit = "°/s",
                Category = FieldCategory.Flight,
                Aliases = ["turn", "turn_rate"],
            },

            // ===== 动力系统 =====
            new FieldDescriptor
            {
                Id = "throttle",
                DisplayName = "油门",
                Unit = "%",
                Category = FieldCategory.Engine,
                DefaultSelected = true,
                Decimals = 0,
                IsPerEngine = true,
                // /state 的 "throttle N, %" 本就是百分比，Multiplier 必须为 1
                Aliases = ["throttle 1, %", "throttle 2, %", "throttle, %", "throttle 1", "throttle"],
                PreferredEndpoint = "state",
            },
            new FieldDescriptor
            {
                Id = "rpm",
                DisplayName = "转速",
                Unit = "rpm",
                Category = FieldCategory.Engine,
                Decimals = 0,
                AbbreviateLarge = true,
                IsPerEngine = true,
                PreferredEndpoint = "state",
                Aliases = ["RPM 1", "RPM 2", "rpm1", "rpm 1", "rpm"],
            },
            new FieldDescriptor
            {
                Id = "power",
                DisplayName = "功率",
                Unit = "hp",
                Category = FieldCategory.Engine,
                Decimals = 0,
                IsPerEngine = true,
                PreferredEndpoint = "state",
                Aliases = ["power 1, hp", "power 2, hp", "power 1", "power"],
            },
            new FieldDescriptor
            {
                Id = "oil_temp",
                DisplayName = "油温",
                Unit = "℃",
                Category = FieldCategory.Engine,
                IsPerEngine = true,
                PreferredEndpoint = "state",
                Aliases = ["oil temp 1, C", "oil temp 2, C", "oil temp 1", "oil_temperature"],
            },
            new FieldDescriptor
            {
                Id = "water_temp",
                DisplayName = "水温",
                Unit = "℃",
                Category = FieldCategory.Engine,
                IsPerEngine = true,
                PreferredEndpoint = "state",
                Aliases = ["water temp 1, C", "water temp 2, C", "water temp 1", "water_temperature"],
            },
            new FieldDescriptor
            {
                Id = "oil_pressure",
                DisplayName = "油压",
                Unit = "atm",
                Category = FieldCategory.Engine,
                Aliases = ["oil_pressure"],
            },
            new FieldDescriptor
            {
                Id = "manifold_pressure",
                DisplayName = "进气压力",
                Unit = "atm",
                Category = FieldCategory.Engine,
                Aliases = ["manifold_pressure", "manifold pressure 1, atm"],
            },
            new FieldDescriptor
            {
                Id = "prop_pitch",
                DisplayName = "桨距",
                Unit = "%",
                Category = FieldCategory.Engine,
                Aliases = ["pitch 1, deg", "prop_pitch"],
            },

            // ===== 操纵面与起落装置 =====
            new FieldDescriptor
            {
                Id = "aileron",
                DisplayName = "副翼",
                Unit = "%",
                Category = FieldCategory.Control,
                Decimals = 0,
                PreferredEndpoint = "state",
                Aliases = ["aileron, %", "aileron, percent", "stick_ailerons"],
            },
            new FieldDescriptor
            {
                Id = "elevator",
                DisplayName = "升降舵",
                Unit = "%",
                Category = FieldCategory.Control,
                Decimals = 0,
                PreferredEndpoint = "state",
                Aliases = ["elevator, %", "elevator, percent", "stick_elevator"],
            },
            new FieldDescriptor
            {
                Id = "rudder",
                DisplayName = "方向舵",
                Unit = "%",
                Category = FieldCategory.Control,
                Decimals = 0,
                PreferredEndpoint = "state",
                Aliases = ["rudder, %", "rudder, percent", "pedals1"],
            },
            new FieldDescriptor
            {
                Id = "flaps",
                DisplayName = "襟翼",
                Unit = "%",
                Category = FieldCategory.Control,
                Decimals = 0,
                PreferredEndpoint = "state",
                Aliases = ["flaps, %", "flaps"],
            },
            new FieldDescriptor
            {
                Id = "gear",
                DisplayName = "起落架",
                Unit = "%",
                Category = FieldCategory.Control,
                Kind = FieldKind.Flag,
                Decimals = 0,
                Aliases = ["gears", "gear, %", "gear"],
            },
            new FieldDescriptor
            {
                Id = "airbrake",
                DisplayName = "减速板",
                Unit = "%",
                Category = FieldCategory.Control,
                Decimals = 0,
                Aliases = ["airbrake_lever", "airbrake, %", "airbrake"],
            },

            // ===== 燃油 =====
            new FieldDescriptor
            {
                Id = "fuel",
                DisplayName = "剩余燃油",
                Unit = "kg",
                Category = FieldCategory.Fuel,
                DefaultSelected = true,
                Decimals = 0,
                PreferredEndpoint = "state",
                Aliases = ["Mfuel, kg", "mfuel, kg", "fuel, kg", "fuel"],
            },
            new FieldDescriptor
            {
                Id = "fuel_capacity",
                DisplayName = "满载燃油",
                Unit = "kg",
                Category = FieldCategory.Fuel,
                Decimals = 0,
                PreferredEndpoint = "state",
                Aliases = ["Mfuel0, kg", "mfuel0, kg", "fuel0, kg"],
            },

            // ===== 武器 =====
            new FieldDescriptor
            {
                Id = "ammo",
                DisplayName = "弹药总数",
                Unit = "发",
                Category = FieldCategory.Weapon,
                Kind = FieldKind.Sum,
                Decimals = 0,
                Aliases = ["ammo"],
            },

            // ===== 载具信息 =====
            new FieldDescriptor
            {
                Id = "vehicle_type",
                DisplayName = "载具代号",
                Unit = string.Empty,
                Category = FieldCategory.Vehicle,
                Kind = FieldKind.Text,
                Aliases = ["type"],
            },
            new FieldDescriptor
            {
                Id = "army",
                DisplayName = "兵种",
                Unit = string.Empty,
                Category = FieldCategory.Vehicle,
                Kind = FieldKind.Text,
                Aliases = ["army"],
            },

            // ===== 过载（state 用 Ny，indicators 用 g_meter）=====
            new FieldDescriptor
            {
                Id = "g_load",
                DisplayName = "过载",
                Unit = "g",
                Category = FieldCategory.Flight,
                DefaultSelected = true,
                Decimals = 1,
                // g_meter 排在前面：仪表读数比原始值稳定，避免抖动
                Aliases = ["g_meter", "Ny", "ny", "g_meter_max", "g"],
            },

            // ===== 派生指标（由 DerivedMetrics 提供）=====
            new FieldDescriptor
            {
                Id = "derived_accel",
                DisplayName = "加速度",
                Unit = "m/s²",
                Category = FieldCategory.Derived,
                Kind = FieldKind.Derived,
                Decimals = 2,
                Aliases = ["accel"],
            },
            new FieldDescriptor
            {
                Id = "derived_energy",
                DisplayName = "能量高度",
                Unit = "m",
                Category = FieldCategory.Derived,
                Kind = FieldKind.Derived,
                Decimals = 0,
                Aliases = ["energy"],
            },
            new FieldDescriptor
            {
                Id = "derived_climb_angle",
                DisplayName = "爬升角",
                Unit = "°",
                Category = FieldCategory.Derived,
                Kind = FieldKind.Derived,
                Aliases = ["climb"],
            },
            new FieldDescriptor
            {
                Id = "derived_turn_rate",
                DisplayName = "回转率(平滑)",
                Unit = "°/s",
                Category = FieldCategory.Derived,
                Kind = FieldKind.Derived,
                Aliases = ["turnRate"],
            },
            new FieldDescriptor
            {
                Id = "derived_turn_radius",
                DisplayName = "回转半径",
                Unit = "m",
                Category = FieldCategory.Derived,
                Kind = FieldKind.Derived,
                Decimals = 0,
                Aliases = ["turnRadius"],
            },
            new FieldDescriptor
            {
                Id = "derived_turn_time",
                DisplayName = "盘旋360°",
                Unit = "s",
                Category = FieldCategory.Derived,
                Kind = FieldKind.Derived,
                Aliases = ["turnTime"],
            },
        ];
    }
}
