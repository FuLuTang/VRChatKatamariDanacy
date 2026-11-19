#!/usr/bin/env python3
"""Run the NUnit EditMode tests that do not need the Unity engine, on this machine.

  python3 Tools/csharp_check/run_tests.py                  # everything we can run
  python3 Tools/csharp_check/run_tests.py --filter Ramp    # test names containing "Ramp"
  python3 Tools/csharp_check/run_tests.py --list           # discovery only

How: check.py compiles the repository's own Runtime/Editor/Test sources with Unity's
references (the single compile implementation) and keeps the DLLs; Tools/csharp_check/
test_runner hosts NUnit 3.5 around those real Unity managed DLLs on .NET 8; test_results.py
sorts every discovered case into passed / failed / known difference / not runnable here.
A case whose engine-native call the .NET runtime refuses is "not runnable", never "passed".

Exit code: 0 no local failure; 1 at least one test failed; 2 the tool itself failed
(compile error, missing prerequisite, runner crash).  Local green is not Unity green: see
README.md for what this does not cover.
"""

from __future__ import annotations

import sys

sys.dont_write_bytecode = True

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import time
import urllib.request
from pathlib import Path

CHECK_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(CHECK_DIR))

import check  # noqa: E402  (the one compile implementation)
import test_results as tr  # noqa: E402

REPO_ROOT = check.REPO_ROOT
CACHE_ROOT = check.CACHE_ROOT
RUNNER_DIR = CHECK_DIR / "test_runner"
KNOWN_DIFFERENCES = CHECK_DIR / "known_differences.json"
TEST_ASSEMBLY = "VRCKatamari.Tests.EditMode"

NUNIT_VERSION = "3.5.0"
NUNIT_URL = f"https://api.nuget.org/v3-flatcontainer/nunit/{NUNIT_VERSION}/nunit.{NUNIT_VERSION}.nupkg"
NUNIT_SHA256 = "a040473a34901526aae1af7e02f7ec733657206d36e5aae1827098544aa03b3b"
# NUnit 3.14 only for --continue-after-failure (3.5 has no Assert.Multiple).
NUNIT_CONTINUE_VERSION = "3.14.0"
NUNIT_CONTINUE_URL = (
    f"https://api.nuget.org/v3-flatcontainer/nunit/{NUNIT_CONTINUE_VERSION}/nunit.{NUNIT_CONTINUE_VERSION}.nupkg"
)
NUNIT_CONTINUE_SHA256 = "0ae3ffab91e8bcf59f016dc2b72fd0c518bb5698f2909dd30cbab94c4348e8a6"
NUNIT_DIR = CACHE_ROOT / "nunit"
RUNNER_BIN = CACHE_ROOT / "test-runner"
RUNNER_BIN_CONTINUE = CACHE_ROOT / "test-runner-continue"
BUILD_ROOT = CACHE_ROOT / "test-build"


class ToolError(RuntimeError):
    pass


# --- prerequisites -----------------------------------------------------------------------


def ensure_nunit(continue_mode: bool = False) -> Path:
    """The NUnit framework DLL this run uses, from the NuGet package cached next to Unity's DLLs.

    Faithful run: NUnit 3.5.0 lib/dotnet (the Unity version; its own net35 build cannot load on
    .NET 8).  --continue-after-failure: NUnit 3.14.0 lib/netstandard2.0.
    """
    import zipfile

    if continue_mode:
        version, url, sha, member = NUNIT_CONTINUE_VERSION, NUNIT_CONTINUE_URL, NUNIT_CONTINUE_SHA256, "lib/netstandard2.0/nunit.framework.dll"
        dll = NUNIT_DIR / version / "nunit.framework.dll"  # the file name must stay nunit.framework.dll
    else:
        version, url, sha, member = NUNIT_VERSION, NUNIT_URL, NUNIT_SHA256, "lib/dotnet/nunit.framework.dll"
        dll = NUNIT_DIR / "nunit.framework.dll"
    if dll.exists():
        return dll
    dll.parent.mkdir(parents=True, exist_ok=True)
    package = NUNIT_DIR / f"NUnit.{version}.nupkg"
    if not package.exists():
        print(f"fetching {url}", file=sys.stderr)
        try:
            with urllib.request.urlopen(url, timeout=120) as response:
                data = response.read()
        except OSError as exc:
            raise ToolError(f"cannot download NUnit {version}: {exc}") from exc
        if hashlib.sha256(data).hexdigest() != sha:
            raise ToolError(f"NUnit {version} package checksum mismatch; refusing to use it")
        package.write_bytes(data)
    with zipfile.ZipFile(package) as archive:
        dll.write_bytes(archive.read(member))
    return dll


def dotnet_executable() -> Path:
    dotnet = check.dotnet_executable()
    if not dotnet.exists():
        raise ToolError(f"{dotnet} is missing; run: python3 {CHECK_DIR / 'check.py'} (it installs the SDK)")
    return dotnet


def _dotnet_env() -> dict:
    return dict(
        os.environ,
        DOTNET_ROOT=str(dotnet_executable().parent),
        DOTNET_CLI_HOME=str(CACHE_ROOT),
        NUGET_PACKAGES=str(CACHE_ROOT / "nuget-packages"),
        DOTNET_NOLOGO="1",
        DOTNET_CLI_TELEMETRY_OPTOUT="1",
        DOTNET_ROLL_FORWARD="LatestMajor",
    )


def ensure_runner(nunit_dll: Path, continue_mode: bool = False) -> Path:
    """Build test_runner into the cache when its sources are newer than the build."""
    out = RUNNER_BIN_CONTINUE if continue_mode else RUNNER_BIN
    dll = out / "test_runner.dll"
    sources = [RUNNER_DIR / "Program.cs", RUNNER_DIR / "test_runner.csproj"]
    newest = max(p.stat().st_mtime for p in sources)
    staged_nunit = out / "nunit.framework.dll"
    if (dll.is_file() and dll.stat().st_mtime >= newest and staged_nunit.is_file()
            and staged_nunit.stat().st_mtime >= nunit_dll.stat().st_mtime):
        return dll
    flavor = ["-p:NUnitFlavor=3.14"] if continue_mode else []
    proc = subprocess.run(
        [str(dotnet_executable()), "build", str(RUNNER_DIR / "test_runner.csproj"), "-c", "Release",
         "-o", str(out), f"-p:BaseIntermediateOutputPath={out / 'obj'}/",
         f"-p:NUnitFrameworkDll={nunit_dll}", "-nologo", "-v:q", *flavor],
        capture_output=True, text=True, env=_dotnet_env(), timeout=600,
    )
    if proc.returncode != 0 or not dll.exists():
        raise ToolError("building test_runner failed:\n" + (proc.stdout + proc.stderr)[-2000:])
    return dll


# --- compile (through check.py) --------------------------------------------------------


def source_fingerprint(project: Path) -> str:
    """Hash of everything check.py would compile plus the reference manifest and compiler."""
    digest = hashlib.sha256()
    for unit in check.UNITS:
        root = project / unit.root
        for path in sorted(root.rglob("*.cs")) if root.is_dir() else []:
            digest.update(path.relative_to(project).as_posix().encode())
            digest.update(b"\0")
            digest.update(path.read_bytes())
            digest.update(b"\0")
    for name in ("manifest.json",):
        path = CACHE_ROOT / name
        if path.exists():
            digest.update(path.read_bytes())
    digest.update((CHECK_DIR / "check.py").read_bytes())
    return digest.hexdigest()[:20]


def build_assemblies(project: Path, rebuild: bool, ssh_host: str, refresh: bool) -> tuple[Path, bool, float]:
    """Compile (or reuse) the four assemblies; returns (directory, reused, seconds)."""
    started = time.perf_counter()
    check.load_manifest(refresh, ssh_host)
    key = source_fingerprint(project)
    target = BUILD_ROOT / key
    if target.is_dir() and (target / f"{TEST_ASSEMBLY}.dll").exists() and not rebuild and not refresh:
        target.touch()
        return target, True, time.perf_counter() - started
    BUILD_ROOT.mkdir(parents=True, exist_ok=True)
    partial = Path(tempfile.mkdtemp(prefix="partial-", dir=BUILD_ROOT))
    argv = ["--project", str(project), "--output-dir", str(partial), "--ssh-host", ssh_host]
    code = check.main(argv)
    if code != 0:
        shutil.rmtree(partial, ignore_errors=True)
        raise ToolError(
            "the C# does not compile (diagnostics above); fix that first with "
            "python3 Tools/csharp_check/check.py" if code == 1 else f"check.py failed (exit {code})"
        )
    if target.exists():
        shutil.rmtree(target)
    partial.rename(target)
    # Keep the last three builds, drop older ones.
    builds = sorted((p for p in BUILD_ROOT.iterdir() if p.is_dir() and not p.name.startswith("partial-")),
                    key=lambda p: p.stat().st_mtime, reverse=True)
    for old in builds[3:]:
        shutil.rmtree(old, ignore_errors=True)
    return target, False, time.perf_counter() - started


# --- run ---------------------------------------------------------------------------------


def regex_escape(text: str) -> str:
    out = []
    for char in text:
        out.append("\\" + char if char in "\\.^$|?*+()[]{}" else char)
    return "".join(out)


def run_runner(runner_dll: Path, bin_dir: Path, filters: list[str], list_only: bool, timeout: int, workdir: Path,
               continue_mode: bool = False):
    result_xml = workdir / "result.xml"
    events = workdir / "events.json"
    # Raise a tool error if .NET 8 is absent; a newer SDK may build the host but must not run it.
    argv = [str(dotnet_executable()), "exec", "--roll-forward", "LatestPatch", str(runner_dll),
            "--bin", str(bin_dir), "--refs", str(CACHE_ROOT / "manifest.json"),
            "--assembly", TEST_ASSEMBLY, "--out", str(result_xml), "--events", str(events)]
    for fragment in filters:
        argv += ["--filter", regex_escape(fragment)]
    if list_only:
        argv.append("--list")
    if continue_mode:
        argv.append("--continue-after-failure")
    started = time.perf_counter()
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, env=_dotnet_env(), timeout=timeout)
    except subprocess.TimeoutExpired as exc:
        raise ToolError(f"the test runner did not finish within {timeout} s (a test may hang)") from exc
    seconds = time.perf_counter() - started
    if proc.returncode != 0 or not result_xml.exists():
        raise ToolError("test runner failed:\n" + (proc.stdout + proc.stderr)[-3000:])
    return result_xml.read_text(encoding="utf-8"), events, seconds


def git_state(project: Path) -> str:
    try:
        top = subprocess.run(["git", "-C", str(project), "rev-parse", "--show-toplevel"], capture_output=True, text=True, timeout=20)
        if top.returncode != 0:
            return "not a git checkout"
        head = subprocess.run(["git", "-C", str(project), "rev-parse", "--short", "HEAD"], capture_output=True, text=True, timeout=20).stdout.strip()
        dirty = subprocess.run(
            ["git", "-C", str(project), "status", "--porcelain", "--", "Assets/VRC_Katamari"],
            capture_output=True, text=True, timeout=60).stdout.strip().splitlines()
        return f"HEAD {head}, " + ("working tree clean under Assets/VRC_Katamari" if not dirty else f"{len(dirty)} uncommitted path(s) under Assets/VRC_Katamari")
    except (OSError, subprocess.TimeoutExpired):
        return "git unavailable"


# --- report ------------------------------------------------------------------------------

GROUP_LABELS = {
    tr.GROUP_SCENE: "需要 GameObject 或场景",
    tr.GROUP_NATIVE: "引擎原生调用",
    tr.GROUP_EDITOR: "编辑器 API",
    tr.GROUP_OTHER: "其它",
}


def short(name: str) -> str:
    return name.replace("VRCKatamari.Tests.", "")


def render(verdicts, env, known_doc, args, timing, state, filtered) -> str:
    summary = tr.summarize(verdicts)
    counts = summary["counts"]
    total = summary["total"]
    baseline = known_doc.get("unity_baseline", {})
    lines: list[str] = []
    add = lines.append
    add(f"本地 NUnit 运行: {env.get('runtime', '?')} {env.get('architecture', '')}, NUnit "
        f"{NUNIT_CONTINUE_VERSION + '（--continue-after-failure，非 Unity 的 3.5）' if args.continue_after_failure else NUNIT_VERSION}; "
        f"{state}; 编译 {'复用缓存' if timing['reused'] else format(timing['compile'], '.1f') + ' s'}, 运行 {timing['run']:.1f} s")
    scope = "（已过滤: " + ", ".join(args.filter) + "）" if filtered else ""
    add(f"分母 = 本地发现的用例数 {total}{scope}")
    add("")
    add(f"  通过                      {counts[tr.PASSED]:>4}")
    add(f"  失败                      {counts[tr.FAILED]:>4}")
    add(f"  与 Unity 不一致（已知）   {counts[tr.KNOWN_DIFFERENCE]:>4}")
    add(f"  本地不可执行              {counts[tr.NOT_RUNNABLE]:>4}   （不算通过）")
    for group in tr.GROUPS:
        if summary["not_runnable_groups"][group]:
            add(f"      {GROUP_LABELS[group]:<18}{summary['not_runnable_groups'][group]:>4}")
    if counts[tr.SKIPPED]:
        add(f"  跳过/忽略                 {counts[tr.SKIPPED]:>4}")
    add(f"  合计                      {total:>4}")

    if not filtered:
        sdk = baseline.get("sdk_test_count", 0)
        unity_total = baseline.get("total", 0)
        add("")
        add(f"与 Unity 对账: Unity 全量 {unity_total}（基线提交 {baseline.get('commit', '?')}）= 本地发现 {total} + VRC SDK 自带 "
            f"{baseline.get('sdk_assembly', 'UnityEditorTests')} {sdk}（本地不编译）+ 未解释 {unity_total - total - sdk}")
        if "uncommitted" in state:
            add("  （工作区有未提交改动，与 Unity 基线不同源，差值仅供参考；对齐基线请用 git archive 快照加 --project）")

    failures = [v for v in verdicts if v.category == tr.FAILED]
    if failures:
        add("")
        add(f"== 失败 ({len(failures)}) ==")
        for v in failures:
            add(f"✗ {v.case.name}")
            if v.location:
                add(f"    位置: {v.location}")
            if v.exception_types:
                add(f"    异常: {' -> '.join(v.exception_types)}")
            if v.note:
                add(f"    注: {v.note}")
            if len(v.case.assertions) > 1:
                add(f"    失败的断言 {len(v.case.assertions)} 条（--continue-after-failure：首条之后的可能是连带失败）:")
                for index, (message, where) in enumerate(v.case.assertions, 1):
                    add(f"    [{index}] {where or '?'}")
                    for text in message.splitlines():
                        add(f"        | {text}")
            else:
                for text in v.case.message.splitlines():
                    add(f"    | {text}")
    known = [v for v in verdicts if v.category == tr.KNOWN_DIFFERENCE]
    if known:
        add("")
        add(f"== 与 Unity 不一致（已知，Unity 里是绿的）({len(known)}) ==")
        for v in known:
            add(f"~ {v.case.name}")
            add(f"    原因: {v.cause}; 位置: {v.location or '?'}")
            for text in v.case.message.splitlines():
                add(f"    | {text}")
            add(f"    说明: {v.explanation}")
    unrunnable = [v for v in verdicts if v.category == tr.NOT_RUNNABLE]
    if unrunnable:
        add("")
        add(f"== 本地不可执行 ({len(unrunnable)}): 引擎拒绝了原生调用，用例没有真正运行 ==")
        by_native: dict[str, int] = {}
        for v in unrunnable:
            key = ", ".join(n.split(".")[-1] for n in v.natives) or v.thrower or (v.exception_types[-1] if v.exception_types else "?")
            by_native[key] = by_native.get(key, 0) + 1
        for key, count in sorted(by_native.items(), key=lambda kv: -kv[1])[:12]:
            add(f"  {count:>4} × {key}")
        swallowed = [v for v in unrunnable if v.note.startswith("passed")]
        for v in swallowed:
            add(f"  ! {v.case.name}: {v.note}")
        if args.verbose:
            for v in unrunnable:
                add(f"  - [{GROUP_LABELS[v.group]}] {v.case.name}  ({', '.join(v.natives) or v.thrower})")
        else:
            add("  （--verbose 列出每一条）")
    red = baseline.get("red", [])
    if red and not filtered:
        by_name = {v.case.name: v for v in verdicts}
        add("")
        add("Unity 最近一次红的用例在本地的状态:")
        for name in red:
            v = by_name.get(name)
            add(f"  {name}: " + (f"{v.category}" + (f" ({GROUP_LABELS[v.group]})" if v.group else "") if v else "本地没有这条（SDK 自带或已改名）"))
    stale = tr.stale_known_differences(verdicts, known_doc, ran_all=not filtered)
    if stale:
        add("")
        add("known_differences.json 里有过期条目（本地已不再出现该差异），请更新:")
        for item in stale:
            add(f"  {item}")
    add("")
    verdict_line = "结论: 本地可执行的用例" + ("全部通过" if not failures else f"有 {len(failures)} 条失败")
    add(verdict_line + f"（{counts[tr.PASSED]} 通过 / {total} 发现；不可执行的 {counts[tr.NOT_RUNNABLE]} 条仍需 Unity 验证，本地绿不等于 Unity 绿）")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run the engine-free NUnit EditMode tests locally.")
    parser.add_argument("--project", type=Path, default=REPO_ROOT / "VRChatKatamariDanacyUnity",
                        help="Unity project root (default: this checkout); use a git archive snapshot to pin a commit")
    parser.add_argument("--filter", action="append", default=[], metavar="TEXT",
                        help="only tests whose full name contains TEXT (repeatable, OR)")
    parser.add_argument("--list", action="store_true", help="discovery only: print the number of discovered cases")
    parser.add_argument("--json", action="store_true", help="machine-readable result on stdout")
    parser.add_argument("--verbose", action="store_true", help="list every not-runnable case; show passing names")
    parser.add_argument("--continue-after-failure", action="store_true",
                        help="diagnostic view: keep executing a test after a failed assertion and list every failed "
                        "assertion (runs on NUnit 3.14 because 3.5 has no Assert.Multiple; a different framework "
                        "version than Unity's 3.5, and later assertions may fail only because an earlier one did)")
    parser.add_argument("--rebuild", action="store_true", help="recompile even when the sources are unchanged")
    parser.add_argument("--refresh", action="store_true", help="re-probe Windows (as check.py --refresh) and rebuild")
    parser.add_argument("--timeout", type=int, default=600, help="seconds before the runner is killed")
    parser.add_argument("--ssh-host", default=os.environ.get("VRCK_SSH_HOST", "pc114514"))
    args = parser.parse_args(argv)
    project = args.project.resolve()
    workdir = Path(tempfile.mkdtemp(prefix="vrck-run-tests-"))
    try:
        nunit = ensure_nunit(args.continue_after_failure)
        runner = ensure_runner(nunit, args.continue_after_failure)
        bin_dir, reused, compile_seconds = build_assemblies(project, args.rebuild, args.ssh_host, args.refresh)
        known_doc = tr.load_known_differences(KNOWN_DIFFERENCES)
        document, events, run_seconds = run_runner(runner, bin_dir, args.filter, args.list, args.timeout, workdir,
                                                 args.continue_after_failure)
        if args.list:
            names = tr.parse_discovery_xml(document)
            print(f"discovered {len(names)} test case(s) in {TEST_ASSEMBLY}")
            if args.verbose:
                print("\n".join(names))
            return 0
        env, refusals = tr.load_refusals(events)
        cases = tr.parse_result_xml(document)
        verdicts = tr.classify_all(cases, refusals, known_doc)
        timing = {"reused": reused, "compile": compile_seconds, "run": run_seconds}
        state = git_state(project)
        if args.json:
            print(json.dumps({
                "project": str(project), "state": state, "environment": env,
                "summary": tr.summarize(verdicts), "timing": timing,
                "results": [v.to_json() for v in verdicts if v.category != tr.PASSED or args.verbose],
            }, indent=2, ensure_ascii=False))
        else:
            print(render(verdicts, env, known_doc, args, timing, state, bool(args.filter)))
        return 1 if any(v.category == tr.FAILED for v in verdicts) else 0
    except (ToolError, check.CheckError) as exc:
        print(f"run_tests failed: {exc}", file=sys.stderr)
        return 2
    finally:
        shutil.rmtree(workdir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
