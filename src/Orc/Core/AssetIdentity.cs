using System.Security.Cryptography;
using System.Text;

namespace Orc.Core;

/// <summary>
/// 资产种类（R1/D4）：card／effect／trigger／handler。与「加载方/来源」（<see cref="AssetLoadKind"/>）不同——
/// 后者是文件级身份，本类型是**资产身份**的一等分型。
/// </summary>
public enum AssetKind
{
    /// <summary>卡牌。</summary>
    Card = 0,

    /// <summary>效果（预制体）。</summary>
    Effect = 1,

    /// <summary>触发器。</summary>
    Trigger = 2,

    /// <summary>handler（含内含 handler 条目）。</summary>
    Handler = 3,
}

/// <summary>资产种类 token（规范小写：<c>card</c>／<c>effect</c>／<c>trigger</c>／<c>handler</c>）读写。</summary>
public static class AssetKindTokens
{
    /// <summary>规范 token。</summary>
    /// <exception cref="ArgumentOutOfRangeException">未定义种类。</exception>
    public static string Token(AssetKind kind) => kind switch
    {
        AssetKind.Card => "card",
        AssetKind.Effect => "effect",
        AssetKind.Trigger => "trigger",
        AssetKind.Handler => "handler",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未定义的资产种类。"),
    };

    /// <summary>解析 token（大小写不敏感；非法＝false）。</summary>
    public static bool TryParse(string? token, out AssetKind kind)
    {
        switch (token?.Trim().ToLowerInvariant())
        {
            case "card": kind = AssetKind.Card; return true;
            case "effect": kind = AssetKind.Effect; return true;
            case "trigger": kind = AssetKind.Trigger; return true;
            case "handler": kind = AssetKind.Handler; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>
/// 资产版本身份（R1/D28）：<c>(包稳定身份, 资产种类, 局部 ID, 资产版本)</c> → GUID。
/// <para>
/// GUID **不含包版本**：包内容变化不改资产身份，只有**资产版本**变化才换 GUID；
/// 版本按规范形参与派生（<c>1.2</c> 与 <c>1.2.0</c> 得同一 GUID）。
/// </para>
/// </summary>
/// <param name="PackageGuid">所属包稳定身份（<c>manifest.guid</c>；非人类可读包 id）。</param>
/// <param name="Kind">资产种类。</param>
/// <param name="LocalId">包内局部 ID。</param>
/// <param name="Version">资产版本。</param>
public readonly record struct AssetIdentity(Guid PackageGuid, AssetKind Kind, string LocalId, AssetVersion Version)
{
    /// <summary>派生 GUID（确定性：同一身份恒得同一 GUID；跨进程稳定）。</summary>
    public Guid AssetGuid => DeriveAssetGuid(PackageGuid, Kind, LocalId, Version);

    /// <summary>
    /// 派生资产 GUID：<c>SHA-256(包GUID + 种类 + 局部ID + 规范形版本)</c> 前 16 字节。
    /// </summary>
    /// <exception cref="ArgumentNullException">localId 为 null。</exception>
    /// <exception cref="ArgumentException">localId 为空白。</exception>
    public static Guid DeriveAssetGuid(Guid packageGuid, AssetKind kind, string localId, AssetVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);

        var payload = string.Concat(
            packageGuid.ToString("N"),
            "\n",
            AssetKindTokens.Token(kind),
            "\n",
            localId,
            "\n",
            CanonicalVersion(version));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>规范形版本串（右补 0 至 8 段；仅用于派生输入与比对，**不得**作为展示串）。</summary>
    public static string CanonicalVersion(AssetVersion version)
    {
        var builder = new StringBuilder(16);
        for (var i = 0; i < AssetVersion.MaxSegments; i++)
        {
            if (i > 0)
            {
                builder.Append('.');
            }

            builder.Append(version.SegmentAt(i));
        }

        return builder.ToString();
    }

    /// <summary>资产全名（R6）：<c>{种类}：{局部ID}@{包GUID8}#{版本}</c>（写出用书写段数）。</summary>
    /// <exception cref="ArgumentNullException">packageGuid8 为 null。</exception>
    public string FormatName(string packageGuid8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageGuid8);
        return $"{AssetKindTokens.Token(Kind)}{AssetName.CanonicalSeparator}{LocalId}@{packageGuid8}#{Version}";
    }
}
