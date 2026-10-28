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
BODY = """## 修掉「倍率读着 5×，页面只快一两成」

### 现象

v1.7.2 修掉「设置页一滚一页」之后，加速变成时灵时不灵：
钩子读数明明爬到 4~5×，页面位移却只比不加速快一两成；
慢拨的时候更是几乎感觉不到变化。同一份配置、同一个页面，
绕过钩子直接用 SendInput 滚就完全正常。

### 根因：倍率的上限，撞在动画器「以单位计」的交付容量上

v1.6.1 有一条不变式：**「5× 加速的量必须装得进积压上限」**。
当年它是靠「系统行数补偿倍数」（恒 3）**间接**成立的——
补偿把单帧上限撑到 45 单位、积压上限撑到 600 单位，
5× 的量才倒得出去。

v1.7.0 删除补偿层时，把这条不变式**一起弄丢了**：
容量缩回 1×（单帧 45 单位、积压 600 单位），
而倍率还是按配置的 5× 去乘。超限的部分被 `PushCore`
**静默丢弃**——没有日志、没有痕迹，只是少滚。

算一下就知道为什么「猛甩必死」：
160 Hz 下单帧 45 单位 = 每秒最多 7200 单位 = **60 格/秒**；
40 格/秒的猛甩 ×5× 需要 **200 格/秒**，超天花板 3 倍多。
（v1.6.0 的「加速被截断」就是这么来的，v1.7.0 删补偿后它变相复活了。）

「慢拨没感觉」也有份：EMA 速度估计 τ=70 ms，要 3~4 格才收敛到
真实速度，倍率爬起来时一次拨动已经结束。

### 修法

- **(a) 容量随倍率上限放大**：`UnitsPerFrame`、`MaxBacklogUnits`
  按 `max(1, MaxMultiplier)` 计算。关掉加速时与旧公式逐字节相同。
- **(b) EMA τ 70 → 50 ms**：两格之内收敛到真实速度。
- **(c) 护栏 3+3 → 2+2**：防抖主力是 70 ms 时长护栏，
  格数护栏只负责挡「一两下」的误触，不必占满加速前的里程。

### 实测（用户同配置：触发 10 / 饱和 39 / 上限 5×，8 帧/格，160 Hz）

| 手速 | 曲线倍率 | v1.7.2 兑现 | v1.7.3 兑现 |
|---|---|---|---|
| 100 ms/格（10 格/秒） | 1.00× | 1.00× | 1.00× |
| 70 ms/格（14.3 格/秒） | 1.24× | 1.11× | **1.17×** |
| 45 ms/格（22.2 格/秒） | 2.53× | **1.64×** | **1.95×** |
| 持续猛甩 16 格 @35 ms | 4.20× | ~1.8× | **3.23×** |

低于触发阈值的慢滚依旧**一个字节都不改**；轻拨防抖护栏不退让。

> 曲线触发阈值默认 10 格/秒，所以 10~14 格/秒只给 1.0~1.2×——
> 这是曲线设计，不是 bug。想更早触发，把设置里的「触发速度」降到 6~7。

### 验证

构建 0 警告 0 错误；`--selftest` **280 条**全过（新增「注入边界：
容量随倍率放大」5×/8× 装得进积压等断言）；`--frametrace` 五场景全过，
单格摊帧手感不变（首帧 24 单位、297 ms 发完、总量守恒）；
`--wheeltest` 新增手速扫描 + 持续猛甩段，全过。
已装到本机并经启动器跑 `--dialogtest` / `--selftest` 全过。

## 下载

`MouseWheelEnhancement.exe`（0.57 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。

需要 .NET 10 桌面运行时。

## 校验

```
SHA256 63029B6D699E96AEDBA95F24AC96EF406FFF36127EDC14A94CA51B653634B952
```
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 倍率不再被动画器的交付容量勒死" % TAG,
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
