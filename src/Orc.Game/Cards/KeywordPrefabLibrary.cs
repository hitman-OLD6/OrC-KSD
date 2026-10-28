namespace Orc.Game.Cards;

/// <summary>
/// 词条效果库目录约定（**旧制品形态已退役**，只保留目录事实）。
/// <para>
/// 旧形态＝效果快照 JSON（<c>Cards/KeywordPrefabs/*.prefab.json</c>，数据壳 ＋ <c>assemblyKey</c>／csx），
/// 由 <c>SnapshotStore.LoadDirectory</c> 装载——该装载链已随 <c>Effect</c> 家族删除。
/// 现行承载＝同目录下的 <c>*.component.json</c>（v4 组件资产，见 <see cref="KeywordComponentLibrary"/>）。
/// </para>
/// </summary>
public static class KeywordPrefabLibrary
{
    /// <summary>库目录（相对程序集输出目录——沿 <c>EffectTemplateLoader.DefaultDirectory</c> 先例）。</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Cards", "KeywordPrefabs");

    /// <summary>旧格式制品文件名模式（只用于识别/清理遗留文件；不再被装载）。</summary>
    public const string LegacyArtifactPattern = "*.prefab.json";
}
