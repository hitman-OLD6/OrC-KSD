using System.Text;
using Orc.Cards;
using Orc.Game.EffectParsing.Dsl;
using Orc.Game.EffectParsing.Templates;

namespace Orc.Game.EffectParsing.Compilation;

/// <summary>
/// 效果转换器（转换段 S4）：DSL 实例 + 模板效果 → **带 csx 的 <see cref="EffectSnapshot"/>**。
/// <para>规则：主触发器/其它触发器/modings/injects 来自模板；被槽位指认的事件 handler＝
/// 由 op 语句模板渲染的 csx 填入；其余事件沿用模板固定 csx。实例 <c>id</c>/<c>version</c> 由调用方给出，<c>stableKey</c> 沿用模板。</para>
/// </summary>
public sealed class EffectCompiler
{
    private readonly IReadOnlyDictionary<string, EffectTemplate> _templates;
    private readonly OpTemplateCatalog _ops;

    /// <summary>创建转换器。</summary>
    /// <exception cref="ArgumentNullException">templates 或 ops 为 null。</exception>
    /// <exception cref="InvalidOperationException">模板 id 重复。</exception>
    public EffectCompiler(IEnumerable<EffectTemplate> templates, OpTemplateCatalog ops)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(ops);

        _templates = EffectTemplateLoader.Index(templates);
        _ops = ops;
    }

    /// <summary>编译 DSL 实例为效果快照。</summary>
    /// <exception cref="ArgumentNullException">dsl 为 null。</exception>
    /// <exception cref="ArgumentException">effectId 空白。</exception>
    /// <exception cref="InvalidOperationException">模板缺失、槽位覆盖不全、op/参数非法或事件无 handler 来源。</exception>
    public EffectSnapshot Compile(DslEffectInstance dsl, string effectId, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(dsl);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectId);

        if (!_templates.TryGetValue(dsl.Template, out var template))
        {
            throw new InvalidOperationException($"找不到模板效果：'{dsl.Template}'。");
        }

        var handlers = ResolveHandlers(template, dsl);

        var mainTrigger = BuildTrigger(template.Root.MainTrigger, template, handlers);
        var others = template.Root.OtherTriggers.Select(trigger => BuildTrigger(trigger, template, handlers)).ToList();

        var wrappers = template.Root.Modings.Select(m =>
        {
            if (string.IsNullOrWhiteSpace(m.Replacement.Csx))
            {
                throw new InvalidOperationException($"模板 '{template.Id}' 的 moding 替换事件 '{m.Replacement.Id}' 缺少 csx。");
            }

            return new WrapperPrefab("replace", m.TargetEventId, null, new EventPrefab(m.Replacement.Id, m.Replacement.Entry, csxSource: m.Replacement.Csx));
        }).ToList();

        var injects = template.Root.Injects
            .Select(i => new InjectPrefab(i.TargetTriggerName, i.BandName, i.EventId, i.Priority))
            .ToList();

        var prefab = new EffectPrefab(effectId, mainTrigger, others, wrappers, version, injects);
        return new EffectSnapshot(prefab);
    }

    /// <summary>编译并序列化为效果快照 JSON 文本。</summary>
    /// <exception cref="ArgumentNullException">dsl 为 null。</exception>
    public string CompileToJson(DslEffectInstance dsl, string effectId, bool indented = true, int version = 1) =>
        PrefabJson.Serialize(Compile(dsl, effectId, version), indented);

    private Dictionary<(EffectTemplateTrigger, int), string> ResolveHandlers(EffectTemplate template, DslEffectInstance dsl)
    {
        var declared = new HashSet<string>(template.Slots.Select(slot => slot.Name), StringComparer.Ordinal);

        foreach (var key in dsl.Fills.Keys)
        {
            if (!declared.Contains(key))
            {
                throw new InvalidOperationException($"DSL 槽位 '{key}' 不在模板 '{template.Id}' 的槽位声明内。");
            }
        }

        var handlers = new Dictionary<(EffectTemplateTrigger, int), string>();
        foreach (var slot in template.Slots)
        {
            if (!dsl.Fills.TryGetValue(slot.Name, out var fill))
            {
                throw new InvalidOperationException($"模板 '{template.Id}' 的槽位 '{slot.Name}' 未被 DSL 填写。");
            }

            if (!EffectTemplateSlots.TryResolve(template, slot, out var reference, out var error))
            {
                throw new InvalidOperationException(error);
            }

            handlers[(reference.Trigger, reference.EventIndex)] = RenderBody(template, slot.Name, reference.Trigger, fill);
        }

        return handlers;
    }

    private string RenderBody(EffectTemplate template, string slotName, EffectTemplateTrigger trigger, DslSlotFill fill)
    {
        var builder = new StringBuilder();
        foreach (var op in fill.Ops)
        {
            var errors = DslOpRegistry.Validate(op);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"模板 '{template.Id}' 的槽位 '{slotName}' 含非法 op：{string.Join(" ", errors)}");
            }

            if (!_ops.Has(op.Op))
            {
                throw new InvalidOperationException($"模板 '{template.Id}' 的槽位 '{slotName}' 的 op '{op.Op}' 无 csx 语句模板。");
            }

            var rendered = _ops.Render(op);
            if (op.Condition is null)
            {
                builder.AppendLine(rendered);
                continue;
            }

            // 条件（A）：owner.*＝真实 csx 守卫；raw＝占位（if (false /* TODO */)）。
            builder.Append("if (").Append(ConditionExpression(op.Condition)).AppendLine(")");
            builder.AppendLine("{");
            foreach (var line in rendered.Split('\n'))
            {
                builder.Append("    ").AppendLine(line.TrimEnd('\r'));
            }

            builder.AppendLine("}");
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>受控比较运算（白名单——条件表达式直接拼进 csx，不允许任意文本）。</summary>
    private static readonly HashSet<string> ComparisonOps =
        new(StringComparer.Ordinal) { ">=", ">", "<=", "<", "==" };

    /// <summary>
    /// "宿主方控制 {types} 数量 {cmp}"：经 <c>orcRuntime.OwnerUnits</c> 的同步只读面统计。
    /// 类型集合或比较运算非法＝条件恒假（不抛——表达式渲染期不该因单条条件打断整卡编译）。
    /// </summary>
    private static string OwnerControlsTypeExpression(DslCondition condition)
    {
        var types = condition.UnitTypes;
        if (types is null || types.Count == 0 || condition.Comparison is not { } comparison
            || !ComparisonOps.Contains(comparison) || condition.Value is not { } value)
        {
            return "false";
        }

        var list = string.Join(", ", types.Select(t => "\"" + t.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
        // 守卫在动作块之外求值——此处 `runtime` 局部变量尚未声明，必须自包含解析；
        // 类型一律全限定并走 `as` 下转，不依赖 csx 头部 `Card` 的命名空间解析。
        // （历史上这里写的是不存在的 `orcRuntime`，因条件字段曾被 DslJson 丢弃而从未真正执行。）
        const string host = "(self as Orc.Game.Cards.CardBase)";
        return $"({host} is not null && {RuntimeType}.ResolveFor({host}) is {{ }} conditionRuntime"
            + $" && conditionRuntime.OwnerUnits({host}).Count(u => (u as Orc.Game.Cards.CardBase)?.Definition.UnitTypes.Any(t => new[] {{{list}}}.Contains(t)) == true) {comparison} {value})";
    }

    private static string ConditionExpression(DslCondition condition) => condition.Kind switch
    {
        // 事件卡取值：card.* 载荷在 Card；unit.* 载荷在 Unit（E1-34——否则 unit.* 系守卫恒假）。
        DslCondition.OwnerSame =>
            $"(view.Card ?? view.Unit) is {CardBaseType} actorEvent && self is {CardBaseType} actorSelf"
            + " && actorSelf.Owner is not null && object.ReferenceEquals(actorSelf.Owner, actorEvent.Owner)",
        DslCondition.OwnerDifferent =>
            $"!((view.Card ?? view.Unit) is {CardBaseType} actorEvent && self is {CardBaseType} actorSelf"
            + " && actorSelf.Owner is not null && object.ReferenceEquals(actorSelf.Owner, actorEvent.Owner))",
        // E1-39：载荷**只有玩家**（slot.gained/slot.lost）时按"宿主玩家 vs 载荷玩家"比较（事件卡面缺失，故取 Player）。
        DslCondition.OwnerSameByPlayer =>
            $"view.Player is {PlayerType} actorPlayer && self is {CardBaseType} actorSelf"
            + " && actorSelf.Owner is not null && object.ReferenceEquals(actorSelf.Owner, actorPlayer)",
        DslCondition.OwnerDifferentByPlayer =>
            $"!(view.Player is {PlayerType} actorPlayer && self is {CardBaseType} actorSelf"
            + " && actorSelf.Owner is not null && object.ReferenceEquals(actorSelf.Owner, actorPlayer))",
        // E1-42/E1-54：事件卡属性过滤（`Raw` ＝ `维度:取值`）——真实 csx 守卫。
        DslCondition.EventCardFilter => EventCardFilterExpression(condition.Raw),
        // E1-47：载荷字段**自指**（`Raw` ＝ 视图属性名）——`object.ReferenceEquals(view.X, self)`。
        DslCondition.PayloadSelf =>
            $"object.ReferenceEquals(view.{ViewProperty(condition.Raw)}, self)",
        // E1-47：载荷字段**是 HQ**（`Raw` ＝ 视图属性名）——`view.X is Hq`。
        DslCondition.PayloadIsHq =>
            $"view.{ViewProperty(condition.Raw)} is {HqType}",
        // E1-50：载荷字段的**归属面**（`Raw` ＝ 视图属性名）——`(view.X as CardBase)?.Owner` 与 `self.Owner` 同一性。
        DslCondition.PayloadOwnerSame =>
            PayloadOwnerExpression(condition.Raw),
        DslCondition.PayloadOwnerDifferent =>
            "!(" + PayloadOwnerExpression(condition.Raw) + ")",
        // S9：宿主方控制指定单位类型数量——同步只读面统计，不依赖交互选择器。
        DslCondition.OwnerControlsType => OwnerControlsTypeExpression(condition),
        // E1-42：合取（`all`）——`(a && b && …)`；空＝恒真。
        // E1-57：**数值比较条件**（真实求值；`Raw` ＝ 规范串）——求值是纯函数 ⇒ 可参与 `&&` 合取。
        DslCondition.Compare => ComparisonSpec.IsValid(condition.Raw)
            ? RenderCompareCondition(condition.Raw!)
            : "false /* TODO 条件规范串非法 */",
        // S3：**回合归属类条件**（`Raw` ＝ `friendly`／`enemy`）——真实求值（既有回合归属判定面）。
        DslCondition.TurnOwner => string.Equals(condition.Raw, "enemy", StringComparison.Ordinal)
            ? $"{TurnRulesType}.IsOpponentTurn(self)"
            : $"{TurnRulesType}.IsOwnerTurn(self)",
        DslCondition.AllKind =>
            condition.All is { Count: > 0 }
                ? "(" + string.Join(" && ", condition.All.Select(ConditionExpression)) + ")"
                : "true",
        _ => $"false /* TODO 条件占位：{EscapeComment(condition.Raw)} */",
    };

    /// <summary>
    /// **数值比较条件**的求值表达式（E1-57 ＋ S3 增量）：
    /// <list type="bullet">
    ///   <item>`turns=` 度量（在场回合数——S3 新增）：**内联渲染**（求值挂钩既有读取面
    ///     <c>UnitCard.TurnsInPlay</c>——<c>EffectRuntime.EvaluateCondition</c> 不含该度量），
    ///     如 `turns=s=self:gte:#3` → <c>((self as …UnitCard)?.TurnsInPlay ?? 0) &gt;= 3</c>。</item>
    ///   <item>其余（`count=`／`points=`／`stat=`）：既有 <c>EffectRuntime.EvaluateCondition</c> 调用。</item>
    /// </list>
    /// </summary>
    private static string RenderCompareCondition(string spec)
    {
        if (spec.StartsWith("turns=", StringComparison.Ordinal))
        {
            var parts = spec.Split(':');
            var op = parts.Length == 3
                ? parts[1] switch
                {
                    "gte" => ">=",
                    "lte" => "<=",
                    "gt" => ">",
                    "lt" => "<",
                    "eq" => "==",
                    _ => null,
                }
                : null;
            if (op is null
                || parts[2].Length < 2
                || parts[2][0] != '#'
                || !int.TryParse(parts[2][1..], out var value))
            {
                return "false /* TODO 在场回合数条件右操作数非法 */";
            }

            return $"((self as {UnitCardType})?.TurnsInPlay ?? 0) {op} {value}";
        }

        return $"{RuntimeType}.EvaluateCondition(self, {CsStringLiteral(spec)})";
    }

    /// <summary>C# 字符串字面量（csx 片段用；键/标识均为受控短串）。</summary>
    private static string CsStringLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // csx 默认导入不含 Orc.Game.*，故游戏层类型一律**全限定**（否则编译失败——E1 编译校验会拦下）。
    private const string CardBaseType = "Orc.Game.Cards.CardBase";

    private const string PlayerType = "Orc.Game.Players.Player";

    private const string HqType = "Orc.Game.Players.Hq";

    private const string TagDataType = "Orc.Game.Cards.TagData";

    private const string RuntimeType = "Orc.Game.Effects.EffectRuntime";

    private const string UnitCardType = "Orc.Game.Cards.UnitCard";

    private const string TurnRulesType = "Orc.Game.EffectParsing.TurnRules";

    /// <summary>
    /// 视图属性名白名单（E1-47；`payload.self`/`payload.hq` 的 `Raw`）——**只允许已知可选面**，
    /// 防止拼出的 csx 片段引用不存在的属性（编译期即失败，此处提前收敛）。
    /// </summary>
    private static readonly HashSet<string> ViewProperties = new(StringComparer.Ordinal)
    {
        "Card", "Unit", "Killer", "Player", "Host", "Effect",
    };

    /// <summary>
    /// **事件卡属性过滤**的 csx 表达式（E1-54）：`Raw` ＝ `维度:取值`（取值经**枚举白名单**校验，
    /// 不把原文拼进代码）；各维度用**互不相同的模式变量名**——多条经 `all` 合取时会落在同一表达式里。
    /// </summary>
    private static string EventCardFilterExpression(string? spec)
    {
        var text = spec ?? string.Empty;
        var separator = text.IndexOf(':', StringComparison.Ordinal);
        var dimension = separator < 0 ? text : text[..separator];
        var value = separator < 0 ? string.Empty : text[(separator + 1)..];
        var card = $"(view.Card as {CardBaseType})";

        return dimension switch
        {
            "keyword" =>
                $"{card} is {{ }} actorEventKeyword && actorEventKeyword.Keywords.Has({CsStringLiteral(value)})",
            "tag" =>
                $"{card} is {{ }} actorEventTagCard && actorEventTagCard.TryGetData<{TagDataType}>(out var actorEventTags)"
                + $" && actorEventTags.ContainsTag({CsStringLiteral(value)})",
            "category" => Orc.Game.Cards.CardCategory.TryNormalize(value, out var categoryId)
                ? $"{card} is {{ }} actorEventCategoryCard && actorEventCategoryCard.Definition.Category == {CsStringLiteral(categoryId)}"
                : "false /* TODO 未知卡类型 */",
            "faction" => Orc.Game.Cards.Faction.TryNormalize(value, out var factionId)
                ? $"{card} is {{ }} actorEventFactionCard && actorEventFactionCard.Definition.Faction == {CsStringLiteral(factionId)}"
                : "false /* TODO 未知阵营 */",
            "name" =>
                $"{card} is {{ }} actorEventNameCard && string.Equals(actorEventNameCard.Name, {CsStringLiteral(value)}, System.StringComparison.Ordinal)",
            _ => "false /* TODO 未知事件卡过滤维度 */",
        };
    }

    /// <summary>枚举取值 → **全限定枚举字面量**（非法取值＝null——不把原文拼进代码）。</summary>
    private static string? EnumValue<TEnum>(string value) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? $"{typeof(TEnum).FullName}.{parsed}"
            : null;

    /// <summary>
    /// 载荷字段的归属比较表达式（E1-50）：`(view.X as CardBase)?.Owner` 与 `self.Owner` 同一性
    /// （字段非卡/字段为空/任一 Owner 为空＝假）。
    /// </summary>
    private static string PayloadOwnerExpression(string? name)
    {
        var property = ViewProperty(name);
        return $"(view.{property} as {CardBaseType})?.Owner is {{ }} actorEventOwner"
            + $" && self is {CardBaseType} actorSelf && actorSelf.Owner is not null"
            + " && object.ReferenceEquals(actorSelf.Owner, actorEventOwner)";
    }

    private static string ViewProperty(string? name) =>
        name is not null && ViewProperties.Contains(name) ? name : "Card";

    private static string EscapeComment(string? text) =>
        text?.Replace("*/", "* /", StringComparison.Ordinal) ?? string.Empty;

    private static TriggerPrefab BuildTrigger(
        EffectTemplateTrigger trigger,
        EffectTemplate template,
        IReadOnlyDictionary<(EffectTemplateTrigger, int), string> handlers)
    {
        var events = new List<EventPrefab>(trigger.Events.Count);
        for (var i = 0; i < trigger.Events.Count; i++)
        {
            var ev = trigger.Events[i];
            string? csx;
            if (handlers.TryGetValue((trigger, i), out var body))
            {
                csx = WrapHandler(trigger.ViewTypeName, ev.Entry, body, template.ActorFrom);
            }
            else
            {
                csx = ev.Csx;
            }

            if (string.IsNullOrWhiteSpace(csx))
            {
                throw new InvalidOperationException(
                    $"模板 '{template.Id}' 的事件 '{trigger.Id}.{ev.Id}' 无 handler 来源（既非槽位、又无固定 csx）。");
            }

            events.Add(new EventPrefab(ev.Id, ev.Entry, csxSource: csx));
        }

        return new TriggerPrefab(
            trigger.Id,
            trigger.StableKey,
            trigger.Kind,
            trigger.Hooks,
            events,
            viewTypeName: trigger.ViewTypeName);
    }

    /// <summary>
    /// 把渲染出的 body 包裹成规定入口变量（csx 入口必须是顶层具名变量），并按模板声明的
    /// <see cref="ActorFrom"/> 生成**施动卡变量 <c>self</c>**（op 模板统一使用 <c>self</c>）。
    /// </summary>
    private static string WrapHandler(string viewTypeName, string entryName, string body, ActorFrom actorFrom)
    {
        var builder = new StringBuilder();
        // 视图类型一律**全限定**（csx 默认导入不含游戏层命名空间，如 Orc.Game.Commanding）；
        // 模板里可能带**程序集限定**（内核 Type.GetType 对跨程序集名只认限定名）——此处剥掉 ", Assembly" 部分再写代码。
        var csTypeName = viewTypeName.Split(',')[0].Trim();
        builder.Append("Func<").Append(csTypeName)
            .Append(", Context, CancellationToken, Task> ").Append(entryName)
            .Append("")
            .AppendLine(" = async (view, ctx, ct) =>");
        builder.AppendLine("{");
        builder.Append("    var self = ").Append(
            actorFrom == ActorFrom.EffectHost
                ? "view.Host as Card;"
                : "view.Card as Card;").AppendLine();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            builder.Append("    ").AppendLine(trimmed);
        }

        builder.Append("};");
        return builder.ToString();
    }
}
