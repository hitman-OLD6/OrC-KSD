using System.Dynamic;

namespace Orc.Core;

/// <summary>
/// 实体基类：具名、可终结、可安全引用。卡牌、单位、玩家等后续均继承此类（保持可继承）。
/// </summary>
/// <remarks>
/// 继承 <see cref="DynamicObject"/>（编排实体化批次 1 的**加性**修改）：新模型的组件实体需要让 csx 经
/// <c>owner.&lt;Name&gt;</c> 动态读写私有字段（R3e），而动态成员访问要求运行期类型可参与 DLR 绑定。
/// 对既有代码无行为影响——静态解析优先于动态回退，仅当成员名在 CLR 上不存在时才走
/// <see cref="DynamicObject.TryGetMember"/>／<see cref="DynamicObject.TrySetMember"/>。
/// </remarks>
public class Entity : DynamicObject
{
    /// <summary>实体标识符（S5 加性）：创建时分配的全局唯一标识；稳定不变（销毁后仍可读），供外部关联/消费（快照 JSON 的 entityId 来源）。</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>实体名（非 null）。</summary>
    public string Name { get; }

    /// <summary>生命周期令牌。</summary>
    public Lifetime Life { get; } = new();

    /// <summary>指向自身的引用（公开只读，类型为基类视角的 <see cref="Ref{T}"/>）。</summary>
    public Ref<Entity> Ref { get; }

    public Entity(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Ref = new Ref<Entity>(this, Life, name);
    }

    /// <summary>销毁实体（幂等）：置生命周期为失效。</summary>
    /// <remarks>
    /// 只杀不管卸载：Destroy 后 Name / Life / Ref 本身的访问仍允许（观察点为 Life.IsAlive / Ref.IsAlive 为 false、Ref.Value 抛 Stale）。
    /// Destroy 本身不触发总线卸载（bus.UnmountOwner 为显式 API；实际调用编排留 S4）。
    /// </remarks>
    public virtual void Destroy() => Life.Kill();
}
