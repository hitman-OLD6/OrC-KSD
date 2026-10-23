using System.Text;
using System.Text.Json;
using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 端到端全链路（批 2）：分发数据（卡目录＋效果库目录）→ 装载 → 注册（RegisterPrefab/DeclarePrefab）→
/// Match 初始化 → 卡 LoadAsync → 兑现行为：
/// a) 卡＋内联效果（经批 1 成环：读→写→再读后仍全链可跑；加入路径驱动）；
/// b) 效果库 <c>*.prefab.json</c> 文件→装载→卡引用→对局行为（assemblyKey 真注册 handler；加入路径驱动）；
/// c) 成环回归（Load→Save（卡＋效果）→再 Load→对局→**打出**（部署链）驱动）。
/// 三链行为证据＝世界状态（优先）；数据全内嵌自含、临时目录、无 outputs 依赖（测后清理）。
/// </summary>
public class CardToolchainEndToEndTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // a) 卡＋内联效果（读出→写出→再读的批 1 成环；加入路径驱动；行为＝敌方防御 -1）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Chain_A_Inline_Effect_Survives_Card_Write_Cycle_And_Runs_In_Match()
    {
        var sourceDirectory = NewTempDir();
        var cycleDirectory = NewTempDir();
        try
        {
            // 分发数据：单卡 JSON（内联 csx 效果——对象态、自包含）。
            WriteUtf8File(
                Path.Combine(sourceDirectory, InlineDamageCardId + ".card.json"), InlineDamageCardJson());

            // ① 读：卡目录装载（内联先注册——以 id 并入声明）。
            var loaded1 = CardDataLoader.LoadDirectory(sourceDirectory);
            Assert.Empty(loaded1.Failures);
            var entry1 = Assert.Single(loaded1.Definitions);
            Assert.Equal(InlineDamageCardId, entry1.Id);
            var prefab1 = Assert.Single(loaded1.Prefabs);
            Assert.Equal(InlineDamageSnapshotId, prefab1.Root.Id);
            var declaration1 = Assert.Single(loaded1.EffectDeclarations);
            Assert.Equal(InlineDamageCardId, declaration1.CardId);
            Assert.Equal(new[] { InlineDamageSnapshotId }, declaration1.PrefabIds);

            // ② 写（批 1 成环——写方向）：卡写出（内联效果按对象态随卡落盘）。
            var save = CardDataWriter.SaveDirectory(cycleDirectory, loaded1.Definitions);
            Assert.Single(save.Succeeded);
            Assert.Empty(save.Failures);
            Assert.Empty(save.Warnings);

            // ③ 再读：成环后数据仍全链可用（内联快照保真）。
            var loaded2 = CardDataLoader.LoadDirectory(cycleDirectory);
            Assert.Empty(loaded2.Failures);
            Assert.Equal(InlineDamageCardId, Assert.Single(loaded2.Definitions).Id);
            var prefab2 = Assert.Single(loaded2.Prefabs);
            Assert.Equal(
                prefab1.Root.MainTrigger.Events[0].CsxSource,
                prefab2.Root.MainTrigger.Events[0].CsxSource);

            // ④ 装配：注册（预制体 ＋ 效果声明）→ Match 初始化 → 卡加载。
            var registry = new CardEffectRegistry();
            foreach (var (cardId, prefabIds) in loaded2.EffectDeclarations)
            {
                registry.DeclarePrefab(cardId, prefabIds);
            }

            var match = CommandTestKit.CreateCommandMatch(
                effectRegistry: registry, extraDefinitions: loaded2.Definitions);
            foreach (var snapshot in loaded2.Prefabs)
            {
                match.Engine.Prefabs.RegisterPrefab(snapshot);
            }

            await match.Initialize();

            // ⑤ 对局活动路径（加入）→ 效果触发（csx 真编译）→ 行为断言（世界状态）。
            var target = await CommandTestKit.PrepareOnFrontAsync(match, match.Players[1], CommandTestKit.BomberId, 0);
            Assert.Equal(2, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));

            await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], InlineDamageCardId, 1);

            Assert.Equal(1, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 2 → 1（效果真的落地）
        }
        finally
        {
            DeleteTempDir(sourceDirectory);
            DeleteTempDir(cycleDirectory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // b) 效果库 *.prefab.json 文件→装载→卡引用→对局行为（assemblyKey 真注册 handler；加入路径驱动）。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Chain_B_Prefab_Library_File_Loaded_Referenced_By_Card_Runs_In_Match()
    {
        var libraryDirectory = NewTempDir();
        var cardDirectory = NewTempDir();
        try
        {
            // 分发数据：效果库文件（经本批落盘能力生成 *.prefab.json）＋卡（引用形态）。
            var save = PrefabWriter.SaveFile(libraryDirectory, LibraryJoinSetSnapshot());
            Assert.Empty(save.Failures);
            Assert.True(File.Exists(Path.Combine(libraryDirectory, LibraryJoinPrefabId + ".prefab.json")));
            WriteUtf8File(
                Path.Combine(cardDirectory, LibraryJoinCardId + ".card.json"), LibraryJoinCardJson());

            // ① 卡装载（纯数据产出——引用清单）。
            var loaded = CardDataLoader.LoadDirectory(cardDirectory);
            Assert.Empty(loaded.Failures);
            Assert.Empty(loaded.Prefabs); // 纯引用形态（无内联）
            var declaration = Assert.Single(loaded.EffectDeclarations);
            Assert.Equal(LibraryJoinCardId, declaration.CardId);
            Assert.Equal(new[] { LibraryJoinPrefabId }, declaration.PrefabIds);

            // ② 装配：声明注册表 ＋ 处理器预注册 ＋ 效果库目录装载（引用可解析面）。
            var registry = new CardEffectRegistry();
            foreach (var (cardId, prefabIds) in loaded.EffectDeclarations)
            {
                registry.DeclarePrefab(cardId, prefabIds);
            }

            var match = CommandTestKit.CreateCommandMatch(
                effectRegistry: registry, extraDefinitions: loaded.Definitions);
            match.Engine.Prefabs.RegisterHandler(LibraryJoinPrefabId, LibraryJoinSetHandler);

            var libraryLoad = match.Engine.Prefabs.LoadDirectory(libraryDirectory);
            Assert.Equal(1, libraryLoad.Loaded);
            Assert.Empty(libraryLoad.Failures);
            Assert.True(match.Engine.Prefabs.TryGetPrefab(LibraryJoinPrefabId, out _));

            await match.Initialize();

            // ③ 对局活动路径（加入）→ 效果触发 → 行为断言（世界状态：指挥组件从部署初值置位）。
            var unit = await CommandTestKit.PrepareOnSupportAsync(match, match.Players[0], LibraryJoinCardId, 1);
            var command = unit.GetData<CommandData>();
            Assert.True(command.CanMove);
            Assert.True(command.CanAttack);

            // 边界（自指过滤）：同方其它单位加入不触发——非宿主不置位。
            var outsider = await CommandTestKit.PrepareOnSupportAsync(
                match, match.Players[0], CommandTestKit.InfantryId, 2);
            var outsiderCommand = outsider.GetData<CommandData>();
            Assert.False(outsiderCommand.CanMove);
            Assert.False(outsiderCommand.CanAttack);
        }
        finally
        {
            DeleteTempDir(libraryDirectory);
            DeleteTempDir(cardDirectory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // c) 成环回归：Load→Save（卡＋效果）→再 Load→对局→打出（部署链驱动）→行为断言。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Chain_C_Full_Cycle_Load_Save_Reload_Play_Drives_Effect_Behavior()
    {
        var sourceCardsDirectory = NewTempDir();
        var sourceLibraryDirectory = NewTempDir();
        var cycleCardsDirectory = NewTempDir();
        var cycleLibraryDirectory = NewTempDir();
        try
        {
            // 分发数据（直接写入）：卡目录 ＋ 效果库目录（csx 形态 *.prefab.json）。
            WriteUtf8File(
                Path.Combine(sourceCardsDirectory, CycleDeployCardId + ".card.json"), CycleDeployCardJson());
            WriteUtf8File(
                Path.Combine(sourceLibraryDirectory, CycleDeployPrefabId + ".prefab.json"),
                CsxSnapshotJson(CycleDeployPrefabId, "端到端·部署置位", "unit.deployed", DeploySetCsx));

            // ① 第一轮装载（读）：卡目录 ＋ 效果库目录。
            var cards1 = CardDataLoader.LoadDirectory(sourceCardsDirectory);
            Assert.Empty(cards1.Failures);
            var readerEngine = new LogicEngine();
            var library1 = readerEngine.Prefabs.LoadDirectory(sourceLibraryDirectory);
            Assert.Equal(1, library1.Loaded);
            Assert.Empty(library1.Failures);
            Assert.True(readerEngine.Prefabs.TryGetPrefab(CycleDeployPrefabId, out var snapshot1));

            // ② 写出（成环）：卡（批 1 写方向）＋ 效果（本批落盘能力）。
            var cardSave = CardDataWriter.SaveDirectory(cycleCardsDirectory, cards1.Definitions);
            Assert.Single(cardSave.Succeeded);
            Assert.Empty(cardSave.Failures);
            var librarySave = PrefabWriter.SaveDirectory(cycleLibraryDirectory, new[] { snapshot1 });
            Assert.Single(librarySave.Succeeded);
            Assert.Empty(librarySave.Failures);

            // ③ 再装载（读回）：轻量一致性（逐字段等值归文件级往返测试——此处只验仍全链可用）。
            var cards2 = CardDataLoader.LoadDirectory(cycleCardsDirectory);
            Assert.Empty(cards2.Failures);
            Assert.Equal(cards1.Definitions.Count, cards2.Definitions.Count);
            Assert.Equal(
                cards1.Definitions.Select(entry => entry.Id).OrderBy(id => id, StringComparer.Ordinal),
                cards2.Definitions.Select(entry => entry.Id).OrderBy(id => id, StringComparer.Ordinal));

            // ④ 装配：注册 → Match 初始化 → 卡加载（效果装载——csx 真编译）。
            var registry = new CardEffectRegistry();
            foreach (var (cardId, prefabIds) in cards2.EffectDeclarations)
            {
                registry.DeclarePrefab(cardId, prefabIds);
            }

            var match = CommandTestKit.CreateCommandMatch(
                effectRegistry: registry, extraDefinitions: cards2.Definitions);
            var library2 = match.Engine.Prefabs.LoadDirectory(cycleLibraryDirectory);
            Assert.Equal(1, library2.Loaded);
            Assert.Empty(library2.Failures);

            await match.Initialize();

            var unit = await CommandTestKit.InstantiateLoadedAsync(
                match, match.Players[0], CycleDeployCardId, toHand: true);

            // ⑤ 打出（真实部署链驱动）→ 行为断言（unit.deployed 时置位——覆盖部署初值 false/false）。
            var result = await match.PlayManager.PlayUnitAsync(unit, match.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
            Assert.Equal(PlayResultStatus.Success, result.Status);

            var command = unit.GetData<CommandData>();
            Assert.True(command.CanMove);
            Assert.True(command.CanAttack);
        }
        finally
        {
            DeleteTempDir(sourceCardsDirectory);
            DeleteTempDir(sourceLibraryDirectory);
            DeleteTempDir(cycleCardsDirectory);
            DeleteTempDir(cycleLibraryDirectory);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助：文件写入 / 数据构造 / 处理器 / 临时目录。
    // ─────────────────────────────────────────────────────────────────────────

    private const string InlineDamageCardId = "u_e2e_inline_damage";
    private const string InlineDamageSnapshotId = "e2e.inline.damage";
    private const string LibraryJoinCardId = "u_e2e_library_join";
    private const string LibraryJoinPrefabId = "e2e.library.join-set";
    private const string CycleDeployCardId = "u_e2e_cycle_deploy";
    private const string CycleDeployPrefabId = "e2e.library.deploy-set";

    private static void WriteUtf8File(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>生成 csx 形态效果快照 JSON（单主触发器 ＋ 单事件；直写分发目录或嵌入卡内联）。</summary>
    private static string CsxSnapshotJson(string id, string stableKey, string hook, string csxSource)
    {
        var csxJson = JsonSerializer.Serialize(csxSource);
        return $$"""
        {
          "schemaVersion": 2,
          "root": {
            "id": "{{id}}",
            "version": 1,
            "mainTrigger": {
              "id": "t.main",
              "stableKey": "{{stableKey}}",
              "kind": "passive",
              "viewType": "Orc.Cards.CardEventView",
              "hooks": ["{{hook}}"],
              "events": [
                { "id": "e1", "entry": "HandleAsync", "csx": {{csxJson}} }
              ]
            },
            "otherTriggers": [],
            "modings": [],
            "injects": []
          }
        }
        """;
    }

    private static string InlineDamageCardJson()
    {
        var inline = CsxSnapshotJson(InlineDamageSnapshotId, "端到端·内联伤害", "unit.joined", InlineDamageCsx);
        return $$"""
        {
          "schemaVersion": 1,
          "id": "{{InlineDamageCardId}}",
          "name": "内联伤害兵",
          "components": [
            { "component": "typeCategory", "type": ["infantry"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 4 },
            { "component": "tagData", "rarity": "Standard" },
            { "component": "effects", "inline": [ {{inline}} ] }
          ]
        }
        """;
    }

    private static string LibraryJoinCardJson() => $$"""
    {
      "schemaVersion": 1,
      "id": "{{LibraryJoinCardId}}",
      "name": "库引用加入兵",
      "components": [
        { "component": "typeCategory", "type": ["infantry"] },
        { "component": "factionCost", "faction": "Germany", "kredits": 1 },
        { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 4 },
        { "component": "tagData", "rarity": "Standard" },
        { "component": "effects", "prefabs": ["{{LibraryJoinPrefabId}}"] }
      ]
    }
    """;

    private static string CycleDeployCardJson() => $$"""
    {
      "schemaVersion": 1,
      "id": "{{CycleDeployCardId}}",
      "name": "库引用部署兵",
      "components": [
        { "component": "typeCategory", "type": ["infantry"] },
        { "component": "factionCost", "faction": "Germany", "kredits": 1 },
        { "component": "battleStats", "operationCost": 1, "attack": 2, "defense": 4 },
        { "component": "tagData", "rarity": "Standard" },
        { "component": "effects", "prefabs": ["{{CycleDeployPrefabId}}"] }
      ]
    }
    """;

    private static EffectSnapshot LibraryJoinSetSnapshot()
        => new(new EffectPrefab(
            LibraryJoinPrefabId,
            new TriggerPrefab(
                "t.main",
                "端到端·库置位",
                TriggerKind.Passive,
                hooks: new[] { "unit.joined" },
                events: new[] { new EventPrefab("e1", assemblyKey: LibraryJoinPrefabId) })));

    /// <summary>库置位处理器（assemblyKey 真注册）：宿主卡加入时置位 CanMove/CanAttack（自指过滤）。</summary>
    private static readonly Func<CardEventView, Context, CancellationToken, Task> LibraryJoinSetHandler =
        (view, ctx, ct) =>
        {
            if (view.Unit is UnitCard joined && ReferenceEquals(joined, view.Host))
            {
                var command = joined.GetData<CommandData>();
                command.ResetQuotas(1, 1);
            }

            return Task.CompletedTask;
        };

    /// <summary>a 链内联 csx（真编译）：友方单位加入时，对一个敌方单位造成 1 点伤害（编译产物固化）。</summary>
    private const string InlineDamageCsx = """
Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    var self = view.Host as Card;
    if ((view.Card ?? view.Unit) is Orc.Game.Cards.CardBase actorEvent && self is Orc.Game.Cards.CardBase actorSelf && actorSelf.Owner is not null && object.ReferenceEquals(actorSelf.Owner, actorEvent.Owner))
    {
        // [damage] amount=1 selector(sel="one" side="enemy" unitType=null keyword=null)
        var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
        if (runtime is not null)
        {
            var targets = await runtime.SelectAsync(self!, new Orc.Game.Effects.EffectSelector("one", "enemy", null, null, null, null, null));
            foreach (var target in targets)
            {
                await runtime.DamageAsync(target, 1, self, ct);
            }
        }
        
    }
};
""";

    /// <summary>c 链部署置位 csx（真编译；与既有词条样本行为等价）：unit.deployed → Host==载荷单位 → 置位。</summary>
    private const string DeploySetCsx = """
Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>
{
    if (view.Unit is not Orc.Cards.Card deployed || !object.ReferenceEquals(deployed, view.Host))
    {
        return Task.CompletedTask; // 非本单位部署：不处理（Host==载荷过滤）
    }

    if (deployed.TryGetData<Orc.Game.Cards.CommandData>(out var command))
    {
        command.ResetQuotas(1, 1); // 置额度（覆盖部署初值 0/0；重复信号幂等）
    }

    return Task.CompletedTask; // 无 CommandData（防御）：跳过、不抛错
};
""";

    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), "orc-toolchain-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTempDir(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
