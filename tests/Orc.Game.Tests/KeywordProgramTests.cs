using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Effects;
using Xunit;

namespace Orc.Game.Tests;

public sealed class KeywordProgramTests
{
    private static readonly List<string> Registered=[];
    private static async Task<(Match Match,UnitCard Unit,string Keyword)> Create(KeywordProgram program,int? value=null,MockTargeterBridge? bridge=null)
    {
        var id="program."+Guid.NewGuid().ToString("N");ProgramKeywordComponent.Register(id,program);Registered.Add(id);
        var match=CommandTestKit.CreateCommandMatch(bridge,extraDefinitions:[new CardDefinitionEntry(id,new CardDefinition("程序单位",1,0,2,5,unitTypes:[UnitType.Infantry],keywords:[new KeywordDeclaration(id,value)],faction:Faction.Germany,rarity:Rarity.Standard))]);
        await match.Initialize();var unit=await CommandTestKit.InstantiateLoadedAsync(match,match.Players[0],id,toHand:true);return(match,unit,id);
    }
    private static KeywordProgramAction A(string op,string value="0",string key="",string target="self")=>new(){Op=op,Value=value,Key=key,Target=target};
    [Fact]
    public async Task RetreatUsesNormalDragCostsAndMovementOpportunity()
    {
        var bridge = new MockTargeterBridge();
        var (m,u,k) = await Create(new() { Rules = [new() { Rule="move.retreat", Value="1" }] }, bridge:bridge);
        await m.PlayManager.JoinUnitAsync(u,m.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(u); bridge.CollectScript=CommandTestKit.AllRefsScript(m);
        var support=m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0);
        var points=m.Players[0].Points;
        Assert.Contains(support.Ref,m.CommandManager.GetCommandAvailability(u).Move.Candidates);
        await CommandTestKit.CancelCommandAsync(m,bridge,u);
        Assert.Equal(points,m.Players[0].Points); Assert.True(u.GetData<CommandData>().CanMove);
        Assert.True((await CommandTestKit.RunCommandAsync(m,bridge,u,support.Ref)).IsSuccess);
        Assert.Same(m.Battlefield.PlayerASupportLine,u.GetData<UnitStateData>().Line);
        Assert.Equal(points-u.Modifiers.GetEffectiveValue(CardStatFields.OperateCost),m.Players[0].Points);
        Assert.False(u.GetData<CommandData>().CanMove); Assert.False(u.GetData<CommandData>().CanAttack);
        Assert.True(u.Keywords.Has(k));
    }
    [Fact]
    public async Task RetreatPermissionIsRecheckedAfterSelectionAndOrdinaryUnitsCannotRetreat()
    {
        var bridge=new MockTargeterBridge();
        var (m,u,k)=await Create(new(){Rules=[new(){Rule="move.retreat",Value="1"}]},bridge:bridge);
        await m.PlayManager.JoinUnitAsync(u,m.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(u); bridge.CollectScript=CommandTestKit.AllRefsScript(m);
        var support=m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0);var points=m.Players[0].Points;
        var task=m.CommandManager.BeginMoveAsync(u);var request=await bridge.WaitForNextBeginAsync();
        await u.Keywords.RevokeAsync(k);
        request.Responder.Complete(request.Description.RequestId,TargeterTestKit.Selection(TargetSlot.DefaultName,support.Ref));
        Assert.False((await task).IsSuccess);Assert.Same(m.Battlefield.FrontLine,u.GetData<UnitStateData>().Line);
        Assert.Equal(points,m.Players[0].Points);Assert.False(m.CommandManager.GetCommandAvailability(u).Move.CanUse);
    }
    [Fact]
    public async Task SecondNormalAttackConsumesKeywordAndCannotAttackAThirdTime()
    {
        var bridge=new MockTargeterBridge();
        var (m,u,k)=await Create(new(){Rules=[new(){Rule="attack.limit",Value="2"}],Handlers=[new(){Event="unit.attack.after",Subject="self",
            When=[new("attacks","gte","2")],Actions=[new(){Op="keyword.remove",Target="self"}]}]},bridge:bridge);
        await m.PlayManager.JoinUnitAsync(u,m.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(u);bridge.CollectScript=CommandTestKit.AllRefsScript(m);
        var hq=m.Players[1].Hq;var points=m.Players[0].Points;
        await CommandTestKit.CancelCommandAsync(m,bridge,u);
        Assert.Equal(0,Assert.Single(KeywordProgramRuntime.Components(u)).Attacks);
        Assert.True((await CommandTestKit.RunCommandAsync(m,bridge,u,hq.Ref)).IsSuccess);
        Assert.True(u.Keywords.Has(k));Assert.True(m.CommandManager.GetCommandAvailability(u).Attack.CanUse);
        Assert.True((await CommandTestKit.RunCommandAsync(m,bridge,u,hq.Ref)).IsSuccess);
        Assert.False(u.Keywords.Has(k));Assert.False(m.CommandManager.GetCommandAvailability(u).Attack.CanUse);
        Assert.Equal(points-2*u.Modifiers.GetEffectiveValue(CardStatFields.OperateCost),m.Players[0].Points);
        Assert.False((await m.CommandManager.BeginAttackAsync(u)).IsSuccess);
    }
    [Fact]
    public async Task IndependentBlitzAndMoveOnlyProgramsChangeActualDeploymentFlags()
    {
        foreach(var attack in new[]{false,true})
        {
            var (m,u,_)=await Create(new(){Handlers=[new(){Actions=[A("move.set","1"),A("attack.set",attack?"1":"0")]}]});
            var result=await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));Assert.Equal(PlayResultStatus.Success,result.Status);
            Assert.True(u.GetData<CommandData>().CanMove);Assert.Equal(attack,u.GetData<CommandData>().CanAttack);
        }
    }
    [Fact]
    public async Task ChargeReadyPersistsUntilConsumedThenStartsAnotherCycle()
    {
        var (m,u,k)=await Create(new(){State=new(){{"ticks",0},{"ready",0}},Handlers=[
            new(){Event="turn.start",Subject="owner",When=[new("state.ready","eq","0")],Actions=[A("state.add","1","ticks"),new(){Op="if",When=[new("state.ticks","gte","value")],Then=[A("state.set","1","ready"),A("emit",key:"keyword.ready")]}]},
            new(){Event="unit.attack.before",When=[new("state.ready","eq","1")],Actions=[new(){Op="buff",Value="3",Temporary=true}]},
            new(){Event="unit.attack.after",When=[new("state.ready","eq","1")],Actions=[A("state.set","0","ready"),A("state.set","0","ticks"),A("emit",key:"keyword.consumed")]}
        ]},3);
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));var component=Assert.Single(KeywordProgramRuntime.Components(u));
        for(var i=0;i<3;i++)await m.Engine.Emit("turn.start",new Dictionary<string,object?>{{"Player",m.Players[0]}});
        Assert.Equal(1,component.State["ready"]);Assert.Equal(3,component.State["ticks"]);
        await m.Engine.Emit("turn.start",new Dictionary<string,object?>{{"Player",m.Players[0]}});Assert.Equal(3,component.State["ticks"]);
        var enemy=await CommandTestKit.PrepareOnFrontAsync(m,m.Players[1],CommandTestKit.BeastId,0);
        CommandTestKit.Activate(u);var command=await m.CommandManager.BeginAttackAsync(u);
        // No scripted choice is supplied: cancellation must not consume readiness.
        Assert.Equal(1,component.State["ready"]);
    }
    [Fact]
    public async Task RuleDenialComposesWithRangeAndRevokeRestoresAvailability()
    {
        var (m,u,k)=await Create(new(){Rules=[new(){Rule="move.allowed",Value="0"},new(){Rule="combat.range",Value="1"}]});
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));CommandTestKit.Activate(u);
        var e=await CommandTestKit.PrepareOnSupportAsync(m,m.Players[1],CommandTestKit.InfantryId,1);
        var availability=m.CommandManager.GetCommandAvailability(u);Assert.False(availability.Move.CanUse);Assert.Contains(e.Ref,availability.Attack.Candidates);
        await u.Keywords.RevokeAsync(k);Assert.True(m.CommandManager.GetCommandAvailability(u).Move.CanUse);
    }
    [Fact]
    public async Task FrontlineDeploymentUsesSameNativeCandidatesAndPortal()
    {
        var (m,u,_)=await Create(new(){Rules=[new(){Rule="deploy.frontline",Value="1"}]});
        var gap=m.PlayManager.GetDeploymentGaps(u).First(g=>ReferenceEquals(g.Line,m.Battlefield.FrontLine));
        Assert.Equal(PlayResultStatus.Success,(await m.PlayManager.PlayUnitAsync(u,gap)).Status);
        Assert.Contains(m.Battlefield.FrontLine.Members,s=>ReferenceEquals(s,u));
    }
    [Fact]
    public async Task PreventDeathOnceRetainsOneDefenseAndLaterDeathStillExecutes()
    {
        var (m,u,_)=await Create(new(){State=new(){{"used",0}},Handlers=[new(){Event="unit.destroy.before",When=[new("state.used","eq","0")],Actions=[A("state.set","1","used"),A("death.cancel"),A("health.set","1")]}]});
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        await EffectRuntime.ResolveFor(u)!.DamageAsync(u,100);
        Assert.False(u.GetData<UnitStateData>().IsDestroyed);Assert.Equal(1,u.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        await EffectRuntime.ResolveFor(u)!.DamageAsync(u,1);Assert.True(u.GetData<UnitStateData>().IsDestroyed);
    }
    [Fact]
    public async Task DeathRewriteSlotIsConsultedBeforeTheKeywordProgramAndCanCancel()
    {
        // S8：旧的两个并列取消口（DeathInterceptor 属性 / 词条 death.cancel）收敛为死亡处理端 Rewrite 段的 handler；
        // 次序＝注册面（priority 0）先、内置词条 handler（priority 100）后——任一取消即终止，且都在置毁之前。
        var (m, u, _) = await Create(new()
        {
            State = new() { { "consulted", 0 } },
            Handlers = [new() { Event = "unit.destroy.before", Actions = [A("state.add", "1", "consulted")] }],
        });
        await m.PlayManager.PlayUnitAsync(u, m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        var calls = 0;
        m.CommandManager.DeathAction.Handle.Register(
            "测试拦截器",
            (view, ctx, _) =>
            {
                calls++;
                view.ShouldDie = false;
                ctx.Stop();
                return Task.CompletedTask;
            },
            ActionHandleBands.Rewrite);

        await EffectRuntime.ResolveFor(u)!.DamageAsync(u, 100);

        Assert.Equal(1, calls);
        Assert.False(u.GetData<UnitStateData>().IsDestroyed);
        Assert.Same(m.Battlefield.PlayerASupportLine, u.GetData<UnitStateData>().Line);
        // 拦截器先于内置词条 handler：取消后词条程序全程未被咨询（「任一取消即终止」次序保持）。
        Assert.Equal(0, Assert.Single(KeywordProgramRuntime.Components(u)).State["consulted"]);

        // 无拦截时的正常死亡路径由 PreventDeathOnceRetainsOneDefenseAndLaterDeathStillExecutes 覆盖
        // （该用例验证 death.cancel 后再次致死仍会消灭）。
    }

    [Fact]
    public async Task EffectDisplacementUsesThePortalWithoutPayingOrConsumingActions()
    {
        var (m, u, _) = await Create(new());
        await m.PlayManager.JoinUnitAsync(u, m.Battlefield.FrontLine.GetInsertionGaps().At(0));
        CommandTestKit.Activate(u);
        var support = m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(0);
        var points = m.Players[0].Points;

        // S6/R16：压制只影响「能否被指挥」，不阻断效果移动。
        Assert.True(await SuppressRules.ApplyAsync(u));

        Assert.True(await m.CommandManager.MoveUnitAsync(u, support));

        Assert.Same(m.Battlefield.PlayerASupportLine, u.GetData<UnitStateData>().Line);
        Assert.Equal(points, m.Players[0].Points);
        Assert.True(u.GetData<CommandData>().CanMove);
        Assert.False(await m.CommandManager.MoveUnitAsync(u, m.Battlefield.FrontLine.GetInsertionGaps().At(0)));
    }

    [Fact]
    public async Task RecordValueIsBoundToEachCreatedCardRatherThanGlobalOrSourceReference()
    {
        var (m,u,_)=await Create(new(){Handlers=[new(){Actions=[A("capture","target.attack","recordedAttack"),new(){Op="create.hand",Key=CommandTestKit.InfantryId,Bindings=new(){{"damage","data.recordedAttack"}}}]}]});
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        var first=m.Players[0].Hand.Single(c=>c.TryGetData<KeywordProgramData>(out _));Assert.Equal(2,first.GetData<KeywordProgramData>().Values["damage"]);
        await EffectRuntime.ResolveFor(u)!.BuffAsync(u,3,0);
        Assert.Equal(2,first.GetData<KeywordProgramData>().Values["damage"]);
        var (other,v,_)=await Create(new(){Handlers=[new(){Actions=[new(){Op="create.hand",Key=CommandTestKit.InfantryId,Bindings=new(){{"damage","7"}}}]}]});
        await other.PlayManager.PlayUnitAsync(v,other.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));Assert.Equal(2,first.GetData<KeywordProgramData>().Values["damage"]);
        Assert.Equal(7,other.Players[0].Hand.Single(c=>c.TryGetData<KeywordProgramData>(out _)).GetData<KeywordProgramData>().Values["damage"]);
    }
    [Fact]
    public void UnknownDeclarationsAreRejectedBeforeTheyReachAMatch()
    {
        Assert.Throws<InvalidDataException>(()=>new KeywordProgram{Rules=[new(){Rule="unknown"}]}.Validate());
        Assert.Throws<InvalidDataException>(()=>new KeywordProgram{Handlers=[new(){Actions=[A("state.set","1","missing")]}]}.Validate());
        Assert.Throws<InvalidDataException>(()=>KeywordProgram.Parse("{\"schema\":1,\"typo\":true}"));
    }
    [Fact]
    public async Task AttackBuffIsConsumedOnlyByActualCombatAndRemovedAfterwards()
    {
        var bridge=new MockTargeterBridge();
        var (m,u,_)=await Create(new(){State=new(){{"ready",1}},Handlers=[
            new(){Event="unit.attack.before",When=[new("state.ready","eq","1")],Actions=[new(){Op="buff",Value="3",Temporary=true}]},
            new(){Event="unit.attack.after",Actions=[A("state.set","0","ready")]}
        ]},bridge:bridge);
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));var enemy=await CommandTestKit.PrepareOnFrontAsync(m,m.Players[1],CommandTestKit.InfantryId,0);
        bridge.CollectScript=CommandTestKit.AllRefsScript(m);CommandTestKit.Activate(u);
        await CommandTestKit.CancelCommandAsync(m,bridge,u);Assert.Equal(1,Assert.Single(KeywordProgramRuntime.Components(u)).State["ready"]);
        var result=await CommandTestKit.RunCommandAsync(m,bridge,u,enemy.Ref);Assert.True(result.IsSuccess);
        Assert.True(enemy.GetData<UnitStateData>().IsDestroyed);Assert.Equal(2,u.Modifiers.GetEffectiveValue(CardStatFields.Attack));Assert.Equal(0,Assert.Single(KeywordProgramRuntime.Components(u)).State["ready"]);
    }
    [Fact]
    public async Task SelectedUnitIsCapturedBeforeDeploymentAndCancellationCostsNothing()
    {
        var bridge=new MockTargeterBridge();var (m,u,_)=await Create(new(){Handlers=[new(){Actions=[A("capture","target.attack","loaded","choose.unit")]}]},bridge:bridge);
        var enemy=await CommandTestKit.PrepareOnFrontAsync(m,m.Players[1],CommandTestKit.BeastId,0);bridge.CollectScript=CommandTestKit.AllRefsScript(m);
        var points=m.Players[0].Points;var play=m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));var pending=await bridge.WaitForNextBeginAsync();pending.Responder.Cancel(pending.Description.RequestId);await play;
        Assert.Equal(points,m.Players[0].Points);Assert.Contains(u,m.Players[0].Hand);Assert.False(u.TryGetData<UnitStateData>(out _));
        play=m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));pending=await bridge.WaitForNextBeginAsync();pending.Responder.Complete(pending.Description.RequestId,TargeterTestKit.Selection(TargetSlot.DefaultName,enemy.Ref));
        Assert.Equal(PlayResultStatus.Success,(await play).Status);Assert.Equal(6,u.GetData<KeywordProgramData>().Values["loaded"]);
    }
    [Fact]
    public async Task TemporaryCopyHasNoSourceKeywordAndOnlyItsHandInstanceExpires()
    {
        var (m,u,k)=await Create(new(){Handlers=[new(){Actions=[new(){Op="copy.hand",Temporary=true}]}]});
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));var copy=Assert.Single(m.Players[0].Hand.OfType<CardBase>(),c=>c.Definition==u.Definition);
        Assert.False(copy.Keywords.Has(k));await m.EndTurn();Assert.DoesNotContain(copy,m.Players[0].Hand);Assert.False(u.GetData<UnitStateData>().IsDestroyed);
    }
    [Fact]
    public async Task ActiveSkillChecksAndPaysPointsAndAttackOpportunityOnce()
    {
        var (m,u,_)=await Create(new(){Skills=[new(){Id="test",Name="强化",Cost="1",SpendAttack=true,Actions=[A("buff","2")]}]});
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));CommandTestKit.Activate(u);var component=Assert.Single(KeywordProgramRuntime.Components(u));
        Assert.False(component.CanUse("test"));await EffectRuntime.ResolveFor(u)!.GainPointsAsync(u,2);Assert.True(component.CanUse("test"));
        Assert.True(await component.UseAsync("test"));Assert.Equal(1,m.Players[0].Points);Assert.False(await component.UseAsync("test"));Assert.Equal(4,u.Modifiers.GetEffectiveValue(CardStatFields.Attack));
    }

    // ────────────── 机制示范包词条专项（docs/CUSTOM_KEYWORD_SYSTEM.md 验收清单的落地口径） ──────────────

    private static async Task<(Match Match,UnitCard Unit,string Keyword,CardDefinitionEntry Entry)> CreateWithExtra(KeywordProgram program,CardDefinitionEntry extra,MockTargeterBridge? bridge=null)
    {
        var id="program."+Guid.NewGuid().ToString("N");ProgramKeywordComponent.Register(id,program);Registered.Add(id);
        var entry=new CardDefinitionEntry(id,new CardDefinition("程序单位",1,0,2,5,unitTypes:[UnitType.Infantry],keywords:[new KeywordDeclaration(id)],faction:Faction.Germany,rarity:Rarity.Standard));
        var match=CommandTestKit.CreateCommandMatch(bridge,extraDefinitions:[entry,extra]);
        await match.Initialize();var unit=await CommandTestKit.InstantiateLoadedAsync(match,match.Players[0],id,toHand:true);return(match,unit,id,entry);
    }
    private static CardDefinitionEntry UnitDef(string id,string name,int cost,int attack,int defense)=>new(id,new CardDefinition(name,deployCost:cost,operateCost:1,attack:attack,defense:defense,unitTypes:[UnitType.Infantry],faction:Faction.Germany,rarity:Rarity.Standard));
    private static CardDefinitionEntry OrderDef(string id,string name,int cost,KeywordDeclaration keyword)=>new(id,new CardDefinition(name,deployCost:cost,operateCost:0,attack:0,defense:0,CardCategory.Command,keywords:[keyword],faction:Faction.Germany,rarity:Rarity.Standard));

    [Fact]
    public async Task JiDongSkillRestoresActionOnceAndRemovesItself()
    {
        var (m,u,k)=await Create(new(){Skills=[new(){Id="extra",Name="额外行动",Cost="0",SpendMove=false,SpendAttack=false,
            Actions=[A("move.set","1"),A("attack.set","1"),new(){Op="keyword.remove",Target="self",Key=""}]}]});
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));CommandTestKit.Activate(u,false,false);
        var c=Assert.Single(KeywordProgramRuntime.Components(u));Assert.True(await c.UseAsync("extra"));
        Assert.True(u.GetData<CommandData>().CanMove);Assert.True(u.GetData<CommandData>().CanAttack);
        Assert.False(u.Keywords.Has(k));Assert.False(await c.UseAsync("extra"));
    }
    [Fact]
    public async Task BuQuCanclesFirstDeathThenDiesAtOwnerTurnStart()
    {
        var (m,u,_)=await Create(new(){State=new(){{"owed",0}},Handlers=[
            new(){Event="unit.destroy.before",Subject="self",When=[new("state.owed","eq","0")],Actions=[A("state.set","1","owed"),new(){Op="death.cancel",Target="self"},new(){Op="health.set",Target="self",Value="1"}]},
            new(){Event="turn.start",Subject="owner",When=[new("state.owed","eq","1")],Actions=[new(){Op="kill",Target="self"}]}]});
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        await EffectRuntime.ResolveFor(u)!.DamageAsync(u,100);
        Assert.False(u.GetData<UnitStateData>().IsDestroyed);Assert.Equal(1,u.Modifiers.GetEffectiveValue(CardStatFields.Defense));
        await m.Engine.Emit("turn.start",new Dictionary<string,object?>{{"Player",m.Players[0]}});
        Assert.True(u.GetData<UnitStateData>().IsDestroyed);
    }
    [Fact]
    public async Task ZhanlveTransformsIntoRoutedArmyInsteadOfDying()
    {
        const string routed="u_routed_test";
        var (m,u,_,__)=await CreateWithExtra(new(){Handlers=[new(){Event="unit.destroy.before",Subject="self",Actions=[
            new(){Op="death.cancel",Target="self"},new(){Op="replace",Target="self",Key=routed}]}]},UnitDef(routed,"溃军",0,1,1));
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        await EffectRuntime.ResolveFor(u)!.DamageAsync(u,100);
        var members=m.Battlefield.PlayerASupportLine.Members.OfType<CardBase>().ToList();
        var routedCard=members.SingleOrDefault(c=>m.CardLibrary.TryGetRegisteredId(c.Definition,out var rid)&&rid==routed);
        Assert.True(routedCard is not null,$"支援线成员：{string.Join("、",members.Select(c=>c.Definition.Name))}");
    }
    [Fact]
    public async Task HuShiConvertsSurvivingAttackedEnemyIntoRoutedArmy()
    {
        const string routed="u_routed_test";
        var bridge=new MockTargeterBridge();
        var (m,u,_,__)=await CreateWithExtra(new(){Handlers=[new(){Event="unit.attack.after",Subject="self",Actions=[
            new(){Op="replace",Target="attack.target",Key=routed}]}]},UnitDef(routed,"溃军",0,1,1),bridge);
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        var enemy=await CommandTestKit.PrepareOnFrontAsync(m,m.Players[1],CommandTestKit.InfantryId,0);
        await EffectRuntime.ResolveFor(enemy)!.BuffAsync(enemy,0,5);
        CommandTestKit.Activate(u);bridge.CollectScript=CommandTestKit.AllRefsScript(m);
        await CommandTestKit.RunCommandAsync(m,bridge,u,enemy.Ref);
        var members=m.Battlefield.FrontLine.Members.OfType<CardBase>().ToList();
        var routedCard=members.SingleOrDefault(c=>m.CardLibrary.TryGetRegisteredId(c.Definition,out var rid)&&rid==routed);
        Assert.True(routedCard is not null,$"前线成员：{string.Join("、",members.Select(c=>c.Definition.Name))}");
    }
    [Fact]
    public async Task FuYuanCopiesToHandWithExtraCostWhenLeaving()
    {
        var (m,u,k)=await Create(new(){Handlers=[new(){Event="card.leaving",Subject="self",Actions=[new(){Op="copy.hand",Target="self",Value="2"}]}],
            Skills=[new(){Id="muster",Name="复员归队",Cost="0",SpendMove=false,SpendAttack=false,Actions=[new(){Op="return.hand",Target="self"}]}]});
        await m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));var c=Assert.Single(KeywordProgramRuntime.Components(u));
        Assert.True(await c.UseAsync("muster"));
        var copies=m.Players[0].Hand.OfType<CardBase>().Where(x=>ReferenceEquals(x.Definition,u.Definition)).ToList();
        static void Walk(Orc.Core.EventStream s,List<string> acc){foreach(var e in s.Entries)if(e.Level>=LogLevel.Error)acc.Add($"{e.Source}:{e.Message}");foreach(var c in s.Children)Walk(c,acc);}
        var errors=new List<string>();Walk(m.Engine.RootStream,errors);
        Assert.True(copies.Count==2,$"手牌实例数={copies.Count}（含复员={copies.Count(x=>x.Keywords.Has(k))}）；流错误：{string.Join(" | ",errors)}");
        var copy=Assert.Single(copies,x=>!x.Keywords.Has(k));var returned=Assert.Single(copies,x=>x.Keywords.Has(k));
        Assert.Equal(u.Definition.DeployCost+2,copy.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
        Assert.Equal(u.Definition.DeployCost,returned.Modifiers.GetEffectiveValue(CardStatFields.DeployCost));
    }
    [Fact]
    public async Task ChanDouLinksEnemyDeniesPairAndFightsAtOwnerTurnStart()
    {
        var bridge=new MockTargeterBridge();
        var (m,u,k)=await Create(new(){Handlers=[
            new(){Event="unit.deployed",Subject="self",Actions=[new(){Op="link",Target="choose.enemy.unit"}]},
            new(){Event="turn.start",Subject="owner",Actions=[new(){Op="attack",Target="linked"}]}],Rules=[
            new(){Rule="move.allowed",Scope="pair",Value="0",Priority=1},
            new(){Rule="attack.allowed",Scope="pair",Value="0",Priority=1}]},bridge:bridge);
        var enemy=await CommandTestKit.PrepareOnFrontAsync(m,m.Players[1],CommandTestKit.InfantryId,0);
        var play=m.PlayManager.PlayUnitAsync(u,m.Battlefield.PlayerASupportLine.GetInsertionGaps().At(1));
        var pending=await bridge.WaitForNextBeginAsync();
        pending.Responder.Complete(pending.Description.RequestId,TargeterTestKit.Selection(TargetSlot.DefaultName,enemy.Ref));
        Assert.Equal(PlayResultStatus.Success,(await play).Status);
        Assert.False(m.CommandManager.GetCommandAvailability(u).Move.CanUse);
        Assert.False(m.CommandManager.GetCommandAvailability(enemy).Move.CanUse);
        await EffectRuntime.ResolveFor(u)!.BuffAsync(u,2,0);
        var c=Assert.Single(KeywordProgramRuntime.Components(u));
        var defenseBefore=enemy.Modifiers.GetEffectiveValue(CardStatFields.Defense);
        await m.Engine.Emit("turn.start",new Dictionary<string,object?>{{"Player",m.Players[0]}});
        Assert.Equal(1,c.Attacks);
        Assert.True(enemy.GetData<UnitStateData>().IsDestroyed||enemy.Modifiers.GetEffectiveValue(CardStatFields.Defense)<defenseBefore);
    }
    [Fact]
    public async Task ZhuangTianRecordsAttackThenLaunchDealsRecordedDamage()
    {
        const string loader="u_loader_test";const string launch="u_launch_test";
        var bridge=new MockTargeterBridge();
        ProgramKeywordComponent.Register(loader,new KeywordProgram{Handlers=[new(){Event="card.played",Subject="self",Actions=[
            new(){Op="capture",Target="choose.unit",Value="target.attack",Key="loaded"},
            new(){Op="create.hand",Target="self",Key=launch,Bindings=new(){{"damage","data.loaded"}}}]}]});
        ProgramKeywordComponent.Register(launch,new KeywordProgram{Handlers=[new(){Event="card.played",Subject="self",Actions=[
            new(){Op="damage",Target="choose.unit",Value="data.damage"}]}]});
        Registered.Add(loader);Registered.Add(launch);
        var match=CommandTestKit.CreateCommandMatch(bridge,extraDefinitions:[
            OrderDef(loader,"装填",0,new KeywordDeclaration(loader)),
            OrderDef(launch,"发射",0,new KeywordDeclaration(launch))]);
        await match.Initialize();
        var source=await CommandTestKit.PrepareOnSupportAsync(match,match.Players[0],CommandTestKit.InfantryId,1);
        var enemy=await CommandTestKit.PrepareOnFrontAsync(match,match.Players[1],CommandTestKit.MegaId,0);
        var loaderCard=await PlayChainTestKit.InstantiateLoadedAsync<OrderCard>(match,match.Players[0],loader);
        var play=match.PlayManager.BeginOrderPrePlayAsync(loaderCard);
        var pending=await bridge.WaitForNextBeginAsync();
        pending.Responder.Complete(pending.Description.RequestId,TargeterTestKit.Selection(TargetSlot.DefaultName,source.Ref));
        Assert.Equal(PlayResultStatus.Success,(await play).Status);
        var launchCard=match.Players[0].Hand.OfType<CardBase>().SingleOrDefault(x=>match.CardLibrary.TryGetRegisteredId(x.Definition,out var lid)&&lid==launch);
        static void Walk(Orc.Core.EventStream s,List<string> acc){foreach(var e in s.Entries)if(e.Level>=LogLevel.Error)acc.Add($"{e.Source}:{e.Message}");foreach(var c in s.Children)Walk(c,acc);}
        var errors=new List<string>();Walk(match.Engine.RootStream,errors);
        var handIds=string.Join("、",match.Players[0].Hand.Select(x=>match.CardLibrary.TryGetRegisteredId(((CardBase)x).Definition,out var cid)?cid:"("+x.GetType().Name+")"));
        Assert.True(launchCard is not null,$"发射未入手；手牌：{handIds}；流错误：{string.Join(" | ",errors)}");
        var recorded=Assert.Single(launchCard.GetData<KeywordProgramData>().Values);
        Assert.Equal("damage",recorded.Key);Assert.Equal(source.Modifiers.GetEffectiveValue(CardStatFields.Attack),recorded.Value);
        var before=enemy.Modifiers.GetEffectiveValue(CardStatFields.Defense);
        var second=match.PlayManager.BeginOrderPrePlayAsync((OrderCard)launchCard);
        var secondPending=await bridge.WaitForNextBeginAsync();
        secondPending.Responder.Complete(secondPending.Description.RequestId,TargeterTestKit.Selection(TargetSlot.DefaultName,enemy.Ref));
        Assert.Equal(PlayResultStatus.Success,(await second).Status);
        Assert.Equal(before-recorded.Value,enemy.Modifiers.GetEffectiveValue(CardStatFields.Defense));
    }
}
