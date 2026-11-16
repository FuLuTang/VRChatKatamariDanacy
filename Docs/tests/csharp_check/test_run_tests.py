"""Tools/csharp_check/run_tests.py: result parsing and the honest three-way classification.

What these tests protect (each is a rule that fails silently otherwise):

* a case the .NET runtime could not run because of an engine-native call is NEVER counted
  as passed, whether it failed with the ECall SecurityException (also wrapped in a
  TargetInvocationException) or "passed" after swallowing it;
* "not runnable" is decided from the exception type the run actually threw, not from a
  name: an ordinary assertion failure with a similar-looking message stays a failure;
* a known difference from Unity needs the test name AND the recorded message fragments (a
  different failure of the same test is a failure), and in --continue-after-failure runs
  every extra failed assertion must be one the entry records;
* check.py's default compile is unchanged: PDBs and --output-dir are opt-in.

The fixtures are hand-written NUnit 3 XML; no .NET, Unity or network is needed.
"""

import json
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
CHECK_DIR = REPO_ROOT / "Tools" / "csharp_check"
sys.dont_write_bytecode = True
sys.path.insert(0, str(CHECK_DIR))

import check  # noqa: E402
import run_tests  # noqa: E402
import test_results as tr  # noqa: E402

ECALL = "ECall methods must be packaged into a system module."


def case(name, outcome="Failed", label="", message="", stack="", assertions=None):
    return tr.Case(name=name, outcome=outcome, label=label, message=message, stack=stack,
                   assertions=assertions or [])


def error_message(*chain):
    """NUnit's own wording for an escaped exception and its inner exceptions."""
    lines = [f"{chain[0][0]} : {chain[0][1]}"]
    lines += [f"  ----> {kind} : {text}" for kind, text in chain[1:]]
    return "\n".join(lines)


RESULT_XML = """<test-suite type="Assembly" name="T.dll" fullname="T" result="Failed">
  <test-case name="Ok" fullname="N.C.Ok" result="Passed" duration="0.001"/>
  <test-case name="Bad" fullname="N.C.Bad" result="Failed" duration="0.002">
    <failure>
      <message><![CDATA[  Expected: 1
  But was:  2
]]></message>
      <stack-trace><![CDATA[   at N.C.Bad() in /home/x/repo/VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/C.cs:line 42
]]></stack-trace>
    </failure>
    <assertions>
      <assertion result="Failed"><message><![CDATA[  Expected: 1
  But was:  2
]]></message><stack-trace><![CDATA[   at N.C.Bad() in /home/x/repo/VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/C.cs:line 42
]]></stack-trace></assertion>
      <assertion result="Failed"><message><![CDATA[later]]></message><stack-trace><![CDATA[   at N.C.Bad() in /home/x/repo/VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Tests/EditMode/C.cs:line 44
]]></stack-trace></assertion>
    </assertions>
  </test-case>
  <test-case name="Boom" fullname="N.C.Boom" result="Failed" label="Error" duration="0.0">
    <failure><message><![CDATA[System.Security.SecurityException : ECall methods must be packaged into a system module.]]></message></failure>
  </test-case>
  <test-case name="Skip" fullname="N.C.Skip" result="Skipped" label="Ignored">
    <reason><message><![CDATA[not now]]></message></reason>
  </test-case>
</test-suite>"""


class ParsingTests(unittest.TestCase):
    def test_result_xml_reads_every_case_with_outcome_label_message_and_assertions(self):
        cases = {c.name: c for c in tr.parse_result_xml(RESULT_XML)}
        self.assertEqual(sorted(cases), ["N.C.Bad", "N.C.Boom", "N.C.Ok", "N.C.Skip"])
        self.assertEqual(cases["N.C.Ok"].outcome, "Passed")
        self.assertEqual(cases["N.C.Boom"].label, "Error")
        self.assertIn("But was:  2", cases["N.C.Bad"].message)
        self.assertEqual(cases["N.C.Skip"].message, "not now")
        self.assertEqual(len(cases["N.C.Bad"].assertions), 2)
        self.assertTrue(cases["N.C.Bad"].assertions[1][1].endswith("C.cs:44"))

    def test_discovery_lists_cases_in_tree_order(self):
        names = tr.parse_discovery_xml(
            '<test-suite><test-case fullname="A.B.One"/><test-suite><test-case fullname="A.B.Two"/></test-suite></test-suite>')
        self.assertEqual(names, ["A.B.One", "A.B.Two"])

    def test_exception_chain_reads_outer_and_inner_but_not_body_lines(self):
        message = error_message(
            ("System.Reflection.TargetInvocationException", "Exception has been thrown by the target of an invocation."),
            ("System.Security.SecurityException", ECALL)) + "\nNote : looks like a header but is a body line"
        chain = tr.exception_chain(message)
        self.assertEqual([t for t, _ in chain],
                         ["System.Reflection.TargetInvocationException", "System.Security.SecurityException"])

    def test_assertion_message_has_no_exception_chain(self):
        self.assertEqual(tr.exception_chain("  Expected: 1\n  But was:  2\n"), [])

    def test_source_location_is_the_first_frame_with_a_source_position_made_repo_relative(self):
        stack = ("   at System.Foo()\n   at N.C.M() in /home/x/repo/VRChatKatamariDanacyUnity/Assets/A.cs:line 7\n"
                 "   at N.C.Other() in /elsewhere/B.cs:line 9")
        self.assertEqual(tr.source_location(stack), "VRChatKatamariDanacyUnity/Assets/A.cs:7")
        self.assertEqual(tr.source_location("   at System.Foo()"), "")


class ClassificationTests(unittest.TestCase):
    def verdict(self, c, refusals=None, known=None):
        return tr.classify(c, refusals or {}, known or [])

    def test_plain_pass_is_passed(self):
        self.assertEqual(self.verdict(case("T.Ok", "Passed")).category, tr.PASSED)

    def test_ecall_security_exception_is_not_runnable_by_its_type(self):
        v = self.verdict(case("T.A", label="Error",
                              message=error_message(("System.Security.SecurityException", ECALL))))
        self.assertEqual(v.category, tr.NOT_RUNNABLE)

    def test_ecall_wrapped_in_target_invocation_uses_the_innermost_type(self):
        message = error_message(
            ("System.Reflection.TargetInvocationException", "Exception has been thrown by the target of an invocation."),
            ("System.Security.SecurityException", ECALL))
        v = self.verdict(case("T.A", label="Error", message=message))
        self.assertEqual(v.category, tr.NOT_RUNNABLE)
        self.assertEqual(v.exception_types[-1], "System.Security.SecurityException")

    def test_a_target_invocation_wrapping_another_exception_is_a_failure(self):
        message = error_message(
            ("System.Reflection.TargetInvocationException", "Exception has been thrown by the target of an invocation."),
            ("System.InvalidOperationException", "boom"))
        self.assertEqual(self.verdict(case("T.A", label="Error", message=message)).category, tr.FAILED)

    def test_a_security_exception_without_the_ecall_text_is_a_failure(self):
        message = error_message(("System.Security.SecurityException", "request failed"))
        self.assertEqual(self.verdict(case("T.A", label="Error", message=message)).category, tr.FAILED)

    def test_an_assertion_whose_message_mentions_ecall_is_still_a_failure(self):
        c = case("T.A", message="  Expected: ECall methods must be packaged into a system module\n  But was: x")
        self.assertEqual(self.verdict(c).category, tr.FAILED)

    def test_missing_native_library_is_not_runnable_engine_native(self):
        message = error_message(("System.DllNotFoundException", "Unable to load shared library"))
        v = self.verdict(case("T.A", label="Error", message=message))
        self.assertEqual((v.category, v.group), (tr.NOT_RUNNABLE, tr.GROUP_NATIVE))

    def test_loader_exceptions_are_not_runnable_other(self):
        message = error_message(("System.TypeLoadException", "Could not load type"))
        v = self.verdict(case("T.A", label="Error", message=message))
        self.assertEqual((v.category, v.group), (tr.NOT_RUNNABLE, tr.GROUP_OTHER))

    def test_other_exceptions_and_assertions_are_failures_with_a_position(self):
        v = self.verdict(case("T.A", label="Error", message=error_message(("System.NullReferenceException", "x")),
                              stack="   at T.A() in /r/VRChatKatamariDanacyUnity/Assets/T.cs:line 3"))
        self.assertEqual(v.category, tr.FAILED)
        self.assertTrue(v.location.endswith("T.cs:3"))
        self.assertEqual(self.verdict(case("T.B", message="  Expected: 1\n  But was: 2")).category, tr.FAILED)

    def test_ignored_is_skipped_not_passed(self):
        self.assertEqual(self.verdict(case("T.S", "Skipped", label="Ignored")).category, tr.SKIPPED)

    def test_a_pass_that_swallowed_a_refused_native_call_is_not_runnable(self):
        refusals = {"T.Swallow": [{"Test": "T.Swallow", "Thrower": "T::M", "Natives": ["UnityEngine.Time::get_time"]}]}
        v = self.verdict(case("T.Swallow", "Passed"), refusals)
        self.assertEqual((v.category, v.group), (tr.NOT_RUNNABLE, tr.GROUP_NATIVE))
        self.assertIn("swallowed", v.note)

    def test_not_runnable_groups_come_from_the_extern_methods_in_the_refused_method(self):
        self.assertEqual(tr.native_group(["UnityEngine.GameObject::Internal_CreateGameObject"]), tr.GROUP_SCENE)
        self.assertEqual(tr.native_group(["UnityEngine.Quaternion::Inverse_Injected"]), tr.GROUP_NATIVE)
        self.assertEqual(tr.native_group(["UnityEngine.SceneManagement.SceneManager::get_sceneCount"]), tr.GROUP_SCENE)
        self.assertEqual(tr.native_group(["UnityEditor.AssetDatabase::LoadMainAssetAtPath"]), tr.GROUP_EDITOR)
        self.assertEqual(tr.native_group([]), tr.GROUP_OTHER)
        self.assertEqual(tr.native_group(["UnityEngine.Quaternion::Inverse_Injected",
                                          "UnityEngine.Transform::SetParent"]), tr.GROUP_SCENE)
        # An editor extern next to a plain engine extern: the editor API is the sharper reason.
        self.assertEqual(tr.native_group(["UnityEditor.X::y", "UnityEngine.Time::get_time"]), tr.GROUP_EDITOR)

    def test_ecall_verdict_carries_the_natives_from_the_sidecar(self):
        refusals = {"T.A": [{"Test": "T.A", "Thrower": "UnityEngine.GameObject::.ctor",
                             "Natives": ["UnityEngine.GameObject::Internal_CreateGameObject"]}]}
        v = self.verdict(case("T.A", label="Error", message=error_message(("System.Security.SecurityException", ECALL))), refusals)
        self.assertEqual((v.group, v.natives, v.thrower),
                         (tr.GROUP_SCENE, ["UnityEngine.GameObject::Internal_CreateGameObject"], "UnityEngine.GameObject::.ctor"))


class KnownDifferenceTests(unittest.TestCase):
    ENTRY = {"test": "T.Known", "cause": "float", "explanation": "why",
             "message_contains": ["Expected: 500", "But was:  0"]}

    def test_known_difference_needs_the_name_and_the_message_fragments(self):
        v = tr.classify(case("T.Known", message="  Expected: 500\n  But was:  0\n"), {}, [self.ENTRY])
        self.assertEqual((v.category, v.cause, v.explanation), (tr.KNOWN_DIFFERENCE, "float", "why"))

    def test_the_same_test_failing_differently_is_a_failure_and_says_so(self):
        v = tr.classify(case("T.Known", message="  Expected: 500\n  But was:  7\n"), {}, [self.ENTRY])
        self.assertEqual(v.category, tr.FAILED)
        self.assertIn("different failure", v.note)

    def test_another_test_with_the_same_message_is_not_covered(self):
        v = tr.classify(case("T.Other", message="  Expected: 500\n  But was:  0\n"), {}, [self.ENTRY])
        self.assertEqual(v.category, tr.FAILED)

    def test_continue_mode_extra_assertions_must_be_recorded_by_the_entry(self):
        first = ("  Expected: 500\n  But was:  0\n", "a.cs:1")
        second = ("saturation is exact", "a.cs:2")
        multi = case("T.Known", message="Multiple failures or warnings in test", assertions=[first, second])
        v = tr.classify(multi, {}, [self.ENTRY])
        self.assertEqual(v.category, tr.FAILED)
        entry = dict(self.ENTRY, assertions=[["Expected: 500"], ["saturation is exact"]])
        self.assertEqual(tr.classify(multi, {}, [entry]).category, tr.KNOWN_DIFFERENCE)
        entry = dict(self.ENTRY, assertions=[["Expected: 500"], ["something else"]])
        self.assertEqual(tr.classify(multi, {}, [entry]).category, tr.FAILED)

    def test_stale_entries_are_reported(self):
        verdicts = [tr.classify(case("T.Known", "Passed"), {}, [self.ENTRY])]
        doc = {"differences": [self.ENTRY, {"test": "T.Gone", "message_contains": []}]}
        stale = tr.stale_known_differences(verdicts, doc, ran_all=True)
        self.assertEqual(len(stale), 2)
        self.assertEqual(len(tr.stale_known_differences(verdicts, doc, ran_all=False)), 1)

    def test_the_checked_in_file_is_well_formed(self):
        doc = tr.load_known_differences(CHECK_DIR / "known_differences.json")
        self.assertEqual(doc["unity_baseline"]["total"], 983)
        seen = set()
        for entry in doc["differences"]:
            self.assertTrue(entry["test"].startswith("VRCKatamari.Tests."))
            self.assertTrue(entry["message_contains"], entry["test"])
            self.assertTrue(entry["cause"] and entry["explanation"])
            self.assertNotIn(entry["test"], seen)
            seen.add(entry["test"])


class SummaryAndHelpersTests(unittest.TestCase):
    def test_summary_counts_every_category_and_the_denominator_is_the_discovered_total(self):
        refusals = {}
        known = [KnownDifferenceTests.ENTRY]
        cases = [
            case("T.P", "Passed"),
            case("T.F", message="  Expected: 1\n  But was: 2"),
            case("T.K", message="  Expected: 500\n  But was:  0\n"),
            case("T.N", label="Error", message=error_message(("System.Security.SecurityException", ECALL))),
            case("T.S", "Skipped", label="Ignored"),
        ]
        cases[2].name = "T.Known"
        summary = tr.summarize(tr.classify_all(cases, refusals, {"differences": known}))
        self.assertEqual(summary["total"], 5)
        self.assertEqual(summary["counts"], {tr.PASSED: 1, tr.FAILED: 1, tr.KNOWN_DIFFERENCE: 1,
                                             tr.NOT_RUNNABLE: 1, tr.SKIPPED: 1})
        self.assertEqual(summary["not_runnable_groups"][tr.GROUP_OTHER], 1)

    def test_refusal_sidecar_is_grouped_by_test(self):
        import tempfile
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "events.json"
            path.write_text(json.dumps({"runtime": ".NET 8", "architecture": "Arm64", "nativeRefusals": [
                {"Test": "T.A", "Thrower": "x", "Natives": ["UnityEngine.Time::get_time"], "Message": ECALL},
                {"Test": "T.A", "Thrower": "y", "Natives": [], "Message": ECALL}]}))
            env, by_test = tr.load_refusals(path)
            self.assertEqual(env["architecture"], "Arm64")
            self.assertEqual(len(by_test["T.A"]), 2)
            self.assertEqual(tr.load_refusals(Path(tmp) / "missing.json"), ({}, {}))

    def test_filter_text_is_escaped_for_the_runner_regex(self):
        self.assertEqual(run_tests.regex_escape("A.B(1)+[x]"), "A\\.B\\(1\\)\\+\\[x\\]")
        self.assertEqual(run_tests.regex_escape("Plain"), "Plain")


class CompileBehaviourTests(unittest.TestCase):
    """check.py stays what it was unless --output-dir is given."""

    MANIFEST = {"assemblies": {"A": {"defines": ["X", "Y"]}}, "langversion": "9.0"}

    def rsp(self, **kwargs):
        unit = check.Unit("A", "r", asmdef=True)
        project = Path("/p")
        unit.sources = [project / "a.cs"]
        return check.response_file(self.MANIFEST, unit, project, [Path("/r/x.dll")], Path("/o/A.dll"), **kwargs)

    def test_default_response_file_has_no_debug_option(self):
        self.assertNotIn("-debug", self.rsp())

    def test_debug_symbols_are_opt_in_portable_pdbs(self):
        self.assertIn("-debug:portable", self.rsp(debug_symbols=True))

    def test_output_dir_is_a_local_backend_option(self):
        text = (CHECK_DIR / "check.py").read_text(encoding="utf-8")
        self.assertIn('"--output-dir"', text)
        self.assertIn("--output-dir needs --backend local", text)


if __name__ == "__main__":
    unittest.main()
