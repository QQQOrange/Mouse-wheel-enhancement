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
BODY = """## 撤销上一版的「接管」，并修掉它带来的顿挫

### 为什么撤

上一版为了给 WPF 一类程序做出"一格三步"，把系统滚动行数降到 1、注入侧再乘 3 倍补偿。
实机跑下来三个问题：

| 现象 | 原因 |
|------|------|
| 整体变**顿** | 补偿让浏览器的 delta 从 15 涨到 45，**帧数还是 8，每帧步长却粗了 3 倍** —— 摊帧的细腻度被自己的补偿吃掉了 |
| **末尾顿挫** | 整格步长按"剩余档数"自适应（6~16 ms）。档数一路减少 → 步长一路变长，前半密后半稀，末尾就是"拖一下" |
| **争抢感** | 系统行数是全局值，改成 1 之后，所有没走钩子的路径（管理员窗口、被拦下的注入）都按 1 行滚，与补偿打架 |

一句话：为了让少数框架多两步，把所有人的手感拖下水了。**已撤销**。

### 新增：每格滚动格数（与系统双向同步）

平滑页新增「每格滚动格数」滑块（1~10），**直接写进系统设置**：

- 这里调成 5 行 → 系统「设置 → 鼠标 → 每次要滚动的行数」立刻变成 5；
- 在系统那边改成 4 → 回到本软件，滑块和生效值自动跟到 4（重新获得焦点时读一次）。

旁边还有「打开系统鼠标设置」按钮，直接跳到 `ms-settings:mousetouchpad`。

### 末尾顿挫

整格步长改成**固定 12 ms**（三档 36 ms、八档 96 ms），不再随档数变化。
节奏从头到尾一致，末尾才不会"被拖慢一下"。

### 代价（说清楚）

第三方 WPF / WinForms 程序回到"一格一步"，没有三步平滑了 —— 这是那类框架的固有限制
（[dotnet/wpf#5936](https://github.com/dotnet/wpf/issues/5936)）。
**但本程序自己的设置页不受影响**：它已经改成在 `PreviewMouseWheel` 里按 delta 换算像素，
所以和浏览器一样是连续推进的。

## 还有上一版的两处修复

- **设置页一段一段跳**：同上，页面改为按像素滚动后，我们的 8 帧摊帧直接生效。
- **蓝底按钮黑字**：`Themes/Controls.xaml` 里有条隐式 `TextBlock` 样式把前景色写死成
  `TextPrimary`（浅色主题接近黑），优先级高于从按钮继承的白字。已在主按钮模板里就近覆盖。

## 下载

`MouseWheelEnhancement.exe`（0.55 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。

需要 .NET 10 桌面运行时。

## 校验

```
SHA256 4CFBEEF54A8B4D6A365373126AF9A60309872A0F81249C0E436DF1D80BC692B6
```

构建 0 警告 0 错误；`--selftest` **241 条**全过。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 撤销接管，改为每格滚动格数同步；修掉末尾顿挫" % TAG,
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
