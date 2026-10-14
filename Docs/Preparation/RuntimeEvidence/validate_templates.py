#!/usr/bin/env python3
"""Validate P18 template shape and NOT_RUN placeholders using Python stdlib only."""
import csv
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parent
WARMUP_POLICY_TEXT = {
    "frame_time": "Exclude only a predeclared warm-up interval; report its duration and excluded count; never hide it",
    "network_queue_depth": "Predeclare warm-up and report excluded samples; do not pool clients",
    "serialization_bytes": "Predeclare warm-up and report event count",
    "network_round_trip": "Predeclare warm-up and document clock/probe setup",
    "memory_used": "Record startup/warm-up window separately",
    "transaction_queue_age": "Predeclare warm-up and phase",
    "pool_object_count": "Capture initial baseline and every round boundary",
    "frame_sample_count": "Record warm-up excluded count separately",
}


def _validate_header(name, header):
    normalized_header = [column.strip() for column in header]
    if not header or any(not column for column in normalized_header) or len(normalized_header) != len(set(normalized_header)):
        raise AssertionError(f"{name}: header contains an empty or duplicate column name")


def read_csv(name):
    path = ROOT / name
    with path.open("r", encoding="utf-8", newline="") as stream:
        raw_rows = list(csv.reader(stream))
    if not raw_rows:
        raise AssertionError(f"{name}: empty CSV")
    header = raw_rows[0]
    _validate_header(name, header)
    width = len(header)
    if any(len(row) != width for row in raw_rows):
        raise AssertionError(f"{name}: inconsistent CSV row width")
    # Construct DictReader only after validating names: duplicate keys would otherwise be silently overwritten.
    with path.open("r", encoding="utf-8", newline="") as stream:
        rows = list(csv.DictReader(stream))
    if any(row.get("schemaVersion") != "1" for row in rows):
        raise AssertionError(f"{name}: schemaVersion must be 1")
    return rows, width


def _reject_json_constant(token):
    raise ValueError(f"non-standard/non-finite JSON numeric constant: {token}")


def _assert_finite_tree(value, path="$" ):
    if isinstance(value, float) and not math.isfinite(value):
        raise AssertionError(f"{path}: JSON numeric value must be finite")
    if isinstance(value, dict):
        for key, child in value.items():
            _assert_finite_tree(child, f"{path}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            _assert_finite_tree(child, f"{path}[{index}]")


def parse_environment(text):
    document = json.loads(text, parse_constant=_reject_json_constant)
    _assert_finite_tree(document)
    return document


def _require_null(mapping, keys, label):
    for key in keys:
        assert mapping[key] is None, f"{label}.{key} must remain null before a run"


def _require_unknown(mapping, keys, label):
    for key in keys:
        assert mapping[key] == "unknown", f"{label}.{key} must remain unknown before a run"


def validate_environment(env):
    assert env["schemaVersion"] == 1 and env["templateStatus"] == "TEMPLATE_NOT_RUN"
    _require_null(env, ("runId", "testDateTime"), "environment")
    assert env["timezone"] == "unknown"

    _require_null(env["unity"], ("editorVersion", "buildTarget", "projectBuildId"), "unity")
    _require_null(env["vrchat"], ("worldBuildId", "worldSdkPackageVersion", "baseSdkPackageVersion", "udonSharpPackageVersion"), "vrchat")
    assert env["vrchat"]["clientBuilds"] == [], "vrchat.clientBuilds must remain empty before a run"

    network = env["network"]
    _require_null(network, ("transportPathOrTestHarness", "configuredLatencyMs", "configuredJitterMs", "configuredLossPercent", "measuredConditionsMethod"), "network")
    _require_unknown(network, ("region", "accessType"), "network")

    assert len(env["clients"]) == 4, "environment must scaffold 2-4 client labels"
    for index, client in enumerate(env["clients"]):
        label = f"clients[{index}]"
        _require_null(client, ("roleDescription",), label)
        _require_null(client["hardware"], ("os", "cpu", "gpu", "memoryGiB", "displayModel", "displayResolution", "refreshRateHz", "headsetModel", "headsetRefreshRateHz"), f"{label}.hardware")
        assert client["avatarCondition"] == "unknown", f"{label}.avatarCondition must remain unknown"
        _require_unknown(client["clock"], ("source", "timebase"), f"{label}.clock")
        _require_null(client["clock"], ("offsetToReferenceMs", "uncertaintyMs"), f"{label}.clock")
        roles = client["observedRoles"]
        _require_unknown(roles, ("matchAuthorityOwnerLabel", "playerObjectOwnerLabel", "vrchatMasterLabel", "instanceOwnerLabel"), f"{label}.observedRoles")
        _require_null(roles, ("observedAt",), f"{label}.observedRoles")

    _require_null(env["authorityAndSnapshot"], ("authorityObjectIdentityPseudonym", "initialEpoch", "snapshotRevision", "snapshotCompletenessEvidence", "initialCommitSequence"), "authorityAndSnapshot")
    _require_null(env["collection"], ("frameSampleMethod", "networkSampleMethod", "memoryMetricAndMethod", "samplingIntervalMsPerClient", "warmupPolicy", "outlierPolicy", "rawDataEvidencePath"), "collection")
    assert env["privacyReview"]["reviewedByUserBeforeExternalSharing"] is None
    _require_null(env, ("notes",), "environment")


def validate_cases(cases):
    assert len(cases) >= 20, f"expected >=20 cases, got {len(cases)}"
    assert len({row["case_id"] for row in cases}) == len(cases), "case_id values must be unique"
    assert all(row["status"] == "NOT_RUN" for row in cases), "all template cases must be NOT_RUN"
    assert all(row["observed"] == "" and row["evidence"] == "" for row in cases), "observed/evidence must be blank"


def validate_metrics(metrics):
    assert all(row["status"] == "NOT_RUN" for row in metrics), "all template metrics must be NOT_RUN"
    observation_fields = ("sampling_interval_ms_per_client", "sample_count", "raw_data_evidence_path", "p50", "p95", "p99")
    for row in metrics:
        for field in observation_fields:
            assert row[field] == "", f"{row['metric_id']}.{field} must be blank before a run"
        # warmup_policy is static methodology guidance in this template, not a measured warm-up value.
        # Keep the approved policy text and reject replacing it with observed duration/count data.
        expected_policy = WARMUP_POLICY_TEXT.get(row["metric_id"])
        assert expected_policy is not None, f"unexpected metric_id: {row['metric_id']}"
        assert row["warmup_policy"] == expected_policy, f"{row['metric_id']}.warmup_policy must remain the template guidance before a run"


def self_check_rejects_prefilled_data(env, metrics):
    """Minimal negative probes: reject malformed headers, filled observations, and nonfinite JSON."""
    import copy

    for bad_header in ([], ["schemaVersion", ""], ["case_id", "case_id"], ["case_id", " case_id "]):
        try:
            _validate_header("self-check.csv", bad_header)
        except AssertionError as exc:
            assert "empty or duplicate column name" in str(exc)
        else:
            raise AssertionError(f"self-check failed to reject CSV header {bad_header!r}")

    for bad_json in ("{\"x\":NaN}", "{\"x\":Infinity}", "{\"x\":-Infinity}"):
        try:
            parse_environment(bad_json)
        except ValueError:
            pass
        else:
            raise AssertionError(f"self-check failed to reject {bad_json}")

    try:
        parse_environment("{\"nested\":{\"value\":1e999}}")
    except AssertionError:
        pass
    else:
        raise AssertionError("self-check failed to reject an exponent that overflows to infinity")

    for bad_tree in ({"outer": [float("nan")]}, {"outer": {"inner": float("inf")}}):
        try:
            _assert_finite_tree(bad_tree)
        except AssertionError:
            pass
        else:
            raise AssertionError("self-check failed to reject nested non-finite number")

    mutations = (
        ("run identity", lambda doc: doc.__setitem__("runId", "run-1")),
        ("hardware", lambda doc: doc["clients"][0]["hardware"].__setitem__("cpu", "filled")),

        ("build", lambda doc: doc["unity"].__setitem__("editorVersion", "filled")),
        ("network", lambda doc: doc["network"].__setitem__("configuredLatencyMs", 20)),
        ("authority", lambda doc: doc["authorityAndSnapshot"].__setitem__("initialEpoch", 1)),
        ("collection", lambda doc: doc["collection"].__setitem__("samplingIntervalMsPerClient", 16)),
    )
    for label, mutate in mutations:
        candidate = copy.deepcopy(env)
        mutate(candidate)
        try:
            validate_environment(candidate)
        except AssertionError:
            pass
        else:
            raise AssertionError(f"self-check failed to reject populated environment {label}")

    metric_mutations = (
        ("sampling interval", "sampling_interval_ms_per_client", "16"),
        ("raw evidence", "raw_data_evidence_path", "raw/run.csv"),
        ("sample count", "sample_count", "1000"),
        ("warm-up observation", "warmup_policy", "Excluded 2 minutes / 120 samples"),
    )
    for label, field, value in metric_mutations:
        candidate = copy.deepcopy(metrics)
        candidate[0][field] = value
        try:
            validate_metrics(candidate)
        except AssertionError:
            pass
        else:
            raise AssertionError(f"self-check failed to reject populated metric {label}")


def main():
    cases, case_columns = read_csv("cases.csv")
    validate_cases(cases)

    metrics, metric_columns = read_csv("metrics.csv")
    validate_metrics(metrics)

    env = parse_environment((ROOT / "session" / "environment.json").read_text(encoding="utf-8"))
    validate_environment(env)
    self_check_rejects_prefilled_data(env, metrics)

    print(f"cases.csv: {len(cases)} rows, {case_columns} columns; all NOT_RUN and observations empty")
    print(f"metrics.csv: {len(metrics)} rows, {metric_columns} columns; observations blank and warm-up guidance unchanged")
    print("session/environment.json: schemaVersion=1, four client slots, unrun fields null/unknown")
    print("negative self-checks: malformed headers, nonfinite JSON, and populated NOT_RUN fields rejected")
    print("P18 template integrity: PASS (host-only structure check; no runtime evidence)")


if __name__ == "__main__":
    main()
