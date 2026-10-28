using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Server.Admin;
using Xunit;

namespace Orc.Server.Tests;

public sealed class CosmeticChannelTests
{
    private static JsonObject Profile(string image="")=>new(){["CardBack"]=image,["Decoration"]="",["Headquarters"]=image,["HeadquartersName"]="我的总部",
        ["Emotes"]=new JsonArray(new JsonObject{["Id"]="hello",["Label"]="你好",["Image"]=image})};
    private static async Task Admit(HostFixture fixture,OrcClient client,int seat)
    {
        await HostFixture.AttachControllerAsync(client,seat);
        Assert.True((await fixture.AdminAsync(AdminOps.ConnectionAdmit,new JsonObject{["connectionId"]=client.ConnectionId})).Ok);
    }
    [Fact] public async Task PendingCannotPublishOrReadAndNeverReceivesCosmetics()
    {
        await using var f=await HostFixture.StartAsync();await using var pending=await f.ConnectAsync();await using var player=await f.ConnectAsync();
        await HostFixture.AttachControllerAsync(pending,1);await using var notifications=new NotificationCollector(pending);
        Assert.NotNull((await pending.CallRawAsync("cosmetics.get",new JsonObject())).Error);
        Assert.NotNull((await pending.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=Profile()})).Error);
        await Admit(f,player,0);
        Assert.Null((await player.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=Profile()})).Error);
        await pending.CallAsync("room.info");
        Assert.False(notifications.Has("notify/cosmetics"));
    }
    [Fact] public async Task ProfilesAndImagesAreSharedAndSeatCannotBeForgedEmotesAreRateLimited()
    {
        await using var f=await HostFixture.StartAsync();await using var first=await f.ConnectAsync();await using var second=await f.ConnectAsync();
        await Admit(f,first,0);await Admit(f,second,1);await using var notifications=new NotificationCollector(second);
        var png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
        Array.Resize(ref png,1100000); // Original artwork may exceed the former 1 MiB framing budget.
        var uploaded=await first.CallRawAsync("cosmetics.asset",new JsonObject{["png"]=Convert.ToBase64String(png)});
        Assert.Null(uploaded.Error);var hash=uploaded.Result!["hash"]!.GetValue<string>();
        var profile=Profile(hash);profile["Emotes"]![0]!["EnemyImage"]=hash;
        Assert.Null((await first.CallRawAsync("cosmetics.set",new JsonObject{["seat"]=1,["profile"]=profile})).Error);
        var changed=await notifications.WaitAsync("notify/cosmetics");Assert.Equal(0,changed.Params!["seat"]!.GetValue<int>());
        var snapshot=await second.CallRawAsync("cosmetics.get",new JsonObject());Assert.Single(snapshot.Result!["profiles"]!.AsArray());
        Assert.True(snapshot.Result["profiles"]![0]!["revision"]!.GetValue<long>()>0);
        var image=await second.CallRawAsync("cosmetics.image",new JsonObject{["seat"]=0,["hash"]=hash});Assert.Equal(png,Convert.FromBase64String(image.Result!["png"]!.GetValue<string>()));
        Assert.Null((await first.CallRawAsync("cosmetics.emote",new JsonObject{["id"]="hello",["seat"]=1})).Error);
        var emote=await notifications.WaitAsync("notify/emote");Assert.Equal(0,emote.Params!["seat"]!.GetValue<int>());Assert.Equal(hash,emote.Params["enemyImage"]!.GetValue<string>());
        Assert.NotNull((await first.CallRawAsync("cosmetics.emote",new JsonObject{["id"]="hello"})).Error);
        Assert.NotNull((await second.CallRawAsync("cosmetics.emote",new JsonObject{["id"]="hello"})).Error);
    }
    [Fact] public async Task RejectsRuleFieldsMissingAssetsAndOversizedImages()
    {
        await using var f=await HostFixture.StartAsync();await using var player=await f.ConnectAsync();await Admit(f,player,0);
        var rules=Profile();rules["InitialDefense"]=999;
        Assert.NotNull((await player.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=rules})).Error);
        Assert.NotNull((await player.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=Profile(new string('A',64))})).Error);
        Assert.NotNull((await player.CallRawAsync("cosmetics.asset",new JsonObject{["png"]=Convert.ToBase64String(new byte[2097153])})).Error);
        var snapshot=await player.CallRawAsync("cosmetics.get",new JsonObject());Assert.Empty(snapshot.Result!["profiles"]!.AsArray());
    }
    [Fact] public async Task ApprovedViewerCanReadButCannotPublishOrEmote()
    {
        await using var f=await HostFixture.StartAsync();await using var player=await f.ConnectAsync();await using var viewer=await f.ConnectAsync();await Admit(f,player,0);
        Assert.Null((await player.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=Profile()})).Error);
        await HostFixture.AttachViewerAsync(viewer,null);
        Assert.True((await f.AdminAsync(AdminOps.ConnectionAdmit,new JsonObject{["connectionId"]=viewer.ConnectionId})).Ok);
        var snapshot=await viewer.CallRawAsync("cosmetics.get",new JsonObject());Assert.Null(snapshot.Error);Assert.Single(snapshot.Result!["profiles"]!.AsArray());
        Assert.NotNull((await viewer.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=Profile()})).Error);
        Assert.NotNull((await viewer.CallRawAsync("cosmetics.emote",new JsonObject{["id"]="hello"})).Error);
    }
    [Fact] public async Task EightEquippedEmotesAreAcceptedAndNinthIsRejected()
    {
        await using var f=await HostFixture.StartAsync();await using var player=await f.ConnectAsync();await Admit(f,player,0);
        var profile=Profile();var emotes=new JsonArray();
        for(var i=0;i<8;i++)emotes.Add(new JsonObject{["Id"]="emote-"+i,["Label"]="表情 "+i,["Image"]=""});
        profile["Emotes"]=emotes;
        Assert.Null((await player.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=profile.DeepClone()})).Error);
        Assert.Null((await player.CallRawAsync("cosmetics.emote",new JsonObject{["id"]="emote-7"})).Error);
        emotes.Add(new JsonObject{["Id"]="emote-8",["Label"]="第九条",["Image"]=""});
        Assert.NotNull((await player.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=profile})).Error);
    }
    [Fact] public async Task OriginalEmoteStyleAndColorSurviveBroadcastAndInvalidStyleIsRejected()
    {
        await using var f=await HostFixture.StartAsync();await using var first=await f.ConnectAsync();await using var peer=await f.ConnectAsync();
        await Admit(f,first,0);await Admit(f,peer,1);await using var notifications=new NotificationCollector(peer);
        var profile=Profile();var emote=profile["Emotes"]![0]!.AsObject();
        emote["Style"]="special";emote["Tint"]="132A40";emote["Label"]="第一行\n第二行";
        Assert.Null((await first.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=profile.DeepClone()})).Error);
        Assert.Null((await first.CallRawAsync("cosmetics.emote",new JsonObject{["id"]="hello"})).Error);
        var sent=await notifications.WaitAsync("notify/emote");
        Assert.Equal("special",sent.Params!["style"]!.GetValue<string>());Assert.Equal("132A40",sent.Params["tint"]!.GetValue<string>());
        emote["EnemyImage"]=new string('A',64);
        Assert.NotNull((await first.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=profile.DeepClone()})).Error);
        emote.Remove("EnemyImage");
        emote["Style"]="unknown";
        Assert.NotNull((await first.CallRawAsync("cosmetics.set",new JsonObject{["profile"]=profile.DeepClone()})).Error);
    }
}
