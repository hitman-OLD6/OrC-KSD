using System.Text.Json;
using Orc.Cards;
using Orc.Core;
using Orc.Output;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S5 验收点④（快照 JSON 断言）：单实体（对象/引用两形态；entityId/组件）、全量（登记序、销毁移除、多引擎独立、空集合）、
/// 失效降级（不抛、可解析）、复杂值降级（引用不递归——防环）、参数契约（null 照常抛参数类异常）。
/// </summary>
public class SnapshotJsonTests
{
    [Fact]
    public void Single_Entity_From_Object_And_Reference_Both_Export_Id_And_Components()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "法师");
        card.AddData(new HealthData { Hp = 7 });
        card.AddData(new SampleData { Value = 2 });

        // 「引用/卡牌」两者皆支持：对象形态与引用形态（card.Ref）产出同构
        foreach (var json in new[] { SnapshotJson.Serialize(card), SnapshotJson.Serialize(card.Ref) })
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(card.Id.ToString("N"), root.GetProperty("entityId").GetString());
            Assert.Equal("法师", root.GetProperty("name").GetString());
            Assert.True(root.GetProperty("alive").GetBoolean());
            var components = root.GetProperty("components");
            Assert.Equal(7, components.GetProperty("HealthData").GetProperty("Hp").GetInt32());
            Assert.Equal(2, components.GetProperty("SampleData").GetProperty("Value").GetInt32());
        }
    }

    [Fact]
    public void Single_Entity_Without_Components_Legal_Empty_Object()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "裸卡");

        var json = SnapshotJson.Serialize(card);
        using var doc = JsonDocument.Parse(json);
        Assert.Empty(doc.RootElement.GetProperty("components").EnumerateObject());
    }

    [Fact]
    public void Invalid_Target_Degrades_Without_Throwing()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "目标");
        card.AddData(new HealthData { Hp = 5 });
        card.Destroy(); // 只杀（失效引用/已销毁实体）——导出不得抛 StaleReferenceException

        // 引用形态：降级输出（alive=false 标记；标识与组件尽力给出）
        var fromRef = SnapshotJson.Serialize(card.Ref);
        using (var doc = JsonDocument.Parse(fromRef))
        {
            var root = doc.RootElement;
            Assert.Equal(card.Id.ToString("N"), root.GetProperty("entityId").GetString());
            Assert.False(root.GetProperty("alive").GetBoolean());
            Assert.Equal(5, root.GetProperty("components").GetProperty("HealthData").GetProperty("Hp").GetInt32());
        }

        // 对象形态：同样降级（经其引用解析）
        var fromObject = SnapshotJson.Serialize(card);
        using (var doc = JsonDocument.Parse(fromObject))
        {
            Assert.False(doc.RootElement.GetProperty("alive").GetBoolean());
            Assert.Equal(card.Id.ToString("N"), doc.RootElement.GetProperty("entityId").GetString());
        }
    }

    [Fact]
    public async Task Full_Snapshot_Registry_Order_Destroyed_Removed_Multi_Engine_Independence_And_Empty()
    {
        var engine = new LogicEngine();
        var a = new Card(engine, "甲"); // 未放置（含未放置卡—登记范围）
        var b = new Card(engine, "乙");
        b.AddData(new HealthData { Hp = 3 });
        await S4TestHelpers.Place(engine, b);
        var c = new Card(engine, "丙");
        await engine.DestroyCard(c); // 一步式销毁 → 登记移除

        var other = new LogicEngine();
        var d = new Card(other, "丁");

        var json = SnapshotJson.SerializeAll(engine);
        using var doc = JsonDocument.Parse(json);
        var cards = doc.RootElement.GetProperty("cards").EnumerateArray().ToArray();

        Assert.Equal(2, cards.Length); // 丙（销毁）已移除
        Assert.Equal(a.Id.ToString("N"), cards[0].GetProperty("entityId").GetString()); // 登记序＝创建序
        Assert.Equal(b.Id.ToString("N"), cards[1].GetProperty("entityId").GetString());
        Assert.DoesNotContain(cards, x => x.GetProperty("name").GetString() == "丙");
        Assert.Equal(3, cards[1].GetProperty("components").GetProperty("HealthData").GetProperty("Hp").GetInt32());

        // 多引擎实例各自独立（other 的登记不含 engine 的卡；反之亦然）
        using var otherDoc = JsonDocument.Parse(SnapshotJson.SerializeAll(other));
        var otherCards = otherDoc.RootElement.GetProperty("cards").EnumerateArray().ToArray();
        Assert.Single(otherCards);
        Assert.Equal(d.Id.ToString("N"), otherCards[0].GetProperty("entityId").GetString());

        // 空快照：合法空集合（不报错）
        var empty = new LogicEngine();
        using var emptyDoc = JsonDocument.Parse(SnapshotJson.SerializeAll(empty));
        Assert.Empty(emptyDoc.RootElement.GetProperty("cards").EnumerateArray());
    }

    [Fact]
    public void Component_Complex_Values_Degrade_Without_Recursion_Into_Targets()
    {
        var engine = new LogicEngine();
        var other = new Card(engine, "另一张卡");
        var card = new Card(engine, "持链卡");
        card.AddData(new LinkData { Mana = 3, Linked = other });

        var json = SnapshotJson.Serialize(card);
        using var doc = JsonDocument.Parse(json);
        var link = doc.RootElement.GetProperty("components").GetProperty("LinkData");
        Assert.Equal(3, link.GetProperty("Mana").GetInt32());

        // 引用型属性值：降级为 {"$ref": GUID(N), "name", "alive"}（不递归导出目标成员——防环）
        var linked = link.GetProperty("Linked");
        Assert.Equal("另一张卡", linked.GetProperty("name").GetString());
        Assert.True(linked.GetProperty("$alive").GetBoolean());
        Assert.True(Guid.TryParseExact(linked.GetProperty("$ref").GetString(), "N", out _));
        Assert.False(linked.TryGetProperty("components", out _));
        Assert.Equal(3, linked.EnumerateObject().Count());
    }

    [Fact]
    public void Component_Getter_Exception_Degrades_With_Error_Marker()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "问题卡");
        card.AddData(new ExplodingData { Ok = 1 });

        var json = SnapshotJson.Serialize(card); // getter 异常：不抛、降级为可诊断标记
        using var doc = JsonDocument.Parse(json);
        var component = doc.RootElement.GetProperty("components").GetProperty("ExplodingData");
        Assert.Equal(1, component.GetProperty("Ok").GetInt32());
        Assert.Equal("InvalidOperationException", component.GetProperty("Boom").GetProperty("$error").GetString());
    }

    [Fact]
    public void Null_Arguments_Throw_Argument_Null()
    {
        Assert.Throws<ArgumentNullException>(() => SnapshotJson.Serialize((Entity)null!));
        Assert.Throws<ArgumentNullException>(() => SnapshotJson.Serialize((Ref<Entity>)null!));
        Assert.Throws<ArgumentNullException>(() => SnapshotJson.SerializeAll(null!));
    }
}

/// <summary>测试用数据组件（复杂值降级：指向另一卡牌的引用型属性；快照不得递归其成员）。</summary>
public sealed class LinkData
{
    public int Mana { get; set; }

    public Card? Linked { get; set; }
}

/// <summary>测试用数据组件（getter 异常降级：可读属性抛出 → {"$error": 类型名}）。</summary>
public sealed class ExplodingData
{
    public int Ok { get; set; }

    public int Boom => throw new InvalidOperationException("getter 故意爆炸");
}
