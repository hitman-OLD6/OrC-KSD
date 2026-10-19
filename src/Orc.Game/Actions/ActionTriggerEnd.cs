using Orc.Game.Triggers;

namespace Orc.Game.Actions;

/// <summary>
/// 动作的一端触发器（S15）：角色标签 + 触发器本体 + 登记委托（登记到 <see cref="TriggerRegistry"/>）。
/// <para>
/// 委托在装配期以强类型闭包捕获触发器，避免运行期反射。公开面供**可替换实现**（插件程序集）暴露自己的两端，
/// 宿主据此把插件动作装配进对局注册表（R17）。
/// </para>
/// </summary>
public sealed class ActionTriggerEnd
{
    /// <summary>处理端角色标签（每动作恰一个）。</summary>
    public const string HandleRole = "处理端";

    /// <summary>创建一端触发器。</summary>
    /// <param name="role">角色（触发端／处理端；多入口动作带入口名，如「部署触发端」）。</param>
    /// <param name="trigger">触发器本体（<c>Trigger&lt;TView&gt;</c>；类型擦除以跨程序集传递）。</param>
    /// <param name="register">登记到对局触发注册表（给定分层）。</param>
    /// <exception cref="ArgumentNullException">trigger / register 为 null。</exception>
    public ActionTriggerEnd(string role, object trigger, Action<TriggerRegistry, TriggerLayer> register)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(register);
        Role = role;
        Trigger = trigger;
        Register = register;
    }

    /// <summary>角色（触发端／处理端；多入口动作带入口名，如「部署触发端」）。</summary>
    public string Role { get; }

    /// <summary>触发器本体（<c>Trigger&lt;TView&gt;</c>）。</summary>
    public object Trigger { get; }

    /// <summary>登记到对局触发注册表（给定分层）。</summary>
    public Action<TriggerRegistry, TriggerLayer> Register { get; }
}
