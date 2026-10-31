namespace Orc.HandlerDsl;

/// <summary>
/// 目录生成的**命名与合并规则**（SPEC §12.1–§12.2 的纯函数部分）：
/// 节点名派生、隐式宿主白名单、同名重载合并。
/// <para>
/// 这里是**契约侧的规则本身**，与反射扫描（S4）解耦：扫描器把方法签名转成
/// <see cref="ParameterSpec"/> 交给 <see cref="MergeOverloads"/>，规则只有一份。
/// </para>
/// <para>
/// 显式标注优先：节点名先取 <c>[HandlerDslNode]</c>，无标注才用 <see cref="DeriveNodeName"/>。
/// </para>
/// </summary>
public static class HandlerDslNaming
{
    private const string AsyncSuffix = "Async";

    /// <summary>
    /// 隐式宿主参数名的**封闭白名单**（不再加）：这些名字作为**首参**时不出引脚，
    /// 生成码固定用 <c>orcSelf</c>。判据是名字，不是位置。
    /// </summary>
    public static IReadOnlyList<string> ImplicitHostParameterNames { get; } = new[] { "host", "viewer", "self" };

    /// <summary>是否隐式宿主参数名（白名单封闭，SPEC §12.1）。</summary>
    public static bool IsImplicitHost(string name) =>
        !string.IsNullOrEmpty(name) && ImplicitHostParameterNames.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// 默认派生节点名：去 <c>Async</c> 后缀、首字母小写（<c>DamageAsync</c> → <c>damage</c>）。
    /// <para>
    /// **只作兜底**：冲突或改名必须有 <c>[HandlerDslNode("…")]</c> 显式名，显式名优先——
    /// 方法一改名，所有已发布草案就会 `node-unknown`，只有显式标注能把两者解耦。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">方法名为空或只有 Async 后缀。</exception>
    public static string DeriveNodeName(string methodName)
    {
        if (string.IsNullOrEmpty(methodName))
        {
            throw new ArgumentException("方法名不得为空。", nameof(methodName));
        }

        var name = methodName;
        if (name.Length > AsyncSuffix.Length && name.EndsWith(AsyncSuffix, StringComparison.Ordinal))
        {
            name = name[..^AsyncSuffix.Length];
        }

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>扫描器交给合并规则的参数描述（已按 SPEC 剔除 <c>CancellationToken</c> 与隐式宿主首参）。</summary>
    /// <param name="Name">参数名（＝引脚名）。</param>
    /// <param name="Type">引脚类型（封闭集）。</param>
    /// <param name="HasDefault">是否有默认值（有默认值→引脚可省略）。</param>
    public sealed record ParameterSpec(string Name, PinType Type, bool HasDefault = false);

    /// <summary>
    /// 同名重载合并为**一个节点**：参数取并集、只在部分重载出现的参数→可选；
    /// 同名参数在不同重载里**类型不同**＝目录生成失败（必须用标注拆成两个节点）。
    /// </summary>
    /// <param name="overloads">各重载的参数表（调用方已剔除 CancellationToken 与隐式宿主首参）。</param>
    /// <param name="error">失败原因（成功＝null）。</param>
    /// <returns>合并后的引脚（按首次出现顺序）；失败＝null。</returns>
    public static IReadOnlyList<DslPin>? MergeOverloads(
        IReadOnlyList<IReadOnlyList<ParameterSpec>> overloads,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(overloads);
        if (overloads.Count == 0)
        {
            throw new ArgumentException("至少需要一个重载。", nameof(overloads));
        }

        var order = new List<string>();
        var types = new Dictionary<string, PinType>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var defaults = new Dictionary<string, bool>(StringComparer.Ordinal);

        for (var index = 0; index < overloads.Count; index++)
        {
            var overload = overloads[index] ?? throw new ArgumentException($"第 {index} 个重载为 null。", nameof(overloads));
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var parameter in overload)
            {
                ArgumentNullException.ThrowIfNull(parameter);
                if (!seen.Add(parameter.Name))
                {
                    error = $"重载 #{index} 内重复参数 '{parameter.Name}'。";
                    return null;
                }

                if (types.TryGetValue(parameter.Name, out var existing))
                {
                    if (!existing.Equals(parameter.Type))
                    {
                        error = $"参数 '{parameter.Name}' 在不同重载里类型不同（{existing.Format()} vs {parameter.Type.Format()}）："
                            + "必须用 [HandlerDslNode] 标注拆成两个节点，不允许合并。";
                        return null;
                    }
                }
                else
                {
                    order.Add(parameter.Name);
                    types[parameter.Name] = parameter.Type;
                }

                occurrences[parameter.Name] = occurrences.GetValueOrDefault(parameter.Name) + 1;
                defaults[parameter.Name] = defaults.GetValueOrDefault(parameter.Name) || parameter.HasDefault;
            }
        }

        var pins = new List<DslPin>(order.Count);
        foreach (var name in order)
        {
            var required = occurrences[name] == overloads.Count && !defaults[name];
            pins.Add(new DslPin(name, types[name], required));
        }

        error = null;
        return pins;
    }
}
