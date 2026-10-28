using Orc.Cards;

namespace Orc.Game.Cards;

/// <summary>
/// 声明式词条行为：内容（效果快照）在**授予时**转成 v4 组件并装配到宿主卡的编排实体上，
/// 句柄交给词条组件持有（随词条卸载/死亡注销/授予回滚逐句柄撤销）。
/// <para>
/// 旧形态用 <c>DynamicEffectFactory</c> 实例化 <c>Effect</c> 并走内嵌效果通道——该通道已随 <c>Effect</c> 家族删除。
/// 现在与内建词条效果走**同一条 v4 装配路径**（<c>LegacySnapshotComponentBinder</c>）。
/// </para>
/// </summary>
public sealed class DeclarativeKeywordComponent : KeywordComponent
{
    private DeclarativeKeywordComponent(string id, Card card, int? value, IReadOnlyList<EffectSnapshot> snapshots)
        : base(id, value)
    {
        var runtime = card.Engine.Model
            ?? throw new InvalidDataException("Keyword behavior requires a match with an orchestration runtime.");
        if (card is not CardBase { ModelEntity: { } entity })
        {
            throw new InvalidDataException("Keyword behavior requires the host card to have an orchestration entity.");
        }

        foreach (var snapshot in snapshots)
        {
            string assetId;
            try
            {
                assetId = LegacySnapshotComponentBinder.Register(
                    runtime, snapshot, card.Engine.ScriptEvaluator, card.Engine.SyncScriptEvaluator);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Keyword behavior could not be converted to a v4 component: " + ex.Message, ex);
            }

            try
            {
                AttachComponent(runtime.AddComponent(entity, assetId, $"{assetId}@{entity.Name}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException("Keyword behavior component could not be mounted: " + ex.Message, ex);
            }
        }
    }

    /// <summary>Use content-versioned ids so edits cannot change keywords in an existing match.</summary>
    public static void Register(string id, IReadOnlyList<EffectSnapshot> snapshots, bool battle = false)
    {
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("Keyword behavior cannot be empty.", nameof(snapshots));
        }

        var frozen = snapshots.ToArray();
        KeywordRegistry.RegisterOrReplace(id, (card, value) => new DeclarativeKeywordComponent(id, card, value, frozen), battle);
    }
}
