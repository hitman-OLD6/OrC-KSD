using Orc.Core;
using Orc.Core.Model;

namespace Orc.Game.Save;

/// <summary>
/// **内置完整复制存档导出器**（本会话 S3／裁决 4／D50）：默认实现 B——
/// 导出 <c>{ extends: 源预制体, slots: 当前参数, privateFields: 私有字段当前值, attachments: 一层动态挂载 }</c>。
/// <para>
/// 服务 id＝<see cref="SavePolicy.FullCopyServiceId"/>（<c>framework.save.full-copy</c>，沿用保留命名域）。
/// **不做自动兜底**（D50）：<c>save</c> 字段写的是服务 id，服务未注册 ⇒ 存档失败、不退回默认——
/// 「插件包没加载」不得变成静默降级。归属 <c>Orc.Game</c>（已知限制 C2）：纯内核构造路径没有内置 B，
/// 调用方自行注册。无状态：全部状态读取经 <see cref="SaveContext"/>（只读），不持跨调用可变字段。
/// </para>
/// <para>
/// 实现判据（D55，测试绑死）：私有字段读 <see cref="OrchestrationEntity.GetPrivateField"/>（<c>_state</c> 当前值）——
/// **不读 <see cref="PrefabType.Slots"/> 声明初值**；空位当前参数读 <see cref="SaveContext.ReadSlots"/>。
/// 附件（S4／裁决 2）：逐个 <see cref="SaveContext.ExportAttachment"/>（**嵌入**、数组顺序即身份与份数；
/// 任一附件不可存档 ⇒ 宿主存档失败）；深度恒为 1。
/// </para>
/// </summary>
public sealed class FullCopySaveExporter : SaveExporter
{
    /// <inheritdoc />
    protected override SaveModing? ExportLogic(OrchestrationEntity self, SaveContext context)
    {
        // 溯源唯一入口（D51／D52）：缓存键与产物资产 id 绝不进 extends。
        var extends = SaveContext.ExtendsOf(self);
        return new SaveModing(
            extends,
            context.ReadSlots(self.Type),
            context.ReadPrivateFields(self),
            context.Runtime.ComponentsOf(self)
                .Select(attachment => context.ExportAttachment(attachment.Component))
                .ToArray());
    }
}
