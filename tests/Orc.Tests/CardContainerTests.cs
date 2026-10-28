using Orc.Cards;
using Orc.Core;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// 验收 C（容器）：数据组件正常存取（同实例）、多类型共存、缺失抛明确异常、重复 Add 拒绝；null 防御。
/// <para>
/// **逻辑组件段已删除**（§28）：卡不再持有 <c>Effect</c> 列表（<c>Effects</c>／<c>AddEffect</c>／<c>RemoveEffect</c>
/// 随 <c>Effect</c> 家族一并删除），逻辑组件改由编排实体上的组件实例承载；
/// 对应的"卡内容器语义"（添加序／重复拒绝／移除后复装）在组件面上不存在同类概念。
/// </para>
/// </summary>
public class CardContainerTests
{
    // ---------- 数据组件 ----------

    [Fact]
    public void AddData_GetData_Returns_Same_Instance_And_Sharing_Works()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        var health = new HealthData { Hp = 10 };

        card.AddData(health);

        Assert.Same(health, card.GetData<HealthData>()); // 返回同一实例（引用共享）
        health.Hp -= 3;                                   // 外部直接修改字段
        Assert.Equal(7, card.GetData<HealthData>().Hp);   // 修改可见
    }

    [Fact]
    public void AddData_Multiple_Types_Coexist()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        card.AddData(new HealthData { Hp = 10 });
        card.AddData(new SampleData { Value = 3 });

        Assert.Equal(10, card.GetData<HealthData>().Hp);
        Assert.Equal(3, card.GetData<SampleData>().Value);
    }

    [Fact]
    public void GetData_Missing_Throws_Explicit_Exception()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");

        Assert.Throws<KeyNotFoundException>(() => card.GetData<HealthData>()); // 缺失＝明确异常（不返回 null/default）
        Assert.False(card.TryGetData<HealthData>(out var data));               // 试探读面：未添加 → false/null
        Assert.Null(data);
    }

    [Fact]
    public void TryGetData_Returns_Instance_When_Present()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        var health = new HealthData { Hp = 10 };
        card.AddData(health);

        Assert.True(card.TryGetData<HealthData>(out var data));
        Assert.Same(health, data);
    }

    [Fact]
    public void AddData_Duplicate_Type_Rejected_Without_Overwrite()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        var first = new HealthData { Hp = 10 };
        card.AddData(first);

        Assert.Throws<InvalidOperationException>(() => card.AddData(new HealthData { Hp = 99 }));
        Assert.Same(first, card.GetData<HealthData>()); // 不静默覆盖
        Assert.Equal(10, card.GetData<HealthData>().Hp);
    }

    [Fact]
    public void AddData_Null_Rejected()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");
        Assert.Throws<ArgumentNullException>(() => card.AddData(null!));
    }

    [Fact]
    public void Card_Is_Entity_With_Lifecycle_And_Initially_Not_Placed()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "战士");

        Assert.Equal("战士", card.Name);
        Assert.True(card.Life.IsAlive);
        Assert.Same(card, card.Ref.Value);
        Assert.False(card.IsPlaced); // 放置前＝静态组装
    }
}
