using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Game.Model;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S8（卡牌实体）的游戏层验收：卡牌成员表由**类型预制体**声明（<c>card.base</c> →
/// <c>card.unit</c>／<c>card.order</c>／<c>card.counter</c>／<c>card.hq</c>），卡数据按 <c>typeCategory</c>
/// 自动绑定；C# 卡牌类退化为**绑定入口**（构造签名不变、源码中不再出现 <c>new Trigger&lt;</c>）。
/// </summary>
public sealed class CardEntityTests
{
    private static readonly string ExistingImage = typeof(CardEntityTests).Assembly.Location;

    // ==================== ① 成员表由类型预制体声明（R3g） ====================

    /// <summary>单位卡有 <c>deploy</c> 成员、指令卡没有，且指令卡装载期不报错。</summary>
    [Fact]
    public void UnitHasDeploy_OrderDoesNot_AndOrderStillLoads()
    {
        var unit = CardTypeBinder.Resolve(HostKind.Unit);
        var order = CardTypeBinder.Resolve(HostKind.Order);

        Assert.True(unit.HasMember(CardProcesses.Deploy));
        Assert.False(order.HasMember(CardProcesses.Deploy));

        // 指令卡不因"没有 deploy"而报错（不引入"移除成员"：各类型各自声明）。
        var card = new OrderCard(new LogicEngine(), Definition("order"));
        Assert.Equal(CardProcesses.OrderPrefabId, card.MemberTable.PrefabId);
        Assert.False(card.MemberTable.Has(CardProcesses.Deploy));
        Assert.True(card.MemberTable.Has(CardProcesses.Play));
    }

    /// <summary>四个 C# 卡牌类绑定到各自的类型预制体（映射表 4 条，唯一改动点 A）。</summary>
    [Fact]
    public void CardClasses_BindTheirOwnTypePrefab()
    {
        var engine = new LogicEngine();

        Assert.Equal(CardProcesses.UnitPrefabId, new UnitCard(engine, Definition("infantry")).MemberTable.PrefabId);
        Assert.Equal(CardProcesses.OrderPrefabId, new OrderCard(engine, Definition("order")).MemberTable.PrefabId);
        Assert.Equal(
            CardProcesses.CounterPrefabId,
            new CounterCard(engine, Definition("countermeasure")).MemberTable.PrefabId);
    }



    // ==================== ② typeCategory 自动绑定（R3g 第 6、8 条） ====================

    /// <summary><c>["infantry","tank"]</c> 是合法的**单位卡**（不按列表长度判"多值不唯一"）。</summary>
    [Fact]
    public void MultiUnitType_IsUnitCard()
    {
        var definition = Definition(units: ["infantry", "tank"]);
        var card = new UnitCard(new LogicEngine(), definition);

        Assert.Equal(CardProcesses.UnitPrefabId, card.MemberTable.PrefabId);
        Assert.Equal(2, definition.UnitTypes.Count);
    }

    /// <summary>类别词映射：<c>order</c>→command、<c>countermeasure</c>→counter（各自绑定类型预制体）。</summary>
    [Fact]
    public void CategoryWords_MapToTheirTypePrefabs()
    {
        Assert.Equal(HostKind.Order, CardTypeBinder.HostOfCategory(CardCategory.Command));
        Assert.Equal(HostKind.Counter, CardTypeBinder.HostOfCategory(CardCategory.Counter));
        Assert.Equal(CardProcesses.HqPrefabId, CardTypeBinder.PrefabIdFor(HostKind.Hq));

        // 未定义类别＝拒绝（不设默认类型预制体兜底）。
        Assert.Throws<PrefabLoadException>(() => CardTypeBinder.HostOfCategory("unknown"));
    }

    /// <summary>
    /// 两条实例化路径（直接构造／数据驱动）产出**同一套成员**：数据路径按 <c>typeCategory</c> 选类，
    /// 构造器再按类绑定同一张类型预制体，两侧不一致＝装载期拒绝。
    /// </summary>
    [Fact]
    public void BothInstantiationPaths_ProduceSameMemberSet()
    {
        var engine = new LogicEngine();
        var library = new CardLibrary(engine);
        var definition = Definition("infantry");
        library.Register("demo.a", definition);

        var fromData = library.Instantiate("demo.a");
        var direct = new UnitCard(engine, definition);

        Assert.Equal(direct.MemberTable.PrefabId, fromData.MemberTable.PrefabId);
        Assert.Equal(direct.MemberTable.MemberNames, fromData.MemberTable.MemberNames);
    }

    /// <summary>两条路径指向不同类型预制体（C# 类与 <c>typeCategory</c> 不一致）＝装载期拒绝（双真源防线）。</summary>
    [Fact]
    public void ConflictingBinding_IsRejected()
    {
        // 定义声明 countermeasure，却被构造成单位卡：两条路径的真源不一致。
        var definition = Definition("countermeasure");

        var error = Assert.Throws<PrefabLoadException>(() => new UnitCard(new LogicEngine(), definition));
        Assert.Contains(CardProcesses.CounterPrefabId, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 卡牌预制体不含"组件挂在哪个触发点"的信息（R3c）：装配信息属于组件自己——
    /// 类型预制体既不列组件、也不声明任何挂载；组件预制体才声明挂载。
    /// </summary>
    [Fact]
    public void CardTypePrefabs_HoldNoComponentMountInformation()
    {
        foreach (var id in new[]
                 {
                     CardProcesses.BasePrefabId, CardProcesses.UnitPrefabId, CardProcesses.OrderPrefabId,
                     CardProcesses.CounterPrefabId, CardProcesses.HqPrefabId,
                 })
        {
            var type = CardTypeBinder.Catalog.Resolve(id);
            Assert.Empty(type.Components); // 卡牌类型不声明组件（组件来自卡数据／装配方）
            Assert.Empty(type.Mounts);     // 更不声明"组件挂在哪个触发点"
            Assert.Empty(type.Wraps);
        }
    }


    // ==================== 场景构件 ====================

    private static string RepoRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string callerFile = "")
        // CallerFilePath＝编译期源文件绝对路径（<repo>/tests/Orc.Game.Tests/CardEntityTests.cs）。
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", ".."));

    /// <summary>用数据体路径生成定义（typeCategory 经判定套件解析 → 与生产同一条装载链）。</summary>
    private static CardDefinition Definition(string? category = null, string[]? units = null)
    {
        // 类别词（数据体口径）：order→command、countermeasure→counter；无类别词＝单位卡。
        var tokens = units ?? (category is null ? ["infantry"] : [category]);
        var parts = new List<string>
        {
            $"{{ \"component\": \"typeCategory\", \"type\": [{string.Join(", ", tokens.Select(static t => $"\"{t}\""))}] }}",
            "{ \"component\": \"factionCost\", \"faction\": \"Germany\", \"kredits\": 1 }",
            "{ \"component\": \"battleStats\", \"operationCost\": 1, \"attack\": 2, \"defense\": 3 }",
            "{ \"component\": \"tagData\", \"rarity\": \"Standard\" }",
        };

        if (category is not null)
        {
            // 指令/反制必须有行为载体（判定套件的非法项）；单位卡不需要。
            parts.Add("{ \"component\": \"effects\", \"prefabs\": [\"demo.effect\"] }");
        }

        var json = "{\n  \"schemaVersion\": 1,\n  \"id\": \"demo.a\",\n  \"name\": \"甲\",\n  \"components\": [\n    "
                   + string.Join(",\n    ", parts)
                   + "\n  ]\n}";

        var result = CardValiditySuite.BuiltIn.Evaluate(new CardValidityInput("pack/demo/demo.a")
        {
            MetadataLocalId = "demo.a",
            RawJson = json,
            CardImagePath = ExistingImage,
            Description = "卡面描述",
        });

        Assert.True(
            result.Definition is not null,
            string.Join(" | ", result.Issues.Select(static i => $"{i.Path}:{i.Reason}:{i.IsInvalid}")));
        return result.Definition!;
    }
}
