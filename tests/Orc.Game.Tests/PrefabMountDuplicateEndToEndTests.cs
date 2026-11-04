using Orc.Cards;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Packages;
using Orc.Packages.Editing;
using Orc.Script;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// **R40 的端到端复现夹具**（现网症状：只做 <c>draw 2</c> 的 moding 草案，打出后抽了 4 张）。
/// <para>
/// 父声明 <c>engine#card.played</c>／<c>onPlayed</c>；草案只把 <c>onPlayed</c> 重载成 <c>draw 2</c>、
/// **不声明挂载** ⇒ 摊平产物 <c>mounts</c> **恰 1 条**，真实对局打出后手牌 **+2**（不是 +4）。
/// </para>
/// <para>
/// 只有端到端能看见「翻倍」：产物结构合法、装载成功、<c>flatten --check</c> 报最新，
/// 症状只剩行为翻倍（R40 的「为什么当时一路绿」）。故本用例同时断言**产物条数**与**行为**。
/// </para>
/// </summary>
public class PrefabMountDuplicateEndToEndTests : IDisposable
{
    private const string ParentId = "test.r40.e2e.parent";
    private const string DraftId = "test.r40.e2e.child.draft";
    private const string ProductId = "test.r40.e2e.child";
    /// <summary>
    /// 单行 csx（JSON 字符串内不能出现裸换行）：<c>card.played</c> 的载荷卡是自己 ⇒ 抽 2 张。
    /// </summary>
    private const string DrawScript =
        "Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) => "
        + "{ if (view.Card is not Orc.Cards.Card played) { return; } "
        + "if (!object.ReferenceEquals(played, view.Host)) { return; } "
        + "var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(played); "
        + "if (runtime is null) { return; } "
        + "await runtime.DrawAsync(played, 2, ct); };";

    private readonly string _root;
    private readonly string _pack;

    public PrefabMountDuplicateEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-r40-e2e-" + Guid.NewGuid().ToString("N"));
        _pack = Path.Combine(_root, "pack");
        Directory.CreateDirectory(Path.Combine(_pack, "effects-v4"));
        Directory.CreateDirectory(Path.Combine(_pack, "modings"));

        Write("effects-v4/" + ParentId + ".component.json", Parent());
        Write("modings/" + DraftId + ".moding.json", Draft());
    }

    [Fact]
    public async Task draw2草案不声明挂载_产物挂载恰一条_打出后手牌加二()
    {
        var flattened = ModingFlattener.FlattenAndWrite(_pack, "modings/" + DraftId + ".moding.json");
        Assert.True(flattened.Success, flattened.Describe());

        // ① 产物结构：**恰 1 条**挂载（重复条目会让同一条 handler 被挂两遍）。
        var product = AssetSchema.Read(flattened.ProductJson!, "product");
        var mount = Assert.Single(product.Mounts);
        Assert.Equal("engine#card.played", mount.Target);
        Assert.Equal("onPlayed", mount.Member);

        // ② 行为：真实对局打出这张卡，手牌 +2（不是 +4）。
        var (before, after) = await PlayAsync();

        // 打出 1 张（离手 −1）＋ 抽 2 张 ＝ 净 +1；重复挂载会是净 +3（抽 4 张）。
        Assert.Equal(before - 1 + 2, after);
        Assert.NotEqual(before - 1 + 4, after);
    }

    // ── 对局夹具 ────────────────────────────────────────────────────────────

    private async Task<(int Before, int After)> PlayAsync()
    {
        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(CommandTestKit.OrderCardId, new[] { ProductId });

        var match = new Match(
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            CommandTestKit.CreateDefinitions(),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true },
            effectRegistry: registry,
            targeterBridge: CommandTestKit.AutoPickBridge(),
            packageActivator: TestHost.Activator);

        match.Engine.ScriptEvaluator ??= new CSharpScriptEvaluator();
        match.Engine.SyncScriptEvaluator ??= new CSharpSyncScriptEvaluator();

        var loaded = AssetPackLoader.Load(
            match.Engine.Model!,
            Path.Combine(_pack, "effects-v4"),
            match.Engine.ScriptEvaluator,
            match.Engine.SyncScriptEvaluator,
            handlerDsl: match.Engine.HandlerDsl);
        Assert.True(
            loaded.Loaded,
            "组件装载失败：" + string.Join("；", loaded.Errors.Select(static e => e.Category + "/" + e.Target + ": " + e.Message)));
        Assert.Contains(ProductId, loaded.AssetIds);

        await match.Initialize();

        var player = match.Players[0];
        var command = (OrderCard)match.CardLibrary.Instantiate(CommandTestKit.OrderCardId);
        await command.LoadAsync(player);
        player.Hand.Add(command);
        await match.ResourceManager.AddPointsAsync(player, 5);

        var before = player.Hand.Count;
        var result = await match.PlayManager.PlayOrderAsync(command);
        Assert.Equal(PlayResultStatus.Success, result.Status);

        return (before, player.Hand.Count);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_pack, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ── 资产夹具 ────────────────────────────────────────────────────────────

    private static string Parent()
        => $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "{{ParentId}}",
                "requires-host": "any",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [
                  {
                    "name": "onPlayed",
                    "visibility": [ "callable", "mountable" ],
                    "view": { "fields": [ { "name": "Card", "type": "entity", "writable": false, "required": false } ] },
                    "slot": { "type": "handler" },
                    "handler": {
                      "id": "{{ParentId}}#onPlayed",
                      "entry": "HandleAsync",
                      "script": "{{DrawScript}}"
                    }
                  }
                ],
                "mounts": [ { "target": "engine#card.played", "member": "onPlayed" } ],
                "wraps": []
              }
            }
            """;

    private static string Draft()
        => $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "moding",
                "id": "{{DraftId}}",
                "product": { "id": "{{ProductId}}" },
                "extends": "{{ParentId}}",
                "requires-host": "any",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [
                  {
                    "name": "onPlayed",
                    "visibility": [ "callable", "mountable" ],
                    "view": { "fields": [ { "name": "Card", "type": "entity", "writable": false, "required": false } ] },
                    "handler": {
                      "id": "{{DraftId}}#onPlayed",
                      "entry": "HandleAsync",
                      "script": "{{DrawScript}}"
                    }
                  }
                ],
                "mounts": [],
                "wraps": []
              }
            }
            """;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
