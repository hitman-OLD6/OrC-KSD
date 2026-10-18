using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// E-D1 数据面与动态效果：schema 3/2 兼容、OneShot、disposing 事件、非法 band。
/// </summary>
public class EffectDisposalGameTests
{
    private const string OneShotJson = """
{
  "schemaVersion": 3,
  "root": {
    "id": "test.oneshot",
    "mainTrigger": {
      "id": "t.main",
      "stableKey": "test.oneshot.main",
      "kind": "passive",
      "hooks": ["turn.start"],
      "events": [{ "id": "e1", "entry": "HandleAsync", "assemblyKey": "test.oneshot" }]
    },
    "otherTriggers": [],
    "modings": [],
    "injects": [],
    "oneShot": true
  }
}
""";

    private const string LegacySchema2Json = """
{
  "schemaVersion": 2,
  "root": {
    "id": "test.legacy",
    "mainTrigger": {
      "id": "t.main",
      "stableKey": "test.legacy.main",
      "kind": "passive",
      "hooks": ["turn.start"],
      "events": [{ "id": "e1", "entry": "HandleAsync", "assemblyKey": "test.legacy" }]
    },
    "otherTriggers": [],
    "modings": [],
    "injects": []
  }
}
""";

    [Fact]
    public void Schema2_Is_Read_And_Normalized_With_Default_Disposing()
    {
        var snapshot = PrefabJson.Deserialize(LegacySchema2Json);

        Assert.Equal(EffectSnapshot.SchemaVersion, snapshot.Version); // 归一化为 3
        Assert.False(snapshot.Root.OneShot);
        Assert.Contains(Updates.CardDestroyed, snapshot.Root.DisposingTrigger.Hooks);
        Assert.Empty(snapshot.Root.DisposingTrigger.Events);
        Assert.Equal("test.legacy/dispose", snapshot.Root.DisposingTrigger.StableKey);
    }

    [Fact]
    public void Invalid_Event_Band_Is_Rejected()
    {
        var json = OneShotJson.Replace(
            "\"assemblyKey\": \"test.oneshot\"",
            "\"assemblyKey\": \"test.oneshot\", \"band\": \"bogus\"",
            StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => PrefabJson.Deserialize(json));
    }

    [Fact]
    public async Task OneShot_Dynamic_Effect_Disposes_After_First_Execution()
    {
        var engine = new LogicEngine();
        var fired = 0;
        engine.Prefabs.RegisterHandler(
            "test.oneshot",
            (Func<CardEventView, Context, CancellationToken, Task>)((view, ctx, ct) =>
            {
                fired++;
                return Task.CompletedTask;
            }));

        var snapshot = PrefabJson.Deserialize(OneShotJson);
        var instantiation = DynamicEffectFactory.Instantiate(engine, snapshot, "test.oneshot");
        Assert.True(instantiation.Success, instantiation.Error);

        var effect = instantiation.Effect!;
        var card = new Card(engine, "甲");
        card.AddEffect(effect);
        Assert.True(effect.IsMounted);

        await engine.Emit(GameUpdates.TurnStart, new Dictionary<string, object?>());

        Assert.Equal(1, fired);
        Assert.True(effect.IsDisposed);
        Assert.Empty(card.Effects);

        await engine.Emit(GameUpdates.TurnStart, new Dictionary<string, object?>());
        Assert.Equal(1, fired); // 一次性：销毁后不再响应
    }

    [Fact]
    public void OneShot_Without_Mountable_Main_Trigger_Is_Rejected()
    {
        var json = OneShotJson.Replace("\"hooks\": [\"turn.start\"]", "\"hooks\": []", StringComparison.Ordinal);
        var snapshot = PrefabJson.Deserialize(json);

        var engine = new LogicEngine();
        var instantiation = DynamicEffectFactory.Instantiate(engine, snapshot, "test.oneshot");

        Assert.False(instantiation.Success);
        Assert.Equal("one-shot-no-trigger", instantiation.ErrorCategory);
    }

    [Fact]
    public async Task Main_Trigger_Disposing_Band_Runs_After_Ordinary_Events()
    {
        const string json = """
{
  "schemaVersion": 3,
  "root": {
    "id": "test.band",
    "mainTrigger": {
      "id": "t.main",
      "stableKey": "test.band.main",
      "kind": "passive",
      "hooks": ["turn.start"],
      "events": [
        { "id": "e.normal", "entry": "HandleAsync", "assemblyKey": "test.band.normal", "band": "default" },
        { "id": "e.dispose", "entry": "HandleAsync", "assemblyKey": "test.band.dispose", "band": "disposing" }
      ]
    },
    "otherTriggers": [],
    "modings": [],
    "injects": []
  }
}
""";

        var engine = new LogicEngine();
        var trace = new List<string>();
        engine.Prefabs.RegisterHandler(
            "test.band.normal",
            (Func<CardEventView, Context, CancellationToken, Task>)((view, ctx, ct) =>
            {
                trace.Add("normal");
                return Task.CompletedTask;
            }));
        engine.Prefabs.RegisterHandler(
            "test.band.dispose",
            (Func<CardEventView, Context, CancellationToken, Task>)((view, ctx, ct) =>
            {
                trace.Add("disposing");
                return Task.CompletedTask;
            }));

        var instantiation = DynamicEffectFactory.Instantiate(
            engine, PrefabJson.Deserialize(json), "test.band");
        Assert.True(instantiation.Success, instantiation.Error);

        var card = new Card(engine, "甲");
        card.AddEffect(instantiation.Effect!);
        await engine.Emit(GameUpdates.TurnStart, new Dictionary<string, object?>());

        Assert.Equal(new[] { "normal", "disposing" }, trace); // 保留 band 恒在普通事件之后
    }

    [Fact]
    public void Disposing_Trigger_Events_Round_Trip_And_Run()
    {
        const string json = """
{
  "schemaVersion": 3,
  "root": {
    "id": "test.dispose-events",
    "mainTrigger": {
      "id": "t.main",
      "stableKey": "test.dispose-events.main",
      "kind": "passive",
      "hooks": ["turn.start"],
      "events": [{ "id": "e1", "entry": "HandleAsync", "assemblyKey": "test.dispose-events" }]
    },
    "otherTriggers": [],
    "modings": [],
    "injects": [],
    "disposingTrigger": {
      "id": "t.dispose",
      "stableKey": "test.dispose-events/dispose",
      "kind": "passive",
      "hooks": ["card.destroyed"],
      "events": [{ "id": "e.dispose", "entry": "HandleAsync", "assemblyKey": "test.dispose-events.dispose" }]
    }
  }
}
""";

        var engine = new LogicEngine();
        var disposed = 0;
        engine.Prefabs.RegisterHandler(
            "test.dispose-events",
            (Func<CardEventView, Context, CancellationToken, Task>)((view, ctx, ct) => Task.CompletedTask));
        engine.Prefabs.RegisterHandler(
            "test.dispose-events.dispose",
            (Func<CardEventView, Context, CancellationToken, Task>)((view, ctx, ct) =>
            {
                disposed++;
                return Task.CompletedTask;
            }));

        var snapshot = PrefabJson.Deserialize(json);
        Assert.Equal("test.dispose-events/dispose", snapshot.Root.DisposingTrigger.StableKey);
        Assert.Single(snapshot.Root.DisposingTrigger.Events);

        // 往返保真
        var roundTrip = PrefabJson.Deserialize(PrefabJson.Serialize(snapshot));
        Assert.Equal("test.dispose-events/dispose", roundTrip.Root.DisposingTrigger.StableKey);
        Assert.Single(roundTrip.Root.DisposingTrigger.Events);

        var instantiation = DynamicEffectFactory.Instantiate(engine, snapshot, "test.dispose-events");
        Assert.True(instantiation.Success, instantiation.Error);
        var card = new Card(engine, "甲");
        card.AddEffect(instantiation.Effect!);

        card.RemoveEffect(instantiation.Effect!);

        Assert.Equal(1, disposed); // 手写 disposing 事件（额外副作用清理）执行
        Assert.Empty(card.Effects);
    }
}
