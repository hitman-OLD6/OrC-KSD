using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Commanding;
using Orc.Game.Effects;
using Orc.Game.Players;
using Orc.Game.Targeting;

namespace Orc.Game.Cards;

/// <summary>Per-instance native keyword state; no rules or mutable match state live in the UI.</summary>
public sealed class ProgramKeywordComponent : KeywordComponent
{
    private readonly KeywordProgram _program;
    private Card _host=null!;
    private KeywordLoadContext? _context;
    private IDisposable? _subscription;
    private readonly Dictionary<string,int> _state;
    private readonly HashSet<string> _skillsRunning=[];
    private sealed class SelectionState
    {
        public readonly Dictionary<string,Card> Targets=[];
        public readonly Dictionary<KeywordProgramAction,string> Cards=new(ReferenceEqualityComparer.Instance);
    }
    private readonly AsyncLocal<SelectionState?> _selection=new();
    private readonly Dictionary<KeywordEventHandler,SelectionState> _preparedPlay=new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<KeywordProgramAction,string[]> _offers=new(ReferenceEqualityComparer.Instance);
    private UnitCard? _linked;
    private int _entries,_attacks;
    private readonly object _temporarySource=new();
    private ProgramKeywordComponent(string id,int? value,KeywordProgram program):base(id,value)
    {_program=program;_state=new(program.State);}
    public static void Register(string id,KeywordProgram program,bool battle=false)
    {
        var frozen=KeywordProgram.Parse(program.ToJson());
        KeywordRegistry.RegisterOrReplace(id,(_,value)=>new ProgramKeywordComponent(id,value,frozen),battle);
    }
    public IReadOnlyDictionary<string,int> State=>new Dictionary<string,int>(_state);
    public int Entries=>_entries;
    public int Attacks=>_attacks;
    internal void RestoreEntries(int entries)=>_entries=entries;
    public KeywordProgram Definition=>KeywordProgram.Parse(_program.ToJson());
    public int Number(string expression)=>NumberFor(expression,_host);
    private int NumberFor(string expression,Card target,Card? actor=null)=>expression switch
    {"value"=>Value??_program.DefaultValue,"entries"=>_entries,"attacks"=>_attacks,"linked.alive"=>LinkedAlive?1:0,
        "target.attack"=>target is CardBase attack?attack.Modifiers.GetEffectiveValue(CardStatFields.Attack):0,
        "target.defense"=>target is CardBase defense?defense.Modifiers.GetEffectiveValue(CardStatFields.Defense):0,
        "target.cost"=>target is CardBase cost?cost.Modifiers.GetEffectiveValue(CardStatFields.DeployCost):0,
        "target.opCost"=>target is UnitCard operation?OperateCosts.Effective(operation):0,
        _ when expression.StartsWith("event.",StringComparison.Ordinal)=>actor is null?throw new InvalidOperationException("当前没有事件单位"):NumberFor("target."+expression[6..],actor),
        _ when expression.StartsWith("data.",StringComparison.Ordinal)=>_host.TryGetData<KeywordProgramData>(out var data)&&data.Values.TryGetValue(expression[5..],out var recorded)?recorded:throw new InvalidOperationException("卡牌缺少实例记录："+expression[5..]),
        _ when expression.StartsWith("state.",StringComparison.Ordinal)=>_state[expression[6..]],_=>int.Parse(expression,System.Globalization.CultureInfo.InvariantCulture)};
    private bool Matches(KeywordCondition[] conditions,Card? actor=null,Card? target=null)=>conditions.All(c=>c.Op switch
    {"eq"=>NumberFor(c.Left,target??_host,actor)==NumberFor(c.Right,target??_host,actor),"ne"=>NumberFor(c.Left,target??_host,actor)!=NumberFor(c.Right,target??_host,actor),"gt"=>NumberFor(c.Left,target??_host,actor)>NumberFor(c.Right,target??_host,actor),"gte"=>NumberFor(c.Left,target??_host,actor)>=NumberFor(c.Right,target??_host,actor),"lt"=>NumberFor(c.Left,target??_host,actor)<NumberFor(c.Right,target??_host,actor),"lte"=>NumberFor(c.Left,target??_host,actor)<=NumberFor(c.Right,target??_host,actor),_=>false});
    private bool LinkedAlive=>OnBoard&&_linked is not null&&_linked.TryGetData<UnitStateData>(out var state)&&!state.IsDestroyed&&state.Position is not null;
    internal IEnumerable<(KeywordRulePatch Rule,int Value)> Contributions(string rule,Card subject)=>_subscription is null?[]:_program.Rules.Where(r=>r.Rule==rule&&Matches(r.When,target:subject)&&Applies(r.Scope,subject)).Select(r=>(r,NumberFor(r.Value,subject)));
    private bool Applies(string scope,Card subject)=>scope switch
    {
        "self"=>ReferenceEquals(subject,_host),"linked"=>LinkedAlive&&ReferenceEquals(subject,_linked),"pair"=>LinkedAlive&&(ReferenceEquals(subject,_host)||ReferenceEquals(subject,_linked)),
        "friendly"=>OnBoard&&subject is CardBase friendly&&ReferenceEquals(friendly.Owner,Owner),"enemy"=>OnBoard&&subject is CardBase enemy&&!ReferenceEquals(enemy.Owner,Owner),"all"=>OnBoard,_=>false
    };
    internal override void Mount(Card card,KeywordLoadContext? context)
    {
        _host=card;_context=context;
        if(context is not null)
        {
            _subscription=context.Engine.Subscribe(OnUpdate);
            KeywordProgramRuntime.Attach(card.Engine,this);
            AttachDeclarations();
        }
    }
    internal override void Unmount(){DetachDeclarations();_subscription?.Dispose();_subscription=null;_context=null;_linked=null;if(_host is not null)KeywordProgramRuntime.Detach(_host.Engine,this);}

    // ---------- 目标可选性声明（S3：词条程序规则 → 包装目标候选名单判定器） ----------

    private readonly List<TargetingDeclaration> _declarations = new();
    private readonly Dictionary<(Card Target,string Kind),TargetingDeclaration> _dynamicDeclarations = new();

    private void AttachDeclarations()
    {
        var match=KeywordProgramRuntime.TryMatchFor(_host.Engine);
        var owner=Owner;
        if(match is null||owner is null)return;
        // 装配链内部访问面：卡加载发生在 Initialize 的准备态，公共 Players/Judicators 受准备态门禁，
        // 内部步骤改走内部面（对外契约不变）。
        var opponent=match.AssemblyPlayers.FirstOrDefault(player=>!ReferenceEquals(player,owner));
        if(opponent is null)return;

        foreach(var rule in _program.Rules)
        {
            if(rule.Rule!="targeting.forbid")continue;
            if(NumberFor(rule.Value,_host)==0)continue;
            var declaration=new TargetingDeclaration(
                match.AssemblyJudicators,
                _host,
                candidate=>Applies(rule.Scope,candidate),
                ()=>OnBoard,
                rule.Source);
            declaration.Attach(opponent.Index);
            _declarations.Add(declaration);
        }
    }

    private void DetachDeclarations()
    {
        foreach(var declaration in _declarations)declaration.Detach();
        _declarations.Clear();
        foreach(var declaration in _dynamicDeclarations.Values)declaration.Detach();
        _dynamicDeclarations.Clear();
    }

    /// <summary>运行期可选性声明（DSL 动作）：target 进入/退出「不可被指定来源指向」。</summary>
    private void ApplyTargetingDeclaration(Card target,string kind,bool forbid)
    {
        var key=(target,kind);
        if(!forbid)
        {
            if(_dynamicDeclarations.Remove(key,out var existing))existing.Detach();
            return;
        }

        if(_dynamicDeclarations.ContainsKey(key))return;

        var match=KeywordProgramRuntime.TryMatchFor(_host.Engine);
        var owner=Owner;
        if(match is null||owner is null)return;
        var opponent=match.AssemblyPlayers.FirstOrDefault(player=>!ReferenceEquals(player,owner));
        if(opponent is null)return;

        var declaration=new TargetingDeclaration(
            match.AssemblyJudicators,
            _host,
            candidate=>ReferenceEquals(candidate,target),
            ()=>OnBoard,
            kind);
        declaration.Attach(opponent.Index);
        _dynamicDeclarations.Add(key,declaration);
    }
    private async Task OnUpdate(string kind,IReadOnlyDictionary<string,object?>? payload,CancellationToken ct)
    {
        var actor=payload?.GetValueOrDefault(GameUpdates.PayloadUnit) as Card??payload?.GetValueOrDefault(GameUpdates.PayloadCard) as Card;
        var player=payload?.GetValueOrDefault(GameUpdates.PayloadPlayer) as Player;
        if(kind=="card.placed"&&ReferenceEquals(actor,_host))_entries++;
        if(kind=="turn.start"&&ReferenceEquals(player,Owner))_attacks=0;
        if(!OnBoard&&(!ReferenceEquals(actor,_host)||kind is not("card.played" or "card.placed" or "unit.deployed" or "unit.joined" or "card.dying" or "card.leaving")))return;
        if(kind.StartsWith("keyword.",StringComparison.Ordinal)&&payload?.GetValueOrDefault(GameUpdates.PayloadKeyword) as string!=Keyword)return;
        await Dispatch(kind,actor,player,null,ct);
        if(kind is "card.dying" or "card.leaving"&&(ReferenceEquals(actor,_host)||ReferenceEquals(actor,_linked)))_linked=null;
    }
    internal async Task Dispatch(string kind,Card? actor,Player? player,KeywordProgramFrame? frame,CancellationToken ct)
    {
        if(_context is null)return;
        using var depth=KeywordProgramRuntime.Enter(_host.Engine);
        if(kind=="unit.attack.after"&&ReferenceEquals(actor,_host))_attacks++;
        foreach(var h in _program.Handlers)
        {
            if(h.Event!=kind||!Matches(h.When,actor))continue;
            var who=actor is CardBase card?card.Owner:player;
            var match=h.Subject switch
            {"self"=>ReferenceEquals(actor,_host),"owner"=>ReferenceEquals(player,Owner),"opponent"=>player is not null&&!ReferenceEquals(player,Owner),"friendly"=>ReferenceEquals(who,Owner),"enemy"=>who is not null&&!ReferenceEquals(who,Owner),"any"=>true,_=>false};
            if(match)await RunHandler(h,actor,frame,ct);
        }
    }
    private async Task RunHandler(KeywordEventHandler handler,Card? actor,KeywordProgramFrame? frame,CancellationToken ct)
    {
        var previous=_selection.Value;
        _selection.Value=_preparedPlay.Remove(handler,out var prepared)?prepared:new();
        try{if(await PrepareSelections(handler.Actions,ct,actor))await Execute(handler.Actions,actor,frame,ct);}
        finally{_selection.Value=previous;}
    }
    private Player? Owner=>(_host as CardBase)?.Owner;
    private bool OnBoard=>_host.TryGetData<UnitStateData>(out var s)&&!s.IsDestroyed&&s.Position is not null;
    private EffectRuntime Runtime=>EffectRuntime.ResolveFor(_host)??throw new InvalidOperationException("词条缺少原生效果服务");
    private async Task<IReadOnlyList<Card>> Targets(string name,Card? actor,CancellationToken ct)
    {
        if(name.StartsWith("choose.",StringComparison.Ordinal))return (_selection.Value?.Targets??new Dictionary<string,Card>()).TryGetValue(name,out var selected)?[selected]:[];
        if(name=="self")return [_host];if(name=="event")return actor is null?[]:[actor];
        if(name=="linked")return LinkedAlive?[_linked!]:[];
        var match=KeywordProgramRuntime.MatchFor(_host.Engine);
        if(name.EndsWith(".hq",StringComparison.Ordinal))return match.Players.Where(p=>name=="friendly.hq"?ReferenceEquals(p,Owner):!ReferenceEquals(p,Owner)).Select(p=>(Card)p.Hq).ToArray();
        var enemy=name.Contains("enemy",StringComparison.Ordinal);var zone=name.Contains("frontline",StringComparison.Ordinal)?"frontline":null;
        var all=await Runtime.SelectAsync(_host,new EffectSelector("all",enemy?"enemy":"friendly",zone),ct);
        return name.StartsWith("random.",StringComparison.Ordinal)&&all.Count>0?[match.RandomService.PickOne(all)]:all;
    }
    private async Task Execute(KeywordProgramAction[] actions,Card? actor,KeywordProgramFrame? frame,CancellationToken ct)
    {
        foreach(var a in actions)
        {
            ct.ThrowIfCancellationRequested();
            if(a.Op=="if"){await Execute(Matches(a.When,actor)?a.Then:a.Else,actor,frame,ct);continue;}
            if(!Matches(a.When,actor))continue;
            // target./event. 相对表达式须等目标确定后再求值（循环内逐目标重算）；此处只求宿主相对部分，
            // 否则宿主缺该字段基准（如指令卡读 Attack、手牌单位读行动花费）会误伤无辜动作。
            var n=a.Value.StartsWith("target.",StringComparison.Ordinal)?0:NumberFor(a.Value,_host,actor);
            var second=a.Second.StartsWith("target.",StringComparison.Ordinal)?0:NumberFor(a.Second,_host,actor);
            if(a.Op=="state.set"){_state[a.Key]=n;continue;}
            if(a.Op=="state.add"){_state[a.Key]=Math.Clamp(checked(_state[a.Key]+n),-1_000_000,1_000_000);continue;}
            if(a.Op=="death.cancel"){if(frame is null)throw new InvalidOperationException("取消消灭只能用于消灭前事件");frame.Cancelled=true;continue;}
            if(a.Op=="emit"){await _host.Engine.Emit(a.Key,new Dictionary<string,object?>{{GameUpdates.PayloadCard,_host},{GameUpdates.PayloadKeyword,Keyword}},ct);continue;}
            foreach(var target in a.Target=="attack.target"?(frame?.AttackTarget is { } attackTarget?new[]{attackTarget}:Array.Empty<Card>()):await Targets(a.Target,actor,ct))
            {
                n=NumberFor(a.Value,target,actor);second=NumberFor(a.Second,target,actor);
                switch(a.Op)
                {
                    case "develop":
                        var developedMatch=KeywordProgramRuntime.MatchFor(_host.Engine);
                        if(_selection.Value?.Cards.TryGetValue(a,out var selectedDefinition)!=true)throw new InvalidOperationException("开发尚未完成选择");
                        var developedOwner=Owner??throw new InvalidOperationException("无归属");
                        var developed=await developedMatch.CardService.CreateAsync(selectedDefinition!,developedOwner,ct);
                        await developedMatch.CardService.PlaceToHandAsync(developed,developedOwner,ct);_offers.Remove(a);break;
                    case "stat.add":case "stat.set":
                        var field=a.Key switch{"attack"=>CardStatFields.Attack,"defense"=>CardStatFields.Defense,"opCost"=>CardStatFields.OperateCost,"deployCost"=>CardStatFields.DeployCost,"hqHealth"=>CardStatFields.HqHealth,_=>throw new InvalidOperationException("未知属性")};
                        if(target is CardBase statCard)await statCard.Modifiers.AddModifierAsync(a.Op=="stat.add"?new AddModifier(field,n,this):new SetModifier(field,n,this),ct);break;
                    case "targeting.forbid":ApplyTargetingDeclaration(target,a.Key,true);break;
                    case "targeting.allow":ApplyTargetingDeclaration(target,a.Key,false);break;
                    case "link":if(target is UnitCard linked&&!ReferenceEquals(linked,_host))_linked=linked;break;
                    case "unlink":_linked=null;break;
                    case "attack":if(_host is UnitCard attacker)await KeywordProgramRuntime.MatchFor(_host.Engine).CommandManager.AttackByEffectAsync(attacker,target,ct,a.BypassRange);break;
                    case "retreat":
                        // 效果位移入口（S6）：不扣费、不清行动位、不受压制阻断；槽位变更与位置更新
                        // 由底层移动动作的唯一落地口完成，本处不再直写槽位。
                        if(target is UnitCard retreat&&retreat.Owner is { } retreatOwner)
                        {
                            var retreatMatch=KeywordProgramRuntime.MatchFor(_host.Engine);
                            var support=retreatMatch.Battlefield.GetSupportLine(retreatOwner);
                            var retreatPosition=retreat.GetData<UnitStateData>().Position;
                            if(retreatPosition is not null&&!support.Contains(retreatPosition)&&support.FirstOrDefault(s=>s.IsEmpty) is { } destination)
                                await retreatMatch.CommandManager.MoveUnitAsync(retreat,destination,ct);
                        }break;
                    case "replace":
                        if(target is UnitCard original&&original.Owner is { } originalOwner&&original.GetData<UnitStateData>().Position is { } oldSlot)
                        {
                            var replacementMatch=KeywordProgramRuntime.MatchFor(_host.Engine);
                            var replacement=await replacementMatch.CardService.CreateAsync(a.Key,originalOwner,ct);
                            if(replacement is not UnitCard replacementUnit)throw new InvalidOperationException("替换目标必须是单位定义");
                            if(!original.GetData<UnitStateData>().IsDestroyed)await replacementMatch.CommandManager.LeaveBattlefieldAsync(original,ct);
                            var joined=await replacementMatch.PlayManager.JoinUnitAsync(replacementUnit,oldSlot,ct);
                            if(joined.Status!=PlayResultStatus.Success)throw new InvalidOperationException("替换入场失败");
                            if(!original.GetData<UnitStateData>().IsDestroyed)await _host.Engine.DestroyCard(original);
                        }break;
                    case "capture":
                        if(!_host.TryGetData<KeywordProgramData>(out var recorded)){recorded=new();_host.AddData(recorded);}recorded.Values[a.Key]=n;break;
                    case "move.set":if(target.TryGetData<CommandData>(out var move))move.SetMoveQuota(n!=0?1:0);break;
                    case "attack.set":if(target.TryGetData<CommandData>(out var attack))attack.SetAttackQuota(n!=0?1:0);break;
                    case "damage":await Runtime.DamageAsync(target,Math.Max(0,n),_host,ct);break;
                    case "kill":await Runtime.KillAsync(target,_host,ct);break;
                    case "health.set":
                        if(target is UnitCard healthCard)await healthCard.SetRemainingDefenseAsync(Math.Max(1,n),ct);break;
                    case "heal":
                        if (target is UnitCard healTarget)
                        {
                            await healTarget.RepairDefenseAsync(Math.Max(0, n), ct);
                        }
                        break;
                    case "buff":
                        if(a.Temporary&&target is CardBase buffCard)
                        {
                            var expiry=new ModifierExpiry(GameUpdates.KeywordAttackFinished,p=>ReferenceEquals(p?.GetValueOrDefault(GameUpdates.PayloadCard),_host));
                            await buffCard.Modifiers.AddModifiersAsync([new AddModifier(CardStatFields.Attack,n,_temporarySource,expiry),new AddModifier(CardStatFields.Defense,second,_temporarySource,expiry)],ct);
                            KeywordProgramRuntime.TrackTemporary(_host);
                        }
                        else await Runtime.BuffAsync(target,n,second,ct:ct,source:_host);break;
                    case "keyword.grant":await Runtime.GrantAsync(target,a.Key,n,ct,_host);break;
                    case "keyword.remove":await Runtime.RevokeAsync(target,a.Key.Length>0?a.Key:Keyword,ct,_host);break;
                    case "move":await Runtime.MoveAsync(target,a.Key,ct,_host);break;
                    case "draw":await Runtime.DrawAsync(target,Math.Max(0,n),ct,_host);break;
                    case "points":if(n>=0)await Runtime.GainPointsAsync(target,n,ct,_host);else await Runtime.LosePointsAsync(target,-n,ct,_host);break;
                    case "attach.prefab":await Runtime.AttachPrefabAsync(target,a.Key,ct:ct);break;
                    case "copy.hand":case "create.hand":
                        var match=KeywordProgramRuntime.MatchFor(_host.Engine);var owner=(target as CardBase)?.Owner??Owner??throw new InvalidOperationException("无归属");
                        string? id=a.Key;
                        if(a.Op=="copy.hand"&&!match.CardService.TryResolveDefinitionId((CardBase)target,out id))throw new InvalidOperationException("复制源定义不存在");
                        var copy=await match.CardService.CreateAsync(id!,owner,ct);
                        var values=new KeywordProgramData();foreach(var pair in a.Bindings)values.Values[pair.Key]=NumberFor(pair.Value,target,actor);if(values.Values.Count>0)copy.AddData(values);
                        if(a.Op=="copy.hand")await copy.Keywords.RevokeAsync(Keyword);
                        if(n!=0)await Runtime.DeployCostModAsync(copy,n,ct);
                        await match.CardService.PlaceToHandAsync(copy,owner,ct);
                        if(a.Temporary)ExpireFromHand(copy,owner);break;
                    case "return.hand":
                        if(target is UnitCard returning&&returning.Owner is { } returningOwner)
                        {
                            var returningMatch=KeywordProgramRuntime.MatchFor(_host.Engine);
                            if(!returningMatch.CardService.TryResolveDefinitionId(returning,out var returningId))throw new InvalidOperationException("回手单位的定义不存在");
                            var history=KeywordProgramRuntime.Components(returning).ToDictionary(c=>c.Keyword,c=>c.Entries);
                            await returningMatch.CommandManager.LeaveBattlefieldAsync(returning,ct);
                            var returned=await returningMatch.CardService.CreateAsync(returningId,returningOwner,ct);
                            if(a.KeepHistory)foreach(var component in KeywordProgramRuntime.Components(returned))component.RestoreEntries(history.GetValueOrDefault(component.Keyword));
                            await _host.Engine.DestroyCard(returning);
                            await returningMatch.CardService.PlaceToHandAsync(returned,returningOwner,ct);
                        }break;
                }
            }
        }
    }
    internal async Task ClearTemporary(CancellationToken ct)
    {if(_host is CardBase c)await c.Modifiers.RemoveBySourceAsync(_temporarySource,ct);}
    internal async Task ReplayDeployment(CancellationToken ct)
    {
        if(!OnBoard)return;
        foreach(var handler in _program.Handlers.Where(h=>h.DeploymentEffect&&Matches(h.When,_host)))
            await RunHandler(handler,_host,null,ct);
    }
    private void ExpireFromHand(CardBase copy,Player owner)
    {
        IDisposable? lease=null;var engine=_host.Engine;
        lease=engine.Subscribe(async(kind,payload,ct)=>
        {
            if(kind=="card.destroyed"&&ReferenceEquals(payload?.GetValueOrDefault(GameUpdates.PayloadCard),copy)){lease?.Dispose();return;}
            if(kind!="turn.end")return;lease?.Dispose();
            if(owner.Hand.Contains(copy)){owner.Hand.Remove(copy);await engine.DestroyCard(copy);}
        });
    }
    public async Task<bool> PreparePlayAsync(CancellationToken ct=default)
    {
        _preparedPlay.Clear();var previous=_selection.Value;var entries=_entries;
        try
        {
            if(_host is UnitCard)_entries++;
            foreach(var h in _program.Handlers.Where(h=>h.Event is "card.played" or "unit.deployed" or "card.placed"&&h.Subject is "self" or "friendly" or "any"))
            {
                if(!Matches(h.When,_host))continue;
                _selection.Value=new();
                if(!await PrepareSelections(h.Actions,ct,_host))return false;
                _preparedPlay[h]=_selection.Value;
            }
            return true;
        }
        finally{_entries=entries;_selection.Value=previous;}
    }
    private IEnumerable<KeywordProgramAction> SelectionSteps(KeywordProgramAction[] actions, Card? actor)
    {
        foreach (var action in actions)
        {
            if (action.Op == "develop") yield return action;
            else if (action.Op == "if")
                foreach (var step in SelectionSteps(Matches(action.When, actor) ? action.Then : action.Else, actor)) yield return step;
            else if (action.Target.StartsWith("choose.", StringComparison.Ordinal) && Matches(action.When, actor)) yield return action;
        }
    }
    private Task<IReadOnlyList<Card>> SelectionCandidates(string target, CancellationToken ct = default)
    {
        var side = target == "choose.unit" ? "both" : target.Contains("enemy", StringComparison.Ordinal) ? "enemy" : "friendly";
        return Runtime.SelectAsync(_host, new EffectSelector("all", side), ct);
    }
    /// <summary>Pure preview of the first native pre-play input. Null targets indicate a card picker,
    /// not a battlefield arrow. No selections, random draws, state changes or payments occur here.</summary>
    public (bool Required, IReadOnlyList<Card>? Targets) ReadFirstPlayInput()
    {
        if (_host is not OrderCard || _context is null) return (false, null);
        foreach (var handler in _program.Handlers.Where(h => h.Event is "card.played" or "unit.deployed" or "card.placed"
            && h.Subject is "self" or "friendly" or "any"))
        {
            if (!Matches(handler.When, _host)) continue;
            var first = SelectionSteps(handler.Actions, _host).FirstOrDefault();
            if (first is null) continue;
            if (first.Op == "develop") return (true, null);

            // 目标解析是纯查询（不同步等待任何外部选择），正常情况下同步完成。
            // 若将来引入真异步查询，这里退化为"需要选择但候选未就绪"，由调用方重新查询，
            // 绝不在调用线程上阻塞等待。
            var candidates = SelectionCandidates(first.Target);
            return (true, candidates.IsCompletedSuccessfully ? candidates.Result : null);
        }
        return (false, null);
    }
    private async Task<bool> PrepareSelections(KeywordProgramAction[] actions,CancellationToken ct,Card? actor=null)
    {
        foreach(var action in SelectionSteps(actions, actor))
        {
            if(action.Op=="develop"&&Matches(action.When,actor))
            {
                var state=_selection.Value??throw new InvalidOperationException("缺少选择上下文");
                if(state.Cards.ContainsKey(action))continue;
                var match=KeywordProgramRuntime.MatchFor(_host.Engine);
                if(action.Cards.Any(id=>!match.CardService.RegisteredDefinitions.ContainsKey(id)))throw new InvalidOperationException("开发卡池含未装载定义");
                if(!_offers.TryGetValue(action,out var offers)){offers=match.RandomService.PickN(action.Cards,Math.Clamp(Number(action.Value),1,Math.Min(10,action.Cards.Length))).ToArray();_offers[action]=offers;}
                if(_context?.TargeterManager is not { } picker)return false;
                string? selectedDefinition=null;
                var developResult=await picker.RunAsync(async flow=>
                {
                    // R2/R8：开发选牌同样是本组件的选靶——归属＝组件归属、类别＝effect。
                    var step=await flow.Step(SelectorTemplates.CardPickerList,new IdentifierSetParameter(offers,1,1){Owner=Owner,Action=SelectorActions.Effect});
                    while(step.IsFailed&&step.Failure==SelectorFailureReason.InvalidSelection)step=await flow.Retry<IReadOnlyList<string>>();
                    if(step.IsCancelled)return TargeterResult.Cancelled();if(step.IsFailed)return TargeterResult.FromSelectorFailure(step.Failure);
                    selectedDefinition=step.Value?.SingleOrDefault();return selectedDefinition is null?TargeterResult.Failed(TargeterFailureReason.InvalidSelection):TargeterResult.Ok();
                });
                if(!developResult.IsOk||selectedDefinition is null)return false;state.Cards[action]=selectedDefinition;continue;
            }
            var target=action.Target;
            var candidates=(await SelectionCandidates(target,ct)).ToArray();
            if(_selection.Value!.Targets.TryGetValue(target,out var existing)&&candidates.Any(c=>ReferenceEquals(c,existing)))continue;
            if(candidates.Length==0||_context?.TargeterManager is not { } targeter)return false;
            Card? selected=null;
            var result=await targeter.RunAsync(async flow=>
            {
                var step=await flow.Step(SelectorTemplates.FieldUnit,new ReferenceSetParameter(candidates.Select(c=>c.Ref),1,1){Owner=Owner,Action=SelectorActions.Effect});
                while(step.IsFailed&&step.Failure==SelectorFailureReason.InvalidSelection)step=await flow.Retry<Ref<Entity>>();
                if(step.IsCancelled)return TargeterResult.Cancelled();if(step.IsFailed)return TargeterResult.FromSelectorFailure(step.Failure);
                selected=step.Value?.Value as Card;return selected is null?TargeterResult.Failed(TargeterFailureReason.InvalidSelection):TargeterResult.Ok();
            });
            if(!result.IsOk||selected is null)return false;_selection.Value!.Targets[target]=selected;
        }
        return true;
    }
    public bool CanUse(string id)=>CanUseCore(id,false);
    private bool CanUseCore(string id,bool active)
    {
        var skill=_program.Skills.FirstOrDefault(s=>s.Id==id);
        return skill is not null&&OnBoard&&_context is not null&&KeywordProgramRuntime.MatchFor(_host.Engine).State==MatchState.InProgress&&ReferenceEquals(_context.CurrentPlayerProvider?.Invoke(),Owner)&&(active||!_skillsRunning.Contains(id))
            &&Matches(skill.When)&&Owner!.Points>=Math.Max(0,Number(skill.Cost))&&!KeywordRules.HasKeyword(_host,KeywordIds.Suppressed)
            &&_host.TryGetData<CommandData>(out var command)&&(!skill.SpendMove||command.CanMove)&&(!skill.SpendAttack||command.CanAttack);
    }
    public async Task<bool> UseAsync(string id,CancellationToken ct=default)
    {
        if(!CanUse(id))return false;var skill=_program.Skills.Single(s=>s.Id==id);_skillsRunning.Add(id);
        var previous=_selection.Value;_selection.Value=new();
        try
        {
            if(!await PrepareSelections(skill.Actions,ct,_host)||!CanUseCore(id,true))return false;
            var cost=Math.Max(0,Number(skill.Cost));
            if(cost>0)await Runtime.LosePointsAsync(_host,cost,ct,_host);
            var command=_host.GetData<CommandData>();if(skill.SpendMove)command.SetMoveQuota(0);if(skill.SpendAttack)command.SetAttackQuota(0);
            await Execute(skill.Actions,_host,null,ct);return true;
        }
        finally{_skillsRunning.Remove(id);_selection.Value=previous;}
    }
}

public sealed class KeywordProgramFrame {public bool Cancelled {get;set;}public Card? AttackTarget {get;init;}}
public sealed class KeywordProgramData {public Dictionary<string,int> Values {get;}=new();}

/// <summary>Match-local composition; priority is explicit and denies win ties. Core moding is unchanged.</summary>
public static class KeywordProgramRuntime
{
    private sealed record AttackOrigin(UnitCard Actor,bool BypassRange);
    private static readonly AsyncLocal<AttackOrigin?> EffectAttacker=new();
    private sealed class EffectScope(AttackOrigin? previous):IDisposable{public void Dispose()=>EffectAttacker.Value=previous;}
    internal static IDisposable BeginEffectAttack(UnitCard card,bool bypassRange=false){var previous=EffectAttacker.Value;EffectAttacker.Value=new(card,bypassRange);return new EffectScope(previous);}
    internal static bool IsEffectAttack(UnitCard card)=>ReferenceEquals(EffectAttacker.Value?.Actor,card);
    internal static bool BypassesRange(UnitCard card)=>ReferenceEquals(EffectAttacker.Value?.Actor,card)&&EffectAttacker.Value!.BypassRange;
    private sealed class Session
    {
        public Match? Match;
        public readonly AsyncLocal<int> Depth = new();
        public readonly List<ProgramKeywordComponent> Live = [];
        public readonly HashSet<Card> Temporary = [];
    }

    /// <summary>
    /// 对局级词条程序服务的引用键：登记在引擎实例引用表（<see cref="LogicEngine.References"/>）上，
    /// 随引擎实例丢弃而清空。不使用 ConditionalWeakTable——那会让 Session→Match→Engine 形成
    /// value→key 强引用环，导致引擎实例无法回收。
    /// </summary>
    private const string SessionKey = "orc.game.keyword-program.session";

    private static Session SessionFor(LogicEngine engine)
    {
        if (engine.References.TryResolve<Session>(SessionKey, out var existing) && existing is not null)
        {
            return existing;
        }

        var session = new Session();
        engine.References.Register(SessionKey, session);
        return session;
    }

    public static void Configure(Match match) => SessionFor(match.Engine).Match = match;

    internal static void Attach(LogicEngine engine, ProgramKeywordComponent component)
    {
        var session = SessionFor(engine);
        lock (session.Live)
        {
            if (!session.Live.Contains(component))
            {
                session.Live.Add(component);
            }
        }
    }

    internal static void Detach(LogicEngine engine, ProgramKeywordComponent component)
    {
        var session = SessionFor(engine);
        lock (session.Live)
        {
            session.Live.Remove(component);
        }
    }

    internal static void TrackTemporary(Card card) => SessionFor(card.Engine).Temporary.Add(card);

    internal static bool TakeTemporary(Card card) => SessionFor(card.Engine).Temporary.Remove(card);

    internal static Match MatchFor(LogicEngine engine)
        => SessionFor(engine).Match ?? throw new InvalidOperationException("词条对局服务未装配");

    /// <summary>取对局服务（未装配＝null；独立构造/装配早期场景的宽容读面）。</summary>
    internal static Match? TryMatchFor(LogicEngine engine) => SessionFor(engine).Match;

    private sealed class DepthScope(Session session) : IDisposable
    {
        public void Dispose() => session.Depth.Value--;
    }

    internal static IDisposable Enter(LogicEngine engine)
    {
        var session = SessionFor(engine);
        if (session.Depth.Value >= 32)
        {
            throw new InvalidOperationException("词条触发超过 32 层，请检查循环");
        }

        session.Depth.Value++;
        return new DepthScope(session);
    }
    public static ProgramKeywordComponent[] Components(Card card)=>card is CardBase c?c.Keywords.Components.OfType<ProgramKeywordComponent>().ToArray():[];
    public static async Task<bool> PreparePlayAsync(Card card,CancellationToken ct)
    {foreach(var component in Components(card))if(!await component.PreparePlayAsync(ct))return false;return true;}
    internal static async Task ReplayDeployment(UnitCard card,CancellationToken ct)
    {foreach(var component in Components(card))await component.ReplayDeployment(ct);}
    public static int Resolve(Card card, string rule, int fallback)
    {
        var session = SessionFor(card.Engine);
        ProgramKeywordComponent[] live;
        lock (session.Live)
        {
            live = session.Live.ToArray();
        }

        var items = live.SelectMany(c => c.Contributions(rule, card)).ToArray();
        if (items.Length == 0)
        {
            return fallback;
        }

        var priority = items.Max(x => x.Rule.Priority);
        var top = items.Where(x => x.Rule.Priority == priority).Select(x => x.Value);
        return rule == "combat.interception" ? top.Max() : top.Min();
    }
    internal static async Task<bool> BeforeDestroy(UnitCard unit,Card? killer,CancellationToken ct)
    {
        var frame=new KeywordProgramFrame();foreach(var c in Components(unit))await c.Dispatch("unit.destroy.before",unit,unit.Owner,frame,ct);return frame.Cancelled;
    }
    internal static async Task AttackEvent(UnitCard unit,Card? target,bool before,CancellationToken ct)
    {
        foreach(var c in Components(unit))await c.Dispatch(before?"unit.attack.before":"unit.attack.after",unit,unit.Owner,new(){AttackTarget=target},ct);
        if(!before)foreach(var c in Components(unit))await c.ClearTemporary(ct);
    }
}
