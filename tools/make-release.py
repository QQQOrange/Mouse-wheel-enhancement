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
BODY = """## 开机自启后功能没启用：暂停状态被当成了设置

### 现象

开了「开机自启」，登录后程序确实起来了（托盘图标在、钩子也装上了），
但滚轮没有任何增强，必须手动点一次「启用增强」才生效。

### 根因

日志时间线是这样的：

```
11:07:37  功能已暂停
11:25:48  ==== 启动 v1.7.12 ====
11:25:50  滚轮钩子已安装
11:25:50  引擎已启动          ← 钩子没问题，是状态被带着走
```

`Enabled` 是个普通持久化字段。用户随手暂停一次（热键或托盘菜单点一下），
这个 `false` 就被写进 `config.json`，**跨进程、跨重启一直有效**。
之后每次自启，引擎都拿着 `Enabled=false` 把每一次滚动原样放行。

排除项：自启任务用的是「登录时触发 + `--minimized`」，会话正确；
钩子也确实装上了（如果是钩子失败，点启用也救不回来）。

### 修复

把「暂停」降级为**本次运行的状态**：

- 新增 `ConfigStore.ResetSessionState()`：读配置时若 `enabled=false` 就抹成 `true`
- 在 `Normalize()` 之后、写回判断之前调用，磁盘上的 `false` 会被顺手纠正回 `true`
- 日志留一条「上次退出时处于暂停状态，本次启动自动启用」

也就是说：**每次启动都是启用态**，想长期停用请关掉开机自启或直接退出。

### 验证

| 方式 | 结果 |
|---|---|
| 造 `enabled:false` 配置走真实启动 | 日志出现「本次启动自动启用」，`config.json` 变 `true` |
| 同配置跑 `--smoke` | 注入 12/12、16/16 全部接管，倍率 3.00× —— 真的在加速 |
| `--selftest` | **308 项全过**（新增 4 条：重置逻辑 ×2、真实读盘 ×2） |
| 回归 | `--wheeltest` 单格 60 px / 兑现 1.48×，`--smoke` rc=0，构建 0 警告 |

## 下载

`MouseWheelEnhancement.exe`（约 0.58 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。
需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 自启即启用" % TAG,
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
