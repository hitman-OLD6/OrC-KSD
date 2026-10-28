using Orc.Core;
using Orc.Core.Model;

namespace Orc.Game.Model;

/// <summary>
/// 游戏层引擎预制体 <c>engine.game</c>（S3 第 7 条／S7）：<c>extends: engine</c>，
/// 在内核 4 个信号之后追加游戏层 35 个信号与 9 个引擎流程成员——同一 <c>engine</c> 根下**跨层合并**解析。
/// <para>
/// 本批（批次 2）的接线边界：引擎成员**只声明、不接线**（无自有边）。生产执行仍走旧总线，
/// 因此不存在同一次发射被两条路径各执行一次的风险；接线下沉到 S8（批次 3）与 S11（批次 4）。
/// </para>
/// </summary>
public static class GameEnginePrefabs
{
    /// <summary>游戏层引擎预制体 id（`engine` 的叶子扩展）。</summary>
    public const string PrefabId = "engine.game";

    /// <summary>游戏层引擎预制体（内核信号继承自 `engine`，本层追加 35 信号＋9 流程成员）。</summary>
    public static PrefabDefinition Create()
        => new(
            PrefabId,
            EntityKind.Engine,
            extends: KernelSignals.PrefabId,
            layer: PrefabLayer.Game,
            members: GameSignals.Members.Concat(EngineProcesses.Members).ToArray());

    /// <summary>创建完整预制体目录（内核基础引擎＋卡牌类型预制体＋游戏层引擎）。</summary>
    public static PrefabCatalog CreateCatalog()
        => new PrefabCatalog()
            .Add(KernelSignals.CreateEnginePrefab())
            .Add(SignalEndpointPrefab.Create())
            .AddRange(CardProcesses.Definitions)
            .Add(Create());

    /// <summary>创建对局级编排运行时（引擎实体＝单例；handler 资产表为空——本批无脚本成员）。</summary>
    public static OrchestrationRuntime CreateRuntime()
        => new(CreateCatalog(), new HandlerRegistry(), PrefabId);
}
