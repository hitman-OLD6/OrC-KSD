Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    var orcSelf = (Orc.Cards.Card)view.Host;
    var __runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(orcSelf);
    if (__runtime is null)
    {
        return;
    }
    var __choice__actions_0 = await Orc.Game.Cards.KeywordEffectActionFace.ChooseAsync(orcSelf, "选择一项：\"抽牌\" 或 \"放弃\"", new string[] { "抽 1 张", "放弃" }, ct);
    switch (__choice__actions_0)
    {
        case 0:
            await __runtime.DrawAsync(viewer: orcSelf, count: 1, source: orcSelf, ct: ct);
            break;
        case 1:
            ctx.Stop();
            break;
    }
    var picked = await Orc.Game.Cards.KeywordEffectActionFace.AskAsync(orcSelf, "unit", "enemy", null, "选一个敌方单位", "first", ct);
    foreach (var p in picked)
    {
        await __runtime.KillAsync(target: p, source: orcSelf, ct: ct);
    }
};
