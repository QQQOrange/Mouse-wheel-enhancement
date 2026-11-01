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
BODY = """## v1.8.6：给「末尾突跳」配一把真实路径的尺子

v1.8.5 修的三处原因都在动画器里，而 `--frametrace` 走的是诊断回调（不注入）。
你眼睛看到的位移还要过 `SendInput → 目标窗口 → WPF 布局 → 像素` 这一段 ——
那段 `FrameSink` 看不到。所以这一版补了 `--scrollsmooth`。

### `--scrollsmooth` 做什么

把光标真的挪到帮助页上、真的 `SendInput` 八格（45 ms/格），
在每一渲染帧读一次 `ScrollViewer.VerticalOffset`，然后按"逐帧步长"判平滑度：

| 判据 | 含义 | 阈值 |
|---|---|---|
| 单帧突刺 | 最大步长 / 中位步长 | ≤ 3.5× |
| 末段不回升 | 末 8 步的后一步 / 前一步 | ≤ 1.6× |
| 末段不拖 | 最后两次变化的间隔 | ≤ 80 ms |

### v1.8.5 之后的实测

```
注入 8 格，采样 190 帧，总位移 705.5 px（88.2 px/格）
单帧突刺 2.6×（最大 20.5 / 中位 8 px）
末段不回升 1.00×，最后一步间隔 30.9 ms

末 10 步：1  1  1  0.5  0.5  0.5  0.5  0.5  0.5  0.5 px
  间隔：  6  6  7  5  7  12  13  19  31 ms
```

末尾是"步长不变小、间隔单调拉长"——渐停该有的样子。中段那个 2.6× 是倍率爬升
（3.5 → 10.5 px，速度刚过阈值），属正常加速。

### 现在有四把尺子，各管一层

| 模式 | 路径 | 抓什么 |
|---|---|---|
| `--selftest` | 纯函数 | 算得对不对 |
| `--featurecheck` | 自带引擎 + 临时配置 | 接线有没有接上 |
| `--frametrace` | 动画器回调（不注入） | 曲线本身有没有不连续 |
| **`--scrollsmooth`** | **真注入 + 真渲染** | **眼睛里有没有突跳** |

### 验证

`--selftest` 336 / `--frametrace`（含 `jitter=12`）/ `--featurecheck` / `--wheeltest` /
`--scrollsmooth` / `--smoke` / `--measure --show` **全部 rc=0**，日志 0 条 WARN/ERROR。

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB）/ `.zip` / `.App.exe`。需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 逐帧平滑度实测" % TAG,
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
