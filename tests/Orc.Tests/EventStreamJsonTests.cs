using System.Globalization;
using System.Text.Json;
using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Output;
using Xunit;

namespace Orc.Tests;

/// <summary>
/// S5 验收点④（事件流 JSON 树形断言）：
/// 结构＝{ id, entries, children }；条目归属＝恰好出现在其写入流节点（语义 B：本地条目；树中不重复）；
/// 从任意流导出＝子树；空流＝合法空输出；条目字段（kind/level 表示、timestamp 格式）与 data 值降级策略。
/// </summary>
public class EventStreamJsonTests
{
    [Fact]
    public async Task Tree_Structure_And_Local_Entry_Ownership_Exactly_Once()
    {
        var engine = new LogicEngine();
        var child = KernelTestKit.Member("子", new OwnedEdge((_, _, _) => Task.CompletedTask));
        KernelTestKit.Register(engine, child);

        // 父成员内部以"另起数据面执行"调用子成员 ⇒ 子流挂父流（树结构由生产路径产生）。
        var parent = KernelTestKit.Member("父", new OwnedEdge((KernelHandler)(async (_, _, _) =>
            await KernelTestKit.RunNestedAsync(engine, "子"))));
        await KernelTestKit.RunAsync(engine, parent);

        var parentStream = engine.RootStream.Children.Single();
        var childStream = parentStream.Children.Single();

        childStream.WriteLog("测试源", "子条目一");
        parentStream.WriteLog("测试源", "父条目一");

        var json = EventStreamJson.Serialize(engine.RootStream);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 结构：根节点 id＝总流 id；children 层级＝挂载树（子流挂父流、顶层流挂总流）
        Assert.Equal(engine.RootStream.Id, root.GetProperty("id").GetString());
        var topNodes = root.GetProperty("children").EnumerateArray().ToArray();
        Assert.Single(topNodes);
        Assert.Equal(parentStream.Id, topNodes[0].GetProperty("id").GetString());
        var childNodes = topNodes[0].GetProperty("children").EnumerateArray().ToArray();
        Assert.Single(childNodes);
        Assert.Equal(childStream.Id, childNodes[0].GetProperty("id").GetString());
        Assert.Empty(childNodes[0].GetProperty("children").EnumerateArray());

        // 条目归属（语义 B）：子条目恰在子流节点；父流节点与总流节点不含它
        Assert.Contains(
            childNodes[0].GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("message").GetString() == "子条目一");
        Assert.DoesNotContain(
            topNodes[0].GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("message").GetString() == "子条目一");
        Assert.DoesNotContain(
            root.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("message").GetString() == "子条目一");

        // 父流本地条目：子流挂载 attach（写入父流侧）＋父条目一（恰两条）
        var parentMessages = topNodes[0].GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("message").GetString()).ToArray();
        Assert.Equal(2, parentMessages.Length);
        Assert.Contains("父条目一", parentMessages);
        var parentAttach = topNodes[0].GetProperty("entries").EnumerateArray()
            .Single(e => e.GetProperty("kind").GetString() == "attach");
        Assert.Equal(childStream.Id, parentAttach.GetProperty("data").GetProperty("childId").GetString());
        Assert.Equal(topNodes[0].GetProperty("id").GetString(), parentAttach.GetProperty("data").GetProperty("parentId").GetString());

        // 总流本地条目：顶层流挂载 attach（恰一条）
        var rootEntries = root.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Single(rootEntries);
        Assert.Equal("attach", rootEntries[0].GetProperty("kind").GetString());
        Assert.Equal(parentStream.Id, rootEntries[0].GetProperty("data").GetProperty("childId").GetString());

        // 恰出现一次：全树统计（条目不重复；每条在且仅在其写入流的节点下）
        Assert.Equal(1, CountTreeEntriesByMessage(root, "子条目一"));
        Assert.Equal(1, CountTreeEntriesByMessage(root, "父条目一"));

        // 内存读面（含冒泡）不受影响：总流 Entries 仍含全部条目（语义 B 仅约束 JSON 呈现）
        Assert.Contains(engine.RootStream.Entries, e => e.Message == "子条目一");
        Assert.Contains(engine.RootStream.Entries, e => e.Message == "父条目一");

        // 从任意流导出＝以该流为根的子树（含全部后代、不含祖先）：以父流为根时含子流节点
        var parentSubtree = EventStreamJson.Serialize(parentStream);
        using var parentDoc = JsonDocument.Parse(parentSubtree);
        Assert.Equal(parentStream.Id, parentDoc.RootElement.GetProperty("id").GetString());
        var nested = parentDoc.RootElement.GetProperty("children").EnumerateArray().Single();
        Assert.Equal(childStream.Id, nested.GetProperty("id").GetString());

        // 不含祖先侧条目：父流挂到总流的 attach（childId＝父流）属于总流节点、不出现在父流子树内
        Assert.DoesNotContain(
            parentDoc.RootElement.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("kind").GetString() == "attach"
                && e.GetProperty("data").GetProperty("childId").GetString() == parentStream.Id);
    }

    [Fact]
    public void Entry_Fields_And_Data_Value_Degradation()
    {
        var engine = new LogicEngine();
        var card = new Card(engine, "测试卡");
        var plain = new Entity("引用目标");

        var now = DateTimeOffset.UtcNow;
        engine.RootStream.WriteLog(
            "ops",
            "人工记录",
            LogLevel.Warning,
            keywords: new[] { "manual", "level:warning" },
            data: new Dictionary<string, object?>
            {
                ["n"] = 42,
                ["flag"] = true,
                ["note"] = "你好",
                ["card"] = card,
                ["reference"] = plain.Ref,
                ["nested"] = new Dictionary<string, object?> { ["x"] = 1 },
                ["list"] = new object?[] { "a", 2, null },
                ["when"] = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
                ["level"] = LogLevel.Error,
            });
        engine.RootStream.WriteUpdate("bus", "demo.updated", data: new Dictionary<string, object?> { ["k"] = "v" });

        var json = EventStreamJson.Serialize(engine.RootStream);
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToArray();

        // 条目字段：kind/level 小写字符串、source/message/keywords
        var log = entries.Single(e => e.GetProperty("message").GetString() == "人工记录");
        Assert.Equal("log", log.GetProperty("kind").GetString());
        Assert.Equal("warning", log.GetProperty("level").GetString());
        Assert.Equal("ops", log.GetProperty("source").GetString());
        Assert.Equal(
            new[] { "manual", "level:warning" },
            log.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()).ToArray());

        // timestamp：ISO 8601（UTC）可解析
        var stamp = DateTimeOffset.Parse(
            log.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture);
        Assert.True(stamp >= now.AddMinutes(-1));
        Assert.True(stamp <= DateTimeOffset.UtcNow);

        // data 值：标量原样；实体/引用降级（不递归成员——防环）
        var data = log.GetProperty("data");
        Assert.Equal(42, data.GetProperty("n").GetInt32());
        Assert.True(data.GetProperty("flag").GetBoolean());
        Assert.Equal("你好", data.GetProperty("note").GetString());
        // 实体/引用：{"$ref": GUID(N), "name": 名, "alive": 存活}——引用键取代旧 $entity 名（多人支持 S5）
        var cardNode = data.GetProperty("card");
        Assert.Equal("测试卡", cardNode.GetProperty("name").GetString());
        Assert.True(cardNode.GetProperty("$alive").GetBoolean());
        Assert.True(Guid.TryParseExact(cardNode.GetProperty("$ref").GetString(), "N", out _));

        var referenceNode = data.GetProperty("reference");
        Assert.Equal("引用目标", referenceNode.GetProperty("name").GetString());
        Assert.True(referenceNode.GetProperty("$alive").GetBoolean());
        Assert.True(Guid.TryParseExact(referenceNode.GetProperty("$ref").GetString(), "N", out _));

        // 嵌套字典/数组递归；null 原样；枚举→字符串；DateTimeOffset→ISO 8601
        Assert.Equal(1, data.GetProperty("nested").GetProperty("x").GetInt32());
        Assert.Equal(
            new[] { JsonValueKind.String, JsonValueKind.Number, JsonValueKind.Null },
            data.GetProperty("list").EnumerateArray().Select(x => x.ValueKind).ToArray());
        Assert.Equal("Error", data.GetProperty("level").GetString());
        Assert.Equal("2026-01-02T03:04:05.0000000+00:00", data.GetProperty("when").GetString());

        // update 条目（S3 总线写入）：kind＝update、message＝更新字符串、data＝载荷
        var update = entries.Single(e => e.GetProperty("kind").GetString() == "update");
        Assert.Equal("demo.updated", update.GetProperty("message").GetString());
        Assert.Equal("v", update.GetProperty("data").GetProperty("k").GetString());
    }

    [Fact]
    public async Task Serialize_From_Arbitrary_Stream_Subtree_Empty_And_Indented()
    {
        var engine = new LogicEngine();

        // 空流/空树：合法输出（空数组，不报错）
        var emptyJson = EventStreamJson.Serialize(engine.RootStream);
        using (var emptyDoc = JsonDocument.Parse(emptyJson))
        {
            Assert.Equal(engine.RootStream.Id, emptyDoc.RootElement.GetProperty("id").GetString());
            Assert.Empty(emptyDoc.RootElement.GetProperty("entries").EnumerateArray());
            Assert.Empty(emptyDoc.RootElement.GetProperty("children").EnumerateArray());
        }

        // 造两条顶层流：从任意流导出＝以该流为根的子树（不含祖先、与兄弟无关）
        var member = KernelTestKit.Member("触发", new OwnedEdge((_, _, _) => Task.CompletedTask));
        KernelTestKit.Register(engine, member);
        await engine.InvokeMemberAsync("触发");
        await engine.InvokeMemberAsync("触发");
        var s1 = engine.RootStream.Children[0];
        var s2 = engine.RootStream.Children[1];
        s1.WriteLog("测试源", "s1 本地");
        s2.WriteLog("测试源", "s2 本地");

        var subtreeJson = EventStreamJson.Serialize(s1);
        using var subtreeDoc = JsonDocument.Parse(subtreeJson);

        Assert.Equal(s1.Id, subtreeDoc.RootElement.GetProperty("id").GetString()); // 根＝传入流
        Assert.Single(subtreeDoc.RootElement.GetProperty("entries").EnumerateArray()); // 仅本地条目
        Assert.DoesNotContain(
            subtreeDoc.RootElement.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("message").GetString() == "s2 本地"); // 不含兄弟

        // indented：合法 JSON 且含缩进
        var indented = EventStreamJson.Serialize(engine.RootStream, indented: true);
        using var indentedDoc = JsonDocument.Parse(indented);
        Assert.Equal(engine.RootStream.Id, indentedDoc.RootElement.GetProperty("id").GetString());
        Assert.Contains("\n", indented);
    }

    /// <summary>全树递归统计：指定 message 的条目总出现次数（"恰一次"断言用）。</summary>
    private static int CountTreeEntriesByMessage(JsonElement node, string message)
    {
        var count = 0;
        foreach (var entry in node.GetProperty("entries").EnumerateArray())
        {
            if (entry.GetProperty("message").GetString() == message)
            {
                count++;
            }
        }

        foreach (var childNode in node.GetProperty("children").EnumerateArray())
        {
            count += CountTreeEntriesByMessage(childNode, message);
        }

        return count;
    }

    [Fact]
    public void Cyclic_Data_And_Unknown_Object_Degrade_Deterministically()
    {
        var engine = new LogicEngine();
        var cyclic = new Dictionary<string, object?>();
        cyclic["self"] = cyclic; // 自引用环（防环：深度截断、不爆栈、不报错）

        engine.RootStream.WriteLog(
            "测试源",
            "环条目",
            data: new Dictionary<string, object?>
            {
                ["cycle"] = cyclic,
                ["weird"] = new object(), // 未知对象：仅输出 $type（不递归成员）
            });

        var json = EventStreamJson.Serialize(engine.RootStream); // 不抛、不爆栈
        using var doc = JsonDocument.Parse(json);                // 可解析
        var data = doc.RootElement.GetProperty("entries").EnumerateArray().Single().GetProperty("data");

        Assert.Equal("System.Object", data.GetProperty("weird").GetProperty("$type").GetString());
        Assert.Contains("$truncated", json); // 环被深度截断（确定性处理）
    }
}
