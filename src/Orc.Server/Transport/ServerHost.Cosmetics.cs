using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Orc.Protocol;

namespace Orc.Server.Transport;

public sealed partial class ServerHost
{
    // Presentation-only room data. Never enters the match executor, card pool or replay actions.
    private readonly object _cosmeticSync = new();
    private string _cosmeticRoom = "";
    private readonly Dictionary<int, CosmeticSeat> _cosmeticSeats = new();
    private long _emoteSequence;
    private long _cosmeticRevision;
    private sealed class CosmeticSeat
    {
        public string? Token;
        public JsonObject? Profile;
        public readonly Dictionary<string,byte[]> Images = new();
        public long LastEmote;
        public long Revision;
    }
    private static bool CosmeticReader(ClientSession s) => s.State is not (SessionState.Connected or SessionState.Pending or SessionState.Revoked)
        && (s.IsViewer || s.IsController || s.IsDebug);
    private RpcResponse HandleCosmetics(RpcRequest request, ClientSession session)
    {
        if (!CosmeticReader(session)) return RpcResponse.Failure(request.Id,RpcErrorCodes.InvalidRequest,"外观通道需要已批准的房间会话。");
        try
        {
            lock (_cosmeticSync)
            {
                var roomId = Room.RoomId;
                if (_cosmeticRoom != roomId) { _cosmeticRoom=roomId; _cosmeticSeats.Clear(); _emoteSequence=0; _cosmeticRevision=0; }
                if (request.Method == "cosmetics.get")
                {
                    var profiles = new JsonArray();
                    foreach (var (seat,data) in _cosmeticSeats)
                        if (data.Profile is not null) profiles.Add(new JsonObject { ["seat"]=seat,["revision"]=data.Revision,["profile"]=data.Profile.DeepClone() });
                    return RpcResponse.Success(request.Id,new JsonObject { ["roomId"]=roomId,["profiles"]=profiles });
                }
                var args = request.Params as JsonObject ?? throw new ArgumentException("缺少外观参数。");
                if (request.Method == "cosmetics.image")
                {
                    var seat = args["seat"]!.GetValue<int>(); var hash = args["hash"]!.GetValue<string>();
                    if (!_cosmeticSeats.TryGetValue(seat,out var owner) || owner.Profile is null || !ProfileImages(owner.Profile).Contains(hash)
                        || !owner.Images.TryGetValue(hash,out var bytes)) throw new ArgumentException("外观图片不存在。");
                    return RpcResponse.Success(request.Id,new JsonObject { ["hash"]=hash,["png"]=Convert.ToBase64String(bytes) });
                }
                if (!session.IsController || session.Seat is not { } ownSeat || string.IsNullOrEmpty(session.SeatToken))
                    return RpcResponse.Failure(request.Id,RpcErrorCodes.InvalidRequest,"只有已批准的座位控制者可以装备外观或发送表情。");
                if (!_cosmeticSeats.TryGetValue(ownSeat,out var own) || own.Token != session.SeatToken)
                    _cosmeticSeats[ownSeat] = own = new CosmeticSeat { Token=session.SeatToken };
                if (request.Method == "cosmetics.asset")
                {
                    var base64 = args["png"]!.GetValue<string>();
                    if (base64.Length > 2800000) throw new ArgumentException("外观图片过大。");
                    var bytes = Convert.FromBase64String(base64);
                    if (bytes.Length is <33 or >2097152 || !bytes.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})
                        || !bytes.AsSpan(12,4).SequenceEqual("IHDR"u8)) throw new ArgumentException("需要 PNG 外观图片。");
                    var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4)); var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4));
                    if (width is <1 or >4096 || height is <1 or >4096) throw new ArgumentException("外观图片尺寸超限。");
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (!own.Images.ContainsKey(hash))
                    {
                        if (own.Images.Count >= 24 || own.Images.Values.Sum(b => b.Length)+bytes.Length > 24*1024*1024) throw new ArgumentException("房间外观缓存已满，请先完成装备或重新加入房间。");
                        own.Images[hash]=bytes;
                    }
                    return RpcResponse.Success(request.Id,new JsonObject { ["hash"]=hash });
                }
                if (request.Method == "cosmetics.set")
                {
                    var profile = (args["profile"] as JsonObject)?.DeepClone() as JsonObject ?? throw new ArgumentException("缺少外观资料。");
                    if (profile.Any(p => p.Key is not ("CardBack" or "Decoration" or "Headquarters" or "HeadquartersName" or "Emotes")))
                        throw new ArgumentException("外观通道不接受规则字段。");
                    static void Text(string? s,int max,bool multiline=false) { if (s is null || s.Length>max || s.Any(c=>char.IsControl(c)&&!(multiline&&c=='\n'))) throw new ArgumentException("外观文字无效。"); }
                    Text(profile["HeadquartersName"]?.GetValue<string>(),24);
                    foreach (var key in new[]{"CardBack","Decoration","Headquarters"}) Text(profile[key]?.GetValue<string>(),64);
                    if (profile["Emotes"] is not JsonArray emotes || emotes.Count is <1 or >8) throw new ArgumentException("表情须为 1–8 个。");
                    var ids = new HashSet<string>();
                    foreach (var e in emotes)
                    {
                        if (e is not JsonObject emote || emote.Any(p => p.Key is not ("Id" or "Label" or "Image" or "Style" or "Tint" or "EnemyImage"))) throw new ArgumentException("表情格式无效。");
                        var id=emote["Id"]?.GetValue<string>(); Text(id,48); Text(emote["Label"]?.GetValue<string>(),160,true); Text(emote["Image"]?.GetValue<string>(),64);
                        if(emote["EnemyImage"] is {} variant)Text(variant.GetValue<string>(),64);
                        if(emote["Style"] is {} style && style.GetValue<string>() is not ("paper" or "special" or "diy"))throw new ArgumentException("未知表情样式。");
                        if(emote["Tint"] is {} tint && (tint.GetValue<string>().Length!=6 || !tint.GetValue<string>().All(Uri.IsHexDigit)))throw new ArgumentException("表情颜色无效。");
                        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c!='-') || !ids.Add(id)
                            || string.IsNullOrWhiteSpace(emote["Label"]!.GetValue<string>())) throw new ArgumentException("表情编号或文字无效。");
                    }
                    var used = ProfileImages(profile).ToHashSet();
                    if (used.Any(hash => hash.Length!=64 || !hash.All(Uri.IsHexDigit) || !own.Images.ContainsKey(hash))) throw new ArgumentException("请先上传已装备的外观图片。");
                    own.Profile=profile;own.Revision=++_cosmeticRevision;
                    foreach (var hash in own.Images.Keys.Where(h => !used.Contains(h)).ToArray()) own.Images.Remove(hash);
                    CosmeticBroadcast("notify/cosmetics",new JsonObject { ["roomId"]=roomId,["seat"]=ownSeat,["revision"]=own.Revision,["profile"]=profile.DeepClone() });
                    return RpcResponse.Success(request.Id,new JsonObject { ["ok"]=true });
                }
                var emoteId=args["id"]!.GetValue<string>();
                if (own.Profile?["Emotes"] is not JsonArray equipped || !equipped.Any(e => e?["Id"]?.GetValue<string>()==emoteId)) throw new ArgumentException("请先装备此表情。");
                var now=Environment.TickCount64;
                if (own.LastEmote!=0 && now-own.LastEmote<2000) throw new ArgumentException("表情发送间隔为 2 秒。");
                own.LastEmote=now;
                var chosen=equipped.Single(e=>e?["Id"]?.GetValue<string>()==emoteId)!;
                CosmeticBroadcast("notify/emote",new JsonObject { ["roomId"]=roomId,["seat"]=ownSeat,["id"]=emoteId,["label"]=chosen["Label"]!.GetValue<string>(),["image"]=chosen["Image"]!.GetValue<string>(),["style"]=chosen["Style"]?.GetValue<string>()??"diy",["tint"]=chosen["Tint"]?.GetValue<string>()??"433a29",["enemyImage"]=chosen["EnemyImage"]?.GetValue<string>()??"",["sequence"]=++_emoteSequence });
                return RpcResponse.Success(request.Id,new JsonObject { ["ok"]=true });
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or NullReferenceException)
        { return RpcResponse.Failure(request.Id,RpcErrorCodes.InvalidParams,e.Message); }
    }
    private static IEnumerable<string> ProfileImages(JsonObject profile) => new[]{"CardBack","Decoration","Headquarters"}
        .Select(k => profile[k]!.GetValue<string>()).Concat(((JsonArray)profile["Emotes"]!).SelectMany(e => new[]{e!["Image"]!.GetValue<string>(),e!["EnemyImage"]?.GetValue<string>()??""})).Where(s => s.Length>0).Distinct();
    private void CosmeticBroadcast(string method,JsonObject payload)
    {
        foreach(var target in _sessions.Values) if(CosmeticReader(target)) SendNotification(target,method,payload.DeepClone());
    }
}
