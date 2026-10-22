using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2B 验收③④（部署词条与加入路径）：部署词条效果按注册序触发（A→B→C 可观测）、
/// 无效果＝空转（条件触发语义、部署不因此失败）、单个效果异常隔离（记录并继续、不阻断部署链）、
/// 触发上下文（被部署单位＋目标槽位）；加入路径（不扣费/不走部署词条/共用单位化/仅发 unit.joined）、
/// 加入拒绝（槽位非空/重复单位化）。
/// S6 随改：A4 部署逻辑路径废弃——部署效果改由**效果 + 注入「部署词条触发器」**表达
/// （<see cref="DeployLogicProbeEffect"/> 测试效果经 <c>Effect.Inject</c> 接入；卸载自动撤销）。
/// 原「空 handler 条目」语义随 A4 退场（P8b：该概念不再存在）。
/// </summary>
public class PlayChainDeploymentTests
{
    [Fact]
    public async Task Deploy_Triggers_Injected_Effects_In_Registration_Order()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var line = match.Battlefield.PlayerASupportLine;
        var order = new List<string>();
        var views = new List<CardTriggerView>();

        unit.AddEffect(new DeployLogicProbeEffect(unit, "A", view => { order.Add("A"); views.Add(view); return Task.CompletedTask; }));
        unit.AddEffect(new DeployLogicProbeEffect(unit, "B", view => { order.Add("B"); views.Add(view); return Task.CompletedTask; }));
        unit.AddEffect(new DeployLogicProbeEffect(unit, "C", view => { order.Add("C"); views.Add(view); return Task.CompletedTask; }));

        var result = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps()[1]);

        // 按注入/执行顺序 A→B→C 触发（顺序可观测）；触发上下文＝被部署单位与目标槽位。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { "A", "B", "C" }, order);
        Assert.All(views, view =>
        {
            Assert.Same(unit, view.Card);
            Assert.Same(line.GetInsertionGaps()[1], view.Position);
        });
    }

    [Fact]
    public async Task Deploy_Without_Effects_Still_Unitizes_And_Charges()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var line = match.Battlefield.PlayerASupportLine;

        // 无部署效果＝空转、仍继续单位化与扣费（部署不因此失败）。
        var result = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps()[1]);

        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Same(unit, line.GetInsertionGaps()[1]);
        Assert.Equal(0, player.Points);
    }

    [Fact]
    public async Task Deploy_Isolates_Single_Effect_Exception_And_Continues()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = await PlayChainTestKit.InstantiateLoadedAsync<UnitCard>(match, player);
        var line = match.Battlefield.PlayerASupportLine;
        var order = new List<string>();

        unit.AddEffect(new DeployLogicProbeEffect(unit, "A", _ => { order.Add("A"); return Task.CompletedTask; }));
        unit.AddEffect(new DeployLogicProbeEffect(unit, "B", _ => throw new InvalidOperationException("部署效果爆炸")));
        unit.AddEffect(new DeployLogicProbeEffect(unit, "C", _ => { order.Add("C"); return Task.CompletedTask; }));
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.PlayUnitAsync(unit, line.GetInsertionGaps()[1]);

        // 单个部署效果异常＝沿用引擎异常隔离（记录并继续）：不阻断部署链与后续效果。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Equal(new[] { "A", "C" }, order);
        Assert.Same(unit, line.GetInsertionGaps()[1]);
        Assert.Equal(
            new[]
            {
                GameUpdates.CardPlayed, Updates.CardPlaced, GameUpdates.UnitDeployed, // W3-A3：含放置驱动信号
                GameUpdates.PointChanged, // 收尾扣部署费（E1-25 后续）
            },
            recorder.Types);
        Assert.Contains(
            match.Engine.RootStream.Entries,
            entry => entry.Keywords.Contains("exception:InvalidOperationException"));
    }

    [Fact]
    public async Task Join_Unitizes_Without_Fee_Or_Deployment_Keyword_Effect()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var player = match.Players[0];
        var unit = (UnitCard)match.CardLibrary.Instantiate(PlayChainTestKit.UnitCheapId); // 不加载（来源不问、归属不要求）
        var front = match.Battlefield.FrontLine.GetInsertionGaps()[0];
        var fired = new List<string>();
        unit.AddEffect(new DeployLogicProbeEffect(unit, "部署词条", _ => { fired.Add("部署词条"); return Task.CompletedTask; }));
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.JoinUnitAsync(unit, front);

        // 加入路径：加入触发器 → 共用单位化；不扣费、不走部署词条；发 card.placed ×1（W3-A3：放置驱动信号）＋ unit.joined ×1（不发 card.played / unit.deployed）。
        Assert.Equal(PlayResultStatus.Success, result.Status);
        Assert.Same(unit, front);
        Assert.Equal(1, unit.GetData<UnitStateData>().OperateCost); // 共用单位化：组件挂载（实时值＝对战组件值）
        Assert.Equal(1, player.Points); // 不扣费
        Assert.Empty(fired); // 不走部署词条
        Assert.Equal(new[] { Updates.CardPlaced, GameUpdates.UnitJoined }, recorder.Types);
        var payload = recorder.Updates[1].Payload!;
        Assert.Same(unit, payload[GameUpdates.PayloadUnit]);
        Assert.Same(front, payload[GameUpdates.PayloadPosition]);
    }

    [Fact]
    public async Task Join_Rejects_Occupied_Slot()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var unit = (UnitCard)match.CardLibrary.Instantiate(PlayChainTestKit.UnitCheapId);
        var occupied = match.Battlefield.FrontLine.GetInsertionGaps()[1];
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.PlayManager.JoinUnitAsync(unit, occupied);

        // 非空＝拒绝、可观测；零副作用。
        Assert.Equal(PlayResultStatus.Failed, result.Status);
        Assert.Equal(PlayFailureReason.TargetSlotOccupied, result.FailureReason);
        Assert.Empty(recorder.Updates);
        Assert.False(unit.TryGetData<UnitStateData>(out _));
    }

    [Fact]
    public async Task Join_Rejects_Already_Unitized()
    {
        var match = PlayChainTestKit.CreatePlayMatch();
        await match.Initialize();
        var unit = (UnitCard)match.CardLibrary.Instantiate(PlayChainTestKit.UnitCheapId);
        var front = match.Battlefield.FrontLine;

        Assert.Equal(PlayResultStatus.Success, (await match.PlayManager.JoinUnitAsync(unit, front.GetInsertionGaps()[0])).Status);
        using var recorder = new UpdateRecorder(match.Engine);
        var again = await match.PlayManager.JoinUnitAsync(unit, front.GetInsertionGaps()[1]);

        Assert.Equal(PlayResultStatus.Failed, again.Status);
        Assert.Equal(PlayFailureReason.UnitAlreadyUnitized, again.FailureReason);
        Assert.True(front[1].IsEmpty());
        Assert.Empty(recorder.Updates);
    }
}

/// <summary>
/// 测试用部署效果（S6 迁移）：装载时（<c>OnMount</c>）经 <c>Effect.Inject</c> 把 handler 注入宿主单位的
/// 「部署词条触发器」默认区段；卸载时框架自动撤销注入——即数据体/预制体路径（<c>inject</c> 声明）的代码等价形态。
/// </summary>
internal sealed class DeployLogicProbeEffect : PassiveEffect
{
    private readonly UnitCard _unit;
    private readonly Func<CardTriggerView, Task> _handler;

    internal DeployLogicProbeEffect(UnitCard unit, string entryName, Func<CardTriggerView, Task> handler)
        : base($"部署探针:{entryName}")
    {
        _unit = unit;
        _handler = handler;
    }

    protected override void OnMount()
        => Inject(
            _unit.DeployKeywordTrigger,
            Name,
            DefaultBands.Default,
            (view, _, _) => _handler(view));
}
