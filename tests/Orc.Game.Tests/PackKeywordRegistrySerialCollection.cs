using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 样例包词条注册面的并行隔离 collection：本组用例都围绕**进程级全局**注册面
/// <see cref="Orc.Game.Cards.KeywordRegistry"/> 展开——它们把同一批**来自样例包**的词条标识
/// （太空战争：强磁护盾／磁反应装甲／海绵装甲／游击；铜墙铁壁：机动／固守／抢修）注册进全局面并声明效果绑定，
/// 用例结束时注销。注册面契约是"注册即配置、重复声明被拒绝"，因此两个类同时在世时后手必然抛
/// <c>InvalidOperationException：词条标识 '…' 的效果绑定已声明</c>；注销侧的对称清理同样会抽掉对方脚下
/// 的地基（对局装载时词条标识已消失）。
/// <para>
/// 上述用例原本靠程序集级 <c>DisableTestParallelization</c> 串行（见 <c>AssemblyInfo.cs</c> 的原注释）；
/// 运行配置改为按 collection 并行后，这条隐含前提不成立 ⇒ 用 collection 显式表达"这一组共享全局注册面、
/// 彼此不得并行"（沿 <c>IntelligenceStaticSerial</c>／<c>KeywordEffectBatch4Serial</c> 先例）。
/// </para>
/// </summary>
[CollectionDefinition("PackKeywordRegistrySerial", DisableParallelization = true)]
public sealed class PackKeywordRegistrySerialCollection
{
}
