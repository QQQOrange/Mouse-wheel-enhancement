#!/usr/bin/env python3
"""在 github.com 被代理挡住时，用 GitHub API 把本地提交推上去。

# 什么时候需要它

某些代理 / 公司网络只放行 api.github.com，而把 github.com 的 CONNECT 隧道拒掉
（表现为 `git push` 报 `CONNECT tunnel failed, response 502`）。
`git push` 必须走 github.com，绕不开，但 API 是通的 —— 这个脚本就用 API 重建提交。

# 做法

对每个待推送的提交：上传变更文件为 blob → 以远程 tree 为 base 建新 tree →
建 commit（父指向远程 HEAD）→ 最后把 main 指过去。

提交的 tree / parent / author / committer / message 全部照抄本地原始 commit 对象，
所以重建出来的 SHA 与本地**逐位相同**，推完两边完全对齐，不会产生分叉历史。

# 用法

    python tools/push-via-api.py            # 推送 main
    python tools/push-via-api.py --dry-run  # 只看会推什么，不改远程

凭据复用 git 的 credential helper（与 git push 同一份），不落盘、不打印。
"""
import base64
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "QQQOrange/Mouse-wheel-enhancement"
BRANCH = "main"

# 脚本放在 tools/ 下，仓库根是上一级
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DRY = "--dry-run" in sys.argv


def git(*args):
    r = subprocess.run(["git"] + list(args), cwd=ROOT, capture_output=True)
    if r.returncode != 0:
        sys.exit("git 失败：git %s\n%s" % (" ".join(args), r.stderr.decode("utf-8", "replace")))
    return r.stdout


def txt(*args):
    return git(*args).decode("utf-8", "replace").strip()


def credentials():
    p = subprocess.run(["git", "credential", "fill"], cwd=ROOT,
                       input="protocol=https\nhost=github.com\n\n",
                       capture_output=True, text=True,
                       env=dict(os.environ, GIT_TERMINAL_PROMPT="0"))
    d = dict(l.split("=", 1) for l in p.stdout.splitlines() if "=" in l)
    return d.get("username", ""), d.get("password", "")


USER, TOKEN = credentials()
if not TOKEN:
    sys.exit("拿不到 git 凭据（先手动 git push 一次，让凭据管理器记住）")


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
        with urllib.request.urlopen(req, timeout=180) as r:
            body = r.read().decode("utf-8", "replace")
            return json.loads(body) if body.strip() else {}
    except urllib.error.HTTPError as e:
        sys.exit("API %s %s → %d: %s" % (method, path, e.code,
                                        e.read().decode("utf-8", "replace")[:400]))


remote = api("GET", "/repos/%s/git/ref/heads/%s" % (REPO, BRANCH))["object"]["sha"]
local_head = txt("rev-parse", "HEAD")
local_base = txt("rev-parse", "origin/main")

print("远程 HEAD      :", remote)
print("本地 origin/main:", local_base)
print("本地 HEAD      :", local_head)

if remote == local_head:
    print("\n已经是最新，无需推送。")
    sys.exit(0)

if remote != local_base:
    sys.exit("\n远程与本地记录不一致（远程 %s / 本地 %s）。\n"
             "说明远程被别人推过 —— 先 git fetch 对齐再跑本脚本。" % (remote[:7], local_base[:7]))

commits = txt("rev-list", "--reverse", "--topo-order", local_base + "..HEAD").split("\n")
commits = [c for c in commits if c]
if not commits:
    print("\n本地没有待推送的提交。")
    sys.exit(0)

print("\n待推送 %d 个提交：" % len(commits))
for c in commits:
    print("  %s %s" % (c[:7], txt("log", "-1", "--format=%s", c)))

if DRY:
    print("\n--dry-run：不改远程。")
    sys.exit(0)

base = api("GET", "/repos/%s/git/commits/%s" % (REPO, local_base))
tree_sha = base["tree"]["sha"]
parent = local_base

print()
for sha in commits:
    raw_commit = git("cat-file", "commit", sha)
    cut = raw_commit.index(b"\n\n")
    msg = raw_commit[cut + 2:].decode("utf-8", "replace")

    entries = []
    for line in txt("diff-tree", "--no-commit-id", "--name-status", "-r", sha).splitlines():
        if not line.strip():
            continue
        parts = line.split("\t")
        status, path = parts[0], parts[-1]

        if status.startswith("D"):
            entries.append({"path": path, "mode": "100644", "type": "blob", "sha": None})
            print("    删除 %s" % path)
            continue

        content = git("show", "%s:%s" % (sha, path))
        blob = api("POST", "/repos/%s/git/blobs" % REPO,
                   {"content": base64.b64encode(content).decode("ascii"), "encoding": "base64"})
        mode = "100755" if txt("ls-tree", sha, path).startswith("100755") else "100644"
        entries.append({"path": path, "mode": mode, "type": "blob", "sha": blob["sha"]})
        print("    %s %s (%d B)" % (status, path, len(content)))

    tree_sha = api("POST", "/repos/%s/git/trees" % REPO,
                   {"base_tree": tree_sha, "tree": entries})["sha"]

    commit = api("POST", "/repos/%s/git/commits" % REPO, {
        "message": msg,
        "tree": tree_sha,
        "parents": [parent],
        "author": {"name": txt("log", "-1", "--format=%an", sha),
                   "email": txt("log", "-1", "--format=%ae", sha),
                   "date": txt("log", "-1", "--format=%aI", sha)},
        "committer": {"name": txt("log", "-1", "--format=%cn", sha),
                      "email": txt("log", "-1", "--format=%ce", sha),
                      "date": txt("log", "-1", "--format=%cI", sha)},
    })
    parent = commit["sha"]
    same = "（SHA 与本地一致）" if parent == sha else "（SHA 与本地不同，内容相同）"
    print("  %s → %s %s\n" % (sha[:7], parent[:7], same))

api("PATCH", "/repos/%s/git/refs/heads/%s" % (REPO, BRANCH), {"sha": parent, "force": True})
print("已更新 %s → %s" % (BRANCH, parent))

# 同步本地的远程跟踪引用。不做这一步的话，git status 会一直显示 [ahead N]，
# 而实际上远程已经推完了 —— 用户会以为没推成功，再推一次又是"已经是最新"，很困惑。
subprocess.run(["git", "update-ref", "refs/remotes/origin/%s" % BRANCH, parent], cwd=ROOT)

if parent != local_head:
    sys.exit("\n注意：远程 SHA 与本地 HEAD 不一致（内容相同）。本地引用已同步到远程。")

print("本地与远程已对齐（SHA 逐位相同）。")
