#!/usr/bin/env python3
"""从 Linux 同步工作区，在隔离的 Windows 副本中运行编译和 NUnit。"""

from __future__ import annotations

import argparse
from hashlib import sha256
import io
import json
import os
from pathlib import Path, PureWindowsPath
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import uuid

HOST = os.environ.get("VRCK_SSH_HOST", "pc114514")
WINDOWS_REPO = Path(r"D:\VRChatKatamariDanacy")
GATE_HOME = WINDOWS_REPO / ".work" / "gate"
GATE = GATE_HOME / "checkout"
PROJECT = "VRChatKatamariDanacyUnity"
SSH_OPTIONS = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10"]
# The gate does not use this owner-local Unity setting (and must not read or diff it).
OWNER_SETTING = f"{PROJECT}/ProjectSettings/ProjectSettings.asset"
PATHSPEC = [".", f":(exclude,literal){OWNER_SETTING}"]


class GateError(RuntimeError):
    pass


def run(argv, **kwargs):
    return subprocess.run(argv, check=True, **kwargs)


def regular_tar_member(archive, name, contents, mode=0o644):
    info = tarfile.TarInfo(name)
    info.size = len(contents)
    info.mode = mode
    archive.addfile(info, io.BytesIO(contents))


def snapshot(repo, archive_path, args):
    def git(*argv, **kwargs):
        return run(["git", "--no-optional-locks", "-C", str(repo), *argv], **kwargs)

    head = git("rev-parse", "HEAD", stdout=subprocess.PIPE).stdout.decode("ascii").strip()
    patch = git("diff", "--no-ext-diff", "--no-textconv", "--binary", "--full-index",
                "--no-renames", head, "--", *PATHSPEC, stdout=subprocess.PIPE).stdout
    names = git("ls-files", "--others", "--exclude-standard", "-z", "--", *PATHSPEC,
                stdout=subprocess.PIPE).stdout.split(b"\0")[:-1]
    with tarfile.open(archive_path, "w:gz") as archive:
        regular_tar_member(archive, "request.json", json.dumps({
            "head": head, "filters": args.filter, "check_only": args.check_only,
        }).encode("utf-8"))
        regular_tar_member(archive, "changes.patch", patch)
        for raw_name in sorted(names):
            name = raw_name.decode("utf-8")
            path = repo / name
            if not path.resolve().is_relative_to(repo) or not stat.S_ISREG(path.lstat().st_mode):
                raise GateError(f"未跟踪文件不是工作区内的普通文件：{name}")
            # Avoid adding symlinks, directories, or tar entries that follow parents outside the repo.
            archive.add(path, arcname=f"untracked/{name}", recursive=False,
                        filter=lambda info: info if info.isfile() else None)
    if head != git("rev-parse", "HEAD", stdout=subprocess.PIPE).stdout.decode("ascii").strip():
        raise GateError("打包期间 HEAD 已变化，请重试。")
    return head, len(names), len(patch)


def linux(args):
    started = time.perf_counter()
    repo = Path(__file__).resolve().parents[2]
    ssh = ["ssh", *SSH_OPTIONS, HOST]
    sid = uuid.uuid4().hex
    remote = str(GATE_HOME) + "\\" + sid
    local_work = repo / ".work" / "gate"
    local_work.mkdir(parents=True, exist_ok=True)
    if local_work.resolve() != local_work:
        raise GateError("Linux 闸门暂存目录是链接。")
    with tempfile.TemporaryDirectory(prefix="upload-", dir=local_work) as tmp:
        archive = Path(tmp) / "snapshot.tar.gz"
        head, count, patch_bytes = snapshot(repo, archive, args)
        with archive.open("rb") as stream:
            digest = sha256()
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        print(f"快照 {head}：补丁 {patch_bytes} 字节，未跟踪文件 {count} 个，"
              f"SHA-256 {digest.hexdigest()}", flush=True)
        run([*ssh, f'mkdir "{remote}"'])
        try:
            destination = remote.replace("\\", "/")
            run(["scp", "-q", *SSH_OPTIONS, str(archive), str(Path(__file__).resolve()),
                 f"{HOST}:{destination}/"])
            print(f"打包和上传：{time.perf_counter() - started:.1f} 秒", flush=True)
            # No user-provided filter is interpolated in cmd.exe; the Windows runner reads JSON.
            return subprocess.run([*ssh, f'python -X utf8 -B "{remote}\\remote_gate.py" --windows']).returncode
        finally:
            # Delete only this invocation's randomly named path under .work/gate.
            subprocess.run([*ssh, f'if exist "{remote}" rmdir /s /q "{remote}"'], check=False)


def git_env():
    env = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    return dict(env, GIT_TERMINAL_PROMPT="0", GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)


def windows_git(*argv, **kwargs):
    return run(["git", "-c", "core.autocrlf=false", "-c", "credential.helper=", *argv],
               env=git_env(), **kwargs)


def sync(archive, directory):
    request = json.load(archive.extractfile("request.json"))
    head = request["head"]
    if not re.fullmatch(r"[0-9a-f]{40}", head):
        raise GateError("快照的 HEAD 无效。")
    if GATE_HOME.resolve() != GATE_HOME or GATE.resolve() != GATE:
        raise GateError("隔离工作树或暂存路径为链接，拒绝清理。")

    def base_git(*argv, **kwargs):
        return windows_git("-C", str(WINDOWS_REPO), *argv, **kwargs)

    def git(*argv, **kwargs):
        return windows_git("-C", str(GATE), *argv, **kwargs)

    try:
        base_git("cat-file", "-e", f"{head}^{{commit}}", stderr=subprocess.DEVNULL)
    except subprocess.CalledProcessError:
        # Import commit objects without updating origin refs or a shared FETCH_HEAD.
        try:
            base_git("fetch", "--no-tags", "--no-write-fetch-head", "--no-auto-gc", "--refmap=", "origin", head)
        except subprocess.CalledProcessError as exc:
            raise GateError(f"Windows 主仓库不含 {head}，origin 也无法按 SHA 抓取。") from exc
    if not GATE.exists():
        base_git("worktree", "add", "--detach", str(GATE), head)
    top = git("rev-parse", "--show-toplevel", stdout=subprocess.PIPE).stdout.decode().strip()
    common = git("rev-parse", "--path-format=absolute", "--git-common-dir",
                 stdout=subprocess.PIPE).stdout.decode().strip()
    symbolic = subprocess.run(["git", "-C", str(GATE), "symbolic-ref", "-q", "HEAD"], env=git_env())
    if (Path(top) != GATE or Path(common) != WINDOWS_REPO / ".git"
            or not (GATE / ".git").is_file() or symbolic.returncode != 1):
        raise GateError("目标不是预期的隔离 detached 工作树；拒绝覆盖。")
    # Worktree identity and real path checked above; these commands must never target a developer checkout.
    git("checkout", "--quiet", "--force", "--detach", head)
    git("clean", "-ffdxq")
    patch = directory / "changes.patch"
    with patch.open("xb") as out:
        shutil.copyfileobj(archive.extractfile("changes.patch"), out)
    if patch.stat().st_size:
        git("apply", "--whitespace=nowarn", str(patch))
    for member in archive:
        if not member.name.startswith("untracked/"):
            continue
        relative = PureWindowsPath(member.name.removeprefix("untracked/"))
        if (not member.isfile() or not relative.parts or relative.anchor
                or any(part in ("..", ".git") or part.endswith((".", " ")) or ":" in part
                       or PureWindowsPath(part).is_reserved() for part in relative.parts)):
            raise GateError(f"拒绝非普通或不兼容的 Windows 路径：{member.name}")
        path = GATE / relative
        if not path.resolve().is_relative_to(GATE):
            raise GateError(f"路径越出隔离副本：{member.name}")
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("xb") as out:
            shutil.copyfileobj(archive.extractfile(member), out)
    return request


def windows():
    import msvcrt

    directory = Path(__file__).resolve().parent
    gate_temp = GATE_HOME
    if gate_temp.resolve() != gate_temp or directory.parent != gate_temp or not re.fullmatch(r"[0-9a-f]{32}", directory.name):
        raise GateError("不是单次运行专用的 Windows 临时目录。")
    with (gate_temp / "gate.lock").open("a+b") as lock:
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as exc:
            raise GateError("已有 Windows 闸门在运行；本次未同步，请稍后重试。") from exc
        started = time.perf_counter()
        with tarfile.open(directory / "snapshot.tar.gz", "r:gz") as archive:
            request = sync(archive, directory)
        print(f"Windows 同步：{time.perf_counter() - started:.1f} 秒", flush=True)
        env = dict(os.environ, TEMP=str(directory), TMP=str(directory), PYTHONUTF8="1",
                   PYTHONUNBUFFERED="1", PYTHONDONTWRITEBYTECODE="1",
                   VRCK_CSHARP_CHECK_CACHE=str(gate_temp / "cache"))
        tool_dir = GATE / "Tools" / "csharp_check"
        started = time.perf_counter()
        code = subprocess.run([sys.executable, str(tool_dir / "check.py"), "--refresh", "--timings"],
                              cwd=GATE, env=env).returncode
        if code or request["check_only"]:
            return code
        print(f"Windows check.py 墙钟：{time.perf_counter() - started:.1f} 秒", flush=True)
        filters = [f"--filter={value}" for value in request["filters"]]
        return subprocess.run([sys.executable, str(tool_dir / "run_tests.py"), *filters],
                              cwd=GATE, env=env).returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--filter", action="append", default=[], help="测试全名包含给定片段；可重复，取并集")
    parser.add_argument("--check-only", action="store_true", help="只编译，不启动测试")
    parser.add_argument("--windows", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.check_only and args.filter:
        parser.error("--check-only 与 --filter 不能同时传入")
    try:
        if args.windows and os.name == "nt":
            return windows()
        if not args.windows and sys.platform.startswith("linux"):
            return linux(args)
        raise GateError("请在 Linux 工作区运行此入口。")
    except (GateError, OSError, ValueError, subprocess.CalledProcessError, tarfile.TarError) as exc:
        print(f"远端闸门失败：{exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
