import tempfile
import unittest
from pathlib import Path

from Docs.tools.third_batch.validate import (
    check_dag,
    load_csv,
    parse_json_text,
    validate_workspace,
)


ROOT = Path(__file__).resolve().parents[3]


class JsonSafetyTests(unittest.TestCase):
    def test_rejects_nonstandard_nan_and_infinity_tokens(self):
        for text in ('{"value": NaN}', '{"value": Infinity}', '{"value": -Infinity}'):
            with self.subTest(text=text):
                errors = []
                self.assertIsNone(parse_json_text(text, "fixture.json", errors))
                self.assertTrue(any("invalid JSON" in error for error in errors))

    def test_rejects_overflowed_json_exponent(self):
        errors = []
        self.assertEqual({"value": float("inf")}, parse_json_text('{"value": 1e999}', "fixture.json", errors))
        self.assertTrue(any("non-finite numeric value" in error for error in errors))


class StructureAndGraphTests(unittest.TestCase):
    def test_csv_rejects_duplicate_headers_and_ragged_rows(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "broken.csv").write_text("id,id\na,b,c\n", encoding="utf-8")
            errors = []
            rows, header = load_csv(root, "broken.csv", errors)
        self.assertEqual(["id", "id"], header)
        self.assertEqual([], rows)
        self.assertTrue(any("duplicate column" in error for error in errors))
        self.assertTrue(any("row width" in error for error in errors))

    def test_dag_checker_reports_unknown_links_and_cycles(self):
        errors = []
        check_dag({"A": ["B"], "B": ["A"], "C": ["missing"]}, "fixture DAG", errors)
        self.assertTrue(any("dependency cycle" in error for error in errors))
        self.assertTrue(any("unknown node missing" in error for error in errors))


class WorkspaceIntegrationTests(unittest.TestCase):
    def test_current_p09_to_p18_host_data_is_consistent(self):
        errors = validate_workspace(ROOT)
        self.assertEqual([], errors, "\n".join(errors))


if __name__ == "__main__":
    unittest.main()
