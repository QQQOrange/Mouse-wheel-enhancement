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
BODY = """## v1.8.1：自审修复 + 手感曲线预设

v1.8.0 落地后做了一轮逐项自审，修掉 4 个真问题，并补上最后一项待办。

### 修掉的问题

| # | 问题 | 修法 |
|---|---|---|
| 1 | **反向软起步会吃满一个核**：`continue` 会回到循环顶部，而帧指针只有真正发帧时才前进，于是 45ms 内空转 | 改为 `WaitUntil(hold)` 睡到那一刻，且仍能被新输入唤醒 |
| 2 | **连续快滚时倍率浮层会突然消失**：上一轮淡出的收尾回调把刚亮起来的窗口藏掉 | 加显示代次，收尾回调比对代次后作废 |
| 3 | **200% 缩放下浮层跑偏**（`GetCursorPos` 是物理像素，WPF 的 `Left/Top` 是 DIP） | 按窗口 DPI 换算成 DIP 再定位，贴边判断同单位 |
| 4 | **导入配置后开机自启可能指向不存在的路径** | 导入后若新 `launcherPath` 无效而本机有效，则保留本机值 |

另外：浮层改为**先 Show 再定位**（Show 前尺寸是 0）；退出时退订 `Engine.Boosted`，不再往要销毁的 UI 线程封送。

### 新增：手感曲线预设

「平滑滚动」页顶部加了 **干脆 / 顺滑 / 悠长** 三档，只改「收尾时间常数」和「惯性长度」——
这两个是普通用户唯一听得出差别的一对参数，阈值、满速点、摊帧数保持不动。

### 验证

- `--selftest` **330 项全过**
- `--wheeltest` 单格 60px / 45ms 档兑现 1.56×
- `--smoke` / `--measure --show` / `--frametrace` 全 rc=0，期间日志无 WARN/ERROR
- 构建 0 警告 0 错误

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB）/ `.zip` / `.App.exe`。需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 自审修复与手感预设" % TAG,
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
