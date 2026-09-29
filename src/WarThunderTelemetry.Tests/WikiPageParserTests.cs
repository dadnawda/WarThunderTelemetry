using WarThunderTelemetry.Core.Vehicles;
using Xunit;

namespace WarThunderTelemetry.Tests;

/// <summary>
/// wiki 页面解析器测试。
/// <para>
/// 用的是从线上抓下来的真实页面存档（<c>Fixtures/</c>），不是手写的最小样例 ——
/// 只有这样才测得出 wiki 那三种参数行形态、嵌套 tooltip、国旗残留字符这些坑。
/// </para>
/// </summary>
public sealed class WikiPageParserTests
{
    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // ------------------------------------------------------ gameId

    [Fact]
    public void 解析gameId_取自页面内嵌JSON()
    {
        var html = LoadFixture("unit-la-9.html");

        Assert.Equal("la-9", WikiPageParser.ParseGameId(html));
    }

    [Fact]
    public void 解析gameId_下划线形式代号也能正确取出()
    {
        // 这架是 wiki slug（a6m2_zero）与游戏代号完全同形的情形，
        // 用来确认取的是内嵌 JSON 而不是从 slug 拼的。
        var html = BuildMinimalPage(gameId: "b_17_e");

        Assert.Equal("b_17_e", WikiPageParser.ParseGameId(html));
    }

    [Fact]
    public void 解析gameId_页面无内嵌JSON时返回null()
    {
        Assert.Null(WikiPageParser.ParseGameId("<html><body>无内容</body></html>"));
    }

    // ------------------------------------------------------ 性能参数

    [Fact]
    public void 解析La9参数_与官方wiki一致()
    {
        var html = LoadFixture("unit-la-9.html");

        var profile = WikiPageParser.ParseProfile(html, "la-9");

        Assert.NotNull(profile);
        Assert.Equal("La-9", profile.DisplayName);
        Assert.Equal(850, profile.MaxSpeedIas);
        Assert.Equal(0.8, profile.MachLimit);
        Assert.Equal(13, profile.GLimitPositive);
        Assert.Equal(8, profile.GLimitNegative);
        Assert.Equal(320, profile.GearSpeedLimit);
        Assert.Equal(490, profile.FlapSpeedLimit);   // 三档取战斗档（最后一个）
        Assert.Equal(19.2, profile.RateOfClimb);     // 多档取 RB 参考值（第一个）
        Assert.Equal(21, profile.TurnTime);
        Assert.Equal(13000, profile.MaxAltitude);    // 带千分位逗号
        Assert.Equal("air", profile.Army);
        Assert.Equal("fighter", profile.Role);
        Assert.Equal("USSR", profile.Nation);
    }

    [Fact]
    public void 解析A20G_定位与国旗识别正确()
    {
        var html = LoadFixture("unit-a-20g.html");

        var profile = WikiPageParser.ParseProfile(html, "a-20g");

        Assert.NotNull(profile);
        Assert.Equal("A-20G-25", profile.DisplayName);
        Assert.Equal("USA", profile.Nation);
        Assert.Equal(696, profile.MaxSpeedIas);
        Assert.Equal(296, profile.GearSpeedLimit);
        Assert.Equal(428, profile.FlapSpeedLimit);
        Assert.Equal(6, profile.GLimitPositive);
        Assert.Equal(3, profile.GLimitNegative);
    }

    [Fact]
    public void 解析G限值_格式规范()
    {
        var html = LoadFixture("unit-la-9.html");

        var profile = WikiPageParser.ParseProfile(html, "la-9");

        Assert.NotNull(profile);

        // wiki 写作「≈ -8/13 G」—— 负号在左、正号在右，解析后正负必须各归各位。
        Assert.True(profile.GLimitPositive > 0);
        Assert.True(profile.GLimitNegative > 0);
        Assert.Equal(13, profile.GLimitPositive);
        Assert.Equal(8, profile.GLimitNegative);
    }

    [Fact]
    public void 解析襟翼限速_单档与三档都能取到()
    {
        // 三档 L/T/C：取最后一个（战斗档）
        var three = BuildMinimalPage(
            gameId: "t",
            chars: new[] { ("Flap Speed Limit (IAS)", "290 / 451 / 490 km/h") });

        // 单档：直接取该值
        var single = BuildMinimalPage(
            gameId: "t",
            chars: new[] { ("Flap Speed Limit (IAS)", "320 km/h") });

        Assert.Equal(490, WikiPageParser.ParseProfile(three, "t")!.FlapSpeedLimit);
        Assert.Equal(320, WikiPageParser.ParseProfile(single, "t")!.FlapSpeedLimit);
    }

    [Fact]
    public void 解析带千分位的数值_逗号不干扰()
    {
        var html = BuildMinimalPage(
            gameId: "t",
            chars: new[] { ("Max altitude", "13,000 m") });

        Assert.Equal(13000, WikiPageParser.ParseProfile(html, "t")!.MaxAltitude);
    }

    [Fact]
    public void 参数缺失时_留空而非猜测()
    {
        // 只给一个字段，其余全缺 —— 缺的必须是 null，不能填 0 或默认值。
        var html = BuildMinimalPage(
            gameId: "t",
            chars: new[] { ("Max Speed Limit (IAS)", "800 km/h") });

        var profile = WikiPageParser.ParseProfile(html, "t");

        Assert.NotNull(profile);
        Assert.Equal(800, profile.MaxSpeedIas);
        Assert.Null(profile.MachLimit);
        Assert.Null(profile.GLimitPositive);
        Assert.Null(profile.GearSpeedLimit);
        Assert.Null(profile.FlapSpeedLimit);
        Assert.Null(profile.StallSpeedIas);
    }

    [Fact]
    public void 无参数区时返回null()
    {
        Assert.Null(WikiPageParser.ParseProfile(
            "<html><body>这是个没有参数表的页面</body></html>", "x"));
    }

    // ------------------------------------------------------ 地图（slug 索引）

    [Fact]
    public void 解析slug列表_去重且排序()
    {
        const string html = """
            <a href="/unit/la-9">La-9</a>
            <a href="/unit/a6m2_zero">A6M2</a>
            <a href="/unit/la-9">重复项</a>
            <a href="/unit/b-17e#specification">B-17E</a>
            """;

        var slugs = WikiPageParser.ParseSlugs(html);

        Assert.Equal(3, slugs.Count);
        Assert.Contains("la-9", slugs);
        Assert.Contains("a6m2_zero", slugs);
        // 片段标识符必须被剥掉，否则拼出来的 URL 404
        Assert.Contains("b-17e", slugs);
        Assert.DoesNotContain("b-17e#specification", slugs);
    }

    // ------------------------------------------------------ 辅助

    /// <summary>
    /// 拼一个最小可解析页面。
    /// <para>
    /// 结构严格模仿 wiki 的真实标记，包括 header 外套一层 tooltip span 的形态 ——
    /// 这正是 G limit 那行的写法，也是最初解析失败的原因。
    /// </para>
    /// </summary>
    private static string BuildMinimalPage(
        string gameId,
        IEnumerable<(string Header, string Value)>? chars = null)
    {
        var lines = string.Concat(
            (chars ?? []).Select(static c => $"""
                <div class="game-unit_chars-line">
                    <span class="game-unit_chars-header"><span class="underline-dotted" data-bs-toggle="tooltip">{c.Header}</span></span>
                    <span class="game-unit_chars-value"> {c.Value} </span>
                </div>
                """));

        var initialJson = $$"""
            {"id":1,"gameId":"{{gameId}}"}
            """;

        return $$"""
            <!DOCTYPE html>
            <html><body>
            <div class="game-unit_name">Test Unit</div>
            <div class="game-unit_chars">
                <div class="game-unit_chars-block">{{lines}}</div>
            </div>
            <textarea id="game-unit-initial" style="display:none">{{initialJson}}</textarea>
            </body></html>
            """;
    }
}
