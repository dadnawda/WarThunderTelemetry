using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WarThunderTelemetry.App.Hud;
using WarThunderTelemetry.App.Services;
using WarThunderTelemetry.Core;

namespace WarThunderTelemetry.App;

/// <summary>
/// 应用入口。
/// <para>
/// 非打包模式下无需写 Windows App SDK 引导代码：
/// csproj 中 <c>WindowsPackageType=None</c> 会让 SDK 自动注入 Bootstrap 初始化。
/// </para>
/// </summary>
public partial class App : Application
{
    /// <summary>主窗口。</summary>
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>悬浮窗（原生分层窗口实现）。</summary>
    public static NativeHudWindow? HudWindow { get; private set; }

    /// <summary>武器发射参数悬浮区（原生分层窗口实现，独立的第二个悬浮窗）。</summary>
    public static NativeWeaponHudWindow? WeaponHudWindow { get; private set; }

    /// <summary>全局遥测状态（Core 层）。</summary>
    public static TelemetryState? Telemetry { get; private set; }

    /// <summary>配置存储。</summary>
    public static AppSettingsStore? Settings { get; private set; }

    /// <summary>
    /// 悬浮窗刷新定时器。
    /// <para>
    /// 原生分层窗口不会自己刷新 —— 它只是一张位图，必须由我们定时重绘。
    /// 间隔取 100ms：肉眼看上去连续，又不会因为每帧新建 GDI 画布而吃满 CPU。
    /// </para>
    /// </summary>
    private static DispatcherQueueTimer? _hudTimer;

    /// <summary>崩溃日志落盘位置（非打包应用无控制台，只能写文件）。</summary>
    private static readonly string CrashLogPath = Path.Combine(
        AppContext.BaseDirectory, "crash.log");

    /// <summary>构造。</summary>
    public App()
    {
        // 越早挂越好：OnLaunched 里的异常也要能抓住。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain", e.ExceptionObject as Exception);

        UnhandledException += (_, e) =>
        {
            LogCrash("XamlUnhandled", e.Exception);
            e.Handled = false;
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("Task", e.Exception);
            e.SetObserved();
        };

        try
        {
            InitializeComponent();

            // 强制浅色主题，不跟随系统（主窗口用；悬浮窗是原生窗口，不受影响）。
            RequestedTheme = ApplicationTheme.Light;
        }
        catch (Exception ex)
        {
            LogCrash("AppCtor", ex);
            throw;
        }
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            OnLaunchedCore(args);
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
            throw;
        }
    }

    private void OnLaunchedCore(LaunchActivatedEventArgs args)
    {
        if (IsSelfCheckMode())
        {
            RunSelfCheck();
            return;
        }

        Settings = AppSettingsStore.Load();

        // 数据源：默认连真实游戏；也可通过配置或命令行切到模拟源。
        var source = AppSettingsStore.CreateSource(Settings.Current);

        // 载具参数：内置库 + 缓存 + 需要时联网抓未收录的机型。
        var vehicles = new Core.Vehicles.OnlineVehicleResolver
        {
            OnlineLookupEnabled = Settings.Current.OnlineVehicleLookup,
        };

        Telemetry = new TelemetryState(source, vehicles);

        MainWindow = new MainWindow();
        MainWindow.Activate();

        Telemetry.Start();

        // 按配置自动打开悬浮窗（用户上次关掉就不自动开）。
        if (Settings.Current.Hud.OpenOnStartup)
        {
            ShowHud();
        }

        if (Settings.Current.Weapon.OpenOnStartup)
        {
            ShowWeaponHud();
        }

        StartHudTimer();
    }

    /// <summary>
    /// 开始定时刷新悬浮窗。
    /// <para>
    /// 原生分层窗口没有自己的渲染循环，必须由外部驱动：
    /// 每次把最新遥测推给两个窗口，它们各自重绘位图并提交。
    /// </para>
    /// </summary>
    private static void StartHudTimer()
    {
        if (_hudTimer is not null)
        {
            return;
        }

        var queue = MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        _hudTimer = queue.CreateTimer();
        _hudTimer.Interval = TimeSpan.FromMilliseconds(100);
        _hudTimer.Tick += (_, _) => RefreshHudWindows();
        _hudTimer.Start();
    }

    /// <summary>把最新遥测推给两个悬浮窗并重绘。</summary>
    private static void RefreshHudWindows()
    {
        var status = Telemetry?.Current;

        HudWindow?.Update(status);
        WeaponHudWindow?.Rebuild();
    }

    /// <summary>
    /// 自检模式：只创建悬浮窗、验证属性、打印结果，不开主窗口也不连游戏。
    /// </summary>
    private void RunSelfCheck()
    {
        Settings = AppSettingsStore.Load();

        // 自检会触发设置页的全部处理器，那些处理器都会保存配置。
        // 关掉落盘，避免把用户真实的字段勾选/悬浮窗位置改掉。
        Settings.PersistenceEnabled = false;

        // 带 PagesFlag 时额外跑一遍页面导航冒烟测试。
        var withPages = Environment.GetCommandLineArgs().Contains(
            HudSelfCheck.PagesFlag, StringComparer.OrdinalIgnoreCase);

        MainWindow = new MainWindow();
        MainWindow.Activate();

        HudWindow = new NativeHudWindow();

        // 武器悬浮区也一起自检：它是这一版新增的窗口，属性必须和遥测悬浮窗对齐。
        WeaponHudWindow = new NativeWeaponHudWindow();

        _ = withPages
            ? RunSelfCheckWithPagesAsync(MainWindow, HudWindow, WeaponHudWindow)
            : RunSelfCheckAllAsync(HudWindow, WeaponHudWindow);
    }

    private static async Task RunSelfCheckAllAsync(
        NativeHudWindow hud, NativeWeaponHudWindow weapon)
    {
        await HudSelfCheck.RunAsync(hud);
        await HudSelfCheck.RunWeaponAsync(weapon);
    }

    private static async Task RunSelfCheckWithPagesAsync(
        MainWindow window, NativeHudWindow hud, NativeWeaponHudWindow weapon)
    {
        await HudSelfCheck.RunPageSmokeTestAsync(window);
        await HudSelfCheck.RunSettingsInteractionTestAsync(window);
        await HudSelfCheck.RunAsync(hud);
        await HudSelfCheck.RunWeaponAsync(weapon);
    }

    /// <summary>是否处于 <c>--selfcheck</c> 自检模式。</summary>
    public static bool IsSelfCheckMode() =>
        Environment.GetCommandLineArgs().Contains(
            HudSelfCheck.Flag, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 把异常写到 EXE 同目录的 crash.log —— 非打包 WinExe 没有控制台，
    /// 不落盘就什么都看不到。
    /// </summary>
    internal static void LogCrash(string stage, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] stage={stage}");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");

            // 附上 HRESULT，方便对照 Win32 错误码。
            if (ex is COMException com)
            {
                sb.AppendLine($"HRESULT = 0x{com.HResult:X8}");
            }

            sb.AppendLine(new string('-', 60));
            File.AppendAllText(CrashLogPath, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // 日志失败也不能影响主流程。
        }
    }

    /// <summary>供自检读取的崩溃日志路径。</summary>
    public static string CrashLogFile => CrashLogPath;

    /// <summary>主窗口的 UI 线程调度器（原生悬浮窗需要在 UI 线程上创建）。</summary>
    internal static DispatcherQueue? UiQueue =>
        MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();

    /// <summary>
    /// 创建或显示悬浮窗。
    /// </summary>
    public static void ShowHud()
    {
        if (HudWindow is not null)
        {
            HudWindow.SetVisible(true);
            return;
        }

        HudWindow = new NativeHudWindow();
    }

    /// <summary>
    /// 关闭悬浮窗。
    /// </summary>
    public static void CloseHud()
    {
        HudWindow?.Dispose();
        HudWindow = null;
    }

    /// <summary>
    /// 字段勾选变化后让悬浮窗重建行布局。
    /// </summary>
    public static void RefreshHudLayout()
    {
        HudWindow?.RebuildRows();
        HudWindow?.RefreshAppearance();
    }

    /// <summary>
    /// 创建或显示武器参数悬浮区。
    /// </summary>
    public static void ShowWeaponHud()
    {
        if (WeaponHudWindow is not null)
        {
            WeaponHudWindow.SetVisible(true);
            return;
        }

        WeaponHudWindow = new NativeWeaponHudWindow();
    }

    /// <summary>
    /// 关闭武器参数悬浮区。
    /// </summary>
    public static void CloseWeaponHud()
    {
        WeaponHudWindow?.Dispose();
        WeaponHudWindow = null;
    }
}
