#!/usr/bin/env python3
"""创建（或更新）GitHub Release 并上传资产（exe + zip + 主程序）。

用法：
    python tools/make-release.py v1.8.12            # 用 notes 文件创建 release 并上传资产
    python tools/make-release.py v1.8.12 --update   # 只改已有 release 的标题与说明，不动资产

发布说明从 tools/release-notes/<tag>.md 读（第一行是标题，其余是正文）。
文件不存在就直接报错 —— 说明**不能**在脚本里硬编码：上一版就是写死的 v1.8.11 文案，
结果 v1.8.12 的 release 里挂着一整页"不再爬行"，用户看到的是上一版的更新内容。

SHA256 由 tools/publish.ps1 算出来写进 bin/package/SHA256.txt，这里复核后拼进正文：
客户端（UpdateService）就是从说明里解析 "SHA256: <64 hex>" 来校验下载到的主程序。
"""
import base64
import hashlib
import json
import mimetypes
import os
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "QQQOrange/Mouse-wheel-enhancement"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NOTES_DIR = os.path.join(ROOT, "tools", "release-notes")

ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
FLAGS = {a for a in sys.argv[1:] if a.startswith("--")}
TAG = ARGS[0] if ARGS else sys.exit("用法：make-release.py <tag> [--update]")


def credentials():
    p = subprocess.run(["git", "credential", "fill"], cwd=ROOT,
                       input="protocol=https\nhost=github.com\n\n",
                       capture_output=True, text=True,
                       env=dict(os.environ, GIT_TERMINAL_PROMPT="0"))
    d = dict(l.split("=", 1) for l in p.stdout.splitlines() if "=" in l)
    return d.get("username", ""), d.get("password", "")


USER, TOKEN = credentials()
if not TOKEN:
    sys.exit("拿不到 GitHub 凭据（git credential fill 返回空）")


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


def upload(rel_id, name, path):
    ctype = mimetypes.guess_type(name)[0] or "application/octet-stream"
    with open(path, "rb") as f:
        data = f.read()
    req = urllib.request.Request(
        "https://uploads.github.com/repos/%s/releases/%s/assets?name=%s"
        % (REPO, rel_id, name), data=data, method="POST")
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("Content-Type", ctype)
    req.add_header("X-GitHub-Api-Version", "2022-11-28")
    req.add_header("User-Agent", USER or "QQQOrange")
    with urllib.request.urlopen(req, timeout=600) as r:
        a = json.loads(r.read().decode("utf-8", "replace"))
    print("  已上传 %s（%s B）" % (a["name"], format(a["size"], ",")))


# ── 发布说明：只从文件读，脚本里绝不写死版本内容 ──
notes_path = os.path.join(NOTES_DIR, TAG + ".md")
if not os.path.exists(notes_path):
    sys.exit("缺少发布说明：%s\n"
             "  每个版本都要单独写一份（标题 + 正文），不要复用上一版的文案。\n"
             "  参考 %s" % (notes_path, os.path.join(NOTES_DIR, "v1.8.12.md")))

notes = open(notes_path, encoding="utf-8").read().strip()
lines = notes.splitlines()
if not lines or not lines[0].startswith("#"):
    sys.exit("发布说明第一行必须是标题（# vX.Y.Z：一句话）")
TITLE = lines[0].lstrip("#").strip()
BODY = "\n".join(lines[1:]).strip()

# ── SHA256：客户端从正文里解析它来校验主程序 ──
pkg = os.path.join(ROOT, "bin", "package")
app_exe = os.path.join(ROOT, "bin", "publish-stage", "MouseWheelEnhancement.App.exe")
sha_file = os.path.join(pkg, "SHA256.txt")
if not os.path.exists(sha_file):
    sys.exit("缺少 %s —— 先跑 tools/publish.ps1（它会算出主程序的 SHA256）" % sha_file)

expected = open(sha_file).read().strip().upper()
if len(expected) != 64:
    sys.exit("SHA256.txt 内容不是 64 位十六进制：%r" % expected)
if not os.path.exists(app_exe):
    sys.exit("找不到主程序产物：%s" % app_exe)

actual = hashlib.sha256(open(app_exe, "rb").read()).hexdigest().upper()
if actual != expected:
    sys.exit("主程序哈希与 SHA256.txt 不一致：\n  文件 %s\n  记录 %s" % (actual, expected))

BODY += "\n\n## 校验\n\n`MouseWheelEnhancement.App.exe` 的 SHA256：\n\n```\nSHA256: %s\n```\n" % actual

# ── --update：只改标题与说明（发好的 release 发现文案写错时用这个，不要重建） ──
if "--update" in FLAGS:
    rel = api("GET", "/repos/%s/releases/tags/%s" % (REPO, TAG))
    rel = api("PATCH", "/repos/%s/releases/%s" % (REPO, rel["id"]),
              {"name": TITLE, "body": BODY})
    print("已更新 %s：%s" % (TAG, rel["html_url"]))
    print("  标题：%s" % rel["name"])
    print("  SHA256 已写入说明：%s" % (actual[:16] + "…"))
    sys.exit(0)

# ── 正常发版：先看这个 tag 有没有已经建过 ──
rels = api("GET", "/repos/%s/releases?per_page=30" % REPO)
existing = next((r for r in rels if r.get("tag_name") == TAG), None)
if existing is not None:
    sys.exit("tag %s 已经有一个 release 了：%s\n"
             "  只想改说明/标题就加 --update；要重发就先在网页上删掉那个 release。"
             % (TAG, existing.get("html_url")))

REL = api("POST", "/repos/%s/releases" % REPO, {
    "tag_name": TAG,
    "target_commitish": "main",
    "name": TITLE,
    "body": BODY,
    "draft": False,
    "prerelease": False,
})
print("Release 已创建：%s" % REL["html_url"])

upload(REL["id"], "MouseWheelEnhancement.exe", os.path.join(pkg, "MouseWheelEnhancement.exe"))
upload(REL["id"], "MouseWheelEnhancement-win-x64.zip",
       os.path.join(ROOT, "bin", "MouseWheelEnhancement-win-x64.zip"))
upload(REL["id"], "MouseWheelEnhancement.App.exe", app_exe)
print("\n完成：%s" % REL["html_url"])
