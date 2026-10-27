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
BODY = """## 修掉上一版的「加速失效」

v1.6.0 为了给 WPF / WinForms 补平滑，把系统滚动行数降到 1 行、注入侧乘 3 倍补偿。
但**两个以「单位」计的边界没跟着放大**，于是加速被自己掐死了：

| 边界 | 后果 |
|------|------|
| `MaxBacklogUnits`（积压上限 = 5 格 = 600） | 5× 加速要发 1800，被**截断回 600** → 只发出 1/3，加速形同失效 |
| `UnitsPerFrame`（单帧量 = 15） | 每帧只发 15，同样位移要发 3 倍帧数 → 摊帧被拖成三倍长，又慢又黏 |

修法：两个量一起乘补偿。乘上之后帧数、时长、平滑度都和接管前一致，
只有每帧的「单位数」变大 —— 也就是既要位移对，也要帧数对。

自检 235 → **240 条**，新增一组「补偿之后边界也要放大」5 条，
其中一条是直接的不变式：**5× 加速（补偿后 1800 单位）必须 ≤ 积压上限**。
这条断言就是为这个 bug 立的，以后再改边界会被它拦住。

### 顺带调的：整格模式的步进

原来固定 16 ms 一档。档少时正好（三档 48 ms，看得出三步），
但 5× 加速要发 15 档 = 240 ms，画面明显拖在手后面。
现在按档数自适应：`Clamp(90ms / 档数, 6ms, 16ms)` ——
档少仍是 16 ms，档多自动收紧，整段不超过 90 ms。

## 完整的修复链（这一版包含前几版全部内容）

1. **末尾「刻度感」**：删掉三条「到点倾泻」的捷径，改为一条指数曲线走到底。
2. **开头「抢先跳一块」**：线程醒来后才记上一帧时刻。
3. **轻拨翻整页**：WPF/WinForms 按事件次数滚动，摊帧会被当成多次滚动 —— 对它们改用整格语义。
4. **起步护栏**：一段滚动必须同时 > 3 格且 > 70 ms 才允许加速。
5. **接管系统滚动粒度**：3 行 → 1 行 + 3 倍补偿，让那类框架的一格分成三步。

## 下载

| 文件 | 用途 |
|------|------|
| `MouseWheelEnhancement.exe` | **下载这个**。单文件，0.55 MB，双击即用 |
| `MouseWheelEnhancement-win-x64.zip` | 整包（内容同上，压缩后更小） |
| `MouseWheelEnhancement.App.exe` | 主程序本体，供软件内自动更新用 |

需要 .NET 10 桌面运行时（未装时软件会提示并给出下载链接）。

## 校验

```
SHA256 D97CED78511832A3E353806C5A545C8301EDCF66BB6AB9145226D2090AD0338C
```

构建 0 警告 0 错误；`--selftest` **240 条**全过。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 修掉加速失效（补偿没有作用到积压上限）" % TAG,
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
