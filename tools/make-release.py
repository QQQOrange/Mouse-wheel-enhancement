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
BODY = """## v1.8.4：把「接线」也纳入验证

这一版加了一个新的自检模式 `--featurecheck`，并修掉它顺手暴露的三处问题。

### 为什么要它

`--selftest` 是纯函数断言：曲线算得对、快照派生得对。但**接线错了它抓不到**：

| 症状 | 逻辑是否正常 | 后果 |
|---|---|---|
| 覆盖 key 归一化过，钩子却拿原始大小写去查 | 正常 | 覆盖静默失效 |
| 看门狗投 `WM_APP+0x4D`，但钩子线程没收到 | 正常 | 轮换永不发生，摘钩后不再自愈 |
| 导入旧 schema + 未知字段 + `enabled:false` | 正常 | 崩溃或设置被带歪 |
| 干净安装（无 config / 无日志）导诊断包 | 正常 | 一点就抛 |

### `--featurecheck` 检查什么

1. **按应用覆盖真的命中**：`Apply` 后用 `SettingsFor("Code.EXE")` 反查钩子那一侧，故意用不同大小写
2. **钩子轮换通道**：请求轮换 → 2 秒内 `Rotations` +1、仍存活 → `Stop` 后线程退出
3. **配置导入**：旧 schema + 未知字段 + 暂停态一起灌，验保留 / 抹平 / 不污染
4. **诊断包**：在没有 Services、没有配置、没有日志的状态下生成并读回
5. **倍率浮层**：连续 Flash 不消失、关闭后不可见

它自己 new 引擎、装一次钩子再拆掉、配置用临时目录，**不碰你的真实设置**；
装钩子期间还把增强关掉，不会顺手改你正在滚的东西。

### 顺带修掉

- **诊断包重复导出会抛**：文件名时间戳只到秒，同秒连点两次撞名，而 `ZipFile.Open(Create)` 遇已存在文件直接 `IOException`
- **关闭时没停 DispatcherTimer**：`_footerHintTimer` / `_helpFilterTimer` 把窗口挂在计时器表上，6 秒内窗口收不掉 ——
  而"关闭到托盘"正是靠销毁窗口还内存
- **`DiagPack` 拆成静态类**：原来写在按钮事件里，自检永远碰不到；现在 UI 与命令行走同一份实现

### 验证

- `--featurecheck` **24 项全过**
- `--selftest` **336 项全过**
- `--wheeltest` 单格 60 px、45 ms/格 兑现 **1.56×**
- `--smoke` / `--measure --show` / `--frametrace` 全 rc=0，期间日志 **0 条 WARN/ERROR**

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB）/ `.zip` / `.App.exe`。需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 功能链路验证" % TAG,
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
