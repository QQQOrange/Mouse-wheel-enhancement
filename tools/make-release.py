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
BODY = """## 关闭窗口不再卡几秒：弹层清理的 O(n²) 遍历

### 定位

在关闭 / 退出各环节加了计时，实测：

| 环节 | 耗时 |
|---|---|
| 保存窗口位置 | 0 ms |
| 保存配置 | 10~22 ms |
| **关闭弹层（`CloseOpenPopups`）** | **4494 ms** |
| 钩子线程退出 | 0 ms |
| 退出各段 | 1~42 ms |

### 根因

`PopupCleanup.CloseAllIn` 同时做「可视树递归」和「逻辑树递归」，**两者都递归调用自身**。
逻辑树与可视树大量重叠，一个节点会被反复访问好几轮 —— 帮助页上千个元素时
退化成 **O(n²)**。每次关闭都跑一遍，于是"点 × / Alt+F4 卡几秒"。

同一个函数还被「点标题栏主题按钮」的路径用到，那里也会跟着卡。

### 修复

加 `HashSet` 去重，每个节点只进一次，复杂度回到 **O(n)**：
**4494 ms → 14 ms**（快约 320 倍），关闭总耗时 < 30 ms。

其它环节本来就不慢：保存配置 10~22 ms、钩子线程退出 0 ms、
`SaveNow` 的强制刷盘最多几十 ms、`MemoryTrim` 是后台线程不阻塞。

### 验证

- `--measure --show` 日志：`关闭·关闭弹层：14 ms`
- `--selftest` 304 全过、`--smoke` rc=0、`--wheeltest` 通过

## 下载

`MouseWheelEnhancement.exe`（约 0.58 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。
需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 关闭延迟修复" % TAG,
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
