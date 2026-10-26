using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MouseWheelEnhancement.Core;

namespace MouseWheelEnhancement.Update;

/// <summary>检查更新的结果：要么拿到 <see cref="Info"/>，要么拿到一句人话错误。</summary>
public sealed record CheckOutcome(UpdateInfo? Info, string? Error)
{
    public bool Ok => Info != null;
}

/// <summary>
/// 自动更新（对接 GitHub Releases）。
///
/// # 用户口径（照抄参考项目的验收标准）
///
/// | 场景 | 行为 |
/// |---|---|
/// | 勾了开关 + 启动 | 后台查一次最新 tag，**静默**，没新版什么都不弹 |
/// | 点「检查更新」 | 立刻查一次，**无论有没有新版都给回话** |
/// | 发现新版 | 提示版本号 + 说明 + 体积，问要不要下载 |
/// | 点下载 | **在程序内**下载 → 校验 SHA256 → 替换旧 exe → 提示重启 |
///
/// # 令牌怎么用
///
/// 填了令牌 → 直接打 <c>releases/latest</c>（认证请求 5000 次/小时，一次调用忽略不计）；
/// 没填令牌 → 先请求 `github.com/{repo}/releases/latest` **不跟随重定向**，
/// 从 302 的 <c>Location</c> 里抠出版本号。这一步**不计入 API 限流**，
/// 所以"日常已是最新版"的情况一次额度都不花。
///
/// 令牌被拒（401/403 且 body 明说令牌问题）时**自动降级成匿名请求**再查一遍，
/// 并把提示带回界面 —— 不能让用户填错一个字符就整个更新功能坏掉。
///
/// # Windows 上"替换正在运行的程序"
///
/// 正在运行的 exe 被锁住（Windows 锁的是"删除/写入"，**不锁改名**），所以：
///   1. 新 exe 下到同目录的 <c>MouseWheelEnhancement.exe.new</c>（必须同目录，跨盘 rename 会失败）；
///   2. 校验 SHA256 与体积；
///   3. <c>MoveFileEx(当前exe → 当前exe.old)</c>；
///   4. <c>MoveFileEx(.new → 正式名)</c>；第 4 步失败就把第 3 步改回去。
/// `.old` 在下次启动时清掉（那时已经没人占着它了）。
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// **远端资产名**（发版时上传的文件名）。
    ///
    /// 刻意没有跟着本地文件名改成 <c>MouseWheelEnhancement.App.exe</c>：
    /// v1.1.0 的客户端会按这个名字去找更新包，改名会让它们全部更新失败
    /// （报"这个版本里没有更新包"）。本地文件名与它无关 —— 见 <see cref="StagedPath"/>。
    /// </summary>
    public const string AssetName = "MouseWheelEnhancement.exe";

    private const string DefaultRepo = "QQQOrange/Mouse-wheel-enhancement";

    private static readonly string UserAgent =
        $"MouseWheelEnhancement/{CurrentVersion} (Windows; +https://github.com/{DefaultRepo})";

    private readonly ConfigStore _store;

    private readonly HttpClient _api;
    private readonly HttpClient _noRedirect;
    private readonly HttpClient _download;

    public UpdateService(ConfigStore store)
    {
        _store = store;

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.All,
        };
        _api = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        _api.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _api.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        _noRedirect = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,
        })
        { Timeout = TimeSpan.FromSeconds(15) };
        _noRedirect.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        /* 下载专用的 client：**不设总超时**。
         * 设了总超时就会出现"能检测到新版、但下载总失败"—— 大文件在慢网下
         * 必然被自己掐断，而报错还长得像网络问题。改成只限制"两块数据之间的空等"，
         * 见 FetchIntoAsync 里的 idle 超时。 */
        var dl = new SocketsHttpHandler
        {
            /* 20 秒而不是 10 秒：发布资产会 302 到 release-assets.githubusercontent.com，
             * 走代理时首次连接（代理侧要重新解析/建隧道）经常超过 10 秒，直接判失败
             * 会让"检查更新一切正常、点下载就报错"这种最难受的状态反复出现。 */
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.None, // Range + 解压混在一起容易算错字节
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _download = new HttpClient(dl) { Timeout = Timeout.InfiniteTimeSpan };
        _download.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /* ══════════════ 当前版本 / 路径 ══════════════ */

    public static string CurrentVersion => Log.Version;

    public string Repo => AppConfig.NormalizeRepo(_store.Config.Update.Repo);

    public string RepoUrl => $"https://github.com/{Repo}";
    public string ReleasesUrl => $"https://github.com/{Repo}/releases";

    public static string ExePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, AssetName);

    public static string ExeDir => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// 更新包落点：**和当前 exe 同目录、并以当前 exe 的名字为前缀**。
    /// 替换时要在这个目录里做两次改名，跨盘会失败；而以自己名字为前缀，
    /// 就等于"启动器版（.App.exe）"和"旧单文件版"各自落在各自的位置上。
    /// </summary>
    public static string StagedPath => ExePath + ".new";

    public static string OldPath => ExePath + ".old";

    /// <summary>程序所在目录能不能写 —— 装在 Program Files 且非管理员时不能。</summary>
    public bool CanSelfUpdate(out string reason)
    {
        reason = "";
        try
        {
            var probe = Path.Combine(ExeDir, ".wb-write-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"程序目录不可写（{ex.Message}）—— 装到 Program Files 时请以管理员身份运行，" +
                     "或把程序放到用户目录下";
            return false;
        }
    }

    /* ══════════════ 端点（带测试逃生口） ══════════════ */

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    public string LatestApi => Env("MWE_UPDATE_API") ?? $"https://api.github.com/repos/{Repo}/releases/latest";
    public string LatestWeb => Env("MWE_UPDATE_WEB") ?? $"https://github.com/{Repo}/releases/latest";
    public string UserApi => Env("MWE_GITHUB_USER_API") ?? "https://api.github.com/user";
    public string RateApi => Env("MWE_GITHUB_RATE_API") ?? "https://api.github.com/rate_limit";

    public string AssetApiUrl(long assetId)
    {
        var b = Env("MWE_GITHUB_ASSET_API") ?? $"https://api.github.com/repos/{Repo}/releases/assets";
        return $"{b.TrimEnd('/')}/{assetId}";
    }

    /* ══════════════ 版本号 ══════════════ */

    /// <summary>`1.2.10` → 可比较的数字三元组。解析不了的段按 0。</summary>
    public static (long Major, long Minor, long Patch) ParseVersion(string s)
    {
        var t = (s ?? "").Trim().TrimStart('v', 'V');
        var parts = t.Split('.', '-', '+');
        long At(int i) => i < parts.Length && long.TryParse(parts[i], out var v) ? v : 0;
        return (At(0), At(1), At(2));
    }

    /// <summary>远端是不是比当前新。**解析失败一律判"不新"**，宁可漏一次提示也不要天天催升级。</summary>
    public static bool IsNewer(string remote, string current)
    {
        var r = ParseVersion(remote);
        var c = ParseVersion(current);
        if (r.Major == 0 && r.Minor == 0 && r.Patch == 0) return false;
        return r.CompareTo(c) > 0;
    }

    /// <summary>从 Release 说明里抠出 <c>SHA256: &lt;64 位十六进制&gt;</c>。找不到返回空串。</summary>
    public static string ShaFromNotes(string notes)
    {
        if (string.IsNullOrEmpty(notes)) return "";
        var i = notes.IndexOf("sha256", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var cur = new StringBuilder(64);
        for (var k = i; k < notes.Length; k++)
        {
            var ch = notes[k];
            if (Uri.IsHexDigit(ch))
            {
                cur.Append(ch);
                if (cur.Length == 64) return cur.ToString().ToUpperInvariant();
            }
            else if (cur.Length > 0) cur.Clear();
        }
        return "";
    }

    /* ══════════════ 检查更新 ══════════════ */

    public async Task<CheckOutcome> CheckAsync(string current, CancellationToken ct = default)
    {
        var token = _store.CurrentToken();
        if (token != null)
        {
            try
            {
                using var doc = await ApiLatestAsync(token, ct);
                return new CheckOutcome(BuildInfo(doc.RootElement, current, true), null);
            }
            catch (ApiException e) when (e.Kind == ApiErrorKind.AuthRejected)
            {
                Log.Warn("update: 令牌被 GitHub 拒了（不是限流）—— 退回匿名请求");
                var o = await CheckAnonymousAsync(current, ct);
                if (o.Info != null)
                    o.Info.AuthWarning =
                        "GitHub 令牌无效、已过期或没有该仓库权限 —— 已改用匿名请求（每小时 60 次额度），" +
                        "可在「更新」页重新验证并保存令牌";
                return o;
            }
            catch (ApiException e)
            {
                return new CheckOutcome(null, e.Message);
            }
            catch (Exception e)
            {
                return new CheckOutcome(null, Explain(e));
            }
        }
        return await CheckAnonymousAsync(current, ct);
    }

    /// <summary>没令牌那条路：先花 0 额度判版本，确认有新版才调 API 拿详情。</summary>
    private async Task<CheckOutcome> CheckAnonymousAsync(string current, CancellationToken ct)
    {
        string tag;
        try
        {
            tag = await LatestTagAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Warn($"update: 302 拿 tag 失败（{ex.Message}），退回用 API");
            try
            {
                using var doc = await ApiLatestAsync(null, ct);
                tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (tag.Length == 0) return new CheckOutcome(null, "这次响应里没有版本号（tag_name 缺失）");
                return new CheckOutcome(BuildInfo(doc.RootElement, current, false), null);
            }
            catch (ApiException ae) { return new CheckOutcome(null, ae.Message); }
            catch (Exception e2) { return new CheckOutcome(null, Explain(e2)); }
        }

        var version = tag.TrimStart('v', 'V');
        if (!IsNewer(version, current))
            return new CheckOutcome(UpToDateInfo(version, tag, current), null);

        try
        {
            using var doc = await ApiLatestAsync(null, ct);
            return new CheckOutcome(BuildInfo(doc.RootElement, current, false), null);
        }
        catch (ApiException e) { return new CheckOutcome(null, e.Message); }
        catch (Exception e) { return new CheckOutcome(null, Explain(e)); }
    }

    private static UpdateInfo UpToDateInfo(string version, string tag, string current) => new()
    {
        Version = version,
        Tag = string.IsNullOrEmpty(tag) ? "v" + version : tag,
        Current = current,
        Newer = false,
        Authed = false,
    };

    /// <summary>
    /// 从 <c>github.com/{repo}/releases/latest</c> 的 302 <c>Location</c> 里拿 tag。
    /// **不计入 API 限流**，是最省额度的一条路。
    /// </summary>
    private async Task<string> LatestTagAsync(CancellationToken ct)
    {
        using var resp = await _noRedirect.GetAsync(LatestWeb, HttpCompletionOption.ResponseHeadersRead, ct);
        var code = (int)resp.StatusCode;
        if (code is >= 300 and < 400)
        {
            var loc = resp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(loc)) throw new Exception("重定向响应里没有 Location");

            /* 必须从 `.../releases/tag/v1.2.3` 里取「tag 之后那一段」。
             * 不能直接取最后一段：仓库**一次发布都没有**时，GitHub 会重定向到
             * `.../releases`，最后一段就是字面量 "releases"，被当成版本号之后
             * 解析成 0.0.0 → 判定"已是最新"，用户永远看不到"还没有正式版本"这个真相。 */
            var parts = loc.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var tagIndex = Array.FindLastIndex(parts,
                p => p.Equals("tag", StringComparison.OrdinalIgnoreCase));
            if (tagIndex < 0 || tagIndex + 1 >= parts.Length)
                throw new Exception($"重定向地址里没有版本标签（{loc}）");

            var tag = parts[tagIndex + 1];
            if (tag.Length == 0) throw new Exception("Location 里没有 tag");
            return tag;
        }
        throw new Exception($"没有重定向（HTTP {code}）");
    }

    private async Task<JsonDocument> ApiLatestAsync(string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);

        using var resp = await _api.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var code = (int)resp.StatusCode;
        if (code == 200)
        {
            try { return JsonDocument.Parse(body); }
            catch (JsonException e) { throw new ApiException(ApiErrorKind.Other, $"返回的不是 JSON：{e.Message}"); }
        }
        throw Classify(code, body, resp.Headers, token != null);
    }

    /// <summary>先看限流头，再看认证 —— 有的令牌在额度耗尽时也回 401，顺序反了会误报"令牌无效"。</summary>
    private static ApiException Classify(int code, string body, HttpResponseHeaders headers, bool authed)
    {
        headers.TryGetValues("x-ratelimit-remaining", out var rem);
        var remaining = rem?.FirstOrDefault();
        long? reset = null;
        if (headers.TryGetValues("x-ratelimit-reset", out var rs) &&
            long.TryParse(rs.FirstOrDefault(), out var r)) reset = r;

        if (remaining == "0")
            return new ApiException(ApiErrorKind.RateLimited, RateLimitMessage(reset, authed), reset);

        if ((code == 401 || code == 403) && AuthRejection(body))
            return new ApiException(ApiErrorKind.AuthRejected, "GitHub 令牌无效或已过期");

        if (code == 404)
            return new ApiException(ApiErrorKind.Other, "这个仓库还没有发布过正式版本（或仓库地址填错了）");

        if (code is 403 or 429)
            return new ApiException(ApiErrorKind.RateLimited, RateLimitMessage(null, authed));

        var head = body.Length > 120 ? body[..120] : body;
        return new ApiException(ApiErrorKind.Other, $"GitHub 返回 HTTP {code}：{head.Replace('\n', ' ')}");
    }

    private static bool AuthRejection(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("message", out var m)) return false;
            var s = (m.GetString() ?? "").ToLowerInvariant();
            return s.Contains("access token") || s.Contains("bad credentials") || s.Contains("bad oauth");
        }
        catch { return false; }
    }

    private static string RateLimitMessage(long? resetEpoch, bool authed)
    {
        var wait = "过一会儿再试";
        if (resetEpoch is > 0)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var minutes = Math.Max(0, (resetEpoch.Value - now + 59) / 60);
            wait = minutes > 0 ? $"约 {minutes} 分钟后恢复" : "马上就恢复";
        }
        return authed
            ? $"GitHub 接口限流了（这个令牌的每小时额度用完了）—— {wait}"
            : $"GitHub 接口限流了（没填令牌时每小时只有 60 次）—— {wait}。" +
              "到「更新」页填一个 GitHub 个人访问令牌，额度是 5000 次/小时";
    }

    private UpdateInfo BuildInfo(JsonElement root, string current, bool authed)
    {
        var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "").Trim() : "";
        if (tag.Length == 0) throw new ApiException(ApiErrorKind.Other, "这次响应里没有版本号（tag_name 缺失）");

        var version = tag.TrimStart('v', 'V');
        var newer = IsNewer(version, current);
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesUrl : ReleasesUrl;

        var url = "";
        var assetApi = "";
        long size = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) continue;

                url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                size = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var l) ? l : 0;
                var id = a.TryGetProperty("id", out var i) && i.TryGetInt64(out var lid) ? lid : 0;
                if (id > 0) assetApi = AssetApiUrl(id);
                break;
            }
        }

        // 有新版却没有包 → 必须报错，不能给界面一个"有新版但没有下载地址"的死状态
        if (newer && url.Length == 0)
            throw new ApiException(ApiErrorKind.Other,
                $"{tag} 这个版本里没有 {AssetName} 更新包（发版时资产名必须叫这个）");

        return new UpdateInfo
        {
            Version = version,
            Tag = tag,
            Newer = newer,
            Current = current,
            Notes = notes,
            Url = url,
            AssetApiUrl = assetApi,
            Size = size,
            Sha256 = ShaFromNotes(notes),
            Page = page,
            Authed = authed,
        };
    }

    /* ══════════════ 令牌验证 / 额度 ══════════════ */

    /// <summary>
    /// 拿令牌去问一次 GitHub「你是谁、额度多少」。
    /// 不用 <c>releases/latest</c> 验证：那个接口对**没权限**的令牌回 404 而不是 401，
    /// 分不清"令牌坏了"还是"仓库没发布过"；<c>/user</c> 只认令牌本身，判据干净。
    /// </summary>
    public async Task<TokenCheck> VerifyTokenAsync(string? rawToken, CancellationToken ct = default)
    {
        var tok = Text.SanitizeToken(rawToken)
                  ?? throw new InvalidOperationException("还没填令牌 —— 把 github_pat_… 或 ghp_… 粘进来再点验证");

        using var req = new HttpRequestMessage(HttpMethod.Get, UserApi);
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", tok);

        using var resp = await _api.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var code = (int)resp.StatusCode;

        if (code != 200)
        {
            if (code == 401 || AuthRejection(body))
                throw new InvalidOperationException("令牌无效或已过期（GitHub 回的是 Bad credentials）—— 到网页上重新生成一个");
            var head = body.Length > 120 ? body[..120] : body;
            throw new InvalidOperationException($"GitHub 返回 HTTP {code}：{head.Replace('\n', ' ')}");
        }

        long Num(string name) =>
            resp.Headers.TryGetValues(name, out var v) && long.TryParse(v.FirstOrDefault(), out var x) ? x : 0;

        using var doc = JsonDocument.Parse(body);
        var login = doc.RootElement.TryGetProperty("login", out var l) ? (l.GetString() ?? "").Trim() : "";
        if (login.Length == 0)
            throw new InvalidOperationException("GitHub 没回账号名 —— 这串东西可能不是个人访问令牌");

        var name = doc.RootElement.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "").Trim() : "";

        // 经典令牌一定带 x-oauth-scopes（值可能是空串）；细粒度令牌不带这个头
        var kind = resp.Headers.Contains("x-oauth-scopes") ? "经典令牌" : "细粒度令牌";

        return new TokenCheck
        {
            Login = login,
            Name = name,
            Limit = Num("x-ratelimit-limit"),
            Remaining = Num("x-ratelimit-remaining"),
            Kind = kind,
        };
    }

    /// <summary>查一次接口额度。<c>/rate_limit</c> 不计入额度，所以可以随便查。</summary>
    public async Task<RateState> GetRateStateAsync(CancellationToken ct = default)
    {
        var tok = _store.CurrentToken();
        using var req = new HttpRequestMessage(HttpMethod.Get, RateApi);
        if (tok != null) req.Headers.Authorization = new AuthenticationHeaderValue("Token", tok);

        using var resp = await _api.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if ((int)resp.StatusCode != 200)
            throw new InvalidOperationException($"查额度失败：HTTP {(int)resp.StatusCode}");

        long Num(string name) =>
            resp.Headers.TryGetValues(name, out var v) && long.TryParse(v.FirstOrDefault(), out var x) ? x : 0;

        var st = new RateState
        {
            Authed = tok != null,
            Limit = Num("x-ratelimit-limit"),
            Remaining = Num("x-ratelimit-remaining"),
            Reset = Num("x-ratelimit-reset"),
        };
        _ = await resp.Content.ReadAsStringAsync(ct);
        if (st.Limit == 0) throw new InvalidOperationException("GitHub 没给额度信息（响应头里没有 x-ratelimit-limit）");
        return st;
    }

    /* ══════════════ 下载 ══════════════ */

    private sealed class DlFailure : Exception
    {
        public bool KeepPartial { get; }
        public bool Cancelled { get; }

        public DlFailure(string message, bool keepPartial = false, bool cancelled = false) : base(message)
        {
            KeepPartial = keepPartial;
            Cancelled = cancelled;
        }
    }

    /// <summary>
    /// 下载更新包到 <c>&lt;exe 同目录&gt;/MouseWheelEnhancement.exe.new</c>，返回落地路径。
    /// 传输中断会**保留断点**用 Range 续传；内容不对（体积/SHA 不符）则丢掉重来。
    /// </summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        var target = StagedPath;

        var sources = new List<(string Url, bool IsApi)>();
        if (!string.IsNullOrWhiteSpace(info.Url)) sources.Add((info.Url.Trim(), false));
        if (!string.IsNullOrWhiteSpace(info.AssetApiUrl)) sources.Add((info.AssetApiUrl.Trim(), true));
        if (sources.Count == 0) throw new InvalidOperationException("这个版本没有可用的下载地址");

        // 上一轮"取消"留下的半截文件会干扰续传判断；内容对不对由校验兜底
        var last = "下载失败";
        const int rounds = 3;

        for (var round = 1; round <= rounds; round++)
        {
            foreach (var (url, isApi) in sources)
            {
                try
                {
                    await FetchIntoAsync(url, isApi, info, target, progress, ct);
                    Log.Info($"update: 已下载 {target}（第 {round} 轮，源 {url}）");
                    return target;
                }
                catch (OperationCanceledException) { throw; }
                catch (DlFailure f)
                {
                    if (f.Cancelled) { progress?.Report(new DownloadProgress(0, 0, UpdateStage.Cancelled, "已取消下载")); throw; }
                    last = f.Message;
                    if (!f.KeepPartial) TryDelete(target);
                    Log.Warn($"update: 第 {round} 轮失败（{(f.KeepPartial ? "保留断点" : "丢弃断点")}）{url} —— {f.Message}");
                }
            }
            if (round < rounds) await Task.Delay(700 * round, ct);
        }

        throw new InvalidOperationException($"{last}（已自动重试 {rounds} 轮）");
    }

    private async Task FetchIntoAsync(string url, bool isApi, UpdateInfo info, string path,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var total = info.Size;
        long have = 0;
        try { if (File.Exists(path)) have = new FileInfo(path).Length; } catch { have = 0; }

        // 断点比目标还大 → 一定不是这个包（多半是上一版残留）
        if (total > 0 && have > total) { TryDelete(path); have = 0; }

        // 已经下满：一字节都不用读，直接校验
        if (total > 0 && have == total)
        {
            progress?.Report(new DownloadProgress(have, total, UpdateStage.Verifying, "校验更新包…"));
            await VerifyFileAsync(path, info, ct);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (isApi) req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

        HttpResponseMessage resp;
        try
        {
            resp = await _download.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            /* 这里最容易踩的坑：连接阶段超时抛的是 TaskCanceledException
             * （"The operation was canceled."），用户完全看不懂 —— 而这句
             * 话以前会原样出现在界面上。翻成"连不上下载服务器"。 */
            throw new DlFailure($"连接下载服务器超时（{new Uri(url).Host}）—— " +
                                "网络或代理不允许访问这个域名", keepPartial: have > 0);
        }
        catch (HttpRequestException e)
        {
            // 公开资产匿名就能下；匿名被拒且确实配了令牌时才带令牌重试一次
            var tok = _store.CurrentToken();
            if (tok != null && IsAuthish(e.StatusCode))
            {
                using var retry = new HttpRequestMessage(HttpMethod.Get, url);
                retry.Headers.Authorization = new AuthenticationHeaderValue("Token", tok);
                if (isApi) retry.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                if (have > 0) retry.Headers.Range = new RangeHeaderValue(have, null);
                resp = await _download.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            else throw new DlFailure(Explain(e), keepPartial: true);
        }

        using (resp)
        {
            var code = (int)resp.StatusCode;
            // 206 = 服务器接受了 Range；200 = 它忽略了（或我们没发），必须从 0 写
            var append = code == 206 && have > 0;
            if (code != 200 && code != 206)
            {
                throw new DlFailure($"HTTP {code}" + (code == 404
                    ? " —— 这个包可能不是公开的，在「更新」页填个令牌再试"
                    : ""), keepPartial: false);
            }

            if (resp.Content.Headers.ContentLength is { } cl && code == 200 && total <= 0) total = cl;
            if (code == 206 && resp.Content.Headers.ContentRange?.Length is { } full) total = full;

            try
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(path, append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

                var buf = new byte[64 * 1024];
                var done = append ? have : 0;
                long lastPct = -1;
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);

                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // 两块数据之间最多等 60 秒；只要还在往下走，下多久都行
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    var n = await src.ReadAsync(buf, idle.Token);
                    if (n == 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;

                    if (total > 0)
                    {
                        var pct = done * 100 / Math.Max(1, total);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            progress?.Report(new DownloadProgress(done, total, UpdateStage.Downloading,
                                $"正在下载 {pct}%（{Text.HumanSize(done)} / {Text.HumanSize(total)}）"));
                        }
                    }
                    else if (done / (1024 * 1024) != lastPct)
                    {
                        lastPct = done / (1024 * 1024);
                        progress?.Report(new DownloadProgress(done, 0, UpdateStage.Downloading,
                            $"正在下载（已下 {Text.HumanSize(done)}）"));
                    }
                }
                await dst.FlushAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                // 网络断层：保留断点，下一轮用 Range 接着下
                throw new DlFailure($"下载中断：{e.Message}", keepPartial: true);
            }
        }

        if (total > 0)
        {
            long got;
            try { got = new FileInfo(path).Length; } catch { got = 0; }
            if (got < total) throw new DlFailure($"下载提前结束（{got}/{total} 字节）", keepPartial: true);
        }

        progress?.Report(new DownloadProgress(total, total, UpdateStage.Verifying, "校验更新包…"));
        await VerifyFileAsync(path, info, ct);
    }

    private static bool IsAuthish(HttpStatusCode? code) => code is HttpStatusCode.Unauthorized
        or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    /// <summary>
    /// 校验落盘的这份是不是我们要的那一份。
    /// 从**文件**重算 SHA（续传时前面那段不在本次流里，增量 hash 必然对不上）。
    /// </summary>
    private static async Task VerifyFileAsync(string path, UpdateInfo info, CancellationToken ct)
    {
        long len;
        try { len = new FileInfo(path).Length; } catch { throw new DlFailure("校验时读不到更新包", false); }

        if (info.Size > 0 && len != info.Size)
            throw new DlFailure($"更新包大小不对（期望 {info.Size}，实际 {len}）", false);

        if (string.IsNullOrEmpty(info.Sha256)) return; // 说明里没写校验值 → 只能比体积

        string got;
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         64 * 1024, useAsync: true))
        {
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct);
            got = Convert.ToHexString(hash);
        }
        if (!got.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new DlFailure($"更新包校验失败（SHA256 对不上）\n期望 {info.Sha256}\n实际 {got}", false);
    }

    /* ══════════════ 安装 ══════════════ */

    /// <summary>
    /// 把下载好的 <c>.new</c> 换成正在运行的 exe。成功后调用方应立即提示重启。
    /// 返回被改名成 <c>.old</c> 的旧文件路径（下次启动时清）。
    /// </summary>
    public string ApplyUpdate()
    {
        var exe = ExePath;
        var staged = StagedPath;
        if (!File.Exists(staged)) throw new InvalidOperationException("还没有下载好更新包");

        var old = OldPath;
        TryDelete(old); // 上一轮如果更新过两次，先清掉旧的；清不掉也无妨

        // ⚠ 顺序：**先给正在运行的自己改名**，再把新的搬进来。反过来一定失败。
        if (!Native.MoveFileEx(exe, old, Native.MOVEFILE_REPLACE_EXISTING | Native.MOVEFILE_WRITE_THROUGH))
        {
            var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"替换失败：改不掉正在运行的程序（错误码 {code}）—— 可能是权限问题（装在 Program Files 且不是管理员）");
        }

        if (!Native.MoveFileEx(staged, exe, Native.MOVEFILE_REPLACE_EXISTING | Native.MOVEFILE_WRITE_THROUGH))
        {
            var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Native.MoveFileEx(old, exe, Native.MOVEFILE_REPLACE_EXISTING | Native.MOVEFILE_WRITE_THROUGH);
            throw new InvalidOperationException($"替换失败（已回滚到旧版本）：错误码 {code}");
        }

        Log.Info($"update: 已替换程序（旧版留在 {old}，下次启动清理）");
        return old;
    }

    /// <summary>启动时清理上次更新留下的 <c>.old</c> / <c>.new</c>。删不掉只是留个文件，绝不影响启动。</summary>
    public static int SweepLeftovers()
    {
        var n = 0;
        try
        {
            foreach (var p in new[] { OldPath, StagedPath })
            {
                if (File.Exists(p) && TryDelete(p)) n++;
            }
        }
        catch { }
        if (n > 0) Log.Info($"update: 清理了 {n} 个更新残留文件");
        return n;
    }

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch { return false; }
    }

    /// <summary>启动新版本，调用方随后应立即退出当前进程。</summary>
    public bool RestartApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("启动新版本失败", ex);
            return false;
        }
    }

    private static string Explain(Exception e)
    {
        var msg = e.Message;
        if (e is HttpRequestException hre)
        {
            var inner = hre.InnerException?.Message ?? "";
            if (msg.Contains("name or service", StringComparison.OrdinalIgnoreCase) ||
                inner.Contains("No such host", StringComparison.OrdinalIgnoreCase))
                return "连不上 GitHub（域名解析失败）—— 检查网络或代理设置";
            if (e is TaskCanceledException) return "请求超时 —— 网络不通或太慢";
            return $"网络请求失败：{msg}";
        }
        if (e is TaskCanceledException) return "请求超时 —— 网络不通或太慢";
        return msg;
    }
}
