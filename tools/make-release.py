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
BODY = """## 修四处实测问题

v1.5.0 之后实测发现的问题，全部在这一版修完。

### ① 禁用态按钮「蓝底黑字」看不清（浅色主题）

「检查更新」「保存令牌」在禁用时是**整块 Opacity 0.45** —— 蓝底和白字一起冲淡，
浅色主题下对比度塌到有时看不清。现在禁用态改用**专用配色**（只换色、不褪色）：

| 主题 | 禁用底色 | 禁用字色 | 对比度 |
|------|----------|----------|--------|
| 浅色 | `#D9E1F0` | `#5A6478` | 4.52:1 |
| 深色 | `#2A3444` | `#9AA6BC` | 5.11:1 |

两种主题各有一套，跟随白/夜模式切换。

### ② 个人访问令牌是明文

令牌输入框从 TextBox 换成 **PasswordBox**，粘贴后显示的是一串圆点。
已保存状态下输入框锁定，同样显示圆点，要更换点「更换」即可。

### ③ 一格一格拨滚轮，还是段落式起停

两个原因叠加：

- **平滑范围默认是「仅加速时」** —— 慢慢拨一格根本不属于"加速"，平滑滚动
  完全不接管，看到的还是系统原生滚动。默认改为「**所有滚动**」，
  老配置会自动迁移（v3），升级后即生效；之后自己想改回「仅加速时」也随意
- **每帧下限是绝对值 0.30 格** —— 单格滚动总共 1 格，第一帧就被下限劫持成
  突发，剩下两帧枯竭，听感上还是"一顿"。下限改为按积压量的**比例**
  （15%），且只作用于跟手期；松手后的滑行期保持纯指数衰减，不会被下限续住
- 另外把最大延迟默认从 60ms 调到 100ms —— 高刷屏（160Hz）上 60ms 比指数
  曲线还短，平滑"还看不出来"就截止了

### ④ 全局圆角

- 窗口外框：Win11 显式开启 DWM 圆角（自绘标题栏之后系统默认圆角会失效）；
  Win10 自动跳过，保持与系统一致
- 侧栏左上/左下 8px 圆角，与窗口外框联动
- 右键菜单（ContextMenu / MenuItem）此前是直角的，补圆角

## 下载

| 文件 | 用途 |
|------|------|
| `MouseWheelEnhancement.exe` | **下载这个**。单文件，0.50 MB，双击即用 |
| `MouseWheelEnhancement-win-x64.zip` | 整包（内容同上，压缩后更小） |
| `MouseWheelEnhancement.App.exe` | 主程序本体，供软件内自动更新用 |

需要 .NET 10 桌面运行时（未装时软件会提示并给出下载链接）。
Windows 11 x64 实测；Windows 10 上圆角自动降级为直角，其余功能不变。

## 校验

```
SHA256 3E5E00193683B8BB23ED247DC8DA852E5A4C4F224618C47572F47AFA8A423C7F
```

自检 147 项全部通过；冒烟测试在白/夜双主题下实测通过
（含像素级截图复核：22 个暗文圆点、禁用态专用配色、圆角形状）。
"""

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": "%s — 禁用态配色 / 令牌暗文 / 单格滚动平滑 / 全局圆角" % TAG,
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
