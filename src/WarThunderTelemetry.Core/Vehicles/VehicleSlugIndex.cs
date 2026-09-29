using System.Reflection;
using System.Text.Json;

namespace WarThunderTelemetry.Core.Vehicles;

/// <summary>
/// wiki 的载具 slug 索引（静态，随程序分发）。
/// <para>
/// 用途：把游戏接口给的载具代号映射到 wiki 的页面 slug。
/// <b>这一步无法从代号推断</b> —— wiki 的命名极不规则
/// （<c>spitfire_ix</c>、<c>hurricane_mk1</c>、<c>a6m2_zero</c>），
/// 所以只能把全量列表固化下来。
/// </para>
/// <para>
/// 有了它，「代号 → slug」完全离线完成，只有真正要读某架载具的参数页时才联网。
/// 索引由 <c>tools/scrape_vehicles.py --index-only</c> 生成。
/// </para>
/// <para>
/// 索引里没有的载具（通常是游戏新版本刚加的）会走兵种兜底阈值，
/// 不影响使用，只是阈值粗一些。
/// </para>
/// </summary>
public static class VehicleSlugIndex
{
    private const string ResourceSuffix = "VehicleSlugs.json";

    private static readonly Lazy<IReadOnlyList<string>> LazySlugs = new(LoadCore);

    /// <summary>全部 wiki slug。加载失败时为空列表（不抛异常）。</summary>
    public static IReadOnlyList<string> All => LazySlugs.Value;

    /// <summary>加载索引；不可用时返回空列表。</summary>
    public static IReadOnlyList<string> Load() => LazySlugs.Value;

    private static IReadOnlyList<string> LoadCore()
    {
        try
        {
            var assembly = typeof(VehicleSlugIndex).Assembly;

            var name = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));

            if (name is null)
            {
                return [];
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return [];
            }

            var slugs = JsonSerializer.Deserialize<List<string>>(stream);

            return slugs is null
                ? []
                : slugs.Where(static s => !string.IsNullOrWhiteSpace(s)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 索引坏了不该拦住程序启动 —— 退化成「运行时联网取索引」。
            return [];
        }
    }
}
