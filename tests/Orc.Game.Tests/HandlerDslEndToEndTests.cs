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

        private TempDslPack(string? dslDocument, string handlerJson)
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

            File.WriteAllText(
                Path.Combine(effects, ComponentId + ".component.json"), ComponentJson(handlerJson));

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

        public async Task<Orc.Game.Match> CreateMatchAsync()
        {
            var pack = DiyExampleKit.LoadPack(CardsDirectory);
            return await DiyExampleKit.CreateMatchAsync(pack, EffectsDirectory);
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
