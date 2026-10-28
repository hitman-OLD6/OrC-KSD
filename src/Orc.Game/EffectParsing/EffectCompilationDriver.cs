using Orc.Cards;
using Orc.Game.Effects;
using Orc.Game.EffectParsing.Compilation;
using Orc.Game.EffectParsing.Dsl;
using Orc.Game.EffectParsing.Parsing;
using Orc.Game.EffectParsing.Templates;

namespace Orc.Game.EffectParsing;

/// <summary>
/// 效果离线编译驱动（端到端创作卡牌工具链·批 5·N2a；工具链/生产面——非运行期、不启动对局）：
/// 卡面文本/DSL → 解析（<see cref="EffectParser"/>／<see cref="DslJson"/>）→ 编译（<see cref="EffectCompiler"/>）
/// → 快照；可选落盘 <c>*.prefab.json</c>（效果级产出——批量入口内部组合 <see cref="PrefabWriter"/>，不新增第二套落盘出口）。
/// <para>入口形态（对外契约——同构：单条＝批量的 0/1 源输入特例）：</para>
/// <list type="bullet">
///   <item><b>单条（纯返回）</b>：<see cref="CompileCardFaceText"/>／<see cref="CompileDsl"/>——
///     返回产物区（内存快照＋JSON 文本）＋报告区；不落盘（需要落盘的调用方可组合 <c>PrefabWriter</c>）。</item>
///   <item><b>目录级批量</b>：<see cref="CompileDirectory"/>——落盘（默认）或 dry-run（预览/校验）；
///     单条失败隔离、报告式返回；批内重复＝首见占位＋重复失败（沿批 2 <c>PrefabWriter</c> 口径）。</item>
/// </list>
/// <para>输入契约（批量）：仅扫顶层、仅认约定扩展名（<c>*.txt</c>＝卡面文本〔一文件＝一卡可多效果〕；
/// <c>*.dsl.json</c>＝DSL〔一文件＝一效果实例、身份基名＝文件基名去 <c>.dsl.json</c>〕）；匹配大小写不敏感、
/// 排序 Ordinal、非约定文件跳过且不计入任何计数；输出目录可与输入目录相同/嵌套（产出 <c>*.prefab.json</c>
/// 不会被扫描当输入）。</para>
/// <para>失败分层（类别字面稳定）：<c>InvalidData</c>／<c>SourceMissing</c>／<c>CompileFailed</c>／
/// <c>InvalidIdentity</c>／<c>DuplicateIdentity</c>／<c>IoFailed</c>；留痕三类（未解析／needsCsx|csx／占位条件）
/// 不静默丢弃、入报告；词条行声明以独立信息区承载。</para>
/// <para>资产来源三形态（复用批 4 先例）：默认目录约定（程序集输出目录 <c>EffectParsing/Templates</c>——
/// 开箱即用）／显式目录覆盖（<see cref="UseEffectAssets(string)"/>）／内存注入
/// （<see cref="UseEffectAssets(IReadOnlyList{EffectTemplate}, OpTemplateCatalog)"/>——测试自含通道）。</para>
/// <para>确定性底线：同一输入＋同一资产内容 ⇒ 同一输出（含身份、文本、报告序）；驱动不引入编译缓存
/// （每次全量重算——正确性等价、无全局可变状态；资产解析沿用批 4 内容指纹语义——资产变更必反映）。
/// 驱动实例非并发安全（本批单线程顺序——批量并发属后续扩展）。</para>
/// <para>范围（本批锁死）：无 CLI 外壳、不接线对局、不做批量并发、报告不落盘（内存返回）、不做缓存持久化。</para>
/// </summary>
public sealed class EffectCompilationDriver
{
    private readonly Lazy<EffectParser> _parser = new(() => EffectParser.CreateDefault(out _));

    private EffectAssetSource? _assetSource;

    /// <summary>
    /// **显式覆盖编译资产目录**（默认＝程序集输出目录约定；测试自含/工具链自定义资产集用）：
    /// 目录约定——根含 <c>*.tpl.json</c>（模板效果）、<c>ops</c> 子目录含 <c>*.csx.tpl</c>（op 语句模板）。
    /// 可重设（后设覆盖先设）；资产按内容指纹解析——目录内容变化必反映。
    /// </summary>
    /// <exception cref="ArgumentException">directory 为 null/空白。</exception>
    public void UseEffectAssets(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _assetSource = EffectAssetSource.ForDirectory(directory);
    }

    /// <summary>
    /// **显式覆盖编译资产（内存注入）**（测试自含/工具链用）：直接注入已加载的模板集合与 op 目录对象
    /// （不经目录扫描）。可与 <see cref="UseEffectAssets(string)"/> 交替重设。
    /// </summary>
    /// <exception cref="ArgumentNullException">templates 或 ops 为 null。</exception>
    public void UseEffectAssets(IReadOnlyList<EffectTemplate> templates, OpTemplateCatalog ops)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(ops);
        _assetSource = EffectAssetSource.ForMemory(templates, ops);
    }

    /// <summary>
    /// **单条入口·卡面文本链**（纯返回——不落盘）：卡面文本 → 解析 → 逐效果编译 → 快照＋JSON 文本。
    /// 身份规则＝基名＋声明序序号（N=1＝<paramref name="effectId"/>；N≥2＝<c>{基名}.{n}</c>，n 自 1 起——
    /// 确定性派生：第 n 个身份只取决于基名与序号）。
    /// </summary>
    /// <param name="cardFaceText">卡面文本（一卡可多效果；空文本＝空成功）。</param>
    /// <param name="effectId">身份基名（必给——参数层 fail-fast）。</param>
    /// <param name="contextLabel">调用上下文标签（可选——承载报告中的文件名缺省位；单条无文件上下文）。</param>
    /// <param name="version">快照版本（可选；缺省 1——对齐 <see cref="EffectCompiler"/> 既有能力）。</param>
    /// <returns>产物区（快照集合＋JSON 文本）＋报告区。</returns>
    /// <exception cref="ArgumentNullException">cardFaceText 为 null。</exception>
    /// <exception cref="ArgumentException">effectId 为 null/空白。</exception>
    /// <exception cref="ArgumentOutOfRangeException">version ≤ 0。</exception>
    public EffectCompilationResult CompileCardFaceText(
        string cardFaceText, string effectId, string? contextLabel = null, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(cardFaceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectId);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "版本须为正整数。");
        }

        var source = new InputSource
        {
            FileName = null,
            ContextLabel = contextLabel,
            InlineText = cardFaceText,
            BaseName = effectId,
            Kind = EffectCompilationInputKind.CardFaceText,
            Version = version,
        };

        return RunPipeline(new[] { source }, EffectCompilationMode.Single, inputDirectory: null, outputDirectory: null,
            persist: false);
    }

    /// <summary>
    /// **单条入口·DSL 链**（纯返回——不落盘）：DSL JSON → 反序列化 → 编译 → 快照＋JSON 文本。
    /// 单实例＝单效果＝单产出（身份＝<paramref name="effectId"/> 直接用）。
    /// </summary>
    /// <param name="dslJson">效果 DSL JSON 文本（<see cref="DslJson"/> 形态）。</param>
    /// <param name="effectId">效果身份（必给——参数层 fail-fast）。</param>
    /// <param name="version">快照版本（可选；缺省 1）。</param>
    /// <returns>产物区（快照集合＋JSON 文本）＋报告区；DSL 无效＝<c>InvalidData</c> 结果（失败不抛出）。</returns>
    /// <exception cref="ArgumentNullException">dslJson 为 null。</exception>
    /// <exception cref="ArgumentException">effectId 为 null/空白。</exception>
    /// <exception cref="ArgumentOutOfRangeException">version ≤ 0。</exception>
    public EffectCompilationResult CompileDsl(string dslJson, string effectId, int version = 1)
    {
        ArgumentNullException.ThrowIfNull(dslJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectId);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "版本须为正整数。");
        }

        var source = new InputSource
        {
            FileName = null,
            ContextLabel = null,
            InlineText = dslJson,
            BaseName = effectId,
            Kind = EffectCompilationInputKind.Dsl,
            Version = version,
        };

        return RunPipeline(new[] { source }, EffectCompilationMode.Single, inputDirectory: null, outputDirectory: null,
            persist: false);
    }

    /// <summary>
    /// **目录级批量入口**（落盘默认／dry-run 可选）：扫描输入目录（顶层＋约定扩展名，Ordinal 文件名序统一排队）
    /// → 逐文件解析 → 逐效果编译 → 落盘（或 dry-run 零写）→ 报告式返回（单条失败隔离、部分失败继续）。
    /// <para>落盘＝内部组合 <see cref="PrefabWriter.SaveDirectory"/>（命名/非法 id 拒绝/批内重复首见写＋重复失败/
    /// 跨运行覆盖/只写不删/目录自动创建/目录准备失败＝整体性抛出——逐字沿用批 2 口径；文本契约单一真源）。</para>
    /// <para>dry-run＝输出侧零写 IO（不写盘、不创建目录、不探测输出目录；输入侧读取照常）；判定与落盘模式一致
    /// （差异仅 <c>IoFailed</c> 不发生）；成功效果必携 JSON 文本（预览即产出）。</para>
    /// </summary>
    /// <param name="inputDirectory">输入目录（只扫顶层；不存在＝整体性抛出——防「静默空产出被误读为全部成功」）。</param>
    /// <param name="outputDirectory">输出目录（落盘模式必填；dry-run 可选——提供时为预期路径全路径、不探测）。</param>
    /// <param name="dryRun">dry-run 模式（默认 false＝落盘）。</param>
    /// <returns>产物区（dry-run＝必携；落盘＝空——内容以磁盘为准）＋报告区。</returns>
    /// <exception cref="ArgumentException">inputDirectory 为 null/空白；落盘模式下 outputDirectory 为 null/空白。</exception>
    /// <exception cref="DirectoryNotFoundException">输入目录不存在（整体性——两模式一致）。</exception>
    /// <exception cref="IOException">输出目录准备失败（整体性——无法创建/准备目标目录；沿批 2）。</exception>
    /// <exception cref="UnauthorizedAccessException">输出目录准备失败（权限；沿批 2）。</exception>
    public EffectCompilationResult CompileDirectory(string inputDirectory, string? outputDirectory = null, bool dryRun = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        if (!Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException(
                $"输入目录不存在：'{inputDirectory}'（整体性失败——防『静默空产出被误读为全部成功』）。");
        }

        var normalizedOutput = string.IsNullOrWhiteSpace(outputDirectory) ? null : outputDirectory;
        if (!dryRun && normalizedOutput is null)
        {
            throw new ArgumentException("批量落盘模式必须提供输出目录（dry-run 时可选）。", nameof(outputDirectory));
        }

        var sources = new List<InputSource>();
        foreach (var file in EnumerateInputFiles(inputDirectory))
        {
            var fileName = Path.GetFileName(file);
            sources.Add(new InputSource
            {
                FilePath = file,
                FileName = fileName,
                BaseName = DeriveBaseName(fileName),
                Kind = IsDslFileName(fileName) ? EffectCompilationInputKind.Dsl : EffectCompilationInputKind.CardFaceText,
                Version = 1,
            });
        }

        return RunPipeline(
            sources,
            dryRun ? EffectCompilationMode.DryRun : EffectCompilationMode.Disk,
            inputDirectory,
            normalizedOutput,
            persist: !dryRun);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 统一管线：解析段 → 编译段 → 打包段（内部架构——对外只有整体链两入口）
    // ─────────────────────────────────────────────────────────────────────────

    private EffectCompilationResult RunPipeline(
        IReadOnlyList<InputSource> sources,
        EffectCompilationMode mode,
        string? inputDirectory,
        string? outputDirectory,
        bool persist)
    {
        var state = new BatchState();

        // 资产解析（一次/批——批内一致性；失败＝结构化「来源未命中」、不外抛；沿批 4 先例）。
        try
        {
            state.Bundle = EffectAssetResolver.Resolve(_assetSource);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.AssetError = ex.Message;
        }

        // 解析段＋编译段：逐输入（源文件序）→ 逐效果（声明序）。
        foreach (var source in sources)
        {
            ProcessInput(source, state);
        }

        // 打包段：落盘（可选）＋预期路径推导（dry-run）。
        if (persist && outputDirectory is not null)
        {
            Persist(outputDirectory, state);
        }

        if (mode == EffectCompilationMode.DryRun && outputDirectory is not null)
        {
            FillExpectedTargets(outputDirectory, state);
        }

        return Assemble(state, mode, inputDirectory, outputDirectory);
    }

    // ── 解析段 ──

    private void ProcessInput(InputSource source, BatchState state)
    {
        var record = new InputRecord(source);
        state.Inputs.Add(record);

        string text;
        if (source.InlineText is not null)
        {
            text = source.InlineText;
        }
        else
        {
            try
            {
                text = File.ReadAllText(source.FilePath!); // UTF-8（容忍 BOM）；CRLF/LF 由解析器归一。
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                record.FileFailureCategory = EffectCompilationFailureCategory.IoFailed;
                record.FileFailureReason = $"读取失败：{ex.Message}";
                return;
            }
        }

        if (source.Kind == EffectCompilationInputKind.Dsl)
        {
            // 次链：DSL＝契约数据（反序列化失败＝InvalidData 结构化失败、不产出——与卡面文本侧宽容解析有意不对称）。
            if (!DslJson.TryDeserialize(text, out var dsl, out var parseError))
            {
                record.FileFailureCategory = EffectCompilationFailureCategory.InvalidData;
                record.FileFailureReason = $"效果 DSL 无效（数据无效）：{parseError}";
                return;
            }

            ProcessEffect(dsl!, source, ordinal: 1, effectCount: 1, record, state);
            return;
        }

        // 主链：卡面文本＝宽容解析（「翻不了」＝Unresolved 留痕、产出继续、不失败）。
        var parse = _parser.Value.Parse(text);

        foreach (var unresolved in parse.Unresolved)
        {
            record.Unresolved.Add(new EffectCompilationUnresolvedTrace(
                EffectCompilationTraceCategory.Unresolved,
                source.FileName,
                unresolved.RawText,
                unresolved.Reason,
                unresolved.Start,
                unresolved.Length));
        }

        foreach (var declaration in parse.Declarations)
        {
            record.Declarations.Add(new EffectCompilationDeclaration(
                source.FileName,
                declaration.Dimension,
                declaration.Id,
                declaration.Value,
                declaration.Registration,
                LineOf(text, declaration.Span.Start),
                declaration.Span.Start,
                declaration.Span.Length));
        }

        for (var i = 0; i < parse.Effects.Count; i++)
        {
            ProcessEffect(parse.Effects[i], source, ordinal: i + 1, effectCount: parse.Effects.Count, record, state);
        }
    }

    // ── 编译段 ──

    private static void ProcessEffect(
        DslEffectInstance dsl,
        InputSource source,
        int ordinal,
        int effectCount,
        InputRecord record,
        BatchState state)
    {
        // 身份派生（确定性）：N=1 ⇒ 基名；N≥2 ⇒ {基名}.{n}（声明序，n 自 1 起）。
        var effectId = effectCount == 1 ? source.BaseName : $"{source.BaseName}.{ordinal}";

        var outcome = new EffectOutcome(source.FileName, ordinal, effectId);

        // 留痕提取（对一切效果——与其后续成败无关；递归覆盖内嵌；模板内固定 csx 不在此列）。
        CollectEffectTraces(dsl, outcome);

        // 身份处理：合法性判定（复用批 2 PrefabWriter 规则——单一真源）。
        // 判定对象＝基名＋派生 id 双重覆盖（「派生序号后缀不改变判定」：基名非法〔含整名空白〕⇒ 全失败，
        // 且空白基名经派生后〔如 '   .1'〕不再空白——故须独立判定基名本身）。
        if (PrefabWriter.IsIllegalPrefabId(source.BaseName) || PrefabWriter.IsIllegalPrefabId(effectId))
        {
            outcome.FailureCategory = EffectCompilationFailureCategory.InvalidIdentity;
            outcome.FailureReason =
                $"效果快照 id 非法（含文件系统非法字符/路径分隔符/穿越序列——拒绝写出）：'{effectId}'。";
        }
        else if (state.Seen.TryGetValue(effectId, out var first))
        {
            // 占位口径：通过身份处理（基名派生＋合法性判定）即占位——与该效果自身后续成败无关；
            // 重复判定以「同批内已处理的最终 id」为准（dry-run 与落盘同判——模式只控制动作）。
            var firstSource = first.SourceFileName is not null
                ? $"源文件 '{first.SourceFileName}'"
                : first.ContextLabel is not null
                    ? $"调用上下文 '{first.ContextLabel}'"
                    : "调用上下文";
            outcome.FailureCategory = EffectCompilationFailureCategory.DuplicateIdentity;
            outcome.FailureReason =
                $"效果快照 id 重复（同批内已占位）：'{effectId}'——首见者：{firstSource} 的派生身份 '{first.EffectId}'（声明序 {first.Ordinal}）。";
        }
        else
        {
            state.Seen[effectId] = new SeenIdentity(effectId, source.FileName, source.ContextLabel, ordinal);
            CompileOutcome(outcome, dsl, effectId, source.Version, state);
        }

        record.Effects.Add(outcome);
        state.Outcomes.Add(outcome);
    }

    private static void CompileOutcome(
        EffectOutcome outcome, DslEffectInstance dsl, string effectId, int version, BatchState state)
    {
        if (state.AssetError is not null)
        {
            outcome.FailureCategory = EffectCompilationFailureCategory.SourceMissing;
            outcome.FailureReason = $"资产解析失败（来源未命中）：{state.AssetError}";
            return;
        }

        var bundle = state.Bundle!;

        // 来源预检（模板/op 资产缺失＝SourceMissing——批 4 先例语义；消费方处置＝补资产）。
        if (!bundle.Templates.Any(t => string.Equals(t.Id, dsl.Template, StringComparison.Ordinal)))
        {
            outcome.FailureCategory = EffectCompilationFailureCategory.SourceMissing;
            outcome.FailureReason = $"模板效果 '{dsl.Template}' 不在资产目录（来源未命中——模板资产缺失）。";
            return;
        }

        if (FindMissingOp(dsl.Fills, bundle.Ops) is { } missingOp)
        {
            outcome.FailureCategory = EffectCompilationFailureCategory.SourceMissing;
            outcome.FailureReason = $"op 语句模板 '{missingOp}' 缺失（来源未命中——op 资产缺失）。";
            return;
        }

        try
        {
            state.Compiler ??= new EffectCompiler(bundle.Templates, bundle.Ops);
            outcome.Snapshot = state.Compiler.Compile(dsl, effectId, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 既有层错误消息原样保留（不吞不降级——允许前缀、不删改）。
            outcome.FailureCategory = EffectCompilationFailureCategory.CompileFailed;
            outcome.FailureReason = $"DSL 编译失败（编译失败）：{ex.Message}";
        }
    }

    /// <summary>
    /// 来源预检：fills 里第一个在 op 资产目录中缺失的 op 名（全在＝null）。
    /// 原属动态效果 partial（<c>EffectRuntime.FindMissingOp</c>），随该 partial 删除迁到本驱动器（唯一消费方）。
    /// </summary>
    private static string? FindMissingOp(IReadOnlyDictionary<string, DslSlotFill> fills, OpTemplateCatalog ops)
    {
        foreach (var fill in fills.Values)
        {
            foreach (var op in fill.Ops)
            {
                if (!ops.Has(op.Op))
                {
                    return op.Op;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 留痕提取（效果级——递归覆盖内嵌）：needsCsx/csx（fills 里的 op）＋占位条件（<c>raw</c> 条件，含
    /// <c>all</c> 合取内嵌）；两类各自独立记录（不互斥）。模板内固定 csx 不在检测面（非 DSL 显式标记）。
    /// </summary>
    private static void CollectEffectTraces(DslEffectInstance dsl, EffectOutcome outcome, string locationPrefix = "")
    {
        foreach (var pair in dsl.Fills)
        {
            var slotPrefix = locationPrefix.Length == 0 ? pair.Key : locationPrefix + "." + pair.Key;
            var ops = pair.Value.Ops;
            for (var i = 0; i < ops.Count; i++)
            {
                var op = ops[i];
                var location = $"{slotPrefix}[{i}]";

                if (string.Equals(op.Op, DslOpRegistry.NeedsCsxOpName, StringComparison.Ordinal))
                {
                    outcome.NeedsCsxTraces.Add(new NeedsCsxTraceData
                    {
                        Category = EffectCompilationTraceCategory.NeedsCsx,
                        Location = location,
                        Script = op.Script,
                    });
                }
                else if (string.Equals(op.Op, DslOpRegistry.CsxOpName, StringComparison.Ordinal))
                {
                    outcome.NeedsCsxTraces.Add(new NeedsCsxTraceData
                    {
                        Category = EffectCompilationTraceCategory.Csx,
                        Location = location,
                        Script = op.Script,
                    });
                }

                CollectConditionTraces(op.Condition, location + ".condition", outcome.PlaceholderTraces);

                if (op.Nested is { Count: > 0 })
                {
                    for (var j = 0; j < op.Nested.Count; j++)
                    {
                        CollectEffectTraces(op.Nested[j], outcome, $"{location}.nested[{j}]");
                    }
                }
            }
        }
    }

    /// <summary>占位条件提取（<c>raw</c> 条件——含 <c>all</c> 合取递归；位置＝best-effort 条件路径）。</summary>
    private static void CollectConditionTraces(
        DslCondition? condition, string location, List<PlaceholderTraceData> placeholders)
    {
        if (condition is null)
        {
            return;
        }

        if (string.Equals(condition.Kind, DslCondition.RawKind, StringComparison.Ordinal))
        {
            placeholders.Add(new PlaceholderTraceData
            {
                Category = EffectCompilationTraceCategory.PlaceholderCondition,
                Location = location,
                RawText = condition.Raw,
            });
        }

        if (condition.All is { Count: > 0 })
        {
            for (var i = 0; i < condition.All.Count; i++)
            {
                CollectConditionTraces(condition.All[i], $"{location}.all[{i}]", placeholders);
            }
        }
    }

    // ── 打包段 ──

    /// <summary>
    /// 落盘（批量落盘模式）：编译成功快照按处理序交 <see cref="PrefabWriter.SaveDirectory"/>（单一落盘出口）；
    /// 写失败（IoFailed）＝条目化（预期路径＝PrefabWriter 目标路径）；目录准备失败＝整体性抛出（沿批 2）。
    /// </summary>
    private static void Persist(string outputDirectory, BatchState state)
    {
        var pending = new List<EffectOutcome>();
        foreach (var outcome in state.Outcomes)
        {
            if (outcome.FinalSuccess)
            {
                pending.Add(outcome);
            }
        }

        if (pending.Count == 0)
        {
            return; // 零快照＝零落盘动作（空成功语义——不做目录准备）。
        }

        var result = PrefabWriter.SaveDirectory(outputDirectory, pending.Select(o => o.Snapshot!));

        var byId = new Dictionary<string, EffectOutcome>(StringComparer.Ordinal);
        foreach (var outcome in pending)
        {
            byId[outcome.EffectId] = outcome;
        }

        foreach (var success in result.Succeeded)
        {
            if (byId.TryGetValue(success.PrefabId, out var outcome))
            {
                outcome.OutputFile = success.File;
            }
        }

        foreach (var failure in result.Failures)
        {
            if (byId.TryGetValue(failure.PrefabId, out var outcome))
            {
                outcome.FailureCategory = EffectCompilationFailureCategory.IoFailed;
                outcome.FailureReason = failure.Error; // 既有层消息原文（不吞不降级）。
                outcome.ExpectedTargetFile = failure.File;

                // 留痕条目的落点承载（预期字段——落盘失败＝必填；同失败条目路径）。
                foreach (var trace in outcome.NeedsCsxTraces)
                {
                    trace.ExpectedTargetFile = failure.File;
                }

                foreach (var trace in outcome.PlaceholderTraces)
                {
                    trace.ExpectedTargetFile = failure.File;
                }
            }
        }

        // 留痕条目的产出文件关联（事实字段——确有产出时填写）。
        foreach (var outcome in pending)
        {
            if (outcome.OutputFile is null)
            {
                continue;
            }

            foreach (var trace in outcome.NeedsCsxTraces)
            {
                trace.OutputFile = outcome.OutputFile;
            }

            foreach (var trace in outcome.PlaceholderTraces)
            {
                trace.OutputFile = outcome.OutputFile;
            }
        }
    }

    /// <summary>
    /// dry-run 预期路径推导（纯字符串——不探测文件系统；dry-run 且提供输出目录 ⇒ 该效果编译成功 ⇒
    /// 成功条目**与其留痕条目**均必填预期目标文件路径〔同第 6 轮规则、无例外〕）。
    /// </summary>
    private static void FillExpectedTargets(string outputDirectory, BatchState state)
    {
        foreach (var outcome in state.Outcomes)
        {
            if (!outcome.FinalSuccess)
            {
                continue;
            }

            var expected = Path.Combine(outputDirectory, outcome.EffectId + PrefabWriter.FileSuffix);
            outcome.ExpectedTargetFile = expected;

            foreach (var trace in outcome.NeedsCsxTraces)
            {
                trace.ExpectedTargetFile = expected;
            }

            foreach (var trace in outcome.PlaceholderTraces)
            {
                trace.ExpectedTargetFile = expected;
            }
        }
    }

    // ── 组装（五区＋汇总） ──

    private EffectCompilationResult Assemble(
        BatchState state,
        EffectCompilationMode mode,
        string? inputDirectory,
        string? outputDirectory)
    {
        var inputs = new List<EffectCompilationInputEntry>();
        var allCounts = new List<EffectCompilationCounts>();
        var unresolved = new List<EffectCompilationUnresolvedTrace>();
        var declarations = new List<EffectCompilationDeclaration>();

        foreach (var input in state.Inputs)
        {
            var counts = ComputeCounts(input);
            allCounts.Add(counts);

            inputs.Add(new EffectCompilationInputEntry(
                input.SourceFileName,
                input.ContextLabel,
                input.Kind,
                input.Effects.Where(e => e.OutputFile is not null).Select(e => e.OutputFile!).ToList(),
                counts,
                input.FileFailureCategory,
                input.FileFailureReason));

            unresolved.AddRange(input.Unresolved);
            declarations.AddRange(input.Declarations);
        }

        var successes = new List<EffectCompilationSuccess>();
        var failures = new List<EffectCompilationFailure>();
        var needsCsx = new List<EffectCompilationNeedsCsxTrace>();
        var placeholders = new List<EffectCompilationPlaceholderConditionTrace>();
        var artifacts = new List<EffectCompilationArtifact>();

        var withArtifacts = mode is EffectCompilationMode.Single or EffectCompilationMode.DryRun;

        foreach (var outcome in state.Outcomes)
        {
            if (outcome.FinalSuccess)
            {
                successes.Add(new EffectCompilationSuccess(
                    outcome.SourceFileName,
                    outcome.EffectId,
                    outcome.DeclarationOrdinal,
                    outcome.SemanticallyComplete,
                    outcome.OutputFile,
                    outcome.ExpectedTargetFile));

                if (withArtifacts)
                {
                    artifacts.Add(new EffectCompilationArtifact(
                        outcome.EffectId,
                        outcome.Snapshot!,
                        PrefabJson.SerializeForDisk(outcome.Snapshot!))); // 与落盘文本单一真源（逐字符一致）。
                }
            }
            else
            {
                failures.Add(new EffectCompilationFailure(
                    outcome.SourceFileName,
                    outcome.EffectId,
                    outcome.DeclarationOrdinal,
                    outcome.FailureCategory!.Value,
                    outcome.FailureReason!,
                    outcome.ExpectedTargetFile));
            }

            foreach (var trace in outcome.NeedsCsxTraces)
            {
                needsCsx.Add(new EffectCompilationNeedsCsxTrace(
                    trace.Category,
                    outcome.SourceFileName,
                    outcome.EffectId,
                    outcome.DeclarationOrdinal,
                    trace.Location,
                    trace.Script,
                    trace.OutputFile,
                    trace.ExpectedTargetFile));
            }

            foreach (var trace in outcome.PlaceholderTraces)
            {
                placeholders.Add(new EffectCompilationPlaceholderConditionTrace(
                    trace.Category,
                    outcome.SourceFileName,
                    outcome.EffectId,
                    outcome.DeclarationOrdinal,
                    trace.Location,
                    trace.RawText,
                    trace.OutputFile,
                    trace.ExpectedTargetFile));
            }
        }

        var total = SumCounts(allCounts);
        var summary = new EffectCompilationSummary(
            mode,
            state.Inputs.Count,
            inputDirectory,
            outputDirectory,
            AssetSourceLabel,
            total);

        var report = new EffectCompilationReport(
            inputs, successes, failures, unresolved, needsCsx, placeholders, declarations, summary);

        return new EffectCompilationResult(artifacts, report);
    }

    private static EffectCompilationCounts ComputeCounts(InputRecord input)
    {
        var effects = input.Effects;
        return new EffectCompilationCounts(
            ParsedEffectCount: effects.Count,
            SuccessCount: effects.Count(e => e.FinalSuccess),
            SemanticallyCompleteCount: effects.Count(e => e.FinalSuccess && e.SemanticallyComplete),
            FailureInvalidDataCount:
                (input.FileFailureCategory == EffectCompilationFailureCategory.InvalidData ? 1 : 0)
                + effects.Count(e => e.FailureCategory == EffectCompilationFailureCategory.InvalidData),
            FailureSourceMissingCount:
                effects.Count(e => e.FailureCategory == EffectCompilationFailureCategory.SourceMissing),
            FailureCompileFailedCount:
                effects.Count(e => e.FailureCategory == EffectCompilationFailureCategory.CompileFailed),
            FailureInvalidIdentityCount:
                effects.Count(e => e.FailureCategory == EffectCompilationFailureCategory.InvalidIdentity),
            FailureDuplicateIdentityCount:
                effects.Count(e => e.FailureCategory == EffectCompilationFailureCategory.DuplicateIdentity),
            FailureIoFailedCount:
                (input.FileFailureCategory == EffectCompilationFailureCategory.IoFailed ? 1 : 0)
                + effects.Count(e => e.FailureCategory == EffectCompilationFailureCategory.IoFailed),
            TraceUnresolvedCount: input.Unresolved.Count,
            TraceNeedsCsxCount: effects.Sum(e => e.NeedsCsxTraces.Count),
            TracePlaceholderConditionCount: effects.Sum(e => e.PlaceholderTraces.Count),
            DeclarationCount: input.Declarations.Count);
    }

    private static EffectCompilationCounts SumCounts(IReadOnlyList<EffectCompilationCounts> counts)
    {
        var parsed = 0;
        var success = 0;
        var complete = 0;
        var invalidData = 0;
        var sourceMissing = 0;
        var compileFailed = 0;
        var invalidIdentity = 0;
        var duplicateIdentity = 0;
        var ioFailed = 0;
        var unresolved = 0;
        var needsCsx = 0;
        var placeholder = 0;
        var declarations = 0;

        foreach (var c in counts)
        {
            parsed += c.ParsedEffectCount;
            success += c.SuccessCount;
            complete += c.SemanticallyCompleteCount;
            invalidData += c.FailureInvalidDataCount;
            sourceMissing += c.FailureSourceMissingCount;
            compileFailed += c.FailureCompileFailedCount;
            invalidIdentity += c.FailureInvalidIdentityCount;
            duplicateIdentity += c.FailureDuplicateIdentityCount;
            ioFailed += c.FailureIoFailedCount;
            unresolved += c.TraceUnresolvedCount;
            needsCsx += c.TraceNeedsCsxCount;
            placeholder += c.TracePlaceholderConditionCount;
            declarations += c.DeclarationCount;
        }

        return new EffectCompilationCounts(
            parsed, success, complete, invalidData, sourceMissing, compileFailed, invalidIdentity,
            duplicateIdentity, ioFailed, unresolved, needsCsx, placeholder, declarations);
    }

    // ── 输入目录扫描（顶层＋约定扩展名；匹配不敏感、排序 Ordinal、跨平台一致） ──

    private const string CardFaceSuffix = ".txt";

    private const string DslSuffix = ".dsl.json";

    private static bool IsDslFileName(string fileName) =>
        fileName.EndsWith(DslSuffix, StringComparison.OrdinalIgnoreCase);

    private static string DeriveBaseName(string fileName)
    {
        if (fileName.EndsWith(DslSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^DslSuffix.Length];
        }

        if (fileName.EndsWith(CardFaceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^CardFaceSuffix.Length];
        }

        return fileName;
    }

    /// <summary>
    /// 枚举匹配输入文件（显式后缀判定——不依赖 <c>EnumerateFiles</c> pattern 的 OS 行为；
    /// 非约定文件跳过且不计入任何计数）；序＝Ordinal 文件名序（两类统一排队——判定与报告条目序同源）。
    /// </summary>
    private static List<string> EnumerateInputFiles(string directory)
    {
        var matches = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (IsDslFileName(name) || name.EndsWith(CardFaceSuffix, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(file);
            }
        }

        matches.Sort((left, right) =>
            string.CompareOrdinal(Path.GetFileName(left), Path.GetFileName(right)));
        return matches;
    }

    /// <summary>行序（1 起——原文行；声明定位锚点的 best-effort 部分）。</summary>
    private static int LineOf(string text, int offset)
    {
        var line = 1;
        var limit = Math.Min(offset, text.Length);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>资产来源标识（运行参数快照——默认目录／显式目录路径／内存注入）。</summary>
    private string AssetSourceLabel =>
        _assetSource is null
            ? "default:" + EffectTemplateLoader.DefaultDirectory
            : _assetSource.Directory is not null
                ? "directory:" + _assetSource.Directory
                : "memory";

    // ── 内部数据（可变——组装前的工作态） ──

    private sealed class InputSource
    {
        public string? FilePath { get; init; }

        public string? FileName { get; init; }

        public string? ContextLabel { get; init; }

        public string? InlineText { get; init; }

        public required string BaseName { get; init; }

        public required EffectCompilationInputKind Kind { get; init; }

        public required int Version { get; init; }
    }

    private sealed class InputRecord
    {
        public InputRecord(InputSource source)
        {
            SourceFileName = source.FileName;
            ContextLabel = source.ContextLabel;
            Kind = source.Kind;
        }

        public string? SourceFileName { get; }

        public string? ContextLabel { get; }

        public EffectCompilationInputKind Kind { get; }

        public EffectCompilationFailureCategory? FileFailureCategory { get; set; }

        public string? FileFailureReason { get; set; }

        public List<EffectOutcome> Effects { get; } = new();

        public List<EffectCompilationUnresolvedTrace> Unresolved { get; } = new();

        public List<EffectCompilationDeclaration> Declarations { get; } = new();
    }

    private sealed class EffectOutcome
    {
        public EffectOutcome(string? sourceFileName, int declarationOrdinal, string effectId)
        {
            SourceFileName = sourceFileName;
            DeclarationOrdinal = declarationOrdinal;
            EffectId = effectId;
        }

        public string? SourceFileName { get; }

        public int DeclarationOrdinal { get; }

        public string EffectId { get; }

        public List<NeedsCsxTraceData> NeedsCsxTraces { get; } = new();

        public List<PlaceholderTraceData> PlaceholderTraces { get; } = new();

        public EffectSnapshot? Snapshot { get; set; }

        public EffectCompilationFailureCategory? FailureCategory { get; set; }

        public string? FailureReason { get; set; }

        public string? OutputFile { get; set; }

        public string? ExpectedTargetFile { get; set; }

        /// <summary>最终成功判定（落盘＝写成功；dry-run/单条＝编译成功）。</summary>
        public bool FinalSuccess => FailureCategory is null && Snapshot is not null;

        /// <summary>语义完整档位（成功且无占位条件、无 needsCsx/csx 留痕——递归覆盖内嵌）。</summary>
        public bool SemanticallyComplete =>
            FinalSuccess && NeedsCsxTraces.Count == 0 && PlaceholderTraces.Count == 0;
    }

    private sealed class NeedsCsxTraceData
    {
        public required EffectCompilationTraceCategory Category { get; init; }

        public required string Location { get; init; }

        public string? Script { get; init; }

        public string? OutputFile { get; set; }

        public string? ExpectedTargetFile { get; set; }
    }

    private sealed class PlaceholderTraceData
    {
        public required EffectCompilationTraceCategory Category { get; init; }

        public required string Location { get; init; }

        public string? RawText { get; init; }

        public string? OutputFile { get; set; }

        public string? ExpectedTargetFile { get; set; }
    }

    private sealed class BatchState
    {
        public EffectAssetBundle? Bundle { get; set; }

        public string? AssetError { get; set; }

        public EffectCompiler? Compiler { get; set; }

        public Dictionary<string, SeenIdentity> Seen { get; } = new(StringComparer.Ordinal);

        public List<InputRecord> Inputs { get; } = new();

        public List<EffectOutcome> Outcomes { get; } = new();
    }

    private sealed record SeenIdentity(string EffectId, string? SourceFileName, string? ContextLabel, int Ordinal);
}
