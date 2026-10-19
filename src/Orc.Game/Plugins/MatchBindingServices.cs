using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Triggers;

namespace Orc.Game.Plugins;

/// <summary>
/// 对局绑定面实现（游戏层内部件；P11 甲·postCommand 相）：把原 <c>Match.Initialize</c> 的内置装配步骤
/// （流程触发器登记／词条效果库装配）交由插件声明执行——S15 起流程触发器登记改为「按动作目录装配」。
/// </summary>
internal sealed class MatchBindingServices : IMatchBindingServices
{
    private readonly Func<Commanding.CommandManager?> _commandManager;
    private readonly TriggerRegistry _triggerRegistry;
    private readonly LogicEngine _engine;

    internal MatchBindingServices(
        Func<Commanding.CommandManager?> commandManager,
        TriggerRegistry triggerRegistry,
        LogicEngine engine)
    {
        _commandManager = commandManager;
        _triggerRegistry = triggerRegistry;
        _engine = engine;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">CommandManager 未就绪（装配性错误）。</exception>
    public void RegisterFlowTriggers(TriggerLayer layer)
    {
        var commands = _commandManager()
            ?? throw new InvalidOperationException("CommandManager 未就绪（流程触发器登记须在 postCommand 相执行）。");

        // S15：由「登记 4 个已存在触发器」升级为「按动作目录装配」——八个动作的两端触发器全部登记；
        // 目录本体不外泄（本方法只做登记，不返回也不暴露目录）。
        commands.Actions.RegisterFlowTriggers(_triggerRegistry, layer);
    }

    /// <inheritdoc />
    public void AssembleKeywordEffectLibrary() => KeywordEffectAssembly.Assemble(_engine);
}
