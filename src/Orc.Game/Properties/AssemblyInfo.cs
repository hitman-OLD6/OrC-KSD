using System.Runtime.CompilerServices;

// R11 门户收口：伤害数值门户（UnitCard.ApplyDefenseDamageAsync / Hq.ApplyDamageAsync）降为 internal，
// 仅本程序集（伤害动作服务面 MatchActionServices.ApplyDamageAsync）可调。
// 测试程序集保留友元（既有 85 处门户调用不改）；三方程序集（UI/MCP/插件/samples）编译期即被挡住。
[assembly: InternalsVisibleTo("Orc.Game.Tests")]
