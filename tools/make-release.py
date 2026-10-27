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
BODY = """## 让 WPF / WinForms 的「一格」也分成三步

上一版修好了"轻拨翻整页"（WPF 无视 delta，摊帧被当成多次滚动），代价是那类程序里彻底没有平滑了：一格就是一次跳。

这一版把它补回来 —— 用的办法是**接管系统的滚动粒度**。

### 卡点：那类框架的粒度只有一个来源

WPF / WinForms 收一个滚轮事件就滚 `SystemParameters.WheelScrollLines` 行（默认 3），
而且**只看向号、不看 delta 大小**（官方 issue [dotnet/wpf#5936](https://github.com/dotnet/wpf/issues/5936)）。

实测同一个窗口：`delta=-120`、`-40`、`-15` 的位移**都是 6.49 个百分点**。
所以只要系统是 3 行，最小步进就是 3 行，任何注入技巧都没法让一步走得更细。

### 办法：降到 1 行，再把位移补回来

```
系统 3 行：1 格 = 1 个事件 = 3 行（一步跳完）
系统 1 行：1 格 = 3 个事件 = 3 行（分三步推进）
```

位移一单位不差，过程从 **1 步变成 3 步**。这是那类框架里能拿到的全部平滑。

配套两条：

- **两档之间至少隔 16 ms**：帧间隔只有 6 ms，三档挤在 19 ms 里人眼还是一次跳；
  16 ms 让三档落到 48 ms、五档 80 ms —— 看得清推进，又不觉得卡。
- **按比例滚动的框架**（Chromium / Qt / Office）delta 乘 3 再摊帧，
  每帧 45 单位，位移一致，8 帧平滑一点没少。

### 这是全局设置，所以有三道保险

`WheelScrollLines` 在 HKCU，所有程序共享 —— 软件没在跑时全系统滚动会只剩 1/3。
所以做了三重保护：

| 保险 | 做法 |
|------|------|
| 正常退出 | `ExitApp` 第一步就还原，排在任何可能抛异常的清理之前 |
| 兜底 | `OnExit` + `ProcessExit` 各挂一次（可重入） |
| 崩溃自愈 | 原值**先落盘再改设置**，下次启动先还原再接管 |

实测：Force kill 后行数停在 1；再次启动立刻打日志
「发现上次异常退出留下的滚动行数（当前 1 行），已还原为 3 行」，然后重新接管。

### 不想要就关掉

平滑页新增「接管系统滚动粒度」开关，**关掉立刻把系统改回去**，不用重启。
以下情况它会自动跳过：系统已经是 1 行、被设为「一次滚一页」、或行数为 0。

### 限制（说在前面）

- **最多 3 步**。系统行数最小是 1，所以一档最多拆成 3 步（默认行数 3）。
  想要 8 步，行数得是 0.375 —— 不存在。
- **个别应用会缓存行数**，改了要重启才认。这类程序会表现为"接管后滚得偏快"
  （我们补了 3 倍，它仍按 3 行算）。
- **触控板**的亚格输入在那类框架里也是一档，同样按 3 档补偿，否则会比接管前慢 3 倍。

## 一并包含前面两版的修复

**① 末尾「刻度感」**：删掉三条「到点倾泻」的捷径，改为一条指数曲线走到底。

**② 开头「抢先跳一块」**：线程醒来后才记上一帧时刻（原来是睡之前记，首帧 dt 被夹到 50 ms）。

**③ 轻拨翻整页**：WPF/WinForms 按"事件次数"滚动，摊帧会被当成多次滚动 ——
现在对它们改用整格语义，一格永远是一格。

**④ 起步护栏**：一段滚动必须同时 > 3 格且 > 70 ms 才允许加速。

## 下载

| 文件 | 用途 |
|------|------|
| `MouseWheelEnhancement.exe` | **下载这个**。单文件，0.55 MB，双击即用 |
| `MouseWheelEnhancement-win-x64.zip` | 整包（内容同上，压缩后更小） |
| `MouseWheelEnhancement.App.exe` | 主程序本体，供软件内自动更新用 |

需要 .NET 10 桌面运行时（未装时软件会提示并给出下载链接）。
Windows 11 x64 实测；Windows 10 上圆角自动降级为直角，其余功能不变。

## 校验

```
SHA256 CC548946CAAAAC44EFC8BF6CBBDA392B23E50B588F06267F55DF28385B15C1FC
```

构建 0 警告 0 错误；`--selftest` **235 条**全过（新增「接管系统滚动行数」10 条）。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 接管系统滚动粒度，让一格分成三步" % TAG,
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
