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
BODY = """## v1.8.5：修末尾「卡一下，然后猛地跳一段」

滚动快到末尾时的一卡一冲，这次按实测定位到三处结构性原因。

### 怎么测出来的

给 `--frametrace` 加了两个能力：**末段 τ 打点**、以及 `jitter=12`
（在出帧回调里睡 12ms，复现"这台机器跟不上"）。改前实测（连甩三格）：

```
末 8 帧 τ：32.2 → 62.3 → 57.3 → 52.3 → 47.4 → 42.4 → 40 → 37.5 ms
前 6 帧：  +16  +140  +58  +43  +30  +19  单位
```

τ 一帧跳 **1.93 倍**（速率瞬间减半 → 卡一下），单帧 **+140 单位**（前后只有 16 / 58 → 猛跳一段）。

### 原因一：收尾上界有个硬开关，τ 在末尾不连续

旧代码只在"剩余 > 12 单位"时收紧 τ。剩余掉到 12 以下那一刻上界突然退出，
τ 从 32ms 一帧跳回 62ms，紧接着落地插值又把它压回来（62→37）—— 一卡一冲。

现在：上界对所有进度都参与（`left` 内部夹到 `CloseUnits`，连续），
下限取软着陆 τ —— 上界只负责"别拖"，绝不允许把 τ 压到比软着陆还小（那就是猛冲）。

### 原因二：掉帧降级会把滑行和落地**整块**跳过

一句 `&& !_degraded` 把落地段也一起关掉了。降级一触发，τ 从 60~70ms 掉回跟手值，
软着陆同时消失，末段直接砸停；而且进出都在 40 这个阈值上，贴住时会逐帧翻转。

现在：降级只做一件事 —— τ 不高于跟手值（别再额外变慢），**落地段任何时候都保留**；
进出阈值拉开成 45 / 12（迟滞），恢复步长 ×2。

### 原因三：迟到的那一帧"一次性补回来"

`FrameAmount = pending × (1 - e^(-dt/τ))`。某帧迟到到 18ms（帧间隔 6.25ms），
那一帧就发掉积压的 43% —— 保持平均速度是对的，观感是错的。

现在：参与曲线计算的 dt 夹到"帧间隔 × 2"，迟到的量摊到后面几帧。
**总位移不变**（`--wheeltest` 兑现 1.57~1.58×、猛甩 2.11×，没有下降）。

### 你自己怎么验证

```
MouseWheelEnhancement.App.exe --frametrace            # 正常节奏
MouseWheelEnhancement.App.exe --frametrace jitter=12  # 模拟慢机器
```

新增两条断言，对着这次的症状：
- **末段 τ 不骤降**（≥0.55）—— 骤降 = 突然加速 = 猛跳一段
- **末段 τ 不骤升**（≤1.65）—— 骤升 = 突然减速 = "卡一下"

改后四个场景（含 jitter）τ 比值全部 **1.00**、末段速度 52~63 单位/秒、落地段 81~175ms。

### 回归

`--selftest` **336 项全过**；`--wheeltest` 兑现 1.58×/1.57×、猛甩 2.11×；
`--smoke` / `--measure --show` / `--featurecheck` 全 rc=0，日志 0 条 WARN/ERROR。

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB）/ `.zip` / `.App.exe`。需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 末尾卡顿与猛跳修复" % TAG,
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
