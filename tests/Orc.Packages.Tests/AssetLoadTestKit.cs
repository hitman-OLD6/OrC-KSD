using System.Security.Cryptography;
using System.Text;
using Orc.Core;
using Orc.Packages;

namespace Orc.Packages.Tests;

/// <summary>
/// S5 测试夹具：注册假资产（元数据 ＋ GUID 派生）、构造 lock、记录装载面。
/// </summary>
internal static class AssetLoadTestKit
{
    /// <summary>按包 id 确定性派生包稳定身份（正式／测试包各得一个，互不相同的 GUID）。</summary>
    internal static Guid PackageGuidOf(string packageId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(packageId));
        return new Guid(bytes.AsSpan(0, 16));
    }

    /// <summary>注册一条资产并返回注册条目（GUID 由身份派生，可直接进 lock）。</summary>
    internal static AssetRegistration Register(
        AssetRegistry registry,
        string packageId,
        string localId,
        AssetKind kind = AssetKind.Card,
        string version = "1.0.0",
        AssetDomain domain = AssetDomain.Formal,
        string? anchor = null,
        string? sourcePath = null,
        params AssetReference[] dependsOn)
    {
        var parsed = AssetVersion.Parse(version);
        var guid = AssetIdentity.DeriveAssetGuid(PackageGuidOf(packageId), kind, localId, parsed);
        var registration = new AssetRegistration(
            new AssetMetadata(guid, kind, localId, parsed, anchor, Loader: null, dependsOn, []),
            domain,
            packageId,
            AssetAppId.Compose(packageId, localId),
            AssetMetadataSource.Sidecar,
            Card: null,
            Prefab: null,
            SourcePath: sourcePath)
        {
            PackageGuid = PackageGuidOf(packageId),
        };
        registry.Register(registration);
        return registration;
    }

    /// <summary>注册条目 → lock 条目。</summary>
    internal static AssetLockEntry Entry(AssetRegistration registration)
        => new(registration.AssetGuid, registration.Kind, registration.AppId, registration.Version);

    /// <summary>正式 lock（包 id ＋ 本包资产）。</summary>
    internal static AssetLock FormalLock(string packageId, params AssetRegistration[] assets)
        => AssetLock.Create(packageId, AssetDomain.Formal, assets.Select(Entry));

    /// <summary>测试 lock（包 id ＋ 本包资产）。</summary>
    internal static AssetLock TestLock(string packageId, params AssetRegistration[] assets)
        => AssetLock.Create(packageId, AssetDomain.Test, assets.Select(Entry));

    /// <summary>依赖声明（同包裸局部 ID 或全名皆可）。</summary>
    internal static AssetReference Depends(AssetKind kind, string appIdOrLocalId, string constraint = "1.*.*")
        => new(kind, appIdOrLocalId, constraint);

    /// <summary>装载面记录器：Apply 成功才落到 <see cref="Applied"/>（用于断言「全有或全无」）。</summary>
    internal sealed class RecordingSink : IAssetLoadSink
    {
        private readonly List<string> _applied = new();

        /// <summary>已落地的应用层 ID（装载面现状）。</summary>
        public IReadOnlyList<string> Applied => _applied;

        /// <summary>调用序（<c>apply:…</c>／<c>revert:…</c>）。</summary>
        public List<string> Calls { get; } = new();

        /// <summary>本批含该版本时抛错（模拟装载失败）。</summary>
        public string? FailOnApplyVersion { get; set; }

        /// <inheritdoc />
        public void Apply(IReadOnlyList<LoadListItem> items)
        {
            Calls.Add("apply:" + string.Join(",", items.Select(item => item.AppId + "#" + item.Version)));
            if (FailOnApplyVersion is { } failure
                && items.Any(item => string.Equals(item.Version.ToString(), failure, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"装载版本 {failure} 失败（测试注入）。");
            }

            _applied.AddRange(items.Select(item => item.AppId));
        }

        /// <inheritdoc />
        public void Revert(IReadOnlyList<LoadListItem> items)
        {
            Calls.Add("revert:" + string.Join(",", items.Select(item => item.AppId + "#" + item.Version)));
            foreach (var item in items)
            {
                _applied.Remove(item.AppId);
            }
        }
    }

    /// <summary>记录请求的放行门禁。</summary>
    internal sealed class RecordingGate : ReloadGateJudicator
    {
        /// <summary>收到的请求。</summary>
        public List<ReloadRequest> Requests { get; } = new();

        /// <inheritdoc />
        protected override ReloadVerdict Evaluate(ReloadRequest request)
        {
            Requests.Add(request);
            return ReloadVerdict.Allow;
        }
    }

    /// <summary>固定拒绝的门禁。</summary>
    internal sealed class DenyingGate(string reason) : ReloadGateJudicator
    {
        /// <inheritdoc />
        protected override ReloadVerdict Evaluate(ReloadRequest request) => ReloadVerdict.Reject(reason);
    }
}
