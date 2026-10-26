namespace MouseWheelEnhancement.Update;

/// <summary>更新流程所处的阶段，界面据此显示一句人话。</summary>
public enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Verifying,
    Applying,
    Done,
    Failed,
    Cancelled,
}

/// <summary>一次"检查更新"的结果。</summary>
public sealed class UpdateInfo
{
    /// <summary>远端最新版本号（**不带 v**，例如 <c>1.2.0</c>）。</summary>
    public string Version { get; set; } = "";

    /// <summary>远端 tag（带 v，例如 <c>v1.2.0</c>）。</summary>
    public string Tag { get; set; } = "";

    /// <summary>有没有比当前新。</summary>
    public bool Newer { get; set; }

    /// <summary>当前版本（由调用方填）。</summary>
    public string Current { get; set; } = "";

    /// <summary>版本说明（Release body，可能为空）。</summary>
    public string Notes { get; set; } = "";

    /// <summary>更新包下载地址（<c>browser_download_url</c>）。</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// 同一资产的 API 入口（<c>api.github.com/repos/.../releases/assets/{id}</c>）。
    /// 两条地址最终都 302 到同一个 CDN，但**入口域名不同**；
    /// 实测 `github.com` 会间歇性 502 而 `api.github.com` 仍可通，
    /// 只留一条路就会出现"检测到新版却总下载失败"。
    /// </summary>
    public string AssetApiUrl { get; set; } = "";

    /// <summary>更新包体积（字节，0 = 远端没给）。</summary>
    public long Size { get; set; }

    /// <summary>更新包的 SHA256（从 Release 说明里解析；解析不到为空串）。</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>这个 release 的网页地址。</summary>
    public string Page { get; set; } = "";

    /// <summary>这次检查**实际**用上了令牌没有（诊断 + 界面提示用）。</summary>
    public bool Authed { get; set; }

    /// <summary>令牌有问题时的提示（降级成匿名请求时产生，界面显示一次）。</summary>
    public string? AuthWarning { get; set; }

    public override string ToString() => $"v{Version} (newer={Newer}, size={Size})";
}

/// <summary>令牌验证结果。</summary>
public sealed class TokenCheck
{
    public string Login { get; set; } = "";
    public string Name { get; set; } = "";
    public long Limit { get; set; }
    public long Remaining { get; set; }
    public string Kind { get; set; } = "";

    public string Display =>
        string.IsNullOrEmpty(Name)
            ? $"@{Login}（{Kind}）· 本小时剩余 {Remaining}/{Limit} 次"
            : $"{Name} @{Login}（{Kind}）· 本小时剩余 {Remaining}/{Limit} 次";
}

/// <summary>接口额度快照。</summary>
public sealed class RateState
{
    public bool Authed { get; set; }
    public long Limit { get; set; }
    public long Remaining { get; set; }
    public long Reset { get; set; }
    public long Used => Math.Max(0, Limit - Remaining);
}

/// <summary>下载进度。</summary>
public readonly record struct DownloadProgress(long Done, long Total, UpdateStage Stage, string Message)
{
    public double Percent => Total > 0 ? Math.Clamp(Done * 100.0 / Total, 0, 100) : 0;
}

/// <summary>GitHub 接口错误的分类 —— 三类对用户的意义完全不同，必须分开。</summary>
public enum ApiErrorKind
{
    /// <summary>令牌被拒（401/403 且 body 明说令牌问题）→ 可以脱掉令牌重试。</summary>
    AuthRejected,

    /// <summary>限流（要告诉用户等多久、以及怎么彻底解决）。</summary>
    RateLimited,

    /// <summary>其它（已经翻成人话）。</summary>
    Other,
}

public sealed class ApiException : Exception
{
    public ApiErrorKind Kind { get; }
    public long? ResetEpoch { get; }

    public ApiException(ApiErrorKind kind, string message, long? resetEpoch = null) : base(message)
    {
        Kind = kind;
        ResetEpoch = resetEpoch;
    }
}
