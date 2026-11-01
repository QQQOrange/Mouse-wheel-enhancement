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
BODY = """## v1.8.2：最后三项补齐

v1.8.1 之后还剩三件没交付，这次全部做完。

### 1. 甩动翻页（实验性，默认关）

快速甩到接近满倍率（≥4.5×）并且速度明显超过满速点时，额外补一屏的量（默认 6 格），
500ms 内只补一次，不会越甩越飞。

**默认关闭的原因**：Windows 没有"一屏多少像素"的 API，"一屏"只能按格估算 ——
长文档是净收益，短页面会冲过头。想要就自己在「加速」页打开。

### 2. 反作弊 / 叠加层放行名单

默认黑名单补 `vgtray.exe`（Vanguard 托盘）、`easyanticheat.exe`、`beservice.exe`。

没有做"自动检测反作弊模块"：枚举目标进程模块必须在钩子回调里跑，
而钩子有硬性超时（超了就被系统静默摘掉）。为了几十毫秒的判定冒
"用着用着失效"的风险不划算 —— 名单 + 白名单已经覆盖得住。

### 3. 环境自检

启动日志多一条：`64位 / 管理员 / 虚拟桌面(DIP) / 刷新率`；
诊断包的 `system.txt` 里带窗口 DPI。这次定位 OSD 偏移（物理像素 vs DIP）
就是靠这组数字，固化下来下次不用再猜。

### 验证

- `--selftest` **333 项全过**（+3：甩动翻页默认关 / 默认 6 格 / 越界夹到 12）
- `--wheeltest` / `--smoke` / `--measure --show` / `--frametrace` 全 rc=0
- 期间日志 **0 条 WARN/ERROR**，构建 0 警告 0 错误

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB）/ `.zip` / `.App.exe`。需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 甩动翻页·反作弊放行·环境自检" % TAG,
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
