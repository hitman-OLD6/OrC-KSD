using Orc.Mcp.Sandbox;
using Orc.TestSupport;
using Xunit;

namespace Orc.Mcp.Tests;

/// <summary>
/// 路径守卫测试（R0d：写路径隔离，防污染正式资产）。
/// </summary>
public class SandboxPathGuardTests : IDisposable
{
    private readonly string _root;
    private readonly SandboxPathGuard _guard;

    public SandboxPathGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-mcp-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _guard = new SandboxPathGuard(_root);
    }

    [Fact]
    public void Resolve_RelativePath_StaysInsideRoot()
    {
        var resolved = _guard.Resolve(Path.Combine("pack", "cards", "a.card.json"));

        Assert.StartsWith(_root, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("a.card.json", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("pack/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    public void Resolve_ParentTraversal_IsRejected(string relative)
    {
        var exception = Assert.Throws<SandboxPathViolationException>(() => _guard.Resolve(relative));
        Assert.Contains("越出沙盒根", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AbsolutePath_IsRejected()
    {
        var absolute = OperatingSystem.IsWindows() ? @"C:\Windows\System32\drivers\etc\hosts" : "/etc/passwd";

        var exception = Assert.Throws<SandboxPathViolationException>(() => _guard.Resolve(absolute));
        Assert.Contains("绝对路径", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UncPath_IsRejected()
    {
        var exception = Assert.Throws<SandboxPathViolationException>(() => _guard.Resolve(@"\\server\share\file.txt"));
        Assert.Contains("UNC", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_EmptyPath_IsRejected()
    {
        Assert.Throws<SandboxPathViolationException>(() => _guard.Resolve("   "));
    }

    [Fact]
    public void TryResolve_ReturnsErrorInsteadOfThrowing()
    {
        var ok = _guard.TryResolve("../escape.txt", out var resolved, out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
        Assert.NotNull(error);
    }

    [Fact]
    public void ResolveWrite_UsesSameGuard()
    {
        Assert.Throws<SandboxPathViolationException>(() => _guard.ResolveWrite("../escape.txt"));

        var resolved = _guard.ResolveWrite("pack/manifest.json");
        Assert.StartsWith(_root, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Guard_Is_An_EditableRootPolicy_With_Single_Root()
    {
        // S8：沙盒守卫只是 IEditableRootPolicy 的一个实现（单根），路径规则只有一份（PathNormalizer）。
        Orc.Packages.Fs.IEditableRootPolicy policy = _guard;

        Assert.Single(policy.Roots);
        Assert.True(policy.Approve(Path.Combine(_root, "pack", "a.card.json")));
        Assert.False(policy.Approve(Path.Combine(Path.GetTempPath(), "outside.card.json")));
        Assert.False(policy.TryApprove(Path.Combine(_root, "..", "escape.txt"), out _));
    }

    [ReparsePointFact]
    public void Resolve_Symlink_Escape_Is_Rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "orc-mcp-guard-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_root, "link");
        Assert.True(ReparsePointProbe.TryCreate(link, outside, out var reason), reason);

        try
        {
            var exception = Assert.Throws<SandboxPathViolationException>(() => _guard.Resolve("link/evil.txt"));
            Assert.Contains("越出沙盒根", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 忽略临时目录清理失败。
        }
    }
}
