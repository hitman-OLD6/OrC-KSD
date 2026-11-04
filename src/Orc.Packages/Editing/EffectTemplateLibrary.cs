using Orc.Core.Model;
using Orc.Game.Model;

namespace Orc.Packages.Editing;

/// <summary>一条派生出来的模板（R33／D30 派生矩阵的一行；纯数据，便于逐条比对）。</summary>
/// <param name="Id">资产 id（<c>template.&lt;signal&gt;.&lt;guard&gt;</c>）。</param>
/// <param name="Signal">信号名（资产字段给出，**不从 id 解析**）。</param>
/// <param name="Guard">守卫档。</param>
/// <param name="Label">展示名（信号中文名，非 any 档再加「（guard 中文名）」）。</param>
/// <param name="Description">展示描述（该信号的 <c>Notes</c>；无则 null）。</param>
/// <param name="ActiveWhen">装配时点（由信号的 <c>SignalTiming</c> 穷举推出）。</param>
/// <param name="RequiresHost">宿主需求（D24 的推导表）。</param>
public sealed record EffectTemplateSpec(
    string Id,
    string Signal,
    HandlerGuard Guard,
    string Label,
    string? Description,
    ActiveWhen ActiveWhen,
    HostKind RequiresHost)
{
    /// <summary>模板文件名（<c>&lt;id&gt;.component.json</c>；id 无 <c>/</c>，可直接当文件名）。</summary>
    public string FileName => Id + ".component.json";

    /// <summary>模板的抽象成员名（R34 第 3 条：内置模板一律固定 <c>onHook</c>）。</summary>
    public const string MemberName = "onHook";
}

/// <summary>
/// **效果模板库**（R33／D30）：按「信号 × 可用 guard 档」派生出的模板资产集合。
/// <para>
/// 这是**生成物**：文件由 <c>ORC_WRITE_TEMPLATES=1</c> 从信号表批量导出，**不得手改**；
/// 守护测试「按规则重算再逐条比对」（**不硬编码 102**——102 只是此刻的实测值，加信号就会变）。
/// </para>
/// <para>
/// 派生规则（S16 的五条，先规则后枚举）：
/// <list type="number">
/// <item>取全部信号（内核 4 ＋ 游戏层 35，按各自声明序），**剔除 <c>SignalTiming.Load</c>**（唯一一条＝<c>card.load</c>）。</item>
/// <item><c>any</c> 档**恒出**。</item>
/// <item><c>Subjects</c> 白名单有登记 ⇒ 出 <c>self</c> 档（**只由白名单决定**，不按载荷顺序或类型推断）。</item>
/// <item><c>Players</c> 白名单有登记 ⇒ 出 <c>owner-same</c> 与 <c>owner-different</c>。</item>
/// <item><c>id = "template." + signal + "." + guard</c>；<c>label = 信号中文名</c>，非 any 档再加「（guard 中文名）」；
///   <c>description = 该信号 Notes</c>（无则省略）。</item>
/// </list>
/// 资产字段：<c>requires-host</c> 按 D24 推导、<c>active-when</c> 按 timing 推导、
/// **不写 <c>abstract</c>／<c>authorize-moding</c>**（两标志已废止，R39 裁决 4）；成员恒一个：<c>onHook</c>、
/// <c>slot: { type: "handler" }</c>（未填 ⇒ 模板）、<c>guard: &lt;档&gt;</c>、
/// 视图字段＝该信号的载荷视图、成员 <c>label</c> 不写。
/// </para>
/// </summary>
public static class EffectTemplateLibrary
{
    /// <summary>模板资产所在目录（包内相对路径；与产物同一约定 <c>effects-v4</c>）。</summary>
    public const string AssetsDirectory = ModingFlattener.ProductDirectory;

    /// <summary>生成开关（与 <c>ORC_WRITE_CATALOG</c>／<c>ORC_WRITE_BASELINE</c> 同形态）。</summary>
    public const string WriteSwitch = "ORC_WRITE_TEMPLATES";

    /// <summary>
    /// **派生全部模板**（确定性：信号按声明序 × 档按 <c>any → self → owner-same → owner-different</c>）。
    /// </summary>
    public static IReadOnlyList<EffectTemplateSpec> Derive()
    {
        var specs = new List<EffectTemplateSpec>();
        foreach (var signal in SignalGuards.Names)
        {
            var member = SignalGuards.MemberOf(signal);
            if (member is null || member.SignalTiming is not { } timing)
            {
                continue;
            }

            // ③ <c>SignalTiming.Load</c> ＝**不出模板**（穷举规则，无默认值）：加载期信号与任何时点都冲突。
            if (timing == SignalTiming.Load)
            {
                continue;
            }

            var label = SignalGuards.Labels[signal];
            var description = SignalGuards.Notes.TryGetValue(signal, out var note) ? note : null;
            var activeWhen = timing == SignalTiming.Placed ? ActiveWhen.Placed : ActiveWhen.Loaded;

            specs.Add(Build(signal, HandlerGuard.Any, label, description, activeWhen));
            if (SignalGuards.HasSelf(signal))
            {
                specs.Add(Build(signal, HandlerGuard.Self, label, description, activeWhen));
            }

            if (SignalGuards.HasOwner(signal))
            {
                specs.Add(Build(signal, HandlerGuard.OwnerSame, label, description, activeWhen));
                specs.Add(Build(signal, HandlerGuard.OwnerDifferent, label, description, activeWhen));
            }
        }

        return specs;

        static EffectTemplateSpec Build(
            string signal,
            HandlerGuard guard,
            string signalLabel,
            string? description,
            ActiveWhen activeWhen)
        {
            var guardLabel = HandlerGuardNames.DisplayName(guard);
            return new EffectTemplateSpec(
                AssetSchema.TemplateIdPrefix + signal + "." + HandlerGuardNames.Format(guard),
                signal,
                guard,
                guardLabel is null ? signalLabel : $"{signalLabel}（{guardLabel}）",
                description,
                activeWhen,
                SignalGuards.DeriveHostKind(guard, signal));
        }
    }

    /// <summary>把一条派生结果落成**组件资产**（生成物形态：未饱和资产 ＋ 单个未填 handler 空位 ＋ 挂到该信号）。</summary>
    /// <param name="spec">派生结果。</param>
    /// <exception cref="InvalidOperationException">信号未在信号表里登记（理论上不可达）。</exception>
    public static ComponentAsset BuildAsset(EffectTemplateSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var signal = SignalGuards.MemberOf(spec.Signal)
            ?? throw new InvalidOperationException($"信号 '{spec.Signal}' 未在信号表里登记（模板派生输入非法）。");

        var member = new ComponentMemberAsset(
            EffectTemplateSpec.MemberName,
            // 可见性**全开**：模板成员是空洞，可见性由子（草案）自行**收紧**——
            // 继承只能收紧（R5），父若只开一部分，子就再也打不开其余能力。
            MemberVisibility.Callable | MemberVisibility.Hostable | MemberVisibility.Mountable,
            signal.View,
            Handler: null,             // 空位未填：handler 由子提供
            Band: null,
            Bands: null,
            Seams: null,
            // **不加 sealed**（R39 裁决 7）：模板的 slot 必须能被子填，sealed 会让抽象永远无法解除。
            Sealed: false,
            Guard: spec.Guard,
            Label: null,               // 内置模板的成员 label 不写（R34 第 3 条：成员名固定即可辨认）
            // R39 裁决 7（D41 的 1:1 迁移）：`abstractMember: true` → `slot: { type: "handler" }` ＋ handler=null。
            // 落 D40 判定表第 1 行＝未填 ⇒ 抽象；**不得**用「不写 slot」表达空洞（那会落纯编排成员，被判为具体资产）。
            Slot: MemberSlotDeclaration.Handler);

        return new ComponentAsset(
            spec.Id,
            spec.RequiresHost,
            spec.ActiveWhen,
            oneShot: false,
            disposalSignal: null,
            privateFields: Array.Empty<PrivateFieldDefinition>(),
            members: new[] { member },
            mounts: new[] { new MountDeclaration("engine#" + spec.Signal, EffectTemplateSpec.MemberName) },
            wraps: Array.Empty<WrapDeclaration>(),
            judgeWraps: Array.Empty<ComponentJudgeWrapAsset>(),
            extends: null,
            // 不写 abstract / authorize-moding：两个手写标志已废止（R39 裁决 4），抽象性由未填空位推导。
            label: spec.Label,
            description: spec.Description);
    }

    /// <summary>派生结果 → 模板文件内容（规范 JSON，与 <c>AssetSchema.Write</c> 同一真源）。</summary>
    public static string Render(EffectTemplateSpec spec) => AssetSchema.Write(BuildAsset(spec));

    /// <summary>
    /// **按规则重算的期望文件集合**（路径 → 文本）：守护测试与生成器共用同一份真源——
    /// 手改任一文件、少一条、多一条、改名都会让它与磁盘不一致。
    /// </summary>
    /// <param name="templateRoot">模板库根（如 <c>packs/effect-templates</c>）。</param>
    public static IReadOnlyDictionary<string, string> ExpectedFiles(string templateRoot)
    {
        ArgumentNullException.ThrowIfNull(templateRoot);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in Derive())
        {
            files[Path.Combine(templateRoot, AssetsDirectory, spec.FileName)] = Render(spec);
        }

        return files;
    }

    /// <summary>
    /// 写盘（**只经 <c>ORC_WRITE_TEMPLATES=1</c> 的生成测试调用**）：先删掉不再派生的旧文件，再逐条写出。
    /// </summary>
    /// <returns>写出的文件数。</returns>
    public static int WriteAll(string templateRoot)
    {
        ArgumentNullException.ThrowIfNull(templateRoot);
        var expected = ExpectedFiles(templateRoot);
        var directory = Path.Combine(templateRoot, AssetsDirectory);
        Directory.CreateDirectory(directory);

        foreach (var stale in Directory.EnumerateFiles(directory, AssetSchema.FilePattern, SearchOption.TopDirectoryOnly))
        {
            if (!expected.ContainsKey(stale))
            {
                File.Delete(stale);
            }
        }

        foreach (var (path, text) in expected.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return expected.Count;
    }
}
