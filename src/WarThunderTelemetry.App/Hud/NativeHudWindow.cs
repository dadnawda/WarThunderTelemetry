using System.Globalization;
using System.Runtime.InteropServices;
using WarThunderTelemetry.App.Native;
using WarThunderTelemetry.App.Services;
using WarThunderTelemetry.Core;
using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.App.Hud;

/// <summary>
/// 遥测悬浮窗（原生分层窗口实现）。
/// <para>
/// <b>为什么不用 WinUI 3：</b>WinUI 3 的非打包窗口在浅色主题下会被 DWM
/// 铺一层白色宿主背景，它位于 XAML 内容之下，无论怎么改 XAML 的 Background
/// 都去不掉；即使补齐 <c>WS_EX_LAYERED</c> + <c>SetLayeredWindowAttributes</c>
/// + <c>DwmExtendFrameIntoClientArea</c>，白底依旧存在。
/// </para>
/// <para>
/// 因此改为原生分层窗口：自己用 GDI 在离屏位图上画好文字，
/// 再通过 <c>UpdateLayeredWindow</c> 逐像素提交。
/// 每个像素的 alpha 由我们完全掌控，<b>透明是操作系统层面保证的</b>，
/// 中间没有任何宿主层，也就不可能再出现白底。
/// </para>
/// </summary>
public sealed class NativeHudWindow : IDisposable
{
    // ---- 交互状态 ----

    private readonly LayeredWindow _window = new();
    private readonly List<string> _rowOrder = [];
    private readonly Dictionary<string, string> _rowTitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rowValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _alertingFields = new(StringComparer.OrdinalIgnoreCase);

    private TelemetryStatus? _latest;
    private bool _locked;
    private bool _dragging;
    private bool _resizing;
    private bool _userResized;
    private bool _disposed;

    private int _windowX;
    private int _windowY;
    private int _windowW = 320;
    private int _windowH = 400;

    /// <summary>拖动时鼠标相对窗口左上角的偏移。</summary>
    private int _dragOffsetX;
    private int _dragOffsetY;

    /// <summary>拉伸起点（屏幕坐标）与起始窗口矩形。</summary>
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

    /// <summary>窗口是否已创建。</summary>
    public bool IsCreated => _window.IsCreated;

    /// <summary>窗口句柄。</summary>
    public nint Handle => _window.Handle;

    /// <summary>当前是否鼠标穿透。</summary>
    public bool IsClickThrough => _window.IsClickThrough;

    /// <summary>当前是否锁定。</summary>
    public bool IsLocked => _locked;

    /// <summary>构造并创建窗口。</summary>
    public NativeHudWindow()
    {
        var settings = App.Settings?.Current.Hud ?? new HudSettings();

        // 恢复上次位置，没有记录则用默认位置（屏幕左侧竖排）。
        // 必须做可见性校正：显示器变化 / 分辨率变化 / 被拖到屏幕外时，
        // 保存的坐标会落在看不见的地方，表现为「悬浮窗找不到了」。
        if (settings.Bounds is { } bounds && bounds.Width > 0 && bounds.Height > 0)
        {
            (_windowX, _windowY, _windowW, _windowH) = ClampToWorkArea(
                bounds.X, bounds.Y, bounds.Width, bounds.Height);

            _userResized = true;
        }
        else
        {
            (_windowX, _windowY, _windowW, _windowH) = DefaultBounds();
        }

        _window.Create(_windowX, _windowY, _windowW, _windowH, "WarThunderTelemetry.Hud");

        // 锁定时保留一圈可点击边带，双击它即可解锁 —— 逃生口。
        _window.HookRing = 8;

        _locked = settings.Locked;
        _window.SetClickThrough(_locked);

        _window.PointerPressed += OnPointerPressed;
        _window.PointerMoved += OnPointerMoved;
        _window.PointerReleased += OnPointerReleased;
        _window.DoubleClicked += OnDoubleClicked;

        RebuildRows();
        Render(_latest);
    }

    /// <summary>
    /// 把保存的窗口矩形校正到主显示器可见范围内。
    /// <para>
    /// 至少要保证窗口左上角一块（约 80px）落在工作区里，
    /// 否则用户看不到窗口，也就无从拖动找回来。
    /// </para>
    /// </summary>
    private static (int X, int Y, int W, int H) ClampToWorkArea(
        int x, int y, int width, int height)
    {
        var (screenW, screenH) = WindowMetrics.PrimaryWorkArea();

        width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, screenW));
        height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, screenH));

        var minVisible = 80;
        x = Math.Clamp(x, -(width - minVisible), screenW - minVisible);
        y = Math.Clamp(y, 0, screenH - minVisible);

        return (x, y, width, height);
    }

    /// <summary>默认位置：屏幕左侧竖排，避开多数游戏 HUD 元素。</summary>
    private static (int X, int Y, int W, int H) DefaultBounds()
    {
        var (screenW, screenH) = WindowMetrics.PrimaryWorkArea();
        var w = 320;
        var h = Math.Min(900, (int)(screenH * 0.72));
        return (16, (screenH - h) / 3, w, h);
    }

    // ================= 数据 =================

    /// <summary>推送最新的遥测状态并重绘。</summary>
    public void Update(TelemetryStatus? status)
    {
        _latest = status;
        Render(status);
    }

    /// <summary>按当前配置重建行清单。</summary>
    public void RebuildRows()
    {
        var settings = App.Settings?.Current.Hud ?? new HudSettings();

        _rowOrder.Clear();
        _rowTitles.Clear();
        _rowValues.Clear();

        var fallback = new HashSet<string>(
            App.Settings?.Current.SelectedFieldIds ?? [],
            StringComparer.OrdinalIgnoreCase);

        var selected = settings.EffectiveFieldIds(
            fallback.Count == 0 ? FieldCatalog.DefaultSelectedIds : fallback);

        // EffectiveFieldIds 在 FieldIds 为空时已经退回主设置；
        // 若主设置也是空集，表示「全选」—— 用户要求「有多少字段就显示多少字段」。
        var showAll = selected.Count == 0;

        foreach (var field in FieldCatalog.All)
        {
            if (!showAll && !selected.Contains(field.Id))
            {
                continue;
            }

            _rowOrder.Add(field.Id);
            _rowTitles[field.Id] = string.IsNullOrEmpty(field.Unit)
                ? field.DisplayName
                : $"{field.DisplayName}/{field.Unit}";
            _rowValues[field.Id] = "–";
        }

        if (!_userResized)
        {
            ResizeToContent();
        }
    }

    /// <summary>让窗口高度贴合内容。</summary>
    public void ResizeToContent()
    {
        var settings = App.Settings?.Current.Hud ?? new HudSettings();
        var metrics = TextMetrics.For(settings.FontSize, settings.CompactRows);

        var contentHeight = (_rowOrder.Count * metrics.RowHeight) + (metrics.PaddingY * 2) + 4;
        var (_, screenH) = WindowMetrics.PrimaryWorkArea();
        var maxHeight = (int)(screenH * 0.85);

        _windowH = Math.Max(60, Math.Min(contentHeight, maxHeight));

        // 只在用户没手动定过宽度时才拉宽到内容所需 ——
        // 否则窄窗口永远拉不窄，表现为「横向拖不动」。
        _windowW = _userResized
            ? Math.Max(180, _windowW)
            : Math.Max(180, EstimateContentWidth(settings.FontSize));

        _window.SetBounds(_windowX, _windowY, _windowW, _windowH);
        SaveBounds();
    }

    /// <summary>估算内容所需宽度：名称列 + 数值列。</summary>
    private int EstimateContentWidth(int fontSize)
    {
        var maxTitle = 0;
        var maxValue = 0;

        foreach (var id in _rowOrder)
        {
            if (_rowTitles.TryGetValue(id, out var title))
            {
                maxTitle = Math.Max(maxTitle, Canvas.EstimateWidth(title, fontSize));
            }

            if (_rowValues.TryGetValue(id, out var value))
            {
                maxValue = Math.Max(maxValue, Canvas.EstimateWidth(value, fontSize));
            }
        }

        // 名称列 + 间隔 + 数值列（至少留 5 个数字的宽度）+ 左右内边距。
        var width = maxTitle + 12 + Math.Max(maxValue, fontSize * 3) + 24;
        return Math.Clamp(width, 180, 620);
    }

    // ================= 渲染 =================

    /// <summary>重绘整块悬浮窗。</summary>
    public void Render(TelemetryStatus? status)
    {
        if (!_window.IsCreated)
        {
            return;
        }

        var settings = App.Settings?.Current.Hud ?? new HudSettings();
        var metrics = TextMetrics.For(settings.FontSize, settings.CompactRows);

        var width = Math.Max(1, _windowW);
        var height = Math.Max(1, _windowH);

        using var canvas = new Canvas(width, height);

        // 背景板：默认关闭 = 完全透明。
        var showBackground = settings.ShowPanelBackground && settings.BackgroundOpacity > 0.001;
        if (showBackground)
        {
            var (br, bg, bb) = ColorUtil.ParseRgb(settings.BackgroundColor, (0, 0, 0));
            var ba = (byte)Math.Round(Math.Clamp(settings.BackgroundOpacity, 0, 1) * 255);
            canvas.FillRect(0, 0, width, height, br, bg, bb, ba);
        }

        // 细边框：明确「窗口边界在哪」。纯文字浮层没有边框时，
        // 拉伸找不到边缘、拖动也容易误判可操作范围。
        DrawResizeBorder(canvas, width, height, showBackground);

        // 未勾选任何字段时的提示。
        if (_rowOrder.Count == 0)
        {
            var (tr, tg, tb) = ColorUtil.ParseRgb(settings.ForegroundColor, (232, 232, 232));
            canvas.DrawText(
                "未勾选任何字段（在设置页「悬浮窗字段」里选）",
                metrics.PaddingX, metrics.PaddingY, 12, false, tr, tg, tb, 220);
            canvas.UpdateAlpha();
            canvas.Present(_window, _windowX, _windowY);
            return;
        }
        // 刷新每行的当前值。
        UpdateRowValues(status);

        var (nr, ng, nb) = ColorUtil.ParseRgb(settings.ForegroundColor, (232, 232, 232));
        var (vr, vg, vb) = string.IsNullOrWhiteSpace(settings.ValueColor)
            ? (nr, ng, nb)
            : ColorUtil.ParseRgb(settings.ValueColor, (nr, ng, nb));

        // 数值列右边界：窗口右侧减去内边距。
        var valueRight = width - metrics.PaddingX;
        var valueColumnWidth = (int)(settings.FontSize * 4.2);

        var y = metrics.PaddingY;

        foreach (var id in _rowOrder)
        {
            var title = _rowTitles.GetValueOrDefault(id, id);
            var value = _rowValues.GetValueOrDefault(id, "–");

            // 名称：左对齐。
            canvas.DrawText(
                title,
                metrics.PaddingX, y,
                settings.FontSize, false,
                nr, ng, nb, 255,
                outline: settings.TextShadow);

            // 数值：右对齐到固定列宽。
            var alerting = _alertingFields.Contains(id);
            var (rr, rg, rb) = alerting ? ((byte)255, (byte)88, (byte)88) : (vr, vg, vb);

            canvas.DrawText(
                value,
                valueRight - valueColumnWidth, y,
                settings.FontSize, alerting,
                rr, rg, rb, 255,
                outline: settings.TextShadow,
                rightAlign: true, maxWidth: valueColumnWidth);

            y += metrics.RowHeight;

            if (y > height)
            {
                break;
            }
        }

        // 关键一步：GDI 画字不写 alpha 通道，必须据绘制结果补上，
        // 否则分层窗口会把文字当成全透明，字就看不见了。
        canvas.UpdateAlpha();
        canvas.Present(_window, _windowX, _windowY);
    }

    /// <summary>
    /// 画一圈细边框，作为「可拉伸边界」的可视提示。
    /// <para>
    /// 平时是一圈很淡的灰线；鼠标靠近某条边时那条边换成亮青色。
    /// 锁定时不画（锁定态窗口不可交互，画了反而误导）。
    /// </para>
    /// </summary>
    private void DrawResizeBorder(Canvas canvas, int width, int height, bool hasBackground)
    {
        if (_locked)
        {
            return;
        }

        // 2px：1px 在高 DPI 缩放下几乎看不见，用户找不到可拉的边。
        const int t = 2;

        var baseRgb = hasBackground
            ? ((byte)120, (byte)120, (byte)120)
            : ((byte)150, (byte)150, (byte)150);

        var baseOpacity = hasBackground ? 0.55 : 0.45;

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

    private void UpdateRowValues(TelemetryStatus? status)
    {
        _alertingFields.Clear();

        var resolver = App.Telemetry?.Resolver ?? FieldCatalog.CreateResolver();

        foreach (var field in FieldCatalog.All)
        {
            if (!_rowValues.ContainsKey(field.Id))
            {
                continue;
            }

            if (status is null || !status.Snapshot.IsConnected)
            {
                _rowValues[field.Id] = "–";
                continue;
            }

            var value = status.Snapshot.Resolve(field, resolver);
            _rowValues[field.Id] = FormatValue(field, value);

            if (IsAlertingField(status, field.Id))
            {
                _alertingFields.Add(field.Id);
            }
        }
    }

    private static bool IsAlertingField(TelemetryStatus status, string fieldId)
    {
        if (status.MaxSeverity < AlertSeverity.Warning || status.Alerts.Count == 0)
        {
            return false;
        }

        return fieldId switch
        {
            "ias" => status.Alerts.Any(static a => a.RuleId is "overspeed" or "gear_overspeed" or "stall"),
            "altitude" or "radar_altitude" => status.Alerts.Any(static a => a.RuleId is "terrain" or "stall"),
            "g_load" => status.Alerts.Any(static a => a.RuleId is "overg" or "negative_g"),
            "fuel" or "fuel_percent" => status.Alerts.Any(static a => a.RuleId is "low_fuel"),
            "mach" => status.Alerts.Any(static a => a.RuleId is "mach_limit"),
            "water_temp" or "oil_temp" => status.Alerts.Any(static a => a.RuleId is "engine_temp"),
            _ => false,
        };
    }

    private static string FormatValue(FieldDescriptor descriptor, FieldValue value)
    {
        if (value.Text is { } text)
        {
            return text;
        }

        if (value.Number is not { } number)
        {
            return "–";
        }

        if (descriptor.AbbreviateLarge && Math.Abs(number) >= 1000)
        {
            return (number / 1000).ToString("F1", CultureInfo.InvariantCulture) + "k";
        }

        var decimals = descriptor.Decimals >= 0
            ? descriptor.Decimals
            : Math.Abs(number) >= 100 ? 0 : 1;

        return number.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    // ================= 交互 =================

    private const int ResizeMargin = 6;
    private const int MinWidth = 120;
    private const int MinHeight = 60;

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

        // 先判断是不是按在边缘 —— 边缘优先触发拉伸。
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
                _dragOffsetX + x, -(_windowW - minVisible), screenW - minVisible);
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
            Render(_latest);
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
                Hud = store.Current.Hud with { Locked = locked },
            });
        }
    }

    private Edge HitTestEdge(int x, int y)
    {
        var (wx, wy, ww, wh) = (_windowX, _windowY, _windowW, _windowH);

        var left = x >= wx && x <= wx + ResizeMargin;
        var right = x >= wx + ww - ResizeMargin && x <= wx + ww;
        var top = y >= wy && y <= wy + ResizeMargin;
        var bottom = y >= wy + wh - ResizeMargin && y <= wy + wh;

        // 必须先确认鼠标在窗口范围内（含边缘外扩一点）。
        if (x < wx - 2 || x > wx + ww + 2 || y < wy - 2 || y > wy + wh + 2)
        {
            return Edge.None;
        }

        var edge = Edge.None;
        if (left)
        {
            edge |= Edge.Left;
        }

        if (right)
        {
            edge |= Edge.Right;
        }

        if (top)
        {
            edge |= Edge.Top;
        }

        if (bottom)
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

        _window.SetBounds(wx, wy, ww, wh);
        Render(_latest);
    }

    private void SaveBounds()
    {
        if (App.IsSelfCheckMode() || App.Settings is not { } store)
        {
            return;
        }

        store.Save(store.Current with
        {
            Hud = store.Current.Hud with
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
        ResizeToContent();
        Render(_latest);
    }

    /// <summary>设置点击穿透（不改变锁定标记，供自检用）。</summary>
    public void SetClickThrough(bool clickThrough) => _window.SetClickThrough(clickThrough);

    /// <summary>显示或隐藏窗口。</summary>
    public void SetVisible(bool visible) => _window.SetVisible(visible);

    // ================= 设置页接口 =================
    //
    // 设置页原先直接调用 WinUI 版悬浮窗的方法。这些方法名保持兼容，
    // 内部统一改成「重读配置 → 重绘」，这样设置页几乎不用改。

    /// <summary>应用锁定状态（设置页入口）。</summary>
    public void ApplyLock(bool locked) => SetLocked(locked);

    /// <summary>行数变化后重新贴合尺寸并重绘。</summary>
    public void ApplyRowSpacing(bool compact)
    {
        // 行间距由 TextMetrics.For 依据配置里的 CompactRows 现算，
        // 因此这里只需重读配置并重绘。
        RefreshAppearance();
    }

    /// <summary>字号变化后重绘。</summary>
    public void ApplyFontSize(int size) => RefreshAppearance();

    /// <summary>配色变化后重绘。</summary>
    public void ApplyColors(HudSettings settings) => RefreshAppearance();

    /// <summary>背景配置变化后重绘。</summary>
    public void ApplyBackground(HudSettings settings) => RefreshAppearance();

    /// <summary>拉扯提示可见性（原生实现不画提示角标，保留空实现以兼容设置页）。</summary>
    public void ApplyResizeHintVisibility(bool show)
    {
        // 原生窗口靠四边热区拉伸，不需要角标；保留方法避免调用点报错。
    }

    /// <summary>重新读取外观配置并重绘。</summary>
    public void RefreshAppearance()
    {
        var settings = App.Settings?.Current.Hud ?? new HudSettings();

        UpdateRowValues(_latest);

        if (!_userResized)
        {
            ResizeToContent();
        }

        Render(_latest);
    }

    /// <summary>重置到默认位置与尺寸（设置页入口名）。</summary>
    public void ResetToDefaultBounds() => ResetBounds();

    // ================= 自检接口 =================

    /// <summary>供自检读取当前行数。</summary>
    internal int RowCountForSelfCheck => _rowOrder.Count;

    /// <summary>供自检读取当前是否锁定。</summary>
    internal bool IsLockedForSelfCheck => _locked;

    /// <summary>原生实现无 XAML 背景，恒为视觉全透明。</summary>
    internal bool IsBackgroundTransparentForSelfCheck => true;

    /// <summary>供自检读取尺寸（读 HWND 实际矩形）。</summary>
    internal (int Width, int Height) SizeForSelfCheck
    {
        get
        {
            var b = _window.GetBounds();
            return (b.Width, b.Height);
        }
    }

    /// <summary>供自检强制渲染一次。</summary>
    internal void RenderForSelfCheck() => Render(_latest);

    /// <summary>供自检把窗口摆到指定矩形。</summary>
    internal void SetBoundsForSelfCheck(int x, int y, int w, int h)
    {
        _windowX = x;
        _windowY = y;
        _windowW = w;
        _windowH = h;
        _userResized = true;
        _window.SetBounds(x, y, w, h);
        Render(_latest);
    }

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

/// <summary>文字排版度量。</summary>
internal readonly record struct TextMetrics(
    int FontSize,
    int RowHeight,
    int PaddingX,
    int PaddingY)
{
    /// <summary>按字号与是否紧凑计算排版度量。</summary>
    internal static TextMetrics For(int fontSize, bool compact)
    {
        var rowHeight = (int)Math.Ceiling(fontSize * (compact ? 1.25 : 1.55));
        var paddingY = compact ? 2 : 4;
        return new TextMetrics(fontSize, rowHeight, 6, paddingY);
    }
}

/// <summary>屏幕度量辅助。</summary>
internal static partial class WindowMetrics
{
    /// <summary>取主显示器工作区尺寸（不含任务栏）。</summary>
    internal static (int Width, int Height) PrimaryWorkArea()
    {
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromPoint(default, 1); // MONITOR_DEFAULTTOPRIMARY

        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref info))
        {
            return (info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
        }

        return (1920, 1080);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint MonitorFromPoint(Point32 pt, uint dwFlags);

    // 注意：user32.dll 里只有 GetMonitorInfoW / GetMonitorInfoA，
    // 没有无后缀的 GetMonitorInfo。LibraryImport 默认按方法名找入口点，
    // 所以必须显式指定 EntryPoint，否则运行时抛 EntryPointNotFoundException。
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);
}

/// <summary>颜色解析辅助。</summary>
internal static class ColorUtil
{
    /// <summary>解析 <c>#RGB</c> / <c>#RRGGBB</c> / <c>#AARRGGBB</c> 为 RGB 三元组。</summary>
    internal static (byte R, byte G, byte B) ParseRgb(string? hex, (byte R, byte G, byte B) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var s = hex.Trim().TrimStart('#');

        try
        {
            return s.Length switch
            {
                3 => (
                    Convert.ToByte(new string(s[0], 2), 16),
                    Convert.ToByte(new string(s[1], 2), 16),
                    Convert.ToByte(new string(s[2], 2), 16)),

                6 => (
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16)),

                8 => (
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16),
                    Convert.ToByte(s.Substring(6, 2), 16)),

                _ => fallback,
            };
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
