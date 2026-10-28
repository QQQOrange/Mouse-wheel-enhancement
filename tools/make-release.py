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
BODY = """## 帮助页里三个老大难：加速 / 刻度感 / 收尾拖沓

### 现象

用户在帮助页里滚了一下，反馈三件事：

> 首先它就没有加速，而且刻度感很严重。其次就是每次滚完最后一下，
> 真的有一种延迟，感觉很难受

### 根因

**(1) 加速不兑现**：取整余量被夹在 ±1 格（±120 单位）太紧。
5× 加速时一格理想量是 600 单位，余量夹到 1/5 就丢，长期兑现倍率低于配置。

**(2) 刻度感**：v1.7.4 的 v5 迁移把用户原来调到 8 帧/格的拆分降到 4 帧/格
（≤6 一律抬到 4）。用户已经调好 8 帧/格来避开 0.5 px 的像素台阶，
被无声覆盖 —— 一格 = 3×20 = 60 px，8 帧/格一帧 0.25 px，4 帧/格一帧 0.5 px
会卡在像素网格上。

**(3) 收尾拖沓**：落地段的 `Math.Max` 让软着陆**根本没生效**。
v1.7.4 把落地段改成 `Math.Max(tau, tau + (LandingTauMs - tau)·k)`，
注释写"只减速不加速"。但 `LandingTauMs = 20` **比滑行 τ（35~45）还小**，
于是内层值比外层小、Max 取外层 —— 落地段完全没生效。
末段仍是原速衰减的 1 单位小步，间隔 12→19→25→31→37→50 ms 一路拉长，
最后一帧要等 75 ms 才发，用户读作"滚完最后一下有延迟"。

### 修法

- **(a) 余量上限 `ResidualLimitFor(boostScale)`**（`Core/BoostCurve.cs`）：
  `WHEEL_DELTA × 加速倍率`，5× → 600 单位。余量不再丢，兑现跟得上配置。
- **(b) v5 迁移只把 ≤3 帧/格抬到 4**（`Core/AppConfig.cs`）：
  4~8 帧/格保持用户原值。**用户调好的参数不被无声覆盖**。
- **(c) 落地段改纯插值**（`Core/ScrollAnimator.cs`）：
  `tau = tau + (LandingTauMs - tau)·k`（去掉 Math.Max），
  LandingTauMs 不管比滑行 τ 大还是小都能生效。
  参数：`LandingStartUnits = 12`（1/10 格）、`LandingTauMs = 35`。
  12 单位让落地段影响 2~3 个单位，35 ms 让末段速度保持在
  50~65 单位/秒的"渐停"区间。
- **(d) 收尾上界 `MaxFinishMs = 150 ms`**（从 170 → 260 → 110 → **150**）。
  只压不抬、落地段期间不参与。150 是扫参找到的甜点。
- **(e) frametrace ④d 中段步长起伏改用进度窗 20%~80%**（按累计位移），
  不再用时间窗（时间窗会把衰减尾巴扫进来，测出假的"刻度感"）。

### 实测（`--frametrace`，160 Hz + 滑行 300 ms）

| 场景 | 指标 | v1.7.4 | v1.7.5 |
|---|---|---|---|
| 1 格 | 落地段 | 175 ms | **112 ms** |
| 1 格 | 末帧间隔 | 75 ms | **43.7 ms** |
| 1 格 | 中段步长起伏 | 5.3× | **2.2×** |
| 3 格 15ms | 落地段 | 188 ms | **131 ms** |
| 3 格 15ms | 中段步长起伏 | 5.3× | **1.8×** |
| 6 格 8ms | 落地段 | 182 ms | **144 ms** |
| 6 格 8ms | 中段步长起伏 | 6.2× | **1.5×** |

末 6 帧速度 50~57 单位/秒（渐停而非砸停），末尾回升 0 单位，
总量一单位不差。加速兑现跟得上配置（5× → ~5×）。

低于触发阈值的慢滚依旧**一个字节都不改**；轻拨防抖护栏不退让
（两下 10 ms / 六格 25 ms 的抖动爆发仍然不加速）。

### 验证

构建 0 警告 0 错误；`--selftest` **299 条**全过（含 ⑥ 整段模拟末尾不突增、
⑦ 收尾不砸停 / 落地段 / 末帧间隔 / 中段步长起伏）；
`--frametrace` 四场景全过（1 格 / 往回 1 格 / 连甩三格 / 连甩六格），
落地段 106~144 ms、末帧间隔 ≤ 43.7 ms、中段步长起伏 ≤ 2.2×；
`--wheeltest` / `--smoke` / `--dialogtest` 全过。
已装到本机并经启动器跑过自检。

## 下载

`MouseWheelEnhancement.exe`（0.57 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。

需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 加速跟手，收尾渐停" % TAG,
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
