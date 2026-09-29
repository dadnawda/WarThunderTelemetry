using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarThunderTelemetry.Core.Models;

namespace WarThunderTelemetry.Core.Vehicles;

/// <summary>
/// 载具参数的在线获取与缓存。
/// <para>
/// 阈值来源优先级（从高到低）：
/// </para>
/// <list type="number">
/// <item>本地缓存文件 —— 之前联网成功抓到过，立即返回，不产生网络开销。</item>
/// <item>内置载具库 —— 随程序分发的常见机型静态数据。</item>
/// <item>联网抓取 wiki —— 首次遇到某架载具时抓一次，成功后写入缓存。</item>
/// <item>兵种缺省阈值 —— 上面的全都没有时兜底，保证告警链路不断。</item>
/// </list>
/// <para>
/// <b>设计取向</b>：抓取是「遇到新的才抓」，不做启动时批量拉取 ——
/// 用户开一局只会用到一两架载具，抓那几架就够，没必要把 1300 页跑一遍。
/// 抓取全程在后台线程，绝不阻塞采集与 UI。
/// </para>
/// </summary>
public sealed class OnlineVehicleResolver : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly VehicleDatabase _builtIn;

    /// <summary>并发保护：同一架载具避免被重复抓取。</summary>
    private readonly ConcurrentDictionary<string, Task<VehicleProfile?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, VehicleProfile> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private bool _dirty;

    /// <summary>构造。</summary>
    /// <param name="builtIn">内置载具库，作为联网抓取之外的二级来源。</param>
    /// <param name="cacheDirectory">
    /// 缓存目录。不传则用 <c>%LocalAppData%\WarThunderTelemetry</c>。
    /// </param>
    /// <param name="http">外部注入的 HttpClient（便于测试）；不传则内部创建。</param>
    public OnlineVehicleResolver(
        VehicleDatabase? builtIn = null,
        string? cacheDirectory = null,
        HttpClient? http = null)
    {
        _builtIn = builtIn ?? VehicleDatabase.LoadDefault();

        var directory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WarThunderTelemetry");

        _cachePath = Path.Combine(directory, "vehicle-cache.json");

        if (http is not null)
        {
            _http = http;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // wiki 会拒绝没有 UA 的请求。
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "WarThunderTelemetry/1.0 (+local telemetry dashboard)");
        }

        LoadCache();
    }

    /// <summary>是否允许联网抓取。用户在设置页关掉后纯离线运行。</summary>
    public bool OnlineLookupEnabled { get; set; } = true;

    /// <summary>缓存中已有的载具数量。</summary>
    public int CachedCount => _cache.Count;

    /// <summary>最近一次抓取失败的原因（用于设置页展示）。</summary>
    public string? LastError { get; private set; }

    /// <summary>抓取成功计数，便于观察是否真的在联网。</summary>
    public int FetchSuccessCount { get; private set; }

    /// <summary>
    /// 解析载具阈值来源。
    /// <para>
    /// 同步返回，绝不阻塞：缓存与内置库直接命中；都没有时先返回兵种兜底，
    /// 同时把联网抓取丢到后台，抓到了下次调用就能命中缓存。
    /// </para>
    /// </summary>
    /// <param name="vehicleType">载具代号（<c>indicators.type</c>）。</param>
    /// <param name="army">兵种（<c>indicators.army</c>）。</param>
    /// <param name="isExactMatch">是否命中了精确数据（缓存或内置库）。</param>
    public VehicleProfile Resolve(string? vehicleType, string? army, out bool isExactMatch)
    {
        var key = vehicleType?.Trim();
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                isExactMatch = true;
                return cached;
            }

            if (_builtIn.TryGet(key) is { } builtIn)
            {
                // 内置库命中即视为精确 —— 它是人工核对过的静态数据。
                isExactMatch = true;
                return builtIn;
            }

            // 没数据：后台抓一次，本次先给兜底。
            if (OnlineLookupEnabled)
            {
                _ = EnsureFetchedAsync(key);
            }
        }

        isExactMatch = false;
        return FallbackProfiles.Create(vehicleType, army);
    }

    /// <summary>
    /// 确保某架载具已抓取。同一代号并发调用只会真正抓一次。
    /// </summary>
    /// <returns>抓到返回档案，失败返回 <c>null</c>。</returns>
    public Task<VehicleProfile?> EnsureFetchedAsync(string vehicleType)
    {
        if (string.IsNullOrWhiteSpace(vehicleType))
        {
            return Task.FromResult<VehicleProfile?>(null);
        }

        var key = vehicleType.Trim();

        if (_cache.TryGetValue(key, out var cached))
        {
            return Task.FromResult<VehicleProfile?>(cached);
        }

        if (_builtIn.TryGet(key) is { } builtIn)
        {
            return Task.FromResult<VehicleProfile?>(builtIn);
        }

        return _inFlight.GetOrAdd(key, k => FetchAndCacheAsync(k));
    }

    /// <summary>
    /// 主动抓取一批载具（设置页的「更新参数」按钮用）。
    /// </summary>
    public async Task<int> PrefetchAsync(
        IEnumerable<string> vehicleTypes,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var keys = vehicleTypes
            .Where(static k => !string.IsNullOrWhiteSpace(k))
            .Select(static k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var done = 0;
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await EnsureFetchedAsync(key).ConfigureAwait(false) is not null)
            {
                done++;
            }

            progress?.Report(done);

            // 对 wiki 客气一点，避免被限流。
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken)
                .ConfigureAwait(false);
        }

        return done;
    }

    // ------------------------------------------------------------ 抓取

    private async Task<VehicleProfile?> FetchAndCacheAsync(string vehicleType)
    {
        try
        {
            var profile = await FetchFromWikiAsync(vehicleType).ConfigureAwait(false);
            if (profile is null)
            {
                LastError = $"wiki 上找不到 {vehicleType}（或页面结构已变化）";
                return null;
            }

            _cache[vehicleType] = profile;
            FetchSuccessCount++;
            LastError = null;
            _dirty = true;

            return profile;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 网络问题不该让告警链路停摆 —— 记录原因，继续走兜底。
            LastError = $"{vehicleType}: {ex.GetType().Name}";
            return null;
        }
        finally
        {
            _inFlight.TryRemove(vehicleType, out _);
        }
    }

    /// <summary>
    /// 从 wiki 抓一架载具。
    /// <para>
    /// 两步：先用 /aviation 页把 slug 全量取下来建索引（一次即可，之后靠内存映射），
    /// 再按索引去对应的 unit 页。之所以不能直接拼 URL，是因为 wiki 的 slug
    /// 命名极不规则（<c>spitfire_ix</c>、<c>hurricane_mk1</c>、<c>a6m2_zero</c>），
    /// 从游戏代号推断不出来。
    /// </para>
    /// </summary>
    private async Task<VehicleProfile?> FetchFromWikiAsync(string vehicleType)
    {
        var (slugs, normalized) = await GetSlugIndexAsync().ConfigureAwait(false);

        var slug = ResolveSlug(slugs, normalized, vehicleType);
        if (slug is null)
        {
            // slug 索引里没有这架 —— 可能是新载具，也可能代号体系不同。
            LastError = $"wiki 索引中没有 {vehicleType}";
            return null;
        }

        var html = await GetStringAsync($"{WikiPageParser.BaseUrl}/unit/{slug}")
            .ConfigureAwait(false);

        if (html is null)
        {
            return null;
        }

        // gameId 是权威代号，取出来核对，避免 slug 撞车导致张冠李戴。
        var gameId = WikiPageParser.ParseGameId(html);
        if (gameId is null)
        {
            LastError = $"{vehicleType}: 页面结构已变化，取不到 gameId";
            return null;
        }

        if (!gameId.Equals(vehicleType, StringComparison.OrdinalIgnoreCase) &&
            !gameId.Equals(slug, StringComparison.OrdinalIgnoreCase))
        {
            LastError = $"{vehicleType}: slug {slug} 指向的是 {gameId}，已放弃";
            return null;
        }

        return WikiPageParser.ParseProfile(html, slug);
    }

    /// <summary>
    /// 找代号对应的 slug。
    /// <para>
    /// 分三级尝试，从精确到模糊：
    /// </para>
    /// <list type="number">
    /// <item>完全相同 —— 大多数载具的代号与 wiki slug 一致。</item>
    /// <item>分隔符互换 —— 游戏用 <c>_</c>、wiki 有时用 <c>-</c>（反之亦然）。</item>
    /// <item>归一化后匹配 —— 去掉分隔符再比，能救回 <c>f4u-1a</c> ↔ <c>f4u_1a</c> 这类。</item>
    /// </list>
    /// <para>
    /// <b>刻意不做更激进的模糊匹配</b>：像 <c>spitfire_mk9</c>（游戏）对
    /// <c>spitfire_ix</c>（wiki）这种「罗马数字 vs 阿拉伯数字」的差异，
    /// 靠字符串相似度猜会引入错误匹配，把 A 机的阈值安到 B 机上 ——
    /// 这比抓不到更危险。这类载具就让它走兵种兜底。
    /// </para>
    /// </summary>
    private static string? ResolveSlug(
        IReadOnlyCollection<string> slugs,
        IReadOnlyDictionary<string, string> normalized,
        string vehicleType)
    {
        // 1. 完全相同（含大小写不敏感）
        foreach (var slug in slugs)
        {
            if (slug.Equals(vehicleType, StringComparison.OrdinalIgnoreCase))
            {
                return slug;
            }
        }

        // 2. 分隔符互换
        var alternate = vehicleType.Contains('_')
            ? vehicleType.Replace('_', '-')
            : vehicleType.Replace('-', '_');

        foreach (var slug in slugs)
        {
            if (slug.Equals(alternate, StringComparison.OrdinalIgnoreCase))
            {
                return slug;
            }
        }

        // 3. 去掉全部分隔符后比对
        var key = StripSeparators(vehicleType);
        return normalized.TryGetValue(key, out var hit) ? hit : null;
    }

    private static string StripSeparators(string value) =>
        value.Replace("_", string.Empty, StringComparison.Ordinal)
             .Replace("-", string.Empty, StringComparison.Ordinal)
             .Replace(" ", string.Empty, StringComparison.Ordinal)
             .ToLowerInvariant();

    /// <summary>
    /// 取「slug 索引」。只会真正联网一次，之后走内存缓存。
    /// </summary>
    private async Task<(IReadOnlyCollection<string> Slugs, IReadOnlyDictionary<string, string> Normalized)>
        GetSlugIndexAsync()
    {
        if (_slugIndex is { } cached && _normalizedIndex is { } cachedNormalized)
        {
            return (cached, cachedNormalized);
        }

        await _slugGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_slugIndex is { } filled && _normalizedIndex is { } filledNormalized)
            {
                return (filled, filledNormalized);
            }

            var slugs = await LoadSlugListAsync().ConfigureAwait(false);

            // 归一化索引：去掉分隔符后比对，用来兜「_ 与 - 混用」的差异。
            var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var slug in slugs)
            {
                normalized.TryAdd(StripSeparators(slug), slug);
            }

            _normalizedIndex = normalized;
            _slugIndex = slugs;

            return (slugs, normalized);
        }
        finally
        {
            _slugGate.Release();
        }
    }

    /// <summary>
    /// 取 slug 列表：优先用随程序分发的静态索引，没有才联网抓。
    /// <para>
    /// 静态索引能让「代号 → slug」这一步完全离线完成 ——
    /// 只有真正去读某架载具的参数页时才需要联网。
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadSlugListAsync()
    {
        if (VehicleSlugIndex.Load() is { Count: > 0 } embedded)
        {
            return embedded;
        }

        var html = await GetStringAsync(WikiPageParser.AviationUrl).ConfigureAwait(false);

        return html is null
            ? []
            : WikiPageParser.ParseSlugs(html);
    }

    private readonly SemaphoreSlim _slugGate = new(1, 1);
    private volatile IReadOnlyList<string>? _slugIndex;
    private volatile IReadOnlyDictionary<string, string>? _normalizedIndex;

    private async Task<string?> GetStringAsync(string url)
    {
        using var response = await _http.GetAsync(url).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------ 缓存

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var json = File.ReadAllText(_cachePath);
            var stored = JsonSerializer.Deserialize<Dictionary<string, VehicleProfile>>(
                json, JsonOptions);

            if (stored is null)
            {
                return;
            }

            foreach (var (key, profile) in stored)
            {
                if (profile is not null)
                {
                    _cache[key] = profile;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 缓存损坏不该影响启动 —— 当没有缓存，重新抓。
            LastError = "载具缓存读取失败，已忽略";
        }
    }

    /// <summary>
    /// 把内存缓存落盘。抓取成功后会调用，也可由调用方在退出前显式调用。
    /// </summary>
    public async Task SaveCacheAsync()
    {
        if (!_dirty)
        {
            return;
        }

        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 先写临时文件再替换，避免写一半被中断导致缓存损坏。
            var temp = _cachePath + ".tmp";
            var json = JsonSerializer.Serialize(
                new Dictionary<string, VehicleProfile>(_cache, StringComparer.OrdinalIgnoreCase),
                JsonOptions);

            await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
            File.Move(temp, _cachePath, overwrite: true);

            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = "载具缓存写入失败";
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>清空缓存（设置页的「清空载具缓存」用）。</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _dirty = true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await SaveCacheAsync().ConfigureAwait(false);
        _http.Dispose();
        _slugGate.Dispose();
        _saveGate.Dispose();
    }
}
