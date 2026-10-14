"""Regression tests for the P18 unrun-template guards (host-only)."""
import csv
import importlib.util
import io
import json
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
P18_SOURCE = REPO_ROOT / "Docs/Preparation/RuntimeEvidence/validate_templates.py"

# Load the P18 standalone checker without emitting __pycache__ outside this test scope.
_spec = importlib.util.spec_from_file_location("p18_validate_templates", P18_SOURCE)
P18 = importlib.util.module_from_spec(_spec)
_previous_bytecode_setting = sys.dont_write_bytecode
sys.dont_write_bytecode = True
try:
    _spec.loader.exec_module(P18)
    from Docs.tools.third_batch.validate import _check_runtime_evidence
finally:
    sys.dont_write_bytecode = _previous_bytecode_setting


class RuntimeEvidenceTemplateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.fixture_root = Path(self.temp.name) / "repo"
        self.p18_root = self.fixture_root / "Docs/Preparation/RuntimeEvidence"
        self.p18_root.mkdir(parents=True)
        source_root = REPO_ROOT / "Docs/Preparation/RuntimeEvidence"
        shutil.copyfile(source_root / "cases.csv", self.p18_root / "cases.csv")
        shutil.copyfile(source_root / "metrics.csv", self.p18_root / "metrics.csv")
        session = self.p18_root / "session"
        session.mkdir()
        shutil.copyfile(source_root / "session/environment.json", session / "environment.json")
        self.original_root = P18.ROOT
        P18.ROOT = self.p18_root
        self.addCleanup(setattr, P18, "ROOT", self.original_root)
        self.addCleanup(self.temp.cleanup)

    @staticmethod
    def read_records(path):
        with path.open("r", encoding="utf-8", newline="") as stream:
            return list(csv.reader(stream))

    @staticmethod
    def write_records(path, records):
        with path.open("w", encoding="utf-8", newline="") as stream:
            csv.writer(stream, lineterminator="\n").writerows(records)

    def test_current_p18_templates_pass_standalone_structure_check(self):
        output = io.StringIO()
        with redirect_stdout(output):
            P18.main()
        self.assertIn("P18 template integrity: PASS", output.getvalue())

    def test_rejects_csv_without_header_row(self):
        path = self.p18_root / "cases.csv"
        path.write_text("", encoding="utf-8")
        with self.assertRaisesRegex(AssertionError, "empty CSV"):
            P18.read_csv("cases.csv")

    def test_rejects_empty_header_field_standalone(self):
        path = self.p18_root / "cases.csv"
        records = self.read_records(path)
        records[0][records[0].index("category")] = ""
        self.write_records(path, records)
        with self.assertRaisesRegex(AssertionError, "empty or duplicate column name"):
            P18.read_csv("cases.csv")

    def test_rejects_duplicate_header_before_dict_reader_standalone(self):
        path = self.p18_root / "cases.csv"
        records = self.read_records(path)
        records[0][records[0].index("category")] = "case_id"
        self.write_records(path, records)
        with self.assertRaisesRegex(AssertionError, "empty or duplicate column name"):
            P18.read_csv("cases.csv")

    def test_rejects_ragged_csv_rows(self):
        path = self.p18_root / "cases.csv"
        records = self.read_records(path)
        records[1] = records[1][:-1]
        self.write_records(path, records)
        with self.assertRaisesRegex(AssertionError, "inconsistent CSV row width"):
            P18.read_csv("cases.csv")

    def test_rejects_wrong_csv_schema_version(self):
        path = self.p18_root / "cases.csv"
        records = self.read_records(path)
        records[1][0] = "2"
        self.write_records(path, records)
        with self.assertRaisesRegex(AssertionError, "schemaVersion must be 1"):
            P18.read_csv("cases.csv")

    def test_rejects_non_not_run_case_status(self):
        cases, _ = P18.read_csv("cases.csv")
        cases[0]["status"] = "PASS"
        with self.assertRaisesRegex(AssertionError, "must be NOT_RUN"):
            P18.validate_cases(cases)

    def test_rejects_nonempty_not_run_observation_fields(self):
        for field in ("observed", "evidence"):
            with self.subTest(field=field):
                cases, _ = P18.read_csv("cases.csv")
                cases[0][field] = "sample observation"
                with self.assertRaisesRegex(AssertionError, "must be blank"):
                    P18.validate_cases(cases)

    def test_rejects_metric_status_and_populated_observations(self):
        metrics, _ = P18.read_csv("metrics.csv")
        metrics[0]["status"] = "PASS"
        with self.assertRaisesRegex(AssertionError, "must be NOT_RUN"):
            P18.validate_metrics(metrics)

        metrics, _ = P18.read_csv("metrics.csv")
        metrics[0]["sample_count"] = "25"
        with self.assertRaisesRegex(AssertionError, "must be blank"):
            P18.validate_metrics(metrics)

    def test_rejects_environment_schema_and_filled_run_observation(self):
        path = self.p18_root / "session/environment.json"
        env = P18.parse_environment(path.read_text(encoding="utf-8"))
        env["schemaVersion"] = 2
        with self.assertRaises(AssertionError):
            P18.validate_environment(env)

        env = P18.parse_environment(path.read_text(encoding="utf-8"))
        env["runId"] = "filled-run-id"
        with self.assertRaisesRegex(AssertionError, "must remain null"):
            P18.validate_environment(env)

    def test_integrated_p18_guard_rejects_duplicate_case_header(self):
        """The P22 workspace guard rejects duplicate headers in the P18 CSV."""
        path = self.p18_root / "cases.csv"
        records = self.read_records(path)
        category_index = records[0].index("category")
        records[0][category_index] = "case_id"
        self.write_records(path, records)

        errors = []
        _check_runtime_evidence(self.fixture_root, errors)
        self.assertTrue(
            any("header contains an empty or duplicate column name" in error for error in errors),
            "duplicate P18 case header should be rejected by the integrated workspace guard",
        )


if __name__ == "__main__":
    unittest.main()
