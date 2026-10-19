using System.Diagnostics;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orc.Cards;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Effects;
using Orc.Game.Managers;

namespace Orc.Game.Tests;

/// <summary>
/// 契约基线反射快照（S17）：把「门面冻结面」抓成确定性 JSON，供 <see cref="ContractFreezeTests"/> 逐条 diff。
/// <para>
/// 覆盖（S17 第 2 条）：<c>GameUpdates</c> 全部 public const string（名＋值）、<c>EffectRuntime</c> 合并 partial 后的
/// 完整成员集（键＝名＋泛型元数＋参数类型全名＋返回类型全名）、<c>GameEntryPoints.All</c> 逐条（按清单序）、
/// <c>PlayResult</c>/<c>CommandResult</c>/<c>CardPlaceResult</c>/<c>DeckFetchResult</c> 公共属性与构造、
/// <c>PrefabJson</c>/<c>CardDefinition</c> 的公共 schema 面（DTO 为 private 嵌套类型，故以公共序列化/定义面为准）。
/// </para>
/// <para>基线文件是受版本控制的资产：生成只经 <c>ORC_WRITE_BASELINE=1</c> 的专用生成测试；契约测试在
/// <c>ORC_REGENERATE_BASELINE=1</c> 时只告警、不重写。</para>
/// </summary>
internal static class ContractSnapshot
{
    /// <summary>基线相对仓库根的路径。</summary>
    internal const string RelativePath = "tests/Orc.Game.Tests/Baselines/contract-freeze.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>仓库根（含 <c>OrcEngine.sln</c> 的目录）。</summary>
    internal static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OrcEngine.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("未找到仓库根（OrcEngine.sln）。");
    }

    /// <summary>基线文件绝对路径。</summary>
    internal static string BaselinePath => Path.Combine(FindRepoRoot(), RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>当前 HEAD commit（git 不可用＝<c>unknown</c>，不阻断测试）。</summary>
    internal static string CurrentCommit()
    {
        try
        {
            var start = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = FindRepoRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return "unknown";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>生成完整基文档（含头部 commit 与生成时间）。</summary>
    internal static JsonObject Capture(string commit, DateTimeOffset generatedAt)
    {
        var body = CaptureBody();
        var root = new JsonObject
        {
            ["commit"] = commit,
            ["generatedAt"] = generatedAt.ToString("O"),
        };
        foreach (var pair in body)
        {
            root[pair.Key] = pair.Value?.DeepClone();
        }

        return root;
    }

    /// <summary>生成无头部（受比对部分）的文档。</summary>
    internal static JsonObject CaptureBody()
    {
        return new JsonObject
        {
            ["gameUpdates"] = GameUpdatesSnapshot(),
            ["effectRuntime"] = EffectRuntimeSnapshot(),
            ["gameEntryPoints"] = GameEntryPointsSnapshot(),
            ["resultShapes"] = ResultShapesSnapshot(),
            ["schemaDto"] = SchemaDtoSnapshot(),
            ["prefabJsonDtos"] = PrefabJsonDtoSnapshot(),
        };
    }

    /// <summary>确定性 JSON 文本（2 空格缩进、LF、尾随换行）。</summary>
    internal static string ToJson(JsonObject root)
        => JsonSerializer.Serialize(root, WriteOptions).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    /// <summary>受比对部分（不含 commit/generatedAt）的规范化行集合（供字段级 diff）。</summary>
    internal static IReadOnlyList<string> CanonicalLines(JsonObject root)
    {
        var lines = new List<string>();

        if (root["gameUpdates"] is JsonArray updates)
        {
            lines.Add("## gameUpdates");
            foreach (var node in updates)
            {
                lines.Add($"GameUpdates.{node!["name"]} = {node["value"]}");
            }
        }

        if (root["effectRuntime"] is JsonArray members)
        {
            lines.Add("## effectRuntime");
            foreach (var node in members)
            {
                lines.Add($"EffectRuntime {node!.GetValue<string>()}");
            }
        }

        if (root["prefabJsonDtos"] is JsonArray dtos)
        {
            lines.Add("## prefabJsonDtos");
            foreach (var node in dtos)
            {
                lines.Add($"PrefabJson {node!.GetValue<string>()}");
            }
        }

        if (root["gameEntryPoints"] is JsonArray entries)
        {
            lines.Add("## gameEntryPoints");
            var index = 0;
            foreach (var node in entries)
            {
                lines.Add(
                    $"Entries[{index++}] {node!["name"]}|{node["category"]}|{node["owner"]}|{node["signature"]}|{node["gate"]}|{node["outcome"]}|{node["status"]}");
            }
        }

        AppendShape(lines, "resultShapes", root["resultShapes"] as JsonObject);
        AppendShape(lines, "schemaDto", root["schemaDto"] as JsonObject);
        return lines;
    }

    private static void AppendShape(List<string> lines, string section, JsonObject? shapes)
    {
        if (shapes is null)
        {
            return;
        }

        lines.Add($"## {section}");
        foreach (var type in shapes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var body = type.Value!.AsObject();
            lines.Add($"{type.Key} properties: {string.Join("; ", body["properties"]!.AsArray().Select(n => n!.GetValue<string>()))}");
            lines.Add($"{type.Key} constructors: {string.Join("; ", body["constructors"]!.AsArray().Select(n => n!.GetValue<string>()))}");
        }
    }

    /// <summary>逐条 unified diff（仅输出差异行；无差异＝空）。</summary>
    internal static IReadOnlyList<string> Diff(JsonObject expected, JsonObject actual)
    {
        var oldLines = CanonicalLines(expected);
        var newLines = CanonicalLines(actual);
        return DiffLines(oldLines, newLines);
    }

    private static List<string> DiffLines(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var n = oldLines.Count;
        var m = newLines.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(oldLines[i], newLines[j], StringComparison.Ordinal)
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<string>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
            {
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                result.Add($"- {oldLines[x++]}");
            }
            else
            {
                result.Add($"+ {newLines[y++]}");
            }
        }

        while (x < n)
        {
            result.Add($"- {oldLines[x++]}");
        }

        while (y < m)
        {
            result.Add($"+ {newLines[y++]}");
        }

        return result;
    }

    // ---------- 各面 ----------

    private static JsonArray GameUpdatesSnapshot()
    {
        var items = typeof(GameUpdates)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .Select(field => (JsonNode)new JsonObject
            {
                ["name"] = field.Name,
                ["value"] = (string?)field.GetRawConstantValue(),
            })
            .ToArray();
        return new JsonArray(items);
    }

    private static JsonArray EffectRuntimeSnapshot()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var keys = typeof(EffectRuntime)
            .GetMethods(flags)
            .Select(MethodKey)
            .Concat(typeof(EffectRuntime).GetProperties(flags).Select(PropertyKey))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => (JsonNode)JsonValue.Create(key)!)
            .ToArray();
        return new JsonArray(keys);
    }

    private static string MethodKey(MethodInfo method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(ParameterKey));
        var returnType = TypeKey(method.ReturnType);
        var arity = method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0;
        return $"{method.Name}`{arity}({parameters}):{returnType}";
    }

    private static string PropertyKey(PropertyInfo property)
    {
        var index = property.GetIndexParameters();
        var suffix = index.Length == 0 ? string.Empty : $"({string.Join(", ", index.Select(ParameterKey))})";
        return $"property {property.Name}{suffix}:{TypeKey(property.PropertyType)}";
    }

    private static string ParameterKey(ParameterInfo parameter)
    {
        var prefix = parameter.ParameterType.IsByRef
            ? parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref "
            : string.Empty;
        return prefix + TypeKey(parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType);
    }

    private static string TypeKey(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return TypeKey(type.GetElementType()!) + "[]";
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var name = (definition.FullName ?? definition.Name).Split('`')[0];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeKey))}>";
        }

        return type.FullName ?? type.Name;
    }

    private static JsonArray GameEntryPointsSnapshot()
    {
        var items = GameEntryPoints.All
            .Select(entry => (JsonNode)new JsonObject
            {
                ["name"] = entry.Name,
                ["category"] = entry.Category,
                ["owner"] = entry.Owner,
                ["signature"] = entry.Signature,
                ["gate"] = entry.Gate,
                ["outcome"] = entry.Outcome,
                ["status"] = entry.Status.ToString(),
            })
            .ToArray();
        return new JsonArray(items);
    }

    private static JsonObject ResultShapesSnapshot() => new()
    {
        ["Orc.Game.Cards.PlayResult"] = Shape(typeof(PlayResult)),
        ["Orc.Game.Commanding.CommandResult"] = Shape(typeof(CommandResult)),
        ["Orc.Game.CardPlaceResult"] = Shape(typeof(CardPlaceResult)),
        ["Orc.Game.Managers.DeckFetchResult"] = Shape(typeof(DeckFetchResult)),
    };

    /// <summary>
    /// <c>PrefabJson</c> 的 schema DTO：DTO 类型为 private 嵌套类（不构成公共类型面），
    /// 故按「嵌套类型名.属性名:类型全名」冻结其属性集——序列化 schema 的键与类型由此受控。
    /// </summary>
    private static JsonArray PrefabJsonDtoSnapshot()
    {
        var items = typeof(PrefabJson)
            .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => (JsonNode)JsonValue.Create(
                    $"{type.Name}.{property.Name}:{TypeKey(property.PropertyType)}")!))
            .ToArray();
        return new JsonArray(items);
    }

    private static JsonObject SchemaDtoSnapshot() => new()
    {
        ["Orc.Cards.PrefabJson"] = Shape(typeof(PrefabJson)),
        ["Orc.Game.Cards.CardDefinition"] = Shape(typeof(CardDefinition)),
    };

    private static JsonObject Shape(Type type)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => (JsonNode)JsonValue.Create($"{property.Name}:{TypeKey(property.PropertyType)}")!)
            .ToArray();
        var constructors = type
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Select(ctor => string.Join(", ", ctor.GetParameters().Select(ParameterKey)))
            .OrderBy(signature => signature, StringComparer.Ordinal)
            .Select(signature => (JsonNode)JsonValue.Create(signature)!)
            .ToArray();

        return new JsonObject
        {
            ["properties"] = new JsonArray(properties),
            ["constructors"] = new JsonArray(constructors),
        };
    }
}
