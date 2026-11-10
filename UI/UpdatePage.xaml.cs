using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MouseWheelEnhancement.Core;
using MouseWheelEnhancement.Update;

namespace MouseWheelEnhancement.UI;

/// <summary>
/// 「更新」页。独立成 UserControl 后，主窗口在第一次切到该页时才构造它；
/// 版本信息、自动检查开关、下载/取消/重启、更新说明和 GitHub 令牌都在这里维护。
/// </summary>
public partial class UpdatePage : UserControl
{
    private readonly MainWindow _host;

    /// <summary>最近一次检查返回的版本信息；下载按钮据此决定能不能点。</summary>
    private UpdateInfo? _info;

    /// <summary>正在下载时的取消源；下载结束（无论成败）都会清掉。</summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>检查或下载期间置位，避免并发点按钮。</summary>
    private bool _busy;

    /// <summary>程序化回填「自动检查」开关时置位，防止 Checked 反向写回配置。</summary>
    private bool _syncing;

    public UpdatePage(MainWindow host)
    {
        _host = host;
        InitializeComponent();

        CurrentVersionText.Text = $"v{Services.Version}";
        RepoText.Text = Services.Updater.Repo;

        SyncFromConfig();
        RefreshTokenUi();
        UiText.Apply(this);

        if (Services.Store.LoadWarning != null) UpdateStatus.Text = Services.Store.LoadWarning;

        /* 页面是懒加载的：启动时那轮自动检查可能在页面还没建好时就发现了新版。
         * 结果一直挂在 Services.PendingUpdate 上，第一次打开这一页时补显示。 */
        if (Services.PendingUpdate is { Newer: true } pending)
            ShowUpdateInfo(pending, fromAutoCheck: true);
    }

    /// <summary>把配置里的「自动检查」回填到开关（主窗口导入/重置/外部变更后调用）。</summary>
    internal void SyncFromConfig()
    {
        _syncing = true;
        try { AutoCheckSwitch.IsChecked = Services.Config.Update.AutoCheck; }
        finally { _syncing = false; }
    }

    /* ══════════════ 事件：自动检查 / 仓库链接 ══════════════ */

    private void AutoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Services.Config.Update.AutoCheck = AutoCheckSwitch.IsChecked == true;
        _host.Commit();
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e) => App.OpenExternal(Services.Updater.RepoUrl);

    /* ══════════════ 事件：检查 / 下载 / 取消 / 重启 ══════════════ */

    /// <summary>把一次检查结果铺到界面上（自动检查与手动检查共用）。</summary>
    internal void ShowUpdateInfo(UpdateInfo info, bool fromAutoCheck)
    {
        _info = info;
        CurrentVersionText.Text = $"v{Services.Version}";

        if (info.Newer)
        {
            UpdateStatus.Foreground = _host.B("WarnBrush");
            UpdateStatus.Text = $"发现新版本 v{info.Version}" +
                                (info.Size > 0 ? $"（{Text.HumanSize(info.Size)}）" : "") +
                                (fromAutoCheck ? " —— 点「下载并安装」即可在程序内更新。" : "");
            DownloadBtn.Visibility = Visibility.Visible;
            DownloadBtn.IsEnabled = true;

            if (!string.IsNullOrWhiteSpace(info.Notes))
            {
                NotesTitle.Text = $"v{info.Version} 更新说明";
                NotesBox.Text = info.Notes;
                NotesCard.Visibility = Visibility.Visible;
            }
        }
        else
        {
            UpdateStatus.Foreground = _host.B("TextSecondary");
            UpdateStatus.Text = $"已是最新版本（v{Services.Version}）";
            DownloadBtn.Visibility = Visibility.Collapsed;
            NotesCard.Visibility = Visibility.Collapsed;
        }

        if (info.AuthWarning != null)
        {
            UpdateStatus.Foreground = _host.B("WarnBrush");
            UpdateStatus.Text += "\n" + info.AuthWarning;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        CheckBtn.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateStatus.Foreground = _host.B("TextSecondary");
        UpdateStatus.Text = "正在检查…";

        try
        {
            var outcome = await Services.Updater.CheckAsync(Services.Version);
            if (!outcome.Ok)
            {
                UpdateStatus.Foreground = _host.B("DangerBrush");
                UpdateStatus.Text = "检查失败：" + outcome.Error;
                return;
            }

            var info = outcome.Info!;
            Services.Config.Update.LastCheckUtc = DateTime.UtcNow.ToString("o");
            Services.Store.Touch(notify: false);
            Services.PendingUpdate = info;
            ShowUpdateInfo(info, fromAutoCheck: false);
        }
        catch (Exception ex)
        {
            UpdateStatus.Foreground = _host.B("DangerBrush");
            UpdateStatus.Text = "检查失败：" + ex.Message;
            Log.Warn("检查更新失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            CheckBtn.IsEnabled = true;
        }
    }

    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _info == null) return;

        if (!Services.Updater.CanSelfUpdate(out var reason))
        {
            UpdateStatus.Foreground = _host.B("DangerBrush");
            UpdateStatus.Text = reason;
            return;
        }

        _busy = true;
        _downloadCts = new CancellationTokenSource();
        DownloadBtn.IsEnabled = false;
        CancelBtn.Visibility = Visibility.Visible;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        RestartBtn.Visibility = Visibility.Collapsed;

        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Total > 0) UpdateProgress.Value = p.Percent;
            UpdateStatus.Foreground = _host.B("TextSecondary");
            UpdateStatus.Text = p.Message;
        });

        try
        {
            await Services.Updater.DownloadAsync(_info, progress, _downloadCts.Token);

            UpdateStatus.Text = "校验通过，正在替换程序…";
            Services.Updater.ApplyUpdate();

            UpdateProgress.Value = 100;
            UpdateStatus.Foreground = _host.B("SuccessBrush");
            UpdateStatus.Text = "更新已就位 —— 重启程序即可用上新版本。";
            RestartBtn.Visibility = Visibility.Visible;
            DownloadBtn.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Foreground = _host.B("TextSecondary");
            UpdateStatus.Text = "已取消下载（已下载的部分会保留，下次可以接着下）。";
        }
        catch (Exception ex)
        {
            UpdateStatus.Foreground = _host.B("DangerBrush");
            UpdateStatus.Text = "更新失败：" + ex.Message;
            Log.Warn("更新失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
            DownloadBtn.IsEnabled = true;
            CancelBtn.Visibility = Visibility.Collapsed;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        try { _downloadCts?.Cancel(); } catch { }
        UpdateStatus.Text = "正在取消…";
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => App.RestartForUpdate();

    /* ══════════════ 事件：令牌 ══════════════ */

    /// <summary>
    /// 把界面刷成与"当前有没有令牌"一致的状态。
    /// 做成 internal 是给 smoke 用：它要在无人工参与的情况下把界面推到
    /// "已保存令牌"那一态，再截图留证。
    /// </summary>
    internal void RefreshTokenUi()
    {
        var tok = Services.Store.CurrentToken();
        if (tok == null)
        {
            // 没保存过：可编辑状态
            PopupCleanup.CloseCurrentToolTip();
            TokenBox.IsEnabled = true;
            TokenBox.Password = "";
            TokenBox.ToolTip = "粘贴令牌后点「保存令牌」，例如 ghp_xxx 或 github_pat_xxx";
            EditTokenBtn.Visibility = Visibility.Collapsed;
            ClearTokenBtn.Visibility = Visibility.Collapsed;
            SaveTokenBtn.IsEnabled = true;
        }
        else
        {
            // 已保存：PasswordBox 没有 IsReadOnly，用禁用 + 一串圆点表示"锁着"
            PopupCleanup.CloseCurrentToolTip();
            TokenBox.IsEnabled = false;
            TokenBox.Password = Text.MaskToken(tok);
            TokenBox.ToolTip = "令牌已加密保存。要换一个请点「更换」。";
            EditTokenBtn.Visibility = Visibility.Visible;
            ClearTokenBtn.Visibility = Visibility.Visible;
            SaveTokenBtn.IsEnabled = false;
        }
    }

    private void EditToken_Click(object sender, RoutedEventArgs e)
    {
        TokenBox.IsEnabled = true;
        TokenBox.Password = "";
        SaveTokenBtn.IsEnabled = true;
        EditTokenBtn.Visibility = Visibility.Collapsed;
        TokenBox.Focus();
        TokenStatus.Text = "把新的令牌粘进来，点「保存令牌」。";
    }

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        if (!TokenBox.IsEnabled) return;
        var raw = TokenBox.Password;
        if (Text.SanitizeToken(raw) == null)
        {
            TokenStatus.Foreground = _host.B("DangerBrush");
            TokenStatus.Text = "没填内容 —— 把 github_pat_… 或 ghp_… 粘进来再保存。";
            return;
        }

        Services.Store.SetToken(raw);
        RefreshTokenUi();
        TokenStatus.Foreground = _host.B("TextSecondary");
        TokenStatus.Text = "已保存（DPAPI 加密，只在本机可解）。正在验证…";
        await VerifyCurrentTokenAsync();
    }

    private async void VerifyToken_Click(object sender, RoutedEventArgs e)
    {
        if (TokenBox.IsEnabled)
        {
            // 输入框里是新粘贴的：直接验证这一串，不落盘
            var raw = TokenBox.Password;
            if (Text.SanitizeToken(raw) == null)
            {
                TokenStatus.Foreground = _host.B("DangerBrush");
                TokenStatus.Text = "还没填令牌 —— 粘进来再点验证。";
                return;
            }
            await VerifyAsync(raw);
            return;
        }
        await VerifyCurrentTokenAsync();
    }

    private async Task VerifyCurrentTokenAsync()
    {
        var tok = Services.Store.CurrentToken();
        if (tok == null)
        {
            TokenStatus.Foreground = _host.B("DangerBrush");
            TokenStatus.Text = "还没保存令牌。";
            return;
        }
        await VerifyAsync(tok);
    }

    private async Task VerifyAsync(string token)
    {
        VerifyTokenBtn.IsEnabled = false;
        TokenStatus.Foreground = _host.B("TextSecondary");
        TokenStatus.Text = "正在向 GitHub 验证…";
        try
        {
            var check = await Services.Updater.VerifyTokenAsync(token);
            TokenStatus.Foreground = _host.B("SuccessBrush");
            TokenStatus.Text = "令牌有效：" + check.Display;

            try
            {
                var rate = await Services.Updater.GetRateStateAsync();
                RateText.Text = $"接口额度：本小时已用 {rate.Used} 次 / 共 {rate.Limit} 次" +
                                (rate.Authed ? "（已带令牌）" : "（匿名）");
            }
            catch (Exception ex)
            {
                RateText.Text = "接口额度：查询失败（" + ex.Message + "）";
            }
        }
        catch (Exception ex)
        {
            TokenStatus.Foreground = _host.B("DangerBrush");
            TokenStatus.Text = "令牌无效：" + ex.Message;
        }
        finally { VerifyTokenBtn.IsEnabled = true; }
    }

    private void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        Services.Store.SetToken("");
        RefreshTokenUi();
        TokenStatus.Foreground = _host.B("TextSecondary");
        TokenStatus.Text = "已清除令牌。查更新会走匿名接口（每小时 60 次额度）。";
        RateText.Text = "接口额度：点「验证」可以查看。";
    }
}
