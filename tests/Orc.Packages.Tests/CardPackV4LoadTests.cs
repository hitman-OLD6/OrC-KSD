using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Model;
using Orc.Packages;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// U5-a 验收（生产装载切 effects-v4）：
/// <list type="bullet">
/// <item>包内提供 <c>effects-v4/</c> ⇒ 组件资产装进对局编排运行时；</item>
/// <item>此时 <c>effects/</c>（旧格式）**不进入装载路径**（放坏文件也不影响装载＝证据）；</item>
/// <item>v4 资产坏 ⇒ 必需包整包失败、运行时零注册（原子性不退化）。</item>
/// </list>
/// </summary>
public class CardPackV4LoadTests
{
    private const string ComponentId = "v4.noop";
    private const string OldPrefabId = "old.noop";

    private static PackageManifest Manifest(string id, bool required = true)
        => PackageManifest.Parse($$"""
            {
              "schemaVersion": 2,
              "id": "{{id}}",
              "guid": "{{Guid.NewGuid()}}",
              "kind": "cardpack",
              "name": "v4 测试包",
              "version": "1.0.0",
              "required": {{(required ? "true" : "false")}},
              "assets": []
            }
            """);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orc-v4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ComponentJson(string id)
        => $$"""
            {
              "schemaVersion": 4,
              "asset": {
                "kind": "component",
                "id": "{{id}}",
                "requires-host": "unit",
                "active-when": "loaded",
                "one-shot": false,
                "private-fields": [],
                "members": [
                  {
                    "name": "onLoad",
                    "visibility": [ "callable", "mountable" ],
                    "view": { "fields": [] }
                  }
                ],
                "mounts": [ { "target": "engine#card.load", "member": "onLoad" } ],
                "wraps": []
              }
            }
            """;

    private static CardPackContent Content(string root, string? effects, string? components)
        => new(
            Manifest("pack.v4"),
            root,
            Array.Empty<CardDefinitionEntry>(),
            Array.Empty<EffectSnapshot>(),
            Array.Empty<(string CardId, IReadOnlyList<string> PrefabIds)>(),
            effects,
            Array.Empty<CardPackKeyword>(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            components);

    [Fact]
    public void 有v4目录_组件装进编排运行时_旧格式目录不进装载路径()
    {
        var root = TempDir();
        var effects = Path.Combine(root, "effects");
        var components = Path.Combine(root, "effects-v4");
        Directory.CreateDirectory(effects);
        Directory.CreateDirectory(components);
        File.WriteAllText(Path.Combine(components, ComponentId + ".component.json"), ComponentJson(ComponentId));
        // 旧格式目录放**坏文件**：若仍被读取，必需包会失败；装载成功即证明该目录未进入路径。
        File.WriteAllText(Path.Combine(effects, OldPrefabId + ".prefab.json"), "{ 这不是 JSON");

        try
        {
            var engine = new LogicEngine { Model = GameEnginePrefabs.CreateRuntime() };
            CardPackAssembler.Attach(
                new CardEffectRegistry(), engine, new[] { Content(root, effects, components) });

            // v4 资产在编排运行时目录里，且能解析成组件预制体。
            Assert.True(engine.Model!.Catalog.Contains(ComponentId));
            Assert.Equal(EntityKind.Component, engine.Model.Catalog.Resolve(ComponentId).Kind);
            Assert.Empty(engine.Prefabs.PrefabIds);
            Assert.Equal(PackLoadStatus.Loaded, Assert.Single(engine.LoadGate.Records).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void v4资产坏_必需包整包失败_运行时零注册()
    {
        var root = TempDir();
        var effects = Path.Combine(root, "effects");
        var components = Path.Combine(root, "effects-v4");
        Directory.CreateDirectory(effects);
        Directory.CreateDirectory(components);
        File.WriteAllText(Path.Combine(components, "broken.component.json"), "{ 这不是 JSON");

        try
        {
            var engine = new LogicEngine { Model = GameEnginePrefabs.CreateRuntime() };
            var error = Assert.Throws<InvalidDataException>(() => CardPackAssembler.Attach(
                new CardEffectRegistry(), engine, new[] { Content(root, effects, components) }));

            Assert.Contains("必需卡包", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PackLoadStatus.Failed, Assert.Single(engine.LoadGate.RequiredFailures).Status);
            Assert.DoesNotContain(engine.Model!.Catalog.Definitions, d => d.Id.Contains("broken", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 包扫描_识别v4组件目录()
    {
        var root = TempDir();
        var packRoot = Path.Combine(root, "pack");
        Directory.CreateDirectory(Path.Combine(packRoot, "cards"));
        Directory.CreateDirectory(Path.Combine(packRoot, "effects"));
        Directory.CreateDirectory(Path.Combine(packRoot, "effects-v4"));

        try
        {
            var scan = PackageScanner.Scan(packRoot, Manifest("pack.scan"));

            Assert.NotNull(scan.EffectsDirectory);
            Assert.NotNull(scan.ComponentsDirectory);
            Assert.EndsWith(PackageScanner.ComponentsDirectoryName, scan.ComponentsDirectory, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
