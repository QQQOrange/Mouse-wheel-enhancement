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
BODY = """## 滚动更顺：文本排版模式 + 帧合并

### 症状

反馈「主要是滚动时候感觉有些不流畅」。

### 定位

先排除已知项：`--frametrace` 四场景帧间隔 6.25 ms 级、步长起伏 ≤2.2×；
`--wheeltest` 单格 60 px 正确；`InputInjector` 只有一次 `SendInput`。
问题集中在本程序自己的窗口（帮助页文本最密集）。

### 三个原因与修法

1. **帮助页全部文本用 `TextFormattingMode.Ideal`**（精确排版）——几百个 TextBlock
   一起滚动时测量/渲染明显更贵。改成 `Display` + `LineStackingStrategy=BlockLineHeight`。
2. **搜索框和跳转按钮夹在滚动区里**，每次滚动都要参与重绘。改成顶部固定卡，
   滚动区只剩内容本身。
3. **注入 160 帧/秒、每帧都 `ScrollToVerticalOffset`**，而 WPF 只在渲染帧真正画一次——
   多余的 Arrange 全白做。改成同一渲染帧内累加、`CompositionTarget.Rendering`
   一次应用：Arrange 次数降到渲染帧率，渲染跟不上时自动降频，不会再"越滚越卡"。

### 验证

- `--selftest` 304 / `--frametrace` 四场景 / `--smoke` 七页 / `--wheeltest` 全部通过
- `--wheeltest` 单格仍是 60 px（帧合并没有丢位移）
- 构建 0 警告 0 错误

## 下载

`MouseWheelEnhancement.exe`（约 0.58 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。
需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 滚动更顺" % TAG,
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
