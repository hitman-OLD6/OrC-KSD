using Orc.Game.Managers;

namespace Orc.Game;

/// <summary>对局级规则/开局配置（创建参数形态：配置对象；含指挥点上限、起手张数与首回合抽牌等开局规则）。</summary>
public sealed class MatchOptions
{
    /// <summary>指挥点上限（默认 12；须为正整数，非法配置于创建期抛参数校验异常）。</summary>
    public int MaxPointSlots { get; init; } = ResourceManager.DefaultMaxPointSlots;

    /// <summary>
    /// 跳过开局换牌（mulligan）相位（缺省 false＝按 A1 序列：<c>Initialize</c> 完成装配与起手装载后置"换牌"、
    /// 先手第 1 回合延后至双方确认）。true＝<c>Initialize</c> 末尾直接执行先手第 1 回合并置"进行"
    /// （既有序列；供测试基建与"无换牌"变体装配）。
    /// </summary>
    public bool SkipMulligan { get; init; }

    // ---------- K4 加性（开局常量配置化：起手张数／首回合抽牌） ----------

    /// <summary>先手起手张数默认值（4）。</summary>
    public const int DefaultOpeningHandSizeFirstPlayer = 4;

    /// <summary>后手起手张数默认值（5）。</summary>
    public const int DefaultOpeningHandSizeSecondPlayer = 5;

    /// <summary>先手起手张数（默认 4；0 合法＝空起手、正常流程；负数于创建期抛参数校验异常）。</summary>
    public int OpeningHandSizeFirstPlayer { get; init; } = DefaultOpeningHandSizeFirstPlayer;

    /// <summary>后手起手张数（默认 5；0 合法＝空起手、正常流程；负数于创建期抛参数校验异常）。</summary>
    public int OpeningHandSizeSecondPlayer { get; init; } = DefaultOpeningHandSizeSecondPlayer;

    /// <summary>
    /// 允许先手全局第 1 回合抽牌（缺省 false＝保持现行"先手第 1 回合不抽"的唯一抽牌例外；
    /// true＝关闭该例外、第 1 回合照抽 1 张）。「后手首回合（全局回合 2）与其余回合照抽」的既有边界不受本开关影响
    /// （开关仅贡献"第 1 回合不抽"这一唯一例外的有无）。
    /// </summary>
    public bool AllowFirstTurnDraw { get; init; }

    // ---------- 沙盒调试原语（授权事实） ----------

    /// <summary>
    /// 沙盒授权（缺省 <c>false</c＝未授权）：由**装配方在构造期决定**。
    /// 未授权时引擎保留触发器族（<see cref="Sandbox.SandboxPrimitives"/>）的全部原语一律明确拒绝、零状态变更；
    /// 授权只在沙盒装配路径开启（<c>Orc.Server --mode sandbox</c> / McpService Authoring / 测试夹具显式声明），
    /// <c>play</c> 路径不开启。
    /// </summary>
    public bool SandboxAuthorized { get; init; }
}
