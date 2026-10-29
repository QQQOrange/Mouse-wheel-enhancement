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
BODY = """## 全面审查后的补漏与体验修补

对 v1.7.5 做了一次从头到尾的审查（目标达成、问题修复、界面、效率），
把发现的文档/文案不一致全部改齐，并补上一批交互与效率改进。

### 修掉的文案/注释不一致

- 帮助页「松手后的惯性」还写着"剩下不到 1/3 格、按 70 ms 落地"，
  实际 v1.7.5 是 **1/10 格（12 单位）、35 ms** —— 同一页两处自相矛盾。
- 「每格拆分」建议从"保持 4"改成"4~8 都可，8 帧/格颗粒更细"。
- `ScrollAnimator` 类注释 5 处滞后（1/2 格 / 1/3 格 / ~340 ms / τ=40）全部对齐实现。
- README 体积 0.47→0.58 MB、断言数 272→304。
- 标题栏版本徽标的设计时占位 `v1.5.5` 改成 `—`（运行时本来就会被覆盖）。

### 新增：窗口大小/位置与上次页签记忆

关闭到托盘会销毁窗口，以前每次重开都回到固定的 1120×830、"概览"页。
现在记录尺寸/位置/最大化/页签，下次原样恢复；旧坐标不在任何屏幕内时
自动回默认居中（换显示器也不会"打开了看不见"）。

### 新增：托盘单击立即响应

旧实现为等双击先延迟 400 ms，体感"点了没反应"。现在第一次点击
**立即显示**，300 ms 内的重复调用被去重，内部仍只走一遍。

### 界面补强

- 6 个滑块两端加语义刻度（如"2 · 几乎不触发 / 25 · 很猛才加速"）。
- 加速页顶部加「在这里试滚」示例区，调参原地试手感。
- 帮助页加搜索框（按关键词过滤）+ 四个跳转按钮。
- 帮助页四组预设升级为「应用」按钮，一键写入配置。
- 概览「当前滚动速度」加 0~40 格/秒 的进度条。
- 侧栏状态卡加"本次已接管 N 次 · 放行 M 次"。
- 名单空状态改成带说明的卡片。
- 托盘菜单加「加速设置… / 平滑设置… / 打开数据目录」。
- 卡片内边距/间距收紧，页面整体短一截。

### 新增：全局热键

- `Ctrl+Alt+W` 开关增强
- `Ctrl+Alt+S` 打开设置
（被其它程序占用时只记日志，不影响使用。）

### 验证

- `--selftest` **304 项**全过（新增 4 条窗口记忆断言）
- `--frametrace` 四场景全过（落地段 107~144 ms、末帧间隔 ≤ 37.7 ms、步长起伏 ≤ 2.2×）
- `--smoke` 七页布局全过、`--measure --show` 正常
- 构建 0 警告 0 错误
- 已装本机并经启动器自检

## 下载

`MouseWheelEnhancement.exe`（约 0.58 MB，双击即用）/ `.zip` / `.App.exe`（供自动更新）。
需要 .NET 10 桌面运行时。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 审查补漏与体验修补" % TAG,
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
