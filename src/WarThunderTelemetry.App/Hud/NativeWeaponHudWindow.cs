using System.Globalization;
using WarThunderTelemetry.App.Native;
using WarThunderTelemetry.App.Services;
using WarThunderTelemetry.Core;
using WarThunderTelemetry.Core.Parsing;
using WarThunderTelemetry.Core.Weapons;

namespace WarThunderTelemetry.App.Hud;

/// <summary>
/// 武器发射参数悬浮区（原生分层窗口实现）。
/// <para>
/// 与遥测悬浮窗是<b>两个独立窗口</b>：位置、外观、开关各自独立，
/// 玩家可以把遥测放在左上、武器参数放在右下，互不遮挡。
/// </para>
/// <para>
/// 显示内容：当前选中的导弹、剩余弹药、根据「本机当前速度/高度/过载」
/// 算出的发射建议与射程区间。数据全部本地计算，不联网。
/// </para>
/// <para>
/// <b>为什么改用原生分层窗口：</b>WinUI 3 非打包窗口在浅色主题下会被 DWM
/// 铺一层白色宿主背景，它位于 XAML 内容之下，改 XAML 去不掉。
/// 原生方案自己用 GDI 画到离屏位图，再 <c>UpdateLayeredWindow</c> 提交，
/// 每个像素的 alpha 完全由我们掌控，透明由操作系统保证，中间无宿主层。
/// </para>
/// <para>
/// <b>关于目标信息</b>：8111 接口不提供雷达锁定目标的数据。
/// 因此默认只显示「本弹在当前状态下的射程区间」与发射条件检查；
/// 玩家在设置页填入目标距离/速度/态势后，才会给出精确到
/// 「可发射 / 太远 / 太近」的判断。
/// </para>
/// </summary>
public sealed class NativeWeaponHudWindow : IDisposable
{
    // ---- 交互状态 ----

    private readonly LayeredWindow _window = new();
    private readonly WeaponStatusService _service;

    private WeaponStatus _latest = WeaponStatus.Disconnected;

    private bool _locked;
    private bool _dragging;
    private bool _resizing;
    private bool _userResized;
    private bool _disposed;

    private int _windowX;
    private int _windowY;
    private int _windowW = 300;
    private int _windowH = 240;

    /// <summary>拖动时鼠标相对窗口左上角的偏移（窗口坐标）。</summary>
    private int _dragOffsetX;
    private int _dragOffsetY;

    /// <summary>拉伸起点（窗口坐标）与起始窗口矩形。</summary>
    private int _resizeStartX;
    private int _resizeStartY;
    private int _resizeStartW;
    private int _resizeStartH;
    private int _resizeStartWindowX;
    private int _resizeStartWindowY;

    /// <summary>拉伸边（位标志，见 <see cref="Edge"/>）。</summary>
    private int _activeEdge;

    /// <summary>当前鼠标所在边（未按下时用于切换光标）。</summary>
    private int _hoverEdge;

    [Flags]
    private enum Edge
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
    }

    private const int ResizeMargin = 8;

    /// <summary>锁定态下仍保留的可点击边缘带宽度（逃生口，见 <see cref="LayeredWindow.HookRing"/>）。</summary>
    private const int UnlockRing = 8;

    private const int MinWidth = 160;
    private const int MinHeight = 60;

    /// <summary>窗口是否已创建。</summary>
    public bool IsCreated => _window.IsCreated;

    /// <summary>窗口句柄。</summary>
    public nint Handle => _window.Handle;

    /// <summary>当前是否鼠标穿透。</summary>
    public bool IsClickThrough => _window.IsClickThrough;

    /// <summary>当前是否锁定。</summary>
    public bool IsLocked => _locked;

    /// <summary>构造并创建窗口。</summary>
    public NativeWeaponHudWindow()
    {
        _service = new WeaponStatusService(
            App.Telemetry?.Resolver ?? FieldCatalog.CreateResolver());

        var settings = App.Settings?.Current.Weapon ?? new WeaponHudSettings();

        if (settings.Bounds is { } b && b.Width > 0 && b.Height > 0)
        {
            // 恢复上次位置。必须做一次可见性校正：显示器换了、分辨率变了、
            // 或者用户把窗口拖到屏幕外，保存的坐标就会落在看不见的地方，
            // 表现为「窗口消失了、拖也拖不到」。
            (_windowX, _windowY, _windowW, _windowH) = ClampToWorkArea(
                b.X, b.Y, b.Width, b.Height);

            _userResized = true;
        }
        else
        {
            (_windowX, _windowY, _windowW, _windowH) = DefaultBounds();
        }

        _window.Create(_windowX, _windowY, _windowW, _windowH, "WarThunderTelemetry.WeaponHud");

        // 锁定时保留一圈可点击边带，双击它即可解锁 —— 逃生口。
        _window.HookRing = UnlockRing;

        _locked = settings.Locked;
        _window.SetClickThrough(_locked);

        _window.PointerPressed += OnPointerPressed;
        _window.PointerMoved += OnPointerMoved;
        _window.PointerReleased += OnPointerReleased;
        _window.DoubleClicked += OnDoubleClicked;

        // 首次解算 + 绘制。注意这里**不能**走 ResizeToContent 的落盘分支 ——
        // 那会把用户辛苦调好的位置尺寸在每次启动时重新写一遍。
        _latest = Compute(settings);

        if (!_userResized)
        {
            ResizeToContent(settings);
        }

        Render(settings);
    }

    /// <summary>
    /// 把保存的窗口矩形校正到主显示器可见范围内。
    /// <para>
    /// 至少要保证标题区域（左上角一块）落在工作区里，
    /// 否则用户根本看不到窗口在哪，也就无从拖动。
    /// </para>
    /// </summary>
    private static (int X, int Y, int W, int H) ClampToWorkArea(
        int x, int y, int width, int height)
    {
        var (screenW, screenH) = WindowMetrics.PrimaryWorkArea();

        width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, screenW));
        height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, screenH));

        // 保证窗口至少有一部分留在屏幕内（左右各留 80px 抓取区）。
        var minVisible = 80;
        var maxX = screenW - minVisible;
        var maxY = screenH - minVisible;

        x = Math.Clamp(x, -(width - minVisible), maxX);
        y = Math.Clamp(y, 0, maxY);

        return (x, y, width, height);
    }

    /// <summary>默认位置：主屏右侧偏下，与遥测悬浮窗（左上）分开，互不遮挡。</summary>
    private static (int X, int Y, int W, int H) DefaultBounds()
    {
        var (screenW, screenH) = WindowMetrics.PrimaryWorkArea();

        return (
            Math.Max(0, screenW - 320),
            Math.Max(0, screenH - 300),
            300,
            240);
    }

    // ================= 数据 =================

    /// <summary>
    /// 重新解算并重绘一次。
    /// <para>
    /// 每次都从配置里重读手动目标／强制导弹并重新解算 ——
    /// 本机速度/高度/过载一直在变，射程区间也应该跟着变。
    /// </para>
    /// </summary>
    /// <param name="force">是否强制重绘（即使内容看起来没变）。</param>
    public void Rebuild(bool force = false)
    {
        var settings = App.Settings?.Current.Weapon ?? new WeaponHudSettings();

        _latest = Compute(settings);

        // 用户正在拖动或拉伸时，绝不自动改尺寸 ——
        // 定时器每 100ms 调一次 Rebuild，若不拦住会把用户刚拉的尺寸覆盖掉，
        // 直观感受就是「怎么调都调不动」。只重绘，不重算尺寸。
        if (_dragging || _resizing)
        {
            Render(settings);
            return;
        }

        if (force || !_userResized)
        {
            ResizeToContent(settings);
        }

        Render(settings);
    }

    /// <summary>解算一次武器状态。</summary>
    private WeaponStatus Compute(WeaponHudSettings settings)
    {
        var manualTarget = settings.TargetDistanceM is null &&
                           settings.TargetSpeedKmh is null &&
                           settings.TargetAltitudeM is null
            ? null
            : new TargetInput
            {
                DistanceM = settings.TargetDistanceM,
                SpeedKmh = settings.TargetSpeedKmh,
                AltitudeM = settings.TargetAltitudeM,
                Aspect = ParseAspect(settings.TargetAspect),
            };

        var manualMissile = string.IsNullOrWhiteSpace(settings.ForcedMissileId)
            ? null
            : MissileDatabase.Profiles.FirstOrDefault(p =>
                string.Equals(p.Id, settings.ForcedMissileId, StringComparison.OrdinalIgnoreCase));

        return _service.Build(App.Telemetry?.Current.Snapshot, manualMissile, manualTarget);
    }

    // ================= 渲染 =================

    /// <summary>重绘整块悬浮窗。</summary>
    public void Render(WeaponHudSettings? settingsOverride = null)
    {
        if (!_window.IsCreated)
        {
            return;
        }

        var settings = settingsOverride ?? App.Settings?.Current.Weapon ?? new WeaponHudSettings();

        var width = Math.Max(1, _windowW);
        var height = Math.Max(1, _windowH);

        using var canvas = new Canvas(width, height);

        // 配色：默认跟随遥测悬浮窗，保证同屏两个悬浮窗看起来是一套东西。
        var (fgHex, valueHex, bgHex, bgOpacity, showPanel) = ResolveAppearance(settings);

        var textColor = ColorUtil.ParseRgb(fgHex, (0, 0, 0));
        var valueColor = string.IsNullOrWhiteSpace(valueHex)
            ? textColor
            : ColorUtil.ParseRgb(valueHex, textColor);

        var showBackground = showPanel && bgOpacity > 0.001;
        if (showBackground)
        {
            var (br, bg, bb) = ColorUtil.ParseRgb(bgHex, (0, 0, 0));
            var ba = (byte)Math.Round(Math.Clamp(bgOpacity, 0, 1) * 255);
            canvas.FillRect(0, 0, width, height, br, bg, bb, ba);
        }

        // 细边框：明确「窗口边界在哪」，否则纯文字浮层看不出可操作范围，
        // 拉伸时找不到边缘、拖动时也容易误判。
        // 鼠标悬停在某条边附近时，那条边会加亮，提示「这里可以拉」。
        DrawResizeBorder(canvas, width, height, showBackground);

        var baseFont = Math.Max(9, settings.FontSize);
        var adviceFont = Math.Max(10, settings.FontSize - 3);
        var detailFont = Math.Max(10, settings.FontSize - 4);

        // 没有背景板时必须描边，否则亮场景（雪地/云）上字会糊掉。
        var outline = !showBackground;

        var padX = 6;
        var padY = 4;

        var y = padY;

        // ---- 第 1 行：武器名 + 弹药 ----
        var weaponName = _latest.WeaponName ?? _latest.Missile?.DisplayName ?? "未挂载";

        canvas.DrawText(
            weaponName,
            padX, y,
            baseFont, true,
            textColor.R, textColor.G, textColor.B, 255,
            outline: outline);

        if (_latest.Ammo is { } ammo)
        {
            var ammoText = "×" + ammo.ToString("F0", CultureInfo.InvariantCulture);
            var ammoColumn = (int)(baseFont * 3.4);

            canvas.DrawText(
                ammoText,
                width - padX - ammoColumn, y,
                baseFont, true,
                valueColor.R, valueColor.G, valueColor.B, 255,
                outline: outline,
                rightAlign: true, maxWidth: ammoColumn);
        }

        y += (int)Math.Ceiling(baseFont * 1.5);

        // ---- 第 2 行：建议（按结论分级着色）----
        var advice = string.IsNullOrEmpty(_latest.Message) ? "—" : _latest.Message;
        var (ar, ag, ab) = _latest.Solution is { } sol
            ? VerdictColor(sol.Verdict)
            : textColor;

        canvas.DrawText(
            advice,
            padX, y,
            adviceFont, true,
            ar, ag, ab, 255,
            outline: outline);

        y += (int)Math.Ceiling(adviceFont * 1.55);

        // ---- 未解算出包线：给出提示就结束 ----
        if (_latest.Solution is not { } solution)
        {
            if (_latest.IsConnected && y + adviceFont < height)
            {
                canvas.DrawText(
                    "在设置页可手动指定导弹型号与目标距离。",
                    padX, y,
                    detailFont, false,
                    textColor.R, textColor.G, textColor.B, 190);
            }

            canvas.Present(_window, _windowX, _windowY);
            return;
        }

        // ---- 射程区间 4 行 ----
        var rowHeight = (int)Math.Ceiling(detailFont * 1.55);
        var valueColumn = (int)(detailFont * 6.2);

        y = DrawRow(canvas, "不可逃逸区", Km(solution.NoEscapeRange),
            padX, y, width, rowHeight, detailFont, valueColumn,
            textColor, valueColor, outline, highlight: solution.InNoEscapeZone);

        y = DrawRow(canvas, "有效射程", Km(solution.EffectiveRange),
            padX, y, width, rowHeight, detailFont, valueColumn,
            textColor, valueColor, outline, highlight: false);

        y = DrawRow(canvas, "最小射程", Km(solution.MinRange),
            padX, y, width, rowHeight, detailFont, valueColumn,
            textColor, valueColor, outline, highlight: false);

        y = DrawRow(canvas, "命中时间", solution.TimeToImpact is { } t
                ? t.ToString("F1", CultureInfo.InvariantCulture) + " s"
                : "—",
            padX, y, width, rowHeight, detailFont, valueColumn,
            textColor, valueColor, outline, highlight: false);

        // ---- 本机状态 3 行 ----
        if (settings.ShowOwnState)
        {
            y = DrawRow(canvas, "本机速度", _latest.OwnSpeedKmh is { } sp
                    ? sp.ToString("F0", CultureInfo.InvariantCulture) + " km/h"
                    : "—",
                padX, y, width, rowHeight, detailFont, valueColumn,
                textColor, valueColor, outline, highlight: false);

            y = DrawRow(canvas, "本机高度", _latest.OwnAltitudeM is { } al
                    ? al.ToString("F0", CultureInfo.InvariantCulture) + " m"
                    : "—",
                padX, y, width, rowHeight, detailFont, valueColumn,
                textColor, valueColor, outline, highlight: false);

            // 过载超限时标红 —— 这是最容易忽略的发射否决条件。
            y = DrawRow(canvas, "本机过载", _latest.OwnLoadG is { } g
                    ? g.ToString("F1", CultureInfo.InvariantCulture) + " g"
                    : "—",
                padX, y, width, rowHeight, detailFont, valueColumn,
                textColor, valueColor, outline, highlight: !solution.LoadWithinLimit);
        }

        // ---- 提示行 ----
        var hint = BuildHint(solution);
        if (!string.IsNullOrEmpty(hint) && y + rowHeight <= height)
        {
            canvas.DrawText(
                hint,
                padX, y,
                Math.Max(9, detailFont - 1), false,
                textColor.R, textColor.G, textColor.B, 185);
        }

        // GDI 画字不写 alpha 通道，必须据绘制结果补上，
        // 否则分层窗口会把文字当成全透明，字就看不见了。
        canvas.Present(_window, _windowX, _windowY);
    }

    /// <summary>
    /// 解出实际使用的外观参数。
    /// <para>
    /// <see cref="WeaponHudSettings.UseHudColors"/> 为 true（默认）时，
    /// 完全跟随遥测悬浮窗 —— 同屏两个悬浮窗看起来就是一套配色，
    /// 玩家改一次主悬浮窗即可，不必调两遍。
    /// </para>
    /// </summary>
    private static (string Fg, string Value, string Bg, double Opacity, bool ShowPanel)
        ResolveAppearance(WeaponHudSettings weapon)
    {
        if (weapon.UseHudColors)
        {
            var hud = App.Settings?.Current.Hud ?? new HudSettings();
            return (
                hud.ForegroundColor,
                hud.ValueColor,
                hud.BackgroundColor,
                hud.BackgroundOpacity,
                hud.ShowPanelBackground);
        }

        return (
            weapon.ForegroundColor,
            weapon.ValueColor,
            weapon.BackgroundColor,
            weapon.BackgroundOpacity,
            weapon.ShowPanelBackground);
    }

    /// <summary>当前生效的「是否有背景板」——描边、边框浓淡都要跟着它走。</summary>
    private static bool EffectiveShowPanel(WeaponHudSettings settings) =>
        ResolveAppearance(settings).ShowPanel;

    /// <summary>
    /// 画一圈细边框，作为「可拉伸边界」的可视提示。
    /// <para>
    /// 平时是一圈很淡的灰线（不遮挡游戏画面，但能看出窗口范围）；
    /// 鼠标靠近某条边时，那条边换成亮青色，明确提示「这里可以拖」。
    /// 锁定时全不画 —— 锁定状态下窗口本来就不可交互，画了反而误导。
    /// </para>
    /// </summary>
    private void DrawResizeBorder(Canvas canvas, int width, int height, bool hasBackground)
    {
        if (_locked)
        {
            return;
        }

        // 2px：1px 在高 DPI 缩放下几乎看不见，用户找不到可拉的边；
        // 2px 既能看清又不至于喧宾夺主。
        const int t = 2;

        // 有背景板时边框可以更淡（背景本身已界定范围）；纯文字时稍亮一点。
        var baseRgb = hasBackground
            ? ((byte)120, (byte)120, (byte)120)
            : ((byte)150, (byte)150, (byte)150);

        var baseOpacity = hasBackground ? 0.55 : 0.45;

        // 四条边分别画，便于按悬停/拉伸状态单独加亮。
        // 注意按 ClientToScreen 口径，窗口客户区左上角对应 (0,0)，
        // 所以左右/上下边都朝窗口内侧画，与 HitTestEdge 的判定区间对齐。
        DrawEdge(canvas, Edge.Top, 0, 0, width, t, baseRgb, baseOpacity);
        DrawEdge(canvas, Edge.Bottom, 0, Math.Max(0, height - t), width, t, baseRgb, baseOpacity);
        DrawEdge(canvas, Edge.Left, 0, 0, t, height, baseRgb, baseOpacity);
        DrawEdge(canvas, Edge.Right, Math.Max(0, width - t), 0, t, height, baseRgb, baseOpacity);
    }

    /// <summary>画一条边；鼠标悬停或正在拉伸时换成亮青色高亮。</summary>
    private void DrawEdge(
        Canvas canvas, Edge edge, int x, int y, int w, int h,
        (byte R, byte G, byte B) baseRgb, double baseOpacity)
    {
        var active = _resizing
            ? ((Edge)_activeEdge).HasFlag(edge)
            : ((Edge)_hoverEdge).HasFlag(edge);

        if (active)
        {
            canvas.FillRect(x, y, w, h, 90, 220, 255, (byte)242);
            return;
        }

        canvas.FillRect(x, y, w, h, baseRgb.R, baseRgb.G, baseRgb.B,
            (byte)Math.Round(baseOpacity * 255));
    }

    /// <summary>画一行「名称（左对齐）+ 数值（右对齐）」。返回下一行的 y。</summary>
    private static int DrawRow(
        Canvas canvas,
        string label,
        string value,
        int padX,
        int y,
        int width,
        int rowHeight,
        int fontSize,
        int valueColumn,
        (byte R, byte G, byte B) textColor,
        (byte R, byte G, byte B) valueColor,
        bool outline,
        bool highlight)
    {
        canvas.DrawText(
            label,
            padX, y,
            fontSize, false,
            textColor.R, textColor.G, textColor.B, 255,
            outline: outline);

        var (vr, vg, vb) = highlight
            ? ((byte)255, (byte)96, (byte)96)
            : valueColor;

        canvas.DrawText(
            value,
            width - padX - valueColumn, y,
            fontSize, highlight,
            vr, vg, vb, 255,
            outline: outline,
            rightAlign: true, maxWidth: valueColumn);

        return y + rowHeight;
    }

    /// <summary>拼出底部提示：目标未知 / 用了估算值。</summary>
    private string BuildHint(LaunchSolution solution)
    {
        if (!_latest.TargetKnown)
        {
            return solution.HasEstimates
                ? "目标距离未知，仅按本机状态估算（部分参数为估算值）。"
                : "目标距离未知，仅按本机状态估算射程区间。";
        }

        return solution.HasEstimates ? "部分参数为估算值，仅供参考。" : string.Empty;
    }

    /// <summary>按结论给颜色：可发射绿、勉强黄、不可行红。</summary>
    private static (byte R, byte G, byte B) VerdictColor(LaunchVerdict verdict) => verdict switch
    {
        LaunchVerdict.Fire => (120, 235, 130),
        LaunchVerdict.Marginal => (255, 205, 90),
        LaunchVerdict.TooFar => (255, 150, 90),
        LaunchVerdict.TooClose => (255, 150, 90),
        LaunchVerdict.Inhibit => (255, 96, 96),
        _ => (200, 200, 200),
    };

    private static string Km(double meters) =>
        ((double)meters / 1000).ToString("F1", CultureInfo.InvariantCulture) + " km";

    private static EngagementAspect ParseAspect(string? raw) => raw switch
    {
        "head-on" => EngagementAspect.HeadOn,
        "tail-on" => EngagementAspect.TailOn,
        "side-on" => EngagementAspect.SideOn,
        _ => EngagementAspect.Unknown,
    };

    // ================= 尺寸 =================

    /// <summary>让窗口贴合内容高度。</summary>
    private void ResizeToContent(WeaponHudSettings settings)
    {
        var baseFont = Math.Max(9, settings.FontSize);
        var adviceFont = Math.Max(10, settings.FontSize - 3);
        var detailFont = Math.Max(10, settings.FontSize - 4);

        var contentHeight = 8
            + (int)Math.Ceiling(baseFont * 1.5)
            + (int)Math.Ceiling(adviceFont * 1.55);

        if (_latest.Solution is not null)
        {
            contentHeight += (int)Math.Ceiling(detailFont * 1.55) * 4;

            if (settings.ShowOwnState)
            {
                contentHeight += (int)Math.Ceiling(detailFont * 1.55) * 3;
            }

            if (!string.IsNullOrEmpty(BuildHint(_latest.Solution)))
            {
                contentHeight += (int)Math.Ceiling(Math.Max(9, detailFont - 1) * 1.55);
            }
        }
        else
        {
            contentHeight += (int)Math.Ceiling(detailFont * 1.55) * 2;
        }

        var (_, screenH) = WindowMetrics.PrimaryWorkArea();
        var maxHeight = (int)(screenH * 0.7);

        _windowH = Math.Max(MinHeight, Math.Min(contentHeight + 6, maxHeight));

        // 只在「用户没手动定过宽度」时才把宽度拉到内容所需的最小宽度。
        // 否则窄窗口永远拉不窄 —— 用户会感觉「横向拖不动」。
        _windowW = _userResized
            ? Math.Max(MinWidth, _windowW)
            : Math.Max(MinWidth, EstimateContentWidth(settings));

        _window.SetBounds(_windowX, _windowY, _windowW, _windowH);
        SaveBounds();
    }

    /// <summary>估算内容所需宽度。</summary>
    private int EstimateContentWidth(WeaponHudSettings settings)
    {
        var detailFont = Math.Max(10, settings.FontSize - 4);
        var baseFont = Math.Max(9, settings.FontSize);

        // 左侧最长标签 + 右侧数值列（至少 6 个字宽）。
        var labelWidth = Canvas.EstimateWidth("不可逃逸区", detailFont);
        var valueWidth = (int)(detailFont * 6.2);

        var headerWidth = Canvas.EstimateWidth("未挂载", baseFont) + (int)(baseFont * 3.4) + 16;

        var width = Math.Max(labelWidth + valueWidth + 24, headerWidth);
        return Math.Clamp(width, MinWidth, 620);
    }

    // ================= 交互 =================

    private void OnPointerPressed(LayeredWindow _, int x, int y)
    {
        if (_locked)
        {
            return;
        }

        // 命中判定前先把托管矩形同步成 HWND 的真实矩形。
        // 原生窗口的尺寸可能被系统或其它路径改过，一旦托管值落后，
        // HitTestEdge 就会算在错误的边界上 —— 表现正是「边缘抓不住、拉不动」。
        SyncBoundsFromWindow();

        // 边缘优先触发拉伸。
        var edge = HitTestEdge(x, y);
        if (edge != Edge.None)
        {
            _resizing = true;
            _activeEdge = (int)edge;
            _resizeStartX = x;
            _resizeStartY = y;
            _resizeStartW = _windowW;
            _resizeStartH = _windowH;
            _resizeStartWindowX = _windowX;
            _resizeStartWindowY = _windowY;
            return;
        }

        _dragging = true;
        _dragOffsetX = _windowX - x;
        _dragOffsetY = _windowY - y;
    }

    /// <summary>把托管的位置/尺寸字段同步成 HWND 当前的矩形。</summary>
    private void SyncBoundsFromWindow()
    {
        if (!_window.IsCreated)
        {
            return;
        }

        var (bx, by, bw, bh) = _window.GetBounds();
        if (bw <= 0 || bh <= 0)
        {
            return;
        }

        _windowX = bx;
        _windowY = by;
        _windowW = bw;
        _windowH = bh;
    }

    private void OnPointerMoved(LayeredWindow _, int x, int y)
    {
        if (_locked)
        {
            return;
        }

        if (_resizing)
        {
            ApplyResize(x, y);
            return;
        }

        if (_dragging)
        {
            var (screenW, screenH) = WindowMetrics.PrimaryWorkArea();

            // 拖动时不许把窗口推出屏幕：至少留 80px 抓得住，
            // 否则一旦拖出去就再也点不到了。
            var minVisible = 80;

            _windowX = Math.Clamp(
                _dragOffsetX + x, -( _windowW - minVisible), screenW - minVisible);
            _windowY = Math.Clamp(
                _dragOffsetY + y, 0, screenH - minVisible);

            _window.SetBounds(_windowX, _windowY, _windowW, _windowH);
            return;
        }

        // 悬停边变化时重绘一次，让那条边亮起来 ——
        // 否则鼠标移到边上没有任何反馈，用户不知道这里可以拉。
        var hover = (int)HitTestEdge(x, y);
        if (hover != _hoverEdge)
        {
            _hoverEdge = hover;
            Render();
        }
    }

    private void OnPointerReleased(LayeredWindow _, int x, int y)
    {
        if (_locked)
        {
            return;
        }

        if (_resizing)
        {
            _resizing = false;
            _activeEdge = 0;
            _userResized = true;
            SaveBounds();
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            SaveBounds();
        }
    }

    /// <summary>双击切换锁定（锁定后鼠标穿透，点击落到游戏里）。</summary>
    private void OnDoubleClicked(LayeredWindow _)
    {
        SetLocked(!_locked);
    }

    /// <summary>设置锁定状态（含鼠标穿透）。</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        _window.SetClickThrough(locked);

        var store = App.Settings;
        if (store is not null)
        {
            store.Save(store.Current with
            {
                Weapon = store.Current.Weapon with { Locked = locked },
            });
        }
    }

    private Edge HitTestEdge(int x, int y)
    {
        var (wx, wy, ww, wh) = (_windowX, _windowY, _windowW, _windowH);

        // 必须先确认鼠标在窗口范围内（含边缘外扩一点）。
        if (x < wx - 2 || x > wx + ww + 2 || y < wy - 2 || y > wy + wh + 2)
        {
            return Edge.None;
        }

        var edge = Edge.None;

        if (x >= wx && x <= wx + ResizeMargin)
        {
            edge |= Edge.Left;
        }

        if (x >= wx + ww - ResizeMargin && x <= wx + ww)
        {
            edge |= Edge.Right;
        }

        if (y >= wy && y <= wy + ResizeMargin)
        {
            edge |= Edge.Top;
        }

        if (y >= wy + wh - ResizeMargin && y <= wy + wh)
        {
            edge |= Edge.Bottom;
        }

        return edge;
    }

    private void ApplyResize(int x, int y)
    {
        var dx = x - _resizeStartX;
        var dy = y - _resizeStartY;

        var wx = _resizeStartWindowX;
        var wy = _resizeStartWindowY;
        var ww = _resizeStartW;
        var wh = _resizeStartH;

        var edge = (Edge)_activeEdge;

        if (edge.HasFlag(Edge.Left))
        {
            var newW = Math.Max(MinWidth, _resizeStartW - dx);
            wx = _resizeStartWindowX + (_resizeStartW - newW);
            ww = newW;
        }
        else if (edge.HasFlag(Edge.Right))
        {
            ww = Math.Max(MinWidth, _resizeStartW + dx);
        }

        if (edge.HasFlag(Edge.Top))
        {
            var newH = Math.Max(MinHeight, _resizeStartH - dy);
            wy = _resizeStartWindowY + (_resizeStartH - newH);
            wh = newH;
        }
        else if (edge.HasFlag(Edge.Bottom))
        {
            wh = Math.Max(MinHeight, _resizeStartH + dy);
        }

        _windowX = wx;
        _windowY = wy;
        _windowW = ww;
        _windowH = wh;

        // 拉左边/上边会把窗口推到屏幕外，拉完就再也抓不到了 —— 做一次可见性夹紧。
        var (screenW, screenH) = WindowMetrics.PrimaryWorkArea();
        var minVisible = 80;
        _windowX = Math.Clamp(_windowX, -( _windowW - minVisible), screenW - minVisible);
        _windowY = Math.Clamp(_windowY, 0, screenH - minVisible);

        _window.SetBounds(_windowX, _windowY, _windowW, _windowH);
        Render();
    }

    private void SaveBounds()
    {
        if (App.IsSelfCheckMode() || App.Settings is not { } store)
        {
            return;
        }

        store.Save(store.Current with
        {
            Weapon = store.Current.Weapon with
            {
                Bounds = new WindowBounds
                {
                    X = _windowX,
                    Y = _windowY,
                    Width = _windowW,
                    Height = _windowH,
                },
            },
        });
    }

    /// <summary>重置到默认位置与尺寸。</summary>
    public void ResetBounds()
    {
        (_windowX, _windowY, _windowW, _windowH) = DefaultBounds();
        _userResized = false;
        _window.SetBounds(_windowX, _windowY, _windowW, _windowH);
        ResizeToContent(App.Settings?.Current.Weapon ?? new WeaponHudSettings());
        Render();
    }

    /// <summary>外观配置变化后重新贴合尺寸并重绘。</summary>
    public void RefreshAppearance()
    {
        var settings = App.Settings?.Current.Weapon ?? new WeaponHudSettings();

        if (!_userResized)
        {
            ResizeToContent(settings);
        }

        Render(settings);
    }

    /// <summary>设置点击穿透（不改变锁定标记，供自检用）。</summary>
    public void SetClickThrough(bool clickThrough) => _window.SetClickThrough(clickThrough);

    /// <summary>隐藏 / 显示窗口。</summary>
    public void SetVisible(bool visible) => _window.SetVisible(visible);

    // ================= 设置页接口 =================

    /// <summary>应用锁定状态（设置页入口名）。</summary>
    public void ApplyLock(bool locked) => SetLocked(locked);

    /// <summary>重置到默认位置与尺寸（设置页入口名）。</summary>
    public void ResetToDefaultBounds() => ResetBounds();

    /// <summary>供自检把窗口摆到指定矩形。</summary>
    internal void SetBounds(int x, int y, int w, int h)
    {
        _windowX = x;
        _windowY = y;
        _windowW = w;
        _windowH = h;
        _userResized = true;
        _window.SetBounds(x, y, w, h);
        Render();
    }

    // ================= 自检辅助 =================

    /// <summary>供自检读取当前锁定态。</summary>
    internal bool IsLockedForSelfCheck => _locked;

    /// <summary>供自检读取武器名文本。</summary>
    internal string WeaponNameForSelfCheck =>
        _latest.WeaponName ?? _latest.Missile?.DisplayName ?? "未挂载";

    /// <summary>供自检读取当前状态。</summary>
    internal WeaponStatus StatusForSelfCheck => _latest;

    /// <summary>供自检读取尺寸（读 HWND 实际矩形）。</summary>
    internal (int Width, int Height) SizeForSelfCheck
    {
        get
        {
            var b = _window.GetBounds();
            return (b.Width, b.Height);
        }
    }

    /// <summary>供自检读取矩形（读 HWND 实际矩形）。</summary>
    internal (int X, int Y, int Width, int Height) BoundsForSelfCheck => _window.GetBounds();

    /// <summary>原生实现无 XAML 背景，恒为视觉全透明。</summary>
    internal bool IsBackgroundTransparentForSelfCheck => true;

    /// <summary>供自检强制渲染一次（不依赖定时器）。</summary>
    internal void RenderForSelfCheck() => Rebuild(force: true);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Dispose();
    }
}
