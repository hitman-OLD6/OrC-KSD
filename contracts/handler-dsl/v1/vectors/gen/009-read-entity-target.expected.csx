Func<dynamic, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
{
    var orcSelf = (Orc.Cards.Card)view.Host;
    var __runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(orcSelf);
    if (__runtime is null)
    {
        return;
    }
    foreach (var u in view.Units)
    {
        if (((int)Orc.Game.Cards.KeywordEffectActionFace.ReadValue(u, "self", "defense") <= 2))
        {
            await __runtime.DrawAsync(viewer: orcSelf, count: 1, source: orcSelf, ct: ct);
        }
    }
};
