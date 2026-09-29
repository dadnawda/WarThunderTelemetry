using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WarThunderTelemetry.App.Services;
using WarThunderTelemetry.Core;
using WarThunderTelemetry.Core.Abstractions;
using WarThunderTelemetry.Core.Alerts;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;
using Windows.UI;

namespace WarThunderTelemetry.App.Views;

/// <summary>
/// 仪表盘：核心飞行数据卡片 + 派生指标 + 当前生效阈值 + 告警条。
/// </summary>
public sealed partial class DashboardPage : Page, IStatusAware
{
    /// <summary>核心飞行数据的字段 Id（顺序即显示顺序）。</summary>
    private static readonly string[] CoreFieldIds =
    [
        "ias", "tas", "altitude", "vertical_speed",
        "mach", "throttle", "rpm", "g_load",
        "fuel", "heading", "pitch", "bank",
    ];

    /// <summary>派生指标的字段 Id。</summary>
    private static readonly string[] DerivedFieldIds =
    [
        "derived_accel", "derived_energy", "derived_climb_angle",
        "derived_turn_rate", "derived_turn_radius", "derived_turn_time",
    ];

    private readonly Dictionary<string, (TextBlock Value, Border Card)> _coreCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (TextBlock Value, Border Card)> _derivedCards = new(StringComparer.OrdinalIgnoreCase);

    private TelemetryStatus? _latest;

    /// <summary>构造。</summary>
    public DashboardPage()
    {
        InitializeComponent();

        var resolver = App.Telemetry?.Resolver ?? FieldCatalog.CreateResolver();

        BuildCards(CardList, CoreFieldIds, resolver, _coreCards);
        BuildCards(DerivedCardList, DerivedFieldIds, resolver, _derivedCards);
    }

    private void BuildCards(
        ItemsControl host,
        string[] fieldIds,
        AliasResolver resolver,
        Dictionary<string, (TextBlock, Border)> store)
    {
        host.Items.Clear();

        foreach (var id in fieldIds)
        {
            var descriptor = resolver.GetById(id);
            if (descriptor is null)
            {
                continue;
            }

            var (card, valueText) = CreateCard(descriptor);
            host.Items.Add(card);
            store[id] = (valueText, card);
        }
    }

    private static (Border Card, TextBlock ValueText) CreateCard(FieldDescriptor descriptor)
    {
        var title = new TextBlock
        {
            Text = descriptor.DisplayName,
            FontSize = 12,
            Opacity = 0.75,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "WtSecondaryTextBrush");

        var value = new TextBlock
        {
            Text = "–",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "WtDataValueBrush");

        var unit = new TextBlock
        {
            Text = descriptor.Unit,
            FontSize = 11,
            Opacity = 0.65,
            Margin = new Thickness(4, 0, 0, 3),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "WtSecondaryTextBrush");

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal };
        valueRow.Children.Add(value);
        if (!string.IsNullOrEmpty(descriptor.Unit))
        {
            valueRow.Children.Add(unit);
        }

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(title);
        content.Children.Add(valueRow);

        var card = new Border
        {
            Width = 200,
            Height = 92,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12),
            Child = content,
        };
        card.SetResourceReference(Border.BackgroundProperty, "WtCardBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "WtCardBorderBrush");

        return (card, value);
    }

    /// <inheritdoc />
    public void OnStatusUpdated(TelemetryStatus status)
    {
        _latest = status;

        var snapshot = status.Snapshot;
        var resolver = App.Telemetry?.Resolver ?? FieldCatalog.CreateResolver();

        foreach (var (id, (valueText, card)) in _coreCards)
        {
            UpdateCard(valueText, card, snapshot, resolver, id);
        }

        foreach (var (id, (valueText, card)) in _derivedCards)
        {
            UpdateCard(valueText, card, snapshot, resolver, id);
        }

        UpdateAlertBar(status);
        UpdateThresholds(status);
    }

    private static void UpdateCard(
        TextBlock valueText,
        Border card,
        TelemetrySnapshot snapshot,
        AliasResolver resolver,
        string fieldId)
    {
        var descriptor = resolver.GetById(fieldId);
        if (descriptor is null)
        {
            return;
        }

        var result = snapshot.Resolve(descriptor, resolver);

        // 未命中统一显示 –，不报错也不留空白 —— 不同机型缺字段是常态。
        valueText.Text = result.Found
            ? FormatValue(descriptor, result)
            : "–";

        // 缺数据的卡片淡化，便于一眼看出哪些字段该机型不提供。
        card.Opacity = result.Found ? 1.0 : 0.55;
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

        // 大数值缩写（RPM 等）
        if (descriptor.AbbreviateLarge && Math.Abs(number) >= 1000)
        {
            return (number / 1000).ToString("F1", CultureInfo.InvariantCulture) + "k";
        }

        var decimals = descriptor.Decimals >= 0
            ? descriptor.Decimals
            : Math.Abs(number) >= 100 ? 0 : 1;

        return number.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    private void UpdateAlertBar(TelemetryStatus status)
    {
        if (status.Alerts.Count == 0)
        {
            AlertBar.IsOpen = false;
            return;
        }

        var top = status.Alerts[0];
        AlertBar.IsOpen = true;
        AlertBar.Severity = top.Severity switch
        {
            AlertSeverity.Critical => InfoBarSeverity.Error,
            AlertSeverity.Warning => InfoBarSeverity.Warning,
            AlertSeverity.Caution => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };

        AlertBar.Title = status.Alerts.Count == 1
            ? top.Title
            : $"{top.Title}（共 {status.Alerts.Count} 条告警）";
        AlertBar.Message = top.Detail;
    }

    private void UpdateThresholds(TelemetryStatus status)
    {
        var t = status.Thresholds;
        var builder = new StringBuilder();

        var source = status.IsExactVehicleMatch
            ? $"来源：载具库「{status.VehicleProfile?.DisplayName}」"
            : "来源：兵种通用缺省值（该载具未收录）";
        builder.AppendLine(source);

        builder.Append("超速上限 IAS：").AppendLine(Format(t.MaxSpeedIas, "km/h"));
        builder.Append("马赫上限：").AppendLine(Format(t.MachLimit, "M"));
        builder.Append("过载上限：").Append('+').Append(Format(t.GLimitPositive, "g"))
               .Append(" / -").AppendLine(Format(t.GLimitNegative, "g"));
        builder.Append("起落架限速：").AppendLine(Format(t.GearSpeedLimit, "km/h"));
        builder.Append("襟翼限速：").AppendLine(Format(t.FlapSpeedLimit, "km/h"));
        builder.Append("失速速度：").AppendLine(Format(t.StallSpeedIas, "km/h"));
        builder.Append("低油量阈值：").Append(t.FuelLowPercent.ToString("F0", CultureInfo.InvariantCulture)).AppendLine("%");

        ThresholdText.Text = builder.ToString();
    }

    private static string Format(double? value, string unit) =>
        value is { } v
            ? $"{v.ToString("F1", CultureInfo.InvariantCulture)} {unit}"
            : "未收录（不告警）";
}
