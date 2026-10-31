Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    var orcSelf = (Orc.Cards.Card)view.Host;
    var __runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(orcSelf);
    if (__runtime is null)
    {
        return;
    }
    // 作者脚本正文原样内联
    var y = view.Amount + 1;
    var __res__actions_1 = await __runtime.FetchFromDeckAsync(target: view.Target, source: orcSelf, ct: ct);
    var st = __res__actions_1.Status.ToString();
    var c = __res__actions_1.Card;
    await __runtime.KillAsync(target: view.Target, source: c, ct: ct);
};
