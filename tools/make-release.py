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
BODY = """## 修掉「滚到最后一段突然停住」

### 现象

开着增强滚页面时，收尾的最后一段会"啪"一下定住；同一页面用原生滚动就没有这个问题。

原生滚动没有收尾过程（一个事件就是一格），所以问题不在"平不平滑"，而在**收尾曲线本身是断的**。

### 根因（两层，都得修）

把真实出帧打出来看（160 Hz、惯性 30 ms、滑行 100 ms、滚一格），修复前末尾是：

```
末 6 帧：+1(+6.1ms)  +1(+6.2ms)  +1(+6.1ms)  +1(+6.4ms)  +1(+6.2ms)  +1(+6.0ms)
```

**每次都是 1 单位、间隔恒为 6.2 ms，然后归零。** 1 单位在 160 Hz 上等于
160 单位/秒 = 1.33 格/秒（约 130 px/s）—— 内容还在以每秒一格多的速度走，下一帧直接变 0。

1. **取整把 0 抬成了 1。** `if (v == 0) v = 1` 本意是"别让这一帧白跑"，
   但尾段曲线算出来的量常常是 0.4、0.3，被抬成 1 之后**速度就有了一个恒定下限**：
   曲线在说"越来越慢"，代码执行的是"每帧固定 1 单位"，直到积压空掉。
2. **τ 相对"最后几个单位"太短。** 滚动量的最小单位是 1 单位（1/120 格，约 0.8 px），
   没法再细分。τ=30 ms 的曲线在剩 5 单位时速度还有 100+ 单位/秒，
   最后 4 px 仍在 40 ms 内冲过去。只修第 1 条不够：实测末段速度只从 160 降到 114。

### 修法

| 层 | 改动 |
|---|---|
| 取整 | 不再四舍五入 + 抬底，改成**零头跨帧累积**：算出来 0.4 就攒着，攒够 1 才发 |
| 曲线 | **落地段**：松手后手里不足 1/3 格时，把时间常数平滑抬到 70 ms（只减速、不加速） |
| 滑行 | 反解出的滑行 τ **不低于跟手 τ** —— 否则"把滑行调短"会让收尾反而提速 |
| 收尾 | 删掉"剩 1 单位就整块返回"的捷径 —— 它绕过曲线，把最后一单位在 6 ms 后就发出去，刚建立的减速被截断 |

1 单位是硬下限，那就**拉长两次发送之间的间隔**：曲线想要 1.2 → 0.9 → 0.6 → 0.3 时，
发送序列变成 `1, 0, 1, 0, 0, 1`，而不是 `1, 1, 1, 1`。

### 实测

| 指标 | 修复前 | 修复后 |
|---|---|---|
| 末 6 次发送的间隔 | 恒定 6.2 ms | 12 → 12 → 25 → 37 → 62 ms |
| 末段速度 | **160 单位/秒**（1.33 格/秒） | **33 单位/秒**（0.27 格/秒） |
| 单格总时长 | 126 ms | 298 ms |
| 位移总量 | 一单位不差 | 一单位不差 |

总时长变长是必然的 —— 要让结尾"看起来是停下来的"，那段时间必须真的存在。
觉得拖就把「惯性长度」调小，或者把「滑行」调成 0（干脆的硬停，不做落地段）。

**普通滚动完全不受影响**：关掉平滑、或范围选「仅加速时」，仍然直接 `CallNextHookEx` 放行。

## 顺带：彻底删掉上一版残留的「接管补偿层」

上一版（v1.7.0）停了"把系统行数降到 1 再补回 N 倍"这个**行为**，但把"乘补偿"的**公式**
留在了原地，只让参数恒为 1（`Compensation`、`ScaledUnitsPerFrame`、
`ScaledBacklog`、`WheelEngine` 里的 `comp` 与重发分支、配置字段 `smooth.wheelLines`、
启动时写系统行数、`OnExit` 覆写）。

恒等参数比废代码更坏：函数签名还在问"补偿几倍"，读代码的人会以为还有一层缩放。已整个删除，
现在注入量就是系统原生语义的量：

- 单帧量 = `120 ÷ 每格帧数`，积压上限 = `格数 × 120`，没有第二个乘数；
- **启动和退出都不再写**系统「每次滚动几行」，只有你拖那一项滑块时才写；
- 配置里不再存一份 `wheelLines`（系统就是唯一真相），界面读值/回填/可用状态只有一个入口；
- 整格模式（WPF/WinForms）单次投递上限独立成 16 档 —— 否则 8× 加速会被 5 格积压上限
  悄悄削成 5 格（v1.5.x 的老坑）；
- 新增 `--wheellines [--fix]`：查看系统行数与旧版备份，必要时按备份还原。

## 下载

`MouseWheelEnhancement.exe`（0.56 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。

需要 .NET 10 桌面运行时。

## 校验

```
SHA256 C86D418322D7AD31FF8FB7CC5CCA3AC6D2D2F4DC1A0B3FEA423AA93BFB48C503
```

构建 0 警告 0 错误；`--selftest` **260 条**全过；`--frametrace` 五个场景全过
（末段速度 ≤ 96 单位/秒、首帧 dt ≤ 20 ms、总量守恒）；`--smoke` 退出码 0。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 修掉收尾「砸停」，并拆净接管补偿层" % TAG,
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
