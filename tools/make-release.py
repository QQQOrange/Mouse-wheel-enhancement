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
BODY = """## 轻轻一滚却翻过整页：真因是 WPF 无视 delta

用户报：**「阈值调到 25 格每秒，还是照样触发。在 KeyboardEnhance 的设置页特别明显。」**

阈值调到 25 还犯，说明**根本不是加速**。上一版已经把倍率那条路用起步护栏堵住了，
能绕过它的只剩一个环节：**摊帧**——`smoothAlways` 默认开着，哪怕倍率是 1.0，
一格也会被拆成 `framesPerNotch`（默认 8）个小事件。

### 两类完全不同的滚动语义

| 语义 | 谁是这样 | 收 8 个 `delta=15` 会怎样 |
|------|---------|--------------------------|
| 按 delta 比例滚像素 | Chromium、Qt、Office、系统 Edit | 和收 1 个 `delta=120` 一样远，摊帧安全 |
| **按事件次数滚一档** | **WPF、WinForms** | **滚 8 档，一格变一屏** |

WPF 这一条是它自己的已知缺陷，官方 issue [dotnet/wpf#5936](https://github.com/dotnet/wpf/issues/5936) 的原话：

> the `ScrollViewer` **always scrolls a fixed amount** in response to mouse wheel events
> despite `MouseWheelEventArgs` returning the Delta value

至今没改（改了会动所有人的滚动距离）。所以它只看向号、不看大小。

### 实测（UIA 读 ScrollViewer 的真实位置）

用增强器自己的签名 `WBOO` 注入可以绕开钩子，测到 WPF 的原始行为；`dwExtraInfo=0` 则会被钩子接管。

修复前（v1.5.7）：

| 注入 | 位移（百分点） |
|------|---------------|
| `delta=-120`（绕过增强器） | 6.49 |
| `delta=-40`（绕过增强器） | **6.49** |
| `delta=-15`（绕过增强器） | **6.49** |
| **`delta=-120` 经增强器** | **74.04（100%，直接到底）** |

前三行是铁证：120 / 40 / 15 滚得分毫不差，WPF 真的只看方向。
最后一行：一个事件被放大成 **11.4 档**，页面直接到底。

修复后（v1.5.8）同一页面同一动作：**6.49 个点**，与纯 WPF 一档完全一致。

### 修法：按目标的滚动语义分流

- 解析目标时顺手取一次顶层窗口类名（跟着 pid 缓存，不增加每次事件的开销）；
- `HwndWrapper[...]` 判为 WPF、`WindowsForms10.*` 判为 WinForms；
- 命中则走**整格语义**：不加速时原样放行（一个事件正好一档）；
  加速时把倍率兑现成**事件个数**——3× 就发 3 个整格，不再发小 delta；
- 小于一格的 delta（触控板）在这类框架里本来就是一档，原样放行，绝不放大。

### 取舍

**WPF / WinForms 应用里没有摊帧的平滑感。** 那类框架的最小粒度就是一格，
再细的 delta 只会被当成"又一次滚动"——想让它平滑只能改它的源码。
换来的是：这些应用里一格永远是一格。

顺带说一句，这也解释了"为什么只在某些软件里明显"：那些软件是 WPF/WinForms 写的，
不是"页面短"。用户看到的"短页面"多半就是各种设置页。

## 一并包含上一版的两处修复

**① 末尾那一下「刻度感」**：删掉三条「到点倾泻」的捷径（`maxLatencyMs` /
`glideMaxMs` / 积压超上限），改成一条指数曲线走到底，滑行 τ 由时长反解，
收尾 1 单位一次收掉。旧版末尾会突然来一发 6~30 单位，惯性调得越长倒得越多，
所以怎么调参数都消不掉。

**② 开头那一下「抢先跳一块」**：动画线程从长睡醒来时 `_lastFrameTicks` 是陈旧的，
首帧 dt 被夹到 50 ms，单格滚动开头先跳 0.375 格。改成醒来后才记时刻。

**③ 起步护栏**：一段滚动必须同时 > 3 格且 > 70 ms 才允许加速，
挡住"轻拨被拆成几个 10 ms 事件"造成的误判。

**④ 配置迁移**：`schemaVersion` 3 → 4，老配置的滑行值换算成新默认，
`maxLatencyMs` / `glideMaxMs` 移除，倍率、阈值、名单等设置一个都不动。

## 下载

| 文件 | 用途 |
|------|------|
| `MouseWheelEnhancement.exe` | **下载这个**。单文件，0.55 MB，双击即用 |
| `MouseWheelEnhancement-win-x64.zip` | 整包（内容同上，压缩后更小） |
| `MouseWheelEnhancement.App.exe` | 主程序本体，供软件内自动更新用 |

需要 .NET 10 桌面运行时（未装时软件会提示并给出下载链接）。
Windows 11 x64 实测；Windows 10 上圆角自动降级为直角，其余功能不变。

## 校验

```
SHA256 EE931F74D610F2CDEF1863DC1277EDD470F1E5185BE81CB6FBCFC5047935CCF2
```

构建 0 警告 0 错误；`--selftest` **219 条**全过（新增「滚动语义」一组 15 条）。
端到端：同一台机器、同一个窗口、同一次点击，修复前 74.04 个百分点，修复后 6.49。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 轻拨翻整页的真凶：WPF 无视滚动增量" % TAG,
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
