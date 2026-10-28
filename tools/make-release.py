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
BODY = """## 修掉「设置页里一滚动就是一页」

### 现象

把「每格滚动格数」调到 1 之后，在本程序自己的页面（帮助页最明显）里拨一格，
内容直接飞过一页；连续快滚时倍率像是失效了。同一个页面、同一份配置，
绕过钩子滚动就完全正常（一格 20 px）。

只在系统行数小的时候明显：行数 3 时一格 60 px，被误放行的事件一跳 48 px，
比例 0.8 倍，混在里面看不出来；行数 1 时一格 20 px、那一跳还是 48 px，
**2.4 倍**于本该滚的量，一格直接顶到一页多。

### 根因：边界判据把「小数位移」当成了「到边界了」

设置页自己的页面是**按像素滚动**的（一格 = 行数 × 20 px，见 DESIGN 第 30 节），
和 WPF 原生的 `ScrollViewer` 是两套语义叠在一起。原来的判据是：

```csharp
if (Math.Abs(target - sv.VerticalOffset) < 0.5) return;   // ← bug
```

`< 0.5` 的本意是"已经到顶/到底了就别拦"，但它也把**位移不到半像素**判成了边界。
而摊帧正好持续产出这种事件：一格拆成 8 帧小 delta，v1.7.1 改成零头累积之后，
尾帧出现 `delta = 1/2/3` —— 行数 1 时折算成 0.17~0.5 px，**每一格都会命中**。

被判成"在边界上"之后事件不被 `Handled`，继续隧道给 WPF 原生 `ScrollViewer`，
它拿到一个事件就滚**一整行**（16 px 起步；缓存行数若是旧值 3 就是 48 px）。
于是我们摊帧发出去 17 px，它一跳几十 px，一格累计 784.5 px。

倍率"失效"是同一件事的另一面：倍率确实乘上了，但位移被那几跳彻底淹没。

### 修法

判据换成 `PlanScroll`（`App.xaml.cs`，抽成纯函数便于自检）：

```csharp
var target = Math.Clamp(offset - px, 0, Math.Max(0, scrollableHeight));
return target == offset ? null : target;      // null = 真的没得滚了，放行
```

- 到顶 / 到底 / 内容比视口还短 → 放行（和以前一样）；
- 位移 0.17 px → 自己滚（`VerticalOffset` 是 double，小数滚得动）；
- `Math.Max(0, …)` 是顺手加的防线：`Math.Clamp` 在 `min > max` 时**抛异常**，
  而这里正处在滚轮事件链上，抛一次就等于这一次滚动彻底丢掉。

顺带把 `OnPreviewMouseWheelScroll` 的每一次判据都留了痕（`--wheeltest` 专用）。

### 实测（`--wheeltest`，视口 741 px）

| 系统行数 | 理论一格 | 修复前 | 修复后 |
|---|---|---|---|
| 1 | 20 px | **784.5 px**（一页多） | **20 px** |
| 2 | 40 px | — | **40 px** |
| 3 | 60 px | — | **60 px** |

快滚 8 格（45 ms 间隔）：倍率读数 4.79~4.84×，位移 355 / 711 / 1061 px 随行数等比。

**浏览器等第三方程序完全不受影响** —— 它们按 delta 比例滚，小 delta 就是小位移。
这个 bug 只在本程序自己的 WPF 页面上成立。

## 新增 `--wheeltest [lines=N]`

把光标真的挪到本程序帮助页上滚，量"一格到底滚了多少像素"，
并打印像素换算的每一次判据（走了多少 / 为什么放行）。

```bash
MouseWheelEnhancement.exe --wheeltest lines=1     # → wheeltest.txt
```

报告里两行是判据：`单格最大位移` 应等于 `行数 × 20 px`，
`像素换算被绕过的次数` 应为 **0**。

## 下载

`MouseWheelEnhancement.exe`（0.57 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。

需要 .NET 10 桌面运行时。

## 校验

```
SHA256 3B8651E632A72DDAC5E0A3D786E61F0851DDAA4AC31ED5429193BC9DC3C8AAE8
```

构建 0 警告 0 错误；`--selftest` **272 条**全过（新增「设置页像素滚动：边界判据」12 条）；
`--smoke` 退出码 0（16/16 注入、倍率 5×）；`--frametrace` 五场景全过
（末段速度 33 单位/秒、首帧 dt 6.8 ms、总量守恒）。
已装到本机并经启动器跑 `--dialogtest` / `--selftest` 全过。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 修掉设置页「一格滚一页」" % TAG,
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
