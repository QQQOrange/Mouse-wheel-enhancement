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
BODY = """## 「以管理员身份运行」其实只管开机那一次

用户报：**「软件启动之后还提示当前不是管理员身份，但我明明在选项里开了管理员身份启动，并且重启了软件。」**

### ① 真相：开关只覆盖了一条启动路径

| 启动方式 | 旧版本 |
|---------|--------|
| 登录时由计划任务拉起 | 管理员（不弹 UAC） |
| **双击 exe / 快捷方式** | **普通权限** |
| **更新完自动重启** | **普通权限** |
| **托盘退出后再打开** | **普通权限** |

而界面上的字写的是「以管理员身份启动」，旁边还挂着一张「当前不是管理员身份」的卡片。
用户开了开关、关掉软件、再双击打开 —— 看到的就是这张卡片。

实机验证过：计划任务那条路是好的（任务结果 `0x0`，普通权限进程杀不掉它），
点开关触发的提权重启也是好的。**唯独"用户自己双击"这条路从来没被实现。**

### ② 为什么会这样

这个开关原本叫 `RunAtLoginAsAdmin`，是「开机自启」的一个可选形态，三层代码都在强调这一点：

```csharp
if (!Behavior.RunAtLogin) Behavior.RunAtLoginAsAdmin = false;   // Normalize 强制拉平
AdminStartRow.IsEnabled = AutoStartSwitch.IsChecked == true;     // UI 整行变灰
```

于是它只在"开机"那条路上有意义。

顺带还有三处"看着正常、其实是错的"：

- 开关显示的是**计划任务存不存在**，不是配置意图 —— 只提权、不开机自启时显示成关；
- 关掉「开机自启」时会**连带清掉管理员模式**，用户悄悄丢了设置；
- 权限卡片只有一句"当前不是管理员身份"，分不清"没开这个功能"和"开了但没提权成功"。

### ③ 修法一：启动时自己提权

```
Services.InitConfig()           配置 / 主题 / 日志（无副作用）
ResolveStableEntry()            稳定入口落盘
TryRestartElevatedIfNeeded()    ← 不是管理员就 runas 重启自己
ApplyStartup / RepairStartup    自启同步与自愈
Services.InitRuntime()          装钩子、起引擎、建托盘
```

启动流程特意拆成两段：**先判断要不要提权，再装钩子和托盘** ——
否则提权那一刻用户会看到托盘图标闪一下又消失。

判定抽成纯函数 `ShouldElevate(是否管理员, 开关, 本轮是否已试过)`，三种情况都不提权：
已经是管理员、开关没开、这一轮已经试过（防 UAC 死循环）。

### ④ 修法二：接班进程与单实例互斥体（最容易漏的一处）

原来的单实例判断是 `new Mutex(true, name, out createdNew)`。
`createdNew` 只能回答"这个名字是不是我刚创建的"，回答不了
"上一代正在退出、我现在能不能接手" —— 而这正是提权重启的接班进程遇到的情况。
照原样写它会把自己当成"第二个实例"直接退出，**现象是：点了开关，软件没了。**

改成抢锁 + 重试：只有接班进程（带 `--elevate-attempt`）才等，最多 6 秒，
并处理上一代被强杀后留下的 `AbandonedMutexException`。

### ⑤ 修法三：把开关的含义改对

- 字段改名 `RunAsAdmin`，但 **JSON 键保持 `runAtLoginAsAdmin`** ——
  键名一改，老用户已有的设置会在升级时被静默重置成 `false`，表现就是"升级完又变回普通权限"；
- 删掉"父开关关掉就拉平"的约束：现在可以**只提权、不开机自启**；
- UI 不再置灰，开关显示**配置意图**；
- 提示条扩成三种状态：该有任务却没有 / 任务失效 / 不该有任务却还在（会无声拉起）；
- 权限卡片分开说话：没开功能（正常）／开了但提权失败（说清原因、怎么重试）；
- 「以管理员身份重启」按钮顺手把开关打开 —— 否则下次打开还会撞同一堵墙。

### ⑥ 一个必须承认的取舍

开启这个开关后，**每次手动启动都会请求一次管理员权限**。这是"始终以管理员运行"
的固有代价：Windows 不允许普通权限进程给自己提权。
开机那次不弹确认（计划任务在创建时就完成了授权），手动那次会问一次 —— 文案里已经写清楚。

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
SHA256 B502B4CD30BEA91B93BCC8A3BB9A8EC5950BB70B401CFDBC4062C8417974B758
```

构建 0 警告 0 错误；`--selftest` **166 条**全过 / `--smoke` 通过。

实机端到端验证：普通权限双击 → 日志「正在以管理员权限重启」→ 只有一个进程，
且普通权限 shell 对它 `Stop-Process` 直接被拒（= 管理员），全程 160ms；
再启动一次不会新增进程（单实例正常）。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 「以管理员身份运行」现在手动启动也生效" % TAG,
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
