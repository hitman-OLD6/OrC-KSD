using System.Reflection;
using System.Text.Json;
using Orc.Game;
using Orc.Game.Output;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 02-输入接口 S6（输入入口全集清单与导出）一致性核对：
/// 清单八类齐全、新增入口在册、本批可核条目反射可解析（成员真实存在）、导出 JSON 与清单逐条一致。
/// </summary>
public class GameEntryPointsTests
{
    private static readonly string[] Categories =
    [
        GameEntryPoints.CategoryMatch,
        GameEntryPoints.CategoryPlay,
        GameEntryPoints.CategoryCommand,
        GameEntryPoints.CategoryTargeting,
        GameEntryPoints.CategorySpawn,
        GameEntryPoints.CategoryRegistry,
        GameEntryPoints.CategoryPlayer,
        GameEntryPoints.CategoryGateway,
    ];

    [Fact]
    public void Catalog_Covers_All_Categories()
    {
        Assert.NotEmpty(GameEntryPoints.All);
        foreach (var category in Categories)
        {
            Assert.Contains(GameEntryPoints.All, entry => entry.Category == category);
        }

        // 每条均有门禁与结果形态标注（S6 验收：逐条标注前置门禁与结果形态）
        Assert.All(GameEntryPoints.All, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Signature));
            Assert.False(string.IsNullOrWhiteSpace(entry.Gate));
            Assert.False(string.IsNullOrWhiteSpace(entry.Outcome));
        });
    }

    [Fact]
    public void Catalog_Contains_New_Entries_As_Available()
    {
        foreach (var name in new[] { "BeginOrderPrePlayAsync", "BeginMoveAsync", "BeginAttackAsync" })
        {
            var entry = GameEntryPoints.All.Single(e => e.Name == name);
            Assert.True(entry.IsAvailable, $"{name} 应为本批已具备");
        }
    }

    [Fact]
    public void Catalog_Marks_01_Entries_As_Pending_Delivery()
    {
        var matchEntries = GameEntryPoints.All
            .Where(e => e.Owner == "Orc.Game.Match")
            .ToArray();
        Assert.Contains(matchEntries, e => e.Name == "Concede" && !e.IsAvailable);
        Assert.Contains(matchEntries, e => e.Name == "MulliganReplace" && !e.IsAvailable);
        Assert.Contains(matchEntries, e => e.Name == "MulliganDone" && !e.IsAvailable);
    }

    [Fact]
    public void Available_Entries_Resolve_To_Real_Members()
    {
        var assembly = typeof(GameEntryPoints).Assembly;

        foreach (var entry in GameEntryPoints.All.Where(e => e.IsAvailable))
        {
            var type = assembly.GetType(entry.Owner);
            Assert.True(type is not null, $"类型不存在：{entry.Owner}");

            if (entry.Name == ".ctor")
            {
                Assert.NotEmpty(type!.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
                continue;
            }

            var methods = type!.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == entry.Name)
                .ToArray();
            Assert.True(methods.Length > 0, $"成员不存在：{entry.Owner}.{entry.Name}");
        }
    }

    [Fact]
    public void Json_Export_Matches_Catalog()
    {
        var json = GameEntryPointsJson.Serialize();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var points = root.GetProperty("entryPoints");
        Assert.Equal(GameEntryPoints.All.Count, points.GetArrayLength());

        var counts = root.GetProperty("counts");
        Assert.Equal(GameEntryPoints.All.Count, counts.GetProperty("total").GetInt32());
        Assert.Equal(
            GameEntryPoints.All.Count(e => e.IsAvailable),
            counts.GetProperty("available").GetInt32());
        Assert.Equal(
            GameEntryPoints.All.Count(e => !e.IsAvailable),
            counts.GetProperty("pendingDelivery").GetInt32());

        // 逐条一致（名称/类别/状态），且门禁与结果形态随导出交付
        var index = 0;
        foreach (var entry in GameEntryPoints.All)
        {
            var node = points[index++];
            Assert.Equal(entry.Name, node.GetProperty("name").GetString());
            Assert.Equal(entry.Category, node.GetProperty("category").GetString());
            Assert.Equal(entry.Owner, node.GetProperty("owner").GetString());
            Assert.Equal(entry.Gate, node.GetProperty("gate").GetString());
            Assert.Equal(entry.Outcome, node.GetProperty("outcome").GetString());
            Assert.False(string.IsNullOrWhiteSpace(node.GetProperty("status").GetString()));
        }
    }
}
