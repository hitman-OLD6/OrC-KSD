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
    private readonly LogicEngine _engine;

    internal MatchBindingServices(Func<Commanding.CommandManager?> commandManager, LogicEngine engine)
    {
        _commandManager = commandManager;
        _engine = engine;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">CommandManager 未就绪（装配性错误）。</exception>
    public void RegisterFlowTriggers()
    {
        _ = _commandManager()
            ?? throw new InvalidOperationException("CommandManager 未就绪（流程装配须在 postCommand 相执行）。");

        // S15 的"按目录登记八个动作的两端"在批次 7（§27）退场：端＝引擎成员，一条边接入即进账，
        // 不需要第二本登记簿（对局级登记簿已随旧触发器类型删除）。
    }

    /// <inheritdoc />
    public void AssembleKeywordEffectLibrary() => KeywordEffectAssembly.Assemble(_engine);
}
