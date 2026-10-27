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
BODY = """## 修掉「开机自启没以管理员启动」—— 任务指向了被删掉的文件

用户报：**「我设置了以管理员方式启动，结果启动的时候好像并没有以管理员方式启动。」**

### ① 真相：不是"没以管理员启动"，是**开机根本没启动**

现场查到的任务配置**全对**：

```
RunLevel=Highest   LogonType=Interactive   登录触发 + 延迟 8 秒
Actions: ...\\app\\1.4.0\\MouseWheelEnhancement.App.exe --minimized
LastRunTime    = 11/30/1999 00:00:00     ← 从未运行过
LastTaskResult = 0x41303                 ← SCHED_S_TASK_HAS_NOT_RUN
```

唯独**目标文件不存在**：`app\\1.4.0\\` 那个目录早被升级清理掉了，现在只剩 `app\\1.5.2\\`。

任务被系统拉起时找不到 exe → 直接失败，而且**不弹任何窗、不写任何日志**，
设置页的开关还照样显示"开"。这是最难查的一类问题：看起来一切正常，行为却是坏的。

### ② 根因：任务指向了「版本目录」

主程序本体并不住在用户放软件的那个文件夹里 —— 启动器把它作为嵌入资源释放到
`%LOCALAPPDATA%\\MouseWheelEnhancement\\app\\<版本>\\` 再启动。
而启动器在释放新版本后会把旧版本目录**删掉**。

计划任务的 Action 当时用的是**当前主程序本体的路径**，于是：

```
升级 → 旧版本目录被删 → 任务指向的文件消失 → 开机静默失败
```

注册表 `Run` 项是同一只 bug 的第二个入口，两个都会在下次升级后失效。

### ③ 修法：指向「稳定入口」

真正稳定的位置是**启动器 exe**（用户放软件的那个文件夹里，升级不换位置），
而且由它启动，开机那次也能走"释放最新版本"的流程。

新增 `StableEntryPath`，解析顺序：

1. 环境变量 `MWE_LAUNCHER_PATH` —— 启动器拉起主程序时传过来
2. 配置里记下的 `LauncherPath` —— 落盘，好让"提权重启自己"之后仍然找得到
3. 都没有 → 当前 exe 本身

计划任务的 Action 与注册表 `Run` 项**都改指它**。

### ④ 失效要看得见

已经指向坏路径的旧任务还在用户机器上，所以补了：

- `IsStale()`：任务在，但目标文件没了 / 指向的不是稳定入口 → 判定已失效；
- 启动自愈：**已是管理员**时自动重建；不是管理员时不写，改为在设置页显示提示条；
- 设置页新增「**立即修复**」按钮：管理员直接重建，普通权限走提权重启流程；
- `--taskcheck` 升级为真正的诊断：打印任务指向、目标是否存在、是否指向稳定入口、
  是否已失效；`--create` / `--delete` 在非管理员下会**自动提权重启自己**再执行。

### ⑤ 一个容易踩的坑

提权（`runas`）启动的进程**不继承父进程的环境变量**。
所以"把启动器路径放环境变量里"这个方案，只有在**经由启动器**的那条链路上才成立 ——
任何"提权重启自己"的实现都必须以**启动器**为目标，不能以主程序本体为目标。

## 下载

| 文件 | 用途 |
|------|------|
| `MouseWheelEnhancement.exe` | **下载这个**。单文件，0.53 MB，双击即用 |
| `MouseWheelEnhancement-win-x64.zip` | 整包（内容同上，压缩后更小） |
| `MouseWheelEnhancement.App.exe` | 主程序本体，供软件内自动更新用 |

需要 .NET 10 桌面运行时（未装时软件会提示并给出下载链接）。
Windows 11 x64 实测；Windows 10 上圆角自动降级为直角，其余功能不变。

## 校验

```
SHA256 145D8AB31749CC3740A92E252CA639DAB2A6B87562319DA5CA3339FD2AF912AF
```

构建 0 警告 0 错误；`--selftest`（新增 6 条稳定入口断言）/ `--smoke` 全部通过。

实机验证：重建任务后手动触发 → 进程正常启动、`LastTaskResult=0x0`，
且普通权限进程对它 `Stop-Process` 直接"拒绝访问" —— 即高完整性（管理员）进程。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 开机自启指向稳定入口，修复任务静默失效" % TAG,
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
