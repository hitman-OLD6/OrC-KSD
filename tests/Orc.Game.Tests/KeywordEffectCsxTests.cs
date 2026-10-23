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

    private static Effect? FindEffect(Card card, string name)
        => card.Effects.FirstOrDefault(effect => effect.Name == name);

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
                new KeywordEffectBinding(KeywordEffectAssembly.MobilizeAccrualPrefabId, MobilizeAccrualEffectName),
                new KeywordEffectBinding(KeywordEffectAssembly.MobilizeLossPrefabId, MobilizeLossEffectName),
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

    [Fact]
    public async Task csx闪击_随生产库装载_授予后部署置位()
    {
        var keywordId = UniqueKeyword("测试csx闪击");
        KeywordRegistry.Register(keywordId, (_, _) => new PlainKeywordComponent(keywordId));
        KeywordRegistry.DeclareEffectBindings(keywordId, new[]
        {
            new KeywordEffectBinding(KeywordEffectAssembly.CsxBlitzDeploySetPrefabId, CsxBlitzEffectName),
        });
        try
        {
            var match = CommandTestKit.CreateCommandMatch();
            await match.Initialize();
            var playerA = match.Players[0];
            var line = match.Battlefield.PlayerASupportLine;

            // 库装载：csx 制品随生产词条效果库装载（与 assemblyKey 版并存；无需手工注册或测试专用装配）。
            Assert.True(match.Engine.Prefabs.TryGetPrefab(KeywordEffectAssembly.CsxBlitzDeploySetPrefabId, out var loaded));
            var ev = Assert.Single(loaded.Root.MainTrigger.Events);
            Assert.True(ev.IsInline);

            // 授予：装载期实例化经 csx 求值器（编译 → 绑定 → 数据效果装载；行为引用生产注册不参与 csx 来源）。
            var unit = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
            Assert.True(await unit.Keywords.GrantAsync(keywordId));
            var effect = Assert.IsType<DynamicPassiveEffect>(FindEffect(unit, CsxBlitzEffectName));
            Assert.True(effect.IsMounted);

            // 真实部署（部署链 → unit.deployed）→ 置位（覆盖部署初值 false/false——行为等价基线）。
            var result = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps().At(1));
            Assert.Equal(PlayResultStatus.Success, result.Status);
            Assert.True(unit.GetData<CommandData>().CanMove);
            Assert.True(unit.GetData<CommandData>().CanAttack);
        }
        finally
        {
            KeywordRegistry.Unregister(keywordId);
        }
    }

    // ---------- ③ 动员：迁移形态预演（绑定切 csx）——双效果装载＋累积＋失去 ----------

    [Fact]
    public async Task csx动员_迁移形态预演_双效果累积与受伤失去()
    {
        // 迁移形态预演：把「动员」的生产绑定临时切至 csx 双效果（serial 保护、finally 恢复）——
        // 切换后行为等价即生产迁移建议的验证依据。
        SwitchMobilizeBindings(csx: true);
        try
        {
            var match = CommandTestKit.CreateCommandMatch(extraDefinitions: new[]
            {
                new CardDefinitionEntry(CsxMobilizeUnitId, new CardDefinition(
                    "迁移动员兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
                    keywords: new[] { new KeywordDeclaration(KeywordIds.Mobilize) },
                    faction: Faction.Germany, rarity: Rarity.Standard)),
            });
            await match.Initialize();
            var playerA = match.Players[0];
            var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CsxMobilizeUnitId, 1);

            // 装载：csx 双效果随「动员」词条装载（数据效果在列、均处装载态——绑定切换生效）。
            var accrual = Assert.IsType<DynamicPassiveEffect>(FindEffect(unit, MobilizeAccrualEffectName));
            Assert.True(accrual.IsMounted);
            var loss = Assert.IsType<DynamicPassiveEffect>(FindEffect(unit, MobilizeLossEffectName));
            Assert.True(loss.IsMounted);
            Assert.Equal(2, unit.Effects.Count); // 单源（禁双装）：仅双数据效果、无 C# 效果并存

            // 累积：敌方回合不加、友方回合开始 +1/+1（行为等价基线）。
            Assert.Equal(2, AttackOf(unit));
            await match.EndTurn(); // → T2（B）：敌方回合开始不加
            Assert.Equal(2, AttackOf(unit));
            await match.EndTurn(); // → T3（A）：友方回合开始 → +1/+1
            Assert.Equal(3, AttackOf(unit));
            Assert.Equal(6, DefenseOf(unit));

            // 失去：受到实际伤害 → csx 失去行为撤销「动员」（经 EffectRuntime 受控入口）→ 双效果卸载、既得保留。
            await unit.ApplyDefenseDamageAsync(2);
            Assert.False(unit.Keywords.Has(KeywordIds.Mobilize));
            Assert.Equal(3, AttackOf(unit));  // 既得 +1 保留
            Assert.Equal(4, DefenseOf(unit)); // 6-2
            Assert.Null(FindEffect(unit, MobilizeAccrualEffectName));
            Assert.Null(FindEffect(unit, MobilizeLossEffectName));

            // 失去后不再累积（效果已卸载）。
            await match.EndTurn(); // → T4
            await match.EndTurn(); // → T5（A）
            Assert.Equal(3, AttackOf(unit));
        }
        finally
        {
            SwitchMobilizeBindings(csx: false);
        }
    }

    // ---------- ④ 失败分类：compile / sandbox / entry-missing（授予链 fail-fast ＋ 整体回滚） ----------

    [Fact]
    public async Task csx装载失败分类_编译_沙箱_入口缺失_授予回滚()
    {
        var scenarios = new[]
        {
            (KeywordId: UniqueKeyword("测试csx编译"), PrefabId: "keyword.test.csx.compile." + Guid.NewGuid().ToString("N"),
                EffectName: "测试·csx编译错误",
                Source: "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) => { this is not valid C# ;;; };",
                Expected: "compile"),
            (KeywordId: UniqueKeyword("测试csx沙箱"), PrefabId: "keyword.test.csx.sandbox." + Guid.NewGuid().ToString("N"),
                EffectName: "测试·csx沙箱拒绝",
                Source: "var x = File.ReadAllText(\"a\");",
                Expected: "sandbox"),
            (KeywordId: UniqueKeyword("测试csx入口"), PrefabId: "keyword.test.csx.entry." + Guid.NewGuid().ToString("N"),
                EffectName: "测试·csx入口缺失",
                Source: "var x = 1;",
                Expected: "entry-missing"),
        };
        try
        {
            foreach (var scenario in scenarios)
            {
                var keywordId = scenario.KeywordId;
                KeywordRegistry.Register(keywordId, (_, _) => new PlainKeywordComponent(keywordId));
                KeywordRegistry.DeclareEffectBindings(keywordId, new[]
                {
                    new KeywordEffectBinding(scenario.PrefabId, scenario.EffectName),
                });
            }

            var match = CommandTestKit.CreateCommandMatch();
            foreach (var scenario in scenarios)
            {
                // 预注册「坏源」csx 预制体：装配期核验通过（库键存在）；装载期实例化失败（csx 求值分类）。
                match.Engine.Prefabs.RegisterPrefab(new EffectSnapshot(new EffectPrefab(
                    scenario.PrefabId,
                    new TriggerPrefab(
                        "t.main", scenario.EffectName, TriggerKind.Passive,
                        hooks: new[] { GameUpdates.UnitDeployed },
                        events: new[] { new EventPrefab("e1", csxSource: scenario.Source) }))));
            }

            await match.Initialize();
            var playerA = match.Players[0];
            var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
            var effectsBefore = unit.Effects.Count;

            foreach (var scenario in scenarios)
            {
                // 授予 → 装载期实例化失败 → 异常上抛（授予路径 fail-fast）；消息承接 csx 求值分类。
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => unit.Keywords.GrantAsync(scenario.KeywordId));
                Assert.Contains("实例化失败", ex.Message);
                Assert.Contains(scenario.Expected, ex.Message);

                // 授予整体回滚：存在性回 false、零残留（效果容器不变）。
                Assert.False(unit.Keywords.Has(scenario.KeywordId));
                Assert.Equal(effectsBefore, unit.Effects.Count);
            }
        }
        finally
        {
            foreach (var scenario in scenarios)
            {
                KeywordRegistry.Unregister(scenario.KeywordId);
            }
        }
    }
}
