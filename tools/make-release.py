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
BODY = """## 加速终于跟手，收尾不再有段落感

### 现象

v1.7.3 把倍率容量随上限放大之后，"倍率读着 5×、页面只快一两成"的截断问题消失了，
但用户实测仍有两个问题：

1. **加速还是感觉不到**：钩子读数爬到 2~4×，页面只快一两成；
2. **收尾仍有段落感**：动画结束时是"匀速走一段、然后停"，不是渐停。

### 根因

**加速不跟手**是三层前置消耗叠加：

| 层 | v1.7.3 | 吃掉几格 |
|---|---|---|
| 起步护栏（> 2 格） | WarmupUnits = 240 | 2 |
| 渐入（2 格） | RampUnits = 240 | 2 |
| EMA τ=50 ms 收敛 | RateTauMs = 50 | 2~3 |

8 格段落里前 4~5 格都在爬坡，等倍率爬起来拨动已经结束了 ——
兑现 1.95×（曲线读数 2.53×），持续猛甩 3.23×（读数 4.20×）。

**收尾段落感**是单帧上限触顶 + 落地段太窄：

- 用户配置 8 帧/格 × 5× → 单帧量 75，3 倍上限 = 225 单位/帧；
- 160 Hz 上折合 36000 单位/秒 = 300 格/秒，听着够；
- 但 τ 曲线在猛甩时峰值要 400+ 格/秒，反复触顶的形态是
  「这一帧发满 225 → 下一帧又发满 225」，节奏被拉成等间隔大步；
- 落地段只在剩 1/3 格（40 单位）时生效，8 帧/格下只剩 3~4 帧可调，
  τ 还没抬起来就落地了。

### 修法

- **(a) EMA τ 50 → 30 ms**：45 ms/格 时一格到 78%、两格到 95%，
  短段落才有"曲线说多少就是多少"的兑现。
- **(b) 护栏 2 格 → 1 格、渐入 2 格 → 1 格**：防抖主力是 70 ms 时长护栏
  和 120 ms 分段，格数护栏只负责挡"一两下"误触，1 格足够。
  合计前置消耗从 4~5 格降到 2~3 格。
- **(c) 单帧上限 3× → 4×**：用户配置下从 225 → 300 单位/帧
  （160 Hz 上 400 格/秒），曲线峰值不再触顶，出帧回到自然衰减。
  一帧 300 单位 = 2.5 格，仍是"连贯滚动"而非"大跳"。
- **(d) 落地段 1/3 格 → 1/2 格、τ 70 → 90 ms**：给落地段留出 5~8 帧，
  平滑过渡才有地方发生；末段速度从 ~51 压到 ~25 单位/秒。

### 实测（用户同配置：触发 10 / 饱和 39 / 上限 5×，8 帧/格，160 Hz）

| 手速 | 曲线倍率 | v1.7.3 兑现 | v1.7.4 兑现 |
|---|---|---|---|
| 45 ms/格（22.2 格/秒） | 2.53× | 1.95× | **2.4×** |
| 持续猛甩 16 格 @35 ms | 4.20× | 3.23× | **3.9×** |

| 收尾指标 | v1.7.3 | v1.7.4 |
|---|---|---|
| 末 6 帧平均速度 | 38~51 单位/秒 | **24~25 单位/秒** |
| 落地段帧数（8 帧/格） | 3~4 | **5~8** |
| 落地段时长 | ~80 ms | **~120 ms** |

低于触发阈值的慢滚依旧**一个字节都不改**；轻拨防抖护栏不退让
（两下 10 ms / 六格 25 ms 的抖动爆发仍然不加速）。

### 验证

构建 0 警告 0 错误；`--selftest` **280 条**全过（护栏/渐入/落地段断言
按新常量更新）；`--frametrace` 四场景全过，末段速度 24~25 单位/秒
（阈值收紧到 72），单格摊帧手感不变（首帧 16~18 单位、434 ms 发完、总量守恒）；
`--wheeltest` 手速扫描 + 持续猛甩段全过。
已装到本机并经启动器跑 `--dialogtest` / `--selftest` 全过。

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
