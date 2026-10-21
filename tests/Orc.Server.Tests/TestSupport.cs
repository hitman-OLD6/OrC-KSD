using System.Text.Json.Nodes;
using Orc.Client;
using Orc.Protocol;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// 服务端测试公共夹具：样例卡包定位、卡组构造、真 socket 连接、临时目录。
/// <para>只放"与断言无关的准备工作"，任何断言都必须留在用例体内。</para>
/// </summary>
internal static class TestSupport
{
    /// <summary>记录卡包的 id（示例卡包唯一兵种）。</summary>
    public const string SampleCardId = "ai.sample.infantry";

    public static string SamplePackDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "card-pack-ai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到 samples/card-pack-ai。");
    }

    public static JsonArray Deck(int size = 39)
    {
        var deck = new JsonArray();
        for (var i = 0; i < size; i++)
        {
            deck.Add(SampleCardId);
        }

        return deck;
    }

    public static async Task<OrcClient> ConnectAsync(
        int port,
        string host = "127.0.0.1",
        TimeSpan? heartbeatInterval = null,
        TimeSpan? heartbeatTimeout = null)
    {
        var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = host,
            Port = port,
            HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(30),
            HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(15),
        });
        await client.InitializeAsync(new InitializeParams { ClientName = "server-tests" });
        return client;
    }

    /// <summary>声明座位（缺省＝房主/本地）。</summary>
    public static JsonObject Claim(int seat, string kind = "local", string name = "玩家")
        => new()
        {
            ["seat"] = seat,
            ["kind"] = kind,
            ["name"] = name,
            ["deck"] = Deck(),
        };

    /// <summary>取工具结果里的座位令牌（失败即断言失败，避免 null 传播）。</summary>
    public static string SeatToken(ToolResultPayload result)
    {
        Assert.True(result.Ok, result.Message);
        return result.Details!["seatToken"]!.GetValue<string>();
    }

    /// <summary>
    /// 读取仍在写入中的日志文件（<c>FileShare.ReadWrite</c>）：无头日志由运行中的服务端持有写句柄，
    /// 默认的 <see cref="File.ReadAllLines(string)"/> 会因共享冲突失败。
    /// </summary>
    public static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    /// <summary>等待条件成立（轮询）；超时返回最后值（由用例决定是否断言失败）。</summary>
    public static async Task<T> WaitUntilAsync<T>(
        Func<Task<T>> probe,
        Func<T, bool> predicate,
        TimeSpan timeout,
        bool throwOnTimeout = false)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var last = default(T)!;

        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        if (throwOnTimeout)
        {
            throw new TimeoutException($"等待条件超时（最后值：{last}）。");
        }

        return last;
    }

    /// <summary>临时目录（用例结束删除；失败不影响结论）。</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string prefix = "orc-server-tests")
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响结论。
            }
        }
    }
}
