using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 词条效果·csx 行为引用（补全点 1）验收——csx 版词条效果样本的装载/运行/失败分类：
/// ①制品＝<c>KeywordPrefabs/*.csx.prefab.json</c>（csx 来源＋hooks 结构面；与 assemblyKey 版并存）；
/// ②装载＝随生产词条效果库装载（Initialize 装配段——无需注册）；运行＝授予链实例化（经
/// <c>LogicEngine.ScriptEvaluator</c> 编译绑定）后对局行为等价生效（闪击置位／动员双效果）；
/// ③失败分类＝csx 求值器分类（compile/sandbox/entry-missing）经授予链 fail-fast 上抛＋整体回滚。
/// 并行隔离：本套与 <see cref="KeywordEffectBatch4Tests"/> 同 collection（进程级注册面〔词条/绑定〕
/// 在测试窗口内被临时改变——含「动员」绑定切 csx 的迁移形态预演、finally 恢复——沿批 4 隔离带先例）。
/// </summary>
[Collection("KeywordEffectBatch4Serial")]
public class KeywordEffectCsxTests
{
    // ---------- 常量与工具 ----------

    private const string CsxBlitzEffectName = "闪击·部署置位（csx）";
    private const string MobilizeAccrualEffectName = "动员·回合累积";
    private const string MobilizeLossEffectName = "动员·受伤失去";
    private const string CsxMobilizeUnitId = "u_fe_csx_mobilize";

    private static readonly string[] CsxPrefabFiles =
    {
        "keyword.blitz.deploy-set.csx.prefab.json",
        "keyword.mobilize.accrual.csx.prefab.json",
        "keyword.mobilize.loss.csx.prefab.json",
    };

    private static string UniqueKeyword(string prefix) => prefix + Guid.NewGuid().ToString("N");

    private static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    private static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    /// <summary>
    /// 切换「动员」的词条效果绑定（csx=true：csx 双效果〔迁移形态预演〕；csx=false：生产 assemblyKey 双效果〔恢复〕）。
    /// 绑定面＝进程级注册面——Unregister（清注册＋绑定）→ Register（同形工厂）→ DeclareEffectBindings（目标双绑定）；
    /// 隔离带＝本类所属 serial collection（不与任何其它 collection 并行——窗口内无他方读/写该注册面）。
    /// </summary>
    private static void SwitchMobilizeBindings(bool csx)
    {
        KeywordRegistry.Unregister(KeywordIds.Mobilize);
        KeywordRegistry.Register(KeywordIds.Mobilize, (_, _) => new MobilizeKeywordComponent());
        KeywordRegistry.DeclareEffectBindings(KeywordIds.Mobilize, csx
            ? new[]
            {
                new KeywordEffectBinding(KeywordEffectAssembly.CsxMobilizeAccrualPrefabId, MobilizeAccrualEffectName),
                new KeywordEffectBinding(KeywordEffectAssembly.CsxMobilizeLossPrefabId, MobilizeLossEffectName),
            }
            : new[]
            {
                // A2：生产绑定已切 v4 组件（本类的恢复目标必须与生产一致，否则会把进程级注册面留在旧形态，
                // 影响后续套件——Batch1 的动员断言读的就是这条绑定）。
                new KeywordEffectBinding(KeywordEffectAssembly.MobilizeAccrualComponentId, MobilizeAccrualEffectName),
                new KeywordEffectBinding(KeywordEffectAssembly.MobilizeLossComponentId, MobilizeLossEffectName),
            });
    }

    // ---------- ① 制品层：三样本源可编译、可按视图绑定 ----------

    [Fact]
    public void csx制品_三样本源可编译且按视图绑定()
    {
        var evaluator = new CSharpScriptEvaluator();
        foreach (var file in CsxPrefabFiles)
        {
            var json = File.ReadAllText(Path.Combine(KeywordPrefabLibrary.DefaultDirectory, file));
            var snapshot = PrefabJson.Deserialize(json);
            var ev = Assert.Single(snapshot.Root.MainTrigger.Events);
            Assert.True(ev.IsInline); // csx 来源（非程序集键）
            Assert.Null(ev.AssemblyKey);

            var result = evaluator.Evaluate(new ScriptRequest(ev.CsxSource!, ev.EntryName, typeof(CardEventView)));

            Assert.True(result.Success, $"{file}：{result.ErrorCategory}：{result.Error}");
            Assert.IsType<Func<CardEventView, Context, CancellationToken, Task>>(result.Handler);
        }
    }

    // ---------- ② 闪击：随生产库装载 ＋ 授予装载 ＋ 真实部署置位（行为等价） ----------


    // ---------- ③ 动员：迁移形态预演（绑定切 csx）——双效果装载＋累积＋失去 ----------


    // ---------- ④ 失败分类：compile / sandbox / entry-missing（授予链 fail-fast ＋ 整体回滚） ----------

}
