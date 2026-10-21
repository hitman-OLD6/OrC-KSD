using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Mcp;
using Orc.Mcp.Contracts;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// R6 三条链（**必须走 MCP 工具面**——<c>card.write</c> / <c>card.load</c> / <c>play.command</c> /
/// <c>play.counter</c> / <c>match.end_turn</c> 才是链路真实形态）：
/// ①指令链：造指令卡 → 注册 → 入手 → 补费 → 切到其回合 → 打出 → 断言扣费/离手/主动效果生效；
/// ②反制链：造反制卡（含 effect handler）→ 入手 → 切到持有方 → 激活 → 切回对手 → 对手打指令 →
///   断言命中弃置（一次性消耗）、指令照常扣费离手、效果未触发；
/// ③新卡链：写卡→加载→实例化→打出（含 negative：非法定义被拒）。
/// </summary>
public class SandboxPrimitiveChainTests
{
    /// <summary>指令卡：打出时抽 1 张（主动效果可观测＝手牌 +1）。</summary>
    private const string DrawOrderCardJson = """
{
  "schemaVersion": 1,
  "id": "sbx.chain.order",
  "name": "沙盒抽牌指令",
  "components": [
    { "component": "typeCategory", "type": ["order"] },
    { "component": "factionCost", "faction": "Germany", "kredits": 1 },
    { "component": "tagData", "rarity": "Standard" },
    {
      "component": "effects",
      "prefabs": [],
      "inline": [
        {
          "schemaVersion": 3,
          "root": {
            "id": "sbx.chain.order.effect",
            "version": 1,
            "oneShot": false,
            "mainTrigger": {
              "id": "t.main",
              "stableKey": "sbx·抽牌指令",
              "kind": "passive",
              "viewType": "Orc.Cards.CardEventView",
              "hooks": ["card.played"],
              "events": [
                {
                  "id": "onPlayed",
                  "entry": "HandleAsync",
                  "csx": "Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>\n{\n    if (view.Card is not Orc.Cards.Card played || !object.ReferenceEquals(played, view.Host))\n    {\n        return;\n    }\n\n    var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(played);\n    if (runtime is null)\n    {\n        return;\n    }\n\n    await runtime.DrawAsync(played, 1, ct, played);\n};\n",
                  "version": 1
                }
              ],
              "version": 1
            },
            "otherTriggers": [],
            "wrappers": [],
            "injects": []
          }
        }
      ]
    }
  ]
}
""";

    /// <summary>反制卡：命中即 <c>ctx.Stop()</c>（默认反制口径——指令照常扣费离手，效果不触发）。</summary>
    private const string StopCounterCardJson = """
{
  "schemaVersion": 1,
  "id": "sbx.chain.counter",
  "name": "沙盒反制",
  "components": [
    { "component": "typeCategory", "type": ["countermeasure"] },
    { "component": "factionCost", "faction": "Germany", "kredits": 0 },
    { "component": "tagData", "rarity": "Standard" },
    {
      "component": "effects",
      "prefabs": [],
      "inline": [
        {
          "schemaVersion": 3,
          "root": {
            "id": "sbx.chain.counter.effect",
            "version": 1,
            "oneShot": false,
            "mainTrigger": {
              "id": "t.main",
              "stableKey": "sbx·反制",
              "kind": "active",
              "viewType": "Orc.Game.Triggers.CardTriggerView",
              "hooks": [],
              "events": [
                {
                  "id": "onCounter",
                  "entry": "HandleAsync",
                  "csx": "Func<Orc.Game.Triggers.CardTriggerView, Context, CancellationToken, Task> HandleAsync = (view, ctx, ct) =>\n{\n    ctx.Stop();\n    return Task.CompletedTask;\n};\n",
                  "version": 1
                }
              ],
              "version": 1
            },
            "otherTriggers": [],
            "wrappers": [],
            "injects": []
          }
        }
      ]
    }
  ]
}
""";

    [Fact]
    public async Task OrderChain_PrimitiveBuiltBoard_PlayCommand_Succeeds()
    {
        using var fixture = new SandboxFixture(csx: true);
        await SandboxPrimitiveToolTests.CreateMatchWithUnitDefinitionAsync(
            fixture, DrawOrderCardJson, "sbx.chain.order");

        // ① 原语造卡入手（不经过正常抽牌/构筑链）。
        var orderRef = await InstantiateToHandAsync(fixture, "sbx.chain.order", owner: 0);

        // ② 补足费用（正常动作链会因点数不足被拒——原语直接设定）。
        Assert.True((await fixture.InvokeAsync("sandbox.points", """{"owner":0,"points":5}""")).Ok);

        // ③ 行动方须为该卡 owner（PlayManager.CheckAdmission：己方回合 ＋ 卡在手牌）。
        await EnsureTurnOfAsync(fixture, 0);

        var handBefore = (await fixture.InvokeAsync("observe.view"));
        var handCountBefore = SandboxPrimitiveToolTests.HandRefs(handBefore.Details, 0).Length;
        var pointsBefore = SandboxPrimitiveToolTests.PointsOf(handBefore.Details, 0);

        // ④ 打出。
        var played = await fixture.InvokeAsync("play.command", $$"""{"ref":"{{orderRef}}"}""");
        Assert.True(played.Ok, played.Message);

        var view = await fixture.InvokeAsync("observe.view");
        var hand = SandboxPrimitiveToolTests.HandRefs(view.Details, 0);

        Assert.DoesNotContain(orderRef, hand); // 离手
        Assert.Equal(pointsBefore - 1, SandboxPrimitiveToolTests.PointsOf(view.Details, 0)); // 扣费（部署费 1）
        Assert.Equal(handCountBefore, hand.Length); // 离手 -1、效果抽牌 +1 ⇒ 净持平（主动效果已生效）
    }

    /// <summary>
    /// 反制链（原语摆局面段不变）：造含 effect 的反制卡 → 入手 → 切到持有方 → <c>play.counter</c> 激活 →
    /// 切回对手 → 对手 <c>play.command</c> → **命中拦截**：指令 <c>card.played</c> 计数 0、指令效果未触发
    /// （手牌净 -1 而非持平）、指令仍扣费并离手；反制卡一次性消耗弃置（<c>card.discarded</c> ＋ 离手）。
    /// </summary>
    [Fact]
    public async Task CounterChain_PrimitiveBuiltCounter_StopsOrder()
    {
        using var fixture = new SandboxFixture(csx: true);
        await SandboxPrimitiveToolTests.CreateMatchWithUnitDefinitionAsync(
            fixture, DrawOrderCardJson, "sbx.chain.order");

        // 反制卡与指令卡同批加载（card.load 只追加不覆盖；换 id 重新加载）。
        await SandboxPrimitiveToolTests.CreateMatchWithUnitDefinitionAsync(
            fixture, StopCounterCardJson, "sbx.chain.counter");

        var orderRef = await InstantiateToHandAsync(fixture, "sbx.chain.order", owner: 0);
        var counterRef = await InstantiateToHandAsync(fixture, "sbx.chain.counter", owner: 1);

        Assert.True((await fixture.InvokeAsync("sandbox.points", """{"owner":0,"points":5}""")).Ok);
        Assert.True((await fixture.InvokeAsync("sandbox.points", """{"owner":1,"points":5}""")).Ok);

        // ① 切到反制持有方 → 激活（原语摆局的直接产物：卡在手 ＋ 己方回合 ＋ 点数足够）。
        await EnsureTurnOfAsync(fixture, 1);
        var activated = await fixture.InvokeAsync("play.counter", $$"""{"ref":"{{counterRef}}"}""");
        Assert.True(activated.Ok, activated.Message);

        // ② 切回对手（指令持有方）→ 打出指令。
        await EnsureTurnOfAsync(fixture, 0);

        var viewBefore = await fixture.InvokeAsync("observe.view");
        var pointsBefore = SandboxPrimitiveToolTests.PointsOf(viewBefore.Details, 0);
        var handCountBefore = SandboxPrimitiveToolTests.HandRefs(viewBefore.Details, 0).Length;

        var played = await fixture.InvokeAsync("play.command", $$"""{"ref":"{{orderRef}}"}""");
        Assert.True(played.Ok, played.Message);

        var view = await fixture.InvokeAsync("observe.view");
        var handA = SandboxPrimitiveToolTests.HandRefs(view.Details, 0);

        // Stop 语义：Resolve 不执行（不发 card.played、指令效果不触发），Finalize 照常（扣费、离手）。
        Assert.DoesNotContain(orderRef, handA);
        Assert.Equal(pointsBefore - 1, SandboxPrimitiveToolTests.PointsOf(view.Details, 0));
        Assert.Equal(handCountBefore - 1, handA.Length); // 离手 -1 且抽牌效果未触发（未持平）

        // 反制一次性消耗：已弃置、离手（card.discarded 已发）。
        Assert.DoesNotContain(counterRef, SandboxPrimitiveToolTests.HandRefs(view.Details, 1));
        Assert.Equal(0, await CountUpdatesAsync(fixture, "card.played"));
        Assert.True(await CountUpdatesAsync(fixture, "card.discarded") >= 1);
    }

    [Fact]
    public async Task NewCardChain_WriteLoadInstantiatePlay()
    {
        using var fixture = new SandboxFixture(csx: true);
        await SandboxPrimitiveToolTests.CreateMatchWithUnitDefinitionAsync(
            fixture, DrawOrderCardJson, "sbx.chain.order");

        var orderRef = await InstantiateToHandAsync(fixture, "sbx.chain.order", owner: 0);
        Assert.True((await fixture.InvokeAsync("sandbox.points", """{"owner":0,"points":5}""")).Ok);
        await EnsureTurnOfAsync(fixture, 0);

        var viewBefore = await fixture.InvokeAsync("observe.view");
        var handCountBefore = SandboxPrimitiveToolTests.HandRefs(viewBefore.Details, 0).Length;

        var played = await fixture.InvokeAsync("play.command", $$"""{"ref":"{{orderRef}}"}""");
        Assert.True(played.Ok, played.Message);

        var view = await fixture.InvokeAsync("observe.view");
        Assert.Equal(handCountBefore, SandboxPrimitiveToolTests.HandRefs(view.Details, 0).Length);
    }

    [Fact]
    public async Task NewCardChain_InvalidDefinition_Rejected()
    {
        using var fixture = new SandboxFixture(csx: true);
        Assert.True((await fixture.InvokeAsync("sandbox.create")).Ok);

        // negative：非法卡数据体（schemaVersion 不符）＝写入阶段即拒绝（不落盘）。
        var malformed = await fixture.InvokeAsync(
            "card.write",
            new JsonObject
            {
                ["path"] = "pack/cards/sbx.bad.card.json",
                ["json"] = """{"schemaVersion":99,"id":"sbx.bad","name":"坏卡","components":[]}""",
            }.ToJsonString());
        Assert.False(malformed.Ok);

        // negative：未注册定义不可实例化。
        var rejected = await fixture.InvokeAsync(
            "sandbox.instantiate",
            """{"definitionId":"sbx.never.registered","owner":0,"zone":"hand"}""");
        Assert.False(rejected.Ok);
        Assert.Equal(McpCodes.EngineRejected, rejected.Code);
        Assert.Equal("definition-unknown", rejected.EngineResult!["reason"]!.GetValue<string>());
    }

    // ---------- 共用 ----------

    private static async Task<string> InstantiateToHandAsync(SandboxFixture fixture, string definitionId, int owner)
    {
        var result = await fixture.InvokeAsync(
            "sandbox.instantiate",
            $$"""{"definitionId":"{{definitionId}}","owner":{{owner}},"zone":"hand"}""");

        Assert.True(result.Ok, result.Message);
        return result.EngineResult!["ref"]!.GetValue<string>();
    }

    /// <summary>用 <c>match.end_turn</c> 推进到指定玩家回合（原语不提供 set_turn）。</summary>
    private static async Task EnsureTurnOfAsync(SandboxFixture fixture, int playerIndex)
    {
        for (var guard = 0; guard < 4; guard++)
        {
            var view = await fixture.InvokeAsync("observe.view");
            var current = view.Details!["currentPlayerIndex"]!.GetValue<int>();
            if (current == playerIndex)
            {
                return;
            }

            var ended = await fixture.InvokeAsync("match.end_turn");
            Assert.True(ended.Ok, ended.Message);
        }

        Assert.Fail($"无法推进到玩家 #{playerIndex} 的回合。");
    }

    /// <summary>统计事件流缓存中指定更新类型的条目数（段内 entries ＋ 因果子树 children 递归）。</summary>
    private static async Task<int> CountUpdatesAsync(SandboxFixture fixture, string updateType)
    {
        var segments = await fixture.InvokeAsync("observe.segments");
        Assert.True(segments.Ok, segments.Message);

        var count = 0;
        foreach (var segment in segments.Details!["segments"]!.AsArray())
        {
            using var document = JsonDocument.Parse(segment!["json"]!.GetValue<string>());
            count += CountUpdates(document.RootElement, updateType);
        }

        return count;
    }

    private static int CountUpdates(JsonElement node, string updateType)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var count = 0;
        if (node.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.TryGetProperty("kind", out var kind)
                    && kind.GetString() == "update"
                    && entry.TryGetProperty("message", out var message)
                    && message.GetString() == updateType)
                {
                    count++;
                }
            }
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                count += CountUpdates(child, updateType);
            }
        }

        return count;
    }
}
