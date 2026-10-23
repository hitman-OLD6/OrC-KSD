using System.Text.Json.Nodes;
using Orc.Core;
using Orc.Mcp.Contracts;
using Orc.Mcp.Pool;
using Orc.Packages;
using Orc.Packages.Loading;
using Orc.Server.Execution;

namespace Orc.Server.Pool;

/// <summary>
/// 开局装配的输入快照（响应与事件用；**事件侧不带目录**）。
/// </summary>
/// <param name="Directories">本局喂给装配链的目录（集合条目按注册序 ＋ 合法沙盒包）。</param>
/// <param name="Packages">集合条目（id/version；不含目录）。</param>
/// <param name="SandboxIncluded">沙盒固定包是否进入了本次输入。</param>
/// <param name="Reconciled">本次开局前真对账移除的条目。</param>
public sealed record PackAssemblyInput(
    IReadOnlyList<string> Directories,
    IReadOnlyList<PoolEntry> Packages,
    bool SandboxIncluded,
    IReadOnlyList<PoolEntry> Reconciled);

/// <summary>
/// **已注册包集合**（进程级、可变；S1）：实例持有的「本实例下一局要加载哪些包」。
/// <para>
/// 生命周期：一次创建、跨房间轮换存活（集合是实例状态，R1）；进程重启回到 spawn 初值，不写任何持久化配置。
/// 沙盒固定包（<c>&lt;sandboxRoot&gt;/pack</c>）**不进集合**、不受注册/注销影响，每局强制加入。
/// </para>
/// <para>
/// **队列纪律**（S1）：核心方法同步且共用同一把 <c>lock</c>，核心绝不调 <c>InQueueAsync</c>：
/// 写操作走自适应入口（消费者上下文内直调，否则入队包裹）；<c>pool.list</c> 不入队、在同一把锁内取快照＋预演。
/// </para>
/// <para>
/// **对账**（S2）：读面只做预演（不写集合、不写盘）；真对账只发生在写面与开局路径，**独立提交**
/// （不随随后的变更被拒或开局失败而回滚）。
/// </para>
/// </summary>
public sealed class PackPool : IPoolControl
{
    /// <summary>轮换 drain 窗口内写操作的可执行说明（**管理面与工具面逐字一致**）。</summary>
    public const string DrainMessage =
        "房间正在轮换，卡包集合变更需等新 lobby 就绪后重试；`pool.list` 不受影响。";

    /// <summary>注册沙盒固定包目录时的固定说明。</summary>
    public const string SandboxDirectoryMessage =
        "该目录是沙盒固定包（<sandboxRoot>/pack）：它每局强制加入、不属于已注册包集合，"
        + "不受注册/注销影响；清空请用 sandbox.reset。";

    private readonly object _sync = new();
    private readonly SerialWorkQueue _queue;
    private readonly Func<bool> _isDraining;
    private readonly Action<string, JsonNode>? _publish;
    private readonly List<PackPoolEntry> _entries = new();
    private readonly List<string> _initialDirectories = new();
    private readonly string _sandboxDirectory;

    private IReadOnlyList<string>? _lastGood;

    // `pool.list` 的单飞（S1）：并发调用合并为一次计算、共享结果，**算完即弃**（无 TTL、无条目级缓存）。
    private readonly object _flightSync = new();
    private TaskCompletionSource<AssemblyInspection>? _flight;

    /// <summary>
    /// 创建集合载体（初值＝spawn 的 <c>packDirectories</c>，剔除沙盒固定包、保留原顺序）。
    /// </summary>
    /// <param name="options">启动选项（读初值目录与沙盒根）。</param>
    /// <param name="queue">串行队列（写操作的自适应入口用）。</param>
    /// <param name="isDraining">轮换 drain 判据（写操作在窗口内被拒）。</param>
    /// <param name="publish">通道事件出口（<c>packset.*</c>；<c>null</c>＝不发事件）。</param>
    public PackPool(
        ServerOptions options,
        SerialWorkQueue queue,
        Func<bool> isDraining,
        Action<string, JsonNode>? publish = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(isDraining);

        _queue = queue;
        _isDraining = isDraining;
        _publish = publish;
        _sandboxDirectory = Path.GetFullPath(Path.Combine(options.SandboxRoot, "pack"));

        foreach (var directory in options.PackDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue; // 启动期已校验（空白项不会走到这里）
            }

            var normalized = Normalize(directory);
            if (IsSandboxDirectory(normalized))
            {
                // R7：沙盒固定包不属于集合（初值里出现只会诱导调用方去注销它）。
                continue;
            }

            if (_entries.Any(entry => PathEquals(entry.Directory, normalized)))
            {
                continue; // 同一目录重复列出＝一条条目
            }

            _initialDirectories.Add(normalized);
            var probe = PackageDirectoryProbe.Probe(normalized);
            _entries.Add(new PackPoolEntry(
                normalized,
                probe.Ok ? probe.PackageId : null,
                probe.PackageId,
                probe.Version,
                probe.Guid));
        }
    }

    /// <summary>沙盒固定包目录（绝对路径；不进集合）。</summary>
    public string SandboxDirectory => _sandboxDirectory;

    /// <summary>spawn 初值（历史信息；已剔除沙盒固定包、保留 spawn 顺序）。</summary>
    public IReadOnlyList<string> InitialDirectories => _initialDirectories.ToArray();

    /// <summary>最后一次**成功**装配的输入（R9 回退用；<c>null</c>＝尚未有过成功装配）。</summary>
    public IReadOnlyList<string>? LastGood
    {
        get
        {
            lock (_sync)
            {
                return _lastGood;
            }
        }
    }

    /// <summary>登记一次成功装配的输入（供下一次失败时回退；不进试算，S3 裁决）。</summary>
    /// <param name="directories">本次真正装配成功使用的目录列表。</param>
    public void MarkLastGood(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        lock (_sync)
        {
            _lastGood = directories.ToArray();
        }
    }

    /// <summary>
    /// 开局前的输入快照：**先真对账（提交）**，再给出「集合全部条目 ＋ 合法沙盒包」。
    /// <para>对账移除**独立提交**：本局后续失败（卡组／AI／引擎拒绝）不回滚它。</para>
    /// </summary>
    public PackAssemblyInput PrepareAssembly()
    {
        List<PoolEntry> reconciled;
        PackAssemblyInput input;

        lock (_sync)
        {
            reconciled = Reconcile();
            var directories = _entries.Select(entry => entry.Directory).ToList();
            var sandbox = Directory.Exists(_sandboxDirectory);
            if (sandbox)
            {
                directories.Add(_sandboxDirectory);
            }

            input = new PackAssemblyInput(
                directories,
                _entries.Select(entry => new PoolEntry(entry.PackageId ?? "unknown", entry.Version)).ToArray(),
                sandbox,
                reconciled);
        }

        EmitReconciled(reconciled);
        return input;
    }

    // ---------------------------------------------------------------- 读面（预演）

    /// <inheritdoc />
    public PoolCallResult Snapshot(PoolAudience audience)
    {
        // 单飞（S1）：同一时刻的多个调用合并为一次预演、共享同一份结果；**算完即弃**（不跨时刻保留）。
        TaskCompletionSource<AssemblyInspection> flight;
        var owner = false;
        lock (_flightSync)
        {
            if (_flight is null)
            {
                _flight = new TaskCompletionSource<AssemblyInspection>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                owner = true;
            }

            flight = _flight;
        }

        if (owner)
        {
            try
            {
                flight.SetResult(PreviewSafely());
            }
            catch (Exception ex)
            {
                flight.SetException(ex);
            }
            finally
            {
                lock (_flightSync)
                {
                    _flight = null; // 不缓存：下一次调用重新算
                }
            }
        }

        var model = flight.Task.GetAwaiter().GetResult();
        var hasEntries = HasEntries();

        var empty = model.Issues.Any(issue => issue.Code == AssetLoadIssueCode.EmptyPool);
        var code = model.Ok ? McpCodes.Ok : McpCodes.MissingAssets;
        var message = model.Ok
            ? "已注册包集合已读取（下一局开局会用它装配卡池）。"
            : empty || !hasEntries
                ? "卡包集合为空：没有任何可装载的卡包（请先 pool.register）。"
                : "卡包集合当前不可装配（见 issues；下一次开局会失败）。";

        return new PoolCallResult(model.Ok, code, message, model);
    }

    private bool HasEntries()
    {
        lock (_sync)
        {
            return _entries.Count > 0;
        }
    }

    // ---------------------------------------------------------------- 写面（事务边界）

    /// <inheritdoc />
    public Task<PoolCallResult> RegisterAsync(string directory, PoolAudience audience, CancellationToken ct = default)
    {
        var reconciled = new List<PoolEntry>();
        return RunWriteAsync(() => RegisterCore(directory, reconciled), reconciled, ct);
    }

    /// <inheritdoc />
    public Task<PoolCallResult> UnregisterAsync(
        string packageId, string version, PoolAudience audience, CancellationToken ct = default)
    {
        var reconciled = new List<PoolEntry>();
        return RunWriteAsync(() => UnregisterCore(packageId, version, reconciled), reconciled, ct);
    }

    /// <summary>自适应入口：消费者上下文内直调核心，否则入队包裹（绝不从核心内部再入队）。</summary>
    private async Task<PoolCallResult> RunWriteAsync(
        Func<PoolCallResult> core, List<PoolEntry> reconciled, CancellationToken ct)
    {
        if (_isDraining())
        {
            return Failure(
                McpCodes.RoomRotating,
                DrainMessage,
                PreviewSafely(),
                new JsonObject { ["retryAfterMs"] = 200 });
        }

        PoolCallResult result;
        try
        {
            result = SerialWorkQueue.IsConsumerContext
                ? core()
                : await _queue.EnqueueAsync(WorkPriority.Command, _ => Task.FromResult(core()), ct).ConfigureAwait(false);
        }
        catch (WorkQueueFullException)
        {
            return Failure(McpCodes.Busy, "服务繁忙，请稍后再试。", PreviewSafely());
        }
        catch (ObjectDisposedException)
        {
            return Failure(McpCodes.RoomRotating, "服务端正在关闭。", PreviewSafely());
        }

        // 对账是独立事件：即使本次变更被拒也照发（S4：对账不回滚）。
        EmitReconciled(reconciled);
        if (result.Ok)
        {
            EmitChanged(result);
        }

        return result;
    }

    /// <summary>注册的核心事务：先对账 → 读盘登记 → 候选试算 → 通过才写入。</summary>
    private PoolCallResult RegisterCore(string directory, List<PoolEntry> reconciled)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return Failure(McpCodes.InvalidArguments, "缺少必填参数 directory（卡包绝对目录路径）。", PreviewViews(_entries.Select(Read).ToList()));
            }

            if (!Path.IsPathRooted(directory))
            {
                return Failure(
                    McpCodes.InvalidArguments,
                    "pool.register 需要绝对目录路径（不按进程 cwd 解析）。",
                    PreviewViews(_entries.Select(Read).ToList()));
            }

            string normalized;
            try
            {
                normalized = Normalize(directory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Failure(McpCodes.InvalidArguments, $"目录路径不可用：{ex.Message}", PreviewViews(_entries.Select(Read).ToList()));
            }

            if (IsSandboxDirectory(normalized))
            {
                return Failure(McpCodes.InvalidArguments, SandboxDirectoryMessage, PreviewViews(_entries.Select(Read).ToList()));
            }

            // ① 真对账（提交）：失效条目 / 重复键 / 结构性非法条目在此被移除。
            reconciled.AddRange(Reconcile());

            // ② 读盘登记（(id, version) 是快照，不是身份）。
            var probe = PackageDirectoryProbe.Probe(normalized);
            if (!probe.Ok)
            {
                return Failure(
                    RegistrationCode(probe),
                    probe.Error ?? "该目录不是可用的卡包。",
                    PreviewViews(_entries.Select(Read).ToList()),
                    RegistrationDetails(probe),
                    reconciled);
            }

            var id = probe.PackageId!;
            var version = probe.Version!;

            // ③ 幂等：同一目录已在册（版本可能已随磁盘漂移刷新）——集合不变。
            if (_entries.FirstOrDefault(entry => PathEquals(entry.Directory, normalized)) is { } existingDirectory)
            {
                return new PoolCallResult(
                    true,
                    McpCodes.Ok,
                    $"该目录已在已注册集合中（{id}#{existingDirectory.Version}）：集合不变。",
                    PreviewViews(_entries.Select(Read).ToList()),
                    Added: Array.Empty<PoolEntry>(),
                    Removed: Array.Empty<PoolEntry>(),
                    Existed: true,
                    RemovedByReconcile: reconciled.ToArray());
            }

            // ④ 重复键：同 key 不同路径 ⇒ 拒绝（两个目录未来会各自漂移，必须由人先决策）。
            if (_entries.FirstOrDefault(entry =>
                    string.Equals(entry.PackageId, id, StringComparison.Ordinal)
                    && string.Equals(entry.Version, version, StringComparison.Ordinal)) is { } sameKey)
            {
                return new PoolCallResult(
                    false,
                    McpCodes.InvalidArguments,
                    $"同一 (id, version)＝({id}, {version}) 已由另一个目录注册：请先 pool.unregister 再注册。",
                    PreviewViews(_entries.Select(Read).ToList()),
                    Added: Array.Empty<PoolEntry>(),
                    Removed: Array.Empty<PoolEntry>(),
                    RemovedByReconcile: reconciled.ToArray(),
                    Details: new JsonObject { ["existingDirectory"] = sameKey.Directory });
            }

            // ⑤ 候选试算：失效条目的 lock 不参与（它们已被对账移除）；准入看 fatal 不看 ok。
            var candidate = new List<PackPoolEntry>(_entries)
            {
                new(normalized, id, id, version, probe.Guid), // 注册序 append
            };

            var trial = PreviewViews(candidate.Select(Read).ToList());
            if (RejectReason(trial) is { } reason)
            {
                return Rejected(reason, trial, reconciled);
            }

            // ⑥ 提交（append：条目顺序＝注册序）。
            _entries.Add(new PackPoolEntry(normalized, id, id, version, probe.Guid));
            return new PoolCallResult(
                true,
                McpCodes.Ok,
                $"已注册 {id}#{version}（仅影响下一局开局）。",
                trial,
                Added: [new PoolEntry(id, version)],
                Removed: Array.Empty<PoolEntry>(),
                RemovedByReconcile: reconciled.ToArray());
        }
    }

    /// <summary>注销的核心事务：先对账 → 按当前快照精确匹配 → 候选试算 → 通过才移除。</summary>
    private PoolCallResult UnregisterCore(string packageId, string version, List<PoolEntry> reconciled)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
            {
                return Failure(
                    McpCodes.InvalidArguments,
                    "pool.unregister 需要 packageId 与 version 两项（只用 id 不足以移除）。",
                    PreviewViews(_entries.Select(Read).ToList()));
            }

            reconciled.AddRange(Reconcile());

            var entry = _entries.FirstOrDefault(item => string.Equals(item.PackageId, packageId, StringComparison.Ordinal));
            if (entry is null)
            {
                return Failure(
                    McpCodes.InvalidArguments,
                    $"包 '{packageId}' 不在已注册集合中（可能已被对账移除）：请先 pool.list 查看当前快照。",
                    PreviewViews(_entries.Select(Read).ToList()),
                    details: null,
                    reconciled);
            }

            if (!string.Equals(entry.Version, version, StringComparison.Ordinal))
            {
                return new PoolCallResult(
                    false,
                    McpCodes.VersionMismatch,
                    $"包 '{packageId}' 的版本与当前快照不符：当前 {entry.Version}（传入 {version}）。",
                    PreviewViews(_entries.Select(Read).ToList()),
                    Added: Array.Empty<PoolEntry>(),
                    Removed: Array.Empty<PoolEntry>(),
                    RemovedByReconcile: reconciled.ToArray(),
                    Details: new JsonObject { ["currentVersion"] = entry.Version });
            }

            var candidate = _entries
                .Where(item => !ReferenceEquals(item, entry))
                .Select(item => new PackPoolEntry(item.Directory, item.ExpectedPackageId, item.PackageId, item.Version, item.Guid))
                .ToList();

            // 允许注销到空集合：EmptyPool 是 ok=false＋fatal=false，故不会被拒绝。
            var trial = PreviewViews(candidate.Select(Read).ToList());
            if (RejectReason(trial) is { } reason)
            {
                return Rejected(reason, trial, reconciled);
            }

            _entries.Remove(entry);
            return new PoolCallResult(
                true,
                McpCodes.Ok,
                $"已注销 {packageId}#{version}（仅影响下一局开局）。",
                trial,
                Added: Array.Empty<PoolEntry>(),
                Removed: [new PoolEntry(packageId, version)],
                RemovedByReconcile: reconciled.ToArray());
        }
    }

    // ---------------------------------------------------------------- 对账（真对账：提交）

    /// <summary>
    /// 真对账：按当前磁盘状态判定条目有效性并**提交**移除（目录消失／manifest 不可解析／id 被替换／IO，
    /// 以及包级结构性非法＝该包不可装载）。同 key 不同路径保留先注册者。
    /// </summary>
    private List<PoolEntry> Reconcile()
    {
        var removed = new List<PoolEntry>();

        // ① 逐条重读 manifest（快照刷新 ＋ 有效性判定；IO 异常按失效处理，不让对账整体失败）。
        var survivors = new List<EntryView>();
        foreach (var entry in _entries)
        {
            var view = Read(entry);
            if (!view.Probe.Ok)
            {
                removed.Add(RemoveRef(view, view.Probe.ErrorCode ?? PackageProbeCodes.ManifestInvalid));
                continue;
            }

            survivors.Add(view);
        }

        // ② 同 key 不同路径（磁盘漂移造成）：保留先注册者，移除后来者（不交给 Inspect——会把重复副本误诊成版本冲突）。
        var seen = new HashSet<(string Id, string Version)>();
        var deduped = new List<EntryView>(survivors.Count);
        foreach (var view in survivors)
        {
            var key = (view.Probe.PackageId!, view.Probe.Version!);
            if (!seen.Add(key))
            {
                removed.Add(RemoveRef(view, PackageProbeCodes.DuplicateKey));
                continue;
            }

            deduped.Add(view);
        }

        // ③ 包级结构性非法（sidecar／GUID 重复等）：该包不可装载 ⇒ 一并移除（否则「对账后可装配」不成立）。
        while (true)
        {
            var trial = PreviewViews(deduped);
            var broken = trial.Packages
                .Where(row => !row.Ok && deduped.Any(view => PathEquals(view.Entry.Directory, row.Directory)))
                .ToArray();
            if (broken.Length == 0)
            {
                break;
            }

            foreach (var row in broken)
            {
                var view = deduped.First(item => PathEquals(item.Entry.Directory, row.Directory));
                removed.Add(new PoolEntry(row.PackageId ?? view.Probe.PackageId ?? "unknown", row.Version ?? view.Probe.Version, row.ErrorCode));
                deduped.Remove(view);
            }
        }

        // ④ 提交（顺序＝原注册序；快照刷新为本次读盘结果）。
        _entries.Clear();
        foreach (var view in deduped)
        {
            view.Entry.PackageId = view.Probe.PackageId;
            view.Entry.Version = view.Probe.Version;
            view.Entry.Guid = view.Probe.Guid;
            _entries.Add(view.Entry);
        }

        return removed;
    }

    // ---------------------------------------------------------------- 预演（读面 / 候选试算）

    /// <summary>读一条条目（重读 manifest；期望 id＝注册时登记的 id ⇒ 可判 id 漂移）。</summary>
    private static EntryView Read(PackPoolEntry entry)
        => new(entry, PackageDirectoryProbe.Probe(entry.Directory, entry.ExpectedPackageId));

    /// <summary>
    /// 预演：逐条判定有效性（失效行 <c>willRemove=true</c> 且不入装配计算），再按「有效条目 ＋ 合法沙盒包」算装配。
    /// <para>纯计算：不写集合、不写盘（<c>pool.list</c> 对 viewer 开放，必须零副作用）。</para>
    /// </summary>
    private AssemblyInspection PreviewViews(IReadOnlyList<EntryView> views)
    {
        // 同 key 不同路径的预演：保留先注册者，后来者标为失效（与真对账同一规则）。
        var duplicates = new HashSet<PackPoolEntry>();
        var seen = new HashSet<(string Id, string Version)>();
        foreach (var view in views)
        {
            if (!view.Probe.Ok)
            {
                continue;
            }

            if (!seen.Add((view.Probe.PackageId!, view.Probe.Version!)))
            {
                duplicates.Add(view.Entry);
            }
        }

        var active = views
            .Where(view => view.Probe.Ok && !duplicates.Contains(view.Entry))
            .Select(view => view.Entry.Directory)
            .ToArray();

        var trial = InspectWithSandbox(active);
        var rows = new List<PackageInspection>(views.Count);
        var warnings = new List<string>(trial.Warnings);

        // 集合为空不是"非法状态"（R2/空集合裁决），但必须显式说明「这一局的输入只剩沙盒固定包」——
        // 否则 ok=true 会让人误以为集合里还有东西（沙盒占位卡让装配照样成功）。
        if (active.Length == 0)
        {
            warnings.Add("registry-empty：已注册包集合为空——本局输入只有沙盒固定包（沙盒包不存在时开局失败）。");
        }

        foreach (var view in views)
        {
            if (!view.Probe.Ok)
            {
                rows.Add(new PackageInspection(
                    view.Entry.Directory,
                    view.Probe.PackageId ?? view.Entry.PackageId,
                    view.Probe.Version ?? view.Entry.Version,
                    view.Probe.Guid ?? view.Entry.Guid,
                    false,
                    view.Probe.ErrorCode,
                    view.Probe.Error,
                    WillRemove: true));
                continue;
            }

            if (duplicates.Contains(view.Entry))
            {
                rows.Add(new PackageInspection(
                    view.Entry.Directory,
                    view.Probe.PackageId,
                    view.Probe.Version,
                    view.Probe.Guid,
                    false,
                    PackageProbeCodes.DuplicateKey,
                    "同一 (id, version) 已被另一个（先注册的）目录占用——本条将在下一次写面或开局时移除。",
                    WillRemove: true));
                continue;
            }

            rows.Add(trial.Packages.FirstOrDefault(row => PathEquals(row.Directory, view.Entry.Directory))
                ?? new PackageInspection(
                    view.Entry.Directory,
                    view.Probe.PackageId,
                    view.Probe.Version,
                    view.Probe.Guid,
                    false,
                    PackageProbeCodes.ManifestInvalid,
                    "该包未参与本次装配（未产出逐包行）。",
                    WillRemove: true));
        }

        var invalid = rows.Count(row => row.WillRemove);
        if (invalid > 0)
        {
            warnings.Add($"{invalid} 条失效条目将在下一次写面或开局时移除（对账尚未提交）。");
        }

        return new AssemblyInspection(trial.Ok, rows, trial.Issues, warnings, trial.Plan);
    }

    /// <summary>
    /// 装配判定：输入＝有效条目目录 ＋ 沙盒固定包。
    /// <para>
    /// 沙盒包非法 ⇒ 剔除它**重算一次**并给 <c>sandbox-degraded</c> 告警（不作为拒绝集合变更的理由）；
    /// 沙盒包缺 sidecar/lock ⇒ 只读探测给 <c>sandbox-metadata-unsynced</c>（绝不为算得更准而写盘）。
    /// </para>
    /// </summary>
    private AssemblyInspection InspectWithSandbox(IReadOnlyList<string> entryDirectories)
    {
        var sandboxExists = Directory.Exists(_sandboxDirectory);
        var input = new List<string>(entryDirectories);
        if (sandboxExists)
        {
            input.Add(_sandboxDirectory);
        }

        var trial = MatchCardAssembly.Inspect(input);
        if (!sandboxExists)
        {
            return trial;
        }

        var sandboxRow = trial.Packages.FirstOrDefault(row => PathEquals(row.Directory, _sandboxDirectory));
        if (!trial.Ok)
        {
            // 「剔除沙盒包重算一次」：重算通过 ⇒ 失败由沙盒包引起（开局已少它）；沙盒逐包行直接判非法同样成立。
            var retry = MatchCardAssembly.Inspect(entryDirectories);
            if (retry.Ok || sandboxRow is { Ok: false })
            {
                var reason = sandboxRow is null || string.IsNullOrWhiteSpace(sandboxRow.Error)
                    ? "沙盒固定包不可装载"
                    : sandboxRow.Error;
                var model = retry.Ok ? retry : trial;
                return model with
                {
                    Warnings =
                    [
                        .. model.Warnings,
                        $"{PoolProjection.SandboxDegradedCode}：沙盒固定包不可装载（{reason}）"
                        + "——本次判定已剔除它重算；开局会按剔除降级。",
                    ],
                };
            }

            return trial; // 剔除后仍非法：集合自身的问题，不把沙盒包当替罪羊
        }

        var warnings = new List<string>(trial.Warnings);
        if (SandboxMetadataUnsynced())
        {
            warnings.Add(
                $"{PoolProjection.SandboxMetadataUnsyncedCode}：沙盒包缺 sidecar/lock（开局装配会自动补齐）"
                + "——本次结论可能比实际悲观。");
        }

        return trial with { Warnings = warnings };
    }

    /// <summary>只读探测沙盒包元数据是否待补（<b>不写盘</b>、不预测补齐是否一定成功）。</summary>
    private bool SandboxMetadataUnsynced()
    {
        try
        {
            if (!Directory.Exists(_sandboxDirectory))
            {
                return false;
            }

            return AssetMigrationTool.Plan(_sandboxDirectory, force: false).HasPendingWrites;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or FormatException or InvalidOperationException or ArgumentException or DirectoryNotFoundException)
        {
            return false; // 探测失败不产生告警（结论保守但不误导）
        }
    }

    // ---------------------------------------------------------------- 准入与投影辅助

    /// <summary>变更准入判据：无 fatal issue 且没有任何不可装载的包（空池是 ok=false＋fatal=false，允许写入）。</summary>
    private static AssetLoadIssue? RejectReason(AssemblyInspection trial)
    {
        var fatal = trial.Issues.FirstOrDefault(issue => issue.IsFatal);
        if (fatal is not null)
        {
            return fatal;
        }

        return trial.Packages.Any(row => !row.Ok) ? new AssetLoadIssue(
            AssetLoadIssueCode.DependencyMissing,
            AssetDomain.Formal,
            AppId: null,
            "候选集合中存在不可装载的包（见 packages.errorCode）。",
            []) : null;
    }

    private static PoolCallResult Rejected(AssetLoadIssue reason, AssemblyInspection trial, List<PoolEntry> reconciled)
    {
        var conflict = trial.Issues.Where(issue => issue.Conflict is not null).Select(issue => issue.Conflict!).ToArray();
        var code = conflict.Length > 0 ? McpCodes.LockConflict : McpCodes.MissingAssets;
        var message = conflict.Length > 0
            ? "变更被拒绝：合并后的 lock 存在冲突（同一资产被锁定为不同版本）；集合保持对账后的状态。"
            : $"变更被拒绝：{reason.Message}；集合保持对账后的状态。";

        var details = new JsonObject
        {
            ["conflicts"] = new JsonArray(conflict.Select(item => (JsonNode)new JsonObject
            {
                ["kind"] = AssetKindTokens.Token(item.Kind),
                ["appId"] = item.AppId,
                ["firstPackageId"] = item.FirstPackageId,
                ["firstVersion"] = item.FirstVersion.ToString(),
                ["secondPackageId"] = item.SecondPackageId,
                ["secondVersion"] = item.SecondVersion.ToString(),
            }).ToArray()),
            ["fatal"] = new JsonArray(trial.Issues
                .Where(issue => issue.IsFatal)
                .Select(issue => (JsonNode)JsonValue.Create(issue.Code.ToString())!)
                .ToArray()),
        };

        return new PoolCallResult(
            false,
            code,
            message,
            trial,
            Added: Array.Empty<PoolEntry>(),
            Removed: Array.Empty<PoolEntry>(),
            RemovedByReconcile: reconciled.ToArray(),
            Details: details);
    }

    /// <summary>注册入参失败的失败码映射（不新增码）。</summary>
    private static string RegistrationCode(PackageProbeResult probe) => probe.ErrorCode switch
    {
        PackageProbeCodes.IoError when probe.Cause is UnauthorizedAccessException => McpCodes.PathDenied,
        _ => McpCodes.InvalidArguments,
    };

    private static JsonObject? RegistrationDetails(PackageProbeResult probe)
        => probe.ErrorCode == PackageProbeCodes.IoError && probe.Cause is not UnauthorizedAccessException
            ? new JsonObject { ["cause"] = "io" }
            : null;

    private static PoolCallResult Failure(
        string code,
        string message,
        AssemblyInspection model,
        JsonObject? details = null,
        IReadOnlyList<PoolEntry>? reconciled = null)
        => new(
            false,
            code,
            message,
            model,
            Added: Array.Empty<PoolEntry>(),
            Removed: Array.Empty<PoolEntry>(),
            RemovedByReconcile: reconciled?.ToArray(),
            Details: details);

    /// <summary>drain／入队失败时的模型（取一次预演，保证响应结构不因失败而缺失）。</summary>
    private AssemblyInspection PreviewSafely()
    {
        lock (_sync)
        {
            return PreviewViews(_entries.Select(Read).ToList());
        }
    }

    private static PoolEntry RemoveRef(EntryView view, string cause)
        => new(
            view.Probe.PackageId ?? view.Entry.PackageId ?? "unknown",
            view.Probe.Version ?? view.Entry.Version,
            // S2：读目录 IO 失败在 removed[] 里给 `cause=io`（包级错误码仍是 io-error）
            cause == PackageProbeCodes.IoError ? "io" : cause);

    // ---------------------------------------------------------------- 事件

    /// <summary>
    /// <c>packset.reconciled</c>：仅在真对账**实际移除**条目时发（写面与开局路径都发）。
    /// <para>载荷不含绝对目录（通道事件经管理器原样转发，可能落到非管理连接）。</para>
    /// </summary>
    private void EmitReconciled(IReadOnlyList<PoolEntry> removed)
    {
        if (removed.Count == 0 || _publish is null)
        {
            return;
        }

        _publish("packset.reconciled", new JsonObject
        {
            ["count"] = removed.Count,
            ["removed"] = new JsonArray(removed.Select(entry => (JsonNode)new JsonObject
            {
                ["packageId"] = entry.PackageId,
                ["version"] = entry.Version,
                ["cause"] = entry.Cause,
            }).ToArray()),
        });
    }

    /// <summary><c>packset.changed</c>：写操作**成功后**发（失败不发；载荷无绝对目录）。</summary>
    private void EmitChanged(PoolCallResult result)
    {
        if (_publish is null)
        {
            return;
        }

        _publish("packset.changed", new JsonObject
        {
            ["added"] = new JsonArray((result.Added ?? Array.Empty<PoolEntry>())
                .Select(entry => (JsonNode)new JsonObject { ["id"] = entry.PackageId, ["version"] = entry.Version })
                .ToArray()),
            ["removed"] = new JsonArray((result.Removed ?? Array.Empty<PoolEntry>())
                .Select(entry => (JsonNode)new JsonObject { ["id"] = entry.PackageId, ["version"] = entry.Version })
                .ToArray()),
            ["ok"] = result.Model.Ok,
            ["itemCount"] = result.Model.Plan.ItemCount,
            ["cardCount"] = result.Model.Plan.CardCount,
        });
    }

    // ---------------------------------------------------------------- 路径口径

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>归一化：绝对路径 ＋ 去末尾分隔符（<c>..</c>／大小写／UNC 都在这步统一）。</summary>
    private static string Normalize(string directory)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private static bool PathEquals(string left, string right)
        => string.Equals(Normalize(left), Normalize(right), PathComparison);

    private bool IsSandboxDirectory(string directory) => PathEquals(directory, _sandboxDirectory);

    // ---------------------------------------------------------------- 内部类型

    /// <summary>集合条目（**以目录为实体**；<c>(id, version)</c> 是每次读盘刷新的快照）。</summary>
    private sealed class PackPoolEntry
    {
        public PackPoolEntry(
            string directory,
            string? expectedPackageId,
            string? packageId,
            string? version,
            string? guid)
        {
            Directory = directory;
            ExpectedPackageId = expectedPackageId;
            PackageId = packageId;
            Version = version;
            Guid = guid;
        }

        /// <summary>目录（归一化绝对路径；条目身份）。</summary>
        public string Directory { get; }

        /// <summary>注册时登记的包 id（对账据此判「目录被替换成另一个包」；初值＝spawn 时读到的 id）。</summary>
        public string? ExpectedPackageId { get; }

        /// <summary>最近一次提交的包 id 快照。</summary>
        public string? PackageId { get; set; }

        /// <summary>最近一次提交的版本快照（内容指纹）。</summary>
        public string? Version { get; set; }

        /// <summary>最近一次提交的包 GUID 快照。</summary>
        public string? Guid { get; set; }
    }

    /// <summary>条目 ＋ 本次读盘结果。</summary>
    private readonly record struct EntryView(PackPoolEntry Entry, PackageProbeResult Probe);
}
