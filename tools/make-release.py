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
BODY = """## 跟随系统主题修好了：根因是监听窗口收不到广播

### 主要修复

**「系统切了浅/深色，程序不跟」**

监听窗口原来是 `HWND_MESSAGE`（message-only），而 `WM_SETTINGCHANGE` 是广播给
**顶层窗口**的 —— message-only 窗口按设计收不到。轮询又只在"创建失败"时才启动，
两条路一起断。

- 监听窗口改成**顶层隐藏窗口**
- **3 秒轮询始终开启**兜底（强制浅/深色时立刻返回，几乎零开销）
- 只认 `WM_THEMECHANGED` 和 lParam 为 `"ImmersiveColorSet"` 的 `WM_SETTINGCHANGE`

**「手动覆盖后回不到跟随」**

标题栏按钮从"浅↔深"两态改成**三态循环**：跟随 → 浅 → 深 → 跟随。
图标和 ToolTip 都跟着状态走。

### 其它修复

- `ApplyPalette` 缓存调色板位置，不再每次遍历字典 + 字符串匹配
- 键盘焦点可见（10 处 `FocusVisualStyle` 从 null 换成统一焦点环）
- 删除从未被引用、尺寸也错的 `SwitchSmall`
- `TextMuted` 两套主题对比度都提到 WCAG AA 附近

### UI 精修

- 开关 Knob 160 ms 滑动（不再瞬间跳）
- 侧栏选中项加左侧强调色小竖条
- 说明文字在深/浅色下都更清楚

### 验证

- **真实系统切换实测**：改 `AppsUseLightTheme` 后 **5.9 秒内**自动切到浅色
  （日志 `系统主题变化 → 浅色`），注册表随即恢复
- `--selftest` 304 全过；`--smoke` 连续两次 rc=0
- 构建 0 警告 0 错误

## 下载

`MouseWheelEnhancement.exe`（约 0.58 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。
需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 跟随系统主题修复" % TAG,
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
