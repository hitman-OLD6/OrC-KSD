using System.Text.Json;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;

namespace Orc.Game.Migration;

/// <summary>一次迁移的转换计数（按转换类别分开，供汇报与守护测试断言）。</summary>
public sealed record MigrationCounters(
    int Triggers,
    int Handlers,
    int ScriptsRewritten,
    int Hooks,
    int HookMounts,
    int VestigialHooks,
    int InjectsRewrite,
    int InjectsCommit,
    int InjectsPlain,
    int SelfWraps,
    int JudgeWraps,
    int DisposalResponses,
    int SkippedEmptyTriggers,
    int OneShot);

/// <summary>一份资产迁移成功后的产物。</summary>
/// <param name="Source">来源文件（诊断用）。</param>
/// <param name="AssetId">资产 id。</param>
/// <param name="Json">新 schema 规范文本（可直接落盘）。</param>
/// <param name="Counters">本次转换的计数。</param>
/// <param name="LegacyHooks">旧 <c>hooks</c> 全集（反向自检的比对基准）。</param>
public sealed record MigratedAsset(
    string Source,
    string AssetId,
    string Json,
    MigrationCounters Counters,
    IReadOnlyList<string> LegacyHooks);

/// <summary>整包迁移报告（原子：任一资产失败＝整包失败并列出全部失败项）。</summary>
/// <param name="PackDirectory">卡包目录。</param>
/// <param name="OutputDirectory">产物目录（<c>effects-v4</c>）。</param>
/// <param name="Assets">迁移成功的资产（按文件名序）。</param>
/// <param name="Failures">失败明细。</param>
public sealed record PackMigrationReport(
    string PackDirectory,
    string OutputDirectory,
    IReadOnlyList<MigratedAsset> Assets,
    IReadOnlyList<string> Failures)
{
    /// <summary>是否全部成功。</summary>
    public bool Ok => Failures.Count == 0;

    /// <summary>计数器汇总。</summary>
    public MigrationCounters Total => new(
        Assets.Sum(static a => a.Counters.Triggers),
        Assets.Sum(static a => a.Counters.Handlers),
        Assets.Sum(static a => a.Counters.ScriptsRewritten),
        Assets.Sum(static a => a.Counters.Hooks),
        Assets.Sum(static a => a.Counters.HookMounts),
        Assets.Sum(static a => a.Counters.VestigialHooks),
        Assets.Sum(static a => a.Counters.InjectsRewrite),
        Assets.Sum(static a => a.Counters.InjectsCommit),
        Assets.Sum(static a => a.Counters.InjectsPlain),
        Assets.Sum(static a => a.Counters.SelfWraps),
        Assets.Sum(static a => a.Counters.JudgeWraps),
        Assets.Sum(static a => a.Counters.DisposalResponses),
        Assets.Sum(static a => a.Counters.SkippedEmptyTriggers),
        Assets.Sum(static a => a.Counters.OneShot));
}

/// <summary>
/// 旧效果预制体（<c>schemaVersion 2/3</c>，<c>PrefabJson</c> 形态）→ 新组件资产（<c>schemaVersion 4</c>）的迁移器（S11）。
/// <para>
/// **转换规则（逐条机械，不做语义猜测）**：
/// <list type="bullet">
/// <item><c>hooks: ["X"]</c> → 挂载声明 <c>engine#X</c>（**纯前缀替换**，成员名零转换）；迁移器自带**反向自检**：
///   去掉 <c>engine#</c> 必须能在旧资产的 hooks 里找到同名项。</item>
/// <item><c>injects</c>：<c>band: Rewrite</c> → <c>wrap</c> + <c>phase: before</c>；<c>band: Commit</c> → <c>wrap</c> + <c>phase: after</c>；
///   无 <c>band</c> → 外来挂载边（<c>mounts</c>）。目标地址一律经 <see cref="EngineProcesses.InjectTargets"/> 常量表解析
///   （R1j：迁移器**不硬编码**显示名映射）。</item>
/// <item><c>oneShot</c> → <c>one-shot</c>（语义改由 R3k 承载）。</item>
/// <item><c>disposingTrigger</c> 段 → 该组件的作者销毁响应边（挂在兜底信号上，位于框架注销边之前）。</item>
/// <item><c>wrappers[]</c>（目标＝本效果内事件 id）→ 包装声明，目标＝<c>self#&lt;成员&gt;</c>；<c>seam</c> 丢弃。</item>
/// <item><c>judicatorWrappers[]</c> → <c>judge-wraps</c>（判定器目标 + 相位 + 同步 csx 原文）。</item>
/// <item>csx 头部 <c>Func&lt;…View,</c> → <c>Func&lt;dynamic,</c>（**正文零差异**；判定器包装器的同步签名不改）。</item>
/// </list>
/// </para>
/// <para>旧资产的 <c>kind</c>（active／passive）不进入新模型（R1）：成员一律声明 <c>callable|mountable</c>。</para>
/// </summary>
public static class AssetSchemaMigrator
{
    /// <summary>旧 <c>band</c> 的已知取值（两套旧段枚举的并集；未知值＝迁移器拒绝，不猜）。</summary>
    private static readonly HashSet<string> KnownLegacyBands = new(StringComparer.Ordinal)
    {
        "Initiate", "Announce", "CounterGuard", "Counter", "Adjust", "Commit",
        "Rewrite", "Veto", "Apply",
    };

    /// <summary>旧效果预制体文件名后缀。</summary>
    public const string LegacyFilePattern = "*.prefab.json";

    /// <summary>迁移产物目录名（与 <c>effects/</c> 并存：旧资产不删除，两套各自可装载）。</summary>
    public const string OutputDirectoryName = "effects-v4";

    /// <summary>迁移一份旧资产文本。</summary>
    /// <exception cref="ArgumentNullException">json 为 null。</exception>
    /// <exception cref="InvalidOperationException">旧资产结构不受支持（不静默降级）。</exception>
    public static MigratedAsset Migrate(string json, string source = "")
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var root = document.RootElement;
        var version = root.TryGetProperty("schemaVersion", out var versionElement) ? versionElement.GetInt32() : 0;
        if (version is not (2 or 3))
        {
            throw new InvalidOperationException(
                $"{source}：不是旧效果预制体（schemaVersion={version}；期望 2 或 3）。");
        }

        if (!root.TryGetProperty("root", out var effect) || effect.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{source}：旧资产缺少 root。");
        }

        var assetId = RequiredString(effect, "id", source);
        var oneShot = effect.TryGetProperty("oneShot", out var oneShotElement) && oneShotElement.ValueKind == JsonValueKind.True;

        var triggers = new List<JsonElement>();
        if (effect.TryGetProperty("mainTrigger", out var main) && main.ValueKind == JsonValueKind.Object)
        {
            triggers.Add(main);
        }

        if (effect.TryGetProperty("otherTriggers", out var others) && others.ValueKind == JsonValueKind.Array)
        {
            triggers.AddRange(others.EnumerateArray().Where(static t => t.ValueKind == JsonValueKind.Object));
        }

        var legacyHooks = new List<string>();
        var members = new List<ComponentMemberAsset>();
        var mounts = new List<MountDeclaration>();
        var wraps = new List<WrapDeclaration>();
        var judgeWraps = new List<ComponentJudgeWrapAsset>();

        var usedMemberNames = new HashSet<string>(StringComparer.Ordinal);
        var memberByEvent = new Dictionary<string, string>(StringComparer.Ordinal);
        var counters = new Counters();

        // 引擎成员表（判定 hooks 是"真订阅"还是"占位"的依据；跨层合并后的 engine.game）。
        var engineType = GameEnginePrefabs.CreateCatalog().Resolve(GameEnginePrefabs.PrefabId);

        // hooks 派生的挂载（反向自检只针对这些：injects 的 engine# 目标来自显示名映射，不在旧 hooks 里）。
        var hookDerivedMounts = new List<MountDeclaration>();

        // ① 触发器 → 成员（一 event 一成员）＋ hooks → engine#<hook> 挂载。
        var triggerIndex = 0;
        foreach (var trigger in triggers)
        {
            triggerIndex++;
            var triggerId = OptionalString(trigger, "id") ?? $"t{triggerIndex}";
            var viewType = OptionalString(trigger, "viewType") ?? "Orc.Cards.CardEventView";
            var view = LegacyViewCatalog.Resolve(viewType);

            var hooks = new List<string>();
            if (trigger.TryGetProperty("hooks", out var hooksElement) && hooksElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var hook in hooksElement.EnumerateArray())
                {
                    var name = hook.GetString() ?? string.Empty;
                    if (name.Length == 0)
                    {
                        throw new InvalidOperationException($"{source}：触发器 '{triggerId}' 的 hooks 含空项。");
                    }

                    if (hooks.Contains(name, StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"{source}：触发器 '{triggerId}' 的 hooks 含重复项 '{name}'（旧模型不允许重复）。");
                    }

                    hooks.Add(name);
                    legacyHooks.Add(name);
                }
            }

            var events = new List<JsonElement>();
            if (trigger.TryGetProperty("events", out var eventsElement) && eventsElement.ValueKind == JsonValueKind.Array)
            {
                events.AddRange(eventsElement.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object));
            }

            if (events.Count == 0)
            {
                // 旧模型里"挂了信号但没有事件"＝空触发器（无行为）。新模型不产生成员（空成员只会是噪声）。
                counters.SkippedEmptyTriggers++;
                continue;
            }

            counters.Triggers++;

            foreach (var @event in events)
            {
                var eventId = RequiredString(@event, "id", source);
                var entry = OptionalString(@event, "entry") ?? "HandleAsync";
                var script = OptionalString(@event, "csx");
                var assemblyKey = OptionalString(@event, "assemblyKey");
                if (script is null && assemblyKey is null)
                {
                    throw new InvalidOperationException(
                        $"{source}：触发器 '{triggerId}' 的事件 '{eventId}' 既无 csx 也无 assemblyKey。");
                }

                var memberName = UniqueMemberName(eventId, triggerId, usedMemberNames);
                var handlerId = assetId + "#" + memberName;

                if (script is not null)
                {
                    var rewritten = RewriteScriptHeader(script);
                    if (!string.Equals(rewritten, script, StringComparison.Ordinal))
                    {
                        counters.ScriptsRewritten++;
                    }

                    script = rewritten;
                }

                members.Add(new ComponentMemberAsset(
                    memberName,
                    MemberVisibility.Callable | MemberVisibility.Mountable,
                    view,
                    new ComponentHandlerAsset(handlerId, entry, script, assemblyKey)));
                counters.Handlers++;

                if (!memberByEvent.TryAdd(eventId, memberName))
                {
                    throw new InvalidOperationException(
                        $"{source}：事件 id '{eventId}' 在资产内重复（injects 按事件 id 引用，无法消歧）。");
                }

                foreach (var hook in hooks)
                {
                    // 占位 hooks（S11 实证）：注入型触发器的 hooks 在旧路径里**必然绑定失败**（视图必填字段
                    // 不在该信号的载荷里 → ContractFailure，被隔离记录），因此从未产生任何行为。
                    // 迁移时按"必填字段是否被信号载荷覆盖"机械判定：覆盖＝真订阅（生成挂载边）；
                    // 不覆盖＝占位（不生成挂载边，计数留痕）——否则新模型会在装载期直接拒绝该挂载（R1c）。
                    if (!CoversRequiredFields(engineType, hook, view))
                    {
                        counters.VestigialHooks++;
                        continue;
                    }

                    var target = "engine#" + hook;
                    var mount = new MountDeclaration(target, memberName);
                    mounts.Add(mount);
                    hookDerivedMounts.Add(mount);
                    counters.HookMounts++;
                }
            }

            counters.Hooks += hooks.Count;
        }

        // ② injects：显示名 → 成员地址（常量表同源），band → 相位／挂载。
        if (effect.TryGetProperty("injects", out var injectsElement) && injectsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var inject in injectsElement.EnumerateArray())
            {
                var displayName = RequiredString(inject, "target", source);
                var eventId = RequiredString(inject, "event", source);
                var band = OptionalString(inject, "band");

                if (!memberByEvent.TryGetValue(eventId, out var memberName))
                {
                    throw new InvalidOperationException(
                        $"{source}：injects 引用了不存在的事件 '{eventId}'（目标显示名 '{displayName}'）。");
                }

                var mapping = EngineProcesses.Find(displayName)
                    ?? throw new InvalidOperationException(
                        $"{source}：injects 目标显示名 '{displayName}' 不在映射表里（R1j 只登记在用显示名；"
                        + "新增显示名＝先补 EngineProcesses 常量表，再迁移）。");

                // band → 相位（R1i 三段语义），**不以"该显示名的历史期望 band"为拒绝条件**：
                // 同一目标在不同作者手里可以出现在不同段（样例包是「伤害处理 + Rewrite」，
                // 而文本编译器模板 `attack_basic` 是「单位攻击 + Initiate」——两者都是合法的旧形态）。
                //   Rewrite／Veto → before（改写输入／否决，否决具体动作由 csx 自身的 stop 承担）
                //   Commit        → after（默认转发之后的尾部副作用）
                //   其余已知段（Initiate／Announce／CounterGuard／Counter／Adjust／Apply）与无 band
                //                 → 外来挂载边（在成员自有边之前跑，与 R1j「无 band＝挂载边」同义）
                // 未知 band ＝ 拒绝（迁移器不猜）。
                if (band is not null && !KnownLegacyBands.Contains(band))
                {
                    throw new InvalidOperationException(
                        $"{source}：injects 目标 '{displayName}' 的 band='{band}' 不在已知旧段集合里"
                        + "（已知：Initiate／Announce／CounterGuard／Counter／Adjust／Commit／Rewrite／Veto／Apply）。");
                }

                var handlerId = assetId + "#" + memberName;
                if (band is "Rewrite" or "Veto" or "Commit")
                {
                    var phase = band == "Commit" ? WrapperPhase.After : WrapperPhase.Before;
                    wraps.Add(WrapDeclaration.ForMember(mapping.Address, phase, handlerId));
                    counters.InjectsRewrite += band == "Commit" ? 0 : 1;
                    counters.InjectsCommit += band == "Commit" ? 1 : 0;
                }
                else
                {
                    mounts.Add(new MountDeclaration(mapping.Address, memberName));
                    counters.InjectsPlain++;
                }
            }
        }

        // ③ wrappers[]：目标＝本效果内的事件 id → self#<成员>（seam 丢弃，接缝退役）。
        if (effect.TryGetProperty("wrappers", out var wrappersElement) && wrappersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var wrapper in wrappersElement.EnumerateArray())
            {
                var phaseText = RequiredString(wrapper, "phase", source);
                var targetEvent = OptionalString(wrapper, "target") ?? string.Empty;
                if (!memberByEvent.TryGetValue(targetEvent, out var targetMember))
                {
                    throw new InvalidOperationException(
                        $"{source}：wrappers 的目标事件 '{targetEvent}' 不存在（旧模型要求目标＝本效果事件 id）。");
                }

                var handler = wrapper.TryGetProperty("handler", out var handlerElement)
                    ? handlerElement
                    : throw new InvalidOperationException($"{source}：wrappers 缺少 handler。");

                var wrapperMember = UniqueMemberName("wrap." + targetMember, "wrap", usedMemberNames);
                var handlerId = assetId + "#" + wrapperMember;
                var entry = OptionalString(handler, "entry") ?? "HandleAsync";
                var script = OptionalString(handler, "csx");
                var assemblyKey = OptionalString(handler, "assemblyKey");
                if (script is null && assemblyKey is null)
                {
                    throw new InvalidOperationException($"{source}：wrappers 的 handler 既无 csx 也无 assemblyKey。");
                }

                if (script is not null)
                {
                    script = RewriteScriptHeader(script);
                    counters.ScriptsRewritten++;
                }

                members.Add(new ComponentMemberAsset(
                    wrapperMember,
                    MemberVisibility.None,
                    ViewSpec.Empty,
                    new ComponentHandlerAsset(handlerId, entry, script, assemblyKey)));

                wraps.Add(WrapDeclaration.ForMember("self#" + targetMember, ParsePhase(phaseText, source), handlerId));
                counters.SelfWraps++;
            }
        }

        // ④ judicatorWrappers[] → judge-wraps（同步签名，源码原文不改）。
        if (effect.TryGetProperty("judicatorWrappers", out var judgeElement) && judgeElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var wrapper in judgeElement.EnumerateArray())
            {
                var judicator = RequiredString(wrapper, "judicator", source);
                var phaseText = OptionalString(wrapper, "phase") ?? "after";
                var handler = wrapper.TryGetProperty("handler", out var handlerElement)
                    ? handlerElement
                    : throw new InvalidOperationException($"{source}：judicatorWrappers 缺少 handler。");

                var entry = OptionalString(handler, "entry") ?? "Handle";
                var script = OptionalString(handler, "csx")
                    ?? throw new InvalidOperationException(
                        $"{source}：judicatorWrappers 的 handler 只有 assemblyKey（迁移器只搬 csx 源码）。");

                judgeWraps.Add(new ComponentJudgeWrapAsset(judicator, ParsePhase(phaseText, source), entry, script));
                counters.JudgeWraps++;
            }
        }

        // ⑤ disposingTrigger → 作者销毁响应边（框架注销边仍隐式合成，位次恒在其后）。
        string? disposalSignal = null;
        if (effect.TryGetProperty("disposingTrigger", out var disposing) && disposing.ValueKind == JsonValueKind.Object)
        {
            var disposalHooks = new List<string>();
            if (disposing.TryGetProperty("hooks", out var disposalHooksElement)
                && disposalHooksElement.ValueKind == JsonValueKind.Array)
            {
                disposalHooks.AddRange(disposalHooksElement.EnumerateArray().Select(static h => h.GetString() ?? string.Empty));
            }

            disposalSignal = disposalHooks.Count > 0 ? disposalHooks[0] : ComponentDisposalPolicy.DefaultSignal;
            if (string.Equals(disposalSignal, Updates.EffectRemoved, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{source}：disposingTrigger 把 effect.removed 作为兜底信号（旧模型也拒绝：防自触发递归，R1f）。");
            }

            var disposalView = LegacyViewCatalog.Resolve(OptionalString(disposing, "viewType") ?? "Orc.Cards.CardEventView");
            if (disposing.TryGetProperty("events", out var disposalEvents) && disposalEvents.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var @event in disposalEvents.EnumerateArray())
                {
                    index++;
                    var eventId = OptionalString(@event, "id") ?? $"dispose{index}";
                    var memberName = UniqueMemberName(eventId, "dispose", usedMemberNames);
                    var handlerId = assetId + "#" + memberName;
                    var entry = OptionalString(@event, "entry") ?? "HandleAsync";
                    var script = OptionalString(@event, "csx");
                    var assemblyKey = OptionalString(@event, "assemblyKey");
                    if (script is null && assemblyKey is null)
                    {
                        throw new InvalidOperationException($"{source}：disposingTrigger 的事件 '{eventId}' 既无 csx 也无 assemblyKey。");
                    }

                    if (script is not null)
                    {
                        script = RewriteScriptHeader(script);
                        if (!string.Equals(script, OptionalString(@event, "csx"), StringComparison.Ordinal))
                        {
                            counters.ScriptsRewritten++;
                        }
                    }

                    members.Add(new ComponentMemberAsset(
                        memberName,
                        MemberVisibility.Callable | MemberVisibility.Mountable,
                        disposalView,
                        new ComponentHandlerAsset(handlerId, entry, script, assemblyKey)));
                    counters.Handlers++;
                    counters.DisposalResponses++;
                    mounts.Add(new MountDeclaration("engine#" + disposalSignal, memberName));
                }
            }
        }

        if (oneShot)
        {
            counters.OneShot++;
        }

        var asset = new ComponentAsset(
            assetId,
            HostKind.Any,
            // 旧模型的效果在卡牌加载后即挂载（含手牌期），因此忠实映射是 loaded；placed 会让加载期信号（card.load）
            // 的挂载被判为"无意义挂载"（S3 第 4c 条）。
            ActiveWhen.Loaded,
            oneShot,
            disposalSignal,
            Array.Empty<PrivateFieldDefinition>(),
            members,
            mounts,
            wraps,
            judgeWraps);

        // ⑥ 反向自检（S11 验收）：每条 hooks 派生的 engine#X 挂载去掉前缀后必须能在旧 hooks 里找到同名项。
        foreach (var mount in hookDerivedMounts)
        {
            var name = mount.Target["engine#".Length..];
            if (!legacyHooks.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{source}：反向自检失败——挂载 '{mount.Target}' 去掉前缀后的 '{name}' 不在旧资产 hooks 里。");
            }
        }

        return new MigratedAsset(
            source,
            assetId,
            AssetSchema.Write(asset),
            new MigrationCounters(
                counters.Triggers,
                counters.Handlers,
                counters.ScriptsRewritten,
                counters.Hooks,
                counters.HookMounts,
                counters.VestigialHooks,
                counters.InjectsRewrite,
                counters.InjectsCommit,
                counters.InjectsPlain,
                counters.SelfWraps,
                counters.JudgeWraps,
                counters.DisposalResponses,
                counters.SkippedEmptyTriggers,
                counters.OneShot),
            legacyHooks);
    }

    /// <summary>
    /// csx 头部机械改写：把入口委托的第一段类型参数（旧视图类型）换成 <c>dynamic</c>，**正文一字不动**。
    /// <para>
    /// 判据＝第一段类型参数以 <c>View</c> 结尾（旧视图类的命名口径）。因此
    /// <c>Func&lt;object[], object[], object[]&gt;</c>（判定器包装器的同步签名）**不被改写**，
    /// 已是 <c>dynamic</c> 的头部也不变（幂等）。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">source 为 null。</exception>
    public static string RewriteScriptHeader(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var head = source.IndexOf("Func<", StringComparison.Ordinal);
        if (head < 0)
        {
            return source;
        }

        var comma = source.IndexOf(',', head + "Func<".Length);
        if (comma < 0)
        {
            return source;
        }

        var typeArgument = source[(head + "Func<".Length)..comma].Trim();
        if (string.Equals(typeArgument, "dynamic", StringComparison.Ordinal)
            || !typeArgument.EndsWith("View", StringComparison.Ordinal))
        {
            return source;
        }

        return string.Concat(source.AsSpan(0, head + "Func<".Length), "dynamic", source.AsSpan(comma));
    }

    /// <summary>把整个卡包的旧效果资产迁到 <c>effects-v4/</c>（旧目录不删；产物原子：任一失败不落盘）。</summary>
    /// <exception cref="ArgumentNullException">packDirectory 为 null。</exception>
    public static PackMigrationReport MigratePackToDisk(string packDirectory, string? outputDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(packDirectory);

        var report = MigratePack(packDirectory);
        if (!report.Ok)
        {
            return report;
        }

        Directory.CreateDirectory(report.OutputDirectory);
        foreach (var asset in report.Assets)
        {
            var path = Path.Combine(report.OutputDirectory, asset.AssetId + ".component.json");

            // 幂等：内容逐字节一致＝不重写（重跑不触碰已正确的产物；也避免只读产物上的无谓写失败）。
            if (File.Exists(path)
                && string.Equals(File.ReadAllText(path), asset.Json, StringComparison.Ordinal))
            {
                continue;
            }

            File.WriteAllText(path, asset.Json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        return report;
    }

    /// <summary>迁移一个卡包目录（只读，不落盘）。</summary>
    /// <exception cref="ArgumentNullException">packDirectory 为 null。</exception>
    public static PackMigrationReport MigratePack(string packDirectory)
    {
        ArgumentNullException.ThrowIfNull(packDirectory);

        var effects = Path.Combine(packDirectory, "effects");
        var output = Path.Combine(packDirectory, OutputDirectoryName);
        if (!Directory.Exists(effects))
        {
            return new PackMigrationReport(packDirectory, output, Array.Empty<MigratedAsset>(), Array.Empty<string>());
        }

        var assets = new List<MigratedAsset>();
        var failures = new List<string>();

        foreach (var file in Directory.EnumerateFiles(effects, LegacyFilePattern, SearchOption.TopDirectoryOnly)
                     .OrderBy(static f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            try
            {
                assets.Add(Migrate(File.ReadAllText(file), name));
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
            {
                failures.Add($"{name}：{ex.Message}");
            }
        }

        return new PackMigrationReport(packDirectory, output, assets, failures);
    }

    /// <summary>
    /// 判断某信号成员是否**真的**承载该触发器（S11 的"占位 hooks"判定）。
    /// 未声明的信号＝明确失败（R1e：引用未注册触发器不允许静默）。
    /// <para>
    /// 两条都要满足：①必填字段被信号载荷覆盖；②视图与信号载荷**至少有一个同名字段**
    /// （＝两者在说同一份数据）。只看①会把"空必填视图"（<c>UnitAttackTriggerView</c>／<c>ActionQuotaView</c>
    /// 这类全可选视图）判成真订阅——样本里这些触发器的 hooks 是 <c>card.destroyed</c> 填充值，
    /// 真实接线在 <c>injects</c>；生成出来的挂载边会在卡销毁时误跑 handler（b6 门 B 实测：强磁护盾/机动）。
    /// </para>
    /// </summary>
    private static bool CoversRequiredFields(PrefabType engineType, string signal, ViewSpec view)
    {
        var member = engineType.FindMember(signal)
            ?? throw new InvalidOperationException(
                $"hooks 引用了未声明的信号 '{signal}'：新模型里它必须先是引擎实体的成员（R1e）。");

        var allowed = new HashSet<string>(member.View.Names, StringComparer.Ordinal);
        if (!view.Fields.Any(field => allowed.Contains(field.Name)))
        {
            return false; // 视图与信号载荷无共同字段：hooks 是占位（真实接线在 injects）
        }

        return view.Fields.Where(static f => f.Required).All(f => allowed.Contains(f.Name));
    }

    private static string UniqueMemberName(string eventId, string triggerId, HashSet<string> used)
    {
        var candidate = Sanitize(eventId);
        if (used.Add(candidate))
        {
            return candidate;
        }

        candidate = Sanitize(triggerId) + "." + Sanitize(eventId);
        if (used.Add(candidate))
        {
            return candidate;
        }

        var suffix = 2;
        while (!used.Add(candidate + "#" + suffix))
        {
            suffix++;
        }

        return candidate + "#" + suffix;
    }

    private static string Sanitize(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? "member" : trimmed;
    }

    private static WrapperPhase ParsePhase(string text, string source) => text switch
    {
        "before" => WrapperPhase.Before,
        "after" => WrapperPhase.After,
        "replace" => WrapperPhase.Replace,
        _ => throw new InvalidOperationException($"{source}：未定义的包装相位 '{text}'（before／after／replace）。"),
    };

    private static string RequiredString(JsonElement element, string name, string source)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException($"{source}：缺少必填字符串字段 '{name}'。");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class Counters
    {
        internal int Triggers;
        internal int Handlers;
        internal int ScriptsRewritten;
        internal int Hooks;
        internal int HookMounts;
        internal int VestigialHooks;
        internal int InjectsRewrite;
        internal int InjectsCommit;
        internal int InjectsPlain;
        internal int SelfWraps;
        internal int JudgeWraps;
        internal int DisposalResponses;
        internal int SkippedEmptyTriggers;
        internal int OneShot;
    }
}
