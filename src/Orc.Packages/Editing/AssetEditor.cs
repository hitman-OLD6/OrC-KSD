using Orc.Core;
using Orc.Packages.Fs;

namespace Orc.Packages.Editing;

/// <summary>
/// **后端编辑器**（R9/R12；S8）：编辑器表达力的唯一来源——资产版本／依赖／冲突／保存／转正。
/// <para>
/// 分层约束：本类型位于 <c>Orc.Packages</c>，**不得引用 <c>Orc.Mcp</c>**（工具面在 S9 由 MCP 侧包装）。
/// 路径规则只有一份（<see cref="PathNormalizer"/>）；写权限由 <see cref="IEditableRootPolicy"/> 判定。
/// </para>
/// <para>
/// 硬约束：
/// <list type="bullet">
///   <item>构造函数**强制注入策略**——没有无策略重载、没有默认实现（缺策略＝编译期不可构造）。</item>
///   <item>**会话单例**：同时只允许一个编辑会话（同一实例内）。</item>
///   <item>前端只能写临时包；正式包在转正前只读。</item>
///   <item>能力集对齐 R9：<c>pack.list</c>／<c>session.begin/end</c>／<c>asset.list/get/commit/create/delete</c>／
///     <c>version.set</c>／<c>dep.set</c>／<c>lock.preview</c>／<c>promote</c>／<c>reload</c>。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AssetEditor
{
    private readonly object _gate = new();
    private readonly IEditableRootPolicy _policy;
    private readonly AssetEditorOptions _options;
    private readonly AssetLoader _loader;
    private AssetEditorSession? _session;

    /// <summary>
    /// 构造编辑器（**策略必填**：沙盒模式注入单根实现，非沙盒由前端配置根列表）。
    /// </summary>
    /// <param name="registry">资产注册表（`reload` 的计算面）。</param>
    /// <param name="policy">可编辑根策略（授权根构造期固定、会话期不得扩权）。</param>
    /// <param name="options">可选项（可编辑包根／装载器）；null＝默认。</param>
    /// <exception cref="ArgumentNullException">registry／policy 为 null。</exception>
    public AssetEditor(AssetRegistry registry, IEditableRootPolicy policy, AssetEditorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);

        _policy = policy;
        _options = options ?? new AssetEditorOptions();
        _loader = _options.Loader ?? new AssetLoader(registry);
    }

    /// <summary>授权根策略（只读；会话期不得扩权）。</summary>
    public IEditableRootPolicy Policy => _policy;

    /// <summary>是否有活动编辑会话。</summary>
    public bool HasActiveSession
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    /// <summary>当前会话快照（无会话＝null）。</summary>
    public EditorSessionInfo? Session
    {
        get
        {
            lock (_gate)
            {
                return _session?.Describe();
            }
        }
    }

    /// <summary>`pack.list`：可编辑卡包（插件与 <c>*.tmp</c> 目录不列出）。</summary>
    public IReadOnlyList<EditablePackageInfo> ListPackages()
    {
        var roots = _options.PackageRoots.Count > 0
            ? _options.PackageRoots
            : _policy.Roots;

        var result = new List<EditablePackageInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in roots)
        {
            NormalizedPath normalized;
            try
            {
                normalized = PathNormalizer.Normalize(root);
            }
            catch (PathViolationException)
            {
                continue;
            }

            if (!Directory.Exists(normalized.Value))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(normalized.Value).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(directory);
                if (name.EndsWith(AssetEditorSession.TemporarySuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                var manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                PackageManifest manifest;
                try
                {
                    manifest = PackageManifest.Parse(File.ReadAllText(manifestPath), out _);
                }
                catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException)
                {
                    continue;
                }

                if (manifest.Kind != PackageKind.CardPack)
                {
                    continue;
                }

                var rootPath = Path.GetFullPath(directory);
                if (!seen.Add(rootPath))
                {
                    continue;
                }

                result.Add(new EditablePackageInfo(
                    manifest.Id,
                    manifest.Name,
                    rootPath,
                    manifest.Version,
                    Directory.Exists(rootPath + AssetEditorSession.TemporarySuffix)));
            }
        }

        return result;
    }

    /// <summary>
    /// `session.begin`：复制到 <c>&lt;包目录&gt;.tmp/</c>（同级兄弟）或为新建包建临时目录；
    /// **同时只允许一个会话**，且临时包已存在即失败（不清理、不复用）。
    /// </summary>
    /// <exception cref="AssetEditorException">会话已存在／临时包已存在／路径未授权／包非法。</exception>
    public EditorSessionInfo BeginSession(EditorSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (_session is not null)
            {
                throw new AssetEditorException(
                    AssetEditorErrorCode.SessionAlreadyActive,
                    $"已有编辑会话（包 '{_session.PackageId}'，临时包 '{_session.TemporaryRoot}'）；请先 session.end。");
            }

            var session = AssetEditorSession.Open(request, _policy, ListPackages());
            _session = session;
            return session.Describe();
        }
    }

    /// <summary>`session.end`：<see cref="EditorSessionEndMode.Save"/>＝转正；<see cref="EditorSessionEndMode.Discard"/>＝放弃。</summary>
    /// <exception cref="AssetEditorException">无会话／转正失败。</exception>
    public EditorPromotionResult EndSession(EditorSessionEndMode mode)
    {
        lock (_gate)
        {
            var session = RequireSession();
            if (mode == EditorSessionEndMode.Discard)
            {
                session.Discard();
                _session = null;
                return new EditorPromotionResult(
                    session.PackageId,
                    session.PackageRoot,
                    session.IsNewPackage,
                    Array.Empty<EditorAssetInfo>(),
                    AssetLock.Create(session.PackageId, AssetDomain.Formal, Array.Empty<AssetLockEntry>()),
                    AssetLock.Create(session.PackageId, AssetDomain.Test, Array.Empty<AssetLockEntry>()),
                    Array.Empty<string>());
            }

            var result = session.Promote();
            _session = null;
            return result;
        }
    }

    /// <summary>`promote`：转正（等价于 `session.end(save)`）。</summary>
    /// <exception cref="AssetEditorException">无会话／转正失败。</exception>
    public EditorPromotionResult Promote() => EndSession(EditorSessionEndMode.Save);

    /// <summary>`asset.list`：进程内 lambda 过滤（null＝全部）。</summary>
    /// <param name="filter">过滤谓词（进程内 API 用；工具面用 <see cref="EditorAssetQuery"/>）。</param>
    /// <exception cref="AssetEditorException">无会话。</exception>
    public IReadOnlyList<EditorAssetInfo> ListAssets(Func<EditorAssetInfo, bool>? filter = null)
    {
        lock (_gate)
        {
            return RequireSession().ListAssets(filter);
        }
    }

    /// <summary>`asset.list`（结构化过滤；与 lambda 在同一后端组件内求值）。</summary>
    /// <param name="query">结构化过滤条件。</param>
    /// <exception cref="AssetEditorException">无会话。</exception>
    public IReadOnlyList<EditorAssetInfo> ListAssets(EditorAssetQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_gate)
        {
            return RequireSession().ListAssets(query.Matches);
        }
    }

    /// <summary>`asset.get`。</summary>
    /// <exception cref="AssetEditorException">无会话／资产未声明。</exception>
    public EditorAssetInfo GetAsset(AssetKind kind, string localId)
    {
        lock (_gate)
        {
            return RequireSession().GetAsset(kind, localId);
        }
    }

    /// <summary>`asset.commit`（文件指针通道：前端已直写，后端读文件重解析）。</summary>
    /// <exception cref="AssetEditorException">无会话／路径越界／内容不可解析。</exception>
    public EditorCommitResult Commit(string relativePath)
    {
        lock (_gate)
        {
            return RequireSession().Commit(relativePath, content: null);
        }
    }

    /// <summary>`asset.commit`（内容通道：工具／agent 直传，后端写盘）。</summary>
    /// <exception cref="AssetEditorException">无会话／路径越界／内容不可解析。</exception>
    public EditorCommitResult Commit(string relativePath, string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        lock (_gate)
        {
            return RequireSession().Commit(relativePath, content);
        }
    }

    /// <summary>`asset.create`（版本从 <c>1.0.0</c> 起）。</summary>
    /// <exception cref="AssetEditorException">无会话／重复声明／源文件缺失。</exception>
    public EditorAssetInfo CreateAsset(
        AssetKind kind, string localId, string? sourcePath = null, string? anchor = null, string? loader = null)
    {
        lock (_gate)
        {
            return RequireSession().CreateAsset(kind, localId, sourcePath, anchor, loader);
        }
    }

    /// <summary>`asset.delete`（当前版移出声明／lock；历史版本仍在 <c>versions/</c>）。</summary>
    /// <exception cref="AssetEditorException">无会话／资产未声明。</exception>
    public void DeleteAsset(AssetKind kind, string localId)
    {
        lock (_gate)
        {
            RequireSession().DeleteAsset(kind, localId);
        }
    }

    /// <summary>`version.set`：null／空白＝默认自动递增（尾段 +1）；手写须严格大于旧版。</summary>
    /// <exception cref="AssetEditorException">无会话／版本非法或未严格递增。</exception>
    public EditorAssetInfo SetVersion(AssetKind kind, string localId, string? version)
    {
        lock (_gate)
        {
            return RequireSession().SetVersion(kind, localId, version);
        }
    }

    /// <summary>`dep.set`：修改资产依赖（测试 lock 随之重算）。</summary>
    /// <exception cref="AssetEditorException">无会话／依赖非法。</exception>
    public EditorAssetInfo SetDependencies(AssetKind kind, string localId, IEnumerable<AssetReference> dependencies)
    {
        lock (_gate)
        {
            return RequireSession().SetDependencies(kind, localId, dependencies);
        }
    }

    /// <summary>`lock.preview`：转正后的正式 lock（预测）＋ 当前测试 lock（实况）。</summary>
    /// <exception cref="AssetEditorException">无会话。</exception>
    public EditorLockPreview PreviewLocks()
    {
        lock (_gate)
        {
            return RequireSession().PreviewLocks();
        }
    }

    /// <summary>`reload`：合并各包 lock ＋ 当前测试 lock 后经资产加载器装载（结构化返回，不抛错）。</summary>
    public EditorReloadResult Reload()
    {
        lock (_gate)
        {
            var locks = new List<AssetLock>();
            foreach (var package in ListPackages())
            {
                foreach (var domain in new[] { AssetDomain.Formal, AssetDomain.Test })
                {
                    try
                    {
                        if (AssetLockJson.ReadPackage(package.RootDirectory, domain) is { } @lock)
                        {
                            locks.Add(@lock);
                        }
                    }
                    catch (Exception ex) when (ex is FormatException or IOException)
                    {
                        // 单包 lock 不可解析＝不并入（加载器按结构性非法报告其余集合）。
                    }
                }
            }

            if (_session is not null)
            {
                locks.Add(_session.BuildTestLock());
            }

            return new EditorReloadResult(_options.Loader is not null, _loader.Load(locks));
        }
    }

    private AssetEditorSession RequireSession()
        => _session ?? throw new AssetEditorException(
            AssetEditorErrorCode.NoSession, "没有活动编辑会话（请先 session.begin）。");
}
