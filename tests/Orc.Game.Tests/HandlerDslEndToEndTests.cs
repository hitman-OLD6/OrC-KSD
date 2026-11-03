using Orc.Core.Model;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 「DSL → csx → **真跑起来**」的端到端证据：用一份**临时卡包**（卡 ＋ 含 DSL handler 的组件资产），
/// 走完整的装载链装置、建局、打出指令，最后断言**世界状态**（目标防御真的掉了）。
/// <para>
/// 与 <see cref="HandlerDslCodegenCompilesTests"/> 的分工：那边证明「生成的文本是合法 C#」，
/// 这边证明「跑起来之后语义是对的」。两者都不能省——编译过但参数顺序错、
/// <c>view</c> 字段取到 null、动作打空，都只有真跑一次才看得出来。
/// </para>
/// </summary>
public class HandlerDslEndToEndTests
{
    /// <summary>
    /// R38 Q3／D33 的**可观测断言**：`choice` 选第 k 项就走第 k 条分支。
    /// 两个分支都落在「抽牌张数」上（1 vs 2），因此分支选错会直接体现在手牌数上。
    /// </summary>
    [Theory]
    [InlineData("抽 1 张", 1)]
    [InlineData("抽 2 张", 2)]
    public async Task choice真打出_选第k项走第k条分支(string picked, int drawn)
    {
        using var pack = TempDslPack.WithDsl(ChoiceThenDrawDsl);
        var bridge = ChoiceBridge(picked);

        var match = await pack.CreateMatchAsync(bridge);
        var playerA = match.Players[0];

        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, TempDslPack.CardId);
        var before = playerA.Hand.Count;

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(before - 1 + drawn, playerA.Hand.Count); // 打出 −1，抽 +drawn

        // R38 Q5／D34：成功裁决落在 `host.Engine`（与 RandomSelections 同址），picked 为 **1 起**。
        var record = Assert.Single(match.Engine.ChoiceSelections);
        Assert.Equal(picked, record.Label);
        Assert.Equal(drawn, record.Picked);
        Assert.Equal(new[] { "抽 1 张", "抽 2 张" }, record.Labels);
        Assert.Equal("选一项", record.Prompt);
        Assert.Equal("火力支援", record.Viewer);
    }

    /// <summary>
    /// R38 Q3／D33：**取消 = 整张牌打出被中断**——断言可观测后果（牌仍在手、未扣费、`choice` 之后的节点零副作用），
    /// 不只断言「抛了异常」（新旧实现都会抛，只断言抛异常区分不出打出是否被中断）。
    /// </summary>
    [Fact]
    public async Task choice被取消_牌仍在手且未扣费且后续节点零副作用()
    {
        using var pack = TempDslPack.WithDsl(ChoiceThenDrawDsl);
        var bridge = new MockTargeterBridge
        {
            InteractionScript = (description, responder) => responder.Cancel(description.RequestId),
        };

        var match = await pack.CreateMatchAsync(bridge);
        var playerA = match.Players[0];

        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, TempDslPack.CardId);
        var handBefore = playerA.Hand.Count;
        var pointsBefore = playerA.Points;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => match.PlayManager.PlayOrderAsync(command));

        // D36：类型只有一个，排障靠「前缀 ＋ reason ＋ 关键 detail」；cancelled 的消息必须**中性**。
        Assert.StartsWith("choice-failed[cancelled]:", error.Message, StringComparison.Ordinal);
        Assert.Contains("可能来自玩家取消或对局收束", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("玩家取消了选择", error.Message, StringComparison.Ordinal);

        // D33：order.finalize 未执行 ⇒ 牌仍在手、未扣费；分支内抽牌同样没发生（＝choice 之后零副作用）。
        Assert.Contains(command, playerA.Hand);
        Assert.Equal(handBefore, playerA.Hand.Count);
        Assert.Equal(pointsBefore, playerA.Points);
        Assert.Empty(match.Engine.ChoiceSelections); // 只记成功裁决（Q5）
    }

    /// <summary>R38：无交互通道 ⇒ 明确失败（`bridge-not-assembled`），不返回哨兵、不选首项。</summary>
    [Fact]
    public async Task choice无桥_明确失败且不选首项()
    {
        using var pack = TempDslPack.WithDsl(ChoiceThenDrawDsl);

        var match = await pack.CreateMatchAsync(withoutBridge: true);
        var playerA = match.Players[0];

        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, TempDslPack.CardId);
        var handBefore = playerA.Hand.Count;
        var pointsBefore = playerA.Points;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => match.PlayManager.PlayOrderAsync(command));

        Assert.StartsWith("choice-failed[bridge-not-assembled]:", error.Message, StringComparison.Ordinal);
        Assert.Contains("选项数=2", error.Message, StringComparison.Ordinal);

        // 「不选首项」的可观测证据：分支的抽牌没有发生（否则手牌会 −1+1 而看不出差别，故两分支张数不同）。
        Assert.Equal(handBefore, playerA.Hand.Count);
        Assert.Contains(command, playerA.Hand);
        Assert.Equal(pointsBefore, playerA.Points);
        Assert.Empty(match.Engine.ChoiceSelections);
    }

    /// <summary>
    /// R38：同一效果内 `ask` → `choice` 的**呈现顺序＝调用顺序**（共用 TargeterManager 的全局 FIFO）。
    /// </summary>
    [Fact]
    public async Task ask与choice_按调用顺序呈现()
    {
        using var pack = TempDslPack.WithDsl(
            """
            {
              "schemaVersion": 1,
              "actions": [
                { "op": "ask", "kind": "unit", "side": "enemy", "prompt": "选一个敌方单位", "bind": "targets" },
                { "op": "choice", "prompt": "选一项", "options": [
                    { "label": "抽 1 张", "actions": [ { "op": "draw", "count": { "lit": 1 } } ] },
                    { "label": "抽 2 张", "actions": [ { "op": "draw", "count": { "lit": 2 } } ] }
                ] }
              ]
            }
            """);

        var presented = new List<string>();
        var bridge = new MockTargeterBridge();
        bridge.InteractionScript = (description, responder) =>
        {
            var slot = TargeterTestKit.PrimarySlot(description);
            lock (presented)
            {
                presented.Add(slot);
            }

            if (slot == Orc.Game.Targeting.SelectorNames.Option)
            {
                responder.Complete(description.RequestId, TargeterTestKit.IdentifierSelection(slot, "抽 2 张"));
            }
            else
            {
                responder.Complete(
                    description.RequestId,
                    TargeterTestKit.Selection(slot, description.AllowedTargets[0]));
            }
        };

        var match = await pack.CreateMatchAsync(bridge);
        var playerB = match.Players[1];
        await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);

        var playerA = match.Players[0];
        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, TempDslPack.CardId);
        var before = playerA.Hand.Count;

        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(
            new[] { Orc.Game.Targeting.SelectorNames.FieldUnit, Orc.Game.Targeting.SelectorNames.Option },
            presented);
        Assert.Equal(before - 1 + 2, playerA.Hand.Count);
    }

    /// <summary>两个分支都落在抽牌张数上，分支选错会直接体现在手牌数上（选首项的实现会被这条抓住）。</summary>
    private const string ChoiceThenDrawDsl =
        """
        {
          "schemaVersion": 1,
          "actions": [
            { "op": "choice", "prompt": "选一项", "options": [
                { "label": "抽 1 张", "actions": [ { "op": "draw", "count": { "lit": 1 } } ] },
                { "label": "抽 2 张", "actions": [ { "op": "draw", "count": { "lit": 2 } } ] }
            ] }
          ]
        }
        """;

    /// <summary>按文案应答的桥（`choice` 走的是标识集：`picks` 按 1 起序号解标识）。</summary>
    private static MockTargeterBridge ChoiceBridge(string label)
    {
        var bridge = new MockTargeterBridge();
        bridge.InteractionScript = (description, responder) =>
            responder.Complete(
                description.RequestId,
                TargeterTestKit.IdentifierSelection(TargeterTestKit.PrimarySlot(description), label));
        return bridge;
    }

    /// <summary>
    /// 指令「按图打 3 伤」：<c>ask</c> 选一个敌方单位 → <c>iterate</c> 遍历 →
    /// <c>damage</c> 扣 3 点防御。等价于手写 csx 的 <c>SelectAsync + foreach DamageAsync</c>。
    /// </summary>
    [Fact]
    public async Task DSL_handler_真装载真打出_目标扣掉对应防御()
    {
        using var pack = TempDslPack.WithDsl(
            """
            {
              "schemaVersion": 1,
              "actions": [
                { "op": "ask", "kind": "unit", "side": "enemy", "prompt": "选一个敌方单位", "bind": "targets" },
                { "op": "iterate", "over": { "var": "targets" }, "as": "t",
                  "actions": [
                    { "op": "damage", "target": { "var": "t" }, "amount": { "lit": 3 } }
                  ] }
              ]
            }
            """);

        var match = await pack.CreateMatchAsync();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        Assert.Equal(5, target.Modifiers.GetEffectiveValue(CardStatFields.Defense));

        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, TempDslPack.CardId);
        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(2, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 5 − 3
    }

    /// <summary>
    /// 条件分支 ＋ 循环次数：<c>loop{times:2}</c> 内 <c>branch</c> 按目标防御判定，
    /// 每轮扣 1 ⇒ 两轮后防御 5→3。锁的是「循环真的跑了两轮」与「表达式取到的是实时值」。
    /// </summary>
    [Fact]
    public async Task DSL_handler_循环与分支_按实时视图值判定()
    {
        using var pack = TempDslPack.WithDsl(
            """
            {
              "schemaVersion": 1,
              "actions": [
                { "op": "ask", "kind": "unit", "side": "enemy", "prompt": "选一个敌方单位", "bind": "targets" },
                { "op": "iterate", "over": { "var": "targets" }, "as": "t",
                  "actions": [
                    { "op": "loop", "times": { "lit": 2 },
                      "actions": [
                        { "op": "damage", "target": { "var": "t" }, "amount": { "lit": 1 } }
                      ] }
                  ] }
              ]
            }
            """);

        var match = await pack.CreateMatchAsync();
        var playerB = match.Players[1];

        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var command = await DiyExampleKit.PrepareCommandAsync(match, match.Players[0], TempDslPack.CardId);
        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(3, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 5 − 1 − 1
    }

    /// <summary>
    /// **对照组**：同一份组件、同一套视图，只把 handler 换回**手写 csx**（照抄 samples 的实现）。
    /// 用来把「DSL 链路的问题」与「卡包装置的问题」分开——对照也打不出伤害，就说明问题不在 DSL。
    /// </summary>
    [Fact]
    public async Task 对照组_手写csx等同效果()
    {
        using var pack = TempDslPack.WithScript(
            """
            Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
            {
                if (view.Card is not Orc.Cards.Card played)
                {
                    return;
                }

                var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(played);
                if (runtime is null)
                {
                    return;
                }

                var targets = await runtime.SelectAsync(played, new Orc.Game.Effects.EffectSelector("one", "enemy"));
                foreach (var t in targets)
                {
                    await runtime.DamageAsync(t, 3, played, ct);
                }
            };
            """);

        var match = await pack.CreateMatchAsync();
        var playerB = match.Players[1];
        var target = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var command = await DiyExampleKit.PrepareCommandAsync(match, match.Players[0], TempDslPack.CardId);
        var result = await match.PlayManager.PlayOrderAsync(command);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(2, target.Modifiers.GetEffectiveValue(CardStatFields.Defense)); // 5 − 3，与 DSL 版同效
    }

    /// <summary>
    /// **模板 → 草案 → 摊平 → 装载 → 打出**：证明模板内置的守卫在**运行期**真的过滤。
    /// <para>
    /// 关键断言不是「自己打出时触发」（那只证明挂载对），而是「**别人**打出时零触发」——
    /// 信号是全局广播：<c>engine#card.played</c> 上的边在任何卡打出时都会跑到。
    /// 守卫若没生成、或比较对象取错，抽牌照样会发生，而文本级/编译级测试都看不出来。
    /// </para>
    /// <para>
    /// 干扰卡是自造的 <c>diy.blank</c>（只有类别与费用、**没有任何 effect 组件**），
    /// 因此它打出时只有 fire-support 那条边在场——抽没抽牌完全由守卫决定。
    /// </para>
    /// </summary>
    [Fact]
    public async Task 模板派生的效果_守卫在运行期拦住别人打的牌()
    {
        using var pack = TempDslPack.Plain();
        var dsl = Orc.Game.Effects.HandlerDslAssembly.Create();
        var templateRoot = Path.Combine(DiyExampleKit.FindRepoRoot(), "packs", "effect-templates");

        const string dslDocument =
            """{ "schemaVersion": 1, "actions": [ { "op": "draw", "count": { "lit": 1 } } ] }""";
        File.WriteAllText(
            Path.Combine(pack.EffectsDirectory, "handler-dsl", "template-on-played.hdsl.json"), dslDocument);

        // 草案：继承模板（父无 handler），只实现空洞成员 onHook。
        const string draftPath = "modings/diy.fire-support.effect.moding.json";
        Directory.CreateDirectory(Path.Combine(pack.Root, "modings"));
        File.WriteAllText(Path.Combine(pack.Root, draftPath), $$"""
        {
          "schemaVersion": 4,
          "asset": {
            "kind": "moding",
            "id": "diy.fire-support.effect.draft",
            "product": { "id": "diy.fire-support.effect" },
            "extends": "template.card.played.self",
            "requires-host": "any",
            "active-when": "loaded",
            "one-shot": false,
            "private-fields": [],
            "members": [
              {
                "name": "onHook",
                "visibility": [ "mountable" ],
                "view": { "fields": [
                  { "name": "Card", "type": "entity", "writable": false, "required": false },
                  { "name": "Player", "type": "entity", "writable": false, "required": false },
                  { "name": "Source", "type": "entity", "writable": false, "required": false }
                ] },
                "handler": {
                  "id": "diy.fire-support.effect.draft#onHook",
                  "entry": "HandleAsync",
                  "dsl": "handler-dsl/template-on-played.hdsl.json",
                  "dslHash": "{{HandlerContent.Hash(dslDocument)}}"
                }
              }
            ],
            "mounts": [],
            "wraps": []
          }
        }
        """);

        var flattened = Orc.Packages.Editing.ModingFlattener.Flatten(pack.Root, draftPath, dsl, templateRoot);
        Assert.True(flattened.Success, flattened.Describe());
        File.WriteAllText(
            Path.Combine(pack.EffectsDirectory, flattened.ProductId + ".component.json"),
            flattened.ProductJson!);

        // 干扰卡：只有类别与费用，没有任何 effect 组件（必须在建局前写入——装载期一次性读卡目录）。
        File.WriteAllText(Path.Combine(pack.CardsDirectory, "diy.blank.card.json"), """
        {
          "schemaVersion": 1,
          "id": "diy.blank",
          "name": "空白指令",
          "components": [
            { "component": "typeCategory", "type": ["order"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 0 },
            { "component": "tagData", "rarity": "Standard" }
          ]
        }
        """);

        var match = await pack.CreateMatchAsync();
        var playerA = match.Players[0];

        // ① 别人打的牌：守卫必须拦住（打出 −1，不抽牌）。
        var blank = await DiyExampleKit.PrepareCommandAsync(match, playerA, "diy.blank");
        var beforeBlank = playerA.Hand.Count;
        var blankResult = await match.PlayManager.PlayOrderAsync(blank);
        Assert.Equal(PlayResultStatus.Success, blankResult.Status);
        Assert.Equal(beforeBlank - 1, playerA.Hand.Count);

        // ② 自己打出：守卫通过（打出 −1、抽 +1 ⇒ 手牌数不变）。
        var command = await DiyExampleKit.PrepareCommandAsync(match, playerA, TempDslPack.CardId);
        var beforeSelf = playerA.Hand.Count;
        var selfResult = await match.PlayManager.PlayOrderAsync(command);
        Assert.Equal(PlayResultStatus.Success, selfResult.Status);
        Assert.Equal(beforeSelf, playerA.Hand.Count);
    }

    /// <summary>
    /// 临时卡包：<c>cards/</c> 里一张指令卡（复用 samples 的 <c>diy.fire-support</c>），
    /// <c>effects/</c> 里一个同名组件资产，其 handler 指向 <c>effects/handler-dsl/*.hdsl.json</c>。
    /// <para>
    /// 组件资产结构照抄 <c>samples/card-pack-diy/effects-v4/diy.fire-support.effect.component.json</c>，
    /// 只把 <c>handler.script</c> 换成 <c>handler.dsl</c>——这正是「手写 csx」与「图形化 DSL」的等价性所在。
    /// </para>
    /// </summary>
    private sealed class TempDslPack : IDisposable
    {
        public const string CardId = "diy.fire-support";

        private const string ComponentId = "diy.fire-support.effect";

        private readonly string _root;

        /// <summary>用 DSL 文档构造（handler 指向 <c>handler-dsl/on-played.hdsl.json</c>）。</summary>
        /// <summary>原始 samples 包（不覆写任何组件）：模板摊平用例自行写入产物。</summary>
        public static TempDslPack Plain() => new(null, null);

        public static TempDslPack WithDsl(string dslDocument) =>
            new(dslDocument, $$"""
                {
                  "id": "{{ComponentId}}#onPlayed",
                  "entry": "HandleAsync",
                  "dsl": "handler-dsl/on-played.hdsl.json"
                }
                """);

        /// <summary>用手写 csx 构造（handler 内联脚本）——对照组用。</summary>
        public static TempDslPack WithScript(string csx) =>
            new(null, $$"""
                {
                  "id": "{{ComponentId}}#onPlayed",
                  "entry": "HandleAsync",
                  "script": {{System.Text.Json.JsonSerializer.Serialize(csx)}}
                }
                """);

        private TempDslPack(string? dslDocument, string? handlerJson)
        {
            _root = Path.Combine(Path.GetTempPath(), "orc-hdsl-e2e-" + Guid.NewGuid().ToString("N"));

            // **整包复制** samples/card-pack-diy，只替换 fire-support 那个组件的 handler 来源。
            // 不手工搭最小包：包根有 manifest.json 与 lock 文件，卡目录有 sidecar，
            // 少一样就可能装载成功但触发链不认（表现为「打出成功、伤害为 0」，极难定位）。
            var source = Path.Combine(DiyExampleKit.FindRepoRoot(), "samples", "card-pack-diy");
            CopyDirectory(source, _root);

            var cards = Path.Combine(_root, "cards");
            var effects = Path.Combine(_root, "effects-v4");
            var dslDirectory = Path.Combine(effects, "handler-dsl");
            Directory.CreateDirectory(dslDirectory);

            if (dslDocument is not null)
            {
                File.WriteAllText(Path.Combine(dslDirectory, "on-played.hdsl.json"), dslDocument);
            }

            // handlerJson 为 null ＝ 保持 samples 原样（模板摊平用例自行覆写组件文件）。
            if (handlerJson is not null)
            {
                File.WriteAllText(
                    Path.Combine(effects, ComponentId + ".component.json"), ComponentJson(handlerJson));
            }

            CardsDirectory = cards;
            EffectsDirectory = effects;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        public string CardsDirectory { get; }

        public string EffectsDirectory { get; }

        /// <summary>包根（摊平器需要它）。</summary>
        public string Root => _root;

        public async Task<Orc.Game.Match> CreateMatchAsync(
            MockTargeterBridge? bridge = null, bool withoutBridge = false)
        {
            var pack = DiyExampleKit.LoadPack(CardsDirectory);
            return await DiyExampleKit.CreateMatchAsync(
                pack, EffectsDirectory, targeterBridge: bridge, withoutTargeterBridge: withoutBridge);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响断言结论（进程退出后由系统回收）。
            }
        }

        /// <summary>与 <c>samples/card-pack-diy/cards/diy.fire-support.card.json</c> 同构。</summary>
        private const string CardJson = """
        {
          "schemaVersion": 1,
          "id": "diy.fire-support",
          "name": "火力支援",
          "components": [
            { "component": "typeCategory", "type": ["order"] },
            { "component": "factionCost", "faction": "Germany", "kredits": 1 },
            { "component": "tagData", "rarity": "Standard" },
            { "component": "effects", "prefabs": ["diy.fire-support.effect"] }
          ]
        }
        """;

        /// <summary>
        /// 与 samples 的手写版**同一份视图声明**，差别只在 handler 来源：
        /// 这里是 <c>dsl</c>（图形化产物），那里是 <c>script</c>（手写 csx）。
        /// </summary>
        private static string ComponentJson(string handlerJson) => $$"""
        {
          "schemaVersion": 4,
            "asset": {
              "kind": "component",
              "id": "diy.fire-support.effect",
              "requires-host": "any",
              "active-when": "loaded",
              "one-shot": false,
              "private-fields": [],
              "wraps": [],
              "mounts": [
                { "target": "engine#card.played", "member": "onPlayed" }
              ],
            "members": [
              {
                "name": "onPlayed",
                "visibility": ["callable", "mountable"],
                "view": {
                  "fields": [
                    { "name": "Card", "type": "entity", "writable": false, "required": false },
                    { "name": "Effect", "type": "entity", "writable": false, "required": false },
                    { "name": "Unit", "type": "entity", "writable": false, "required": false },
                    { "name": "Player", "type": "entity", "writable": false, "required": false },
                    { "name": "Killer", "type": "entity", "writable": false, "required": false },
                    { "name": "Source", "type": "entity", "writable": false, "required": false },
                    { "name": "TriggerCard", "type": "entity", "writable": false, "required": false }
                  ]
                },
                "handler": {{handlerJson}}
              }
            ]
          }
        }
        """;
    }
}
