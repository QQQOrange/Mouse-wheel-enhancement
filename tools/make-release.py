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
BODY = """## 修掉「VisualTarget 的根 Visual 不能具有父级」崩溃

v1.5.1 之后用户报：点一下托盘图标，弹出"应用程序的组件中发生了未经处理的异常。
如果单击\"继续\"……"，异常文本是 **VisualTarget 的根 Visual 不能具有父级**。
点「继续」程序还能继续跑，但**日志里一条记录都没有**，事后完全无从查起。

### ① 根因：异常从原生 WndProc 逃出去了，三条通道全接不住

托盘图标用的是 WinForms 的 `NotifyIcon`，它的鼠标消息走**原生 Win32 窗口过程**。
异常从那里抛出来时：

| 通道 | 接得住吗 | 原因 |
|------|----------|------|
| WPF `DispatcherUnhandledException` | ❌ | 那条通道只处理 `DispatcherOperation` 异常 |
| `AppDomain.UnhandledException` | ❌ | 被 WinForms 的 `Application.ThreadException` 先一步拦下 |
| WinForms `Application.ThreadException` | ✅ | 但**我们从没订阅它** |

没订阅 → 走 WinForms 的**默认**处理：弹框架自带的那句弹窗。
这与用户日志逐字吻合（崩溃窗口 3.3 秒内零 ERROR 记录）。

写了对照探针坐实：抛在 WinForms `Control` 的 WndProc 里能命中 `ThreadException`；
抛在裸 `NativeWindow` 的 WndProc 里三条通道全不接、直接崩。

**现在**：`SetUnhandledExceptionMode(CatchException)` + 订阅 `ThreadException`，
处理方式与 Dispatcher 一致（记日志 → 落盘取证 → 弹我们自己的框）。
**用户永远不会再看到框架默认弹窗**，且每次崩溃必有 ERROR 日志 + `crash-*.txt` 完整堆栈。

### ② 消除 Popup 风险面

`ToolTip` / `ContextMenu` 底层都是 `Popup`，各自自建一个 `HwndSource`，
并把该窗口的根 Visual 设成弹层内容。而 `SetRootVisual` 在
"这个 Visual 已有父级、或已是某个 CompositionTarget 的根"时就会抛
`VisualTarget 的根 Visual 不能具有父级`。

规矩：**要动宿主窗口之前，先把弹层都关掉。** 新增 `PopupCleanup`：

- 递归扫**视觉树**、**逻辑树**以及根元素 `Resources` 里声明的 `ContextMenu`，
  把所有开着的 `Popup` 统统关掉
- 关闭当前提示气泡：反射拿 `ToolTipService` 内部静态字段（依次试三个候选名），
  拿不到就退回扫全部窗口的树

接入三个点：窗口关闭前、切换主题按钮重新赋值 `ToolTip` 前、
刷新令牌状态重新赋值 `ToolTip` 前（最后两处的场景正是
"用户悬停在按钮上点了它、气泡正开着"）。

### ③ `ShowMainWindow` 加固

- 重入标记 + `PresentationSource` 存活检查
- 异常后销毁重建窗口，不留半死状态
- `Closed` 事件**身份校验** —— 修掉"旧窗口的 Closed 把新窗口引用清空"的隐患
- `Topmost` 翻转独立包 try/catch，不再连累主流程
- 托盘单击/双击合并为单次触发（400ms 延迟窗口），杜绝 `ShowWindow()` 三连发

### ④ 排查过程中实证排除的路径

- 滚轮钩子回调（独立线程 + 完整 try/catch + 异常节流记日志）—— 若在此抛出必留 ERROR 记录
- 刷新率变化回调（try/catch 包裹）
- 主题监听用的 `HwndSource`（未设 `RootVisual`）
- 纯 WPF 上下文中的 Topmost 翻转 / Popup+Topmost / Hide→Show / 切主题 / 切页面 / 重入 Show
- 真实 `NotifyIcon` 消息投递的 5 个场景

### ⑤ 探针保留为长期回归手段

`--probe` / `--wndprobe` / `--hover` / `--trayprobe` 四个取证探针保留在仓库里，
各有独立 CLI 开关，不参与正常启动路径。

## 下载

| 文件 | 用途 |
|------|------|
| `MouseWheelEnhancement.exe` | **下载这个**。单文件，0.52 MB，双击即用 |
| `MouseWheelEnhancement-win-x64.zip` | 整包（内容同上，压缩后更小） |
| `MouseWheelEnhancement.App.exe` | 主程序本体，供软件内自动更新用 |

需要 .NET 10 桌面运行时（未装时软件会提示并给出下载链接）。
Windows 11 x64 实测；Windows 10 上圆角自动降级为直角，其余功能不变。

## 校验

```
SHA256 94A7AEAB0E80E8FF36A66456E6E7063D85EAAE73A9F3E5973C00D87213DA61B9
```

构建 0 警告 0 错误；`--selftest` / `--smoke` / `--probe` / `--wndprobe` / `--trayprobe` 全部通过。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 崩溃归因修复：接管 WinForms 异常通道 / 消除 Popup 风险面" % TAG,
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
