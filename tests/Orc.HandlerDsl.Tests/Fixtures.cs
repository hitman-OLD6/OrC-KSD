using System.Text.Json;
using Orc.HandlerDsl;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>测试夹具：目录快照、视图声明、契约包与向量的定位。</summary>
internal static class Fixtures
{
    /// <summary>仓库根（以 <c>OrcEngine.sln</c> 为锚；产物落在 <c>artifacts/slot</c>，向上必能命中）。</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>契约包根（<c>contracts/handler-dsl/v1</c>）。</summary>
    public static string ContractRoot { get; } = Path.Combine(RepoRoot, "contracts", "handler-dsl", "v1");

    /// <summary>视图声明（模拟挂载点目标成员的 <c>ViewSpec</c>）。</summary>
    public static IReadOnlyDictionary<string, PinType> View { get; } = new Dictionary<string, PinType>(StringComparer.Ordinal)
    {
        ["Target"] = PinType.Entity,
        ["Owner"] = PinType.Entity,
        ["Defense"] = PinType.Number,
        ["Name"] = PinType.String,
        ["Units"] = PinType.List(PinType.Entity),
    };

    /// <summary>测试用目录（catalogRevision = 7）。</summary>
    public static DslCatalog Catalog { get; } = new(
        7,
        new[] { Damage(), Draw(), Fetch(), Announce(), Kill() },
        new Dictionary<string, PinType>(StringComparer.Ordinal)
        {
            ["attack"] = PinType.Number,
            ["defense"] = PinType.Number,
            ["kredits"] = PinType.Number,
            ["name"] = PinType.String,
        });

    /// <summary>damage(target: entity{unit,hq}, amount: number) → 无输出。</summary>
    public static DslNodeSignature Damage() => new(
        "damage",
        new[]
        {
            new DslPin("target", PinType.Entity.WithSubtypes("unit", "hq")),
            new DslPin("amount", PinType.Number),
        },
        Array.Empty<DslPin>(),
        hasAwait: true,
        hasSideEffect: true,
        face: "EffectRuntime",
        method: "DamageAsync");

    /// <summary>draw(count?: number) → drawn: number（单输出）。</summary>
    public static DslNodeSignature Draw() => new(
        "draw",
        new[] { new DslPin("count", PinType.Number, required: false) },
        new[] { new DslPin("drawn", PinType.Number) },
        hasAwait: true,
        hasSideEffect: true,
        face: "EffectRuntime",
        method: "DrawAsync");

    /// <summary>fetch(from?: string{deck,hand}) → status: string ＋ card: entity（多输出）。</summary>
    public static DslNodeSignature Fetch() => new(
        "fetch",
        new[] { new DslPin("from", PinType.String, required: false, enumValues: new[] { "deck", "hand" }) },
        new[] { new DslPin("status", PinType.String), new DslPin("card", PinType.Entity) },
        hasAwait: true,
        hasSideEffect: false,
        face: "EffectRuntime",
        method: "FetchAsync");

    /// <summary>announce(text: string) → 无输出（string 引脚用于「无隐式转换」用例）。</summary>
    public static DslNodeSignature Announce() => new(
        "announce",
        new[] { new DslPin("text", PinType.String) },
        Array.Empty<DslPin>(),
        hasAwait: false,
        hasSideEffect: true,
        face: "EffectRuntime",
        method: "Announce");

    /// <summary>kill(target: entity, source?: entity) → 无输出（用于 source 折叠用例）。</summary>
    public static DslNodeSignature Kill() => new(
        "kill",
        new[]
        {
            new DslPin("target", PinType.Entity),
            new DslPin("source", PinType.Entity, required: false),
        },
        Array.Empty<DslPin>(),
        hasAwait: true,
        hasSideEffect: true,
        face: "EffectRuntime",
        method: "KillAsync");

    /// <summary>拼一份文档（只给 actions 数组的 JSON 片段）。</summary>
    public static string Doc(string actionsJson) => $"{{\"schemaVersion\":1,\"actions\":[{actionsJson}]}}";

    /// <summary>读文档（默认用测试目录）。</summary>
    public static HandlerDslReadResult Read(string json, IHandlerDslCatalog? catalog = null) =>
        HandlerDslReader.Read(json, catalog ?? Catalog);

    /// <summary>读＋类型校验（默认用测试目录与视图声明）；读取期已有错误＝不再叠加类型层噪声。</summary>
    public static IReadOnlyList<Orc.Core.HandlerDslError> Check(string json)
    {
        var read = Read(json);
        if (read.Errors.Count > 0 || read.Document is null)
        {
            return read.Errors;
        }

        return HandlerDslTypeChecker.Check(read.Document, Catalog, View);
    }

    /// <summary>断言「恰好一条错误，且码与路径都符合预期」。</summary>
    public static Orc.Core.HandlerDslError Single(IReadOnlyList<Orc.Core.HandlerDslError> errors, string code, string path)
    {
        var error = Assert.Single(errors);
        Assert.Equal(code, error.Code);
        Assert.Equal(path, error.NodePath);
        return error;
    }

    /// <summary>读完一条 check 向量。</summary>
    public sealed record CheckVector(
        string File,
        string Name,
        string DocumentJson,
        DslCatalog Catalog,
        DslView View,
        string Code,
        string NodePath);

    /// <summary>载入契约包里的全部 check 向量（按文件名序）。</summary>
    public static IReadOnlyList<CheckVector> LoadCheckVectors()
    {
        var dir = Path.Combine(ContractRoot, "vectors", "check");
        Assert.True(Directory.Exists(dir), $"check 向量目录不存在：{dir}");

        var files = Directory.GetFiles(dir, "*.check.json").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);

        var vectors = new List<CheckVector>(files.Length);
        foreach (var file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var root = json.RootElement;
            Assert.Equal("check", root.GetProperty("kind").GetString());

            var expect = root.GetProperty("expect");
            vectors.Add(new CheckVector(
                Path.GetFileName(file),
                root.GetProperty("name").GetString()!,
                root.GetProperty("document").GetRawText(),
                BuildCatalog(root.GetProperty("catalog")),
                ViewFromSnapshot(root.GetProperty("view")),
                expect.GetProperty("code").GetString()!,
                expect.GetProperty("nodePath").GetString()!));
        }

        return vectors;
    }

    /// <summary>从向量快照构造目录（供 gen 向量与 S9 复用）。</summary>
    public static DslCatalog CatalogFromSnapshot(JsonElement element) => BuildCatalog(element);

    /// <summary>从向量快照构造视图声明（含可写性）。</summary>
    public static DslView ViewFromSnapshot(JsonElement element)
    {
        var fields = new List<DslViewField>();
        foreach (var property in element.GetProperty("fields").EnumerateObject())
        {
            var type = PinType.Parse(property.Value.GetProperty("type").GetString()!);
            var writable = property.Value.TryGetProperty("writable", out var writableElement)
                           && writableElement.GetBoolean();
            fields.Add(new DslViewField(property.Name, type, writable));
        }

        return new DslView(fields);
    }

    /// <summary>
    /// 目录快照 → <see cref="DslCatalog"/>：**直接走契约包自己的解析器**。
    /// <para>
    /// 早先此处是手写的第二套解析，只读了 face／method／hasAwait／hasSideEffect，
    /// 于是 <c>implicitHost</c>／<c>static</c>／<c>faceType</c>／<c>tokenParameter</c> 全部静默丢失——
    /// 向量里的目录与真实扫描结果对不上，生成文本也就跟着撒谎（漏传 <c>ct</c> 就是这么一直没被发现的）。
    /// 只有一份解析实现，才不会再次漂移。
    /// </para>
    /// </summary>
    private static DslCatalog BuildCatalog(JsonElement element) =>
        HandlerDslCatalogJson.Deserialize(element.GetRawText());

    private static IReadOnlyList<DslPin> BuildPins(JsonElement nodeElement, string field)
    {
        var pins = new List<DslPin>();
        if (!nodeElement.TryGetProperty(field, out var array))
        {
            return pins;
        }

        foreach (var pinElement in array.EnumerateArray())
        {
            var type = PinType.Parse(pinElement.GetProperty("type").GetString()!);
            if (pinElement.TryGetProperty("subtypes", out var subtypes))
            {
                type = type.IsList
                    ? type.WithElementSubtypes(subtypes.EnumerateArray().Select(s => s.GetString()!).ToArray())
                    : type.WithSubtypes(subtypes.EnumerateArray().Select(s => s.GetString()!).ToArray());
            }

            var enumValues = pinElement.TryGetProperty("enum", out var enumElement)
                ? enumElement.EnumerateArray().Select(s => s.GetString()!).ToArray()
                : null;

            pins.Add(new DslPin(
                pinElement.GetProperty("name").GetString()!,
                type,
                !pinElement.TryGetProperty("required", out var required) || required.GetBoolean(),
                enumValues));
        }

        return pins;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OrcEngine.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根（未命中 OrcEngine.sln）。");
    }
}
