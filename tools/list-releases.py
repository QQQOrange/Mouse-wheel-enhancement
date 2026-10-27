#!/usr/bin/env python3
"""查 GitHub 上的 release 列表和资产结构（只读）。"""
import json
import os
import subprocess
import sys
import urllib.request

REPO = "QQQOrange/Mouse-wheel-enhancement"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


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
    with urllib.request.urlopen(req, timeout=60) as r:
        body = r.read().decode("utf-8", "replace")
        return json.loads(body) if body.strip() else {}


for rel in api("GET", "/repos/%s/releases?per_page=5" % REPO):
    print(f"== {rel['tag_name']}  {rel['name']}  (prerelease={rel['prerelease']})")
    for a in rel["assets"]:
        print(f"   - {a['name']}  {a['size']:,} B  {a['content_type']}")
    body = (rel.get("body") or "").strip()
    print("   body 前 400 字:")
    for line in body.splitlines()[:14]:
        print("     " + line)
    print()
