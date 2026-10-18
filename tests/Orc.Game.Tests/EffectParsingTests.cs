using Orc.Cards;
using Orc.Core;
using Orc.Game.EffectParsing.Compilation;
using Orc.Game.EffectParsing.Dsl;
using Orc.Game.EffectParsing.Templates;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 效果解析器·转换段验收（S1–S5）：DSL 读写与校验、模板效果加载与槽位语义、
/// op 语句模板渲染、转换器（DSL → 带 csx 的 EffectSnapshot）与往返一致性。
/// </summary>
public class EffectParsingTests
{
    private static readonly (OpTemplateCatalog Catalog, IReadOnlyList<OpTemplateLoadFailure> Failures) OpSetup = LoadOps();

    private static OpTemplateCatalog Ops => OpSetup.Catalog;

    private static IReadOnlyList<OpTemplateLoadFailure> OpFailures => OpSetup.Failures;

    private static (OpTemplateCatalog, IReadOnlyList<OpTemplateLoadFailure>) LoadOps()
    {
        var catalog = OpTemplateCatalog.LoadDirectory(OpTemplateCatalog.DefaultDirectory, out var failures);
        return (catalog, failures);
    }

    private static EffectCompiler CreateCompiler()
    {
        var result = EffectTemplateLoader.LoadDirectory(EffectTemplateLoader.DefaultDirectory);
        Assert.Empty(result.Failures);
        Assert.Empty(OpFailures);
        return new EffectCompiler(result.Templates, Ops);
    }

    [Fact]
    public void Templates_Are_Shipped_And_Loaded()
    {
        var result = EffectTemplateLoader.LoadDirectory(EffectTemplateLoader.DefaultDirectory);

        Assert.Empty(result.Failures);
        Assert.Equal(31, result.Templates.Count);

        var template = Assert.Single(result.Templates, item => item.Id == "deploy_basic");
        Assert.Equal(TriggerKind.Passive, template.Root.MainTrigger.Kind);
        Assert.Contains("unit.deployed", template.Root.MainTrigger.Hooks);
        Assert.Equal(ActorFrom.EventCard, template.ActorFrom);
        Assert.Single(template.Slots);
        Assert.Equal("on_deploy", template.Slots[0].Name);

        // 监听型模板：施动卡＝效果宿主（actorFrom: effectHost）
        var death = Assert.Single(result.Templates, item => item.Id == "death_basic");
        Assert.Equal(ActorFrom.EffectHost, death.ActorFrom);
        Assert.Contains("card.died", death.Root.MainTrigger.Hooks);
        Assert.Equal("on_event", death.Slots[0].Name);

        // S3（29→31）：老兵/隐蔽模板——hook 与 S1/S2 冻结信号名一致、stableKey 按既有惯例。
        var veteran = Assert.Single(result.Templates, item => item.Id == "veteran_basic");
        Assert.Contains("unit.upgraded", veteran.Root.MainTrigger.Hooks);
        Assert.Equal("tpl.veteran_basic.main", veteran.Root.MainTrigger.StableKey);
        Assert.Equal("on_event", veteran.Slots[0].Name);

        var reveal = Assert.Single(result.Templates, item => item.Id == "reveal_basic");
        Assert.Contains("unit.revealed", reveal.Root.MainTrigger.Hooks);
        Assert.Equal("tpl.reveal_basic.main", reveal.Root.MainTrigger.StableKey);
        Assert.Equal("on_event", reveal.Slots[0].Name);
    }

    [Fact]
    public void Op_Templates_Are_Shipped_And_Loaded()
    {
        Assert.Empty(OpFailures);
        foreach (var op in new[]
                 {
                     "damage", "draw", "buff", "grant", "move", "destroy", "pin", "silence",
                     "addToHand", "shuffleIn", "costMod", "gainSlot", "loseSlot",
                     "gainPoint", "losePoint", "csx", "needsCsx", "nested", "aura",
                     "upgrade", "reveal",
                 })
        {
            Assert.True(Ops.Has(op), $"缺少 op 模板：{op}");
        }
    }

    [Fact]
    public void Dsl_RoundTrips()
    {
        var dsl = new DslEffectInstance(
            "deploy_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_deploy"] = new(new[]
                {
                    new DslOp("damage", new DslSelector("one", side: "enemy"), amount: 2),
                }),
            });

        var json = DslJson.Serialize(dsl, indented: true);
        var back = DslJson.Deserialize(json);

        Assert.Equal("deploy_basic", back.Template);
        var fill = Assert.Single(back.Fills);
        Assert.Equal("on_deploy", fill.Key);
        var op = Assert.Single(fill.Value.Ops);
        Assert.Equal("damage", op.Op);
        Assert.Equal(2, op.Amount);
        Assert.Equal("one", op.Target!.Sel);
        Assert.Equal("enemy", op.Target.Side);
    }

    [Fact]
    public void Dsl_Op_Field_RoundTrips_Faithfully()
    {
        // 缺陷修复（批 0 遗留）：OpDto.Field（目标字段——仅 aura）序列化往返补全——
        // ToDto/FromDto 此前皆未携带 ⇒ 补全并断言往返保真（null 与非 null 两态；不改变既有序列化行为）。
        var withField = new DslEffectInstance(
            "deploy_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_deploy"] = new(new[]
                {
                    new DslOp("aura", new DslSelector("all", side: "friendly"), amount: 2, field: "attack"),
                }),
            });

        var json = DslJson.Serialize(withField);
        Assert.Contains("\"field\":\"attack\"", json, StringComparison.Ordinal);
        var back = DslJson.Deserialize(json);
        var op = Assert.Single(Assert.Single(back.Fills).Value.Ops);
        Assert.Equal("aura", op.Op);
        Assert.Equal("attack", op.Field);
        Assert.Equal(2, op.Amount);

        // null 态：字段省略（WhenWritingNull 既有口径）、往返后 Field 为 null（显式 0/未提供口径不受影响）。
        var withoutField = new DslEffectInstance(
            "deploy_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_deploy"] = new(new[] { new DslOp("draw", count: 1) }),
            });

        var json2 = DslJson.Serialize(withoutField);
        Assert.DoesNotContain("\"field\"", json2, StringComparison.Ordinal);
        var back2 = DslJson.Deserialize(json2);
        Assert.Null(Assert.Single(Assert.Single(back2.Fills).Value.Ops).Field);
    }

    [Fact]
    public void Dsl_Condition_OwnerControlsType_RoundTrips()
    {
        // 缺陷修复（L1）：`owner.controls.type` 的 unitTypes/comparison/value 此前未进
        // ConditionDto，序列化后字段丢失 ⇒ 条件编译成恒假守卫（声明式卡片效果永不触发）。
        var condition = new DslCondition(DslCondition.AllKind, all:
        [
            new DslCondition(DslCondition.PayloadSelf, "Card"),
            new DslCondition(DslCondition.OwnerControlsType,
                unitTypes: ["fighter", "bomber"], comparison: ">=", value: 1),
        ]);
        var dsl = new DslEffectInstance(
            "deploy_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_deploy"] = new(new[] { new DslOp("draw", count: 1, condition: condition) }),
            });

        var json = DslJson.Serialize(dsl);
        // `>` 经 System.Text.Json 默认编码器转义（\u003E），故只对无特殊字符的字段做原文断言。
        Assert.Contains("\"unitTypes\":[\"fighter\",\"bomber\"]", json, StringComparison.Ordinal);

        var op = Assert.Single(Assert.Single(DslJson.Deserialize(json).Fills).Value.Ops);
        var all = Assert.IsType<IReadOnlyList<DslCondition>>(op.Condition!.All, exactMatch: false);
        Assert.Equal(2, all.Count);
        Assert.Equal(DslCondition.PayloadSelf, all[0].Kind);
        Assert.Equal(DslCondition.OwnerControlsType, all[1].Kind);
        Assert.Equal(["fighter", "bomber"], all[1].UnitTypes);
        Assert.Equal(">=", all[1].Comparison);
        Assert.Equal(1, all[1].Value);
    }

    [Fact]
    public void Dsl_Rejects_Missing_Template()
    {
        var ok = DslJson.TryDeserialize("""{ "fills": {} }""", out var instance, out var error);

        Assert.False(ok);
        Assert.Null(instance);
        Assert.Contains("template", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dsl_Rejects_Empty_Op_List()
    {
        var json = """{ "template": "deploy_basic", "fills": { "on_deploy": { "ops": [] } } }""";

        var ok = DslJson.TryDeserialize(json, out var instance, out var error);

        Assert.False(ok);
        Assert.Null(instance);
        Assert.NotNull(error);
    }

    [Fact]
    public void Dsl_Rejects_Unregistered_Op()
    {
        var json = """{ "template": "deploy_basic", "fills": { "on_deploy": { "ops": [ { "op": "teleport" } ] } } }""";

        var ok = DslJson.TryDeserialize(json, out var instance, out var error);

        Assert.False(ok);
        Assert.Null(instance);
        Assert.Contains("未登记", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Dsl_Rejects_Op_Missing_Required_Parameter()
    {
        var json = """{ "template": "deploy_basic", "fills": { "on_deploy": { "ops": [ { "op": "damage" } ] } } }""";

        var ok = DslJson.TryDeserialize(json, out _, out var error);

        Assert.False(ok);
        Assert.Contains("amount", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Fills_Slot_And_Keeps_Template_Main_Trigger()
    {
        var compiler = CreateCompiler();
        var dsl = new DslEffectInstance(
            "deploy_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_deploy"] = new(new[]
                {
                    new DslOp("damage", new DslSelector("one", side: "enemy"), amount: 2),
                }),
            });

        var snapshot = compiler.Compile(dsl, "effect.deploy.demo");

        Assert.Equal(EffectSnapshot.SchemaVersion, snapshot.Version);
        Assert.Equal("effect.deploy.demo", snapshot.Root.Id);
        Assert.Equal(TriggerKind.Passive, snapshot.Root.MainTrigger.Kind);
        Assert.Equal("tpl.deploy_basic.main", snapshot.Root.MainTrigger.StableKey);
        Assert.Contains("unit.deployed", snapshot.Root.MainTrigger.Hooks);

        var ev = Assert.Single(snapshot.Root.MainTrigger.Events);
        Assert.True(ev.IsInline);
        Assert.Equal("HandleAsync", ev.EntryName);
        Assert.Contains("HandleAsync", ev.CsxSource, StringComparison.Ordinal);
        Assert.Contains("amount=2", ev.CsxSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Output_RoundTrips_Through_PrefabJson()
    {
        var compiler = CreateCompiler();
        var dsl = new DslEffectInstance(
            "deploy_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_deploy"] = new(new[]
                {
                    new DslOp("damage", new DslSelector("one", side: "enemy"), amount: 2),
                }),
            });

        var json = compiler.CompileToJson(dsl, "effect.deploy.demo");
        var back = PrefabJson.Deserialize(json);

        Assert.Equal("effect.deploy.demo", back.Root.Id);
        var ev = Assert.Single(back.Root.MainTrigger.Events);
        Assert.True(ev.IsInline);
        Assert.Null(ev.AssemblyKey);
    }

    [Fact]
    public void Compile_Supports_Multiple_Ops_And_Csx_Escape_Hatch()
    {
        var compiler = CreateCompiler();
        var dsl = new DslEffectInstance(
            "deploy_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["on_deploy"] = new(new[]
                {
                    new DslOp("damage", new DslSelector("one", side: "enemy"), amount: 1),
                    new DslOp("csx", script: "await Task.CompletedTask; // 逃生舱"),
                }),
            });

        var snapshot = compiler.Compile(dsl, "effect.deploy.multi");
        var csx = Assert.Single(snapshot.Root.MainTrigger.Events).CsxSource!;

        Assert.Contains("amount=1", csx, StringComparison.Ordinal);
        Assert.Contains("逃生舱", csx, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Rejects_Unknown_Slot()
    {
        var compiler = CreateCompiler();
        var dsl = new DslEffectInstance(
            "deploy_basic",
            new Dictionary<string, DslSlotFill>(StringComparer.Ordinal)
            {
                ["not_a_slot"] = new(new[] { new DslOp("draw", count: 1) }),
            });

        var ex = Assert.Throws<InvalidOperationException>(() => compiler.Compile(dsl, "effect.x"));
        Assert.Contains("not_a_slot", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Rejects_Unfilled_Slot()
    {
        var compiler = CreateCompiler();
        var dsl = new DslEffectInstance("deploy_basic", new Dictionary<string, DslSlotFill>(StringComparer.Ordinal));

        var ex = Assert.Throws<InvalidOperationException>(() => compiler.Compile(dsl, "effect.x"));
        Assert.Contains("on_deploy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Rejects_Missing_Template()
    {
        var compiler = CreateCompiler();
        var dsl = new DslEffectInstance("no_such_template", new Dictionary<string, DslSlotFill>(StringComparer.Ordinal));

        var ex = Assert.Throws<InvalidOperationException>(() => compiler.Compile(dsl, "effect.x"));
        Assert.Contains("no_such_template", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_With_Dangling_Slot_Target_Is_Rejected()
    {
        var json = """
        {
          "schemaVersion": 1,
          "id": "broken",
          "slots": [ { "name": "on_x", "target": "mainTrigger.events[nope]" } ],
          "root": {
            "mainTrigger": {
              "id": "t", "stableKey": "k", "kind": "passive",
              "hooks": ["unit.deployed"],
              "events": [ { "id": "e1", "entry": "HandleAsync" } ]
            }
          }
        }
        """;

        var ok = EffectTemplateJson.TryDeserialize(json, out var template, out var error);

        Assert.False(ok);
        Assert.Null(template);
        Assert.Contains("nope", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_Event_With_Fixed_Csx_Cannot_Be_A_Slot()
    {
        var json = """
        {
          "schemaVersion": 1,
          "id": "conflict",
          "slots": [ { "name": "on_x", "target": "mainTrigger.events[e1]" } ],
          "root": {
            "mainTrigger": {
              "id": "t", "stableKey": "k", "kind": "passive",
              "hooks": ["unit.deployed"],
              "events": [ { "id": "e1", "entry": "HandleAsync", "csx": "await Task.CompletedTask;" } ]
            }
          }
        }
        """;

        var ok = EffectTemplateJson.TryDeserialize(json, out _, out var error);

        Assert.False(ok);
        Assert.Contains("固定 csx", error!, StringComparison.Ordinal);
    }
}
