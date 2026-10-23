using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Data;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 批 4（守护词条化）验收：①数据体路径生效（`guard` 映射 → 「守护」词条 → 守护维护——相邻/HQ 被守护、
/// 守护者自身不受、资格链随动）；②IsGuard 代码通道并行（既有语义零变化）；③升级边界（升级获得 guard→维护重算；
/// 升级失去 guard→维护撤销）；词条授予-移除-复装随动维护（词条链完成点驱动——动作完成即重算）；
/// 打标生效（对战词条池含 guard）。
/// 数据构造＝等价数据（内嵌数据体 JSON；经 <see cref="CardDataLoader"/> 真实加载路径——不依赖 outputs 外部产物）。
/// </summary>
public class GuardKeywordTests
{
    // ---------- 卡 id（自含数据） ----------

    private const string GuarderId = "gk_guarder";             // 数据体 guard 卡（守护者；0/6）
    private const string VetGainBaseId = "gk_vet_gain_base";   // 升级获得方向：基础（无 guard）
    private const string VetGainVetId = "gk_vet_gain_vet";     // 升级获得方向：老兵（guard）
    private const string VetLossBaseId = "gk_vet_loss_base";   // 升级失去方向：基础（guard）
    private const string VetLossVetId = "gk_vet_loss_vet";     // 升级失去方向：老兵（无 guard）

    /// <summary>自含数据体 JSON 集 → 真实加载路径 → 定义集（每测例独立临时目录；加载后即删除——定义已构造）。</summary>
    private static IReadOnlyList<CardDefinitionEntry> LoadGuardDefinitions()
    {
        var jsons = new[]
        {
            CardJson(GuarderId, "守护测试兵", "Soviet", 1, 1, 0, 6, "infantry", "guard"),
            CardJson(VetGainBaseId, "升级获得·基础", "Germany", 1, 1, 1, 2, "infantry",
                $"BecomesVeteran:{VetGainVetId}"),
            CardJson(VetGainVetId, "升级获得·老兵", "Germany", 1, 1, 2, 2, "infantry",
                $"VeteranOf:{VetGainBaseId}", "guard"),
            CardJson(VetLossBaseId, "升级失去·基础", "Germany", 1, 1, 1, 2, "infantry",
                $"BecomesVeteran:{VetLossVetId}", "guard"),
            CardJson(VetLossVetId, "升级失去·老兵", "Germany", 1, 1, 2, 2, "infantry",
                $"VeteranOf:{VetLossBaseId}"),
        };

        var directory = Path.Combine(Path.GetTempPath(), "orc-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var i = 0; i < jsons.Length; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"card{i:D2}.card.json"), jsons[i]);
            }

            var loaded = CardDataLoader.LoadDirectory(directory);
            Assert.Empty(loaded.Failures);
            return loaded.Definitions;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CardJson(
        string id, string name, string faction, int kredits, int operationCost, int attack, int defense,
        string unitType, params string[] attributes)
    {
        var attributeList = attributes.Length == 0
            ? "[]"
            : "[" + string.Join(", ", attributes.Select(a => $"\"{a}\"")) + "]";
        return $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "name": "{{name}}",
          "components": [
            { "component": "typeCategory", "type": ["{{unitType}}"] },
            { "component": "factionCost", "faction": "{{faction}}", "kredits": {{kredits}} },
            { "component": "battleStats", "operationCost": {{operationCost}}, "attack": {{attack}}, "defense": {{defense}} },
            { "component": "tagData", "rarity": "Standard" },
            { "component": "keywords", "attributes": {{attributeList}} }
          ]
        }
        """;
    }

    private static Match CreateMatch()
        => CommandTestKit.CreateCommandMatch(extraDefinitions: LoadGuardDefinitions());

    // ---------- ①数据体路径生效（guard 卡：相邻/HQ 被守护、自身不受、资格链随动） ----------

    [Fact]
    public async Task Guard_DataBody_Protects_Adjacent_And_Hq_Never_Self()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 打标生效：guard ∈ 对战词条池（wiki『属于对战词条』；具体清单以注册面打标为准）。
        Assert.True(KeywordRegistry.IsBattleKeyword(KeywordIds.Guard));

        // B 支援线：步兵（槽 0）＋守护者（槽 1——紧邻步兵，且与 HQ 相邻）——落位后＝[步兵, 守护者, HQ]。
        var guarded = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 0);
        var guardian = await CommandTestKit.PrepareOnSupportAsync(match, playerB, GuarderId, 1);
        var infantry = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.InfantryId, 0);
        var artillery = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.ArtilleryId, 1);
        CommandTestKit.Activate(infantry);
        CommandTestKit.Activate(artillery);

        // 词条面：数据体 `guard` 映射 → 「守护」词条（IsGuard 代码通道恒 false——数据体路径）。
        Assert.True(guardian.Keywords.Has(KeywordIds.Guard));
        Assert.True(KeywordRules.HasKeyword(guardian, KeywordIds.Guard));
        Assert.False(guardian.Definition.IsGuard);

        // 相邻非守护单位被守护；HQ 被守护；守护者自身不受。
        Assert.True(match.CommandManager.IsUnitGuarded(guarded));
        Assert.True(match.CommandManager.IsHqGuarded(playerB));
        Assert.False(match.CommandManager.IsUnitGuarded(guardian));

        // 资格链随动（查询面读维护产物）：步/坦筛除被守护者与 HQ；炮/轰可通过；守护者自身可被任意合法攻击者攻击。
        var infantryReport = match.CommandManager.GetCommandAvailability(infantry);
        Assert.DoesNotContain(guarded.Ref, infantryReport.Attack.Candidates);
        Assert.DoesNotContain(playerB.Hq.Ref, infantryReport.Attack.Candidates);
        Assert.Contains(guardian.Ref, infantryReport.Attack.Candidates);

        var artilleryReport = match.CommandManager.GetCommandAvailability(artillery);
        Assert.Contains(guarded.Ref, artilleryReport.Attack.Candidates);
        Assert.Contains(playerB.Hq.Ref, artilleryReport.Attack.Candidates);
    }

    // ---------- 词条授予-移除-复装随动维护（词条链完成点驱动） ----------

    [Fact]
    public async Task Guard_Grant_Revoke_Regrant_Drives_Maintenance()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        // B 支援线：[HQ, 待授予者, 邻位步兵]——授予者与 HQ、邻位步兵均相邻（有序列表：相邻＝位置序号连续）。
        var grantee = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var neighbor = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 2);

        // 初始：非守护者——无守护来源。
        Assert.False(match.CommandManager.IsUnitGuarded(neighbor));
        Assert.False(match.CommandManager.IsHqGuarded(playerB));

        // 授予（运行期）：动作完成即随动——邻位与 HQ 获被守护；授予者自身不受。
        Assert.True(await grantee.Keywords.GrantAsync(KeywordIds.Guard));
        Assert.True(match.CommandManager.IsUnitGuarded(neighbor));
        Assert.True(match.CommandManager.IsHqGuarded(playerB));
        Assert.False(match.CommandManager.IsUnitGuarded(grantee));

        // 移除：动作完成即随动——撤销。
        Assert.True(await grantee.Keywords.RevokeAsync(KeywordIds.Guard));
        Assert.False(match.CommandManager.IsUnitGuarded(neighbor));
        Assert.False(match.CommandManager.IsHqGuarded(playerB));

        // 复装：再次授予——恢复（同一随动链）。
        Assert.True(await grantee.Keywords.GrantAsync(KeywordIds.Guard));
        Assert.True(match.CommandManager.IsUnitGuarded(neighbor));
        Assert.True(match.CommandManager.IsHqGuarded(playerB));

        // 相邻另一守护者（词条来源）：守护者自身跳过语义保持——两者均不获被守护。
        Assert.True(await neighbor.Keywords.GrantAsync(KeywordIds.Guard));
        Assert.False(match.CommandManager.IsUnitGuarded(grantee));
        Assert.False(match.CommandManager.IsUnitGuarded(neighbor));
        Assert.True(match.CommandManager.IsHqGuarded(playerB)); // 多守护源不叠加＝存在性判定（仍成立）
    }

    // ---------- ③升级边界：升级获得 guard → 维护重算 ----------

    [Fact]
    public async Task Guard_Upgrade_Gain_Recomputes_Maintenance()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        // B 前线：升级对象（槽 0）＋相邻步兵（槽 1）。
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerB, VetGainBaseId, 0);
        var friend = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);

        // 升级前：基础形态无守护（无词条、无来源）。
        Assert.False(unit.Keywords.Has(KeywordIds.Guard));
        Assert.False(match.CommandManager.IsUnitGuarded(friend));

        // 升级（经运行期门面驱动）：词条集替换获得「守护」——相邻即被守护（维护重算）。
        var runtime = EffectRuntime.ResolveFor(unit);
        Assert.NotNull(runtime);
        var result = await runtime!.UpgradeAsync(unit);

        Assert.Equal(VeteranPromotionOutcome.Promoted, result.Outcome);
        Assert.True(VeteranRules.IsVeteran(unit));
        Assert.True(unit.Keywords.Has(KeywordIds.Guard));
        Assert.True(match.CommandManager.IsUnitGuarded(friend)); // 升级获得 guard → 维护重算（相邻即被守护）
    }

    // ---------- ③升级边界：升级失去 guard → 维护撤销 ----------

    [Fact]
    public async Task Guard_Upgrade_Loss_Revokes_Maintenance()
    {
        var match = CreateMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        // B 前线：升级对象（槽 0——基础形态含守护词条）＋相邻步兵（槽 1）。
        var unit = await CommandTestKit.PrepareOnFrontAsync(match, playerB, VetLossBaseId, 0);
        var friend = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);

        // 升级前：基础形态守护生效（数据体词条路径）。
        Assert.True(unit.Keywords.Has(KeywordIds.Guard));
        Assert.True(match.CommandManager.IsUnitGuarded(friend));

        // 升级（经运行期门面驱动）：词条集替换失去「守护」——维护撤销（原相邻者不再被守护）。
        var runtime = EffectRuntime.ResolveFor(unit);
        Assert.NotNull(runtime);
        var result = await runtime!.UpgradeAsync(unit);

        Assert.Equal(VeteranPromotionOutcome.Promoted, result.Outcome);
        Assert.True(VeteranRules.IsVeteran(unit));
        Assert.False(unit.Keywords.Has(KeywordIds.Guard));
        Assert.False(match.CommandManager.IsUnitGuarded(friend)); // 升级失去 guard → 维护撤销
    }

    // ---------- ②IsGuard 代码通道并行（既有语义零变化） ----------

    [Fact]
    public async Task Guard_IsGuard_Code_Channel_Compatibility()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        // 既有测试卡（isGuard: true——代码通道）行为不变：守护生效、自身不受、词条面无需承载。
        var guardian = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.GuardianId, 0);
        var guarded = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.InfantryId, 1);

        Assert.True(guardian.Definition.IsGuard);
        Assert.False(guardian.Keywords.Has(KeywordIds.Guard)); // 代码通道卡不经词条（通道各表、判定等价）
        Assert.True(match.CommandManager.IsUnitGuarded(guarded));
        Assert.False(match.CommandManager.IsUnitGuarded(guardian));
    }
}
