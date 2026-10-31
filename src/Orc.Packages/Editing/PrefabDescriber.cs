using Orc;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Model;

namespace Orc.Packages.Editing;

/// <summary>
/// **预制体视图的投影器**（S10 入口一／D13 的实现体）。
/// <para>
/// 铁律：<b>判定逻辑零新增</b>。成员表、视图字段、可见性、seam、挂载与包装声明**全部**从
/// <see cref="PrefabCatalog.Resolve"/> 的展开结果读（<see cref="PrefabType"/>），本类只做投影
/// ——R4 末条的「与装载期展开结果同源」是这条实现的唯一验收标准，编辑器侧**不得**再写一套展开。
/// </para>
/// <para>
/// 数据源：<c>&lt;pack&gt;/effects-v4/*.component.json</c>（组件资产）＋ <c>&lt;pack&gt;/modings/*.moding.json</c>
/// （草案）。未注册的新包没有 registry 记录，就按包根现场构建一份**临时**
/// <see cref="PrefabCatalog"/>——与已注册包共用同一个 <see cref="PrefabCatalog.Resolve"/>，差异只在 catalog 实例。
/// </para>
/// <para>
/// 引擎层（<c>engine</c>／<c>engine.game</c>／卡牌类型预制体）来自
/// <see cref="GameEnginePrefabs.CreateCatalog"/>：可挂载信号清单必须能看到它们，否则前端拿不到
/// <c>engine#card.placed</c> 这类目标。
/// </para>
/// </summary>
internal static class PrefabDescriber
{
    /// <summary>组件资产目录名（与 <c>PackageScanner</c>／<c>AssetSchemaMigrator</c> 同一约定）。</summary>
    private const string ComponentsDirectory = "effects-v4";

    /// <summary>moding 草案目录名（S11／D11：<c>modings/&lt;localId&gt;.moding.json</c>）。</summary>
    internal const string ModingsDirectory = "modings";

    /// <summary>草案文件后缀。</summary>
    internal const string ModingFileSuffix = ".moding.json";

    /// <summary>成员目标的三种相位（成员外围：before／after／replace 都成立，SPEC 与 <see cref="WrapDeclaration"/>）。</summary>
    private static readonly IReadOnlyList<string> MemberPhases = new[] { "before", "after", "replace" };

    /// <summary>投影一份预制体视图（**不抛异常**：一切失败走 <see cref="PrefabDescription.Diagnostics"/>）。</summary>
    public static PrefabDescription Describe(string packRoot, string prefabAssetId, string? draftPath)
    {
        var diagnostics = new List<PrefabDescriptionDiagnostic>();

        if (string.IsNullOrWhiteSpace(packRoot) || !Directory.Exists(packRoot))
        {
            diagnostics.Add(new PrefabDescriptionDiagnostic(
                PrefabDescriptionDiagnostic.PackMissing, packRoot ?? string.Empty, $"包根不存在：'{packRoot}'。"));
            return Empty(packRoot ?? string.Empty, prefabAssetId, draft: null, diagnostics);
        }

        var root = Path.GetFullPath(packRoot);
        var assets = LoadAssets(root, diagnostics);
        var catalog = BuildCatalog(assets, diagnostics);

        // 父资产未注册＝**返回诊断而不是抛异常**（R1 第 2 条：编辑器据此给出「父未注册」，不展示半张表）。
        if (!catalog.Contains(prefabAssetId))
        {
            diagnostics.Add(new PrefabDescriptionDiagnostic(
                PrefabDescriptionDiagnostic.ParentMissing,
                prefabAssetId,
                $"父资产 '{prefabAssetId}' 未注册（包 '{root}' 内没有该 id 的组件资产）：请先补资产或改选父效果。"));
            return Empty(root, prefabAssetId, ReadDraft(root, draftPath, parent: null, diagnostics: diagnostics), diagnostics);
        }

        PrefabType type;
        try
        {
            type = catalog.Resolve(prefabAssetId);
        }
        catch (Exception ex) when (ex is PrefabLoadException or KeyNotFoundException)
        {
            var category = ex is PrefabLoadException prefab ? prefab.Category : PrefabErrorCategories.Structure;
            diagnostics.Add(new PrefabDescriptionDiagnostic(
                PrefabDescriptionDiagnostic.ParentInvalid,
                prefabAssetId,
                $"父资产 '{prefabAssetId}' 展开失败（[{category}]）：{ex.Message}"));
            return Empty(root, prefabAssetId, ReadDraft(root, draftPath, parent: null, diagnostics: diagnostics), diagnostics);
        }

        var draft = ReadDraft(root, draftPath, type, diagnostics);
        if (draft is not null && draft.DeclaredParentId is { } declared
            && !string.Equals(declared, prefabAssetId, StringComparison.Ordinal))
        {
            diagnostics.Add(new PrefabDescriptionDiagnostic(
                PrefabDescriptionDiagnostic.DraftParentMismatch,
                draft.Path,
                $"草案声明的父是 '{declared}'，与本次查询的父 '{prefabAssetId}' 不一致："
                + "编辑器不得把一份草案叠到别的父上（展开表会与装载期不一致）。"));
        }

        var draftMembers = new Dictionary<string, ComponentMemberAsset>(StringComparer.Ordinal);
        if (draft is not null)
        {
            var draftAsset = TryReadAsset(Path.Combine(root, draft.Path.Replace('/', Path.DirectorySeparatorChar)), diagnostics);
            if (draftAsset is not null)
            {
                foreach (var member in draftAsset.Members)
                {
                    draftMembers[member.Name] = member;
                }
            }
        }

        var members = new List<PrefabMemberDescription>(type.Members.Count + draftMembers.Count);
        foreach (var member in type.Members)
        {
            members.Add(DescribeMember(type, member, draftMembers, draft?.DraftAssetId, assets));
        }

        // 草案新增的成员：父的展开表里没有，直接按草案声明投影（新增＝本层）。
        foreach (var (declaredName, declaredMember) in draftMembers)
        {
            if (type.FindMember(declaredName) is not null)
            {
                continue;
            }

            members.Add(DescribeDraftOnlyMember(declaredName, declaredMember, draft!.DraftAssetId));
        }

        return new PrefabDescription(
            root,
            prefabAssetId,
            Summarize(type),
            draft,
            members,
            DescribeMountableTargets(catalog, diagnostics),
            DescribeWrappableMembers(type, catalog),
            diagnostics);
    }

    // ── 成员表 ──────────────────────────────────────────────────────────────

    private static PrefabMemberDescription DescribeMember(
        PrefabType type,
        MemberDefinition member,
        IReadOnlyDictionary<string, ComponentMemberAsset> draftMembers,
        string? draftAssetId,
        IReadOnlyDictionary<string, ComponentAsset> assets)
    {
        var overridden = draftMembers.TryGetValue(member.Name, out var draftMember);
        // 「继承／本层」的**本层＝草案**（D13）：无草案时全部 inherited:true（那是「打开父效果准备派生」的入口），
        // 有草案时只有被草案重载的成员才是「本层」。父自己的声明层另由 DeclaredBy 给出，不混用这两个概念。
        var inherited = !overridden;

        // 覆写＝整段替换（R3a）：被草案重载的成员，其视图／可见性／seams／封死位读**草案的声明**——
        // 那正是装载期展开时会用到的形态（同源，不猜）。
        var view = overridden ? draftMember!.View : member.View;
        var visibility = overridden ? draftMember!.Visibility : member.Visibility;
        var seams = overridden ? draftMember!.Seams ?? Array.Empty<string>() : member.Seams;
        var sealedMember = overridden ? draftMember!.Sealed : member.Sealed;
        var handlerId = overridden ? draftMember!.Handler?.Id : HandlerOf(member, assets).Id;
        var handlerSource = overridden
            ? SourceNameOf(draftMember!.Handler)
            : HandlerOf(member, assets).Source;

        return new PrefabMemberDescription(
            member.Name,
            "self#" + member.Name,
            inherited,
            overridden,
            Added: false,
            overridden ? draftAssetId : member.DeclaredBy,
            VisibilityNames(visibility),
            member.IsSignal,
            member.IsSignal ? TimingName(member.SignalTiming!.Value) : null,
            sealedMember,
            // 可重载性＝资产级开关 且 未封死（framework. 前缀恒 sealed）。R2 的两级判定在装载期执行，
            // 这里给的是**同一判定**的投影值——前端据此灰掉入口，不重复实现规则。
            type.AuthorizesModing && !(sealedMember || ComponentDisposalPolicy.IsReservedMember(member.Name)),
            seams,
            view.Fields
                .Select(static field => new PrefabViewFieldDescription(
                    field.Name, ViewFieldTypes.Format(field.Type), field.Writable, field.Required))
                .ToArray(),
            handlerSource,
            handlerId,
            type.Mounts
                .Where(mount => string.Equals(mount.Member, member.Name, StringComparison.Ordinal))
                .Select(static mount => mount.Target)
                .ToArray(),
            type.Wraps
                .Where(wrap => wrap.Target.IsMember
                               && string.Equals(wrap.Target.Address.Member, member.Name, StringComparison.Ordinal))
                .Select(static wrap => new PrefabWrapSummary(
                    wrap.Target.Address.ToString(),
                    PhaseName(wrap.Phase),
                    wrap.Seam))
                .ToArray());
    }

    /// <summary>草案新增的成员（父里没有）：新增＝本层，可直接编辑。</summary>
    private static PrefabMemberDescription DescribeDraftOnlyMember(
        string name, ComponentMemberAsset declared, string draftAssetId)
        => new(
            name,
            "self#" + name,
            Inherited: false,
            Overridden: false,
            Added: true,
            draftAssetId,
            VisibilityNames(declared.Visibility),
            IsSignal: false,
            SignalTiming: null,
            declared.Sealed,
            Overridable: false, // 新增成员由本草案拥有，不存在「覆写父」的问题。
            declared.Seams ?? Array.Empty<string>(),
            declared.View.Fields
                .Select(static field => new PrefabViewFieldDescription(
                    field.Name, ViewFieldTypes.Format(field.Type), field.Writable, field.Required))
                .ToArray(),
            SourceNameOf(declared.Handler),
            declared.Handler?.Id,
            Array.Empty<string>(),
            Array.Empty<PrefabWrapSummary>());

    /// <summary>
    /// handler 来源（<c>script</c>／<c>dsl</c>／<c>assembly-key</c>）与 id：读**声明该成员的资产**，
    /// 不重算、不猜。引擎内建成员（<see cref="MemberDefinition.DeclaredBy"/> 为 null）＝无 handler 来源。
    /// <para>
    /// 这条信息服务 R7：无图来源（手写 csx）的成员不显示「可视化编辑」入口。
    /// </para>
    /// </summary>
    private static (string? Id, string? Source) HandlerOf(
        MemberDefinition member, IReadOnlyDictionary<string, ComponentAsset> assets)
    {
        if (member.DeclaredBy is null || !assets.TryGetValue(member.DeclaredBy, out var asset))
        {
            return (null, null);
        }

        var declared = asset.Members.FirstOrDefault(m => string.Equals(m.Name, member.Name, StringComparison.Ordinal));
        return declared?.Handler is { } handler
            ? (handler.Id, SourceNameOf(handler))
            : (null, null);
    }

    /// <summary>handler 来源的文本名（<c>dsl</c>／<c>script</c>／<c>assembly-key</c>；无 handler＝null）。</summary>
    private static string? SourceNameOf(ComponentHandlerAsset? handler) => handler?.SourceKind switch
    {
        HandlerSourceKind.Dsl => "dsl",
        HandlerSourceKind.Script => "script",
        HandlerSourceKind.AssemblyKey => "assembly-key",
        _ => null,
    };

    // ── 可挂载信号清单（R24：每条必须带 timing）──────────────────────────────

    private static IReadOnlyList<PrefabMountableTarget> DescribeMountableTargets(
        PrefabCatalog catalog, List<PrefabDescriptionDiagnostic> diagnostics)
    {
        var targets = new List<PrefabMountableTarget>();
        foreach (var definition in catalog.Definitions.OrderBy(static d => d.Id, StringComparer.Ordinal))
        {
            PrefabType resolved;
            try
            {
                resolved = catalog.Resolve(definition.Id);
            }
            catch (Exception ex) when (ex is PrefabLoadException or KeyNotFoundException)
            {
                // 单个预制体展开失败不该让整张信号清单消失（父的展开已单独报错）。
                diagnostics.Add(new PrefabDescriptionDiagnostic(
                    PrefabDescriptionDiagnostic.AssetInvalid, definition.Id, $"预制体 '{definition.Id}' 展开失败：{ex.Message}"));
                continue;
            }

            foreach (var member in resolved.Members)
            {
                if (!member.IsSignal || !member.Has(MemberVisibility.Hostable))
                {
                    continue;
                }

                targets.Add(new PrefabMountableTarget(
                    "engine#" + member.Name,
                    member.Name,
                    TimingName(member.SignalTiming!.Value),
                    definition.Id,
                    member.View.Fields
                        .Select(static field => new PrefabViewFieldDescription(
                            field.Name, ViewFieldTypes.Format(field.Type), field.Writable, field.Required))
                        .ToArray()));
            }
        }

        return targets
            .GroupBy(static t => t.Address, StringComparer.Ordinal)
            .Select(static g => g.First())
            .OrderBy(static t => t.Address, StringComparer.Ordinal)
            .ToArray();
    }

    // ── 可包装成员与相位（R24：writableFields 与视图声明同源）────────────────

    private static IReadOnlyList<PrefabWrappableMember> DescribeWrappableMembers(PrefabType type, PrefabCatalog catalog)
    {
        var members = new List<PrefabWrappableMember>();

        // 本资产自身的成员：编辑器最常包的就是它们（self#…）。
        members.AddRange(type.Members.Select(member => Wrappable(
            "self#" + member.Name, member)));

        // 引擎层成员（信号与流程成员）：wrap 也常常落在 engine#… 上。
        foreach (var definition in catalog.Definitions.OrderBy(static d => d.Id, StringComparer.Ordinal))
        {
            if (definition.Layer is not (PrefabLayer.Kernel or PrefabLayer.Game))
            {
                continue;
            }

            PrefabType resolved;
            try
            {
                resolved = catalog.Resolve(definition.Id);
            }
            catch (Exception ex) when (ex is PrefabLoadException or KeyNotFoundException)
            {
                continue;
            }

            members.AddRange(resolved.Members.Select(member => Wrappable(
                "engine#" + member.Name, member)));
        }

        return members
            .GroupBy(static m => m.Address, StringComparer.Ordinal)
            .Select(static g => g.First())
            .OrderBy(static m => m.Address, StringComparer.Ordinal)
            .ToArray();
    }

    private static PrefabWrappableMember Wrappable(string address, MemberDefinition member)
        => new(
            address,
            member.Name,
            MemberPhases,
            member.Seams,
            // 可写字段＝视图声明里 writable:true 的那些（与 PrefabMemberDescription.ViewFields[].Writable 同源）。
            member.View.Fields.Where(static f => f.Writable).Select(static f => f.Name).ToArray());

    // ── 草案 ────────────────────────────────────────────────────────────────

    private static PrefabDraftSummary? ReadDraft(
        string root, string? draftPath, PrefabType? parent, List<PrefabDescriptionDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(draftPath))
        {
            return null;
        }

        var absolute = Path.IsPathRooted(draftPath)
            ? draftPath
            : Path.Combine(root, draftPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(absolute))
        {
            diagnostics.Add(new PrefabDescriptionDiagnostic(
                PrefabDescriptionDiagnostic.DraftMissing, draftPath, $"草案文件不存在：'{draftPath}'。"));
            return null;
        }

        var asset = TryReadAsset(absolute, diagnostics);
        if (asset is null)
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, absolute).Replace(Path.DirectorySeparatorChar, '/');

        // 重载 vs 新增：以**父的展开表**为判据（父没注册＝全部按重载候选列出、并已有 parent-missing 诊断）。
        var overridden = new List<string>();
        var added = new List<string>();
        foreach (var member in asset.Members)
        {
            if (parent is null || parent.FindMember(member.Name) is not null)
            {
                overridden.Add(member.Name);
            }
            else
            {
                added.Add(member.Name);
            }
        }

        return new PrefabDraftSummary(
            relative,
            asset.Id,
            asset.Extends,
            overridden,
            added,
            asset.Mounts
                .Select(static m => new PrefabDeclarationSummary(m.Target, m.Member))
                .ToArray(),
            asset.Wraps
                .Select(static w => new PrefabWrapSummary(
                    w.Target.IsMember ? w.Target.Address.ToString() : "judicator:" + w.Target.Judicator,
                    PhaseName(w.Phase),
                    w.Seam))
                .ToArray());
    }

    // ── 资产读取与目录构建 ──────────────────────────────────────────────────

    /// <summary>读包内全部组件资产与草案（解析失败逐个记诊断，继续读其余的）。</summary>
    private static Dictionary<string, ComponentAsset> LoadAssets(
        string root, List<PrefabDescriptionDiagnostic> diagnostics)
    {
        var assets = new Dictionary<string, ComponentAsset>(StringComparer.Ordinal);

        foreach (var file in EnumerateAssets(root, ComponentsDirectory, "*.component.json"))
        {
            Add(TryReadAsset(file, diagnostics), file);
        }

        foreach (var file in EnumerateAssets(root, ModingsDirectory, "*" + ModingFileSuffix))
        {
            Add(TryReadAsset(file, diagnostics), file);
        }

        return assets;

        void Add(ComponentAsset? asset, string file)
        {
            if (asset is null)
            {
                return;
            }

            if (!assets.TryAdd(asset.Id, asset))
            {
                diagnostics.Add(new PrefabDescriptionDiagnostic(
                    PrefabDescriptionDiagnostic.AssetInvalid,
                    file,
                    $"同一包内重复声明资产 id '{asset.Id}'（{Path.GetFileName(file)}）：只取第一份。"));
            }
        }
    }

    private static IEnumerable<string> EnumerateAssets(string root, string directory, string pattern)
    {
        var path = Path.Combine(root, directory);
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(static f => f, StringComparer.Ordinal)
            : Enumerable.Empty<string>();
    }

    private static ComponentAsset? TryReadAsset(string path, List<PrefabDescriptionDiagnostic> diagnostics)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return AssetSchema.Read(File.ReadAllText(path), Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is PrefabLoadException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new PrefabDescriptionDiagnostic(
                PrefabDescriptionDiagnostic.AssetInvalid,
                Path.GetFileName(path),
                $"资产不可解析：{ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// 构建临时目录：引擎层（内核／游戏层预制体）＋ 包内全部组件资产。
    /// **共用** <see cref="PrefabCatalog.Resolve"/>——差异只在 catalog 实例（未注册的新包也有同一展开语义）。
    /// </summary>
    private static PrefabCatalog BuildCatalog(
        IReadOnlyDictionary<string, ComponentAsset> assets, List<PrefabDescriptionDiagnostic> diagnostics)
    {
        var catalog = GameEnginePrefabs.CreateCatalog();

        foreach (var asset in assets.Values.OrderBy(static a => a.Id, StringComparer.Ordinal))
        {
            try
            {
                catalog.Add(asset.ToStructuralPrefabDefinition());
            }
            catch (Exception ex) when (ex is PrefabLoadException or ArgumentException)
            {
                // 父已单独报错时这类重复条目会再冒一次；这里只记诊断，不阻断其余资产。
                diagnostics.Add(new PrefabDescriptionDiagnostic(
                    PrefabDescriptionDiagnostic.AssetInvalid, asset.Id, $"资产 '{asset.Id}' 无法进入目录：{ex.Message}"));
            }
        }

        return catalog;
    }

    // ── 小工具（格式与装载期同一口径）────────────────────────────────────────

    private static PrefabDescription Empty(
        string packRoot,
        string prefabAssetId,
        PrefabDraftSummary? draft,
        List<PrefabDescriptionDiagnostic> diagnostics)
        => new(
            packRoot,
            prefabAssetId,
            Prefab: null,
            draft,
            Array.Empty<PrefabMemberDescription>(),
            Array.Empty<PrefabMountableTarget>(),
            Array.Empty<PrefabWrappableMember>(),
            diagnostics);

    private static PrefabSummary Summarize(PrefabType type)
        => new(
            type.Id,
            type.Kind.ToString().ToLowerInvariant(),
            type.Layer.ToString().ToLowerInvariant(),
            type.Chain,
            AssetSchema.FormatHostKind(type.RequiresHost),
            type.ActiveWhen == ActiveWhen.Loaded ? "loaded" : "placed",
            type.OneShot,
            type.DisposalSignal,
            type.AuthorizesModing);

    private static IReadOnlyList<string> VisibilityNames(MemberVisibility visibility)
    {
        var names = new List<string>(3);
        if ((visibility & MemberVisibility.Callable) != 0)
        {
            names.Add("callable");
        }

        if ((visibility & MemberVisibility.Hostable) != 0)
        {
            names.Add("hostable");
        }

        if ((visibility & MemberVisibility.Mountable) != 0)
        {
            names.Add("mountable");
        }

        return names;
    }

    private static string TimingName(SignalTiming timing) => timing switch
    {
        SignalTiming.Load => "load",
        SignalTiming.Placed => "placed",
        _ => "any",
    };

    private static string PhaseName(WrapperPhase phase) => phase switch
    {
        WrapperPhase.Before => "before",
        WrapperPhase.After => "after",
        _ => "replace",
    };
}
