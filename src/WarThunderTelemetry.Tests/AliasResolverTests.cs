using WarThunderTelemetry.Core.Parsing;
using Xunit;

namespace WarThunderTelemetry.Tests;

/// <summary>
/// 别名解析器测试。
/// <para>
/// 重点覆盖真实接口字段名含空格逗号这一特性，以及字段缺失时的降级行为。
/// </para>
/// </summary>
public class AliasResolverTests
{
    private static AliasResolver Resolver() => FieldCatalog.CreateResolver();

    private static EndpointFields Fields(params (string Key, string Value)[] pairs)
    {
        var fields = new EndpointFields(pairs.Length) { Endpoint = "state" };
        foreach (var (key, value) in pairs)
        {
            fields.Set(key, value);
        }

        return fields;
    }

    [Theory]
    [InlineData("IAS, km/h")]
    [InlineData("ias,km/h")]
    [InlineData("IAS,km/h")]
    [InlineData("  IAS ,  km/h  ")]
    public void 规范化键_忽略空格与大小写(string rawKey)
    {
        var resolver = Resolver();
        var descriptor = resolver.GetById("ias")!;
        var fields = Fields((rawKey, "480"));

        var value = resolver.Resolve(descriptor, fields);

        Assert.True(value.Found);
        Assert.Equal(480, value.Number);
    }

    [Fact]
    public void 真实字段名_含逗号空格_可正确解析()
    {
        var resolver = Resolver();
        var fields = Fields(
            ("H, m", "2400"),
            ("Vy, m/s", "-8.5"),
            ("Ny", "3.2"),
            ("Mfuel, kg", "750"));

        Assert.Equal(2400, resolver.Resolve(resolver.GetById("altitude")!, fields).Number);
        Assert.Equal(-8.5, resolver.Resolve(resolver.GetById("vertical_speed")!, fields).Number);
        Assert.Equal(3.2, resolver.Resolve(resolver.GetById("g_load")!, fields).Number);
        Assert.Equal(750, resolver.Resolve(resolver.GetById("fuel")!, fields).Number);
    }

    [Fact]
    public void 油门百分比_不做错误乘算()
    {
        // /state 的 "throttle N, %" 本身就是百分比，
        // 若误乘 100 会得到 7500% 这种荒谬值。
        var resolver = Resolver();
        var descriptor = resolver.GetById("throttle")!;
        var fields = Fields(("throttle 1, %", "75"));

        var value = resolver.Resolve(descriptor, fields);

        Assert.True(value.Found);
        Assert.Equal(75, value.Number);
    }

    [Fact]
    public void 航向_真实字段名是compass而非heading()
    {
        var resolver = Resolver();
        var descriptor = resolver.GetById("heading")!;

        var withCompass = resolver.Resolve(descriptor, Fields(("compass", "101.27")));
        Assert.True(withCompass.Found);
        Assert.Equal(101.27, withCompass.Number!.Value, 2);

        // 兼容旧名 heading
        var withHeading = resolver.Resolve(descriptor, Fields(("heading", "200")));
        Assert.True(withHeading.Found);
        Assert.Equal(200, withHeading.Number);
    }

    [Fact]
    public void 过载_优先取g_meter读数()
    {
        var resolver = Resolver();
        var descriptor = resolver.GetById("g_load")!;
        var fields = Fields(("Ny", "5.0"), ("g_meter", "4.8"));

        var value = resolver.Resolve(descriptor, fields);

        // g_meter 排在别名首位，仪表读数优先。
        Assert.Equal(4.8, value.Number);
    }

    [Fact]
    public void 字段缺失_返回未命中_不抛异常()
    {
        var resolver = Resolver();
        var descriptor = resolver.GetById("oil_temp")!;
        var fields = Fields(("H, m", "2400"));

        var value = resolver.Resolve(descriptor, fields);

        // 活塞机没有油温字段是常态，必须优雅降级而不是报错。
        Assert.False(value.Found);
        Assert.Null(value.Number);
    }

    [Fact]
    public void 字符串形态数值_可正常解析()
    {
        // /indicators 的 speed、vario 是字符串形式的数值。
        var resolver = Resolver();
        var descriptor = resolver.GetById("vehicle_type")!;

        var fields = Fields(("type", "la_9"));
        var text = resolver.Resolve(descriptor, fields);

        Assert.True(text.Found);
        Assert.Equal("la_9", text.Text);

        var numberLike = resolver.Resolve(resolver.GetById("ias")!, Fields(("IAS, km/h", "480.5")));
        Assert.Equal(480.5, numberLike.Number);
    }

    [Fact]
    public void 混入单位后缀_仍可提取数值()
    {
        var resolver = Resolver();
        var fields = Fields(("IAS, km/h", "850 km/h"));

        var value = resolver.Resolve(resolver.GetById("ias")!, fields);

        Assert.True(value.Found);
        Assert.Equal(850, value.Number);
    }

    [Fact]
    public void 弹药求和_累加多个匹配字段()
    {
        var resolver = Resolver();
        var descriptor = resolver.GetById("ammo")!;
        var fields = Fields(("ammo1", "150"), ("ammo2", "60"), ("ammo3", "340"));

        var value = resolver.Resolve(descriptor, fields);

        Assert.True(value.Found);
        Assert.Equal(550, value.Number);
    }

    [Theory]
    [InlineData("1", 1.0)]
    [InlineData("0", 0.0)]
    [InlineData("DOWN", 1.0)]
    [InlineData("UP", 0.0)]
    [InlineData("true", 1.0)]
    [InlineData("0.85", 0.85)]
    public void 起落架_兼容数值与字符串两种形态(string raw, double expected)
    {
        var resolver = Resolver();
        var descriptor = resolver.GetById("gear")!;
        var fields = Fields(("gears", raw));

        var value = resolver.Resolve(descriptor, fields);

        Assert.True(value.Found);
        Assert.Equal(expected, value.Number!.Value, 3);
    }

    [Fact]
    public void 解析顺序_优先state再indicators()
    {
        var resolver = Resolver();
        var descriptor = resolver.GetById("vertical_speed")!;

        var state = Fields(("Vy, m/s", "-8.5"));
        var indicators = Fields(("vario", "9.9"));

        // state 有值时应取 state
        var fromState = resolver.Resolve(descriptor, state, indicators);
        Assert.Equal(-8.5, fromState.Number);

        // state 无值时回退到 indicators
        var empty = Fields(("H, m", "2400"));
        var fallback = resolver.Resolve(descriptor, empty, indicators);
        Assert.Equal(9.9, fallback.Number);
    }

    [Fact]
    public void 重复别名_先登记者优先()
    {
        var descriptor = new FieldDescriptor
        {
            Id = "test",
            DisplayName = "测试",
            DefaultSelected = false,
            Aliases = ["first", "second"],
        };

        var resolver = new AliasResolver([descriptor]);

        Assert.Same(descriptor, resolver.ResolveDescriptor("first"));
        Assert.Same(descriptor, resolver.ResolveDescriptor("second"));
    }

    [Fact]
    public void 默认勾选_恰好七项且为常见数据()
    {
        Assert.Equal(7, FieldCatalog.DefaultSelectedIds.Count);
        Assert.Contains("ias", FieldCatalog.DefaultSelectedIds);
        Assert.Contains("altitude", FieldCatalog.DefaultSelectedIds);
        Assert.Contains("vertical_speed", FieldCatalog.DefaultSelectedIds);
        Assert.Contains("throttle", FieldCatalog.DefaultSelectedIds);
        Assert.Contains("g_load", FieldCatalog.DefaultSelectedIds);
        Assert.Contains("fuel", FieldCatalog.DefaultSelectedIds);
        Assert.Contains("heading", FieldCatalog.DefaultSelectedIds);
    }
}
