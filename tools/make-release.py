#!/usr/bin/env python3
"""创建 GitHub Release 并上传资产（exe + zip）。

用法：
    python tools/make-release.py v1.5.1
"""
import base64
import json
import mimetypes
import os
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "QQQOrange/Mouse-wheel-enhancement"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TAG = sys.argv[1] if len(sys.argv) > 1 else sys.exit("用法：make-release.py <tag>")


def credentials():
    p = subprocess.run(["git", "credential", "fill"], cwd=ROOT,
                       input="protocol=https\nhost=github.com\n\n",
                       capture_output=True, text=True,
                       env=dict(os.environ, GIT_TERMINAL_PROMPT="0"))
    d = dict(l.split("=", 1) for l in p.stdout.splitlines() if "=" in l)
    return d.get("username", ""), d.get("password", "")


USER, TOKEN = credentials()


def api(method, path, payload=None):
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request("https://api.github.com" + path, data=data, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    req.add_header("User-Agent", USER or "QQQOrange")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            body = r.read().decode("utf-8", "replace")
            return json.loads(body) if body.strip() else {}
    except urllib.error.HTTPError as e:
        sys.exit("API %s %s → %d: %s" % (method, path, e.code,
                                        e.read().decode("utf-8", "replace")[:600]))


def upload(name, path):
    ctype = mimetypes.guess_type(name)[0] or "application/octet-stream"
    with open(path, "rb") as f:
        data = f.read()
    req = urllib.request.Request(
        "https://uploads.github.com/repos/%s/releases/%s/assets?name=%s"
        % (REPO, REL["id"], name), data=data, method="POST")
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("Content-Type", ctype)
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    req.add_header("User-Agent", USER or "QQQOrange")
    with urllib.request.urlopen(req, timeout=600) as r:
        a = json.loads(r.read().decode("utf-8", "replace"))
    print("  已上传 %s（%s B）" % (a["name"], format(a["size"], ",")))


# ── 发布说明 ──
BODY = """## 修掉「点托盘图标没反应」—— 它从 v1.5.2 就一直存在

这轮做的是**全局审查**：逐项核对已规划功能的实现情况、确认已知 bug 是否修好、
排查改动有没有带出衍生问题。查出 9 处实质缺陷，其中一处所有测试都没抓到。

### ① 最严重：点托盘图标根本没打开窗口

```csharp
_ = Task.Delay(400, cts.Token).ContinueWith(t =>
{
    if (t.IsCanceled) return;
    try { Services.ShowWindow(); }
    catch (Exception ex) { Log.Warn("托盘打开窗口失败：" + ex.Message); }
}, TaskScheduler.Default);      // ← 线程池，MTA
```

而 `ShowWindow` 直接 `new MainWindow()`。**WPF 窗口只能在 UI 线程创建**，
在线程池（MTA）上必抛"调用线程必须为 STA" —— 异常被那层 `catch` 吞成一条 WARN：

> 点托盘图标没有任何反应，日志里只有一行 WARN。

更糟的是 `ShowMainWindow` 的 catch 会把 `Services.Window` 置空并 `Close()` 掉
原来开着的窗口 —— 所以窗口本来开着的话，点一下托盘反而把它关掉，然后再也打不开。

**为什么之前没发现**：`--trayprobe` 只统计三条异常通道，这条异常在
`ContinueWith` 里就被吃掉了；`--smoke` 只建窗口、不走托盘。

修法：把封送放在**唯一入口** `Services.ShowWindow`，所有调用方都从这里进。
并给冒烟加了断言：从线程池调一次，断言窗口真的可见。
**反向验证过**：把封送短路掉再跑，`--smoke` 退出码 6、日志写"窗口没能打开"。

### ② 设置会在升级时被删掉

数据目录判据错了一级：判断"主程序在版本目录里"时，代码去看
`<安装目录>\\app\\` 里有没有启动器 —— 而启动器在**安装目录**，不在 `app\\` 里，
条件永远不成立。于是用户直接双击 `app\\<版本>\\` 里的 exe 时，设置就落在那个
下次升级会被 `CleanOldVersions` 整个删掉的目录里。

### ③ 「关闭自启」时残留的计划任务永远清不掉

`RepairStartupIfNeeded` 第一行是 `if (!RunAtLogin) return;`，
而它注释里承诺的第二件事正是"配置说不用管理员自启、任务却还留着 → 清掉"。
"自启整体关掉 + 任务没删成功"恰好被第一行挡住 —— 之后每次管理员启动都不再自愈，
开关显示关、开机照样弹一次提权。

### ④ 令牌可能明文落盘

原来的判断是"先看加密字段空不空"。用户手改 `config.json` 往 `gitHubToken`
里贴了个新令牌、而旧的加密字段还在时：明文既不加密也不清空，会被原样写回文件，
日志还把原因错报成"DPAPI 不可用"。

### ⑤ 另外四处

| 位置 | 问题 |
|------|------|
| 下载重试 | 带令牌重试那一次没包 try/catch，重试再失败会越过 3 轮重试与换源，界面显示原始英文异常 |
| 滚轮钩子 | `HookProc` 只看配置开关、不看运行标志 —— `Stop()` 后线程未退出时钩子仍在工作（"关了还在加速"） |
| 托盘图标 | 后台线程直接改 WinForms `NotifyIcon` → 偶发图标空白/提示文字错乱 |
| 启动器诊断 | `--runtimecheck` 判断主程序用 `File.Exists`，可主程序是内嵌资源、从不并排落地 → 永远打印"（缺失）" |

### ⑥ 上一版自己带出来的两处风险

- **稳定入口解析会误清有效配置**：环境变量指向的文件没了时，
  会连带把配置里仍有效的路径一起清掉，于是稳定入口退化成版本目录。
- **自愈可能把任务写回版本目录**：现在命中 `…\\app\\<版本>\\xxx.exe` 一律**拒绝创建**
  并说明原因；`PointsToCurrentExe` 也改名 `PointsToStableEntry` ——
  上一版那个 bug 的根源就是"任务指向当前 exe"这个错误概念。

另外 `IsStale()` 加了"是否真的知道稳定入口"这道门：
不是从启动器启动时不再误报"任务已失效"。

### ⑦ 完整性

- 自检 **149 → 160 条**（新增布局判据 7 条 + 稳定入口解析 6 条）
- 「注入帧率」的说明文字现在跟着"自动帧率"开关变（原来关掉自动后还写着"自动跟着显示器走"）
- 删掉遗留 API（`AutoStart.Set` / `AutoStart.IsEnabled` / 重复的 `AppPaths.ConfigPath`）
- 核对结论：45 个配置项**全部**有实际读取点（没有"界面有开关、程序没人用"的死配置）；
  两份调色板 35 个 Key 一一对应；55 个事件处理器与 XAML 绑定一一对应

## 下载

| 文件 | 用途 |
|------|------|
| `MouseWheelEnhancement.exe` | **下载这个**。单文件，0.53 MB，双击即用 |
| `MouseWheelEnhancement-win-x64.zip` | 整包（内容同上，压缩后更小） |
| `MouseWheelEnhancement.App.exe` | 主程序本体，供软件内自动更新用 |

需要 .NET 10 桌面运行时（未装时软件会提示并给出下载链接）。
Windows 11 x64 实测；Windows 10 上圆角自动降级为直角，其余功能不变。

## 校验

```
SHA256 4C5112C33EDFAFEA2250D8CA828374883562E00EBD8006C3C20003048D9F285D
```

构建 0 警告 0 错误；`--selftest`（160 项）/ `--smoke` / `--taskcheck` / `--trayprobe` 全部通过。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 全局审查：修掉 9 处缺陷，含「点托盘没反应」" % TAG,
    "body": BODY,
    "draft": False,
    "prerelease": False,
})
print("Release 已创建：%s" % REL["html_url"])

pkg = os.path.join(ROOT, "bin", "package")
upload("MouseWheelEnhancement.exe", os.path.join(pkg, "MouseWheelEnhancement.exe"))
upload("MouseWheelEnhancement-win-x64.zip", os.path.join(ROOT, "bin", "MouseWheelEnhancement-win-x64.zip"))
upload("MouseWheelEnhancement.App.exe", os.path.join(ROOT, "bin", "publish-stage", "MouseWheelEnhancement.App.exe"))
print("\n完成：%s" % REL["html_url"])
