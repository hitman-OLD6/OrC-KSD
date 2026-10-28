using Orc.Core;
using Orc.Core.Model;

namespace Orc.Game.Cards;

/// <summary>
/// 内建词条效果组件库（v4；A2 决策门落地）：生产内建词条的**行为制品**——<c>Cards/KeywordPrefabs/*.component.json</c>
/// （组件资产：成员 csx ＋ 挂载/包装声明），随程序集输出（<c>Orc.Game.csproj</c> 既有复制规则）。
/// <para>
/// 与旧「数据壳＋行为引用」库（<see cref="KeywordPrefabLibrary"/>，<c>*.prefab.json</c>）同目录、不同文件模式：
/// 本库产物是**编排运行时的组件**（<c>engine.Model.Catalog</c>），不是 <c>Effect</c> 预制体。
/// </para>
/// <para>
/// 装载时点＝对局构造期（<c>Match</c> 构造器，在 <c>Match.Initialize</c> 的装配段之前）：
/// 词条效果绑定的核验（<see cref="KeywordEffectAssembly.VerifyBindings"/>）要求绑定 id 在目录里可解析，
/// 授予链的 v4 首选路径（<c>KeywordManager.TryAttachComponent</c>）也以目录为判据。
/// </para>
/// <para>
/// **失败语义**：内建资产是随程序集分发的配置——装载失败＝装配性错误，fail-fast 上抛
/// （不给"少了某个词条的卡"的静默半包）。单文件解析/csx 编译失败由 <see cref="AssetPackLoader"/> 汇总后一并上抛。
/// </para>
/// </summary>
public static class KeywordComponentLibrary
{
    /// <summary>库标识（装载报告/审查用）。</summary>
    public const string PackId = "builtin.keyword-components";

    /// <summary>默认库目录（相对程序集输出目录；与旧数据壳库同目录）。</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "Cards", "KeywordPrefabs");

    /// <summary>
    /// 把内建词条效果组件装进对局编排运行时（原子：全部成功才提交）。
    /// </summary>
    /// <param name="runtime">目标编排运行时（正式面；失败时不被触碰）。</param>
    /// <param name="evaluator">异步脚本求值器（组件成员 csx 的装载期预编译）。</param>
    /// <param name="directory">库目录（缺省＝<see cref="DefaultDirectory"/>）。</param>
    /// <returns>装载结果（已注册资产 id 与文件数）。</returns>
    /// <exception cref="ArgumentNullException">runtime 为 null。</exception>
    /// <exception cref="InvalidOperationException">库装载失败（内建资产损坏——装配性错误）。</exception>
    public static AssetPackLoadResult LoadInto(
        OrchestrationRuntime runtime, IScriptEvaluator? evaluator, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var result = AssetPackLoader.Load(
            runtime, directory ?? DefaultDirectory, evaluator, judgeEvaluator: null, packId: PackId);

        if (!result.Loaded)
        {
            throw new InvalidOperationException(
                "内建词条效果组件库装载失败（配置错误——对局不开局）："
                + string.Join("；", result.Errors.Select(static e => $"{e.Target}: {e.Message}"))
                + "。");
        }

        return result;
    }
}
