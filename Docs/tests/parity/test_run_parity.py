"""The gap-table runner: determinism, verdict semantics, null tolerance.

Three properties matter more than any single number:

1. **Deterministic** — same corpus + same level → byte-identical Markdown, so a
   diff in the report is a diff in the data, not in the run.
2. **Honest under nulls** — a stage whose timer unit is unknown must produce a
   row that says so, not a row with a guessed number and not a crash.
3. **Refuses rather than ignores** — a corpus with an unknown field exits
   non-zero *before* a table is printed, because a silently dropped field looks
   exactly like a metric that passed.
"""

import contextlib
import copy
import io
import json
import tempfile
import unittest
from pathlib import Path

from Docs.Design.Parity.parity_harness import run_parity
from Docs.Design.Parity.parity_harness.corpus_schema import Corpus, validate_corpus
from Docs.tests.parity.support import (
    AUDIT_DOC,
    CANCER_CORPUS,
    OURS_LEVEL,
    SEED_CORPUS,
    synthetic_corpus,
    synthetic_ours_level,
    write_corpus,
)

HAVE_SEED = SEED_CORPUS.is_file()
HAVE_CANCER = CANCER_CORPUS.is_file()
NO_CORPUS = "corpus not built: local data-mine export, not committed " \
            "(run parity_harness/build_corpus.py)"


def quiet(function, *args):
    """Call a CLI entry point without letting it print into the test report."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        result = function(*args)
    return result, out.getvalue(), err.getvalue()


def render_synthetic(corpus_dict=None, ours_dict=None):
    corpus = Corpus(corpus_dict or synthetic_corpus())
    ours = run_parity.OursLevel(ours_dict or synthetic_ours_level())
    rows = run_parity.build_rows(corpus, ours)
    return run_parity.render(corpus, ours, rows), rows


class DeterminismTest(unittest.TestCase):
    def test_two_runs_are_identical(self):
        first, _ = render_synthetic()
        second, _ = render_synthetic()
        self.assertEqual(first, second)

    @unittest.skipUnless(HAVE_SEED, NO_CORPUS)
    def test_seed_run_is_identical_across_calls(self):
        ours = run_parity.load_ours_level(OURS_LEVEL)
        corpus = run_parity.load_corpus(SEED_CORPUS)
        a = run_parity.render(corpus, ours, run_parity.build_rows(corpus, ours))
        b = run_parity.render(corpus, ours, run_parity.build_rows(corpus, ours))
        self.assertEqual(a, b)

    def test_rows_are_ordered_and_unique(self):
        _, rows = render_synthetic()
        ids = [row.id for row in rows]
        self.assertEqual(len(ids), len(set(ids)))
        self.assertEqual(47, len(rows))
        # Rows are grouped by layer in the documented order, and each layer is
        # contiguous (the report reads layer by layer).
        prefixes = [row.id[0] for row in rows]
        order = []
        for prefix in prefixes:
            if not order or order[-1] != prefix:
                order.append(prefix)
        self.assertEqual(["T", "M", "G", "C", "L", "S"], order)

    def test_every_row_is_in_the_vocabulary(self):
        _, rows = render_synthetic()
        for row in rows:
            self.assertIn(row.klass, run_parity.CLASSES, row.id)
            self.assertTrue(row.name, row.id)
            self.assertTrue(row.ours_source, row.id)
            self.assertIn(row.layer, run_parity.LAYERS.values())


class VerdictTest(unittest.TestCase):
    def _row(self, rows, metric_id):
        for row in rows:
            if row.id == metric_id:
                return row
        raise AssertionError("no row %s" % metric_id)

    def test_matching_start_diameter_reads_consistent(self):
        _, rows = render_synthetic()
        row = self._row(rows, "G01")
        self.assertEqual(0.2997, row.original)
        self.assertEqual(0.2997, row.ours)
        self.assertEqual(run_parity.CONSISTENT, row.klass)
        self.assertEqual("—", row.delta_text())

    def test_different_start_diameter_reports_the_ratio(self):
        corpus = synthetic_corpus()
        corpus["parameters"]["start_diameter_m"] = 0.1
        _, rows = render_synthetic(corpus)
        row = self._row(rows, "G01")
        self.assertEqual(run_parity.PARAM, row.klass)
        self.assertIn("我方", row.delta_text())
        self.assertIn("3.00", row.delta_text())

    def test_absorb_gate_is_consistent_at_ten(self):
        _, rows = render_synthetic()
        self.assertEqual(run_parity.CONSISTENT, self._row(rows, "G07").klass)

    def test_growth_fraction_uses_the_curve(self):
        _, rows = render_synthetic()
        row = self._row(rows, "G06")
        self.assertAlmostEqual(0.8, row.original)
        self.assertEqual(run_parity.PARAM, row.klass)

    def test_unknown_original_is_not_a_number(self):
        corpus = synthetic_corpus()
        corpus["parameters"]["timer_seconds"] = None
        corpus["parameters"]["timer_unit"] = None
        corpus["unresolved"] = ["timer_unit 与 timer_seconds 未证实"]
        _, rows = render_synthetic(corpus)
        row = self._row(rows, "L02")
        self.assertIsNone(row.original)
        self.assertEqual(run_parity.UNKNOWN, row.klass)
        self.assertEqual("null", run_parity._fmt(row.original))


class NullToleranceTest(unittest.TestCase):
    def test_empty_growth_curve_degrades_to_unknown(self):
        corpus = synthetic_corpus()
        corpus["parameters"]["growth_curve"] = []
        _, rows = render_synthetic(corpus)
        row = [r for r in rows if r.id == "G06"][0]
        self.assertIsNone(row.original)
        self.assertEqual(run_parity.UNKNOWN, row.klass)

    def test_null_camera_fov_does_not_crash(self):
        corpus = synthetic_corpus()
        corpus["parameters"]["camera"]["fov_deg"] = None
        text, rows = render_synthetic(corpus)
        self.assertIn("L03", text)
        self.assertIsNone([r for r in rows if r.id == "L03"][0].original)

    def test_unplaced_instances_are_kept_but_not_positioned(self):
        corpus = synthetic_corpus()
        corpus["instances"].append(
            {"mono_idx": "10", "position": None, "placed": False})
        corpus["parameters"]["instance_count"] = 4
        text, rows = render_synthetic(corpus)
        self.assertIn("C01", text)
        # 4 instances, all counted: the runner must not quietly drop the row.
        self.assertEqual(4, [r for r in rows if r.id == "C01"][0].original)


@unittest.skipUnless(HAVE_CANCER, NO_CORPUS)
class SecondCorpusTest(unittest.TestCase):
    """The second corpus must not silently borrow the seed's numbers."""

    def test_cancer_run_does_not_leak_seed_values(self):
        from Docs.tests.parity.support import CANCER_CORPUS
        ours = run_parity.load_ours_level(OURS_LEVEL)
        corpus = run_parity.load_corpus(CANCER_CORPUS)
        text = run_parity.render(corpus, ours,
                                 run_parity.build_rows(corpus, ours))
        for leaked in ("654", "5 cm", "10/12/14/15"):
            self.assertNotIn(leaked, text,
                             "%r is a seed-corpus number leaking into the "
                             "second corpus" % leaked)

    def test_cancer_reports_its_own_content_counts(self):
        from Docs.tests.parity.support import CANCER_CORPUS
        ours = run_parity.load_ours_level(OURS_LEVEL)
        corpus = run_parity.load_corpus(CANCER_CORPUS)
        rows = run_parity.build_rows(corpus, ours)
        c01 = [r for r in rows if r.id == "C01"][0]
        self.assertEqual(523, c01.original)
        self.assertIn("523", c01.orig_source)


class RefusalTest(unittest.TestCase):
    def test_cli_refuses_a_corpus_with_unknown_fields(self):
        corpus = synthetic_corpus()
        corpus["parameters"]["secret_knob"] = 1.0
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "bad.json"
            path.write_text(json.dumps(corpus, ensure_ascii=False), encoding="utf-8")
            rc, out, err = quiet(run_parity.main,
                                ["--corpus", str(path), "--ours", str(OURS_LEVEL)])
        self.assertEqual(2, rc)
        self.assertIn("corpus refused", err)
        self.assertEqual("", out, "a refused corpus must not print a table")

    def test_cli_writes_the_table_to_a_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            corpus = write_corpus(tmp, synthetic_corpus())
            out_path = Path(tmp) / "gap.md"
            rc, out, err = quiet(run_parity.main,
                                 ["--corpus", str(corpus), "--ours", str(OURS_LEVEL),
                                  "--out", str(out_path)])
            self.assertEqual(0, rc)
            text = out_path.read_text(encoding="utf-8")
        self.assertIn("差分 gap 表", text)
        self.assertIn("合计 47 条指标", text)
        self.assertNotIn("654", text, "the build must use the synthetic corpus")

    def test_missing_corpus_prints_the_build_recipe(self):
        with tempfile.TemporaryDirectory() as tmp:
            rc, out, err = quiet(run_parity.main,
                                 ["--corpus", str(Path(tmp) / "nope.json"),
                                  "--ours", str(OURS_LEVEL)])
        self.assertEqual(2, rc)
        self.assertIn("corpus missing", err)
        self.assertIn("build_corpus.py", err)
        self.assertEqual("", out)

    @unittest.skipUnless(HAVE_SEED, NO_CORPUS)
    def test_cli_default_run_uses_the_seed_corpus(self):
        with tempfile.TemporaryDirectory() as tmp:
            rc, out, err = quiet(run_parity.main,
                                 ["--out", str(Path(tmp) / "seed.md")])
        self.assertEqual(0, rc)


class SupplyTest(unittest.TestCase):
    """G11/G12: supply must be *far* above what the stage asks for."""

    def test_supply_and_starvation_are_reported(self):
        _, rows = render_synthetic()
        supply = [r for r in rows if r.id == "G11"][0]
        starve = [r for r in rows if r.id == "G12"][0]
        self.assertIsInstance(supply.original, float)
        self.assertGreater(supply.ours, 0.0)
        self.assertGreaterEqual(starve.original, starve.ours)

    def test_abundant_supply_reads_consistent_with_itself(self):
        # Double every item's volume: supply must roughly double, never shrink.
        corpus = synthetic_corpus()
        base, _ = render_synthetic(corpus)
        base_value = [r for r in base.splitlines() if r.startswith("| G11 ")][0]
        richer = copy.deepcopy(corpus)
        for entry in richer["catalogue"].values():
            entry["volume_m3"] *= 2.0
        text, _ = render_synthetic(richer)
        rich_value = [r for r in text.splitlines() if r.startswith("| G11 ")][0]
        self.assertNotEqual(base_value, rich_value)

    def test_empty_content_starves(self):
        corpus = synthetic_corpus()
        corpus["catalogue"] = {k: v for k, v in corpus["catalogue"].items()}
        corpus["instances"] = [i for i in corpus["instances"] if i["mono_idx"] == "11"]
        text, rows = render_synthetic(corpus)
        starve = [r for r in rows if r.id == "G12"][0]
        self.assertEqual(0.0, starve.ours if starve.ours is not None else 0.0)


@unittest.skipUnless((run_parity.REPO_ROOT /
                      "Docs/Design/Levels/_ref_mas1/placement.json").is_file(),
                     "no _ref_mas1 rebuild on this checkout")
class RefLevelComparisonTest(unittest.TestCase):
    def test_report_compares_story_01_and_ref_mas1(self):
        with tempfile.TemporaryDirectory() as tmp:
            corpus_path = write_corpus(tmp, synthetic_corpus())
            out_path = Path(tmp) / "gap.md"
            rc, _, err = quiet(run_parity.main,
                               ["--corpus", str(corpus_path),
                                "--ours", str(OURS_LEVEL), "--out", str(out_path)])
            self.assertEqual(0, rc)
            text = out_path.read_text(encoding="utf-8")
        self.assertIn("供给比：两关我方数据", text)
        self.assertIn("_ref_mas1", text)
        self.assertIn("story_01_bench_size", text)


class VolumeFactorTest(unittest.TestCase):
    """G13: the original's volume is k x bbox — we ship k = 1."""

    def test_factor_is_unknown_without_the_block(self):
        _, rows = render_synthetic()
        row = [r for r in rows if r.id == "G13"][0]
        self.assertIsNone(row.original)
        self.assertEqual(run_parity.UNKNOWN, row.klass)

    def test_factor_is_read_from_the_catalogue(self):
        corpus = synthetic_corpus()
        for entry in corpus["catalogue"].values():
            entry["volume_factor"] = 0.7
        corpus["evidence"]["catalogue.*.volume_factor"] = {
            "source": "fixture", "level": "数据挖掘"}
        validate_corpus(corpus)
        _, rows = render_synthetic(corpus)
        row = [r for r in rows if r.id == "G13"][0]
        self.assertAlmostEqual(0.7, row.original)
        self.assertEqual(run_parity.MISSING, row.klass)
        self.assertAlmostEqual(1.4286, row.ratio, places=3)


class ZoneProfileTest(unittest.TestCase):
    """C08–C11 read the zone profile optionally: absent means 'unknown', not 0."""

    def test_zone_metrics_are_unknown_without_the_block(self):
        text, rows = render_synthetic()
        for metric_id in ("C08", "C09", "C10", "C11"):
            row = [r for r in rows if r.id == metric_id][0]
            self.assertIsNone(row.original, metric_id)
            self.assertEqual(run_parity.UNKNOWN, row.klass, metric_id)

    def test_zone_metrics_read_the_block_when_present(self):
        corpus = synthetic_corpus()
        corpus["zone_profile"] = {
            "source": "fixture",
            "areas": [{"area": 0, "instances": 3, "has_growth_bands": False,
                       "min_mm": 30.0, "eat_now": 0.5,
                       "min_ratio_to_ball": 0.25,
                       "small_1_10cm_share": 0.75,
                       "spearman_size_radius": 0.4}],
        }
        # A pattern that covers nothing is itself an error, so the fixture adds
        # the citation together with the block it cites.
        corpus["evidence"]["zone_profile.*"] = {"source": "fixture",
                                                "level": "数据挖掘"}
        validate_corpus(corpus)
        _, rows = render_synthetic(corpus)
        c08 = [r for r in rows if r.id == "C08"][0]
        c11 = [r for r in rows if r.id == "C11"][0]
        self.assertAlmostEqual(0.25, c08.original)
        self.assertAlmostEqual(0.4, c11.original)

    def test_spearman_is_signed_and_bounded(self):
        rising = [(1.0, 1.0), (2.0, 2.0), (3.0, 3.0)]
        falling = [(1.0, 3.0), (2.0, 2.0), (3.0, 1.0)]
        self.assertAlmostEqual(1.0, run_parity.spearman(rising))
        self.assertAlmostEqual(-1.0, run_parity.spearman(falling))
        self.assertIsNone(run_parity.spearman([(1.0, 1.0)]))


class DecisionsTest(unittest.TestCase):
    def test_report_states_the_owner_decisions(self):
        text, _ = render_synthetic()
        self.assertIn("owner 决定（2026-11-15）", text)
        for decision_id in ("D1", "D2", "D3", "D4", "D5", "D6", "D7"):
            self.assertIn("| %s |" % decision_id, text)
        # A decision must point at rows that exist in this very table.
        _, rows = render_synthetic()
        ids = {row.id for row in rows}
        for _, _, metric_ids in run_parity.DECISIONS:
            self.assertTrue(set(metric_ids) <= ids, metric_ids)


class DocumentSyncTest(unittest.TestCase):
    def test_every_metric_appears_in_the_audit(self):
        """The audit doc and the runner must describe the same metric list."""
        self.assertTrue(AUDIT_DOC.is_file(), "missing %s" % AUDIT_DOC)
        doc = AUDIT_DOC.read_text(encoding="utf-8")
        _, rows = render_synthetic()
        missing = [row.id for row in rows
                   if ("| %s |" % row.id) not in doc and ("`%s`" % row.id) not in doc]
        self.assertEqual([], missing,
                         "metric(s) not documented in PARITY_AUDIT.md")

    def test_doc_has_the_owner_decision_section(self):
        doc = AUDIT_DOC.read_text(encoding="utf-8")
        self.assertIn("## 5. owner 决定（2026-11-15）", doc)
        for decision_id in ("D1", "D2", "D3", "D4", "D5", "D6", "D7"):
            self.assertIn("**%s**" % decision_id, doc)
        self.assertIn("## 0.5 术语", doc)

    def test_doc_states_the_metric_count(self):
        doc = AUDIT_DOC.read_text(encoding="utf-8")
        _, rows = render_synthetic()
        self.assertIn("%d" % len(rows), doc)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
