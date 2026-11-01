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
BODY = """## v1.8.3：交付前最后一轮自审，修掉 4 处

不是新功能，是把 v1.8.x 新增代码逐行推演一遍后修掉的问题。

### 1. 整格积压被拆成小 delta，Qt 里会整段丢

「整格应用里没加速 → 原样放行」那一支原来是 `if (WholeNotch) WholeNotch = false; else if (有积压) Clear();`。
`else if` 意味着**清了模式却没清积压** —— 排队的整格（120 的整数倍）被按比例模式拆成 15/30，
而 Qt 的 `angleDelta/120` 是整数除法，**全部算成 0 档**。
表现：快甩几下再轻拨一下，那一小段滚动凭空消失。

现在：**只要还有积压就保持整格模式**，一格一格排空后再交回按比例。

### 2. 底部状态被提示文字永久占掉

导出诊断包后，底部那行会停在「诊断包已导出：xxx.zip」，版本号和权限提示再也看不到
（而 `RefreshStats` 每 400ms 又会把提示刷掉——两头都不对）。
现在：提示显示 6 秒期间挡住刷新，之后自动还原。

### 3. 覆盖上限低于全局下限时曲线会反向

`MultiplierFor = Min + (Max - Min) * e`。全局下限 2.5、某应用覆盖上限 2 时，
曲线变成**越甩越慢**，容量还会再被夹一次。现在派生快照时把下限压到不超过上限，
并加了「下限 ≤ 上限」「曲线单调不降」两条断言。

### 4. 看门狗重启失败每 5 秒硬来一次

钩子装不上时每次 `Restart()` 都可能 `Join(2000)`，日志也刷满。加 30 秒退避。

### 验证

- `--selftest` **336 项全过**（+3）
- `--wheeltest` 通过：单格 60 px、45 ms/格 兑现 **1.57×**
- `--smoke` / `--measure --show` / `--frametrace` 全 rc=0，期间日志 **0 条 WARN/ERROR**
- 构建 0 警告 0 错误

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB）/ `.zip` / `.App.exe`。需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 交付前自审修复" % TAG,
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
