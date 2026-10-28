using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Orc.Core;

namespace Orc.Script;

/// <summary>
/// csx 脚本求值器（S-C6；<see cref="IScriptEvaluator"/> 的 Roslyn 实现）：
/// ①安全检查（引用白名单 → 导入白名单 → 关键字过滤）；②编译（诊断错误＝compile 失败）；
/// ③执行取入口（具名变量；入口缺失/类型不符＝entry-missing/signature）；④超时（timeout）；
/// ⑤运行时异常（runtime）。一切失败结构化返回、**不抛异常**。
/// 契约（csx 入口写法）：脚本以**具名变量**定义入口，值须为
/// <c>Func&lt;TView, Context, CancellationToken, Task&gt;</c>（如
/// <c>Func&lt;MyView, Context, CancellationToken, Task&gt; HandleAsync = (view, ctx, ct) =&gt; ...;</c>）。
/// 结果按（视图类型 + 入口名 + 超时 + 源码哈希）缓存复用（编译一次）。
/// 安全定位：**尽力而为**的弱沙箱（同进程）；非安全边界，强隔离须进程级。
/// </summary>
public sealed class CSharpScriptEvaluator : IScriptEvaluator
{
    private readonly CSharpScriptSecurityOptions _options;
    private readonly ConcurrentDictionary<string, ScriptEvaluationResult> _cache = new(StringComparer.Ordinal);
    private readonly Lazy<ScriptOptions> _scriptOptions;

    /// <summary>创建求值器（options 缺省＝<see cref="CSharpScriptSecurityOptions"/> 默认）。</summary>
    public CSharpScriptEvaluator(CSharpScriptSecurityOptions? options = null)
    {
        _options = options ?? new CSharpScriptSecurityOptions();
        _scriptOptions = new Lazy<ScriptOptions>(BuildScriptOptions, isThreadSafe: true);
    }

    /// <returns>总缓存条数（诊断用）。</returns>
    public int CacheCount => _cache.Count;

    /// <inheritdoc />
    public ScriptEvaluationResult Evaluate(ScriptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : _options.TimeoutMs;
        var key = $"{request.ViewType.AssemblyQualifiedName}|{request.EntryName}|{timeoutMs}|{StableHash.Fnv1a64(request.Source):x16}";

        // 视图静态语法检查**不进缓存**：它依赖本次请求声明的视图字段（同一源码可被不同视图声明的成员引用），
        // 而缓存键按 S5 定稿只含「承载视图类型 + 入口名 + 超时 + 源码哈希」，不再按形状分桶。
        var analysis = request.View is null
            ? null
            : ViewBindingAnalyzer.Analyze(
                request.Source, request.View, request.OwnerMembers ?? Array.Empty<string>());

        if (analysis is not null && !analysis.IsValid)
        {
            return ScriptEvaluationResult.Fail(ScriptErrorCategories.Binding, analysis.Describe(), analysis);
        }

        if (_cache.TryGetValue(key, out var cached))
        {
            if (analysis is null)
            {
                return cached; // 旧路径：不做静态检查，直接复用缓存实例（含引用同一性）
            }

            // 缓存项只承载"编译 + 入口取用"的结果；静态检查结论总是按本次视图声明重算。
            return cached.Success
                ? ScriptEvaluationResult.Ok(cached.Handler!, analysis)
                : ScriptEvaluationResult.Fail(cached.ErrorCategory!, cached.Error!, analysis);
        }

        var result = EvaluateCore(request, timeoutMs, analysis);
        _cache[key] = result;
        return result;
    }

    private ScriptEvaluationResult EvaluateCore(
        ScriptRequest request, int timeoutMs, Orc.Core.Model.ViewBindingAnalysis? analysis)
    {
        var violation = FindViolation(request.Source);
        if (violation is not null)
        {
            return ScriptEvaluationResult.Fail(ScriptErrorCategories.Sandbox, violation, analysis);
        }

        Script<object> script;
        try
        {
            script = CSharpScript.Create(request.Source, _scriptOptions.Value);
        }
        catch (Exception ex)
        {
            return ScriptEvaluationResult.Fail(ScriptErrorCategories.Compile, ex.Message, analysis);
        }

        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            return ScriptEvaluationResult.Fail(
                ScriptErrorCategories.Compile, string.Join("; ", errors.Select(e => e.GetMessage())), analysis);
        }

        ScriptState<object>? state;
        try
        {
            var run = script.RunAsync();
            if (!run.Wait(timeoutMs))
            {
                // 说明：同进程内无法真正终止脚本——超时＝逻辑中断（尽力而为；强隔离须进程级）。
                return ScriptEvaluationResult.Fail(
                    ScriptErrorCategories.Timeout, $"脚本执行超过 {timeoutMs}ms（同进程尽力中断）。", analysis);
            }

            state = run.Result;
        }
        catch (AggregateException ex)
        {
            return ScriptEvaluationResult.Fail(
                ScriptErrorCategories.Runtime, ex.InnerException?.Message ?? ex.Message, analysis);
        }
        catch (Exception ex)
        {
            return ScriptEvaluationResult.Fail(ScriptErrorCategories.Runtime, ex.Message, analysis);
        }

        if (state is null)
        {
            return ScriptEvaluationResult.Fail(
                ScriptErrorCategories.Runtime, "脚本执行未产出状态。", analysis);
        }

        var entry = state.Variables.FirstOrDefault(v => string.Equals(v.Name, request.EntryName, StringComparison.Ordinal));
        if (entry is null)
        {
            return ScriptEvaluationResult.Fail(
                "entry-missing", $"脚本未定义入口 '{request.EntryName}'。", analysis);
        }

        if (entry.Value is not Delegate handler)
        {
            return ScriptEvaluationResult.Fail(
                "entry-missing",
                $"入口 '{request.EntryName}' 不是委托值（实际：{entry.Value?.GetType().Name ?? "null"}）。",
                analysis);
        }

        // 承载视图路径（S5 第 4 条）：按"形状 + 首参可接收性"判定，不按类型名逐字相等。
        if (request.ViewType == typeof(Orc.Core.Model.ViewCarrier))
        {
            var kernelHandler = Orc.Core.Model.HandlerSignature.TryBindCarrier(handler);
            if (kernelHandler is null)
            {
                return ScriptEvaluationResult.Fail(
                    ScriptErrorCategories.Signature,
                    $"入口 '{request.EntryName}' 签名不符：{Orc.Core.Model.HandlerSignature.Describe(handler.GetType())}",
                    analysis);
            }

            return ScriptEvaluationResult.Ok((Delegate)kernelHandler, analysis);
        }

        var bound = ScriptHandlerAdapter.TryBind(request.ViewType, handler);
        if (bound is null)
        {
            return ScriptEvaluationResult.Fail(
                ScriptErrorCategories.Signature,
                $"入口 '{request.EntryName}' 签名不符（期望 Func<{request.ViewType.Name}, Context, CancellationToken, Task>）。",
                analysis);
        }

        return ScriptEvaluationResult.Ok((Delegate)bound, analysis);
    }

    /// <summary>关键字过滤（文本级；命中＝沙箱拒绝。文档注明可被绕过，非安全边界）。</summary>
    private string? FindViolation(string source)
    {
        foreach (var keyword in _options.BlockedKeywords)
        {
            if (!string.IsNullOrEmpty(keyword) && source.Contains(keyword, StringComparison.Ordinal))
            {
                return $"脚本命中屏蔽片段 '{keyword}'（安全策略拒绝；非安全边界，仅尽力而为）。";
            }
        }

        return null;
    }

    /// <summary>按引用白名单构建 <see cref="ScriptOptions"/>（仅取白名单内的已加载程序集）。</summary>
    private ScriptOptions BuildScriptOptions()
    {
        var allowed = new HashSet<string>(_options.AllowedAssemblies, StringComparer.OrdinalIgnoreCase);
        var references = new List<MetadataReference>();

        // dynamic（承载视图）需要 DLR 绑定器程序集；它未必已被进程加载——白名单含它时显式触发一次加载，
        // 否则引用集里没有它，csx 里写 dynamic 会直接编译失败（S5／W-15）。
        foreach (var required in new[] { "Microsoft.CSharp", "System.Linq.Expressions" })
        {
            if (!allowed.Contains(required))
            {
                continue;
            }

            try
            {
                _ = System.Reflection.Assembly.Load(required);
            }
            catch (Exception)
            {
                // 加载失败＝引用集里没有它；后面的编译会给出 compile 失败（不静默吞掉结果）。
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || !allowed.Contains(assembly.GetName().Name ?? string.Empty))
            {
                continue;
            }

            string location;
            try
            {
                location = assembly.Location;
            }
            catch (Exception)
            {
                continue; // 单文件发布等无路径场景：跳过
            }

            if (!string.IsNullOrEmpty(location))
            {
                references.Add(MetadataReference.CreateFromFile(location));
            }
        }

        return ScriptOptions.Default.WithReferences(references).WithImports(_options.AllowedImports);
    }
}
