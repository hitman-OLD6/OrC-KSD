using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orc.Game.Cards;

/// <summary>Serializable, name-independent vocabulary for authored native keyword behavior.</summary>
public sealed record KeywordProgram
{
    public int Schema { get; init; } = 1;
    public int DefaultValue { get; init; } = 1;
    public Dictionary<string,int> State { get; init; } = new();
    public KeywordRulePatch[] Rules { get; init; } = [];
    public KeywordEventHandler[] Handlers { get; init; } = [];
    public KeywordActiveSkill[] Skills { get; init; } = [];
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive=true, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow, WriteIndented=true };
    public static KeywordProgram Parse(string json)
    {
        if(json.Length>250_000)throw new InvalidDataException("词条程序超过 250 KB");
        KeywordProgram p;
        try{p=JsonSerializer.Deserialize<KeywordProgram>(json,Json)??throw new InvalidDataException("词条程序为空");}
        catch(JsonException e){throw new InvalidDataException("词条程序格式无效："+e.Message,e);}
        p.Validate();return p;
    }
    public string ToJson()=>JsonSerializer.Serialize(this,Json);
    public void Validate()
    {
        if(Schema!=1||DefaultValue is <0 or >10000||State is null||State.Count>64||Rules is null||Rules.Length>64||Handlers is null||Handlers.Length>64||Skills is null||Skills.Length>16)throw new InvalidDataException("词条程序结构或数量无效");
        foreach(var name in State.Keys)if(!ValidName(name))throw new InvalidDataException("状态名须使用字母、数字、下划线或点");
        foreach(var r in Rules){if(r is null||!RuleNames.Contains(r.Rule)||r.When is null||!new[]{"self","linked","pair","friendly","enemy","all"}.Contains(r.Scope))throw new InvalidDataException("未知规则点或作用范围");if(r.Source is not("any" or "command" or "unit-effect"))throw new InvalidDataException("未知来源类型");Conditions(r.When);Number(r.Value);}
        foreach(var h in Handlers){if(h is null||!EventNames.Contains(h.Event)||!Subjects.Contains(h.Subject)||h.When is null||h.Actions is null)throw new InvalidDataException("未知事件或监听对象");Conditions(h.When);Actions(h.Actions,0);}
        if(Skills.Select(s=>s.Id).Distinct().Count()!=Skills.Length)throw new InvalidDataException("主动技能编号重复");
        foreach(var s in Skills){if(s is null||!ValidName(s.Id)||string.IsNullOrWhiteSpace(s.Name)||s.Name.Length>40||s.When is null||s.Actions is null)throw new InvalidDataException("主动技能字段无效");Number(s.Cost);Conditions(s.When);Actions(s.Actions,0);}
    }
    public static readonly string[] RuleNames=["move.allowed","move.retreat","attack.allowed","combat.range","combat.counter.eligibility","combat.guard.eligibility","combat.interception","combat.ambush.condition","deploy.frontline","attack.limit","targeting.forbid"];
    public static readonly string[] EventNames=["unit.acted","turn.start.before","turn.end.before","card.played","unit.deployed","unit.joined","card.placed","turn.start","turn.end","card.damaged","card.dying","card.leaving","unit.attack.before","unit.attack.after","unit.destroy.before","keyword.ready","keyword.consumed"];
    public static readonly string[] Subjects=["self","friendly","enemy","any","owner","opponent"];
    public static readonly string[] Operations=["state.set","state.add","capture","link","unlink","move.set","attack.set","attack","replace","retreat","damage","buff","stat.add","stat.set","heal","kill","keyword.grant","keyword.remove","emit","death.cancel","health.set","copy.hand","return.hand","create.hand","develop","move","draw","points","attach.prefab","targeting.forbid","targeting.allow","if"];
    public static readonly string[] Targets=["self","event","linked","attack.target","enemy.hq","friendly.hq","enemy.units","friendly.units","enemy.frontline","friendly.frontline","random.enemy.frontline","choose.unit","choose.enemy.unit","choose.friendly.unit"];
    private static bool ValidName(string? s)=>!string.IsNullOrEmpty(s)&&s.Length<=64&&s.All(c=>char.IsLetterOrDigit(c)||c is '_' or '.' or '-');
    private void Number(string s)
    {if(s is null||!(int.TryParse(s,out var v)&&v is >=-10000 and <=10000||s is "value" or "entries" or "attacks" or "linked.alive" or "target.attack" or "target.defense" or "target.cost" or "target.opCost" or "event.attack" or "event.defense" or "event.cost" or "event.opCost"||s.StartsWith("state.",StringComparison.Ordinal)&&State.ContainsKey(s[6..])||s.StartsWith("data.",StringComparison.Ordinal)&&ValidName(s[5..])))throw new InvalidDataException("无效数值表达式："+s);}
    private void Conditions(KeywordCondition[] conditions)
    {if(conditions.Length>16)throw new InvalidDataException("条件过多");foreach(var c in conditions){if(c is null||c.Op is not("eq" or "ne" or "gt" or "gte" or "lt" or "lte"))throw new InvalidDataException("未知比较操作");Number(c.Left);Number(c.Right);}}
    private void Actions(KeywordProgramAction[] actions,int depth)
    {
        if(depth>8||actions.Length>64)throw new InvalidDataException("动作嵌套或数量过多");
        foreach(var a in actions)
        {
            if(a is null||!Operations.Contains(a.Op)||!Targets.Contains(a.Target)||a.When is null||a.Then is null||a.Else is null)throw new InvalidDataException("未知动作或对象");
            Number(a.Value);Number(a.Second);Conditions(a.When);Actions(a.Then,depth+1);Actions(a.Else,depth+1);
            if(a.Cards is null||a.Cards.Length>64||a.Cards.Any(string.IsNullOrWhiteSpace)||a.Cards.Distinct().Count()!=a.Cards.Length||a.Op=="develop"&&a.Cards.Length==0)throw new InvalidDataException("请为开发选择有效的关联卡池");
            if(a.Op is "stat.add" or "stat.set"&&!new[]{"attack","defense","opCost","deployCost","hqHealth"}.Contains(a.Key))throw new InvalidDataException("请选择有效属性");
            if(a.Bindings is null||a.Bindings.Count>32)throw new InvalidDataException("生成卡携带的数据过多");foreach(var pair in a.Bindings){if(!ValidName(pair.Key))throw new InvalidDataException("记录字段名无效");Number(pair.Value);}
            if(a.Op.StartsWith("state.",StringComparison.Ordinal)&&!State.ContainsKey(a.Key))throw new InvalidDataException("未声明状态："+a.Key);
            if(a.Op=="emit"&&a.Key is not("keyword.ready" or "keyword.consumed"))throw new InvalidDataException("不允许伪造引擎事件");
            if(a.Op is "keyword.grant" or "create.hand" or "attach.prefab"&&string.IsNullOrWhiteSpace(a.Key))throw new InvalidDataException("请填写引用编号");
            if(a.Op is "targeting.forbid" or "targeting.allow"&&a.Key is not("any" or "command" or "unit-effect"))throw new InvalidDataException("请选择有效的来源类型");
        }
    }
}
public sealed record KeywordCondition(string Left="0",string Op="eq",string Right="0");
public sealed record KeywordRulePatch
{
    public string Scope {get;init;}="self";
    public string Rule {get;init;}="move.allowed";
    public string Value {get;init;}="0";
    public int Priority {get;init;}
    public KeywordCondition[] When {get;init;}=[];
    /// <summary>来源卡类型限定（仅 targeting.forbid 使用）：command / unit-effect / any（缺省 any）。</summary>
    public string Source {get;init;}="any";
}
public sealed record KeywordEventHandler
{
    public bool DeploymentEffect {get;init;}
    public string Event {get;init;}="unit.deployed";
    public string Subject {get;init;}="self";
    public KeywordCondition[] When {get;init;}=[];
    public KeywordProgramAction[] Actions {get;init;}=[];
}
public sealed record KeywordProgramAction
{
    public string Op {get;init;}="state.set";
    public string Target {get;init;}="self";
    public string Key {get;init;}="";
    public string Value {get;init;}="0";
    public string Second {get;init;}="0";
    public bool Temporary {get;init;}
    public bool KeepHistory {get;init;}
    public bool BypassRange {get;init;}
    public Dictionary<string,string> Bindings {get;init;}=new();
    public string[] Cards {get;init;}=[];
    public KeywordCondition[] When {get;init;}=[];
    public KeywordProgramAction[] Then {get;init;}=[];
    public KeywordProgramAction[] Else {get;init;}=[];
}
public sealed record KeywordActiveSkill
{
    public string Id {get;init;}="skill-"+Guid.NewGuid().ToString("N");
    public string Name {get;init;}="技能";
    public string Cost {get;init;}="0";
    public bool SpendMove {get;init;}
    public bool SpendAttack {get;init;}=true;
    public KeywordCondition[] When {get;init;}=[];
    public KeywordProgramAction[] Actions {get;init;}=[];
}
