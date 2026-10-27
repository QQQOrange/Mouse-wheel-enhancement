using System.Text;
using System.Text.Json;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 配置的读写与持久化。
///
/// # 存放位置
///
/// 默认就放在**程序自己文件夹**里的 `Mouse wheel enhancement data`（见 <see cref="AppPaths"/>）。
/// 程序目录不可写时（装在 Program Files 且非管理员）才退到 %APPDATA%，
/// 并把这个事实显示在「关于」页上。
/// 环境变量 `MWE_DATA_DIR` 可以强制指定（自检和调试用）。
///
/// # 写盘策略
///
/// 1. 内存里立刻生效（界面拖滑块要即时反馈）；
/// 2. 落盘延迟 600ms 合并 —— 拖一次滑块会产生几十次变更，不能每次都写盘；
/// 3. 写盘走 `临时文件 → File.Replace`：中途断电也不会留下半个 JSON，
///    旧版本还会留成 `.bak` 兜底；
/// 4. 读不出来（损坏 / 手改坏了）→ 原文件改名成 `config.corrupt-时间戳.json`
///    保留证据，然后用默认值继续跑。**绝不静默覆盖用户的文件。**
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 中文备注不要被转成 \uXXXX：配置文件是给人看的，也可能被人手改
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly System.Threading.Timer _saveTimer;
    private string _cachedToken = "";
    private bool _tokenLoaded;

    public string Dir { get; }
    public string ConfigPath => Path.Combine(Dir, "config.json");    public AppConfig Config { get; private set; } = AppConfig.Default();

    /// <summary>配置发生**有效**变化时触发（已做防抖之外的即时通知）。</summary>
    public event Action<AppConfig>? Changed;

    /// <summary>本次启动是否读到过损坏的配置（界面上提示一次）。</summary>
    public string? LoadWarning { get; private set; }

    public ConfigStore(string? dir = null)
    {
        Dir = dir ?? ResolveDir();
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    public static string ResolveDir() => AppPaths.DataDir;

    public void Load()
    {
        try
        {
            Directory.CreateDirectory(Dir);
        }
        catch (Exception ex)
        {
            LoadWarning = $"配置目录不可写（{ex.Message}），本次运行不会保存设置。";
            Log.Warn(LoadWarning);
            Config = AppConfig.Default();
            return;
        }

        if (!File.Exists(ConfigPath))
        {
            Config = AppConfig.Default();
            Config.Normalize();
            Log.Info($"没有配置文件，使用默认值：{ConfigPath}");
            SaveNow();
            return;
        }

        try
        {
            var text = File.ReadAllText(ConfigPath, Encoding.UTF8);
            var cfg = JsonSerializer.Deserialize<AppConfig>(text, JsonOpts) ?? AppConfig.Default();

            /* 归一化可能真的改了东西（越界值被夹回、小数被取整、名单被去重）。
             * 改过就立刻写回，否则用户打开 config.json 会看到 9.927287581699336
             * 这种鬼数字，而界面上显示的是 10 —— 两边对不上比什么都可疑。 */
            var before = JsonSerializer.Serialize(cfg, JsonOpts);
            cfg.Normalize();
            var changed = !string.Equals(before, JsonSerializer.Serialize(cfg, JsonOpts), StringComparison.Ordinal);

            Config = cfg;
            MigrateTokenIfNeeded();
            Log.Info($"已读取配置：{ConfigPath}");

            if (changed)
            {
                Log.Info("配置里有越界值或小数，已归一化为整格并回写");
                SaveNow();
            }
        }
        catch (Exception ex)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var bad = Path.Combine(Dir, $"config.corrupt-{stamp}.json");
            try
            {
                File.Move(ConfigPath, bad, true);
                LoadWarning = $"配置文件读不出来（{ex.Message}），已备份成 {Path.GetFileName(bad)} 并恢复默认设置。";
            }
            catch
            {
                LoadWarning = $"配置文件读不出来（{ex.Message}），已使用默认设置。";
            }
            Log.Warn(LoadWarning);
            Config = AppConfig.Default();
            Config.Normalize();
        }
    }

    /// <summary>把明文令牌（用户手改 / 老版本留下的）迁移成加密字段。</summary>
    private void MigrateTokenIfNeeded()
    {
        var plain = Text.SanitizeToken(Config.Update.GitHubToken);
        if (plain == null)
        {
            Config.Update.GitHubToken = "";
            return;
        }

        /* 只要有明文，就**以明文为准**加密一次并清掉它。
         *
         * 这里以前是先判 `GitHubTokenProtected` 是否为空，非空就直接跳到"DPAPI 不可用"
         * 那条分支 —— 于是"用户手改 config 往 gitHubToken 里贴了新令牌、旧的加密字段还在"
         * 这种情况：明文既不加密也不清空，会被 SaveNow 原样写回文件（凭据明文落盘），
         * 日志还把原因错报成 DPAPI 不可用。 */
        var prot = Dpapi.Protect(plain);
        if (prot != null)
        {
            Config.Update.GitHubTokenProtected = prot;
            Config.Update.GitHubToken = "";
            _cachedToken = plain;
            _tokenLoaded = true;
            Log.Info("已把明文 GitHub 令牌迁移为 DPAPI 加密存储");
            SaveNow();
            return;
        }

        // 加密不可用（极少数受限环境）：保持明文，但至少写进日志提醒
        Log.Warn("DPAPI 不可用，GitHub 令牌只能以明文保存在本机配置里");
        _cachedToken = plain;
        _tokenLoaded = true;
    }

    /// <summary>当前可用的令牌（已解密）。没填 → null。</summary>
    public string? CurrentToken()
    {
        if (_tokenLoaded) return string.IsNullOrEmpty(_cachedToken) ? null : _cachedToken;

        _tokenLoaded = true;
        var plain = Text.SanitizeToken(Config.Update.GitHubToken);
        if (plain != null) { _cachedToken = plain; return plain; }

        var dec = Dpapi.Unprotect(Config.Update.GitHubTokenProtected);
        _cachedToken = Text.SanitizeToken(dec) ?? "";
        return string.IsNullOrEmpty(_cachedToken) ? null : _cachedToken;
    }

    /// <summary>写入令牌（自动清洗 + 加密）。传空串 = 清除。</summary>
    public void SetToken(string? raw)
    {
        var clean = Text.SanitizeToken(raw);
        Config.Update.GitHubToken = "";
        if (clean == null)
        {
            Config.Update.GitHubTokenProtected = "";
            _cachedToken = "";
        }
        else
        {
            var prot = Dpapi.Protect(clean);
            if (prot != null)
            {
                Config.Update.GitHubTokenProtected = prot;
            }
            else
            {
                // 极端环境下退化成明文，功能优先
                Config.Update.GitHubToken = clean;
                Config.Update.GitHubTokenProtected = "";
            }
            _cachedToken = clean;
        }
        _tokenLoaded = true;
        SaveNow();
        Changed?.Invoke(Config);
    }

    public bool HasToken => CurrentToken() != null;

    /// <summary>内存里改完之后调它：立刻通知运行时，600ms 后合并写盘。</summary>
    public void Touch(bool notify = true)
    {
        Config.Normalize();
        if (notify) Changed?.Invoke(Config);
        try { _saveTimer.Change(600, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    public void SaveNow()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var json = JsonSerializer.Serialize(Config, JsonOpts);
                var tmp = ConfigPath + ".tmp";

                var bytes = new UTF8Encoding(false).GetBytes(json);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }

                if (File.Exists(ConfigPath))
                {
                    try { File.Replace(tmp, ConfigPath, ConfigPath + ".bak", true); }
                    catch (IOException)
                    {
                        // 少数文件系统不支持 Replace → 退化成"删了再搬"
                        File.Copy(tmp, ConfigPath, true);
                        File.Delete(tmp);
                    }
                }
                else
                {
                    File.Move(tmp, ConfigPath);
                }
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
            }
        }
    }

    public void ResetToDefaults()
    {
        var token = Config.Update.GitHubTokenProtected;
        var plain = Config.Update.GitHubToken;
        Config = AppConfig.Default();
        Config.Normalize();
        Config.Update.GitHubTokenProtected = token;
        Config.Update.GitHubToken = plain;
        SaveNow();
        Changed?.Invoke(Config);
    }

    public void Dispose() => _saveTimer.Dispose();
}

/// <summary>零散的文本清洗工具。</summary>
public static class Text
{
    /// <summary>
    /// 把用户粘进来的"令牌"洗成能直接塞进 <c>Authorization</c> 头的样子。
    ///
    /// 要处理三种真实存在的情况（不处理的结果都是 401，而用户完全看不出原因）：
    ///   ① 从网页框选复制 → 首尾带空白/换行，或者外层裹了引号；
    ///   ② 从终端 echo 出来 → 中间夹了换行；
    ///   ③ 照老教程填成 `用户名:令牌` → 取冒号后面那段。
    /// </summary>
    public static string? SanitizeToken(string? raw)
    {
        var s = (raw ?? "").Trim().Trim('"', '\'');
        if (s.Length == 0) return null;

        var colon = s.IndexOf(':');
        if (colon >= 0 && colon + 1 < s.Length) s = s[(colon + 1)..].Trim();

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) continue;
            sb.Append(c);
        }
        if (sb.Length == 0) return null;
        return sb.Length > 512 ? sb.ToString(0, 512) : sb.ToString();
    }

    /// <summary>把令牌变成可安全显示的形态：`ghp_1…cdef`。日志/界面都不该出现完整令牌。</summary>
    public static string MaskToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "(未填写)";
        if (token.Length <= 10) return new string('•', token.Length);
        return $"{token[..6]}…{token[^4..]}（{token.Length} 字符）";
    }

    public static string HumanSize(long bytes)
    {
        if (bytes <= 0) return "未知大小";
        string[] u = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}
