using Orc.Cards;
using Orc.Game.EffectParsing;
using Orc.Game.EffectParsing.Dsl;
using Orc.Game.EffectParsing.Parsing;
using Orc.Game.EffectParsing.Templates;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 效果离线编译驱动（批 5·N2a）测试辅助：共享工具（自含目录、DSL 文本样例、快照结构等值断言）。
/// 测试自含：临时目录/内嵌数据、不依赖 outputs、测后清理（沿用批 4 先例）。
/// </summary>
internal static class EffectCompilationDriverTestKit
{
    /// <summary>创建驱动（默认资产——程序集输出目录约定）。</summary>
    internal static EffectCompilationDriver CreateDriver() => new();

    /// <summary>创建自含临时目录（GUID 隔离；调用方负责清理）。</summary>
    internal static string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>创建自含资产目录（复制默认资产：模板＋ops；测后由调用方清理）。</summary>
    internal static string CreateTempAssetDirectory()
    {
        var directory = CreateTempDirectory("orc-driver-assets");
        CopyDirectory(EffectTemplateLoader.DefaultDirectory, directory);
        return directory;
    }

    /// <summary>递归复制目录。</summary>
    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>文本 → 解析 → 单效果 DSL JSON（测试样例构造；先例：批 4 测试）。</summary>
    internal static string DslJsonOfText(string text)
    {
        var parser = EffectParser.CreateDefault(out var lexFailures);
        Assert.Empty(lexFailures);
        var parse = parser.Parse(text);
        Assert.Empty(parse.Unresolved);
        return DslJson.Serialize(Assert.Single(parse.Effects));
    }

    /// <summary>写入输入文件（返回文件名）。</summary>
    internal static string WriteInputFile(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return fileName;
    }

    /// <summary>快照结构等值断言（对象级逐字段——读回快照 ↔ 内存快照/预期快照）。</summary>
    internal static void AssertSnapshotStructureEqual(EffectSnapshot expected, EffectSnapshot actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        AssertPrefabEqual(expected.Root, actual.Root);
    }

    private static void AssertPrefabEqual(EffectPrefab expected, EffectPrefab actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Version, actual.Version);
        AssertTriggerEqual(expected.MainTrigger, actual.MainTrigger);

        Assert.Equal(expected.OtherTriggers.Count, actual.OtherTriggers.Count);
        for (var i = 0; i < expected.OtherTriggers.Count; i++)
        {
            AssertTriggerEqual(expected.OtherTriggers[i], actual.OtherTriggers[i]);
        }

        Assert.Equal(expected.Wrappers.Count, actual.Wrappers.Count);
        for (var i = 0; i < expected.Wrappers.Count; i++)
        {
            Assert.Equal(expected.Wrappers[i].TargetEventId, actual.Wrappers[i].TargetEventId);
            AssertEventEqual(expected.Wrappers[i].Handler, actual.Wrappers[i].Handler);
        }

        Assert.Equal(expected.Injects.Count, actual.Injects.Count);
        for (var i = 0; i < expected.Injects.Count; i++)
        {
            Assert.Equal(expected.Injects[i].TargetTriggerName, actual.Injects[i].TargetTriggerName);
            Assert.Equal(expected.Injects[i].BandName, actual.Injects[i].BandName);
            Assert.Equal(expected.Injects[i].EventId, actual.Injects[i].EventId);
            Assert.Equal(expected.Injects[i].Priority, actual.Injects[i].Priority);
        }
    }

    private static void AssertTriggerEqual(TriggerPrefab expected, TriggerPrefab actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.StableKey, actual.StableKey);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.ViewTypeName, actual.ViewTypeName);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Hooks, actual.Hooks);

        Assert.Equal(expected.Events.Count, actual.Events.Count);
        for (var i = 0; i < expected.Events.Count; i++)
        {
            AssertEventEqual(expected.Events[i], actual.Events[i]);
        }
    }

    private static void AssertEventEqual(EventPrefab expected, EventPrefab actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.EntryName, actual.EntryName);
        Assert.Equal(expected.CsxSource, actual.CsxSource);
        Assert.Equal(expected.AssemblyKey, actual.AssemblyKey);
        Assert.Equal(expected.Downstream, actual.Downstream);
        Assert.Equal(expected.Version, actual.Version);
    }
}
