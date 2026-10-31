using System.Text.Json;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>S1 验收：契约文本包的位置、内容边界与双版本轴（R21／D1／D2／D8／D19）。</summary>
public class ContractPackageTests
{
    [Fact]
    public void 契约包在仓库顶层_与src平级()
    {
        Assert.True(Directory.Exists(Fixtures.ContractRoot), $"契约包缺失：{Fixtures.ContractRoot}");
        Assert.Equal(
            Path.Combine(Fixtures.RepoRoot, "contracts", "handler-dsl", "v1"),
            Fixtures.ContractRoot);
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}",
            Fixtures.ContractRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    [Fact]
    public void 契约包文件齐全()
    {
        foreach (var relative in new[]
                 {
                     "SPEC.md",
                     "schema.json",
                     "catalog.schema.json",
                     "EXCLUSIONS.md",
                     Path.Combine("vectors", "README.md"),
                 })
        {
            Assert.True(File.Exists(Path.Combine(Fixtures.ContractRoot, relative)), $"缺少 {relative}");
        }

        Assert.True(Directory.Exists(Path.Combine(Fixtures.ContractRoot, "vectors", "check")));
        Assert.True(Directory.Exists(Path.Combine(Fixtures.ContractRoot, "vectors", "gen")));
    }

    [Fact]
    public void 契约包不属于任何csproj的编译范围()
    {
        var offenders = new List<string>();
        foreach (var project in Directory.GetFiles(Fixtures.RepoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var normalized = project.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.Ordinal)
                || normalized.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(project).Replace('\\', '/');
            // 只看真正的 MSBuild 项（Include=…）：注释里提到契约包路径不算「纳入编译范围」。
            var includesContract = text
                .Split('\n')
                .Where(line => line.Contains("Include=", StringComparison.OrdinalIgnoreCase))
                .Any(line => line.Contains("contracts/handler-dsl", StringComparison.Ordinal));
            if (includesContract)
            {
                offenders.Add(normalized);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void 契约包不含CSharp源码()
    {
        var sources = Directory.GetFiles(Fixtures.ContractRoot, "*.cs", SearchOption.AllDirectories);
        Assert.Empty(sources);
    }

    [Fact]
    public void SPEC含生成格式冻结节且逐项列全()
    {
        var spec = File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "SPEC.md"));
        Assert.Contains("## §13 生成格式（冻结）", spec, StringComparison.Ordinal);

        for (var i = 1; i <= 12; i++)
        {
            Assert.Contains($"| F{i} |", spec, StringComparison.Ordinal);
        }

        // 格式改动＝破坏性变更（须升 schemaVersion）。
        Assert.Contains("改本节任一项＝**破坏性变更**", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void SPEC含标注语言元模型_但不含per_method实例表()
    {
        var spec = File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "SPEC.md"));
        Assert.Contains("### §12.4 标注语言", spec, StringComparison.Ordinal);
        Assert.Contains("本包只定义「标注这门语言」", spec, StringComparison.Ordinal);

        var annotations = Directory.GetFiles(Fixtures.ContractRoot, "*annotations*", SearchOption.AllDirectories);
        Assert.Empty(annotations);
    }

    [Fact]
    public void SPEC写明两条版本轴与破坏性变更义务()
    {
        var spec = File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "SPEC.md"));
        Assert.Contains("`schemaVersion`", spec, StringComparison.Ordinal);
        Assert.Contains("`catalogRevision`", spec, StringComparison.Ordinal);
        Assert.Contains("只递增 `catalogRevision`，旧 DSL 照常装载", spec, StringComparison.Ordinal);
        Assert.Contains("**破坏**", spec, StringComparison.Ordinal);
        Assert.Contains("「前端零改动」的准确含义", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void SPEC写明封闭错误码集为13个()
    {
        var spec = File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "SPEC.md"));
        Assert.Contains("### §14.1 封闭码集（v1，13 个）", spec, StringComparison.Ordinal);
        Assert.All(HandlerDslErrorCodes.All, code => Assert.Contains($"`{code}`", spec, StringComparison.Ordinal));
    }

    [Fact]
    public void schema规定根必填与字段封闭()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "schema.json")));
        var root = json.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("schemaVersion", required);
        Assert.Contains("actions", required);
        Assert.DoesNotContain("catalogRevision", required); // 缺省合法
    }

    [Fact]
    public void schema的内建op与代码常量一致()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "schema.json")));
        var defs = json.RootElement.GetProperty("$defs");

        Assert.All(HandlerDslContract.BuiltinOps, op => Assert.True(defs.TryGetProperty(op, out _), $"schema 缺少内建 op 定义：{op}"));

        var builtinEnum = defs.GetProperty("actionNode").GetProperty("properties").GetProperty("op")
            .GetProperty("not").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(HandlerDslContract.BuiltinOps.OrderBy(o => o, StringComparer.Ordinal),
            builtinEnum.OrderBy(o => o, StringComparer.Ordinal));
    }

    [Fact]
    public void schema与catalogSchema的引脚类型封闭集与代码一致()
    {
        var schemaTypes = ReadPinTypeEnum(Path.Combine(Fixtures.ContractRoot, "schema.json"));
        var catalogTypes = ReadPinTypeEnum(Path.Combine(Fixtures.ContractRoot, "catalog.schema.json"));

        Assert.Equal(PinType.CanonicalNames, schemaTypes);
        Assert.Equal(PinType.CanonicalNames, catalogTypes);
    }

    [Fact]
    public void 契约包不出现任意CLR类型()
    {
        foreach (var name in new[] { "schema.json", "catalog.schema.json" })
        {
            var text = File.ReadAllText(Path.Combine(Fixtures.ContractRoot, name));
            Assert.DoesNotContain("System.", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void catalogSchema是目录元模型_不含方法实例()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "catalog.schema.json")));
        var root = json.RootElement;
        var properties = root.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Contains("nodes", properties);
        Assert.Contains("catalogRevision", properties);
        Assert.DoesNotContain("annotations", properties);
        Assert.DoesNotContain("methods", properties);
    }

    [Fact]
    public void 每条vector都带kind与catalog快照()
    {
        var directory = Path.Combine(Fixtures.ContractRoot, "vectors", "check");
        var files = Directory.GetFiles(directory, "*.check.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var root = json.RootElement;
            Assert.Equal("check", root.GetProperty("kind").GetString());
            Assert.True(root.TryGetProperty("catalog", out var catalog));
            Assert.True(catalog.TryGetProperty("catalogRevision", out _));
            Assert.True(catalog.TryGetProperty("scannedFaces", out _));
            Assert.True(root.TryGetProperty("view", out _));
            Assert.True(root.GetProperty("expect").TryGetProperty("code", out _));
            Assert.True(root.GetProperty("expect").TryGetProperty("nodePath", out _));
        }
    }

    [Fact]
    public void 生成类期望文本必须与gen向量成对出现()
    {
        var directory = Path.Combine(Fixtures.ContractRoot, "vectors", "gen");
        foreach (var expected in Directory.GetFiles(directory, "*.expected.csx"))
        {
            var json = expected[..^".expected.csx".Length] + ".gen.json";
            Assert.True(File.Exists(json), $"期望文本没有对应的 gen 向量：{Path.GetFileName(expected)}");
        }
    }

    [Fact]
    public void 排除清单每条含方法名理由替代路径()
    {
        var text = File.ReadAllText(Path.Combine(Fixtures.ContractRoot, "EXCLUSIONS.md"));
        Assert.Contains("| 方法 | 理由 | 替代路径 |", text, StringComparison.Ordinal);

        foreach (var method in new[] { "ResolveFor", "Judge", "EvaluateCondition", "AurasOf", "SelectAsync" })
        {
            Assert.Contains($"`{method}", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 内核只持纯接口_不引用DSL类型系统()
    {
        var kernel = typeof(Orc.Core.IHandlerDslCompiler).Assembly;
        var dsl = typeof(PinType).Assembly;

        Assert.NotSame(kernel, dsl);
        Assert.DoesNotContain(
            kernel.GetTypes(),
            type => type.Namespace?.StartsWith("Orc.HandlerDsl", StringComparison.Ordinal) == true);

        // satellite 注入方向：DSL 程序集引用内核，内核不引用 DSL。
        Assert.Contains(dsl.GetReferencedAssemblies(), a => a.Name == kernel.GetName().Name);
        Assert.DoesNotContain(kernel.GetReferencedAssemblies(), a => a.Name == dsl.GetName().Name);

        // 接口只认 string 进、string 出 ＋ 错误列表；视图声明以**内核纯数据形态**（HandlerDslView）传入，
        // 它同样是 string 类型名 ＋ bool，不把契约的引脚类型系统带进内核（D4 裁决：视图参数上提到接口）。
        var compile = typeof(Orc.Core.IHandlerDslCompiler).GetMethod("Compile")!;
        Assert.Equal(
            new[] { typeof(string), typeof(Orc.Core.IHandlerCatalog), typeof(Orc.Core.HandlerDslView) },
            compile.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Orc.Core.HandlerDslCompileResult), compile.ReturnType);

        // 视图记录本身也必须留在内核程序集里（纯数据，零依赖）。
        Assert.Same(kernel, typeof(Orc.Core.HandlerDslView).Assembly);
        Assert.Same(kernel, typeof(Orc.Core.HandlerDslViewField).Assembly);
        Assert.All(
            typeof(Orc.Core.HandlerDslViewField).GetProperties(),
            static p => Assert.True(
                p.PropertyType == typeof(string) || p.PropertyType == typeof(bool),
                $"HandlerDslViewField.{p.Name} 不是纯数据字段：{p.PropertyType}"));
    }

    private static IReadOnlyList<string> ReadPinTypeEnum(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("$defs").GetProperty("pinTypeName").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
    }
}
