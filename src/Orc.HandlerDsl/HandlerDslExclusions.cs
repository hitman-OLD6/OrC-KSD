namespace Orc.HandlerDsl;

/// <summary>排除条目的性质：**结构性排除**（现有规则＋标注都表达不了）／**延后**（能表达，本轮不做）。</summary>
public enum HandlerDslExclusionKind
{
    /// <summary>结构性：record 参数、复合结果、内建 op 的运行时入口——没有可用的表达手段。</summary>
    Structural,

    /// <summary>延后：**技术上可拆**（补标注即可进目录），只是本轮不做。</summary>
    Deferred,
}

/// <summary>
/// 排除清单的一条（R27）：**方法名 ＋ 理由 ＋ 替代路径**，三项缺一不可。
/// <para>
/// 语义固定：排除清单＝「**图形化表达不了**」；它不是 <c>docs/卡牌DIY开发文档/reference/缺口表.md</c>
/// （后者＝「引擎层面没有」）。两者可重叠，但判定标准不同，不得互相替代、不得合并（D10）。
/// </para>
/// <para>
/// <see cref="Kind"/> 区分「表达不了」与「能表达但这轮不做」——**理由写错会让后来者以为这条路走不通**，
/// 从而在别处重复踩坑。延后条目必须写明「补什么就能进目录」。
/// </para>
/// </summary>
/// <param name="Signature">方法签名键（<c>Name(参数类型短名, …)</c>；见 <see cref="HandlerDslExclusions.SignatureOf"/>）。</param>
/// <param name="Method">方法名（人类可读）。</param>
/// <param name="Reason">为什么默认映射规则 ＋ 标注都表达不了（不接受「暂不支持」）。</param>
/// <param name="Alternative">替代路径：节点组合（写具体 op 序列）或「只能脚本节点」。</param>
/// <param name="Kind">性质：结构性／延后。</param>
/// <param name="Priority">是否优先补（仅延后条目有意义）。</param>
public sealed record HandlerDslExclusion(
    string Signature,
    string Method,
    string Reason,
    string Alternative,
    HandlerDslExclusionKind Kind = HandlerDslExclusionKind.Structural,
    bool Priority = false);

/// <summary>
/// 排除清单（v1 初版）——**代码侧唯一真源**，<c>contracts/handler-dsl/v1/EXCLUSIONS.md</c> 是它的人读镜像
/// （由 <c>ExclusionListTests</c> 断言两侧条目集一致，杜绝「文档写了、代码没排除」与反向漂移）。
/// <para>
/// **守护测试（S5／D8／D10）：一个测试、三态判定**——遍历集＝带 <c>[HandlerDslActionFace]</c> 的类型，
/// 对其中每个 public 方法断言**三态必居其一**：①走默认规则能生成 ／ ②有标注 ／ ③在本清单里。
/// 三者都不满足＝测试失败（错误信息给出方法名与建议）。这同时承担 R27「清单不许悄悄变长」的守护。
/// </para>
/// <para>
/// 本清单**不承担「发现新门面」**——那是 <c>*ActionFace</c> 漏标断言的职责（D10：两者职责不得混）。
/// </para>
/// </summary>
public static class HandlerDslExclusions
{
    /// <summary>全部条目（顺序稳定，供与 EXCLUSIONS.md 比对）。</summary>
    public static IReadOnlyList<HandlerDslExclusion> All { get; } = new[]
    {
        new HandlerDslExclusion(
            "ResolveFor(Card)",
            "ResolveFor",
            "静态工厂，返回运行时实例（EffectRuntime）；不是状态变更动作，返回值也不在封闭集内。",
            "生成码内部使用（动作节点调用的接收者解析），作者无需在图上调用。"),

        new HandlerDslExclusion(
            "Judge(string, object[])",
            "Judge",
            "变参 ＋ object 载荷，封闭集表达不了；判定器名字面是另一套符号系统（JudicatorNames）。",
            "用 read ＋ 比较算子组合（eq／lt／ge…）表达同一条件，或改用脚本节点。"),

        new HandlerDslExclusion(
            "EvaluateCondition(Card, string)",
            "EvaluateCondition",
            "入参是条件表达式字符串（`count=s=enemy:gte:#2` 一类），等于在 DSL 里开第二套 DSL（R27 明令不许）。",
            "用表达式树表达同一条件（read ＋ 比较算子 ＋ and／or 组合）。"),

        new HandlerDslExclusion(
            "AurasOf(Card)",
            "AurasOf",
            "返回 AuraRegistry（引擎对象），不在封闭集内；且它只是取注册表，本身不是动作。",
            "只能脚本节点；常规持续态走 DeclareAuraAsync 的动作节点。"),

        new HandlerDslExclusion(
            "SelectAsync(Card, EffectSelector, CancellationToken)",
            "SelectAsync",
            "EffectSelector 是 record 参数（含阈值与阵营多字段），默认规则与标注都表达不了；且它是交互语义。",
            "用 ask 节点（R23）；纯查询另由 read／聚合算子表达。"),

        new HandlerDslExclusion(
            "DeclareAuraAsync(Card, string, int, EffectAuraFilter, CancellationToken)",
            "DeclareAuraAsync",
            "EffectAuraFilter 是 record 参数（阵营／兵种／词条／区域／排除自身五字段），"
            + "封闭集表达不了，也没有把 record 展开成多引脚的标注（R16 明确不提供结构体）。",
            "只能脚本节点；或改用 buff 动作节点表达一次性修饰。"),

        new HandlerDslExclusion(
            "AskAsync(Card, string, string, string, string, string, CancellationToken)",
            "AskAsync",
            "内建 ask 节点的**运行时实现**（交互通道降级策略的落点），不是给作者连线的原语。",
            "用 ask 节点：本方法只由生成码调用。"),

        new HandlerDslExclusion(
            "ChooseAsync(Card, string, string[], CancellationToken)",
            "ChooseAsync",
            "内建 choice 节点的**运行时实现**（选项呈现），不是给作者连线的原语。",
            "用 choice 节点：本方法只由生成码调用。"),

        new HandlerDslExclusion(
            "ReadValue(Card, string, string)",
            "ReadValue",
            "read 变体的**运行时读取入口**（read.path → 实时值），不是给作者连线的原语。",
            "用 read 变体：本方法只由生成码调用。"),

        new HandlerDslExclusion(
            "RevealAsync(Card, CancellationToken, Card)",
            "RevealAsync",
            "返回 RevealOutcome——**可以**用 [HandlerDslResult] 拆成标量输出（如 `status:string:enum`），"
            + "**本轮未拆**（不是结构上拆不了）；按 R25 第 3 条，结果对象未标注＝目录生成失败，故本轮先排除。",
            "补 [HandlerDslResult] 拆解后即可进目录（本轮未做）；在此之前只能脚本节点。",
            HandlerDslExclusionKind.Deferred),

        new HandlerDslExclusion(
            "JoinFrontlineAsync(Card, UnitCard, int?, CancellationToken)",
            "JoinFrontlineAsync",
            "返回 ActionResult?——**可以**用 [HandlerDslResult] 拆（如 `status:string` ＋ 目标数），"
            + "**本轮未拆**（不是结构上拆不了）；未标注时目录生成失败，故本轮先排除。",
            "补 [HandlerDslResult] 拆解后即可进目录——这是「把单位塞进战场」的核心动作，**优先补**。",
            HandlerDslExclusionKind.Deferred,
            Priority: true),

        new HandlerDslExclusion(
            "JoinSupportLineAsync(Card, UnitCard, int?, CancellationToken)",
            "JoinSupportLineAsync",
            "同 JoinFrontlineAsync：返回 ActionResult?，**可以**用 [HandlerDslResult] 拆，**本轮未拆**。",
            "补 [HandlerDslResult] 拆解后即可进目录——核心动作，**优先补**。",
            HandlerDslExclusionKind.Deferred,
            Priority: true),

        new HandlerDslExclusion(
            "OwnerUnits(CardBase)",
            "OwnerUnits",
            "**查询**而非动作：首参 host 是「查谁的」这个数据参数，不是执行宿主；"
            + "却因名字落在隐式宿主白名单（host／viewer／self）里被当成了服务宿主，"
            + "生成的 host: orcSelf 还附加 Card→CardBase 的向下转换失败。",
            "用 read（属性读取）或脚本节点；若要「遍历某人拥有的单位」，"
            + "应由新增的只读 path／目录节点承担，而不是伪装成动作节点。"),
    };

    /// <summary>签名键形式：<c>Name(参数类型短名, …)</c>（参数类型只用短名，避免反射全名的程序集版本噪声）。</summary>
    public static string SignatureOf(string methodName, IEnumerable<string> parameterTypeNames) =>
        $"{methodName}({string.Join(", ", parameterTypeNames)})";

    /// <summary>按签名键查条目。</summary>
    public static bool TryFind(string signature, out HandlerDslExclusion exclusion)
    {
        foreach (var entry in All)
        {
            if (string.Equals(entry.Signature, signature, StringComparison.Ordinal))
            {
                exclusion = entry;
                return true;
            }
        }

        exclusion = null!;
        return false;
    }
}
