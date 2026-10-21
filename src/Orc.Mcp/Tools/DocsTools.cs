using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Orc.Mcp.Contracts;

namespace Orc.Mcp.Tools;

/// <summary>
/// 面向 agent 的文档读取工具（R4）：`docs.list` 扁平清单 + 分节目录；`docs.read` 按行段读取。
/// <para>
/// 文档集＝**内嵌资源**（构建期打入程序集；无工作目录依赖）。
/// 事实表条目（事件/入口/判定器/词条/组件清单）由 <c>diag.*</c> 工具以代码生成方式提供，避免文档与代码漂移。
/// </para>
/// </summary>
internal static class DocsTools
{
    private const string ResourcePrefix = "Orc.Mcp.Docs.";

    public static void Register(McpService service)
    {
        service.AddTool(
            "docs.list",
            "docs",
            "列出全部面向 agent 的文档条目（id/标题/分组/摘要/分节目录/总行数）。",
            """
            { "type": "object", "properties": {}, "additionalProperties": false }
            """,
            (call, ct) => Task.FromResult(List()),
            readOnly: true);

        service.AddTool(
            "docs.read",
            "docs",
            "按 id 读取文档内容（可用 offset/limit 按行段读取；缺省返回全文）。",
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string", "description": "文档 id（来自 docs.list）" },
                "offset": { "type": "integer", "description": "起始行（0 起；缺省 0）" },
                "limit": { "type": "integer", "description": "读取行数（缺省＝到末尾）" }
              },
              "required": ["id"],
              "additionalProperties": false
            }
            """,
            (call, ct) => Task.FromResult(Read(call)),
            readOnly: true);
    }

    private static IEnumerable<(string Id, string ResourceName)> EnumerateResources()
    {
        var assembly = typeof(McpService).Assembly;
        var names = assembly.GetManifestResourceNames();
        Array.Sort(names, StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = name[ResourcePrefix.Length..^3];
            yield return (id, name);
        }
    }

    private static string ReadResourceText(string resourceName)
    {
        var assembly = typeof(McpService).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"内嵌文档资源不存在：{resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static McpToolResult List()
    {
        var documents = new JsonArray();

        foreach (var (id, resourceName) in EnumerateResources())
        {
            var text = ReadResourceText(resourceName);
            var parsed = Parse(text);

            var sections = new JsonArray();
            foreach (var section in parsed.Sections)
            {
                sections.Add(new JsonObject
                {
                    ["title"] = section.Title,
                    ["line"] = section.Line,
                    ["level"] = section.Level,
                });
            }

            documents.Add(new JsonObject
            {
                ["id"] = id,
                ["title"] = parsed.Title,
                ["category"] = CategoryOf(id),
                ["summary"] = parsed.Summary,
                ["lines"] = parsed.LineCount,
                ["updatedAt"] = File.GetLastWriteTimeUtc(typeof(McpService).Assembly.Location).ToString("O", CultureInfo.InvariantCulture),
                ["sections"] = sections,
            });
        }

        return McpToolResult.Success($"{documents.Count} 个文档条目。", details: new JsonObject { ["documents"] = documents });
    }

    private static McpToolResult Read(McpToolCall call)
    {
        var id = ToolArgs.RequireString(call.Arguments, "id");
        var offset = ToolArgs.OptionalInt(call.Arguments, "offset") ?? 0;
        var limit = ToolArgs.OptionalInt(call.Arguments, "limit");

        var match = EnumerateResources().FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
        if (match.ResourceName is null)
        {
            var available = EnumerateResources().Select(entry => entry.Id).ToArray();
            return McpToolResult.Failure(
                McpCodes.InvalidArguments,
                $"文档 id '{id}' 不存在。",
                new JsonObject { ["availableIds"] = SandboxTools.ToJsonArray(available) });
        }

        var text = ReadResourceText(match.ResourceName);
        var parsed = Parse(text);

        if (offset < 0)
        {
            return McpToolResult.Failure(McpCodes.InvalidArguments, "offset 不能为负。");
        }

        if (offset >= parsed.Lines.Length)
        {
            return McpToolResult.Failure(
                McpCodes.InvalidArguments,
                $"offset {offset} 超出文档长度（总行数 {parsed.Lines.Length}）。");
        }

        var count = limit is > 0 ? Math.Min(limit.Value, parsed.Lines.Length - offset) : parsed.Lines.Length - offset;
        var slice = string.Join('\n', parsed.Lines.Skip(offset).Take(count));

        var sections = new JsonArray();
        foreach (var section in parsed.Sections)
        {
            sections.Add(new JsonObject
            {
                ["title"] = section.Title,
                ["line"] = section.Line,
                ["level"] = section.Level,
            });
        }

        var details = new JsonObject
        {
            ["id"] = id,
            ["title"] = parsed.Title,
            ["totalLines"] = parsed.Lines.Length,
            ["offset"] = offset,
            ["returnedLines"] = count,
            ["content"] = slice,
            ["sections"] = sections,
        };

        return McpToolResult.Success($"已读取 '{id}' 第 {offset}..{offset + count - 1} 行。", details: details);
    }

    private static string CategoryOf(string id)
    {
        var separator = id.IndexOf('.', StringComparison.Ordinal);
        return separator > 0 ? id[..separator] : "general";
    }

    private static (string Title, string Summary, (string Title, int Line, int Level)[] Sections, string[] Lines, int LineCount) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sections = new List<(string, int, int)>();
        var title = string.Empty;
        var summary = string.Empty;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith('#'))
            {
                var level = 0;
                while (level < line.Length && line[level] == '#')
                {
                    level++;
                }

                var heading = line[level..].Trim();
                if (level == 1 && title.Length == 0)
                {
                    title = heading;
                }

                if (level is >= 1 and <= 3 && heading.Length > 0)
                {
                    sections.Add((heading, i, level));
                }

                continue;
            }

            if (summary.Length == 0 && line.Trim().Length > 0 && !line.StartsWith(">", StringComparison.Ordinal))
            {
                summary = line.Trim();
                if (summary.Length > 160)
                {
                    summary = summary[..160] + "…";
                }
            }
        }

        return (title.Length > 0 ? title : "(无标题)", summary, sections.ToArray(), lines, lines.Length);
    }
}
