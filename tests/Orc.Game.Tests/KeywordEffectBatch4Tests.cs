using Orc.Cards;
using Orc.Core;
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

    private static Effect? FindEffect(Card card, string name)
        => card.Effects.FirstOrDefault(effect => effect.Name == name);

    private static int AttackOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Attack);

    private static int DefenseOf(UnitCard unit) => unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);

    // ---------- ⑤ 成功路径：生产装配端到端（行为引用/库就绪 → 词条装载 → 数据效果生效） ----------

    [Fact]
    public async Task 生产装配_行为引用与库键就绪_数据效果端到端生效()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 生产装配就绪（Initialize 后即可解析——无需测试手工注册）：库键与行为引用各 3 项。
        Assert.True(match.Engine.Prefabs.TryGetPrefab(KeywordEffectAssembly.BlitzDeploySetPrefabId, out _));
        Assert.True(match.Engine.Prefabs.TryGetPrefab(KeywordEffectAssembly.MobilizeAccrualPrefabId, out _));
        Assert.True(match.Engine.Prefabs.TryGetPrefab(KeywordEffectAssembly.MobilizeLossPrefabId, out _));
        Assert.True(match.Engine.Prefabs.TryGetHandler(KeywordEffectAssembly.BlitzDeploySetPrefabId, out _));
        Assert.True(match.Engine.Prefabs.TryGetHandler(KeywordEffectAssembly.MobilizeAccrualPrefabId, out _));
        Assert.True(match.Engine.Prefabs.TryGetHandler(KeywordEffectAssembly.MobilizeLossPrefabId, out _));

        // 端到端（闪击）：词条装载 → 数据效果在列（新形态类型断言与行为断言并存）→ 真实部署置位。
        var blitz = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.BlitzId, toHand: true);
        var blitzEffect = Assert.IsType<DynamicPassiveEffect>(FindEffect(blitz, "闪击·部署置位"));
        Assert.True(blitzEffect.IsMounted);
        Assert.Single(blitz.Effects); // 单源（禁双装）：壳不构造 C# 效果——仅数据效果一件
        // 追踪连续性（4b②）：审查链中效果节点稳定标识＝效果名（与现形态连续——批 3 形态为同名回退弱身份）。
        Assert.Contains(
            match.Engine.Orchestration.BuildAuditChain().Nodes,
            node => node.StableKey == "闪击·部署置位");
        var deploy = await match.PlayManager.PlayUnitAsync(blitz, match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        Assert.Equal(PlayResultStatus.Success, deploy.Status);
        Assert.True(blitz.GetData<CommandData>().CanMove);  // 部署置位（行为）
        Assert.True(blitz.GetData<CommandData>().CanAttack);

        // 端到端（动员）：双数据效果装载（单源——恰 2 件）→ 回合累积（+1/+1）→ 受伤失去（词条移除链、既得保留、双效果卸载）。
        var mobilize = await CommandTestKit.PrepareOnSupportAsync(match, playerA, MobilizeUnitId, 2);
        Assert.IsType<DynamicPassiveEffect>(FindEffect(mobilize, "动员·回合累积"));
        Assert.IsType<DynamicPassiveEffect>(FindEffect(mobilize, "动员·受伤失去"));
        Assert.Equal(2, mobilize.Effects.Count); // 单源（禁双装）：双数据效果、无 C# 效果并存

        await match.EndTurn(); // → T2（B）
        await match.EndTurn(); // → T3（A）：回合开始 +1/+1
        Assert.Equal(3, AttackOf(mobilize));
        Assert.Equal(6, DefenseOf(mobilize));

        await mobilize.ApplyDefenseDamageAsync(2); // 受到实际伤害（净 2＞0）→ 失去动员
        Assert.False(mobilize.Keywords.Has(KeywordIds.Mobilize));
        Assert.Equal(3, AttackOf(mobilize));  // 既得 +1 保留
        Assert.Equal(4, DefenseOf(mobilize)); // 6-2
        Assert.Null(FindEffect(mobilize, "动员·回合累积")); // 双效果随词条卸载
        Assert.Null(FindEffect(mobilize, "动员·受伤失去"));
    }

    // ---------- ① 库文件层隔离（好文件＋坏文件——隔离记录、不阻断其余） ----------

    [Fact]
    public void 词条效果库_单文件失败隔离记录_不阻断其余()
    {
        var engine = new LogicEngine();
        var tempDir = Path.Combine(Path.GetTempPath(), "orc-kwprefabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // 6 个有效库文件（自默认库目录拷贝——同一制品：3 assemblyKey ＋ 3 csx 样本〔补全点 1〕）＋1 个坏文件（JSON 非法）。
            foreach (var file in Directory.EnumerateFiles(KeywordPrefabLibrary.DefaultDirectory, "*.prefab.json"))
            {
                File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)));
            }

            File.WriteAllText(Path.Combine(tempDir, "broken.prefab.json"), "{ 这不是合法 JSON");

            var result = KeywordEffectAssembly.Assemble(engine, tempDir);

            // 好文件全部注册、坏文件隔离记录（失败明细＋引擎总流留痕）、整体不阻断（核验通过——绑定均可解析）。
            Assert.Equal(3, result.HandlersRegistered);
            Assert.Equal(6, result.PrefabsLoaded); // 库文件集变化的数据适配（3 → 6：新增 3 个 csx 样本制品；隔离语义不变）
            var failure = Assert.Single(result.LibraryFailures);
            Assert.Contains("broken.prefab.json", failure);
            Assert.Contains(
                engine.RootStream.Entries,
                e => e.Message.Contains("broken.prefab.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---------- ② 绑定核验（键缺失＝装配期 fail-fast——对局初始化链） ----------

    [Fact]
    public async Task 绑定核验_库键缺失_对局装配期failfast()
    {
        var keywordId = "测试缺键词条" + Guid.NewGuid().ToString("N");
        KeywordRegistry.Register(keywordId, (_, _) => new PlainKeywordComponent(keywordId));
        KeywordRegistry.DeclareEffectBindings(keywordId, new[]
        {
            new KeywordEffectBinding("keyword.test.missing-entry", "测试·缺键效果"),
        });
        try
        {
            var match = CommandTestKit.CreateCommandMatch();

            // 装配期 fail-fast：初始化抛错（绑定引用的库键缺失——不得静默跳过）；对局保持准备态。
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => match.Initialize());
            Assert.Contains("绑定核验失败", ex.Message);
            Assert.Contains("keyword.test.missing-entry", ex.Message);
        }
        finally
        {
            KeywordRegistry.Unregister(keywordId);
        }
    }

    // ---------- ③ 运行时装载失败（处理器缺失→授予整体回滚、零残留、异常上抛） ----------

    [Fact]
    public async Task 运行时装载失败_授予整体回滚零残留异常上抛()
    {
        var keywordId = "测试坏引用词条" + Guid.NewGuid().ToString("N");
        var prefabId = "keyword.test.missing-handler." + Guid.NewGuid().ToString("N");
        KeywordRegistry.Register(keywordId, (_, _) => new PlainKeywordComponent(keywordId));
        KeywordRegistry.DeclareEffectBindings(keywordId, new[]
        {
            new KeywordEffectBinding(prefabId, "测试·无处理器效果"),
        });
        try
        {
            var match = CommandTestKit.CreateCommandMatch();

            // 预注册「处理器缺失」的测试预制体：装配期核验通过（库键存在）；装载期实例化失败（handler-missing）。
            match.Engine.Prefabs.RegisterPrefab(new EffectSnapshot(new EffectPrefab(
                prefabId,
                new TriggerPrefab(
                    "t.main", "测试·无处理器", TriggerKind.Passive,
                    hooks: new[] { GameUpdates.UnitDeployed },
                    events: new[] { new EventPrefab("e1", assemblyKey: "keyword.test.no-such-handler") }))));
            await match.Initialize();

            var playerA = match.Players[0];
            var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
            var effectsBefore = unit.Effects.Count;

            // 运行时授予 → 装载期实例化失败 → 异常上抛（授予路径 fail-fast）。
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => unit.Keywords.GrantAsync(keywordId));
            Assert.Contains("实例化失败", ex.Message);
            Assert.Contains("handler-missing", ex.Message);

            // 授予整体回滚：存在性回 false、零残留（效果容器/行为态）。
            Assert.False(unit.Keywords.Has(keywordId));
            Assert.Equal(effectsBefore, unit.Effects.Count);
            Assert.DoesNotContain(unit.Keywords.Components, component => component.Keyword == keywordId);
        }
        finally
        {
            KeywordRegistry.Unregister(keywordId);
        }
    }

    // ---------- ④ 独立构造（无上下文＝不实例化/跳过——加载不失败、零残留） ----------

    [Fact]
    public async Task 独立构造_无上下文_数据效果不实例化且加载不失败()
    {
        var engine = new LogicEngine();
        var library = new CardLibrary(engine);
        library.Register("u_fe4_offline_mob", new CardDefinition(
            "离线动员兵", 1, 1, 2, 5, unitTypes: new[] { UnitType.Infantry },
            keywords: new[] { new KeywordDeclaration(KeywordIds.Mobilize) },
            faction: Faction.Germany, rarity: Rarity.Standard));

        var match = CreateMatch();
        await match.Initialize();
        var offline = library.Instantiate("u_fe4_offline_mob");

        await offline.LoadAsync(match.Players[0]); // 独立构造＝无上下文：不抛错（加载不失败）

        Assert.True(offline.Keywords.Has(KeywordIds.Mobilize)); // 词条完整
        Assert.Null(FindEffect(offline, "动员·回合累积"));       // 双效果均不实例化（跳过——功能不可用）
        Assert.Null(FindEffect(offline, "动员·受伤失去"));
        Assert.Empty(offline.Effects); // 零残留（无半态）
    }

    // ---------- ⑥ 坏文件＋有引用者（落入核验 fail-fast） ----------

    [Fact]
    public void 坏文件且被引用_库键缺失落入核验failfast()
    {
        var keywordId = "测试坏文件词条" + Guid.NewGuid().ToString("N");
        var missingPrefabId = "keyword.test.broken-file." + Guid.NewGuid().ToString("N");
        KeywordRegistry.Register(keywordId, (_, _) => new PlainKeywordComponent(keywordId));
        KeywordRegistry.DeclareEffectBindings(keywordId, new[]
        {
            new KeywordEffectBinding(missingPrefabId, "测试·坏文件效果"),
        });

        var tempDir = Path.Combine(Path.GetTempPath(), "orc-kwprefabs-broken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // 坏文件（其"应提供"的键被临时词条引用）——文件层隔离照常（留痕），引用缺失 → 装配期核验 fail-fast。
            File.WriteAllText(Path.Combine(tempDir, "broken.prefab.json"), "{ 坏文件内容");
            var engine = new LogicEngine();

            var ex = Assert.Throws<InvalidOperationException>(
                () => KeywordEffectAssembly.Assemble(engine, tempDir));
            Assert.Contains("绑定核验失败", ex.Message);
            Assert.Contains(missingPrefabId, ex.Message);
            Assert.Contains(
                engine.RootStream.Entries,
                e => e.Message.Contains("broken.prefab.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
            KeywordRegistry.Unregister(keywordId);
        }
    }
}
