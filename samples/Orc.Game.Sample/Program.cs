// =============================================================================
// Orc.Game.Sample —— KARDS 风格游戏层「最小可玩宿主」示例（供集成参考）
//
// 用法：
//   dotnet run --project samples/Orc.Game.Sample                  # 自动演示（无人值守跑完一整场）
//   dotnet run --project samples/Orc.Game.Sample -- --interactive # 手动玩（控制台输入应答交互）
//   dotnet run --project samples/Orc.Game.Sample -- --seed 123    # 指定随机种子（可复现）
//
// 本示例演示的能力：
//   ① 建局：Match 构造注入（卡组／定义集／前端桥接）→ Initialize → 进入"换牌"（mulligan）相位
//   ② mulligan：**特制选择器槽位**（MulliganSelectSlot：专用 Kind/呈现标注，供 UI 播专属动画）交互换牌
//   ③ 进对局：双方确认 → 先手第 1 回合序列（turn 三连）
//   ④ 出牌：统一预行为入口 BeginUnitPrePlayAsync ＋ 桥接应答驱动后续执行链
//   ⑤ 回合循环（EndTurn）＋ 独立移动入口（BeginMoveAsync）
//   ⑥ 独立攻击入口（BeginAttackAsync）→ HQ 归零 → 终局（状态/相位/胜者/原因）与终局冻结
//   ⑦ 观察面：即时更新订阅（Engine.Subscribe）与段轮询（Engine.TakeSegments）
//
// 边界说明：
//   · sample 是**额外示例产物**，不替代集成测试——01 完整流程的 DoD 证据是
//     tests/Orc.Game.Tests/MatchEndToEndTests.cs（脚本化桥接端到端）。
//   · 生产侧由真实前端实现 ITargeterBridge（本项目的 DemoTargeterBridge 仅示例）：
//     收集＝提交前端可交互的完整引用列表；交互＝渲染槽位描述／候选并回传 Complete／Cancel。
// =============================================================================

using Orc.Foundation.Actions;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Orc.Packages;
using Orc.Plugins.BuiltIn;

namespace Orc.Game.Sample;

internal static class Program
{
    private const string InfantryId = "s_inf";
    private const string MegaId = "s_mega";
    private const string CommandId = "s_cmd";
    private const string CounterId = "s_cnt";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var interactive = args.Contains("--interactive", StringComparer.OrdinalIgnoreCase);
        var seed = ParseSeed(args) ?? 42;

        Console.WriteLine("=== Orc.Game 示例对局 ===");
        Console.WriteLine($"模式＝{(interactive ? "交互（手动应答）" : "自动演示")}；种子＝{seed}");

        // 资产化 批次 2：宿主启动时装配插件的**进程级**资产（词条等静态注册面）——须早于任何对局。
        BuiltInPlugin.RegisterProcessDefaults();
        FoundationActions.Register();

        // ① 建局：调用方提供数据（双方卡组＋定义集），Match 负责装配
        var bridge = new DemoTargeterBridge(interactive);
        var match = new Match(
            new CardList(Enumerable.Repeat(InfantryId, 10)),
            new CardList(Enumerable.Repeat(InfantryId, 10)),
            CreateDefinitions(),
            seed: seed,
            targeterBridge: bridge,
            packageActivator: new SnapshotPackageActivator(AssetCatalogSnapshot.FromPackages(new[]
            {
                BuiltInPluginPackage.Load(),
                FoundationActionsPluginPackage.Load(),
            })));
        bridge.Match = match; // 桥接需读对局状态（候选收集／交互）

        // ⑦ 观察面之一：即时更新订阅（UI 非阻塞反馈；示例直接打印到控制台）
        using var subscription = match.Engine.Subscribe((type, payload, ct) =>
        {
            Console.WriteLine($"  [更新] {type}");
            return Task.CompletedTask;
        });

        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        Console.WriteLine($"[Initialize] 状态={match.State}、相位={match.Phase}（先手回合已延后至双方确认）");

        // ② mulligan：先手换 1 张、后手不换（均经特制槽位交互）
        await RunMulliganAsync(match, bridge, playerA, autoReplaceCount: 1);
        await RunMulliganAsync(match, bridge, playerB, autoReplaceCount: 0);
        Console.WriteLine(
            $"[mulligan 完成] 相位={match.Phase}、当前行动方={NameOf(match.CurrentPlayer)}、回合={match.TurnNumber}");

        // ④ 出牌：步兵（部署费 1；第 1 回合点数＝1）
        var infantry = await InstantiateUnitToHandAsync(match, playerA, InfantryId);
        var infantryPlay = await match.PlayManager.BeginUnitPrePlayAsync(infantry);
        Console.WriteLine($"[出牌] 步兵 → {infantryPlay.Status}（玩家A 剩余点数={playerA.Points}）");

        // ⑤ 回合循环 ×2 → 回到先手第 2 回合（指挥点结算至槽 2）
        await match.EndTurn();
        await match.EndTurn();
        Console.WriteLine(
            $"[回合循环] 当前行动方={NameOf(match.CurrentPlayer)}、回合={match.TurnNumber}、点数={playerA.Points}");

        // ⑤ 独立移动入口：步兵由支援线推进前线（行动费 1）
        var move = await match.CommandManager.BeginMoveAsync(infantry);
        Console.WriteLine($"[移动] 步兵 → {move.Status}（剩余点数={playerA.Points}）");

        // ④ 出牌：巨炮（攻 30——用于一击把敌方 HQ 打到 0）
        var mega = await InstantiateUnitToHandAsync(match, playerA, MegaId);
        var megaPlay = await match.PlayManager.BeginUnitPrePlayAsync(mega);
        Console.WriteLine($"[出牌] 巨炮 → {megaPlay.Status}（剩余点数={playerA.Points}）");

        // 回合循环 ×2 → 先手第 3 回合（回合恢复使巨炮获得行动权）
        await match.EndTurn();
        await match.EndTurn();

        // ⑥ 独立攻击入口：巨炮攻击敌方 HQ → 归零 → 终局
        var strike = await match.CommandManager.BeginAttackAsync(mega);
        Console.WriteLine($"[攻击] 巨炮 → 敌方总部：{strike.Status}");

        // 终局记录（HQ 归零与认输共用同一"置结束"单源路径）
        Console.WriteLine(
            $"[终局] 状态={match.State}、相位={match.Phase}、胜者={NameOf(match.Winner)}、原因={match.EndReason}、" +
            $"玩家B总部血量={playerB.HqHealth}");

        // ⑦ 观察面之二：段轮询（每个动作作用域产出一段——UI 的批量表现播放单元）
        var segments = match.Engine.TakeSegments();
        Console.WriteLine($"[段轮询] 取到 {segments.Count} 段（终局前累计未取走的动作段）");

        // 终局冻结：动作入口拒绝、只读面可用
        try
        {
            await match.EndTurn();
            Console.WriteLine("[终局冻结] 意外：EndTurn 未被拒绝");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[终局冻结] EndTurn 被拒绝：{ex.Message}");
        }

        Console.WriteLine("=== 示例结束 ===");
        return 0;
    }

    /// <summary>跑一方的换牌段：发起特制槽位交互（自动模式按换牌张数、交互模式读输入），并打印结局。</summary>
    private static async Task RunMulliganAsync(Match match, DemoTargeterBridge bridge, Player player, int autoReplaceCount)
    {
        bridge.AutoReplaceCount = autoReplaceCount;
        var result = await match.BeginMulliganAsync(player);
        var reason = result.FailureReason is { } failure ? $"（{failure}）" : string.Empty;
        Console.WriteLine($"[mulligan] {NameOf(player)} → {result.Status}{reason}");
    }

    /// <summary>实例化＋加载一张单位卡并置入手牌（对局内取用卡实例的最小路径）。</summary>
    private static async Task<UnitCard> InstantiateUnitToHandAsync(Match match, Player player, string id)
    {
        var unit = (UnitCard)match.CardLibrary.Instantiate(id);
        await unit.LoadAsync(player);
        player.Hand.Add(unit);
        return unit;
    }

    /// <summary>示例定义集：两张单位（步兵／巨炮）＋指令／反制示例位（仅注册，供参考入口面）。</summary>
    private static IReadOnlyList<CardDefinitionEntry> CreateDefinitions() =>
    [
        new CardDefinitionEntry(
            InfantryId,
            new CardDefinition(
                "步兵", deployCost: 1, operateCost: 1, attack: 2, defense: 5,
                unitTypes: [UnitType.Infantry], faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(
            MegaId,
            new CardDefinition(
                "巨炮", deployCost: 1, operateCost: 1, attack: 30, defense: 30,
                unitTypes: [UnitType.Artillery], faction: Faction.Germany, rarity: Rarity.Standard)),
        // 指令／反制：对外入口见 GameEntryPoints（BeginOrderPrePlayAsync／UseCounterAsync）——本示例不展开
        new CardDefinitionEntry(
            CommandId,
            new CardDefinition(
                "示例指令", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
                CardCategory.Command, faction: Faction.Germany, rarity: Rarity.Standard)),
        new CardDefinitionEntry(
            CounterId,
            new CardDefinition(
                "示例反制", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
                CardCategory.Counter, faction: Faction.Germany, rarity: Rarity.Standard)),
    ];

    private static int? ParseSeed(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--seed", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var seed))
            {
                return seed;
            }
        }

        return null;
    }

    private static string NameOf(Player? player)
        => player is null ? "<无>" : player.Index == 0 ? "玩家A" : "玩家B";
}
