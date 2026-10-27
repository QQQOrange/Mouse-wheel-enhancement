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
BODY = """## 滚到末尾那一下「刻度感」没了

用户报：**「滚动到末尾时最后一下会有明显的刻度感/顿挫，调整参数也无法消除。」**

### ① 根因不在参数，在算法里有三条「到点倾泻」的捷径

旧版本摊帧用的是指数曲线 —— 但曲线跑着跑着会被三条捷径打断，
它们一律 **「时间一到，把还没发完的积压整块 return 出去」**：

| # | 触发条件 | 后果 |
|---|---------|------|
| ① | `maxLatencyMs`（最大延迟）到期 | 剩余积压一帧发完 |
| ② | `glideMaxMs`（滑行上限）到期 → 滑行态变 false → 掉进 ① | 同上 |
| ③ | `Push` 时积压超上限 → 把截止时间压到下一帧 | 同上，而且更快 |

指数曲线已经把速度压到 1~2 单位/帧了，末尾突然来一发 6~30 单位 —— 就是那一下"刻度"。

实测旧版末尾 12 帧（1 格 = 120 单位）：

| 场景 | 末尾 12 帧 |
|------|-----------|
| 单格，惯性 110 | `4 3 3 3 2 2 2 1 1 1 1` **`6`** |
| 单格，惯性 160 | … `1 1` **`13`** |
| 单格，惯性 220 | … `2 2` **`19`** |
| 单格，惯性 300 | … `2 2` **`27`** |
| 144Hz，惯性 300 | … `1 1` **`22`** |
| 连甩 4 格 ×3 | `46` **`358`** **`280`** |

**这就是「调参数也消不掉」的原因**：惯性调得越长、积压越多，末尾倒出去的越多。
调参只是把那一下从 6 换成 27，换不掉它。

### ② 改法：一条指数曲线走到底，不留任何到期路径

- 每帧发 `pending × (1 − e^(−dt/τ))`，**删掉 `maxLatencyMs` / `glideMaxMs` 两个参数和全部倾泻逻辑**；
- 松手后不再「另换一套曲线」，只把 τ 从跟手值经 **55 ms smoothstep** 过渡到滑行值 ——
  τ 一步跳变（45 → 110）会让出帧速率瞬间掉一半，手感就是"咔"一下；
- 滑行 τ 由**滑行时长反解**：`τ = glideMs / ln(积压 / 1)`，保证到那一刻只剩最后 1 个单位，
  所以「惯性 300 ms」是真的 300 ms，不是"到 300 ms 强行截断"；
- 收尾阈值 **1 单位**（1/120 格，亚像素）一次收掉，替代原来的"每帧下限"；
- 单帧上限 **3 个标准帧量**（默认 90 单位 = 3/4 格）；积压超上限改成**截断**（宁可少滚一点，不要跳一下）。

改完的末尾 12 帧：

| 场景 | 新版末尾 |
|------|---------|
| 单格（任意惯性） | `4 3 2 2 1 1 1 1 1 1` |
| 单格，惯性 300 | `3 3 2 2 1 1 1 1 1 1` |
| 144Hz，惯性 300 | `1 1 1 1 1 1 1 1 1 1` |
| 连甩 4 格 ×3 | `17 11 8 5 4 2 2 1 1 1` |

### ③ 顺带修掉第二个 bug：开头那一下「抢先跳一块」

只有末尾有问题是错觉。排查时发现开头也有一下反向的抖动：
动画线程没活干时会 `WaitOne(无限)` 睡下去，**旧代码是在睡之前就把"上一帧时刻"记成当时** ——
线程睡了几分钟，醒来后 dt = 整段空闲（被夹到 50 ms），第一帧就按 50 ms 的量发，
单格滚动开头先跳出去 **0.375 格**。

改成**醒来之后**才记这个时刻。为此新增一个诊断开关 `--frametrace`：
它真开一条动画线程、先让线程闲够、再推入滚动量，量"空闲之后第一帧发了多少"。
（不装钩子、不建窗口、不注入滚动，所以已有实例在跑时也能跑。）

| 场景 | 撤掉修复 | 有修复 |
|------|---------|-------|
| 空闲后滚一格 | 首帧 **80 单位** | 首帧 **1 单位** |
| 空闲后往回滚一格 | 首帧 **80 单位** | 首帧 **1 单位** |
| 连甩三格 | 首帧 **80 单位** | 首帧 **1 单位**，最大单帧 38 |
| 连甩六格 | 首帧 **80 单位** | 首帧 **1 单位**，最大单帧 64 |

四组场景都同时满足：总量守恒、单帧不超上限、末尾不回升（最大回升 0）、400 ms 内发完。

### ④ 参数变化

| 参数 | 旧 | 新 |
|------|----|----|
| 最大延迟 `maxLatencyMs` | 有（倾泻触发器） | **删除** |
| 滑行上限 `glideMaxMs` | 有（倾泻触发器） | **删除** |
| `glideTauMs` | 时间常数，默认 150 | **滑行时长**，默认 300 ms |
| 松手判定 `glideDelayMs` | 26 ms | 30 ms |

配置自动迁移（`schemaVersion` 3 → 4）：老配置的滑行值换算成新默认 300 ms、
`maxLatencyMs` / `glideMaxMs` 被移除，**用户的倍率、阈值、名单等设置一个都不动**；
把滑行关掉（`0`）的老配置升级后仍然是关的。

### ⑤ 附带修掉：自检结论其实传不出来

启动器把主程序拉起来之后是 `Process.Start(...); return 0;` —— 不等、也不回传退出码。
于是 `--selftest` 永远返回 0，`tools/publish.ps1` 里那句"两个自检都通过"等于没检查
（把自检故意跑挂，脚本照样报绿）。现在对"跑完就退出"的诊断开关
（`--selftest` / `--frametrace` / `--smoke` / `--measure` / `--taskcheck` / `--checkupdate`）
等它跑完并把结论原样返回；普通启动仍然立刻返回。

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
SHA256 D5F773EF0F015240AFE4B9C0415D49A4BDAFF7D53261A250238214B57A386DB9
```

构建 0 警告 0 错误；`--selftest` **192 条**全过 / `--smoke` 通过（注入 16 次，钩子接管 16 次、倍率 5.00×）
/ `--frametrace` 四组场景全过。

端到端实机验证（旧构建，验证的是同一套摊帧算法）：注入一下真实滚轮 120 单位 →
收到 24 条、合计 120 单位（**总量守恒**），末尾 12 条全是 `1`、**末尾最大回升 0 单位**。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 滚到末尾那一下「刻度感」没了" % TAG,
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
