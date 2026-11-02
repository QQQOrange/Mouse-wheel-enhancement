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
BODY = """## v1.8.11：不许爬行 —— 彻底去掉「停住之后又挪了一下」

你确认了形态：不是停得快，而是**停住之后又动了一下**。这一版从根上删掉那一下。

### 为什么上一版（24ms）还不够

v1.8.9 把末步空窗压到 24ms，但方式是"把间隔**封顶**在 24ms 继续发"——
于是尾部成了一串 `0.5px / 20ms` 的**匀速爬行**（约 25~40 px/s）。
问题不在间隔多大，而在：**这段速度低于眼睛的运动检测阈值**，
画面早被读作"已经停了"，爬行最后那一下自然就成了孤立的一跳。

一句话：只要允许"爬"，迟早会爬到"看起来停了"的地方再停。

### 修法：间隔一旦超过 12ms 就收尾

```
gap = τ·ln(p/(p-1))
if gap > 12ms  →  直接结束（丢下剩余 ≤8 单位 ≈ 4px），不再发
```

12ms ≈ 67 px/s。低于这个速度的位移已经不被读作"在动"，所以在它发生**之前**就结束。
尾巴最后一跳因此总是紧挨着前一跳，读作"一直在动，然后停住"。

代价是每次少走 2~4px —— 绝对位置人眼分辨不出来，这个交换划算。

### 实测

| 指标 | v1.8.8 | v1.8.9 | **v1.8.11** |
|---|---|---|---|
| 末步空窗 | 54.5 ms | 23.6 ms | **12.7 ms** |
| 末段间隔 | 6 13 5 13 13 18 19 27 55 | 5 6 12 12 12 19 19 25 26 | **5 6 6 12 6 7 6 7 11**（全在 1 帧级） |
| 8 格总位移 | 705 px | 704 px | **702 px** |
| 收尾总时长 | 235~469 ms | 235~449 ms | 235~430 ms（速度自适应保留） |

### 断言口径同步

旧口径"末 6 帧速度 ≤72"的前提是"收尾要爬到 25~35 单位/秒"，与新版意图相反。改为：

- **最后 3 帧单步 ≤ 2 单位**（最后一跳必须是亚像素）
- 末 6 帧速度 ≤ 160（粗兜底，防整格砸下来）
- 总量守恒允许尾段丢弃 ≤ 8 单位（丢弃可见、有上限）

### 验证

`--selftest` **346 项**；`--frametrace`（含 `jitter=12`）四场景全过；
`--scrollsmooth` 末步空窗 **12.7ms**；`--wheeltest` 兑现 **1.56×**；
`--featurecheck` / `--smoke` / `--measure` 全 rc=0，日志 0 条 WARN/ERROR。

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB）/ `.zip` / `.App.exe`。需要 .NET 10 桌面运行时。
"""

pkg = os.path.join(ROOT, "bin", "package")
app_exe = os.path.join(ROOT, "bin", "publish-stage", "MouseWheelEnhancement.App.exe")

# ── SHA256 必须进发版说明 ──
# 客户端（UpdateService）从说明里解析 "SHA256: <64 hex>" 来校验下载到的主程序；
# 说明里没有就等于只比体积。而 publish.ps1 已经把**主程序**（不是启动器！）的
# 哈希写进 bin/package/SHA256.txt —— 这里读它、核对、再拼进 body。
sha_file = os.path.join(pkg, "SHA256.txt")
if not os.path.exists(sha_file):
    sys.exit("缺少 %s —— 先跑 tools/publish.ps1（它会同时算出主程序的 SHA256）" % sha_file)

expected = open(sha_file).read().strip().upper()
if len(expected) != 64:
    sys.exit("SHA256.txt 内容不是 64 位十六进制：%r" % expected)
if not os.path.exists(app_exe):
    sys.exit("找不到主程序产物：%s" % app_exe)

import hashlib
actual = hashlib.sha256(open(app_exe, "rb").read()).hexdigest().upper()
if actual != expected:
    sys.exit("主程序哈希与 SHA256.txt 不一致：\n  文件 %s\n  记录 %s" % (actual, expected))

BODY += "\n## 校验\n\n`MouseWheelEnhancement.App.exe` 的 SHA256：\n\n```\nSHA256: %s\n```\n" % actual

REL = api("POST", "/repos/%s/releases" % REPO, {    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 不再爬行" % TAG,
    "body": BODY,
    "draft": False,
    "prerelease": False,
})
print("Release 已创建：%s" % REL["html_url"])

pkg = os.path.join(ROOT, "bin", "package")
upload("MouseWheelEnhancement.exe", os.path.join(pkg, "MouseWheelEnhancement.exe"))
upload("MouseWheelEnhancement-win-x64.zip", os.path.join(ROOT, "bin", "MouseWheelEnhancement-win-x64.zip"))
upload("MouseWheelEnhancement.App.exe", app_exe)
print("\n完成：%s" % REL["html_url"])
