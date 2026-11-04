#!/usr/bin/env python3
"""用 GitHub API 推送当前 HEAD（git 协议走不通时的兜底）。

背景：这台机器上 `github.com:443` 有时连不上，而 `api.github.com` 一直通 ——
于是 `git push` 会失败，但发布（走 API）照样成功。这个脚本把"推送"也搬到 API 上。

做法：读远端 main 的 tree → 用本地 HEAD 的变更文件建 blob → 建 tree → 建 commit →
PATCH ref。**关键点**：commit 的 author/committer/message/parents 全部照抄本地提交，
所以 API 造出来的 commit **SHA 与本地完全一致**（实测一致），两边不会分叉。

用法：
    python tools/push-via-api.py            # 推 main
    python tools/push-via-api.py --dry-run  # 只看会推什么

凭据来源与 make-release.py 相同：`git credential fill`（Windows 凭据管理器里的令牌）。
"""

import base64
import json
import re
import subprocess
import sys
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
OWNER = "QQQOrange"
NAME = "Mouse-wheel-enhancement"
BRANCH = "main"
DRY = "--dry-run" in sys.argv


def git(*args, binary=False):
    r = subprocess.run(["git", *args], cwd=REPO, capture_output=True)
    if r.returncode != 0:
        sys.exit("git %s 失败: %s" % (" ".join(args), r.stderr.decode("utf-8", "replace")))
    return r.stdout if binary else r.stdout.decode("utf-8", "replace").strip()


def credentials():
    p = subprocess.run(["git", "credential", "fill"], cwd=REPO, capture_output=True,
                       input=b"protocol=https\nhost=github.com\n\n")
    user = pw = None
    for line in p.stdout.decode("utf-8", "replace").splitlines():
        if line.startswith("username="):
            user = line.split("=", 1)[1]
        elif line.startswith("password="):
            pw = line.split("=", 1)[1]
    return user, pw


USER, TOKEN = credentials()
if not TOKEN:
    sys.exit("拿不到 GitHub 凭据（git credential fill 返回空）")


def api(path, data=None, method="GET"):
    body = json.dumps(data).encode() if data is not None else None
    req = urllib.request.Request("https://api.github.com" + path, data=body, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Accept", "application/vnd.github+json")
    if body:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.loads(r.read().decode())


def iso(unix_ts, tz):
    """git 的 `+0800` → ISO 8601（GitHub 只认后者）。"""
    sign = -1 if tz[0] == "-" else 1
    offset = sign * (int(tz[1:3]) * 3600 + int(tz[3:5]) * 60)
    stamp = datetime.fromtimestamp(int(unix_ts) + offset, timezone.utc)
    return stamp.strftime("%Y-%m-%dT%H:%M:%S") + tz[:3] + ":" + tz[3:]


def main():
    head = git("rev-parse", "HEAD")
    raw = git("cat-file", "-p", "HEAD", binary=True).decode("utf-8")
    header, message = raw.split("\n\n", 1)

    ref = api(f"/repos/{OWNER}/{NAME}/git/ref/heads/{BRANCH}")
    remote = ref["object"]["sha"]
    print(f"本地 {head[:7]} / 远端 {remote[:7]}")
    if remote == head:
        print("已经同步，无需推送")
        return 0

    if subprocess.run(["git", "merge-base", "--is-ancestor", remote, head], cwd=REPO).returncode != 0:
        sys.exit("远端有本地没有的提交，不能快进（先 git pull）")

    base_tree = api(f"/repos/{OWNER}/{NAME}/git/commits/{remote}")["tree"]["sha"]
    entries = []
    for line in git("diff", "--name-status", remote, head).splitlines():
        status, path = line.split("\t", 1)
        if status.startswith("D"):
            entries.append({"path": path, "mode": "100644", "type": "blob", "sha": None})
            print("删除", path)
            continue
        blob = git("cat-file", "-p", f"HEAD:{path}", binary=True)
        mode = git("ls-tree", "HEAD", path).split()[0]
        if DRY:
            print("将上传", path, len(blob), "字节")
            continue
        sha = api(f"/repos/{OWNER}/{NAME}/git/blobs",
                  {"content": base64.b64encode(blob).decode(), "encoding": "base64"},
                  "POST")["sha"]
        entries.append({"path": path, "mode": mode, "type": "blob", "sha": sha})
        print("上传", path, sha[:7])

    if DRY:
        print("（dry-run，未写入）")
        return 0

    tree = api(f"/repos/{OWNER}/{NAME}/git/trees",
               {"base_tree": base_tree, "tree": entries}, "POST")["sha"]

    author = re.search(r"author\s+(.*?)\s+<(.+?)>\s+(\d+)\s+([+-]\d{4})", header)
    committer = re.search(r"committer\s+(.*?)\s+<(.+?)>\s+(\d+)\s+([+-]\d{4})", header)

    commit = api(f"/repos/{OWNER}/{NAME}/git/commits", {
        "message": message,
        "tree": tree,
        "parents": [remote],
        "author": {"name": author.group(1), "email": author.group(2),
                   "date": iso(author.group(3), author.group(4))},
        "committer": {"name": committer.group(1), "email": committer.group(2),
                      "date": iso(committer.group(3), committer.group(4))},
    }, "POST")["sha"]

    api(f"/repos/{OWNER}/{NAME}/git/refs/heads/{BRANCH}",
        {"sha": commit, "force": False}, "PATCH")

    same = "（SHA 与本地一致，两边同源）" if commit == head else f"（注意：SHA {commit[:7]} 与本地不同）"
    print(f"已更新远端 {BRANCH} → {commit[:7]} {same}")

    # 让本地 remote-tracking 跟上，避免 git status 显示"领先一个提交"
    subprocess.run(["git", "update-ref", f"refs/remotes/origin/{BRANCH}", commit], cwd=REPO)
    return 0


if __name__ == "__main__":
    sys.exit(main())
