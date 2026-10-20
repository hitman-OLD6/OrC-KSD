using Orc.Client;
using Orc.Game.Cards;
using Orc.Game.Decks;
using Orc.Packages.Loading;
using Orc.Server.Rooms;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S8b 启动链路：bootstrap 起监听并服务 <c>room.info</c>；AI 卡组生成遵守稀有度上限。
/// </summary>
public class BootstrapTests
{
    [Fact]
    public async Task Bootstrap_StartsListener_AndServesRoomInfo()
    {
        using var temp = new TempDirectory();
        await using var host = await ServerBootstrap.StartAsync(new ServerOptions
        {
            PackDirectories = [],
            SandboxRoot = temp.Path,
            Port = 0,
        });

        Assert.True(host.Port > 0);

        await using var client = await OrcClient.ConnectAsync(new ClientOptions
        {
            Host = "127.0.0.1",
            Port = host.Port,
        });

        var info = await client.CallAsync("room.info");

        Assert.True(info.Ok, info.Message);
        Assert.Equal("lobby", info.Details!["state"]!.GetValue<string>());
        Assert.Equal(2, info.Details!["seats"]!.AsArray().Count);
    }

    [Fact]
    public void AiDeckGenerator_RespectsCopyLimits_AndReturnsEmpty_WhenPoolTooSmall()
    {
        var pool = LoadSamplePool();
        var generator = new AiDeckGenerator(pool, new Random(7));

        var deck = generator.Generate(10);

        Assert.Equal(10, deck.Count);
        foreach (var id in deck)
        {
            Assert.True(pool.TryGetValue(id, out var definition), $"'{id}' 不在卡池中。");
            var limit = DeckRules.CopyLimit(definition!.Rarity);
            Assert.True(deck.Count(entry => entry == id) <= limit);
        }

        // 示例卡包 5 张卡，容量不足以凑 39 张 → 明确返回空，而不是生成非法卡组。
        Assert.Empty(generator.Generate(DeckRules.DefaultRequiredSize));
    }

    private static IReadOnlyDictionary<string, CardDefinition> LoadSamplePool()
    {
        var assembly = MatchCardAssembly.Register([SamplePackDirectory()]);
        return assembly.Definitions.ToDictionary(entry => entry.Id, entry => entry.Definition, StringComparer.Ordinal);
    }

    private static string SamplePackDirectory()
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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orc-bootstrap-tests", Guid.NewGuid().ToString("N"));
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
