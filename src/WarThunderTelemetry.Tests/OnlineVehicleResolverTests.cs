using WarThunderTelemetry.Core.Models;
using WarThunderTelemetry.Core.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace WarThunderTelemetry.Tests;

/// <summary>
/// 联网抓取链路的集成测试。
/// <para>
/// <b>默认全部跳过</b>：这些用例要访问 wiki，不能在 CI 或无网环境下拖累测试。
/// 需要真跑时用环境变量打开：
/// </para>
/// <code>
/// set WT_ONLINE_TESTS=1
/// dotnet vstest WarThunderTelemetry.Tests.dll
/// </code>
/// <para>
/// 之所以保留它们：解析器依赖线上页面结构，wiki 改版时只有真抓一次才发现得了。
/// </para>
/// </summary>
public sealed class OnlineVehicleResolverTests
{
    private static bool OnlineEnabled =>
        Environment.GetEnvironmentVariable("WT_ONLINE_TESTS") == "1";

    private readonly ITestOutputHelper _output;

    /// <summary>构造。</summary>
    public OnlineVehicleResolverTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public async Task 抓取未收录载具_能拿到真实参数()
    {
        Skip.IfNot(OnlineEnabled, "需要 WT_ONLINE_TESTS=1 才跑联网测试");

        await using var resolver = new OnlineVehicleResolver(
            cacheDirectory: Path.Combine(Path.GetTempPath(), "wt-test-" + Guid.NewGuid()));

        // la_9 在内置库里，换一架内置库没有的机型来验证真实联网路径。
        var profile = await resolver.EnsureFetchedAsync("spitfire_ix");

        _output.WriteLine($"抓到：{profile?.DisplayName} / {profile?.Nation} / " +
                          $"IAS={profile?.MaxSpeedIas} G=+{profile?.GLimitPositive}");

        Assert.NotNull(profile);
        Assert.True(profile.MaxSpeedIas is > 300 and < 1200,
            $"超速上限不合理：{profile.MaxSpeedIas}");
        Assert.True(profile.GLimitPositive is > 0 and < 20,
            $"G 限不合理：{profile.GLimitPositive}");
    }

    [SkippableFact]
    public async Task 分隔符差异能自动归一()
    {
        Skip.IfNot(OnlineEnabled, "需要 WT_ONLINE_TESTS=1 才跑联网测试");

        await using var resolver = new OnlineVehicleResolver(
            cacheDirectory: Path.Combine(Path.GetTempPath(), "wt-test-" + Guid.NewGuid()));

        // wiki 上写作 bf-109f-4，游戏里可能是 bf-109f-4 或 bf_109f_4，
        // 归一化索引应当都能对到同一页。
        var dashed = await resolver.EnsureFetchedAsync("bf-109f-4");
        Assert.NotNull(dashed);
        Assert.Equal("Bf 109 F-4", dashed.DisplayName);
    }

    [SkippableFact]
    public async Task 代号体系不同时_不硬猜而是回退兜底()
    {
        Skip.IfNot(OnlineEnabled, "需要 WT_ONLINE_TESTS=1 才跑联网测试");

        await using var resolver = new OnlineVehicleResolver(
            cacheDirectory: Path.Combine(Path.GetTempPath(), "wt-test-" + Guid.NewGuid()));

        // spitfire_mk9（游戏风格写法）在 wiki 上叫 spitfire_ix（罗马数字），
        // 两者语义相同但字符串差很多。我们刻意不做激进模糊匹配 ——
        // 猜错会把别的机型的阈值安上来，比抓不到更危险。
        var profile = resolver.Resolve("spitfire_mk9", "air", out var isExact);

        Assert.False(isExact);
        Assert.Equal("fallback", profile.Source);
    }

    [SkippableFact]
    public async Task 抓取后写缓存_再读时不再联网()
    {
        Skip.IfNot(OnlineEnabled, "需要 WT_ONLINE_TESTS=1 才跑联网测试");

        var dir = Path.Combine(Path.GetTempPath(), "wt-test-" + Guid.NewGuid());

        string slug;
        await using (var first = new OnlineVehicleResolver(cacheDirectory: dir))
        {
            var profile = await first.EnsureFetchedAsync("spitfire_ix");
            Assert.NotNull(profile);
            slug = profile.DisplayName;
            await first.SaveCacheAsync();
        }

        // 第二个实例应从缓存直接命中，且不需要联网就能返回数值。
        await using var second = new OnlineVehicleResolver(cacheDirectory: dir)
        {
            OnlineLookupEnabled = false,
        };

        var cached = second.Resolve("spitfire_ix", "air", out var isExact);

        Assert.True(isExact, "缓存应视为精确命中");
        Assert.Equal(slug, cached.DisplayName);
        Assert.NotNull(cached.MaxSpeedIas);
    }

    [SkippableFact]
    public async Task 关闭联网时_未收录载具退回兵种兜底()
    {
        Skip.IfNot(OnlineEnabled, "需要 WT_ONLINE_TESTS=1 才跑联网测试");

        await using var resolver = new OnlineVehicleResolver(
            cacheDirectory: Path.Combine(Path.GetTempPath(), "wt-test-" + Guid.NewGuid()))
        {
            OnlineLookupEnabled = false,
        };

        var profile = resolver.Resolve("某架不存在的飞机_xyz", "air", out var isExact);

        Assert.False(isExact);
        Assert.Equal("fallback", profile.Source);
    }

    [SkippableFact]
    public async Task 缓存损坏时_不抛异常并回到可用状态()
    {
        Skip.IfNot(OnlineEnabled, "需要 WT_ONLINE_TESTS=1 才跑联网测试");

        var dir = Path.Combine(Path.GetTempPath(), "wt-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "vehicle-cache.json"), "{ 这不是合法 JSON");

        await using var resolver = new OnlineVehicleResolver(cacheDirectory: dir);

        // 不抛异常，且退回兜底而不是崩掉。
        var profile = resolver.Resolve("la_9", "air", out _);
        Assert.NotNull(profile);
    }
}
