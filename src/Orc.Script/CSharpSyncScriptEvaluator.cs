using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Orc.Core;

namespace Orc.Script;

/// <summary>
/// 同步 csx 求值器（<see cref="ISyncScriptEvaluator"/> 的 Roslyn 实现）：判定器/资产服务包装器专用。
/// 入口契约为具名变量 <c>Func&lt;object[]?, object[]?, object[]?&gt;</c>（如 <c>Handle</c>）；
/// 与异步触发器 handler 求值器（<see cref="CSharpScriptEvaluator"/>）并列、共用安全选项与缓存策略。
/// </summary>
public sealed class CSharpSyncScriptEvaluator : ISyncScriptEvaluator
{
    private readonly CSharpScriptSecurityOptions _options;
    private readonly ConcurrentDictionary<string, SyncScriptEvaluationResult> _cache = new(StringComparer.Ordinal);
    private readonly Lazy<ScriptOptions> _scriptOptions;

    /// <summary>创建同步求值器（options 缺省＝默认安全选项）。</summary>
    public CSharpSyncScriptEvaluator(CSharpScriptSecurityOptions? options = null)
    {
        _options = options ?? new CSharpScriptSecurityOptions();
        _scriptOptions = new Lazy<ScriptOptions>(BuildScriptOptions, isThreadSafe: true);
    }

    /// <returns>总缓存条数（诊断用）。</returns>
    public int CacheCount => _cache.Count;

    /// <inheritdoc />
    public SyncScriptEvaluationResult Evaluate(SyncScriptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : _options.TimeoutMs;
        var key =
            $"{request.EntryName}|{timeoutMs}|{request.CacheScope ?? "-"}|{StableHash.Fnv1a64(request.Source):x16}";
        return _cache.GetOrAdd(key, _ => EvaluateCore(request, timeoutMs));
    }

    private SyncScriptEvaluationResult EvaluateCore(SyncScriptRequest request, int timeoutMs)
    {
        var violation = FindViolation(request.Source);
        if (violation is not null)
        {
            return SyncScriptEvaluationResult.Fail("sandbox", violation);
        }

        Script<object> script;
        try
        {
            script = CSharpScript.Create(request.Source, _scriptOptions.Value);
        }
        catch (Exception ex)
        {
            return SyncScriptEvaluationResult.Fail("compile", ex.Message);
        }

        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            return SyncScriptEvaluationResult.Fail(
                "compile", string.Join("; ", errors.Select(e => e.GetMessage())));
        }

        ScriptState<object>? state;
        try
        {
            var run = script.RunAsync();
            if (!run.Wait(timeoutMs))
            {
                return SyncScriptEvaluationResult.Fail(
                    "timeout", $"脚本执行超过 {timeoutMs}ms（同进程尽力中断）。");
            }

            state = run.Result;
        }
        catch (AggregateException ex)
        {
            return SyncScriptEvaluationResult.Fail("runtime", ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return SyncScriptEvaluationResult.Fail("runtime", ex.Message);
        }

        if (state is null)
        {
            return SyncScriptEvaluationResult.Fail("runtime", "脚本执行未产出状态。");
        }

        var entryName = string.IsNullOrWhiteSpace(request.EntryName) ? "Handle" : request.EntryName;
        var entry = state.Variables.FirstOrDefault(
            v => string.Equals(v.Name, entryName, StringComparison.Ordinal));
        if (entry is null)
        {
            return SyncScriptEvaluationResult.Fail("entry-missing", $"脚本未定义入口 '{entryName}'。");
        }

        if (entry.Value is not Func<object[]?, object[]?, object[]?> handler)
        {
            return SyncScriptEvaluationResult.Fail(
                "signature",
                $"入口 '{entryName}' 签名不符（期望 Func<object[]?, object[]?, object[]?>）。");
        }

        return SyncScriptEvaluationResult.Ok(handler);
    }

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

    private ScriptOptions BuildScriptOptions()
    {
        var allowed = new HashSet<string>(_options.AllowedAssemblies, StringComparer.OrdinalIgnoreCase);
        var references = new List<MetadataReference>();

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
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
            {
                continue;
            }

            references.Add(MetadataReference.CreateFromFile(location));
        }

        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(_options.AllowedImports);
    }
}
