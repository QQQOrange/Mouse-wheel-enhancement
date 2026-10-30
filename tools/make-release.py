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
BODY = """## 帮助页跳转修复 + 全功能体检 + 动画补强

### 修复：帮助页「快速定位」点了不跳转

**根因**：跳转用的是「正文包含关键词」匹配。简介卡里有"…或关掉平滑滚动"这句话，
所以点"平滑滚动"永远命中第一张卡 —— 看起来就是"没反应"。

- 每张卡片现在带 `Tag` 锚点，按钮按锚点**精确匹配**
- 跳转改用绝对偏移滚到卡片顶部（`BringIntoView` 只保证"进视野"，高卡片会只露一角）

### 全功能体检（16 项，全部正常）

- `--selftest` **304 全过**；`--wheeltest` 单格 60 px、兑现 1.48~1.56×
- `--frametrace` 四场景；`--smoke` 七页 + 自增强 12/12
- `--taskcheck` 任务存在且指向启动器；主题 5.9 s 跟随
- 窗口记忆 1120×846 @400,125；`--dialogtest`/`--runtimecheck` rc=0；资源正常

唯一降级项：`Ctrl+Alt+W` 被其它程序占用，已自动退到 **`Ctrl+Alt+Shift+W`**（日志有记录）。

### 动画补强（只加合成层，不触发布局）

- **切页**：165 ms 淡入 + 190 ms 上移 6 px
- **滑块圆点**：hover 放大 1.16×
- **速度条**：260 ms 补间，不再每 400 ms 硬跳
- 开关 Knob：160 ms 滑动（已有）

### 验证

`--selftest` 304 / `--wheeltest` 通过 / `--smoke` rc=0 / `--measure` 正常；
构建 0 警告 0 错误。

## 下载

`MouseWheelEnhancement.exe`（约 0.58 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。
需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 帮助跳转修复与动画补强" % TAG,
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
