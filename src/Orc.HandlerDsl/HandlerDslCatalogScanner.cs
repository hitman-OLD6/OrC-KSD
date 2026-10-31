using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Orc.Game.Effects.HandlerDsl;

namespace Orc.HandlerDsl;

/// <summary>一个动作面方法在扫描中的归宿（S5 三态判定 ＋ 失败）。</summary>
public enum HandlerDslMethodOutcome
{
    /// <summary>① 走默认映射规则就能生成节点。</summary>
    Generated,

    /// <summary>② 有标注（节点名／引脚／结果拆解／取值集／子类型）。</summary>
    Annotated,

    /// <summary>③ 在排除清单里（不生成节点）。</summary>
    Excluded,

    /// <summary>三者都不满足＝扫描期失败（不静默丢弃）。</summary>
    Failed,
}

/// <summary>一个动作面方法的扫描结论（供守护测试逐条断言三态必居其一）。</summary>
/// <param name="Face">门面类型全名。</param>
/// <param name="Method">方法名。</param>
/// <param name="Signature">签名键（与排除清单同一格式）。</param>
/// <param name="NodeName">派生／标注出的节点名（失败或排除时可能为 null）。</param>
/// <param name="Outcome">归宿。</param>
/// <param name="Detail">失败原因（成功＝null）。</param>
public sealed record HandlerDslMethodScan(
    string Face,
    string Method,
    string Signature,
    string? NodeName,
    HandlerDslMethodOutcome Outcome,
    string? Detail);

/// <summary>扫描结果：目录 ＋ 逐方法归宿 ＋ 失败清单（失败＝目录不可用）。</summary>
/// <param name="Catalog">扫描出的目录（失败时为 null）。</param>
/// <param name="Methods">逐方法归宿。</param>
/// <param name="Errors">目录生成失败的原因（每条指名方法）。</param>
/// <param name="ScannedFaces">扫描输入集（门面类型全名）。</param>
public sealed record HandlerDslCatalogScanResult(
    DslCatalog? Catalog,
    IReadOnlyList<HandlerDslMethodScan> Methods,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> ScannedFaces)
{
    /// <summary>是否扫描成功。</summary>
    public bool Success => Errors.Count == 0 && Catalog is not null;
}

/// <summary>
/// **节点目录的扫描生成**（S4／SPEC §12）：把「带 <c>[HandlerDslActionFace]</c> 的类型」的 public 方法
/// 按默认映射规则 ＋ 标注，生成 <see cref="DslCatalog"/>。
/// <para>
/// 扫描输入集＝**显式登记的门面类型集合**（D10），不走鸭子类型判据：内部步骤方法、测试辅助、事件处理器
/// 不会被误扫进来。静态门面（如 <c>KeywordEffectActionFace</c>）扫其 public **静态**方法；
/// 实例门面（如 <c>EffectRuntime</c>）扫其 public **实例**方法。
/// </para>
/// <para>
/// 规则来源是 <see cref="HandlerDslNaming"/>（纯函数）：命名派生、隐式宿主白名单、重载合并只有一份，
/// 本类只负责反射与类型映射。
/// </para>
/// <para>
/// **<c>catalogRevision</c> 由内容派生**（稳定、可复现）：把扫描结果的规范化文本取 SHA-256 的前 4 字节作正整数。
/// 同一份代码两次扫描必得同一个 revision；内容一变就变——不需要人工维护版本号。
/// </para>
/// </summary>
public static class HandlerDslCatalogScanner
{
    /// <summary>实体类型名（按简单名匹配；本程序集不引用 Orc.Cards／Orc.Game，故用名字而非类型引用）。</summary>
    private static readonly HashSet<string> EntityTypeNames =
        new(StringComparer.Ordinal) { "Card", "CardBase", "UnitCard", "Hq", "Entity" };

    /// <summary>数字类型名。</summary>
    private static readonly HashSet<string> NumberTypeNames =
        new(StringComparer.Ordinal) { "Int32", "Int64", "Double", "Single", "Decimal", "Int16", "Byte" };

    /// <summary>集合类型名（元素类型再按封闭集映射）。</summary>
    private static readonly HashSet<string> ListTypeNames =
        new(StringComparer.Ordinal)
        {
            "IReadOnlyList`1", "IList`1", "List`1", "IEnumerable`1", "ICollection`1", "IReadOnlyCollection`1", "Card[]",
        };

    /// <summary>
    /// 扫描给定程序集里的全部已登记门面。
    /// </summary>
    /// <param name="assemblies">要扫描的程序集（顺序不影响结果：内部按类型全名排序）。</param>
    /// <exception cref="ArgumentNullException">assemblies 为 null。</exception>
    public static HandlerDslCatalogScanResult Scan(params Assembly[] assemblies) =>
        Scan((IEnumerable<Assembly>)assemblies);

    /// <summary>
    /// 扫描给定程序集里的全部已登记门面。
    /// </summary>
    /// <param name="assemblies">要扫描的程序集。</param>
    /// <exception cref="ArgumentNullException">assemblies 为 null。</exception>
    public static HandlerDslCatalogScanResult Scan(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var faceTypes = new List<Type>();
        foreach (var assembly in assemblies.Distinct())
        {
            ArgumentNullException.ThrowIfNull(assembly);
            foreach (var type in assembly.GetTypes())
            {
                if ((type.IsPublic || type.IsNestedPublic)
                    && type.GetCustomAttribute<HandlerDslActionFaceAttribute>(inherit: false) is not null)
                {
                    faceTypes.Add(type);
                }
            }
        }

        faceTypes.Sort(static (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        return ScanTypes(faceTypes);
    }

    /// <summary>
    /// 扫描**给定的门面类型集合**（调用方已按 <c>[HandlerDslActionFace]</c> 筛好）。
    /// <para>分离出来的理由：测试要能对单个合成门面扫描（不被同程序集里的其它门面干扰）。</para>
    /// </summary>
    /// <param name="faceTypes">门面类型集合（顺序不影响结果：内部按类型全名排序）。</param>
    /// <exception cref="ArgumentNullException">faceTypes 为 null。</exception>
    public static HandlerDslCatalogScanResult ScanTypes(IEnumerable<Type> faceTypes)
    {
        ArgumentNullException.ThrowIfNull(faceTypes);

        var ordered = faceTypes.Distinct().ToList();
        foreach (var type in ordered)
        {
            ArgumentNullException.ThrowIfNull(type);
            if (type.GetCustomAttribute<HandlerDslActionFaceAttribute>(inherit: false) is null)
            {
                throw new ArgumentException(
                    $"类型 '{type.FullName}' 未标 [HandlerDslActionFace]：扫描输入集＝显式登记的门面（D10）。",
                    nameof(faceTypes));
            }
        }

        ordered.Sort(static (a, b) => string.CompareOrdinal(a.FullName, b.FullName));

        var scannedFaces = ordered.Select(static t => t.FullName ?? t.Name).ToArray();
        var scans = new List<HandlerDslMethodScan>();
        var errors = new List<string>();
        var groups = new Dictionary<string, List<MethodScaffold>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();

        foreach (var face in ordered)
        {
            var isStaticFace = face.IsAbstract && face.IsSealed;
            var flags = BindingFlags.Public | BindingFlags.DeclaredOnly
                | (isStaticFace ? BindingFlags.Static : BindingFlags.Instance);

            var methods = face.GetMethods(flags)
                .Where(static m => !m.IsSpecialName && !m.IsGenericMethodDefinition)
                .OrderBy(static m => m.Name, StringComparer.Ordinal)
                .ThenBy(static m => m.GetParameters().Length)
                .ThenBy(static m => SignatureOf(m), StringComparer.Ordinal)
                .ToArray();

            foreach (var method in methods)
            {
                var signature = SignatureOf(method);
                var annotated = HasAnnotation(method, out var annotationKinds);

                if (HandlerDslExclusions.TryFind(signature, out var exclusion))
                {
                    scans.Add(new HandlerDslMethodScan(
                        face.FullName ?? face.Name, method.Name, signature, NodeNameOf(method),
                        HandlerDslMethodOutcome.Excluded, exclusion.Reason));
                    continue;
                }

                var scaffold = BuildScaffold(face, method, isStaticFace, out var failure);
                if (scaffold is null)
                {
                    errors.Add(failure!);
                    scans.Add(new HandlerDslMethodScan(
                        face.FullName ?? face.Name, method.Name, signature, NodeNameOf(method),
                        HandlerDslMethodOutcome.Failed, failure));
                    continue;
                }

                scans.Add(new HandlerDslMethodScan(
                    face.FullName ?? face.Name, method.Name, signature, scaffold.NodeName,
                    annotated ? HandlerDslMethodOutcome.Annotated : HandlerDslMethodOutcome.Generated,
                    annotated ? annotationKinds : null));

                if (!groups.TryGetValue(scaffold.NodeName, out var bucket))
                {
                    bucket = new List<MethodScaffold>();
                    groups.Add(scaffold.NodeName, bucket);
                    groupOrder.Add(scaffold.NodeName);
                }

                bucket.Add(scaffold);
            }
        }

        if (errors.Count > 0 && groups.Count == 0)
        {
            return new HandlerDslCatalogScanResult(null, scans, errors, scannedFaces);
        }

        var nodes = new List<DslNodeSignature>(groupOrder.Count);
        foreach (var name in groupOrder)
        {
            if (HandlerDslContract.IsBuiltinOp(name))
            {
                errors.Add($"节点名 '{name}' 与内建 op 冲突：10 个内建 op 与扫描名必须不相交（SPEC §3）。");
                continue;
            }

            if (HandlerDslContextNodes.IsContextOp(name))
            {
                errors.Add($"节点名 '{name}' 是上下文原语保留名（SPEC §9.2），动作面方法不得占用。");
                continue;
            }

            var bucket = groups[name];
            var merged = HandlerDslNaming.MergeOverloads(
                bucket.Select(static s => (IReadOnlyList<HandlerDslNaming.ParameterSpec>)s.Pins.Select(static p => p.Spec).ToArray()).ToArray(),
                out var mergeError);
            if (merged is null)
            {
                errors.Add($"节点 '{name}' 的重载无法合并：{mergeError}");
                continue;
            }

            var first = bucket[0];
            var pins = new List<DslPin>(merged.Count);
            foreach (var pin in merged)
            {
                var meta = bucket.SelectMany(static s => s.Pins).First(p => p.Spec.Name == pin.Name);
                pins.Add(new DslPin(pin.Name, pin.Type, pin.Required, meta.EnumValues, meta.EnumType));
            }

            nodes.Add(new DslNodeSignature(
                name,
                pins,
                first.Outputs,
                hasAwait: first.HasAwait,
                hasSideEffect: first.HasSideEffect,
                face: first.FaceShortName,
                method: first.Method,
                implicitHost: first.ImplicitHost,
                isStatic: first.IsStatic,
                faceType: first.FaceType,
                cancellationTokenParameter: first.CancellationTokenParameter));
        }

        if (errors.Count > 0)
        {
            return new HandlerDslCatalogScanResult(null, scans, errors, scannedFaces);
        }

        var catalog = new DslCatalog(RevisionOf(nodes, scannedFaces), nodes, DslReadables.V1, scannedFaces);
        return new HandlerDslCatalogScanResult(catalog, scans, Array.Empty<string>(), scannedFaces);
    }

    /// <summary>
    /// 内容派生 <c>catalogRevision</c>：节点（名字、引脚、可选性、类型、枚举、子类型、来源）的规范化文本
    /// 取 SHA-256 前 4 字节 → 正整数。同内容必同 revision（可复现）；空目录＝0。
    /// </summary>
    /// <param name="nodes">节点清单（顺序无关：内部按名排序）。</param>
    /// <param name="scannedFaces">门面清单（顺序无关：内部排序）。</param>
    public static int RevisionOf(IEnumerable<DslNodeSignature> nodes, IReadOnlyList<string>? scannedFaces = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var builder = new StringBuilder();
        foreach (var face in (scannedFaces ?? Array.Empty<string>()).OrderBy(static f => f, StringComparer.Ordinal))
        {
            builder.Append("face:").Append(face).Append('\n');
        }

        foreach (var node in nodes.OrderBy(static n => n.Name, StringComparer.Ordinal))
        {
            builder.Append("node:").Append(node.Name)
                .Append('|').Append(node.ImplicitHost ?? "-")
                .Append('|').Append(node.IsStatic ? "static" : "instance")
                .Append('|').Append(node.Method ?? "-")
                .Append('\n');

            foreach (var pin in node.Inputs)
            {
                builder.Append("  in:").Append(pin.Name).Append(':').Append(pin.Type.Format())
                    .Append(pin.Required ? ":required" : ":optional")
                    .Append(pin.EnumType is null ? string.Empty : ":" + pin.EnumType)
                    .Append(pin.EnumValues.Count == 0 ? string.Empty : ":enum=" + string.Join(",", pin.EnumValues))
                    .Append('\n');
            }

            foreach (var pin in node.Outputs)
            {
                builder.Append("  out:").Append(pin.Name).Append(':').Append(pin.Type.Format())
                    .Append(pin.Type.Subtypes.Count == 0 ? string.Empty : ":sub=" + string.Join(",", pin.Type.Subtypes))
                    .Append('\n');
            }
        }

        if (builder.Length == 0)
        {
            return 0;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        var revision = (hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3];
        return revision == 0 ? 1 : Math.Abs(revision);
    }

    /// <summary>方法签名键（与 <see cref="HandlerDslExclusions.SignatureOf"/> 同格式）。</summary>
    public static string SignatureOf(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return HandlerDslExclusions.SignatureOf(
            method.Name,
            method.GetParameters().Select(static p => TypeName(p.ParameterType)));
    }

    /// <summary>类型名（C# 关键字形态；可空保留 <c>?</c>；数组保留 <c>[]</c>）。</summary>
    private static string TypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null)
        {
            return TypeName(underlying) + "?";
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[]";
        }

        return type.Name switch
        {
            "Int32" => "int",
            "Int64" => "long",
            "Int16" => "short",
            "Byte" => "byte",
            "Double" => "double",
            "Single" => "float",
            "Decimal" => "decimal",
            "Boolean" => "bool",
            "String" => "string",
            "Object" => "object",
            "Void" => "void",
            _ => type.Name,
        };
    }

    /// <summary>节点名：<c>[HandlerDslNode]</c> 显式名优先，否则默认派生（去 Async、首字母小写）。</summary>
    private static string? NodeNameOf(MethodInfo method) =>
        method.GetCustomAttribute<HandlerDslNodeAttribute>(inherit: false)?.Name
        ?? HandlerDslNaming.DeriveNodeName(method.Name);

    private static bool HasAnnotation(MethodInfo method, out string kinds)
    {
        var found = new List<string>();
        if (method.GetCustomAttribute<HandlerDslNodeAttribute>(inherit: false) is not null)
        {
            found.Add("node");
        }

        if (method.GetCustomAttribute<HandlerDslResultAttribute>(inherit: false) is not null)
        {
            found.Add("result");
        }

        if (method.GetCustomAttribute<HandlerDslEnumValuesAttribute>(inherit: false) is not null
            || method.ReturnParameter.GetCustomAttribute<HandlerDslEnumValuesAttribute>(inherit: false) is not null)
        {
            found.Add("enum");
        }

        if (method.ReturnParameter.GetCustomAttribute<HandlerDslSubtypeAttribute>(inherit: false) is not null)
        {
            found.Add("subtype");
        }

        if (method.GetParameters().Any(static p => p.GetCustomAttribute<HandlerDslPinAttribute>(inherit: false) is not null))
        {
            found.Add("pin");
        }

        if (method.GetParameters().Any(static p => p.GetCustomAttribute<HandlerDslEnumValuesAttribute>(inherit: false) is not null))
        {
            found.Add("enum");
        }

        if (method.GetParameters().Any(static p => p.GetCustomAttribute<HandlerDslSubtypeAttribute>(inherit: false) is not null))
        {
            found.Add("subtype");
        }

        kinds = found.Count == 0 ? string.Empty : string.Join("／", found.Distinct(StringComparer.Ordinal));
        return found.Count > 0;
    }

    private static MethodScaffold? BuildScaffold(
        Type face, MethodInfo method, bool isStaticFace, out string? failure)
    {
        failure = null;
        var parameters = method.GetParameters();
        var specs = new List<PinMeta>(parameters.Length);
        string? implicitHost = null;
        string? cancellationTokenParameter = null;

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];

            // CancellationToken 恒不出引脚（SPEC §12.1），但**必须记下形参名**：
            // 生成码要把它原样传下去，否则动作调用恒拿 default(CancellationToken)，
            // 取消与超时永远传导不到动作内部（位置不固定，故生成码用命名实参）。
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                cancellationTokenParameter ??= parameter.Name;
                continue;
            }

            // 隐式宿主：**首参**且名字在白名单内（判据是名字，不是位置——位置是「首参」这一条）。
            if (index == 0 && HandlerDslNaming.IsImplicitHost(parameter.Name))
            {
                implicitHost = parameter.Name;
                continue;
            }

            var pin = parameter.GetCustomAttribute<HandlerDslPinAttribute>(inherit: false);
            var pinName = pin?.Name ?? parameter.Name ?? $"arg{index}";
            var required = pin?.Required ?? !parameter.HasDefaultValue;

            PinType type;
            List<string> enumValues = new();
            string? enumType = null;

            if (pin?.Type is { Length: > 0 } explicitType)
            {
                if (!PinType.TryParse(explicitType, out type))
                {
                    failure = $"方法 '{method.Name}' 的参数 '{parameter.Name}' 用标注给出的引脚类型 '{explicitType}' "
                        + $"超出封闭集：只允许 {string.Join("／", PinType.CanonicalNames)}。";
                    return null;
                }
            }
            else if (!TryMap(parameter.ParameterType, method, parameter.Name, out type, out enumValues, out enumType, out failure))
            {
                return null;
            }

            var enumAnnotation = parameter.GetCustomAttribute<HandlerDslEnumValuesAttribute>(inherit: false);
            if (enumAnnotation is not null)
            {
                enumValues = enumAnnotation.Values.ToList();
            }

            specs.Add(new PinMeta(
                new HandlerDslNaming.ParameterSpec(pinName, type, HasDefault: !required), enumValues, enumType));
        }

        var outputs = new List<DslPin>();
        var hasAwait = false;
        var hasSideEffect = false;

        var returnType = method.ReturnType;
        var resultAnnotation = method.GetCustomAttribute<HandlerDslResultAttribute>(inherit: false);

        if (resultAnnotation is not null)
        {
            hasAwait = true;
            hasSideEffect = true;
            foreach (var entry in resultAnnotation.Outputs)
            {
                var parts = entry.Split(':');
                if (parts.Length is < 2 or > 3)
                {
                    failure = $"方法 '{method.Name}' 的 [HandlerDslResult] 条目 '{entry}' 格式非法："
                        + "必须形如 \"name:type\" 或 \"name:type:enum\"。";
                    return null;
                }

                if (!PinType.TryParse(parts[1], out var outputType))
                {
                    failure = $"方法 '{method.Name}' 的 [HandlerDslResult] 条目 '{entry}' 的类型 '{parts[1]}' 超出封闭集。";
                    return null;
                }

                var enumKind = parts.Length == 3 && string.Equals(parts[2], DslPin.EnumOutputMarker, StringComparison.Ordinal);
                outputs.Add(new DslPin(
                    parts[0],
                    outputType,
                    required: true,
                    enumValues: null,
                    enumType: enumKind ? DslPin.EnumOutputMarker : null));
            }
        }
        else if (returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            hasAwait = true;
            hasSideEffect = true;
        }
        else if (TryUnwrapTask(returnType, out var payload))
        {
            hasAwait = true;
            hasSideEffect = true;
            if (payload == typeof(void))
            {
                // Task（无泛型）已在上分支处理；此处保留防御。
            }
            else if (TryMap(payload, method, "返回值", out var payloadType, out var payloadEnums, out var payloadEnumType, out failure))
            {
                var subtype = method.ReturnParameter.GetCustomAttribute<HandlerDslSubtypeAttribute>(inherit: false);
                if (subtype is not null && payloadType.Kind == PinTypeKind.Entity)
                {
                    payloadType = payloadType.WithSubtypes(subtype.Types.ToArray());
                }

                var enumAnnotation = method.ReturnParameter.GetCustomAttribute<HandlerDslEnumValuesAttribute>(inherit: false);
                var enumValues = enumAnnotation?.Values.ToList() ?? payloadEnums;
                outputs.Add(new DslPin("result", payloadType, required: true, enumValues, payloadEnumType));
            }
            else
            {
                failure = $"方法 '{method.Name}' 返回结果对象 '{payload.Name}'，默认映射规则表达不了，"
                    + "且**未标注** [HandlerDslResult] 的多输出拆解——扫描期直接失败，不静默退化成一个 string 引脚"
                    + "（SPEC §12.1／R25 第 3 条）。";
                return null;
            }
        }
        else
        {
            // 同步方法：纯查询（无挂起、无副作用），例如 OwnerUnits。
            if (returnType != typeof(void)
                && TryMap(returnType, method, "返回值", out var syncType, out var syncEnums, out var syncEnumType, out failure))
            {
                var subtype = method.ReturnParameter.GetCustomAttribute<HandlerDslSubtypeAttribute>(inherit: false);
                if (subtype is not null && syncType.Kind == PinTypeKind.Entity)
                {
                    syncType = syncType.WithSubtypes(subtype.Types.ToArray());
                }

                var enumAnnotation = method.ReturnParameter.GetCustomAttribute<HandlerDslEnumValuesAttribute>(inherit: false);
                var enumValues = enumAnnotation?.Values.ToList() ?? syncEnums;
                outputs.Add(new DslPin("result", syncType, required: true, enumValues, syncEnumType));
            }
            else if (returnType != typeof(void))
            {
                return null;
            }
        }

        return new MethodScaffold(
            NodeNameOf(method)!,
            method.Name,
            face.Name,
            face.FullName ?? face.Name,
            isStaticFace,
            implicitHost,
            specs,
            outputs,
            hasAwait,
            hasSideEffect,
            cancellationTokenParameter);
    }

    private static bool TryUnwrapTask(Type type, out Type payload)
    {
        payload = typeof(void);
        if (!type.IsGenericType)
        {
            return false;
        }

        var definition = type.GetGenericTypeDefinition();
        if (definition != typeof(Task<>) && definition != typeof(ValueTask<>))
        {
            return false;
        }

        payload = type.GetGenericArguments()[0];
        return true;
    }

    /// <summary>
    /// 类型 → 引脚类型（SPEC §12.1 默认映射）。返回 false＝不在封闭集内（目录生成失败，需标注拆解或进排除清单）。
    /// </summary>
    private static bool TryMap(
        Type type,
        MethodInfo method,
        string what,
        out PinType pinType,
        out List<string> enumValues,
        out string? enumType,
        out string? failure)
    {
        pinType = PinType.Number;
        enumValues = new List<string>();
        enumType = null;
        failure = null;

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))
        {
            pinType = PinType.String;
            return true;
        }

        if (underlying == typeof(bool))
        {
            pinType = PinType.Boolean;
            return true;
        }

        if (NumberTypeNames.Contains(underlying.Name))
        {
            pinType = PinType.Number;
            return true;
        }

        if (EntityTypeNames.Contains(underlying.Name))
        {
            pinType = PinType.Entity;
            return true;
        }

        if (underlying.IsEnum)
        {
            // 枚举＝string ＋ 允许取值集（SPEC §6.3）；CLR 类型名留给生成码落 <类型>.<成员>。
            pinType = PinType.String;
            enumValues = Enum.GetNames(underlying).ToList();
            enumType = underlying.FullName ?? underlying.Name;
            return true;
        }

        if (ListTypeNames.Contains(underlying.Name))
        {
            var element = underlying.IsArray ? underlying.GetElementType()! : underlying.GetGenericArguments()[0];
            var elementUnderlying = Nullable.GetUnderlyingType(element) ?? element;
            if (EntityTypeNames.Contains(elementUnderlying.Name))
            {
                pinType = PinType.List(PinType.Entity);
                return true;
            }

            if (NumberTypeNames.Contains(elementUnderlying.Name))
            {
                pinType = PinType.List(PinType.Number);
                return true;
            }

            if (elementUnderlying == typeof(string))
            {
                pinType = PinType.List(PinType.String);
                return true;
            }

            if (elementUnderlying == typeof(bool))
            {
                pinType = PinType.List(PinType.Boolean);
                return true;
            }
        }

        failure = $"方法 '{method.Name}' 的{what}类型 '{underlying.Name}' 不在引脚封闭集内（SPEC §6）："
            + "要么用 [HandlerDslPin]／[HandlerDslResult] 标注拆解，要么进 EXCLUSIONS.md。";
        return false;
    }

    private sealed record PinMeta(
        HandlerDslNaming.ParameterSpec Spec,
        IReadOnlyList<string> EnumValues,
        string? EnumType);

    private sealed record MethodScaffold(
        string NodeName,
        string Method,
        string FaceShortName,
        string FaceType,
        bool IsStatic,
        string? ImplicitHost,
        IReadOnlyList<PinMeta> Pins,
        IReadOnlyList<DslPin> Outputs,
        bool HasAwait,
        bool HasSideEffect,
        string? CancellationTokenParameter);
}
