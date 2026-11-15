"""Schema contract for one parity corpus.

What is pinned here:

* the seed corpus (real Make a Star 1 data) validates and keeps its shape;
* unknown fields are **refused** at every level, including inside a catalogue
  entry — a typo must never become "a metric we forgot to fill";
* ``null`` is legal only where the schema says so, and a null in the fields that
  carry the open questions must be named in ``unresolved``;
* every datum needs an evidence pattern, and every pattern needs a datum (a
  stale pattern is how a renamed field hides);
* a bare ``*`` is not a citation.
"""

import copy
import unittest

from Docs.Design.Parity.parity_harness.corpus_schema import (
    SCHEMA_VERSION,
    Corpus,
    CorpusError,
    check_evidence,
    check_unresolved,
    describe,
    load_corpus,
    validate_corpus,
)
from Docs.tests.parity.support import (
    CANCER_CORPUS,
    SEED_CORPUS,
    synthetic_corpus,
)

HAVE_SEED = SEED_CORPUS.is_file()
HAVE_CANCER = CANCER_CORPUS.is_file()
NO_CORPUS = "corpus not built: this file is a local data-mine export, not committed " \
            "(run parity_harness/build_corpus.py)"


@unittest.skipUnless(HAVE_SEED, NO_CORPUS)
class SeedCorpusTest(unittest.TestCase):
    def test_seed_is_valid_and_keeps_its_shape(self):
        corpus = load_corpus(SEED_CORPUS)
        self.assertEqual(SCHEMA_VERSION, corpus.data["schema"])
        self.assertEqual("mas1", corpus.stage_id)
        self.assertEqual("Make a Star 1", corpus.stage_name)
        self.assertEqual(654, len(corpus.instances))
        self.assertEqual(107, len(corpus.catalogue))
        self.assertAlmostEqual(0.05, corpus.start_diameter_m, places=9)
        self.assertAlmostEqual(0.10, corpus.clear_diameter_m, places=9)
        self.assertAlmostEqual(0.30, corpus.max_diameter_m, places=9)
        self.assertTrue(corpus.single_area)

    def test_seed_has_real_positions(self):
        corpus = load_corpus(SEED_CORPUS)
        placed = corpus.instances_with_position()
        self.assertEqual(652, len(placed))
        # The item cloud is a house-sized room, area-local: a sanity box, not a
        # coordinate claim (DATAMODEL_NOTES.md §3).
        xs = [i["position"][0] for i in placed]
        ys = [i["position"][1] for i in placed]
        zs = [i["position"][2] for i in placed]
        self.assertLess(max(xs) - min(xs), 6.0)
        self.assertLess(max(ys) - min(ys), 4.0)
        self.assertLess(max(zs) - min(zs), 8.0)

    @unittest.skipUnless(HAVE_CANCER, NO_CORPUS)
    def test_second_corpus_is_cancer(self):
        corpus = load_corpus(CANCER_CORPUS)
        self.assertEqual("cancer", corpus.stage_id)
        self.assertEqual(523, len(corpus.instances))
        self.assertEqual(148, len(corpus.catalogue))
        self.assertAlmostEqual(0.222, corpus.start_diameter_m, places=9)
        # No growth curve in any public source for this stage: the field is
        # empty rather than invented, and the metric must degrade to 原作未知.
        self.assertEqual([], corpus.params["growth_curve"])
        self.assertIsNone(corpus.growth_fraction(corpus.start_diameter_m))


class SyntheticCorpusCoversTheSameContract(unittest.TestCase):
    """Everything the real corpus proved, provable without the real corpus.

    The seed files are uncommitted local exports, so a clean checkout must still
    exercise the whole schema: shape, nulls, positions, strict refusal.
    """

    def test_shape_and_values(self):
        corpus = Corpus(synthetic_corpus())
        self.assertEqual(3, len(corpus.instances))
        self.assertEqual(2, len(corpus.catalogue))
        self.assertTrue(corpus.single_area)
        self.assertAlmostEqual(0.2997, corpus.start_diameter_m, places=9)
        self.assertEqual(2, len(corpus.params["rating_diameters_m"]))

    def test_positions_are_read_only_when_present(self):
        corpus = Corpus(synthetic_corpus())
        self.assertEqual(3, len(corpus.instances_with_position()))
        corpus.data["instances"][0]["position"] = None
        self.assertEqual(2, len(corpus.instances_with_position()))

    def test_piecewise_curve_tolerates_two_knots(self):
        corpus = Corpus(synthetic_corpus())
        self.assertAlmostEqual(0.8, corpus.growth_fraction(0.30))
        self.assertAlmostEqual(0.6, corpus.growth_fraction(0.45))
        self.assertAlmostEqual(0.4, corpus.growth_fraction(9.0))


class StrictnessTest(unittest.TestCase):
    def setUp(self):
        self.corpus = synthetic_corpus()

    def test_valid_synthetic_passes(self):
        validate_corpus(copy.deepcopy(self.corpus))
        self.assertIn("corpus.parameters.start_diameter_m", describe())

    def test_unknown_top_level_field_is_refused(self):
        broken = copy.deepcopy(self.corpus)
        broken["extra_metric"] = 1
        with self.assertRaisesRegex(CorpusError, "unknown field"):
            validate_corpus(broken)

    def test_unknown_nested_field_is_refused(self):
        broken = copy.deepcopy(self.corpus)
        broken["parameters"]["ball_radius_m"] = 0.1
        with self.assertRaisesRegex(CorpusError, "parameters: unknown field"):
            validate_corpus(broken)

    def test_unknown_catalogue_field_is_refused(self):
        broken = copy.deepcopy(self.corpus)
        broken["catalogue"]["10"]["rotation_y_deg"] = 30.0
        with self.assertRaisesRegex(CorpusError, "catalogue.10: unknown field"):
            validate_corpus(broken)

    def test_unknown_instance_field_is_refused(self):
        broken = copy.deepcopy(self.corpus)
        broken["instances"][0]["scale"] = 2.0
        with self.assertRaisesRegex(CorpusError, "instances\\[\\]: unknown field"):
            validate_corpus(broken)

    def test_missing_required_field_is_refused(self):
        broken = copy.deepcopy(self.corpus)
        del broken["parameters"]["start_diameter_m"]
        with self.assertRaisesRegex(CorpusError, "missing required field"):
            validate_corpus(broken)

    def test_wrong_type_is_refused(self):
        broken = copy.deepcopy(self.corpus)
        broken["parameters"]["start_diameter_m"] = "0.05"
        with self.assertRaisesRegex(CorpusError, "expected a number"):
            validate_corpus(broken)

    def test_bool_is_not_a_number(self):
        broken = copy.deepcopy(self.corpus)
        broken["parameters"]["gate_volume_ratio"] = True
        with self.assertRaisesRegex(CorpusError, "expected a number"):
            validate_corpus(broken)

    def test_negative_value_is_refused(self):
        broken = copy.deepcopy(self.corpus)
        broken["catalogue"]["10"]["volume_m3"] = -1.0
        with self.assertRaisesRegex(CorpusError, "must be > 0"):
            validate_corpus(broken)

    def test_bad_schema_tag_is_refused(self):
        broken = copy.deepcopy(self.corpus)
        broken["schema"] = "parity_corpus/999"
        with self.assertRaisesRegex(CorpusError, "not one of"):
            validate_corpus(broken)

    def test_annotation_keys_are_allowed(self):
        annotated = copy.deepcopy(self.corpus)
        annotated["_note"] = "hand-written"
        annotated["parameters"]["_todo"] = "fill the timer unit later"
        annotated["catalogue"]["10"]["_source"] = "fixture"
        validate_corpus(annotated)

    def test_annotation_must_be_a_string(self):
        broken = copy.deepcopy(self.corpus)
        broken["_note"] = 3
        with self.assertRaisesRegex(CorpusError, "annotations are strings"):
            validate_corpus(broken)


class NullAndTodoTest(unittest.TestCase):
    def test_null_position_is_allowed(self):
        corpus = synthetic_corpus()
        corpus["instances"][0]["position"] = None
        corpus["instances"][0]["placed"] = False
        validate_corpus(corpus)

    def test_null_where_not_nullable_is_refused(self):
        broken = synthetic_corpus()
        broken["parameters"]["max_diameter_m"] = None
        with self.assertRaisesRegex(CorpusError, "null is not allowed"):
            validate_corpus(broken)

    def test_null_timer_unit_must_be_explained(self):
        corpus = synthetic_corpus()
        corpus["parameters"]["timer_unit"] = None
        corpus["parameters"]["timer_seconds"] = None
        with self.assertRaisesRegex(CorpusError, "unresolved"):
            validate_corpus(corpus)
        corpus["unresolved"] = [
            "parameters.timer_unit=null：单位未证实，因此 timer_seconds 也留空"]
        validate_corpus(corpus)

    def test_check_unresolved_is_a_noop_when_absent(self):
        corpus = synthetic_corpus()
        del corpus["parameters"]["timer_unit"]
        check_unresolved(corpus)


class EvidenceTest(unittest.TestCase):
    def test_uncovered_field_is_refused(self):
        corpus = synthetic_corpus()
        del corpus["evidence"]["parameters.*"]
        corpus["evidence"]["parameters.camera.*"] = {
            "source": "fixture", "level": "数据挖掘"}
        with self.assertRaisesRegex(CorpusError, "no evidence entry covers"):
            validate_corpus(corpus)

    def test_dead_pattern_is_refused(self):
        corpus = synthetic_corpus()
        corpus["evidence"]["instances[].rotation"] = {
            "source": "fixture", "level": "数据挖掘"}
        with self.assertRaisesRegex(CorpusError, "matches no field"):
            validate_corpus(corpus)

    def test_bare_wildcard_is_not_a_citation(self):
        corpus = synthetic_corpus()
        corpus["evidence"] = {"*": {"source": "fixture", "level": "数据挖掘"}}
        with self.assertRaisesRegex(CorpusError, "bare"):
            validate_corpus(corpus)

    def test_evidence_level_is_a_closed_vocabulary(self):
        corpus = synthetic_corpus()
        corpus["evidence"]["stage.*"]["level"] = "我觉得"
        with self.assertRaisesRegex(CorpusError, "not one of"):
            validate_corpus(corpus)

    def test_field_outside_every_pattern_is_refused(self):
        corpus = synthetic_corpus()
        corpus["evidence"] = {"stage.*": {"source": "fixture", "level": "数据挖掘"}}
        with self.assertRaisesRegex(CorpusError, "no evidence entry covers"):
            check_evidence(corpus)


class LoaderTest(unittest.TestCase):
    def test_load_returns_a_corpus_view(self):
        corpus = Corpus(synthetic_corpus())
        self.assertEqual(3, len(corpus.instances))
        self.assertEqual(2, len(corpus.catalogue))
        self.assertAlmostEqual(0.0000141, corpus.volume_m3(corpus.instances[0]))
        self.assertAlmostEqual(0.030, corpus.req_pickup_m(corpus.instances[0]))

    def test_piecewise_growth_curve(self):
        corpus = Corpus(synthetic_corpus())
        self.assertAlmostEqual(0.8, corpus.growth_fraction(0.20))
        self.assertAlmostEqual(0.6, corpus.growth_fraction(0.45))
        self.assertAlmostEqual(0.4, corpus.growth_fraction(5.0))

    def test_bad_json_is_named(self):
        import tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "broken.json"
            path.write_text("{not json", encoding="utf-8")
            with self.assertRaisesRegex(CorpusError, "not valid JSON"):
                load_corpus(path)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
