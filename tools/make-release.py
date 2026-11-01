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
BODY = """## v1.8.0：自愈、按应用差异化、看得见的手感

这次不换算法，补的是三件产品化的事。

### 1. 钩子看门狗（不用再手动点「重启钩子」）

Windows 在回调超时后会**静默摘掉**低层钩子：句柄还在、回调不再来、界面一切正常，
就是不加速了。现在每 5 秒自检一次：

- 线程没了 → 自动重启；
- 长期空闲 → 定期换一个新钩子（先拆后装，不会出现两个钩子并存导致的"速度翻倍"）；
- 顺带记录回调耗时，超过 40ms 写一条 WARN，性能劣化能被日志抓住。

### 2. 按应用覆盖：一套参数适配不同程序

「应用名单」里**右键**任意程序，可以单独设它的：
- 倍率上限（2× ~ 8×）
- 平滑开关（跟随全局 / 开 / 关）

不设就跟随全局。列表里会显示「· 上限 3× · 平滑关」这样的摘要。
典型用法：IDE 要干脆 → 2× + 平滑关；浏览器要远要滑 → 5× + 平滑开。

### 3. Shift + 滚轮横向加速

宽表格、代码 diff、时间轴不用再慢慢拖。横向**只放大不摊帧**
（HWHEEL 在多数程序里是整格跳），方向、速度判定都用独立的跟踪器，
不会因为之前上下滚过就"一横滚直接顶格"。

### 4. 三处手感打磨（都可关）

| 改动 | 效果 |
|---|---|
| 反向软起步 | 快滚中途回拨时，方向翻转处插 45ms 呼吸感，不再硬切 |
| 速度停手 | 甩得越猛，收尾前多等一会儿（最多 +80%），不会被过早截断 |
| 掉帧降级 | 机器跑不动时自动关掉滑行，避免"越滚越滞后" |

### 5. 看得见、搬得走

- **倍率浮层**：快速滚动时在光标旁闪一下「3.2×」，300ms 淡出；不抢焦点、点不中
- **一键导出诊断包**：配置 + 日志 + 环境信息打成一个 zip
- **导出 / 导入配置**：「关于」页直接点击，不用再手动找 config.json
- **兼容名单**：默认放行向日葵 / ToDesk / AnyDesk / TeamViewer / RustDesk

### 两条没按原设计落地的（已在设计文档说明）

- **参数按每格像素归一化**：Windows 没有 API 能读别的进程的滚动范围，
  改为「按应用覆盖」这条等价但可调的路径。
- **短内容不滑行**：同样拿不到外部窗口的可滚高度；本程序自己的窗口本来就按像素裁剪，
  外部程序的富余量不到一格（亚像素）。替代：惯性可设 0、4 个预设、按应用关平滑。

### 验证

- `--selftest` **330 项全过**（新增 19 条：覆盖派生、新开关默认值、schema 6 迁移）
- `--wheeltest` 通过：单格 60px、45ms/格 兑现 **1.56×**
- `--smoke` / `--measure --show` / `--frametrace` 全 rc=0
- 旧配置升级只推 schema 版本号，不改任何已有设置

## 下载

`MouseWheelEnhancement.exe`（约 0.60 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。
需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 自愈·按应用覆盖·横向加速" % TAG,
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
