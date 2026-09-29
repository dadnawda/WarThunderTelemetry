using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WarThunderTelemetry.Core;
using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Parsing;

namespace WarThunderTelemetry.App.Views;

/// <summary>
/// 全字段表格：列出游戏接口给的一切字段，支持搜索与来源筛选。
/// <para>
/// 采用「游戏给什么就显示什么」的策略 —— 游戏更新导致字段增减时，
/// 这里不会丢数据，也不需要跟着改代码。
/// </para>
/// </summary>
public sealed partial class RawFieldsPage : Page, IStatusAware
{
    private readonly List<FieldRow> _allRows = new(128);
    private TelemetryStatus? _latest;

    /// <summary>
    /// XAML 里 <c>SourcePicker</c> 的首项带 <c>IsSelected="True"</c>，
    /// 因此 <c>SelectionChanged</c> 会在 <c>InitializeComponent()</c> 期间就触发，
    /// 而那时声明在其后的 <c>CountText</c> / <c>FieldList</c> 还是 null。
    /// 用这个标志把初始化期的回调挡掉。
    /// </summary>
    private bool _ready;

    /// <summary>构造。</summary>
    public RawFieldsPage()
    {
        InitializeComponent();

        // XAML 全部就位后才允许筛选逻辑访问控件。
        Loaded += (_, _) =>
        {
            _ready = true;
            ApplyFilter();
        };
    }

    /// <inheritdoc />
    public void OnStatusUpdated(TelemetryStatus status)
    {
        _latest = status;
        Rebuild();
    }

    private void Rebuild()
    {
        if (_latest is not { } status)
        {
            return;
        }

        var resolver = App.Telemetry?.Resolver ?? FieldCatalog.CreateResolver();
        _allRows.Clear();

        // 收集三个来源的全部原始字段，不做任何过滤 —— 保证零遗漏。
        Collect(_allRows, status.Snapshot.State.RawValues, "state", resolver);
        Collect(_allRows, status.Snapshot.Indicators.RawValues, "indicators", resolver);
        Collect(_allRows, status.Snapshot.Derived.RawValues, "derived", resolver);

        _allRows.Sort(static (a, b) =>
        {
            var bySource = string.CompareOrdinal(a.Source, b.Source);
            return bySource != 0 ? bySource : string.CompareOrdinal(a.RawKey, b.RawKey);
        });

        ApplyFilter();
    }

    private static void Collect(
        List<FieldRow> target,
        IReadOnlyDictionary<string, string> values,
        string source,
        AliasResolver resolver)
    {
        foreach (var (key, value) in values)
        {
            var descriptor = resolver.ResolveDescriptor(key);

            target.Add(new FieldRow
            {
                RawKey = key,
                Value = value,
                Source = source,
                LocalId = descriptor?.Id ?? string.Empty,
                DisplayName = descriptor?.DisplayName ?? string.Empty,
            });
        }
    }

    private void ApplyFilter()
    {
        // 初始化阶段控件尚未就位，直接跳过 —— 空表本来也没什么可筛的。
        if (!_ready)
        {
            return;
        }

        var keyword = SearchBox.Text?.Trim() ?? string.Empty;
        var sourceFilter = (SourcePicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部来源";

        var filtered = _allRows.Where(row =>
        {
            if (sourceFilter != "全部来源" &&
                !row.Source.Equals(sourceFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (keyword.Length == 0)
            {
                return true;
            }

            return row.RawKey.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                   || row.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                   || row.LocalId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                   || row.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        FieldList.ItemsSource = filtered;
        CountText.Text = $"共 {filtered.Count} / {_allRows.Count} 个字段";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    /// <summary>表格行。</summary>
    private sealed class FieldRow
    {
        /// <summary>游戏接口返回的原始键名。</summary>
        public required string RawKey { get; init; }

        /// <summary>原始值。</summary>
        public required string Value { get; init; }

        /// <summary>来源端点。</summary>
        public required string Source { get; init; }

        /// <summary>映射到的本地字段 Id（未登记时为空）。</summary>
        public required string LocalId { get; init; }

        /// <summary>本地显示名。</summary>
        public required string DisplayName { get; init; }

        /// <summary>供 ListView 绑定的展示文本。</summary>
        public string SourceDisplay => Source switch
        {
            "state" => "/state",
            "indicators" => "/indicators",
            "derived" => "本地计算",
            _ => Source,
        };

        /// <summary>本地字段展示文本。</summary>
        public string LocalDisplay => DisplayName.Length > 0 ? $"{DisplayName} ({LocalId})" : "—";
    }
}
