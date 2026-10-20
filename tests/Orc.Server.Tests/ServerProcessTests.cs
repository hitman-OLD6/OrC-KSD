using Orc.Client;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S8e 真进程冒烟：用 <c>dotnet run</c> 拉起服务端，解析 stdout 就绪行，连接后回收子进程。
/// </summary>
public class ServerProcessTests
{
    [Fact]
    public async Task ServerProcess_Starts_Connects_AndIsReclaimed()
    {
        var repoRoot = FindRepositoryRoot();
        var serverProject = Path.Combine(repoRoot, "src", "Orc.Server", "Orc.Server.csproj");
        Assert.True(File.Exists(serverProject), $"未找到服务端工程：{serverProject}");

        var root = Path.Combine(Path.GetTempPath(), "orc-process-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var server = await ServerProcess.StartAsync(new ServerProcessOptions
            {
                ExecutablePath = "dotnet",
                Arguments =
                [
                    "run", "--project", serverProject, "--no-build", "--",
                    "--port", "0",
                    "--sandbox", Path.Combine(root, "sandbox"),
                ],
                WorkingDirectory = repoRoot,
                LogPath = Path.Combine(root, "server.log"),
                StartupTimeout = TimeSpan.FromSeconds(90),
            });

            try
            {
                Assert.True(server.Port > 0);

                await using var client = await OrcClient.ConnectAsync(new ClientOptions
                {
                    Host = "127.0.0.1",
                    Port = server.Port,
                });

                var info = await client.CallAsync("room.info");
                Assert.True(info.Ok, info.Message);
                Assert.Equal("lobby", info.Details!["state"]!.GetValue<string>());
            }
            finally
            {
                await server.DisposeAsync();
            }

            Assert.True(server.HasExited);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ParentWatchdog_DetectsDeadParent()
    {
        Assert.True(ParentWatchdog.IsAlive(Environment.ProcessId));
        Assert.False(ParentWatchdog.IsAlive(int.MaxValue));
    }

    [Fact]
    public async Task Server_Exits_WhenParentProcessIsGone()
    {
        var repoRoot = FindRepositoryRoot();
        var serverProject = Path.Combine(repoRoot, "src", "Orc.Server", "Orc.Server.csproj");
        var root = Path.Combine(Path.GetTempPath(), "orc-watchdog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var server = await ServerProcess.StartAsync(new ServerProcessOptions
            {
                ExecutablePath = "dotnet",
                Arguments =
                [
                    "run", "--project", serverProject, "--no-build", "--",
                    "--port", "0",
                    "--sandbox", Path.Combine(root, "sandbox"),
                    "--parent-pid", int.MaxValue.ToString(),
                ],
                WorkingDirectory = repoRoot,
                LogPath = Path.Combine(root, "server.log"),
                StartupTimeout = TimeSpan.FromSeconds(90),
            });

            try
            {
                Assert.True(server.Port > 0);

                // 父 PID 不存在 → watchdog 在 ~2 秒内让服务端退出。
                var exitCode = await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(0, exitCode);
            }
            finally
            {
                await server.DisposeAsync();
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OrcEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("未找到仓库根（OrcEngine.slnx）。");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响结论。
        }
    }
}
