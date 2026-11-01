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
BODY = """## v1.8.9：最后一下的「停住之后又补一下」

### 先把症状量出来

给 `--scrollsmooth` 加了两个指标：**末步空窗**（最后一次位移距上一次多久）和
**末段速度回跳次数**。改前实测：

```
末 10 步：0.5px  0.5(+6)  0.5(+13)  0.5(+5)  0.5(+13)  0.5(+13)  0.5(+18)  0.5(+19)  0.5(+27)  0.5(+55)
```

**最后一步空窗 54.5ms** —— 画面先停住 54ms，然后补 0.5px 再结束。
人眼把前 40ms 读作"已经停了"，后面那一下就是独立的一跳。这就是"最后一下生硬"
的真正形态：不是速度太快，而是**停住之后又动了一下**。

### 根因

只剩几个单位时，每帧该发的量已经小于 1 单位，只能靠零头累积 ——
**什么时候凑满 1 单位取决于累积速度**，于是间隔忽长忽短（6 13 5 13 13 18 19 27），
最后一步尤其容易拖出大空窗。

### 修法：尾段改成"按曲线定时发"

剩 ≤ 8 单位时，一次固定发 1 单位，下一次的时刻按 `τ·ln(p/(p-1))` 推后
（这正是指数衰减从 p 走到 p-1 单位所需的时间），并封顶 28ms。
于是尾段每个单位都精确落在曲线上，间隔天然单调变长，且不会再有"停住再补"。

### 实测

| 指标 | 改前 | 改后 |
|---|---|---|
| 末步空窗 | **54.5 ms** | **26.7 ~ 30.9 ms** |
| 末段间隔 | 6 13 5 13 13 18 19 27 55 | 5 6 12 12 12 19 19 25 31（单调） |
| 收尾总时长 | 235~469 ms | 235~449 ms（速度自适应保留） |

### 顺带说明：为什么不是"再放慢一点"

试过继续放大 τ —— 空窗反而从 30ms 涨到 55ms：**越慢的尾段越容易"停住再补一下"**。
所以这版不是调参数，而是换发射方式：把"何时发送"从"攒够就发"改成"按曲线定时"。

### 验证

`--selftest` **345 项**（+3：尾段间隔单调 / 有上限 / 有下限）；
`--frametrace`（含 `jitter=12`）四个场景末帧间隔 28~32ms；
`--scrollsmooth` / `--wheeltest`（兑现 1.56×）/ `--featurecheck` / `--smoke` / `--measure` 全 rc=0，
日志 0 条 WARN/ERROR。

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB）/ `.zip` / `.App.exe`。需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 末段定时发送" % TAG,
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
