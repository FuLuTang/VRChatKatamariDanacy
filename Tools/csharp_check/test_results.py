"""Parse and classify the NUnit result XML that Tools/csharp_check/test_runner writes.

Pure Python, no .NET needed: run_tests.py feeds it the runner's XML and sidecar, and the
unit tests under Docs/tests/csharp_check/ feed it fixtures.

Every test case ends in exactly one category:

  passed            NUnit says Passed (and it did not swallow an engine-native refusal).
  failed            An assertion failed or an exception other than the ones below escaped.
  known_difference  Failed locally, is green in Unity, and matches an entry of
                    known_differences.json (same test AND the recorded message fragments).
  not_runnable      The .NET runtime refused an engine-native call (SecurityException
                    "ECall methods must be packaged into a system module"), or the exception
                    type says the test cannot load here at all.  Never counted as passed.
  skipped           NUnit Ignored/Explicit/Skipped.

Classification looks only at the exception types the run actually threw (parsed from the
failure message chain) and at the sidecar the runner records from the first-chance
exception (the extern methods inside the refused method).  It never guesses from test names.
"""

from __future__ import annotations

import json
import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

PASSED = "passed"
FAILED = "failed"
KNOWN_DIFFERENCE = "known_difference"
NOT_RUNNABLE = "not_runnable"
SKIPPED = "skipped"
CATEGORIES = (PASSED, FAILED, KNOWN_DIFFERENCE, NOT_RUNNABLE, SKIPPED)

# Not-runnable sub-groups, in the precedence used when one refused method contains externs
# of several kinds.
GROUP_SCENE = "scene_object"
GROUP_EDITOR = "editor_api"
GROUP_NATIVE = "engine_native"
GROUP_OTHER = "other"
GROUPS = (GROUP_SCENE, GROUP_NATIVE, GROUP_EDITOR, GROUP_OTHER)

# Engine types whose natives only make sense with a live object or scene behind them.
SCENE_OBJECT_TYPES = {
    "UnityEngine.GameObject",
    "UnityEngine.Transform",
    "UnityEngine.Component",
    "UnityEngine.Object",
    "UnityEngine.Behaviour",
    "UnityEngine.MonoBehaviour",
    "UnityEngine.ScriptableObject",
    "UnityEngine.Renderer",
    "UnityEngine.MeshRenderer",
    "UnityEngine.MeshFilter",
    "UnityEngine.Collider",
    "UnityEngine.BoxCollider",
    "UnityEngine.SphereCollider",
    "UnityEngine.MeshCollider",
    "UnityEngine.Rigidbody",
}

# Exception types whose mere occurrence means "this environment cannot run the test", as
# opposed to "the test found a bug".  Only the type decides; nothing here reads a name.
NATIVE_EXCEPTION_TYPES = {
    "System.DllNotFoundException",
    "System.EntryPointNotFoundException",
}
LOADER_EXCEPTION_TYPES = {
    "System.TypeLoadException",
    "System.MissingMethodException",
    "System.MissingFieldException",
    "System.MissingMemberException",
    "System.IO.FileNotFoundException",
    "System.IO.FileLoadException",
    "System.BadImageFormatException",
    "System.PlatformNotSupportedException",
}
ECALL_TYPE = "System.Security.SecurityException"
ECALL_MARKER = "ECall methods must be packaged into a system module"

_CHAIN_LINE = re.compile(r"^\s*(?:----> )?(?P<type>[A-Za-z_][\w.+`]*(?:\[[^\]\r\n]*\])?) : (?P<text>.*)$")
_FRAME_WITH_SOURCE = re.compile(r"^\s*at .* in (?P<path>.+?):line (?P<line>\d+)\s*$")


@dataclass
class Case:
    """One executed (or discovered) NUnit test case."""

    name: str
    outcome: str = "Passed"  # NUnit result: Passed, Failed, Skipped, Inconclusive
    label: str = ""  # NUnit label: Error, Ignored, ...
    message: str = ""
    stack: str = ""
    seconds: float = 0.0
    # Every failed assertion as (message, "path:line"); more than one only in a
    # --continue-after-failure run, where a test goes on after its first failed assertion.
    assertions: list = field(default_factory=list)


@dataclass
class Verdict:
    case: Case
    category: str
    group: str = ""  # not_runnable sub-group
    exception_types: list = field(default_factory=list)  # outermost .. innermost
    natives: list = field(default_factory=list)  # extern methods in the refused method
    thrower: str = ""  # the method the runtime refused
    location: str = ""  # "path:line" of the failing assertion / throwing frame
    cause: str = ""  # known_difference: the cause id
    explanation: str = ""  # known_difference: why Unity is green
    note: str = ""

    def to_json(self) -> dict:
        return {
            "test": self.case.name,
            "category": self.category,
            "group": self.group,
            "exception_types": self.exception_types,
            "natives": self.natives,
            "thrower": self.thrower,
            "location": self.location,
            "cause": self.cause,
            "explanation": self.explanation,
            "note": self.note,
            "message": self.case.message,
            "failed_assertions": [{"message": m, "location": l} for m, l in self.case.assertions],
            "seconds": self.case.seconds,
        }


# --- XML ---------------------------------------------------------------------------


def _text(element: ET.Element | None) -> str:
    return (element.text or "").strip("\n") if element is not None else ""


def parse_result_xml(text: str) -> list[Case]:
    """Every <test-case> of an NUnit 3 result (or discovery) document."""
    root = ET.fromstring(text)
    cases: list[Case] = []
    for element in root.iter("test-case"):
        message = _text(element.find("failure/message"))
        if not message:
            message = _text(element.find("reason/message"))
        failed_assertions = []
        for assertion in element.findall("assertions/assertion"):
            if (assertion.get("result") or "") != "Failed":
                continue
            failed_assertions.append(
                (_text(assertion.find("message")), source_location(_text(assertion.find("stack-trace"))))
            )
        cases.append(
            Case(
                name=element.get("fullname") or element.get("name") or "",
                outcome=element.get("result") or ("Passed" if element.get("runstate") is None else "NotRun"),
                label=element.get("label") or "",
                message=message,
                stack=_text(element.find("failure/stack-trace")),
                seconds=float(element.get("duration") or 0.0),
                assertions=failed_assertions,
            )
        )
    return cases


def parse_discovery_xml(text: str) -> list[str]:
    """Full names of the test cases in a --list document, in tree order."""
    return [e.get("fullname") or "" for e in ET.fromstring(text).iter("test-case")]


def exception_chain(message: str) -> list[tuple[str, str]]:
    """(type, text) from outermost to innermost exception in an NUnit error message.

    NUnit writes an error as ``Type : message`` and each inner exception as an indented
    ``----> Type : message`` line.  An assertion failure has no type prefix at all.
    """
    chain: list[tuple[str, str]] = []
    for index, line in enumerate(message.splitlines()):
        match = _CHAIN_LINE.match(line)
        if match is None:
            continue
        # Only the first line and explicit "----> " lines are exception headers; a
        # message body line that merely looks like "Foo : bar" is not.
        if index == 0 or line.lstrip().startswith("----> "):
            chain.append((match.group("type"), match.group("text")))
    return chain


def source_location(stack: str, repo_hint: str = "VRChatKatamariDanacyUnity/") -> str:
    """The first stack frame that has a source position, as ``path:line`` (repo-relative)."""
    for line in stack.splitlines():
        match = _FRAME_WITH_SOURCE.match(line)
        if match is None:
            continue
        path = match.group("path").replace("\\", "/")
        cut = path.find(repo_hint)
        if cut >= 0:
            path = path[cut:]
        return f"{path}:{match.group('line')}"
    return ""


# --- sidecar and known differences ---------------------------------------------------


def load_refusals(path: Path) -> tuple[dict, dict[str, list[dict]]]:
    """(environment, refusals by test full name) from the runner's --events sidecar."""
    if not path.exists():
        return {}, {}
    data = json.loads(path.read_text(encoding="utf-8"))
    by_test: dict[str, list[dict]] = {}
    for refusal in data.get("nativeRefusals", []):
        by_test.setdefault(refusal.get("Test", ""), []).append(refusal)
    return {"runtime": data.get("runtime", ""), "architecture": data.get("architecture", "")}, by_test


def load_known_differences(path: Path) -> dict:
    if not path.exists():
        return {"differences": [], "unity_baseline": {}}
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("schema") != 1:
        raise ValueError(f"unsupported known_differences schema in {path}")
    return data


def _declaring_type(native: str) -> str:
    return native.split("::", 1)[0]


def native_group(natives: list[str]) -> str:
    """not_runnable sub-group from the engine externs inside the refused method."""
    engine = [n for n in natives if n.startswith(("UnityEngine.", "UnityEditor."))]
    if any(_declaring_type(n) in SCENE_OBJECT_TYPES or _declaring_type(n).startswith("UnityEngine.SceneManagement.") for n in engine):
        return GROUP_SCENE
    if any(n.startswith("UnityEditor.") for n in engine):
        return GROUP_EDITOR
    if engine:
        return GROUP_NATIVE
    return GROUP_OTHER


# --- classification --------------------------------------------------------------------


def classify(case: Case, refusals: dict[str, list[dict]], known: list[dict]) -> Verdict:
    outcome = case.outcome
    own = refusals.get(case.name, [])
    natives = sorted({n for r in own for n in r.get("Natives", []) if n.startswith(("UnityEngine.", "UnityEditor."))})
    thrower = own[0].get("Thrower", "") if own else ""

    if outcome == "Passed":
        if own:
            # The test passed although the runtime refused an engine call inside it: the
            # test swallowed the exception, so the pass proves nothing.
            return Verdict(
                case, NOT_RUNNABLE, group=native_group(natives), natives=natives, thrower=thrower,
                exception_types=[ECALL_TYPE],
                note="passed, but an engine-native call was refused and swallowed inside the test",
            )
        return Verdict(case, PASSED)
    if outcome in ("Skipped", "Inconclusive", "NotRun") and case.label not in ("Error", "Invalid"):
        return Verdict(case, SKIPPED, note=case.label or outcome)

    # NUnit labels an escaped exception "Error" (or "SetUp Error", ...); an assertion failure
    # has no label and no exception-type prefix in its message.
    chain = exception_chain(case.message) if "Error" in case.label else []
    types = [t for t, _ in chain]
    innermost = chain[-1] if chain else ("", "")
    location = source_location(case.stack)

    if innermost[0] == ECALL_TYPE and ECALL_MARKER in innermost[1]:
        return Verdict(
            case, NOT_RUNNABLE, group=native_group(natives), exception_types=types, natives=natives,
            thrower=thrower, location=location,
        )
    if innermost[0] in NATIVE_EXCEPTION_TYPES:
        return Verdict(case, NOT_RUNNABLE, group=GROUP_NATIVE, exception_types=types, location=location)
    if innermost[0] in LOADER_EXCEPTION_TYPES:
        return Verdict(
            case, NOT_RUNNABLE, group=GROUP_OTHER, exception_types=types, location=location,
            note="the type or assembly cannot be loaded in this environment",
        )

    verdict = Verdict(case, FAILED, exception_types=types, location=location)
    for entry in known:
        if entry.get("test") != case.name:
            continue
        if len(case.assertions) > 1:
            # --continue-after-failure: the test went on and failed more assertions.  The entry
            # explains them only if it lists exactly these ("assertions": one list of message
            # fragments per failed assertion, in order).
            expected = entry.get("assertions")
            if not expected or len(expected) != len(case.assertions) or not all(
                all(fragment in message for fragment in fragments)
                for fragments, (message, _) in zip(expected, case.assertions)
            ):
                verdict.note = "listed in known_differences.json, but the assertions that failed after the first are not the recorded ones"
                continue
        fragments = entry.get("message_contains", [])
        first_message = case.assertions[0][0] if case.assertions else case.message
        if all(fragment in first_message for fragment in fragments):
            verdict.category = KNOWN_DIFFERENCE
            verdict.cause = entry.get("cause", "")
            verdict.explanation = entry.get("explanation", "")
            return verdict
        verdict.note = "listed in known_differences.json, but the failure no longer matches the recorded message: a different failure"
    return verdict


def classify_all(cases: list[Case], refusals: dict[str, list[dict]], known_doc: dict) -> list[Verdict]:
    known = known_doc.get("differences", [])
    return [classify(case, refusals, known) for case in cases]


def stale_known_differences(verdicts: list[Verdict], known_doc: dict, ran_all: bool) -> list[str]:
    """known_differences.json entries that did not fail locally (ran_all=True: also absent ones)."""
    by_name = {v.case.name: v for v in verdicts}
    stale = []
    for entry in known_doc.get("differences", []):
        verdict = by_name.get(entry.get("test", ""))
        if verdict is None:
            if ran_all:
                stale.append(f"{entry.get('test')}: not discovered any more")
        elif verdict.category != KNOWN_DIFFERENCE:
            stale.append(f"{entry.get('test')}: now {verdict.category}")
    return stale


def summarize(verdicts: list[Verdict]) -> dict:
    counts = {category: 0 for category in CATEGORIES}
    groups = {group: 0 for group in GROUPS}
    for verdict in verdicts:
        counts[verdict.category] += 1
        if verdict.category == NOT_RUNNABLE:
            groups[verdict.group or GROUP_OTHER] += 1
    return {"total": len(verdicts), "counts": counts, "not_runnable_groups": groups}
