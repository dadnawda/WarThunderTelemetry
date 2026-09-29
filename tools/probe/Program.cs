using System.Net.Http;
using WarThunderTelemetry.Core.Vehicles;

var handler = new HttpClientHandler
{
    AutomaticDecompression = System.Net.DecompressionMethods.All,
};

using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "WarThunderTelemetry/1.0 (+local telemetry dashboard)");

Console.WriteLine("1) 直接取 /aviation …");
try
{
    var html = await http.GetStringAsync(WikiPageParser.AviationUrl);
    Console.WriteLine($"   长度 {html.Length}");
    var slugs = WikiPageParser.ParseSlugs(html);
    Console.WriteLine($"   解析出 {slugs.Count} 个 slug");
    Console.WriteLine($"   前几个: {string.Join(", ", slugs.Take(5))}");
}
catch (Exception ex)
{
    Console.WriteLine($"   失败: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("2) 走解析器抓 la-9 的页面 …");
try
{
    var html = await http.GetStringAsync(WikiPageParser.BaseUrl + "/unit/la-9");
    Console.WriteLine($"   长度 {html.Length}");
    Console.WriteLine($"   gameId = {WikiPageParser.ParseGameId(html)}");
    var profile = WikiPageParser.ParseProfile(html, "la-9");
    Console.WriteLine($"   档案 = {profile?.DisplayName} / IAS={profile?.MaxSpeedIas}");
}
catch (Exception ex)
{
    Console.WriteLine($"   失败: {ex.GetType().Name}: {ex.Message}");
}
