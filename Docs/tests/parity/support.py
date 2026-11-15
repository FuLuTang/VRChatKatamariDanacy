"""Shared fixtures for the parity-harness tests.

The synthetic corpus is deliberately tiny and hand-written: it is the *contract*
example (one stage, two object types, three instances, real positions), so a
schema change that breaks it breaks a test with a readable diff instead of a
140 KB seed file.
"""

import copy
import json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
SEED_CORPUS = REPO_ROOT / "Docs/Design/Parity/parity_harness/corpus_star1.json"
CANCER_CORPUS = REPO_ROOT / "Docs/Design/Parity/parity_harness/corpus_cancer.json"
OURS_LEVEL = REPO_ROOT / "Docs/Design/Levels/story_01_bench_size/placement.json"
AUDIT_DOC = REPO_ROOT / "Docs/Design/Parity/PARITY_AUDIT.md"

EVIDENCE = {
    "stage.*": {"source": "test fixture", "level": "数据挖掘"},
    "units.*": {"source": "test fixture", "level": "数据挖掘"},
    "timebase.*": {"source": "test fixture", "level": "源码"},
    "parameters.*": {"source": "test fixture", "level": "数据挖掘"},
    "catalogue.*": {"source": "test fixture", "level": "数据挖掘"},
    "instances[].*": {"source": "test fixture", "level": "数据挖掘"},
}


def synthetic_corpus():
    """A valid corpus whose numbers are chosen to hit known verdicts.

    ``start_diameter_m`` equals our own start diameter (0.2997 m) on purpose:
    ``G01`` must then read 一致, which is the cheapest proof that the runner
    really compares against our level instead of echoing the corpus.
    """
    return {
        "schema": "parity_corpus/1",
        "stage": {
            "id": "fixture",
            "name": "Fixture Stage",
            "game": "fixture",
            "single_area": True,
            "source": "hand-written test fixture",
        },
        "units": {"length": "m", "diameter": "m", "volume": "m3", "time": "s"},
        "timebase": {
            "fixed_step_hz": 30.0,
            "fixed_step_s": 1.0 / 30.0,
            "source": "fixture",
        },
        "parameters": {
            "start_diameter_m": 0.2997,
            "clear_diameter_m": 0.2997,
            "max_diameter_m": 1.2,
            "area_count": 1,
            "instance_count": 3,
            "type_count": 2,
            "timer_raw": 4,
            "timer_unit": "min",
            "timer_seconds": 240.0,
            "clear_type": "N",
            "gate_volume_ratio": 10.0,
            "gate_floor_m": 0.001,
            "hud_diameter_offset_m": 0.0002,
            "growth_curve": [
                {"diameter_m": 0.30, "fraction": 0.8},
                {"diameter_m": 0.60, "fraction": 0.4},
            ],
            "rating_diameters_m": [0.30, 0.40],
            "zone_gates_m": [0.35, 0.50],
            "camera": {
                "fov_deg": 37.0,
                "near_m": 0.01,
                "far_m": 700.0,
                "focus_distance_m": 0.12,
                "aperture_f": 6.5,
            },
            "spawn": {
                "position": [0.0, 0.0, 0.0],
                "rot_y_rad": 0.0,
                "frame_note": "fixture",
            },
        },
        "catalogue": {
            "10": {
                "name": "Small Thing",
                "internal_name": "SMALL01",
                "req_pickup_mm": 30.0,
                "volume_m3": 0.0000141,
                "own_diam_mm": 30.0,
                "category": "0",
                "unit_type": "1",
                "kind": None,
                "path_speed_index": "2",
                "vault_points": 0,
                "hit_lod": 1,
                "penalty": 0.8,
            },
            "11": {
                "name": "Big Thing",
                "internal_name": "BIG01",
                "req_pickup_mm": 900.0,
                "volume_m3": 0.381,
                "own_diam_mm": 900.0,
                "category": "3",
                "unit_type": "1",
                "kind": None,
                "path_speed_index": "0",
                "vault_points": 2,
                "hit_lod": 1,
                "penalty": 0.5,
            },
        },
        "instances": [
            {"mono_idx": "10", "position": [1.0, 0.1, 2.0], "placed": True},
            {"mono_idx": "10", "position": [1.5, 0.1, 2.5], "placed": True},
            {"mono_idx": "11", "position": [0.0, 0.45, 0.0], "placed": True},
        ],
        "evidence": copy.deepcopy(EVIDENCE),
        "unresolved": [],
    }


def write_corpus(tmpdir, corpus):
    path = Path(tmpdir) / "corpus_fixture.json"
    path.write_text(json.dumps(corpus, ensure_ascii=False, indent=1),
                    encoding="utf-8")
    return path


def synthetic_ours_level():
    """A tiny our-side level: two props, one absorbable at the start diameter."""
    return {
        "schema": "level_placement/1",
        "level": {
            "id": "fixture_level",
            "ballDiameterStart": 0.2997,
            "ballDiameterMax": 2.2,
            "groundY": 0.0,
            "zones": [{"id": "a"}, {"id": "b"}],
        },
        "defs": [{"id": "pebble", "kind": "nature", "valueUnits": 1},
                 {"id": "crate", "kind": "structure", "valueUnits": 64}],
        "props": [
            {"itemId": "pebble_01", "def": "pebble", "kind": "nature",
             "zone": "a", "position": [0.0, 0.02, 0.0],
             "rotationQuat": [0.0, 0.0, 0.0, 1.0],
             "aabb": [0.06, 0.043999, 0.0319], "absorbable": True,
             "valueUnits": 1},
            {"itemId": "crate_01", "def": "crate", "kind": "structure",
             "zone": "b", "position": [3.0, 0.2, 0.0],
             "rotationQuat": [0.0, 0.0, 0.0, 1.0],
             "aabb": [0.6, 0.4, 0.4], "absorbable": True,
             "valueUnits": 64},
        ],
        "structures": [],
    }
