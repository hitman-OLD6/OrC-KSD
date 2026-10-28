using Orc.Core.Model;

namespace Orc.Game.Actions;

/// <summary>
/// 动作的一端（S15／U2／§27）：**角色标签 ＋ 成员地址**。
/// <para>
/// 端在新模型里就是引擎实体上的一个成员（<c>engine#hand.add</c>…）：地址是登记、审查与调用的唯一身份。
/// 旧实现同时携带"触发器实例"与"登记委托"（把实例记进对局级登记簿），
/// 批次 7 一并删除——端接入成员边即进账，不需要第二本账。
/// </para>
/// <para>
/// 公开面供**可替换实现**（插件程序集）暴露自己的两端，宿主据此把插件动作装配进对局（R17）。
/// </para>
/// </summary>
public sealed class ActionTriggerEnd
{
    /// <summary>处理端角色标签（每动作恰一个）。</summary>
    public const string HandleRole = "处理端";

    /// <summary>创建一端。</summary>
    /// <param name="role">角色（触发端／处理端；多入口动作带入口名，如「部署触发端」）。</param>
    /// <param name="member">成员地址（端在引擎实体上的位置）。</param>
    /// <exception cref="ArgumentNullException">role 为空。</exception>
    public ActionTriggerEnd(string role, MemberAddress member)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        Role = role;
        Member = member;
    }

    /// <summary>角色（触发端／处理端；多入口动作带入口名，如「部署触发端」）。</summary>
    public string Role { get; }

    /// <summary>成员地址（<c>作用域#成员名</c>）。</summary>
    public MemberAddress Member { get; }
}
