using System.Text;
using System.Text.Json;
using Orc.Core;
using Orc.Core.Model;
using Xunit;

namespace Orc.Tests.Model;

/// <summary>
/// S6／S7 验收（恢复面，纯内核路径）：存档 moding → 重建实体。
/// <list type="bullet">
/// <item><b>两阶段</b>（D57）：第一阶段失败 ⇒ <c>Runtime.Entities</c> 数量与恢复前相同（零实体、无痕迹）；
/// 第二阶段失败 ⇒ 逆序销毁 ＋ 异常带残留清单（销毁会发信号＝已知代价）。</item>
/// <item><b>写回时机</b>（D55）：<c>CreateEntity</c> 之后、<c>Assemble()</c> 之前——装配期读到的私有字段
/// 是存档值（<c>Assembled</c> 观察点），不是初值；同名冲突 <c>privateFields</c> 赢过 <c>slots</c>。</item>
/// <item><b>round-trip 两条</b>：未演化 ⇒ <c>M2</c> 与 <c>M1</c> 逐字相同；仅状态演化 ⇒ 只有
/// <c>privateFields</c> 变、<c>slots</c> 逐字不变。</item>
/// <item><b>D54／D58</b>：同 prefab 多份附件按序恢复（不撞 <c>Construct</c> 的查重）、组件经宿主嵌入、
/// 恢复后孤儿数为 0；批量实例化＝同一 moding 多次恢复得到多个互不影响的实体。</item>
/// <item><b>遗留 1</b>：直接 <c>CreateEntity(id, name, host)</c> 造出的组件不在 <c>_componentSets</c> ⇒
/// 存档侧**拒绝**（不静默丢状态）；存档失败时审查导出照常成功（D56 回归）。</item>
/// <item><b>裁决 9 回归</b>：<c>SetPrivateField</c> 公开语义逐字不变（恢复走批内入口）。</item>
/// </list>
/// </summary>
public sealed class SaveRestoreTests
{
    private const string FullCopy = "test.full";

    // ==================== round-trip（D55） ====================

    /// <summary>
    /// round-trip ①（未演化）：存档 → 恢复 → 再存档，<c>M2</c> 与 <c>M1</c> **逐字相同**
    /// （extends／slots／privateFields／attachments 全等——序列化后逐字节比对）。
    /// </summary>
    [Fact]
    public void 恢复_round_trip_未演化_M2与M1逐字相同()
    {
        var scene = new Scene();
        var host = scene.Runtime.CreateEntity("unit.fire", "fire1");
        host.SetPrivateField("hp", 1m);
        scene.Runtime.GrantComponent(host, "fx.echo", "echo1").Component.SetPrivateField("charge", 4m);

        var m1 = Assert.Single(scene.Engine.EvaluateSave());

        // 恢复到一个**新名字**（同名回收会被第一阶段拒绝——裁决 9），再存档读 M2。
        var restored = Assert.Single(scene.Runtime.Restore(
            new[] { new SavedEntityRecord("copy1", m1.Moding) }, scene.Declarations, scene.Judicators));

        var m2 = scene.Engine.EvaluateSave().Single(record => record.Name == "copy1");

        Assert.Equal(Write(m1.Moding), Write(m2.Moding)); // 逐字相同
        Assert.Equal(m1.Moding.Extends, m2.Moding.Extends);
        Assert.Equal("unit.fire", m2.Moding.Extends); // 锚点仍是源预制体（不是内联缓存键）
        Assert.Equal(1m, restored.GetPrivateField("hp")); // 状态也复原了
        Assert.Equal(4m, Assert.Single(scene.Runtime.ComponentsOf(restored)).Component.GetPrivateField("charge"));

        // 注入型字段同名时仍读 _state（不是空位值）：恢复出的实体改写 power ⇒ 存档取 5，slots 仍是配置值 2。
        restored.SetPrivateField("power", 5m);
        var evolved = scene.Engine.EvaluateSave().Single(record => record.Name == "copy1").Moding;
        Assert.Equal(5m, evolved.PrivateFields["power"]);
        Assert.Equal(2m, evolved.Slots["power"]);
    }

    /// <summary>
    /// round-trip ②（仅状态演化）：同一实体改 <c>_state</c> 后重存 ⇒ 只有 <c>privateFields</c> 变，
    /// <c>slots</c> 逐字不变（两张独立的表——D55）。
    /// </summary>
    [Fact]
    public void 恢复_round_trip_仅状态演化_只有privateFields变()
    {
        var scene = new Scene();
        var host = scene.Runtime.CreateEntity("unit.fire", "fire1");
        host.SetPrivateField("hp", 1m);

        var before = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        host.SetPrivateField("hp", 9m);
        var after = Assert.Single(scene.Engine.EvaluateSave()).Moding;

        Assert.Equal(before.Slots.Keys.OrderBy(k => k, StringComparer.Ordinal),
            after.Slots.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(before.Slots["power"], after.Slots["power"]); // slots 逐字不变（配置值）
        Assert.Equal(2m, after.Slots["power"]);

        Assert.Equal(1m, before.PrivateFields["hp"]);
        Assert.Equal(9m, after.PrivateFields["hp"]); // 只有状态变
        Assert.Equal(before.PrivateFields["buddy"], after.PrivateFields["buddy"]); // 其余字段不变
    }

    // ==================== 第一阶段（零实体） ====================

    /// <summary>第一阶段：与场上现存实体重名 ⇒ 失败，且 <c>Entities</c> 数量与恢复前相同（零实体、无痕迹）。</summary>
    [Fact]
    public void 第一阶段_重名_零实体()
    {
        var scene = new Scene();
        var host = scene.Runtime.CreateEntity("unit.fire", "fire1");
        var moding = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        var before = scene.Runtime.Entities.Count;

        var error = Assert.Throws<InvalidOperationException>(
            () => scene.Runtime.Restore(
                new[] { new SavedEntityRecord("fire1", moding) }, scene.Declarations, scene.Judicators));

        Assert.Contains("重名", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, scene.Runtime.Entities.Count);
        Assert.Same(host, scene.Runtime.Entities.Single(entity => entity.Name == "fire1"));
    }

    /// <summary>
    /// 第一阶段：**全部条目校验完才建**——第二条的源不可解析 ⇒ 第一条也不建
    /// （数量与恢复前相同，证明不是"边建边校验"）。
    /// </summary>
    [Fact]
    public void 第一阶段_源不可解析_整批零实体()
    {
        var scene = new Scene();
        var host = scene.Runtime.CreateEntity("unit.fire", "fire1");
        var good = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        var bad = new SaveModing(
            "missing.prefab",
            good.Slots,
            good.PrivateFields,
            Array.Empty<SaveAttachment>());
        var before = scene.Runtime.Entities.Count;

        var error = Assert.Throws<InvalidOperationException>(
            () => scene.Runtime.Restore(
                new[]
                {
                    new SavedEntityRecord("ok1", good),
                    new SavedEntityRecord("bad1", bad),
                },
                scene.Declarations, scene.Judicators));

        Assert.Contains("missing.prefab", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, scene.Runtime.Entities.Count); // 第一条也没建
        Assert.DoesNotContain(scene.Runtime.Entities, entity => entity.Name == "ok1");
    }

    /// <summary>第一阶段：附件未声明 <c>save</c> ⇒ 拒绝（恢复出的实体必须可再存档），零实体。</summary>
    [Fact]
    public void 第一阶段_附件无save_零实体()
    {
        var scene = new Scene();
        scene.Runtime.CreateEntity("unit.fire", "fire1");
        var good = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        var bad = new SaveModing(
            good.Extends,
            good.Slots,
            good.PrivateFields,
            new[] { new SaveAttachment("fx.nosave", Empty(), Empty()) });
        var before = scene.Runtime.Entities.Count;

        var error = Assert.Throws<InvalidOperationException>(
            () => scene.Runtime.Restore(
                new[] { new SavedEntityRecord("trial1", bad) }, scene.Declarations, scene.Judicators));

        Assert.Contains("save", error.Message, StringComparison.Ordinal);
        Assert.Contains("fx.nosave", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, scene.Runtime.Entities.Count);
    }

    /// <summary>第一阶段：<c>Entity</c> 型私有字段引用的名字不可重解析 ⇒ 拒绝，零实体（不静默置空）。</summary>
    [Fact]
    public void 第一阶段_实体引用不可解析_零实体()
    {
        var scene = new Scene();
        scene.Runtime.CreateEntity("unit.fire", "fire1");
        var good = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        var bad = new SaveModing(
            good.Extends,
            good.Slots,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["hp"] = 1m,
                ["buddy"] = "ghost1",
            },
            Array.Empty<SaveAttachment>());
        var before = scene.Runtime.Entities.Count;

        var error = Assert.Throws<InvalidOperationException>(
            () => scene.Runtime.Restore(
                new[] { new SavedEntityRecord("trial1", bad) }, scene.Declarations, scene.Judicators));

        Assert.Contains("ghost1", error.Message, StringComparison.Ordinal);
        Assert.Contains("buddy", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, scene.Runtime.Entities.Count);
    }

    // ==================== 第二阶段（逆序销毁＋残留清单） ====================

    /// <summary>
    /// 第二阶段：附件私有字段名漂移（第一阶段查不出、逐条写回时才发现）⇒
    /// **逆序销毁已建实体 ＋ 异常带残留清单**；销毁后实体与组件两侧的数量都回到恢复前，
    /// 且名字可被下一次恢复复用（回滚确实清干净）。
    /// </summary>
    [Fact]
    public void 第二阶段_写回漂移_逆序销毁并带残留清单()
    {
        var scene = new Scene();
        var host = scene.Runtime.CreateEntity("unit.fire", "fire1");
        host.SetPrivateField("hp", 1m);
        scene.Runtime.GrantComponent(host, "fx.echo", "echo1").Component.SetPrivateField("charge", 4m);
        var m1 = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        host.Destroy();

        var drifted = new SaveModing(
            m1.Extends,
            m1.Slots,
            m1.PrivateFields,
            new[]
            {
                new SaveAttachment(
                    "fx.echo",
                    Empty(),
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["nope"] = 1m }),
            });

        var entitiesBefore = scene.Runtime.Entities.Count;
        var componentsBefore = scene.Runtime.Components.Count;

        var error = Assert.Throws<InvalidOperationException>(
            () => scene.Runtime.Restore(
                new[] { new SavedEntityRecord("fire1", drifted) }, scene.Declarations, scene.Judicators));

        Assert.Contains("第二阶段", error.Message, StringComparison.Ordinal);
        Assert.Contains("残留清单", error.Message, StringComparison.Ordinal);
        Assert.Contains("fire1", error.Message, StringComparison.Ordinal);

        Assert.Equal(entitiesBefore, scene.Runtime.Entities.Count); // 残留被清
        Assert.Equal(componentsBefore, scene.Runtime.Components.Count);
        Assert.DoesNotContain(scene.Runtime.Entities, entity => entity.Name == "fire1");

        // 名字可复用：回滚清得干净（不是"名字被占用但实体已消失"的半态）。
        var restored = Assert.Single(
            scene.Runtime.Restore(
                new[] { new SavedEntityRecord("fire1", m1) }, scene.Declarations, scene.Judicators));
        Assert.Equal("fire1", restored.Name);
    }

    // ==================== 写回时机与同名冲突（D55） ====================

    /// <summary>
    /// 写回在 <c>Assemble()</c> **之前**：组件装配完成通知（<c>Assembled</c>）里读到的私有字段
    /// 已是存档值，不是声明初值——否则实体在装配那一刻用初值做了判断，行为已经偏了。
    /// </summary>
    [Fact]
    public void 恢复_写回早于装配_装配期读到存档值()
    {
        var scene = new Scene();
        var host = scene.Runtime.CreateEntity("unit.fire", "fire1");
        scene.Runtime.GrantComponent(host, "fx.echo", "echo1").Component.SetPrivateField("charge", 7m);
        var m1 = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        host.Destroy();

        var observed = new List<object?>();
        scene.Runtime.Assembled += attachment => observed.Add(attachment.Component.GetPrivateField("charge"));

        var restored = Assert.Single(
            scene.Runtime.Restore(
                new[] { new SavedEntityRecord("fire1", m1) }, scene.Declarations, scene.Judicators));

        Assert.Equal(new object?[] { 7m }, observed); // 装配那一刻已是存档值
        Assert.Equal(7m, Assert.Single(scene.Runtime.ComponentsOf(restored)).Component.GetPrivateField("charge"));
    }

    /// <summary>
    /// 同名冲突 **<c>privateFields</c> 赢**：先按 <c>slots</c> 注入初值（3），再被
    /// <c>privateFields</c>（1）覆盖——配置值与运行时状态是两张独立的表。
    /// </summary>
    [Fact]
    public void 恢复_同名冲突_privateFields赢过slots()
    {
        var scene = new Scene();
        var moding = new SaveModing(
            "unit.same",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["hp"] = 3m },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["hp"] = 1m },
            Array.Empty<SaveAttachment>());

        var restored = Assert.Single(
            scene.Runtime.Restore(
                new[] { new SavedEntityRecord("same1", moding) }, scene.Declarations, scene.Judicators));

        Assert.Equal(1m, restored.GetPrivateField("hp")); // privateFields 赢
        Assert.Equal(3m, restored.Type.Slots.Single(slot => slot.Name == "hp").Value); // slots 仍是配置值
    }

    // ==================== D54／D58：附件与批量实例化 ====================

    /// <summary>
    /// 同 prefab 多份附件（D58：<c>Construct</c> 的查重不能挡路）＋ 数组顺序即身份与份数（D54）：
    /// 恢复后段序 1／2、状态各自正确、全部已装配、组件都有宿主（孤儿数 0）。
    /// </summary>
    [Fact]
    public void 恢复_同prefab多份附件_顺序与状态复原_孤儿数为零()
    {
        var scene = new Scene();
        var host = scene.Runtime.CreateEntity("unit.fire", "fire1");
        scene.Runtime.GrantComponent(host, "fx.echo", "e1").Component.SetPrivateField("charge", 1m);
        scene.Runtime.GrantComponent(host, "fx.echo", "e2").Component.SetPrivateField("charge", 2m);
        var m1 = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        Assert.Equal(2, m1.Attachments.Count);
        host.Destroy();

        var restored = Assert.Single(
            scene.Runtime.Restore(
                new[] { new SavedEntityRecord("fire1", m1) }, scene.Declarations, scene.Judicators));

        var components = scene.Runtime.ComponentsOf(restored);
        Assert.Equal(2, components.Count);
        Assert.Equal(new[] { 1, 2 }, components.Select(c => c.Segment));
        Assert.Equal(new object?[] { 1m, 2m }, components.Select(c => c.Component.GetPrivateField("charge")));
        Assert.All(components, c => Assert.True(c.IsAssembled));

        // 孤儿数 0：每个组件都登记在某宿主的附件集里，且宿主就是恢复出的根。
        Assert.All(components, c => Assert.Same(restored, c.Component.Host));
        Assert.All(components, c => Assert.Contains(c, scene.Runtime.Components));
        Assert.Equal(
            0,
            scene.Runtime.Entities.Count(entity =>
                entity.Host is not null && !scene.Runtime.Components.Any(c => ReferenceEquals(c.Component, entity))));
    }

    /// <summary>
    /// 批量实例化（裁决 3／Q4）：同一 moding 多次恢复 ⇒ 多个独立实体，初态相同、后续互不影响；
    /// 附件实例名按宿主名派生 ⇒ 两次恢复不撞名。
    /// </summary>
    [Fact]
    public void 批量实例化_同一moding多次_独立实体互不影响()
    {
        var scene = new Scene();
        var host = scene.Runtime.CreateEntity("unit.fire", "fire1");
        host.SetPrivateField("hp", 1m);
        scene.Runtime.GrantComponent(host, "fx.echo", "echo1").Component.SetPrivateField("charge", 3m);
        var m1 = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        host.Destroy();

        var first = Assert.Single(
            scene.Runtime.Restore(
                new[] { new SavedEntityRecord("batch1", m1) }, scene.Declarations, scene.Judicators));
        var second = Assert.Single(
            scene.Runtime.Restore(
                new[] { new SavedEntityRecord("batch2", m1) }, scene.Declarations, scene.Judicators));

        Assert.NotSame(first, second);
        Assert.Equal(1m, first.GetPrivateField("hp"));
        Assert.Equal(1m, second.GetPrivateField("hp"));
        Assert.Equal(3m, Assert.Single(scene.Runtime.ComponentsOf(first)).Component.GetPrivateField("charge"));
        Assert.Equal(3m, Assert.Single(scene.Runtime.ComponentsOf(second)).Component.GetPrivateField("charge"));

        first.SetPrivateField("hp", 9m);
        Assert.Single(scene.Runtime.ComponentsOf(first)).Component.SetPrivateField("charge", 8m);

        Assert.Equal(9m, first.GetPrivateField("hp"));
        Assert.Equal(1m, second.GetPrivateField("hp")); // 互不影响
        Assert.Equal(8m, Assert.Single(scene.Runtime.ComponentsOf(first)).Component.GetPrivateField("charge"));
        Assert.Equal(3m, Assert.Single(scene.Runtime.ComponentsOf(second)).Component.GetPrivateField("charge"));
    }

    /// <summary>
    /// <c>Entity</c> 型私有字段按名字重解析（S2／裁决 9）：跨条目引用在恢复后指向**恢复出的**那个实体
    /// （不是原名对应的旧对象——原实体已销毁）。
    /// </summary>
    [Fact]
    public void 恢复_实体引用按名重解析_指向恢复出的实体()
    {
        var scene = new Scene();
        var fire = scene.Runtime.CreateEntity("unit.fire", "fire1");
        var buddy = scene.Runtime.CreateEntity("unit.fire", "buddy1");
        fire.SetPrivateField("buddy", buddy);

        var records = scene.Engine.EvaluateSave()
            .OrderBy(record => record.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, records.Length);

        fire.Destroy();
        buddy.Destroy();
        Assert.Single(scene.Runtime.Entities); // 只剩引擎实体

        var restored = scene.Runtime.Restore(records, scene.Declarations, scene.Judicators);
        var restoredFire = restored.Single(entity => entity.Name == "fire1");
        var restoredBuddy = restored.Single(entity => entity.Name == "buddy1");

        Assert.Same(restoredBuddy, restoredFire.GetPrivateField("buddy"));
    }

    // ==================== 裁决 12：只有目录类型（Card 等代码定义预制体） ====================

    /// <summary>
    /// 裁决 12（Card round-trip）：场上真正的实体（卡牌／单位）由 <c>CardProcesses</c> 这类**代码定义**，
    /// 没有资产形态。`extends` 第一级查资产表落空 ⇒ 第二级查目录类型、**直接用它建实体**（不展开），
    /// 随后照常写回私有字段。断言：实体数量与名一致、`Kind` 保持、`M2` 与 `M1` **逐字相同**。
    /// </summary>
    [Fact]
    public void Card_round_trip_只有目录类型_逐字相同()
    {
        var scene = new Scene();
        var card = scene.Runtime.CreateEntity("unit.card", "card1");
        card.SetPrivateField("hp", 4m);
        var m1 = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        Assert.Empty(m1.Slots); // 代码定义预制体没有空位（裁决 12 的前提）

        card.Destroy();
        Assert.Single(scene.Runtime.Entities);

        // 关键：资产声明表**空表**——Card 没有资产形态，恢复只靠目录。
        var restored = Assert.Single(scene.Runtime.Restore(
            new[] { new SavedEntityRecord("card1", m1) },
            new Dictionary<string, ComponentAsset>(StringComparer.Ordinal),
            scene.Judicators));

        Assert.Equal("card1", restored.Name);
        Assert.Equal(EntityKind.Card, restored.Type.Kind); // 走目录类型路径 ⇒ Kind 不被展开改写
        Assert.Equal(4m, restored.GetPrivateField("hp"));

        var m2 = scene.Engine.EvaluateSave().Single(record => record.Name == "card1");
        Assert.Equal(Write(m1), Write(m2.Moding)); // M2 与 M1 逐字相同
        Assert.Equal("unit.card", m2.Moding.Extends); // 锚点仍是源预制体 id
    }

    /// <summary>
    /// 裁决 12 ＋ D58：Card 的附件仍走**资产形态**（组件是资产）——根走目录类型、附件走展开，
    /// 两条路径混用后 round-trip 依然逐字相同，附件状态与装配序复原。
    /// </summary>
    [Fact]
    public void Card_带附件_混用两条路径_逐字相同()
    {
        var scene = new Scene();
        var card = scene.Runtime.CreateEntity("unit.card", "card1");
        scene.Runtime.GrantComponent(card, "fx.echo", "echo1").Component.SetPrivateField("charge", 6m);
        var m1 = Assert.Single(scene.Engine.EvaluateSave()).Moding;
        Assert.Single(m1.Attachments);
        card.Destroy();

        var restored = Assert.Single(scene.Runtime.Restore(
            new[] { new SavedEntityRecord("card1", m1) }, scene.Declarations, scene.Judicators));

        Assert.Equal(EntityKind.Card, restored.Type.Kind);
        var components = scene.Runtime.ComponentsOf(restored);
        Assert.Single(components);
        Assert.Equal(6m, components[0].Component.GetPrivateField("charge"));
        Assert.True(components[0].IsAssembled);

        var m2 = scene.Engine.EvaluateSave().Single(record => record.Name == "card1");
        Assert.Equal(Write(m1), Write(m2.Moding));
    }

    /// <summary>
    /// 裁决 12：只有目录类型却带着 <c>slots</c> ⇒ **拒绝**（参数无处可注，不静默丢弃），
    /// 且挡在第一阶段（零实体）。
    /// </summary>
    [Fact]
    public void 只有目录类型但带slots_拒绝且零实体()
    {
        var scene = new Scene();
        var bad = new SaveModing(
            "unit.card",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["power"] = 2m },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["hp"] = 1m },
            Array.Empty<SaveAttachment>());
        var before = scene.Runtime.Entities.Count;

        var error = Assert.Throws<InvalidOperationException>(
            () => scene.Runtime.Restore(
                new[] { new SavedEntityRecord("card1", bad) },
                new Dictionary<string, ComponentAsset>(StringComparer.Ordinal),
                scene.Judicators));

        Assert.Contains("无空位", error.Message, StringComparison.Ordinal);
        Assert.Contains("slots", error.Message, StringComparison.Ordinal);
        Assert.Contains("power", error.Message, StringComparison.Ordinal);
        Assert.Contains("拒绝静默丢弃", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, scene.Runtime.Entities.Count);
    }

    /// <summary>裁决 12：两处都没有 ⇒ 文案要指出**两个查法都试过**（不是只报资产表）。</summary>
    [Fact]
    public void 源两处都没有_文案区分()
    {
        var scene = new Scene();
        var bad = new SaveModing(
            "missing.prefab",
            Empty(),
            Empty(),
            Array.Empty<SaveAttachment>());

        var error = Assert.Throws<InvalidOperationException>(
            () => scene.Runtime.Restore(
                new[] { new SavedEntityRecord("ghost1", bad) },
                new Dictionary<string, ComponentAsset>(StringComparer.Ordinal),
                scene.Judicators));

        Assert.Contains("既不在资产声明表里、也不在预制体目录里", error.Message, StringComparison.Ordinal);
        Assert.Contains("missing.prefab", error.Message, StringComparison.Ordinal);
    }

    // ==================== 遗留 1：存档侧孤儿守卫 ====================

    /// <summary>
    /// 遗留 1（存档侧）：直接 <c>CreateEntity(id, name, host)</c> 造出的组件不在 <c>_componentSets</c>，
    /// 既不会进 <c>savedEntities</c>（有宿主被排除）、也不在任何宿主的 <c>attachments</c> 里 ⇒
    /// 状态会被**静默丢失**。存档侧必须**拒绝**（与恢复侧孤儿校验构成双向守卫）。
    /// </summary>
    [Fact]
    public void 遗留1_直建宿主实体_存档侧拒绝()
    {
        var scene = new Scene();
        var host = scene.Runtime.CreateEntity("unit.fire", "fire1");
        scene.Runtime.CreateEntity("fx.echo", "direct.echo", host); // 绕过组件装配面

        var error = Assert.Throws<InvalidOperationException>(() => scene.Engine.ExportSave());

        Assert.Contains("direct.echo", error.Message, StringComparison.Ordinal);
        Assert.Contains("直接 CreateEntity", error.Message, StringComparison.Ordinal);
        Assert.Contains("fire1", error.Message, StringComparison.Ordinal);

        // D56 回归：存档失败不传染诊断面——既有导出照常成功（不抛、可反复调用）。
        var audit = scene.Engine.ExportAuditChainJson();
        Assert.False(string.IsNullOrEmpty(audit));
        Assert.Equal(audit, scene.Engine.ExportAuditChainJson());
        Assert.False(string.IsNullOrEmpty(
            scene.Engine.ExportChainJson(TriggerId.FromKey("engine#card.placed"))));
    }

    // ==================== 裁决 9 回归：公开写语义未变 ====================

    /// <summary>
    /// <c>SetPrivateField</c> 公开语义逐字不变（恢复走 internal 批内入口，不动公开面）：
    /// 未声明字段＝<see cref="PermissionDeniedException"/>、类型不符＝<see cref="InvalidCastException"/>、
    /// null 回填默认值、合法值原样写入。
    /// </summary>
    [Fact]
    public void SetPrivateField_公开语义未变()
    {
        var scene = new Scene();
        var entity = scene.Runtime.CreateEntity("unit.fire", "fire1");

        entity.SetPrivateField("hp", 5m);
        Assert.Equal(5m, entity.GetPrivateField("hp"));

        Assert.Throws<PermissionDeniedException>(() => entity.SetPrivateField("nope", 1m));
        Assert.Throws<InvalidCastException>(() => entity.SetPrivateField("hp", "不是数字"));

        entity.SetPrivateField("hp", null); // null ⇒ 默认值（Number 的默认是 ViewFieldTypes.DefaultValue＝int 0）
        Assert.Equal(0, entity.GetPrivateField("hp"));

        entity.SetPrivateField("buddy", entity); // Entity 型接受实体
        Assert.Same(entity, entity.GetPrivateField("buddy"));
    }

    // ==================== 场景 ====================

    /// <summary>
    /// 恢复测试场景：内核目录（引擎信号）＋四份资产（单元／同名空位／可存档组件／不可存档组件）
    /// ＋一枚**代码定义的 Card**（无资产形态，裁决 12 专用）。
    /// </summary>
    private sealed class Scene
    {
        public Scene()
        {
            var unit = UnitTemplate();
            var same = SameNameTemplate();
            var echo = EchoTemplate();
            var nosave = NoSaveTemplate();

            Declarations = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal)
            {
                [unit.Id] = unit,
                [same.Id] = same,
                [echo.Id] = echo,
                [nosave.Id] = nosave,
            };

            var catalog = KernelSignals.CreateCatalog()
                .Add(unit.ToStructuralPrefabDefinition())
                .Add(same.ToStructuralPrefabDefinition())
                .Add(echo.ToStructuralPrefabDefinition())
                .Add(nosave.ToStructuralPrefabDefinition())
                // 裁决 12：代码定义的 Card（没有资产形态、没有空位）——恢复只能按目录类型建实体。
                .Add(new PrefabDefinition(
                    "unit.card",
                    EntityKind.Card,
                    hosts: HostKind.Unit,
                    privateFields: new[]
                    {
                        new PrivateFieldDefinition("hp", ViewFieldType.Number, 0m),
                        new PrivateFieldDefinition("buddy", ViewFieldType.Entity),
                    },
                    save: FullCopy));

            Runtime = new OrchestrationRuntime(catalog, new HandlerRegistry());
            Judicators = new JudicatorRegistry();
            Judicators.Register(FullCopy, new FullCopyExporter());
            Engine = new LogicEngine { Model = Runtime, Judicators = Judicators };
        }

        public OrchestrationRuntime Runtime { get; }

        public JudicatorRegistry Judicators { get; }

        public LogicEngine Engine { get; }

        public IReadOnlyDictionary<string, ComponentAsset> Declarations { get; }
    }

    private static ComponentAsset UnitTemplate()
        => new(
            "unit.fire",
            HostKind.Unit,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: new[]
            {
                new PrivateFieldDefinition("hp", ViewFieldType.Number, 0m),
                new PrivateFieldDefinition("buddy", ViewFieldType.Entity),
            },
            members: Array.Empty<ComponentMemberAsset>(),
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[] { new AssetSlotDeclaration("power", SlotType.Number, 2m) },
            save: FullCopy);

    private static ComponentAsset SameNameTemplate()
        => new(
            "unit.same",
            HostKind.Unit,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: new[] { new PrivateFieldDefinition("hp", ViewFieldType.Number, 0m) },
            members: Array.Empty<ComponentMemberAsset>(),
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            slots: new[] { new AssetSlotDeclaration("hp", SlotType.Number, 3m) },
            save: FullCopy);

    private static ComponentAsset EchoTemplate()
        => new(
            "fx.echo",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: new[] { new PrivateFieldDefinition("charge", ViewFieldType.Number, 0m) },
            members: Array.Empty<ComponentMemberAsset>(),
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            save: FullCopy);

    private static ComponentAsset NoSaveTemplate()
        => new(
            "fx.nosave",
            HostKind.Any,
            ActiveWhen.Placed,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            members: Array.Empty<ComponentMemberAsset>(),
            mounts: Array.Empty<MountDeclaration>(),
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>());

    private static IReadOnlyDictionary<string, object?> Empty()
        => new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>moding 的确定性序列化（与落盘同一份实现）——逐字比对用。</summary>
    private static string Write(SaveModing moding)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Orc.Output.SaveModingJson.WriteModing(writer, moding);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>测试用完整复制导出器（与 <c>Orc.Game</c> 内置 B 同构；纯内核路径没有内置 B——已知限制 C2）。</summary>
    private sealed class FullCopyExporter : SaveExporter
    {
        protected override SaveModing? ExportLogic(OrchestrationEntity self, SaveContext context)
            => new(
                SaveContext.ExtendsOf(self),
                context.ReadSlots(self.Type),
                context.ReadPrivateFields(self),
                context.Runtime.ComponentsOf(self)
                    .Select(attachment => context.ExportAttachment(attachment.Component))
                    .ToArray());
    }
}
