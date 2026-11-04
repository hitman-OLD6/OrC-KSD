using System.Text.Json;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Commanding;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 存档的游戏层验收（S7／D50 守护）：内置 B（<c>framework.save.full-copy</c>）注册于
/// <see cref="Match.Initialize"/>——**作者删掉自己的服务后仍可用内置 B 存档**（否则内置 B 可能永远没人走过）。
/// 纯内核构造路径没有内置 B（已知限制 C2），故本断言只能落在游戏层。
/// </summary>
public sealed class SaveExportGameTests
{
    /// <summary>
    /// D50 守护：预制体声明 <c>save: "framework.save.full-copy"</c>（不注册任何自定义导出器）⇒
    /// ExportSave 经内置 B 成功产出完整复制 moding；未声明 save 的实体照常排除。
    /// </summary>
    [Fact]
    public async Task D50_内置B_无需自定义注册即可存档()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();

        // 内置 B 随对局初始化注册（存在性在存档时刻解析——装载期不校验）。
        Assert.NotNull(match.Judicators.Resolve(SavePolicy.FullCopyServiceId));

        match.EngineRuntime.Catalog.Add(new PrefabDefinition(
            "unit.save.me",
            EntityKind.Card,
            hosts: HostKind.Unit,
            privateFields: new[] { new PrivateFieldDefinition("hp", ViewFieldType.Number, 3m) },
            slots: new[] { new AssetSlotDeclaration("power", SlotType.Number, 2m) },
            save: SavePolicy.FullCopyServiceId));
        match.EngineRuntime.Catalog.Add(new PrefabDefinition(
            "unit.plain", EntityKind.Card, hosts: HostKind.Unit)); // 未声明 save

        var entity = match.EngineRuntime.CreateEntity("unit.save.me", "saved1");
        entity.SetPrivateField("hp", 1m); // 状态演化：_state.hp=1（配置 3）——D55 判据
        match.EngineRuntime.CreateEntity("unit.plain", "plain1");

        using var doc = JsonDocument.Parse(match.Engine.ExportSave());
        var saved = doc.RootElement.GetProperty("savedEntities");
        var entry = Assert.Single(saved.EnumerateArray().ToArray()); // plain1 被门户排除

        Assert.Equal("saved1", entry.GetProperty("name").GetString());
        var moding = entry.GetProperty("moding");
        Assert.Equal("unit.save.me", moding.GetProperty("extends").GetString());
        Assert.Equal(2m, moding.GetProperty("slots").GetProperty("power").GetDecimal()); // 配置值
        Assert.Equal(1m, moding.GetProperty("privateFields").GetProperty("hp").GetDecimal()); // _state 当前值
        Assert.Equal(0, moding.GetProperty("attachments").GetArrayLength()); // 无附件
    }
}
