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
BODY = """## 微信里加速失效：Qt 的整数除法把拆帧吃掉了

### 现象

浏览器里加速正常；微信 4.x 的聊天列表快滚**完全没有加速**，慢滚却一切正常。

### 根因

微信 4.x 是 **Qt**（顶层类名 `Qt51514QWindowIcon`）。我们的滚动语义只把
WPF（`HwndWrapper[...]`）/ WinForms 当成"按事件滚动"，Qt 被当成"按 delta 比例"，
于是把一格 120 **拆成 4~8 帧、每帧 15~30 单位**发出去。

而 Qt 的 `QScrollBar::wheelEvent` 是：

```cpp
int delta = event->angleDelta().y() / 120;   // 整数除法
```

**15 / 120 = 0** —— 拆帧发出去的每一个事件都"滚 0 档"，加速这一段等于什么都没做。
慢滚低于阈值时原样放行，所以慢滚完全正常。

### 修复

1. **Qt 归入"按事件滚动"**：识别 `Qt<版本>QWindowIcon`（Qt 5/6），
   改成发**整格**（倍率几倍就几个 ±120），和真实滚轮事件一模一样。
   代价是 Qt 应用里没有亚格平滑（和 WPF 同理，由框架粒度决定）。
2. **UIPI 兜底**：微信常以管理员运行，非管理员进程的 `SendInput` 会被系统**静默丢弃**。
   原来的逻辑会"吞掉原事件再注入"，那时用户看到的是**滚动卡死**。
   现在先比一次权限，权限不够就原样放行（加速失效但滚动正常），日志留 WARN。
3. **可观测**：每个程序首次整格加速记一条日志
   `整格加速：weixin.exe（Qt51514QWindowIcon）倍率 3.00× → 3 格`，
   排查"某软件里没反应"时一眼就能看出引擎有没有接管。

### 验证

- `--selftest` **311 项全过**（新增 4 条 Qt 识别断言）
- `--wheeltest` 通过：单格 60 px、兑现 1.48×、猛甩 2.11×
- `--smoke` / `--measure --show` / `--frametrace` 全部 rc=0
- 浏览器等"按比例"应用分类未变，仍是摊帧平滑（无回归）

> 注：本机微信聊天列表未溢出、且微信以管理员运行（非管理员探针被 UIPI 拦），
> 无法自动注入验证。请在本程序正常运行下于微信里快滚确认，
> 若日志出现"整格加速：weixin.exe …"，说明引擎已接管。

## 下载

`MouseWheelEnhancement.exe`（约 0.58 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。
需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 微信加速修复" % TAG,
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
