using Orc.Core;
using Xunit;

namespace Orc.HandlerDsl.Tests;

/// <summary>
/// 契约包里的 <c>check</c> 向量：后端跑一遍，结论必须与 <c>expect</c> **逐字相等**
/// （一个码 ＋ 一个节点路径；SPEC §15）。前端将来跑同一批文件，两侧结论必须一致（R26 第 3 条）。
/// </summary>
public class VectorTests
{
    public static TheoryData<string> CheckVectorFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var vector in Fixtures.LoadCheckVectors())
            {
                data.Add(vector.File);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CheckVectorFiles))]
    public void check向量_后端结论与期望一致(string file)
    {
        var vector = Fixtures.LoadCheckVectors().Single(v => v.File == file);

        // 走**装载期同一条链**：读取（结构）＋ 类型／作用域／绑定 ＋ 视图可写性（S6 的校验器）。
        // 只跑 Reader＋TypeChecker 会漏掉 write-forbidden（那是校验器补的条款）。
        var errors = HandlerDslValidator.Validate(vector.DocumentJson, vector.Catalog, vector.View);

        var actual = string.Join(
            "；",
            errors.Select(e => $"({e.Code}, {e.NodePath})"));

        var error = Assert.Single(errors);
        Assert.Equal(vector.Code, error.Code);
        Assert.Equal(vector.NodePath, error.NodePath);
        Assert.Equal($"({vector.Code}, {vector.NodePath})", actual);
    }

    [Fact]
    public void 向量集覆盖多个错误码_不只测成功路径()
    {
        var codes = Fixtures.LoadCheckVectors().Select(v => v.Code).ToHashSet(StringComparer.Ordinal);
        Assert.True(codes.Count >= 4, $"check 向量的覆盖码过少：{string.Join("／", codes)}");
        Assert.All(codes, code => Assert.True(HandlerDslErrorCodes.IsKnown(code), $"向量用了封闭集外的码：{code}"));
    }

    [Fact]
    public void 每条向量的期望都是单码单路径()
    {
        foreach (var vector in Fixtures.LoadCheckVectors())
        {
            Assert.False(string.IsNullOrEmpty(vector.Code));
            Assert.False(string.IsNullOrEmpty(vector.NodePath));
            Assert.DoesNotContain(",", vector.Code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 契约包里的每个错误码都至少有一条拒绝向量()
    {
        // R26 第 2 条：13 个码**每个**至少一条拒绝 vector。S9 已收口，故这里是「零缺失」的硬断言。
        var covered = Fixtures.LoadCheckVectors().Select(v => v.Code).ToHashSet(StringComparer.Ordinal);
        var missing = HandlerDslErrorCodes.All.Where(code => !covered.Contains(code)).ToArray();

        Assert.All(covered, code => Assert.True(HandlerDslErrorCodes.IsKnown(code), $"向量用了封闭集外的码：{code}"));
        Assert.True(missing.Length == 0, $"以下错误码缺少拒绝向量：{string.Join("／", missing)}");
    }

    [Fact]
    public void 每条向量的目录快照自足_不依赖运行期扫描()
    {
        foreach (var vector in Fixtures.LoadCheckVectors())
        {
            Assert.True(vector.Catalog.Revision > 0, $"{vector.File} 的快照缺 catalogRevision");
            Assert.NotEmpty(vector.Catalog.Nodes);
            Assert.NotEmpty(vector.Catalog.Readables);
        }
    }
}
