namespace Orc.Game.Cards.Data;

/// <summary>
/// 卡牌数据体（P2／Q7b）：一段元数据 ＋ 游戏层确定的需要加载的组件集（声明序）。
/// 形态＝纯数据镜像：<see cref="Id"/>（卡库注册键）、<see cref="Name"/>（单值名称，官方 <c>title["zh-Hans"]</c>）、
/// <see cref="Components"/>（组件定义集，声明序＝第二段加载序）。
/// 校验：id/name 必填非空白；同类型组件至多一份（重复＝拒绝，对齐容器「每类型恰一份」语义）。
/// </summary>
public sealed class CardDataBody
{
    /// <summary>创建数据体（fail-fast）。</summary>
    /// <param name="id">卡库注册键（非 null/空白）。</param>
    /// <param name="name">单值名称（非 null/空白）。</param>
    /// <param name="components">组件定义集（声明序；可缺省＝空）。</param>
    /// <exception cref="ArgumentException">id/name 为 null/空白；components 含重复组件类型。</exception>
    public CardDataBody(
        string id,
        string name,
        IEnumerable<ICardDataComponentDefinition>? components = null,
        bool deckable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var list = new List<ICardDataComponentDefinition>();
        if (components is not null)
        {
            var seen = new HashSet<Type>();
            foreach (var item in components)
            {
                ArgumentNullException.ThrowIfNull(item);

                if (!seen.Add(item.GetType()))
                {
                    throw new ArgumentException(
                        $"数据体含重复组件类型 '{item.GetType().Name}'（同一组件每卡至多一份——拒绝）。",
                        nameof(components));
                }

                list.Add(item);
            }
        }

        Id = id;
        Name = name;
        Components = list.ToArray();
        Deckable = deckable;
    }

    /// <summary>卡库注册键（＝数据体 <c>id</c>）。</summary>
    public string Id { get; }

    /// <summary>单值名称（＝数据体 <c>name</c>）。</summary>
    public string Name { get; }

    /// <summary>组件定义集（声明序；空＝无组件）。</summary>
    public IReadOnlyList<ICardDataComponentDefinition> Components { get; }

    /// <summary>可构筑标记（R9：数据体根字段；缺省 true——存量卡包零改动）。</summary>
    public bool Deckable { get; }

    /// <summary>按类型取组件定义（未含＝false、不抛错）。</summary>
    public bool TryGetComponent<TDefinition>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TDefinition? definition)
        where TDefinition : class, ICardDataComponentDefinition
    {
        foreach (var item in Components)
        {
            if (item is TDefinition typed)
            {
                definition = typed;
                return true;
            }
        }

        definition = null;
        return false;
    }
}

/// <summary>数据体读取结果（结构化：<see cref="Body"/> 非 null＝成功；<see cref="Warnings"/>＝隔离留痕）。</summary>
/// <param name="Body">读成的数据体（失败＝null）。</param>
/// <param name="Warnings">隔离告警集（未知组件／组件反序列化失败等——该组件跳过、其余照常）。</param>
/// <param name="Error">失败原因（成功＝null）。</param>
public sealed record CardDataReadResult(
    CardDataBody? Body,
    IReadOnlyList<string> Warnings,
    string? Error);
