using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarThunderTelemetry.Core.Models;

namespace WarThunderTelemetry.Core.Vehicles;

/// <summary>
/// 内置载具参数库。
/// <para>
/// 从嵌入资源 <c>Vehicles/VehicleData.json</c> 加载，运行时无需外部文件。
/// 匹配不到具体载具时，由 <see cref="FallbackProfiles"/> 按兵种兜底。
/// </para>
/// <para>后续扩展：若将来要联网从官方 wiki 补全，只需在 <see cref="TryGet"/> 未命中时
/// 挂一个异步补全钩子，其余调用方无需改动。</para>
/// </summary>
public sealed class VehicleDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, VehicleProfile> _byType;

    private VehicleDatabase(Dictionary<string, VehicleProfile> byType)
    {
        _byType = byType;
    }

    /// <summary>库中载具数量。</summary>
    public int Count => _byType.Count;

    /// <summary>全部载具（按代号）。</summary>
    public IReadOnlyDictionary<string, VehicleProfile> All => _byType;

    /// <summary>
    /// 从嵌入资源加载内置载具库。
    /// </summary>
    public static VehicleDatabase LoadDefault()
    {
        var assembly = typeof(VehicleDatabase).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("VehicleData.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未找到嵌入资源 VehicleData.json。");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法打开嵌入资源 {resourceName}。");

        return Load(stream);
    }

    /// <summary>
    /// 从数据流加载。
    /// </summary>
    public static VehicleDatabase Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var document = JsonSerializer.Deserialize<VehicleDataDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("载具数据反序列化结果为空。");

        var map = new Dictionary<string, VehicleProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, profile) in document.Vehicles)
        {
            if (profile is null)
            {
                continue;
            }

            // 以 JSON 中的键为准填入 type 与显示名，避免每架都重复书写。
            map[key] = profile with
            {
                Type = string.IsNullOrWhiteSpace(profile.Type) ? key : profile.Type,
                DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? key : profile.DisplayName,
            };
        }

        return new VehicleDatabase(map);
    }

    /// <summary>
    /// 按载具代号精确查找。代号大小写不敏感。
    /// </summary>
    public VehicleProfile? TryGet(string? vehicleType)
    {
        if (string.IsNullOrWhiteSpace(vehicleType))
        {
            return null;
        }

        return _byType.TryGetValue(vehicleType.Trim(), out var profile) ? profile : null;
    }

    /// <summary>
    /// 按载具代号查找；未命中时返回兵种兜底档案。
    /// </summary>
    /// <param name="vehicleType">载具代号（<c>indicators.type</c>）。</param>
    /// <param name="army">兵种（<c>indicators.army</c>），用于兜底选择。</param>
    /// <param name="isExactMatch">是否命中了库中的具体载具。</param>
    public VehicleProfile ResolveOrFallback(string? vehicleType, string? army, out bool isExactMatch)
    {
        var exact = TryGet(vehicleType);
        if (exact is not null)
        {
            isExactMatch = true;
            return exact;
        }

        isExactMatch = false;
        return FallbackProfiles.Create(vehicleType, army);
    }

    /// <summary>JSON 根结构。</summary>
    private sealed class VehicleDataDocument
    {
        [JsonPropertyName("vehicles")]
        public Dictionary<string, VehicleProfile?> Vehicles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
