using Orc.Game.Cards;
using Orc.Packages;

namespace Orc.UI;

/// <summary>
/// 卡包词条加载器（S6·D3/D5）：观察卡包生命周期——`load`/`reload` 成功时读该包 <c>keywords.json</c>
/// 并注册进词条注册面（覆盖语义）；`unload` **不注销**（D5：运行期注销需冷重启）。
/// 内建/他包词条重名＝拒绝（不得劫持既有词汇）。
/// </summary>
public sealed class CardPackKeywordLoader : IPackageLifecycleObserver
{
    private static readonly Dictionary<string, string> KeywordOwner = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void OnPackageChanged(PackageLifecycleChange change, LoadedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (change == PackageLifecycleChange.Unloaded)
        {
            return; // D5：不允许运行期注销（注销须冷重启）
        }

        if (package.Manifest.Kind != PackageKind.CardPack)
        {
            return; // 插件无卡包词条
        }

        foreach (var keyword in PackageScanner.ReadKeywords(package.RootDirectory))
        {
            Register(keyword, package.Manifest.Id);
        }
    }

    private static void Register(CardPackKeyword keyword, string packId)
    {
        if (KeywordOwner.TryGetValue(keyword.Id, out var owner)
            && !string.Equals(owner, packId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"词条 '{keyword.Id}' 已被卡包 '{owner}' 注册");
        }

        if (!KeywordOwner.ContainsKey(keyword.Id) && KeywordRegistry.IsDefined(keyword.Id))
        {
            throw new InvalidDataException($"词条 '{keyword.Id}' 与引擎内建/既有词条重名（拒绝覆盖）");
        }

        var id = keyword.Id;
        KeywordRegistry.RegisterOrReplace(
            id, (_, _) => new PlainKeywordComponent(id), keyword.IsBattleKeyword);
        KeywordOwner[id] = packId;
        KeywordRegistry.DeclareEffectBindings(id, keyword.Bindings);
    }
}
