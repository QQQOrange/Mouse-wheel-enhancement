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
BODY = """这一版修了两个手感问题，加起来就是"滚动不该有的那几下突然"。

## 一、滚到末尾最后一下的「刻度感」

用户报：**「滚动到末尾时最后一下会有明显的刻度感/顿挫，调整参数也无法消除。」**

### 根因不在参数，在算法里有三条「到点倾泻」的捷径

旧版本的摊帧本来是指数曲线，但会被三条捷径打断，它们一律 **「时间一到，把还没发完的积压整块 return 出去」**：

| # | 触发条件 | 后果 |
|---|---------|------|
| ① | `maxLatencyMs`（最大延迟）到期 | 剩余积压一帧发完 |
| ② | `glideMaxMs`（滑行上限）到期 | 同上 |
| ③ | 积压超上限时把截止时间压到下一帧 | 同上，而且更快 |

指数曲线已经把速度压到 1~2 单位/帧了，末尾突然来一发 6~30 单位 —— 就是那一下"刻度"。实测旧版末尾 12 帧：

| 场景 | 末尾 12 帧 |
|------|-----------|
| 单格，惯性 110 | `4 3 3 3 2 2 2 1 1 1 1` **`6`** |
| 单格，惯性 160 | … `1 1` **`13`** |
| 单格，惯性 220 | … `2 2` **`19`** |
| 单格，惯性 300 | … `2 2` **`27`** |
| 144Hz，惯性 300 | … `1 1` **`22`** |
| 连甩 4 格 ×3 | `46` **`358`** **`280`** |

**这就是「调参数也消不掉」的原因**：惯性调得越长、积压越多，末尾倒出去的越多。调参只是把那一下从 6 换成 27。

### 改法：一条指数曲线走到底，不留任何到期路径

- 每帧发 `pending × (1 − e^(−dt/τ))`，**删掉 `maxLatencyMs` / `glideMaxMs` 和全部倾泻逻辑**；
- 松手后不另换曲线，只把 τ 从跟手值经 **55 ms smoothstep** 过渡到滑行值（τ 一步跳变 45 → 110 会让速率瞬间掉一半，手感就是"咔"）；
- 滑行 τ 由**滑行时长反解**：`τ = glideMs / ln(积压 / 1)`，保证到那一刻只剩最后 1 个单位 —— 「惯性 300 ms」是真的 300 ms，不是"到 300 ms 强行截断"；
- 收尾阈值 **1 单位**（1/120 格，亚像素）一次收掉；
- 单帧上限 **3 个标准帧量**；积压超上限改成**截断**（宁可少滚一点，不要跳一下）。

改完的末尾 12 帧：单格 `4 3 2 2 1 1 1 1 1 1`；144Hz `1 1 1 1 1 1 1 1 1 1`；连甩 4 格 ×3 `17 11 8 5 4 2 2 1 1 1`。

### 顺带修掉：开头那一下「抢先跳一块」

动画线程没活干时会无限期睡下去，**旧代码在睡之前就把"上一帧时刻"记成当时** —— 线程睡了几分钟，醒来后 dt = 整段空闲（被夹到 50 ms），第一帧就按 50 ms 的量发，单格滚动开头先跳出去 **0.375 格**。

改成**醒来之后**才记这个时刻。为此新增诊断开关 `--frametrace`：真开一条动画线程、先让线程闲够、再推入滚动量，量"空闲之后第一帧发了多少"（不装钩子、不建窗口、不注入）。

| 场景 | 撤掉修复 | 有修复 |
|------|---------|-------|
| 空闲后滚一格 | 首帧 **80 单位** | 首帧 **1 单位** |
| 连甩六格 | 首帧 **80 单位** | 首帧 **1 单位**，最大单帧 64 |

## 二、轻轻一拨却翻过整整一页

用户报：**「设了滚动超过 9~10 个格子才触发加速，但实际只轻轻滚动一次、理论上最多五六个格子，画面就直接翻过整整一页。关掉软件立刻正常，短页面上也会出现。」**

### 根因：阈值量的是「速度」，不是「累计格数」

`fastThresholdPerSec` 的单位是**格/秒**。滚得再多，只要慢，永远不该加速；反过来，一瞬间出现几个事件，速度就能爆表 —— **和"滚了几下"完全无关**。

而速度是逐事件算的 EMA：`alpha` 与 dt 成正比、`inst` 与 dt 成反比，两者相乘 ≈ `1000/70 = 14.3`。也就是说**不管 dt 多小，每个事件都能把速度推高约 14 格/秒**。默认阈值 10：

| 事件 | dt | 速度 | 倍率 |
|------|----|------|------|
| 第 1 下 | 500 ms | 2 格/秒 | 1.0× |
| 第 2 下 | 10 ms | ≈ 13 格/秒 | 1.4× |
| 第 3 下 | 10 ms | ≈ 25 格/秒 | 2.5× |
| 第 4 下 | 10 ms | ≈ 35 格/秒 | 顶到上限 |

**轻轻一拨在物理上就是 2~3 个事件**（手指抖动、解码器抖动、驱动拆分），于是必然中招。上限调到 5× 时，一格直接变四格 —— 在内容只有一屏多的页面上就是一整屏。长文本页面里同样的位移只是"滚过一段"，所以看不出来：**不是长页面不犯，是看不出来**。

还有一处放大器：`dt` 下限原是 1 ms，两个"同一毫秒"的事件会算出 `1000 格/秒` 这种没有物理意义的数。

### 三道闸

| # | 闸门 | 挡掉什么 |
|---|------|---------|
| ① | `dt` 下限 1 → **8 ms** | 上千格/秒的离谱瞬时值 |
| ② | 起步护栏：一段滚动必须**同时** > 3 格 **且** > 70 ms 才允许加速 | 轻拨、解码器抖动、驱动拆事件 |
| ③ | 渐入：护栏之后再用 3 格从 1× 线性爬到配置倍率 | 第 4 格从 1× 直接窜到 5× 的突兀感 |

护栏要两条条件是因为它们各挡一类假信号：只按格数，挡不住"一拨连发六格但只有 25 ms"；只按时长，挡不住"慢慢拨但连拨好几秒"。两条同时成立，才是"人的手在连续地滚"。

一段的边界用 120 ms，于是**慢滚（每格 200 ms 以上）每格都算新的一段，永远凑不满护栏 —— 慢滚永远不会加速**。

### 效果（按投诉者的真实参数：阈值 9 / 满速 40 / 上限 5×）

| 场景 | 修复前 | 修复后 |
|------|--------|--------|
| 两下 10 ms 的轻拨 | 168 单位（1.4×） | **120 单位（原样一格）** |
| 三下 10 ms | 300 单位（2.5×） | **120 单位** |
| 六格挤在 25 ms | 顶到上限 | **120 单位** |
| 慢滚六格（每格 200 ms） | 1.0× | 1.0×（不变） |
| 持续 300 ms 快滚 | 上限 | 上限（不变） |

未加速时**永远是一格 120 单位**，加速时是 `120 × 倍率`，不多也不少 —— 短页面上的增量与设定参数一致。

代价：快速滚动的**起步**慢 3 格（约 70 ms）。抖动和真滚动的唯一区别就是"持续了多久"，不付这点延迟就必然把抖动当滚动。

## 三、参数变化

| 参数 | 旧 | 新 |
|------|----|----|
| 最大延迟 `maxLatencyMs` | 有（倾泻触发器） | **删除** |
| 滑行上限 `glideMaxMs` | 有（倾泻触发器） | **删除** |
| `glideTauMs` | 时间常数，默认 150 | **滑行时长**，默认 300 ms |
| 松手判定 `glideDelayMs` | 26 ms | 30 ms |

配置自动迁移（`schemaVersion` 3 → 4）：老配置的滑行值换算成新默认、两个倾泻参数被移除，**倍率、阈值、名单等设置一个都不动**。

## 四、顺带修掉：自检结论其实传不出来

启动器拉起主程序后是 `Process.Start(...); return 0;` —— 不等、也不回传退出码。于是 `--selftest` 永远返回 0，`tools/publish.ps1` 里那句"两个自检都通过"等于没检查。现在对"跑完就退出"的诊断开关等它跑完并把结论原样返回。

## 下载

| 文件 | 用途 |
|------|------|
| `MouseWheelEnhancement.exe` | **下载这个**。单文件，0.54 MB，双击即用 |
| `MouseWheelEnhancement-win-x64.zip` | 整包（内容同上，压缩后更小） |
| `MouseWheelEnhancement.App.exe` | 主程序本体，供软件内自动更新用 |

需要 .NET 10 桌面运行时（未装时软件会提示并给出下载链接）。
Windows 11 x64 实测；Windows 10 上圆角自动降级为直角，其余功能不变。

## 校验

```
SHA256 F15911F61CC492262113241B07260F033FA507039CD4FFEFAEEB88F3A0F193EA
```

构建 0 警告 0 错误；`--selftest` **204 条**全过（新增 12 条里，有两条直接用真实参数：阈值 9 / 满速 40 / 上限 5×）。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 末尾不再「刻度」，轻拨不再翻页" % TAG,
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
