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
BODY = """## v1.8.8：停止时长按这一次的滚动手速自适应

### 症状

「最后一次停止时仍不够平滑 —— 停止后动画会在极短时间内迅速结束，非常突兀」。

### 根因

v1.8.5 为了修"末尾一卡一冲"，把收尾 τ 的下限钉成了 35ms。于是**不管滚得多快，
尾巴都被压成 35ms**：快甩攒下的动量被一刀砍掉，剩下的量在 ~120ms 内被催着发完。
慢滚的尾巴长度也一样，跟手速完全无关。

### 新模型：先按手速定时长，再反解 τ

```
时长倍数 = 0.45 + 0.18 × log2(1 + 格/秒)      // 夹在 0.45 ~ 1.35
目标时长 = 「惯性长度」× 时长倍数              // 90 ~ 900ms
τ        = 目标时长 / ln(积压/1单位)
τ        = max(τ, 跟手 τ)                     // 滑行只能更慢，绝不更快
```

手速来自**推送速度的 EMA**（不是"积压 ÷ 延迟"，那会把单格算成 33 格/秒）。
没有任何速度样本时按最慢一档处理。

**落地段的 τ 也改成 `0.75 × 滑行 τ`（原固定 35）**，滚得越快，最后几个单位之间也越疏。

顺带删掉了 `MaxFinishMs = 150` 那道硬上界 —— 它本质上就是"不管多快，尾巴一律
150ms 收完"，正是这次症状的直接来源。

### 实测（`--frametrace`，惯性 300ms / 跟手 τ 45）

| 场景 | 末段 τ | 收尾时长 | 末段速度 |
|---|---|---|---|
| 1 格（慢） | 43.5 → 35.9 ms | **235 ms** | 57~61 单位/s |
| 3 格 @15ms | 74.6 → 57.5 ms | **449 ms** | 36 单位/s |
| 6 格 @8ms | 68.8 → 56.7 ms | **469 ms** | 36 单位/s |

快滚收尾约为慢滚的 2 倍，末段速度从 57 降到 36 单位/秒。
真实注入路径（`--scrollsmooth`）：末段最后一步间隔从 30.9 ms 拉长到 **56.7 ms**，
末 10 步间隔 5→12→13→18→18→25→57 单调拉长。

### 没变的东西

- 加速兑现不变：`--wheeltest` 1.57×（猛甩 2.11×）
- 末帧间隔仍 ≤ 70ms（"最后一帧不拖"那条边界没突破）
- 落地段不许吃掉整段收尾的 62% 以上

### 验证

`--selftest`（+6 条收尾自适应断言）、`--frametrace` 含 `jitter=12`、`--featurecheck`、
`--wheeltest`、`--scrollsmooth`、`--smoke`、`--measure --show` 全部 rc=0，日志 0 条 WARN/ERROR。

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB）/ `.zip` / `.App.exe`。需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 收尾时长随手速自适应" % TAG,
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
