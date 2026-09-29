using System.Text.Json;
using System.Text.Json.Serialization;
using WarThunderTelemetry.Core;
using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.App.Services;

/// <summary>
/// 应用配置。
/// </summary>
public sealed record AppSettings
{
    /// <summary>是否使用内置模拟数据源（无游戏时调试用）。</summary>
    public bool UseMockSource { get; init; }

    /// <summary>游戏本机接口端口。</summary>
    public int Port { get; init; } = 8111;

    /// <summary><c>/state</c> 轮询间隔（毫秒）。</summary>
    public int StateIntervalMs { get; init; } = 150;

    /// <summary><c>/indicators</c> 轮询间隔（毫秒）。</summary>
    public int IndicatorsIntervalMs { get; init; } = 250;

    /// <summary>已勾选显示的字段 Id 集合。</summary>
    public List<string> SelectedFieldIds { get; init; } = [];

    /// <summary>
    /// 是否允许联网从官方 wiki 抓取未收录载具的参数。
    /// <para>
    /// 关掉后纯离线运行：只用内置库 + 本地缓存，抓不到就退回兵种缺省阈值。
    /// </para>
    /// </summary>
    public bool OnlineVehicleLookup { get; init; } = true;

    /// <summary>主窗口位置与尺寸。</summary>
    public WindowBounds? MainWindowBounds { get; init; }

    /// <summary>悬浮窗设置。</summary>
    public HudSettings Hud { get; init; } = new();

    /// <summary>武器发射参数悬浮区设置。</summary>
    public WeaponHudSettings Weapon { get; init; } = new();

    /// <summary>
    /// 取勾选字段集合；未初始化时返回默认的 7 项。
    /// </summary>
    public IReadOnlySet<string> EffectiveSelectedFieldIds()
    {
        if (SelectedFieldIds.Count == 0)
        {
            return FieldCatalog.DefaultSelectedIds;
        }

        return new HashSet<string>(SelectedFieldIds, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>窗口位置与尺寸。</summary>
public sealed record WindowBounds
{
    /// <summary>左上角 X。</summary>
    public int X { get; init; }

    /// <summary>左上角 Y。</summary>
    public int Y { get; init; }

    /// <summary>宽度。</summary>
    public int Width { get; init; } = 1280;

    /// <summary>高度。</summary>
    public int Height { get; init; } = 860;
}

/// <summary>悬浮窗设置。</summary>
public sealed record HudSettings
{
    /// <summary>位置与尺寸。</summary>
    public WindowBounds? Bounds { get; init; }

    /// <summary>字号（像素）。</summary>
    public int FontSize { get; init; } = 18;

    /// <summary>
    /// 悬浮窗专用字段勾选；为空时回退到主设置的 <see cref="AppSettings.SelectedFieldIds"/>。
    /// <para>
    /// 悬浮窗和仪表盘的使用场景差别很大 —— 悬浮窗只放几项最关键的，
    /// 仪表盘则可以铺开看。因此两者分开配置。
    /// </para>
    /// </summary>
    public List<string> FieldIds { get; init; } = [];

    /// <summary>文字颜色（#AARRGGBB）。</summary>
    /// <remarks>
    /// <b>默认全黑</b>：用户要在游戏亮色场景（雪地、云层、白昼天空）上看清楚，
    /// 黑字最稳。想要荧光色在设置页改即可。
    /// </remarks>
    public string ForegroundColor { get; init; } = "#FF000000";

    /// <summary>数值文字颜色（#AARRGGBB）。为空时跟随 <see cref="ForegroundColor"/>。</summary>
    public string ValueColor { get; init; } = "#FF000000";

    /// <summary>背景不透明度 0~1。0 为全透明（连背景框一起隐藏）。</summary>
    public double BackgroundOpacity { get; init; } = 0.45;

    /// <summary>背景色（#RRGGBB）。</summary>
    public string BackgroundColor { get; init; } = "#FF000000";

    /// <summary>是否显示面板边框与圆角背景。</summary>
    /// <remarks>
    /// <b>默认 false</b>：用户要的是「文字直接浮在画面上」，不要任何窗口感。
    /// 想要背景板的用户在设置页打开即可。
    /// </remarks>
    public bool ShowPanelBackground { get; init; }

    /// <summary>文字描边（浅色主题下用描边保证游戏画面上可读）。</summary>
    /// <remarks>
    /// 纯文字无背景时这项更重要 —— 碰到云、雪地这类亮背景，
    /// 没有描边的浅色字会糊掉。
    /// </remarks>
    public bool TextShadow { get; init; } = true;

    /// <summary>是否显示右下角的拉伸手柄提示。默认关闭，避免边框感。</summary>
    public bool ShowResizeHint { get; init; }

    /// <summary>是否锁定（锁定后鼠标穿透）。</summary>
    public bool Locked { get; init; }

    /// <summary>行间紧凑模式（关闭行间距，多塞几行）。</summary>
    public bool CompactRows { get; init; }

    /// <summary>启动时自动打开悬浮窗。</summary>
    public bool OpenOnStartup { get; init; } = true;

    /// <summary>未单独配置悬浮窗字段时，取哪一份字段作为默认。</summary>
    public IReadOnlySet<string> EffectiveFieldIds(IReadOnlySet<string> fallback)
    {
        return FieldIds.Count == 0
            ? fallback
            : new HashSet<string>(FieldIds, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 武器发射参数悬浮区的设置。
/// <para>
/// 与 <see cref="HudSettings"/> 相互独立：玩家可以把遥测放左上、
/// 武器参数放右下，各自调外观与位置。
/// </para>
/// </summary>
public sealed record WeaponHudSettings
{
    /// <summary>位置与尺寸。</summary>
    public WindowBounds? Bounds { get; init; }

    /// <summary>字号（像素）。</summary>
    public int FontSize { get; init; } = 16;

    /// <summary>是否显示卡片背景。默认关，保持纯文字浮层。</summary>
    public bool ShowPanelBackground { get; init; }

    /// <summary>背景不透明度 0~1。</summary>
    public double BackgroundOpacity { get; init; } = 0.40;

    /// <summary>背景色（#RRGGBB）。</summary>
    public string BackgroundColor { get; init; } = "#FF000000";

    /// <summary>
    /// 是否跟随遥测悬浮窗的配色。
    /// <para>
    /// <b>默认 true</b>：两个悬浮窗在同一屏上并排显示，配色不一致会显得很乱。
    /// 玩家在设置页调一次主悬浮窗颜色，武器区自动同步，不用调两遍。
    /// 需要单独配色时把这项关掉即可。
    /// </para>
    /// </summary>
    public bool UseHudColors { get; init; } = true;

    /// <summary>标签文字颜色。默认全黑，详见 <see cref="HudSettings.ForegroundColor"/>。</summary>
    public string ForegroundColor { get; init; } = "#FF000000";

    /// <summary>数值文字颜色。默认全黑。</summary>
    public string ValueColor { get; init; } = "#FF000000";

    /// <summary>是否显示本机速度/高度/过载这三行。</summary>
    public bool ShowOwnState { get; init; } = true;

    /// <summary>是否显示右下角拉扯提示。默认关。</summary>
    public bool ShowResizeHint { get; init; }

    /// <summary>是否锁定（锁定后鼠标穿透）。</summary>
    public bool Locked { get; init; }

    /// <summary>
    /// 强制指定导弹型号的 Id。为空时自动从游戏挂载识别。
    /// <para>
    /// 有些挂载名游戏给得很模糊（或接口没暴露），自动识别会失败；
    /// 这时玩家可以手动指定，保证面板能用。
    /// </para>
    /// </summary>
    public string? ForcedMissileId { get; init; }

    /// <summary>手动输入的目标距离（m）。8111 接口不提供目标数据，只能手填。</summary>
    public double? TargetDistanceM { get; init; }

    /// <summary>手动输入的目标速度（km/h）。</summary>
    public double? TargetSpeedKmh { get; init; }

    /// <summary>手动输入的目标高度（m）。</summary>
    public double? TargetAltitudeM { get; init; }

    /// <summary>交战态势：<c>head-on</c> / <c>tail-on</c> / <c>side-on</c>。</summary>
    public string? TargetAspect { get; init; }

    /// <summary>启动时自动打开武器参数悬浮区。</summary>
    public bool OpenOnStartup { get; init; } = true;
}

/// <summary>
/// 配置持久化。
/// <para>
/// 写到 <c>%LocalAppData%\WarThunderTelemetry\settings.json</c>。
/// <b>不可</b>使用 <c>ApplicationData.Current</c> —— 那是打包应用专属 API，
/// 非打包模式下会抛异常。
/// </para>
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;

    private AppSettingsStore(string filePath, AppSettings settings)
    {
        _filePath = filePath;
        Current = settings;
    }

    /// <summary>当前配置。</summary>
    public AppSettings Current { get; private set; }

    /// <summary>配置文件路径。</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 加载配置；文件不存在或损坏时返回默认配置。
    /// </summary>
    public static AppSettingsStore Load()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WarThunderTelemetry");

        var filePath = Path.Combine(directory, "settings.json");
        AppSettings settings;

        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
            }
        }
        catch (Exception)
        {
            // 配置损坏不应导致应用无法启动，退回默认值。
            settings = new AppSettings();
        }

        return new AppSettingsStore(filePath, settings);
    }

    /// <summary>保存配置。</summary>
    public void Save()
    {
        Save(Current);
    }

    /// <summary>是否允许写盘。<c>false</c> 时 <see cref="Save(AppSettings)"/> 只更新内存。</summary>
    /// <remarks>
    /// 自检模式（<c>--selfcheck</c>）会逐个触发设置页的处理器来验证它们有响应，
    /// 而这些处理器全都会调 <see cref="Save(AppSettings)"/> ——
    /// 结果是自检把用户的字段勾选、悬浮窗尺寸、配色全部改成自检过程中的随机状态。
    /// 这个开关让自检既能验证「处理器被正确接线」，又完全不碰用户配置。
    /// </remarks>
    public bool PersistenceEnabled { get; set; } = true;

    /// <summary>保存指定配置。</summary>
    public void Save(AppSettings settings)
    {
        Current = settings;

        // 自检模式：只更新内存，绝不落盘。
        if (!PersistenceEnabled)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception)
        {
            // 写盘失败不影响运行。
        }
    }

    /// <summary>
    /// 按配置创建数据源。
    /// <para>
    /// 关键行为：勾了「模拟数据源」但端口起不来（典型是真实游戏正占着 8111），
    /// 这里<b>自动回退到真实数据源</b>，而不是让用户卡在假数据里。
    /// 模拟源存在的意义只是「没游戏时能调试」——游戏真在跑的时候，
    /// 用户要的显然是游戏数据。
    /// </para>
    /// </summary>
    public static ITelemetrySource CreateSource(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.UseMockSource)
        {
            var mock = new MockTelemetryServer(settings.Port);

            // MockTelemetryServer.Start() 内部已改为不抛异常，失败时置 StartupError。
            // 这里先启动探一下：起不来就直接换成真实源。
            mock.Start();
            if (!mock.HasStartupError)
            {
                return mock;
            }

            _ = mock.DisposeAsync();
            LastSourceFallbackReason =
                $"模拟数据源无法监听 {settings.Port} 端口（多半是游戏正在运行），已自动切换为读取真实接口。";
        }
        else
        {
            LastSourceFallbackReason = null;
        }

        return new HttpTelemetrySource(new TelemetryClientOptions
        {
            Port = settings.Port,
            Intervals = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [TelemetryEndpoint.State] = Math.Max(50, settings.StateIntervalMs),
                [TelemetryEndpoint.Indicators] = Math.Max(50, settings.IndicatorsIntervalMs),
                [TelemetryEndpoint.MapInfo] = 1000,
            },
        });
    }

    /// <summary>
    /// 最近一次创建数据源时发生的自动回退原因；<c>null</c> 表示无异常。
    /// 供状态栏提示用户「为什么配置里勾着模拟源，却在读真实数据」。
    /// </summary>
    public static string? LastSourceFallbackReason { get; private set; }
}
