using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WarThunderTelemetry.App.Services;
using WarThunderTelemetry.Core.Parsing;
using WarThunderTelemetry.Core.Weapons;

namespace WarThunderTelemetry.App.Views;

/// <summary>
/// 设置页：数据源、悬浮窗、显示字段勾选。
/// <para>
/// 所有改动<b>立即生效并落盘</b>，不设「保存」按钮 —— 悬浮窗字号之类的调整
/// 需要边看边调，多一步确认只会碍事。
/// </para>
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>字段分类的显示顺序与中文名。</summary>
    private static readonly (FieldCategory Category, string Title)[] CategoryOrder =
    [
        (FieldCategory.Flight, "飞行数据"),
        (FieldCategory.Derived, "派生指标（本地计算）"),
        (FieldCategory.Engine, "动力系统"),
        (FieldCategory.Control, "操纵与起落装置"),
        (FieldCategory.Fuel, "燃油"),
        (FieldCategory.Weapon, "武器"),
        (FieldCategory.Vehicle, "载具信息"),
    ];

    private readonly ObservableCollection<FieldCheckItem> _items = [];
    private readonly ObservableCollection<FieldCheckItem> _hudItems = [];

    private bool _loading = true;

    /// <summary>构造。</summary>
    public SettingsPage()
    {
        InitializeComponent();

        // 必须 try/finally：初始化里任何一步抛异常，
        // 都会让 _loading 永久卡在 true，导致整页所有控件都失去响应。
        // 宁可初始化不完整，也不能让页面变成只读的砖头。
        try
        {
            BuildFieldList();
            BuildHudFieldList();
            LoadFromSettings();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 按分类构建字段勾选列表。
    /// <para>
    /// 用固定容器 + 复选框而非 <c>ListView</c>：这里字段总数不到 50，
    /// 一次性铺完比虚拟化列表更简单，也避开了 ListView 选中态与 CheckBox 打架的问题。
    /// </para>
    /// </summary>
    private void BuildFieldList()
    {
        BuildFieldChecks(FieldCheckList, _items, OnFieldCheckChanged);
    }

    /// <summary>构建悬浮窗专用字段勾选列表。</summary>
    private void BuildHudFieldList()
    {
        BuildFieldChecks(HudFieldCheckList, _hudItems, OnHudFieldCheckChanged);
    }

    /// <summary>
    /// 把全部字段按分类铺成三列复选框，挂到指定容器。
    /// </summary>
    private static void BuildFieldChecks(
        ItemsControl host,
        ObservableCollection<FieldCheckItem> target,
        RoutedEventHandler onChanged)
    {
        host.Items.Clear();
        target.Clear();

        foreach (var (category, title) in CategoryOrder)
        {
            var fields = FieldCatalog.All.Where(f => f.Category == category).ToList();
            if (fields.Count == 0)
            {
                continue;
            }

            var header = new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 4),
            };
            header.SetResourceReference(TextBlock.ForegroundProperty, "WtPrimaryTextBrush");
            host.Items.Add(header);

            // 字段名长短不一，用三列网格铺开即可。
            var grid = new Grid
            {
                ColumnSpacing = 12,
                RowSpacing = 0,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (var row = 0; row < (fields.Count + 2) / 3; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                var check = new CheckBox
                {
                    Content = FormatLabel(field),
                    Tag = field.Id,
                    Margin = new Thickness(0, 2, 8, 2),
                    IsChecked = field.DefaultSelected,
                };
                check.SetResourceReference(ForegroundProperty, "WtPrimaryTextBrush");
                check.Checked += onChanged;
                check.Unchecked += onChanged;

                Grid.SetColumn(check, i % 3);
                Grid.SetRow(check, i / 3);
                grid.Children.Add(check);

                target.Add(new FieldCheckItem(field.Id, field.DisplayName, field.Unit, check));
            }

            host.Items.Add(grid);
        }
    }

    private static string FormatLabel(FieldDescriptor field) =>
        string.IsNullOrEmpty(field.Unit)
            ? field.DisplayName
            : $"{field.DisplayName}（{field.Unit}）";

    /// <summary>把配置回填到控件。</summary>
    private void LoadFromSettings()
    {
        var settings = App.Settings?.Current ?? new AppSettings();

        MockToggle.IsOn = settings.UseMockSource;
        PortBox.Value = settings.Port;
        StateIntervalBox.Value = settings.StateIntervalMs;
        IndicatorsIntervalBox.Value = settings.IndicatorsIntervalMs;

        var hud = settings.Hud;
        HudLockToggle.IsOn = hud.Locked;
        HudPanelBgToggle.IsOn = hud.ShowPanelBackground;
        HudResizeHintToggle.IsOn = hud.ShowResizeHint;
        HudCompactToggle.IsOn = hud.CompactRows;
        HudFontSizeSlider.Value = Math.Clamp(hud.FontSize, 12, 36);
        HudBgOpacitySlider.Value = Math.Clamp(hud.BackgroundOpacity, 0, 1);

        HudTitleColorPicker.Color = ToColor(hud.ForegroundColor, Windows.UI.Color.FromArgb(255, 0, 0, 0));
        HudValueColorPicker.Color = ToColor(hud.ValueColor, Windows.UI.Color.FromArgb(255, 0, 0, 0));
        HudBgColorPicker.Color = ToColor(hud.BackgroundColor, Windows.UI.Color.FromArgb(255, 0, 0, 0));

        OnlineLookupToggle.IsOn = settings.OnlineVehicleLookup;

        // ---- 武器悬浮区 ----
        var weapon = settings.Weapon;
        WeaponFollowHudColorsToggle.IsOn = weapon.UseHudColors;
        WeaponLockToggle.IsOn = weapon.Locked;
        WeaponOwnStateToggle.IsOn = weapon.ShowOwnState;
        WeaponPanelBgToggle.IsOn = weapon.ShowPanelBackground;
        WeaponBgOpacitySlider.Value = Math.Clamp(weapon.BackgroundOpacity, 0, 1);
        WeaponFontSizeSlider.Value = Math.Clamp(weapon.FontSize, 10, 32);

        BuildMissileCombo(weapon.ForcedMissileId);

        TargetDistanceBox.Value = weapon.TargetDistanceM ?? double.NaN;
        TargetSpeedBox.Value = weapon.TargetSpeedKmh ?? double.NaN;
        TargetAltBox.Value = weapon.TargetAltitudeM ?? double.NaN;
        SelectAspect(weapon.TargetAspect);

        ApplySelection(_items, settings.EffectiveSelectedFieldIds());
        ApplySelection(_hudItems, hud.EffectiveFieldIds(settings.EffectiveSelectedFieldIds()));
        UpdateVehicleCacheText();
        UpdateMissileInfoText(weapon.ForcedMissileId);
    }

    /// <summary>
    /// 构建导弹下拉框。
    /// <para>
    /// 第一项是「自动识别」，后面按类型分组列出全部导弹。
    /// 用 <c>Tag</c> 存导弹 Id，避免依赖显示名的字符串匹配。
    /// </para>
    /// </summary>
    private void BuildMissileCombo(string? selectedId)
    {
        WeaponMissileBox.Items.Clear();

        var auto = new ComboBoxItem { Content = "自动识别（按游戏挂载）", Tag = null };
        WeaponMissileBox.Items.Add(auto);
        WeaponMissileBox.SelectedItem = auto;

        ComboBoxItem? toSelect = auto;

        foreach (var (kind, title) in new (MissileKind Kind, string Title)[]
                 {
                     (MissileKind.AirToAir, "空空导弹"),
                     (MissileKind.AirToGround, "空地导弹"),
                 })
        {
            var group = MissileDatabase.Profiles.Where(p => p.Kind == kind).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            var header = new ComboBoxItem
            {
                Content = $"── {title} ──",
                IsEnabled = false,
            };
            WeaponMissileBox.Items.Add(header);

            foreach (var missile in group)
            {
                // 参数库已换为挖掘表，中文名多为 null，此时只显示原名。
                var label = string.IsNullOrWhiteSpace(missile.DisplayNameZh)
                    ? missile.DisplayName
                    : $"{missile.DisplayNameZh}（{missile.DisplayName}）";

                var item = new ComboBoxItem
                {
                    Content = label,
                    Tag = missile.Id,
                };

                WeaponMissileBox.Items.Add(item);

                if (selectedId is not null &&
                    string.Equals(selectedId, missile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    toSelect = item;
                }
            }
        }

        WeaponMissileBox.SelectedItem = toSelect;
    }

    private void SelectAspect(string? aspect)
    {
        foreach (var item in TargetAspectBox.Items.OfType<ComboBoxItem>())
        {
            var tag = item.Tag as string;
            var matches = string.IsNullOrWhiteSpace(aspect)
                ? tag == "unknown"
                : string.Equals(tag, aspect, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                TargetAspectBox.SelectedItem = item;
                return;
            }
        }

        TargetAspectBox.SelectedIndex = 0;
    }

    /// <summary>显示当前选中导弹的关键参数，让玩家知道库里到底有没有这枚弹。</summary>
    private void UpdateMissileInfoText(string? missileId)
    {
        if (string.IsNullOrWhiteSpace(missileId))
        {
            WeaponMissileInfoText.Text =
                $"导弹库已收录 {MissileDatabase.Count} 种导弹。当前为自动识别模式。";
            return;
        }

        var missile = MissileDatabase.Profiles.FirstOrDefault(p =>
            string.Equals(p.Id, missileId, StringComparison.OrdinalIgnoreCase));

        // 注意：换库后多数字段是 null（数据表没填就不编），
        // 一律走空值安全的格式化，绝不能对 null 解引用 —— 那会直接崩设置页。
        WeaponMissileInfoText.Text = missile is null
            ? "未找到该导弹。"
            : BuildMissileInfo(missile);
    }

    /// <summary>
    /// 拼一段导弹关键信息。优先展示数据挖掘实测值，缺失的字段写「—」而不是编数字。
    /// </summary>
    private static string BuildMissileInfo(MissileProfile m)
    {
        static string Num(double? v, string format, string? unit) =>
            v is { } value ? string.Concat(value.ToString(format), unit is null ? "" : " " + unit) : "—";

        var name = string.IsNullOrWhiteSpace(m.DisplayNameZh) ? m.DisplayName : m.DisplayNameZh!;

        // 最大飞行距离单位是米，展示换算成千米。
        var rangeText = m.MaxDistanceM is { } range ? $"{range / 1000:F1} km" : "—";

        var parts = new List<string>
        {
            $"最大飞行距离 {rangeText}",
            $"燃烧 {Num(m.BurnTime, "F1", "s")}",
            $"过载 {Num(m.MaxG, "F0", "g")}",
            $"推力 {Num(m.ThrustN, "F0", "N")}",
            $"质量 {Num(m.MassKg, "F1", "kg")}",
            $"阻力系数 {Num(m.DragCxk, "F2", null)}",
            $"滞空 {Num(m.LifeTimeS, "F0", "s")}",
            $"制导 {GuidanceName(m.Guidance)}",
        };

        var text = $"{name}：" + string.Join("，", parts);
        return m.Source is null ? text + "。" : $"{text}。（数据来源：{m.Source}）";
    }

    private static string GuidanceName(Core.Weapons.MissileGuidance guidance) => guidance switch
    {
        Core.Weapons.MissileGuidance.Ir => "红外",
        Core.Weapons.MissileGuidance.Sarh => "半主动雷达",
        Core.Weapons.MissileGuidance.Arh => "主动雷达",
        Core.Weapons.MissileGuidance.Command => "指令制导",
        Core.Weapons.MissileGuidance.AntiRadiation => "反辐射",
        _ => "未知",
    };

    /// <summary>把 <c>#RRGGBB</c> 解析成 Color，供 ColorPicker 回填。</summary>
    private static Windows.UI.Color ToColor(string? hex, Windows.UI.Color fallback)
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
                3 => Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(new string(s[0], 2), 16),
                    Convert.ToByte(new string(s[1], 2), 16),
                    Convert.ToByte(new string(s[2], 2), 16)),
                6 => Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16)),
                8 => Windows.UI.Color.FromArgb(
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16),
                    Convert.ToByte(s.Substring(6, 2), 16)),
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>把 Color 转成 <c>#RRGGBB</c>。</summary>
    private static string ToHex(Windows.UI.Color c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>刷新载具缓存的展示文本。</summary>
    private void UpdateVehicleCacheText()
    {
        var resolver = App.Telemetry?.Vehicles;
        if (resolver is null)
        {
            VehicleCacheText.Text = "缓存：不可用";
            return;
        }

        var text = $"已缓存 {resolver.CachedCount} 架载具的参数";

        if (resolver.LastError is { Length: > 0 } error)
        {
            text += $"\n最近一次抓取：{error}";
        }

        VehicleCacheText.Text = text;
    }

    private static void ApplySelection(
        IEnumerable<FieldCheckItem> items,
        IReadOnlySet<string> selected)
    {
        foreach (var item in items)
        {
            item.Check.IsChecked = selected.Contains(item.Id);
        }
    }

    /// <summary>把当前勾选写入配置。</summary>
    private void PersistSelection()
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        store.Save(store.Current with
        {
            SelectedFieldIds = _items
                .Where(item => item.Check.IsChecked == true)
                .Select(item => item.Id)
                .ToList(),
        });

        // 悬浮窗立即重建行布局，不必等重启。
        App.RefreshHudLayout();
    }

    /// <summary>把悬浮窗专属勾选写入配置。</summary>
    private void PersistHudSelection()
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var hud = store.Current.Hud;
        store.Save(store.Current with
        {
            Hud = hud with
            {
                FieldIds = _hudItems
                    .Where(item => item.Check.IsChecked == true)
                    .Select(item => item.Id)
                    .ToList(),
            },
        });

        App.RefreshHudLayout();
    }

    // ================= 数据源 =================

    private void OnMockToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        store.Save(store.Current with { UseMockSource = MockToggle.IsOn });
        NotifyRestartRequired();
    }

    private void OnPortChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue) || App.Settings is not { } store)
        {
            return;
        }

        var port = (int)Math.Clamp(args.NewValue, 1, 65535);
        if (port == store.Current.Port)
        {
            return;
        }

        store.Save(store.Current with { Port = port });
        NotifyRestartRequired();
    }

    private void OnIntervalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue) || App.Settings is not { } store)
        {
            return;
        }

        var value = (int)Math.Clamp(args.NewValue, 50, 2000);

        // 两个 NumberBox 共用一个处理器，靠 sender 区分。
        var updated = ReferenceEquals(sender, StateIntervalBox)
            ? store.Current with { StateIntervalMs = value }
            : store.Current with { IndicatorsIntervalMs = value };

        store.Save(updated);
        NotifyRestartRequired();
    }

    // ================= 载具参数 =================

    private void OnOnlineLookupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var enabled = OnlineLookupToggle.IsOn;
        store.Save(store.Current with { OnlineVehicleLookup = enabled });

        // 立即作用到解析器，不必重启 —— 关掉后正在排队的抓取也不再发起。
        if (App.Telemetry?.Vehicles is { } resolver)
        {
            resolver.OnlineLookupEnabled = enabled;
        }
    }

    private async void OnRefreshVehicleClick(object sender, RoutedEventArgs e)
    {
        var resolver = App.Telemetry?.Vehicles;
        if (resolver is null)
        {
            return;
        }

        // 当前载具代号取自最近一次状态。
        var vehicleType = App.Telemetry?.Current.Snapshot.VehicleType;
        if (string.IsNullOrWhiteSpace(vehicleType))
        {
            VehicleCacheText.Text = "当前没有载具数据 —— 先进一局再刷新。";
            return;
        }

        if (sender is not Button button)
        {
            return;
        }

        button.IsEnabled = false;
        VehicleCacheText.Text = $"正在抓取 {vehicleType} 的参数…";

        try
        {
            var profile = await resolver.EnsureFetchedAsync(vehicleType);

            VehicleCacheText.Text = profile is null
                ? $"未抓到 {vehicleType} 的参数，仍使用兵种缺省阈值。"
                : $"已更新：{profile.DisplayName}（{profile.Nation}）"
                  + $"\n超速上限 {Fmt(profile.MaxSpeedIas, "km/h")} · "
                  + $"G 限 +{Fmt(profile.GLimitPositive, "g")}/-{Fmt(profile.GLimitNegative, "g")} · "
                  + $"起落架限速 {Fmt(profile.GearSpeedLimit, "km/h")}";

            await resolver.SaveCacheAsync();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnClearVehicleCacheClick(object sender, RoutedEventArgs e)
    {
        var resolver = App.Telemetry?.Vehicles;
        if (resolver is null)
        {
            return;
        }

        resolver.ClearCache();
        await resolver.SaveCacheAsync();
        UpdateVehicleCacheText();
        VehicleCacheText.Text = "载具缓存已清空 —— 之后遇到未收录机型会重新联网抓取。";
    }

    private static string Fmt(double? value, string unit) =>
        value is { } v
            ? $"{v.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} {unit}"
            : "—";

    // ================= 悬浮窗 =================

    private void OnToggleHudClick(object sender, RoutedEventArgs e)
    {
        if (App.HudWindow is null)
        {
            App.ShowHud();
        }
        else
        {
            App.CloseHud();
        }
    }

    /// <summary>
    /// 清掉保存的位置尺寸，下次打开悬浮窗回到默认位置。
    /// 悬浮窗被拖到屏幕外找不回来时，这是救命按钮。
    /// </summary>
    private void OnResetHudBoundsClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings is not { } store)
        {
            return;
        }

        store.Save(store.Current with
        {
            Hud = store.Current.Hud with { Bounds = null },
        });

        // 已打开的悬浮窗立刻回到默认位置。
        if (App.HudWindow is { } hud)
        {
            hud.ResetToDefaultBounds();
        }
    }

    private void OnHudLockToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var hud = store.Current.Hud;
        store.Save(store.Current with { Hud = hud with { Locked = HudLockToggle.IsOn } });

        // 立即作用到已打开的悬浮窗。
        App.HudWindow?.ApplyLock(HudLockToggle.IsOn);
    }

    private void OnHudFontSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var size = (int)Math.Round(e.NewValue);
        var hud = store.Current.Hud;
        store.Save(store.Current with { Hud = hud with { FontSize = size } });

        App.HudWindow?.ApplyFontSize(size);
    }

    private void OnHudBgOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var opacity = Math.Clamp(e.NewValue, 0, 1);
        var hud = store.Current.Hud;
        var updated = hud with { BackgroundOpacity = opacity };
        store.Save(store.Current with { Hud = updated });

        App.HudWindow?.ApplyBackground(updated);
        SyncWeaponHudAppearance();
    }

    private void OnHudPanelBgToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var hud = store.Current.Hud;
        var updated = hud with { ShowPanelBackground = HudPanelBgToggle.IsOn };
        store.Save(store.Current with { Hud = updated });

        App.HudWindow?.ApplyBackground(updated);
        SyncWeaponHudAppearance();
    }

    private void OnHudCompactToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var hud = store.Current.Hud;
        store.Save(store.Current with { Hud = hud with { CompactRows = HudCompactToggle.IsOn } });

        App.HudWindow?.ApplyRowSpacing(HudCompactToggle.IsOn);
    }

    /// <summary>
    /// 遥测悬浮窗外观变化后，同步刷一次武器悬浮区。
    /// <para>
    /// 武器区默认跟随主悬浮窗配色（<see cref="WeaponHudSettings.UseHudColors"/>），
    /// 所以主悬浮窗改色/改背景时它必须跟着重绘，否则同屏两个悬浮窗会不一致。
    /// </para>
    /// </summary>
    private static void SyncWeaponHudAppearance()
    {
        if (App.Settings?.Current.Weapon.UseHudColors is not false)
        {
            App.WeaponHudWindow?.RefreshAppearance();
        }
    }

    private void OnHudTitleColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var hud = store.Current.Hud;
        var updated = hud with { ForegroundColor = ToHex(args.NewColor) };
        store.Save(store.Current with { Hud = updated });

        App.HudWindow?.ApplyColors(updated);
        SyncWeaponHudAppearance();
    }

    private void OnHudValueColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var hud = store.Current.Hud;
        var updated = hud with { ValueColor = ToHex(args.NewColor) };
        store.Save(store.Current with { Hud = updated });

        App.HudWindow?.ApplyColors(updated);
        SyncWeaponHudAppearance();
    }

    private void OnHudBgColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading || App.Settings is not { } store)
        {
            return;
        }

        var hud = store.Current.Hud;
        var updated = hud with { BackgroundColor = ToHex(args.NewColor) };
        store.Save(store.Current with { Hud = updated });

        App.HudWindow?.ApplyBackground(updated);
        SyncWeaponHudAppearance();
    }

    /// <summary>
    /// 一键配色预设。
    /// 游戏画面明暗差异极大，给几个调好的组合比让用户从零选色实用。
    /// </summary>
    private void OnHudPresetDarkClick(object sender, RoutedEventArgs e) =>
        ApplyColorPreset("#FFE8E8E8", "#FFFFFFFF", "#FF000000", backgroundOpacity: 0.55);

    private void OnHudPresetLightClick(object sender, RoutedEventArgs e) =>
        ApplyColorPreset("#FF101010", "#FF000000", "#FFFFFFFF", backgroundOpacity: 0.55);

    private void OnHudPresetNeonClick(object sender, RoutedEventArgs e) =>
        ApplyColorPreset("#FF7CFF7C", "#FF39FF14", "#FF000000", backgroundOpacity: 0.35);

    /// <summary>套用配色组合并立即生效。</summary>
    private void ApplyColorPreset(
        string title, string value, string background, double backgroundOpacity)
    {
        if (App.Settings is not { } store)
        {
            return;
        }

        var hud = store.Current.Hud with
        {
            ForegroundColor = title,
            ValueColor = value,
            BackgroundColor = background,
            BackgroundOpacity = backgroundOpacity,
            ShowPanelBackground = true,
        };

        store.Save(store.Current with { Hud = hud });

        // 回填控件（期间屏蔽事件，避免重复保存）。
        // 必须 try/finally —— 这里任何一步抛异常都会让 _loading 永久卡在 true，
        // 此后所有事件处理器都会在开头的 `if (_loading) return;` 直接返回，
        // 表现为「设置页所有按钮都点不动」。
        _loading = true;
        try
        {
            HudTitleColorPicker.Color = ToColor(title, Windows.UI.Color.FromArgb(255, 0, 0, 0));
            HudValueColorPicker.Color = ToColor(value, Windows.UI.Color.FromArgb(255, 0, 0, 0));
            HudBgColorPicker.Color = ToColor(background, Windows.UI.Color.FromArgb(255, 0, 0, 0));
            HudBgOpacitySlider.Value = backgroundOpacity;
            HudPanelBgToggle.IsOn = true;
        }
        finally
        {
            _loading = false;
        }

        App.HudWindow?.ApplyColors(hud);
        App.HudWindow?.ApplyBackground(hud);

        // 武器悬浮区跟随配色，预设改完也要一起刷。
        SyncWeaponHudAppearance();
    }

    // ================= 悬浮窗字段 =================

    private void OnHudResizeHintToggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var hud = store.Current.Hud;
        store.Save(store.Current with
        {
            Hud = hud with { ShowResizeHint = HudResizeHintToggle.IsOn },
        });

        App.HudWindow?.ApplyResizeHintVisibility(HudResizeHintToggle.IsOn);
    }

    private void OnHudFieldCheckChanged(object sender, RoutedEventArgs e) => PersistHudSelection();

    /// <summary>清空悬浮窗专属配置 → 回退成跟随仪表盘那份字段。</summary>
    private void OnHudFieldsFollowDashboardClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        store.Save(store.Current with
        {
            Hud = store.Current.Hud with { FieldIds = [] },
        });

        ApplySelection(_hudItems, store.Current.EffectiveSelectedFieldIds());
        App.RefreshHudLayout();
    }

    private void OnHudFieldsResetClick(object sender, RoutedEventArgs e)
    {
        ApplySelection(_hudItems, FieldCatalog.DefaultSelectedIds);
        PersistHudSelection();
    }

    private void OnHudFieldsAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in _hudItems)
        {
            item.Check.IsChecked = true;
        }

        PersistHudSelection();
    }

    private void OnHudFieldsNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in _hudItems)
        {
            item.Check.IsChecked = false;
        }

        PersistHudSelection();
    }

    // ================= 武器悬浮区 =================

    private void OnToggleWeaponHudClick(object sender, RoutedEventArgs e)
    {
        if (App.WeaponHudWindow is null)
        {
            App.ShowWeaponHud();
        }
        else
        {
            App.CloseWeaponHud();
        }
    }

    private void OnResetWeaponBoundsClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings is not { } store)
        {
            return;
        }

        store.Save(store.Current with
        {
            Weapon = store.Current.Weapon with { Bounds = null },
        });

        App.WeaponHudWindow?.ResetToDefaultBounds();
    }

    private void OnWeaponLockToggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var weapon = store.Current.Weapon;
        store.Save(store.Current with
        {
            Weapon = weapon with { Locked = WeaponLockToggle.IsOn },
        });

        App.WeaponHudWindow?.ApplyLock(WeaponLockToggle.IsOn);
    }

    private void OnWeaponOwnStateToggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var weapon = store.Current.Weapon;
        store.Save(store.Current with
        {
            Weapon = weapon with { ShowOwnState = WeaponOwnStateToggle.IsOn },
        });

        App.WeaponHudWindow?.RenderForSelfCheck();
    }

    private void OnWeaponPanelBgToggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var weapon = store.Current.Weapon;
        store.Save(store.Current with
        {
            Weapon = weapon with { ShowPanelBackground = WeaponPanelBgToggle.IsOn },
        });

        App.WeaponHudWindow?.RenderForSelfCheck();
    }

    private void OnWeaponBgOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var weapon = store.Current.Weapon;
        store.Save(store.Current with
        {
            Weapon = weapon with { BackgroundOpacity = Math.Clamp(e.NewValue, 0, 1) },
        });

        App.WeaponHudWindow?.RenderForSelfCheck();
    }

    private void OnWeaponFollowHudColorsToggled(object sender, RoutedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var weapon = store.Current.Weapon;
        store.Save(store.Current with
        {
            Weapon = weapon with { UseHudColors = WeaponFollowHudColorsToggle.IsOn },
        });

        App.WeaponHudWindow?.RefreshAppearance();
    }

    private void OnWeaponFontSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var size = (int)Math.Round(e.NewValue);
        var weapon = store.Current.Weapon;
        store.Save(store.Current with
        {
            Weapon = weapon with { FontSize = size },
        });

        App.WeaponHudWindow?.RefreshAppearance();
        App.WeaponHudWindow?.RenderForSelfCheck();
    }

    private void OnWeaponMissileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var id = (WeaponMissileBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var weapon = store.Current.Weapon;

        store.Save(store.Current with
        {
            Weapon = weapon with { ForcedMissileId = string.IsNullOrWhiteSpace(id) ? null : id },
        });

        UpdateMissileInfoText(id);
        App.WeaponHudWindow?.RenderForSelfCheck();
    }

    private void OnTargetInputChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var weapon = store.Current.Weapon;

        store.Save(store.Current with
        {
            Weapon = weapon with
            {
                TargetDistanceM = Clean(TargetDistanceBox.Value),
                TargetSpeedKmh = Clean(TargetSpeedBox.Value),
                TargetAltitudeM = Clean(TargetAltBox.Value),
            },
        });

        App.WeaponHudWindow?.RenderForSelfCheck();

        // NaN 是 NumberBox 清空时的值，必须转成 null，否则会存进 JSON 变成非法数字。
        static double? Clean(double value) =>
            double.IsNaN(value) || value <= 0 ? null : value;
    }

    private void OnTargetAspectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        var tag = (TargetAspectBox.SelectedItem as ComboBoxItem)?.Tag as string;
        var weapon = store.Current.Weapon;

        store.Save(store.Current with
        {
            Weapon = weapon with
            {
                TargetAspect = tag is null or "unknown" ? null : tag,
            },
        });

        App.WeaponHudWindow?.RenderForSelfCheck();
    }

    private void OnClearTargetClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings is not { } store || _loading)
        {
            return;
        }

        TargetDistanceBox.Value = double.NaN;
        TargetSpeedBox.Value = double.NaN;
        TargetAltBox.Value = double.NaN;
        TargetAspectBox.SelectedIndex = 0;

        store.Save(store.Current with
        {
            Weapon = store.Current.Weapon with
            {
                TargetDistanceM = null,
                TargetSpeedKmh = null,
                TargetAltitudeM = null,
                TargetAspect = null,
            },
        });

        App.WeaponHudWindow?.RenderForSelfCheck();
    }

    // ================= 显示字段 =================

    private void OnFieldCheckChanged(object sender, RoutedEventArgs e) => PersistSelection();

    private void OnResetFieldsClick(object sender, RoutedEventArgs e)
    {
        ApplySelection(_items, FieldCatalog.DefaultSelectedIds);
        PersistSelection();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.Check.IsChecked = true;
        }

        PersistSelection();
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.Check.IsChecked = false;
        }

        PersistSelection();
    }

    /// <summary>
    /// 数据源类改动需重建数据源，这里只提示，不擅自重启采集，
    /// 免得用户在调整时界面突然断流。
    /// </summary>
    private void NotifyRestartRequired()
    {
        RestartHint.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 交互自检：逐个触发按钮与开关，确认处理器真的会执行。
    /// <para>
    /// 之所以需要它：设置页大量处理器开头都有 <c>if (_loading) return;</c> 守卫，
    /// 一旦某个初始化异常把 <c>_loading</c> 永久卡在 true，页面看着完全正常，
    /// 但所有控件都变成死的 —— 只有真的点一遍才能发现。
    /// </para>
    /// </summary>
    internal string RunInteractionSelfCheck()
    {
        var report = new System.Text.StringBuilder();
        var failures = 0;

        // 记录每个处理器是否真的跑到了「落盘」这一步。
        void Check(string name, Action action)
        {
            var before = App.Settings?.Current;

            try
            {
                action();

                // 处理器若被 _loading 挡掉，配置不会有任何变化。
                var changed = !ReferenceEquals(before, App.Settings?.Current)
                              || !Equals(before, App.Settings?.Current);

                report.AppendLine($"  {name,-22} : {(changed ? "PASS" : "无响应")}");
                if (!changed)
                {
                    failures++;
                }
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"  {name,-22} : FAIL - {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 先确认基础状态：_loading 必须已经是 false，否则整页都点不动。
        if (_loading)
        {
            report.AppendLine($"  {"_loading 标志",-22} : FAIL - 仍为 true，页面所有控件都是死的");
            return report.ToString();
        }

        report.AppendLine($"  {"_loading 标志",-22} : PASS");

        Check("切换背景面板", () => OnHudPanelBgToggled(this, new RoutedEventArgs()));
        Check("切换紧凑行距", () => OnHudCompactToggled(this, new RoutedEventArgs()));
        Check("配色预设-深色", () => OnHudPresetDarkClick(this, new RoutedEventArgs()));
        Check("配色预设-浅色", () => OnHudPresetLightClick(this, new RoutedEventArgs()));
        Check("配色预设-荧光绿", () => OnHudPresetNeonClick(this, new RoutedEventArgs()));
        Check("悬浮窗字段全选", () => OnHudFieldsAllClick(this, new RoutedEventArgs()));
        Check("悬浮窗字段全不选", () => OnHudFieldsNoneClick(this, new RoutedEventArgs()));
        Check("悬浮窗字段恢复默认", () => OnHudFieldsResetClick(this, new RoutedEventArgs()));
        Check("悬浮窗跟随仪表盘", () => OnHudFieldsFollowDashboardClick(this, new RoutedEventArgs()));
        Check("重置悬浮窗位置", () => OnResetHudBoundsClick(this, new RoutedEventArgs()));
        Check("显示字段全选", () => OnSelectAllClick(this, new RoutedEventArgs()));
        Check("显示字段全不选", () => OnSelectNoneClick(this, new RoutedEventArgs()));
        Check("显示字段恢复默认", () => OnResetFieldsClick(this, new RoutedEventArgs()));
        Check("切换锁定", () => OnHudLockToggled(this, new RoutedEventArgs()));
        Check("切换模拟数据源", () => OnMockToggled(this, new RoutedEventArgs()));
        Check("切换联网获取", () => OnOnlineLookupToggled(this, new RoutedEventArgs()));

        report.AppendLine($"交互测试结论         : {(failures == 0 ? "PASS" : $"FAIL ({failures} 项无响应)")}");

        // 收尾：把锁定态复位，免得自检把用户的悬浮窗留在锁定状态。
        if (App.Settings is { } store)
        {
            store.Save(store.Current with
            {
                Hud = store.Current.Hud with { Locked = false },
            });
        }

        return report.ToString();
    }

    /// <summary>字段勾选项。</summary>
    private sealed record FieldCheckItem(string Id, string DisplayName, string Unit, CheckBox Check);
}
