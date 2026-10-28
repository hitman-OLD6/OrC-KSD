using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 批 4 专项的并行隔离 collection：本套用例在「装配核验」窗口内会注册带缺失/隔离库键的临时绑定
/// （绑定面＝进程级注册面——核验语义所必需：任何引擎装配时对全部绑定核验）；为避免与其它测试类的
/// 对局初始化（装配核验）并行冲突，本 collection 声明为不与其它 collection 并行执行。
/// </summary>
[CollectionDefinition("KeywordEffectBatch4Serial", DisableParallelization = true)]
public sealed class KeywordEffectBatch4SerialCollection
{
}

/// <summary>
/// 词条效果化·批 4（数据化·路线 A）验收——数据效果装载专项：
/// 机制层四要素（词条效果库／绑定面／装载期实例化点／行为引用生产注册）＋失败语义与独立构造语义的证据面。
/// 覆盖（4c 六项清单）：①库文件层隔离（好文件＋坏文件＝隔离记录、不阻断其余）；②绑定核验（键缺失＝装配期 fail-fast）；
/// ③运行时装载失败（处理器缺失→授予整体回滚、零残留、异常上抛——回滚机制本体＝申报引用批 0）；
/// ④独立构造（无上下文＝不实例化/跳过——动员侧）；⑤成功路径经生产装配端到端（Initialize 后行为引用与库键
/// 即可解析、无需测试手工注册）；⑥坏文件＋有引用者＝落入核验 fail-fast。
/// 试点行为等价（闪击置位／动员累积与失去）由 <see cref="KeywordEffectBatch1Tests"/>／
/// <see cref="KeywordEffectBatch3Tests"/>（适配后）与既有测试共同承载；本文件聚焦机制层与失败语义。
/// </summary>
[Collection("KeywordEffectBatch4Serial")]
public class KeywordEffectBatch4Tests
{
    // ---------- 定义与工具 ----------

    private const string MobilizeUnitId = "u_fe4_mobilize";

    private static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() => new[]
    {
        new CardDefinitionEntry(MobilizeUnitId, new CardDefinition(
            "动员兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Mobilize) }, faction: Faction.Germany, rarity: Rarity.Standard)),
    };

    private static Match CreateMatch() => CommandTestKit.CreateCommandMatch(extraDefinitions: CreateDefinitions());

    /// <summary>
    /// 词条行为承载检索（v4 组件形态；A2：闪击/动员生产绑定已切组件资产，行为不再以 <c>Effect</c> 实例承载）。
    /// </summary>
    private static ComponentAttachment? FindCarrier(Card card, string componentId)
        => card is CardBase { ModelEntity: { } entity }
            ? entity.Runtime.ComponentsOf(entity).FirstOrDefault(a => a.Type.Id == componentId)
            : null;

    /// <summary>装配核验用的引擎（A2：内建词条组件库须在目录里——绑定核验跨两种承载）。</summary>
    private static LogicEngine NewAssemblingEngine()
    {
        var engine = new LogicEngine();
        var runtime = Orc.Game.Model.GameEnginePrefabs.CreateRuntime();
        engine.Model = runtime;
        engine.ScriptEvaluator = new Orc.Script.CSharpScriptEvaluator();
        KeywordComponentLibrary.LoadInto(runtime, engine.ScriptEvaluator);
        return engine;
    }

    private static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    private static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    // ---------- ⑤ 成功路径：生产装配端到端（行为引用/库就绪 → 词条装载 → 数据效果生效） ----------

}
