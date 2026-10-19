// [grant] keyword={{keyword}} selector(sel={{sel}} side={{side}})
// 块化（批 4·序列③）：「获得 X 和 Y」逐词条授予＝同 handler 多个 grant——块作用域隔离变量
// （runtime/targets），防同 handler 重复声明的编译冲突；单 grant 渲染逐字节等价（块内文本不变）。
{
    var runtime = self is null ? null : Orc.Game.Effects.EffectRuntime.ResolveFor(self);
    if (runtime is not null)
    {
        var targets = await runtime.SelectAsync(self!, new Orc.Game.Effects.EffectSelector({{sel}}, {{side}}, {{selZone}}, {{filterUnitType}}, {{filterKeyword}}, null, {{selThreshold}}));
        foreach (var target in targets)
        {
            await runtime.GrantAsync(target, {{keyword}}{{value}}, ct, self);
        }
    }
}
