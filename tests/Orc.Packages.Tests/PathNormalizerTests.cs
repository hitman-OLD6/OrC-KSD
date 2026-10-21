using Orc.Packages.Fs;
using Orc.TestSupport;
using Xunit;

namespace Orc.Packages.Tests;

/// <summary>
/// S8 验收：<see cref="PathNormalizer"/> 是**全仓唯一的路径归一实现**——
/// realpath 归一、拒绝 UNC／<c>..</c>／相对路径、大小写归一、符号链接解析；
/// <see cref="IEditableRootPolicy"/> 只做「直接父目录命中授权根之一」的纯判定。
/// </summary>
public class PathNormalizerTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;

    public PathNormalizerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-path-" + Guid.NewGuid().ToString("N"));
        _outside = Path.Combine(Path.GetTempPath(), "orc-path-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    [Theory]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    public void Unc_Is_Rejected(string path)
    {
        var exception = Assert.Throws<PathViolationException>(() => PathNormalizer.Normalize(path));
        Assert.Contains("UNC", exception.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Parent_Traversal_Is_Rejected()
    {
        var path = Path.Combine(_root, "..", "escape.txt");

        var exception = Assert.Throws<PathViolationException>(() => PathNormalizer.Normalize(path));
        Assert.Contains("..", exception.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_Path_Is_Rejected()
    {
        var exception = Assert.Throws<PathViolationException>(() => PathNormalizer.Normalize("pack/cards/a.card.json"));
        Assert.Contains("相对路径", exception.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_Is_Case_Insensitive_On_Windows()
    {
        var a = PathNormalizer.Normalize(Path.Combine(_root, "Packs", "Demo"));
        var b = PathNormalizer.Normalize(Path.Combine(_root, "packs", "demo"));

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }
        else
        {
            Assert.NotEqual(a, b);
        }
    }

    [Fact]
    public void Combine_Rejects_Escape_And_Rooted()
    {
        var root = PathNormalizer.Normalize(_root);

        Assert.Throws<PathViolationException>(() => PathNormalizer.Combine(root, "../x"));
        Assert.Throws<PathViolationException>(() => PathNormalizer.Combine(root, Path.Combine(_root, "x")));
        Assert.Equal(
            Path.Combine(_root, "pack", "a.json"),
            PathNormalizer.Combine(root, "pack/a.json").Value);
    }

    [Fact]
    public void Policy_Approves_Child_Paths_But_Not_The_Root_Itself()
    {
        var policy = new EditableRootPolicy(_root);
        var child = Path.Combine(_root, "packs", "demo.tmp", "cards", "a.card.json");

        Assert.True(policy.Approve(child));
        Assert.False(policy.Approve(_root)); // 授权单位＝直接父目录：根本身不在判定范围内
        Assert.False(policy.Approve(Path.Combine(_outside, "a.card.json")));
    }

    [Fact]
    public void Policy_Rejects_Ungranted_Parent_With_Reason()
    {
        var policy = new EditableRootPolicy(_root);
        var path = Path.Combine(_outside, "a.card.json");

        Assert.False(policy.TryApprove(path, out var reason));
        Assert.Contains("父目录", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_Roots_Are_Fixed_At_Construction()
    {
        var policy = new EditableRootPolicy(_root);

        var roots = policy.Roots;
        Assert.Single(roots);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(roots);
        // 无任何 AddRoot/Append 入口：授权根在会话期不得扩权（接口只有 Approve/TryApprove）。
        Assert.Null(typeof(IEditableRootPolicy).GetMethod("AddRoot"));
    }

    [ReparsePointFact]
    public void Symlink_Escape_Is_Normalized_Outside_And_Denied()
    {
        var link = Path.Combine(_root, "link");
        Assert.True(ReparsePointProbe.TryCreate(link, _outside, out var reason), reason);

        var target = Path.Combine(link, "a.card.json");
        Assert.False(PathNormalizer.Normalize(target).IsUnderOrEqual(PathNormalizer.Normalize(_root)));

        var policy = new EditableRootPolicy(_root);
        Assert.False(policy.Approve(target));
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _outside })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // 忽略临时目录清理失败。
            }
        }
    }
}
