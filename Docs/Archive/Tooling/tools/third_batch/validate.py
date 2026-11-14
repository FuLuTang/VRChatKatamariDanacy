"""Focused host-side integrity checks for P09–P18 machine-readable artifacts.

This validates selected IDs, links, proposal boundaries, and unrun evidence fields.
It is not a JSON Schema engine, runtime test, Unity gate, or rights approval.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path, PurePosixPath
import re
import sys
from typing import Any, Iterable


EXPECTED_PACKAGES = {f"P{i:02d}" for i in range(9, 19)}
PROTOCOL_CASES = {
    "same-spawn-two-claimants", "old-spawn-generation", "partial-magnet-success",
    "duplicate-bank-business-id", "ko-keeps-banked-clears-unbanked",
    "ko-reward-exceeds-unbanked", "bank-vs-ko-same-fixture-tick",
    "cutoff-receive-time-boundary", "authority-epoch-old-packet",
    "snapshot-complete-migrate", "snapshot-missing-or-mixed-part-cancel",
    "request-sequence-reused-after-epoch-migration", "sender-metadata-fixture-mismatch",
}
MATH_CASES = {
    "V03-radius-diameter-distinct", "V19-support-direction-normalized",
    "V20-negative-scale-rejected", "V21-zero-scale-rejected", "V22-nan-rejected",
    "V23-infinity-rejected", "V24-zero-direction-rejected",
    "V25-attachment-root-local-pose-contract",
}


def _reject_constant(value: str) -> None:
    raise ValueError(f"non-standard JSON constant {value}")


def _check_finite(value: Any, path: str, errors: list[str]) -> None:
    if isinstance(value, bool) or value is None or isinstance(value, (str, int)):
        return
    if isinstance(value, float):
        if not math.isfinite(value):
            errors.append(f"{path}: non-finite numeric value")
        return
    if isinstance(value, list):
        for index, item in enumerate(value):
            _check_finite(item, f"{path}[{index}]", errors)
        return
    if isinstance(value, dict):
        for key, item in value.items():
            _check_finite(item, f"{path}.{key}", errors)


def parse_json_text(text: str, label: str, errors: list[str]) -> Any | None:
    try:
        value = json.loads(text, parse_constant=_reject_constant)
    except (json.JSONDecodeError, ValueError) as exc:
        errors.append(f"{label}: invalid JSON ({exc})")
        return None
    _check_finite(value, label, errors)
    return value


def load_json(root: Path, relative: str, errors: list[str]) -> Any | None:
    path = root / relative
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        errors.append(f"{relative}: cannot read ({exc})")
        return None
    return parse_json_text(text, relative, errors)


def load_csv(root: Path, relative: str, errors: list[str]) -> tuple[list[dict[str, str]], list[str]]:
    path = root / relative
    try:
        with path.open("r", encoding="utf-8", newline="") as stream:
            records = list(csv.reader(stream))
    except (OSError, csv.Error, UnicodeError) as exc:
        errors.append(f"{relative}: cannot read CSV ({exc})")
        return [], []
    if not records:
        errors.append(f"{relative}: CSV is empty")
        return [], []
    header = records[0]
    if any(not name for name in header) or len(header) != len(set(header)):
        errors.append(f"{relative}: header contains an empty or duplicate column name")
    if any(len(row) != len(header) for row in records[1:]):
        errors.append(f"{relative}: inconsistent row width")
    rows = [dict(zip(header, row)) for row in records[1:] if len(row) == len(header)]
    return rows, header


def check_unique(rows: Iterable[dict[str, Any]], key: str, label: str, errors: list[str]) -> set[str]:
    seen: set[str] = set()
    duplicates: set[str] = set()
    for row in rows:
        value = row.get(key)
        if not isinstance(value, str) or not value:
            errors.append(f"{label}: missing {key}")
            continue
        if value in seen:
            duplicates.add(value)
        seen.add(value)
    for value in sorted(duplicates):
        errors.append(f"{label}: duplicate {key} {value!r}")
    return seen


def check_dag(graph: dict[str, Iterable[str]], label: str, errors: list[str]) -> None:
    nodes = set(graph)
    normalized: dict[str, list[str]] = {}
    for node, dependencies in graph.items():
        deps = list(dependencies)
        normalized[node] = deps
        if len(deps) != len(set(deps)):
            errors.append(f"{label}: {node} repeats a dependency")
        for dependency in deps:
            if dependency not in nodes:
                errors.append(f"{label}: {node} links to unknown node {dependency}")
            if dependency == node:
                errors.append(f"{label}: {node} depends on itself")

    state: dict[str, int] = {}

    def visit(node: str, stack: list[str]) -> None:
        marker = state.get(node, 0)
        if marker == 2:
            return
        if marker == 1:
            cycle = stack[stack.index(node):] + [node] if node in stack else [node]
            errors.append(f"{label}: dependency cycle {' -> '.join(cycle)}")
            return
        state[node] = 1
        stack.append(node)
        for dependency in normalized.get(node, []):
            if dependency in nodes:
                visit(dependency, stack)
        stack.pop()
        state[node] = 2

    for node in sorted(nodes):
        visit(node, [])


def _required_columns(header: list[str], required: Iterable[str], label: str, errors: list[str]) -> None:
    missing = sorted(set(required) - set(header))
    if missing:
        errors.append(f"{label}: missing columns {', '.join(missing)}")


def _check_schema_version(rows: list[dict[str, str]], label: str, errors: list[str]) -> None:
    for index, row in enumerate(rows, start=2):
        if row.get("schemaVersion") != "1":
            errors.append(f"{label}:{index}: schemaVersion must be 1")


def _check_numeric_fields(rows: list[dict[str, str]], fields: Iterable[str], label: str, errors: list[str]) -> None:
    for index, row in enumerate(rows, start=2):
        for field in fields:
            value = row.get(field, "").strip()
            if not value:
                continue
            try:
                number = float(value)
            except ValueError:
                errors.append(f"{label}:{index}: {field} must be numeric or blank")
                continue
            if not math.isfinite(number):
                errors.append(f"{label}:{index}: {field} must be finite")


def _check_status_and_blanks(rows: list[dict[str, str]], status_field: str, blank_fields: Iterable[str], label: str, errors: list[str]) -> None:
    for index, row in enumerate(rows, start=2):
        if row.get(status_field) != "NOT_RUN":
            errors.append(f"{label}:{index}: {status_field} must remain NOT_RUN")
        for field in blank_fields:
            if row.get(field, "").strip():
                errors.append(f"{label}:{index}: unrun field {field} must be blank")


def _check_package_config(root: Path, errors: list[str]) -> None:
    data = load_json(root, "Docs/config/offline_preparation.json", errors)
    if not isinstance(data, dict):
        return
    packages = data.get("workPackages")
    if not isinstance(packages, list):
        errors.append("Docs/config/offline_preparation.json: workPackages must be a list")
        return
    ids = [item.get("id") for item in packages if isinstance(item, dict)]
    if len(ids) != len(packages) or len(ids) != len(set(ids)):
        errors.append("Docs/config/offline_preparation.json: package IDs must exist and be unique")
    by_id = {item["id"]: item for item in packages if isinstance(item, dict) and isinstance(item.get("id"), str)}
    for required_id in sorted(EXPECTED_PACKAGES):
        if required_id not in by_id:
            errors.append(f"offline work-package DAG: missing {required_id}")
    graph: dict[str, list[str]] = {}
    allowed_statuses = {"TODO", "READY", "RUNNING", "REVIEW", "DONE", "BLOCKED"}
    for package_id, item in by_id.items():
        deps = item.get("dependencies", [])
        if not isinstance(deps, list) or not all(isinstance(dep, str) for dep in deps):
            errors.append(f"offline work-package DAG: {package_id} dependencies must be string IDs")
            deps = []
        graph[package_id] = deps
        if item.get("status") not in allowed_statuses:
            errors.append(f"offline work-package DAG: {package_id} has unknown status")
    check_dag(graph, "offline work-package DAG", errors)


def _check_asset_coverage(root: Path, errors: list[str]) -> set[str]:
    coverage_path = "Docs/Preparation/AssetCoverage/coverage.csv"
    rows, header = load_csv(root, coverage_path, errors)
    _required_columns(header, ("candidate_id", "file_metadata_status", "visual_review_status", "concept_fit_status", "per_file_rights_status"), coverage_path, errors)
    candidate_ids = check_unique(rows, "candidate_id", coverage_path, errors)
    if len(rows) != 36 or candidate_ids != {f"PIC-{i:02d}" for i in range(1, 37)}:
        errors.append(f"{coverage_path}: expected exactly PIC-01 through PIC-36")
    allowed_file = {"EXACT_NAME", "PARTIAL_NAME", "METADATA_CANDIDATE", "NOT_FOUND"}
    allowed_visual = {"RENDER_CONFIRMED", "PARTIAL", "RENDER_MISMATCH", "NOT_RENDERED"}
    allowed_fit = {"MATCH", "PARTIAL", "MISMATCH", "UNKNOWN"}
    for index, row in enumerate(rows, start=2):
        if row.get("file_metadata_status") not in allowed_file:
            errors.append(f"{coverage_path}:{index}: invalid file_metadata_status")
        if row.get("visual_review_status") not in allowed_visual:
            errors.append(f"{coverage_path}:{index}: invalid visual_review_status")
        if row.get("concept_fit_status") not in allowed_fit:
            errors.append(f"{coverage_path}:{index}: invalid concept_fit_status")
        if row.get("per_file_rights_status") != "PACKAGE_CC0_ONLY_PER_FILE_REVIEW_PENDING":
            errors.append(f"{coverage_path}:{index}: per-file rights must remain pending")

    need_path = "Docs/Preparation/AssetCoverage/next_download_needs.csv"
    needs, need_header = load_csv(root, need_path, errors)
    _required_columns(need_header, ("candidate_id", "new_package_download_authorized"), need_path, errors)
    need_ids = check_unique(needs, "candidate_id", need_path, errors)
    if len(needs) != 17:
        errors.append(f"{need_path}: expected 17 gated need rows")
    if not need_ids <= candidate_ids:
        errors.append(f"{need_path}: candidate_id must reference coverage.csv")
    for index, row in enumerate(needs, start=2):
        if row.get("new_package_download_authorized") != "NO":
            errors.append(f"{need_path}:{index}: package download authorization must remain NO")
    return candidate_ids


def _check_asset_authoring(root: Path, coverage_ids: set[str], errors: list[str]) -> None:
    relative = "Docs/Preparation/AssetAuthoring/examples.json"
    data = load_json(root, relative, errors)
    if not isinstance(data, dict):
        return
    if data.get("status") != "HOST_ONLY_NOT_RUNTIME":
        errors.append(f"{relative}: status must remain HOST_ONLY_NOT_RUNTIME")
    definitions = data.get("definitions")
    instances = data.get("instances")
    if not isinstance(definitions, list) or not isinstance(instances, list):
        errors.append(f"{relative}: definitions and instances must be lists")
        return
    if len(definitions) != 5 or len(instances) != 4:
        errors.append(f"{relative}: expected five definitions and four draft instances")
    definition_ids = check_unique(definitions, "definitionId", relative + ".definitions", errors)
    spawn_ids = check_unique(instances, "spawnId", relative + ".instances", errors)
    composition_cases = data.get("compositionCases", [])
    lifecycle_cases = data.get("lifecycleCases", [])
    if not isinstance(composition_cases, list) or not isinstance(lifecycle_cases, list):
        errors.append(f"{relative}: compositionCases/lifecycleCases must be lists")
    else:
        check_unique(composition_cases, "caseId", relative + ".compositionCases", errors)
        check_unique(lifecycle_cases, "caseId", relative + ".lifecycleCases", errors)
        for case in lifecycle_cases:
            if isinstance(case, dict) and case.get("numericBudgetStatus") != "NOT_SET":
                errors.append(f"{relative}: lifecycle numeric budgets must remain NOT_SET")
    for index, definition in enumerate(definitions):
        if not isinstance(definition, dict):
            errors.append(f"{relative}.definitions[{index}]: expected object")
            continue
        candidate_refs = definition.get("candidateRefs", [])
        if not isinstance(candidate_refs, list) or not set(candidate_refs) <= coverage_ids:
            errors.append(f"{relative}.definitions[{index}]: candidateRefs must point to P10 candidates")
        source_refs = definition.get("sourceRefs", [])
        if not isinstance(source_refs, list):
            errors.append(f"{relative}.definitions[{index}].sourceRefs: expected a list")
            source_refs = []
        for source_index, source in enumerate(source_refs):
            if not isinstance(source, dict) or source.get("licenseEvidenceStatus") != "PACKAGE_LEVEL_ONLY_PER_FILE_REVIEW_PENDING":
                errors.append(f"{relative}.definitions[{index}].sourceRefs[{source_index}]: license review must remain pending")
        render_policy = definition.get("renderPolicy", {})
        expected_rights = "PACKAGE_LEVEL_ONLY_PER_FILE_REVIEW_PENDING" if source_refs else "NO_SOURCE_NOT_ASSESSED"
        if not isinstance(render_policy, dict) or render_policy.get("licenseEvidenceStatus") != expected_rights:
            errors.append(f"{relative}.definitions[{index}]: renderPolicy license status must match source evidence")
        elif render_policy.get("localVisualLodRef") is not None or render_policy.get("remoteProxyVariant") is not None:
            errors.append(f"{relative}.definitions[{index}]: visual LOD/proxy choices must remain unresolved")
        values = definition.get("gameplayValues", {})
        for field in ("gainVolumeUnits", "pickupDiameterWorldM", "collisionRadiusWorldM", "surfaceClass", "canWakePhysics"):
            if isinstance(values, dict) and values.get(field) is not None:
                errors.append(f"{relative}.definitions[{index}]: {field} must remain unresolved/null")
        layers = definition.get("geometryLayers", {})
        if isinstance(layers, dict):
            for proxy_name in ("gameplayColliderProxy", "shapeSupportProxy"):
                proxy = layers.get(proxy_name)
                if isinstance(proxy, dict) and proxy.get("dimensionsWorldM") is not None:
                    errors.append(f"{relative}.definitions[{index}]: {proxy_name}.dimensionsWorldM must remain null")
            support_proxy = layers.get("shapeSupportProxy")
            if isinstance(support_proxy, dict) and support_proxy.get("supportPointsLocal"):
                errors.append(f"{relative}.definitions[{index}]: support points must remain unmeasured/empty")
    graph: dict[str, list[str]] = {}
    for index, instance in enumerate(instances):
        if not isinstance(instance, dict):
            continue
        definition_id = instance.get("definitionId")
        if definition_id not in definition_ids:
            errors.append(f"{relative}.instances[{index}]: unknown definitionId {definition_id!r}")
        parent = instance.get("supportParentId")
        if parent is not None and parent not in spawn_ids:
            errors.append(f"{relative}.instances[{index}]: unknown supportParentId {parent!r}")
        graph[instance.get("spawnId", f"<row-{index}>")] = [parent] if isinstance(parent, str) else []
        if instance.get("initialPose") is not None:
            errors.append(f"{relative}.instances[{index}]: initialPose must remain null before scale approval")
    check_dag(graph, relative + " support-parent links", errors)


def _check_decisions(root: Path, errors: list[str]) -> None:
    relative = "Docs/Preparation/RuleDecisions/decisions.json"
    data = load_json(root, relative, errors)
    if not isinstance(data, dict):
        return
    decisions = data.get("decisions")
    if not isinstance(decisions, list):
        errors.append(f"{relative}: decisions must be a list")
        return
    check_unique(decisions, "id", relative + ".decisions", errors)
    for index, decision in enumerate(decisions):
        if not isinstance(decision, dict):
            continue
        if decision.get("recommended_option") is not None:
            errors.append(f"{relative}.decisions[{index}]: no option may be treated as a new user decision")
        candidates = decision.get("candidates", [])
        if not isinstance(candidates, list):
            errors.append(f"{relative}.decisions[{index}]: candidates must be a list")
            continue
        for option_index, option in enumerate(candidates):
            if isinstance(option, dict) and not str(option.get("reject_basis_if_not_selected", "")).strip():
                errors.append(f"{relative}.decisions[{index}].candidates[{option_index}]: missing reject basis")


def _check_protocol(root: Path, errors: list[str]) -> None:
    relative = "Docs/Preparation/ProtocolExamples/transaction_cases.json"
    data = load_json(root, relative, errors)
    if not isinstance(data, dict):
        return
    if data.get("status") != "ILLUSTRATIVE_NOT_RUNTIME_ACCEPTANCE":
        errors.append(f"{relative}: fixture status must retain non-runtime boundary")
    cases = data.get("cases")
    if not isinstance(cases, list):
        errors.append(f"{relative}: cases must be a list")
        return
    ids = check_unique(cases, "id", relative + ".cases", errors)
    for missing in sorted(PROTOCOL_CASES - ids):
        errors.append(f"{relative}: missing protocol counterexample {missing}")
    for case in cases:
        if not isinstance(case, dict):
            continue
        if case.get("id") in {"bank-vs-ko-same-fixture-tick", "cutoff-receive-time-boundary"} and case.get("decision") != "OPEN_PROJECT_POLICY":
            errors.append(f"{relative}: unresolved bank/KO and cutoff policies must remain OPEN")


def _check_math(root: Path, errors: list[str]) -> None:
    relative = "Docs/Preparation/MathCases/vectors.json"
    data = load_json(root, relative, errors)
    if not isinstance(data, dict):
        return
    vectors = data.get("vectors")
    if not isinstance(vectors, list):
        errors.append(f"{relative}: vectors must be a list")
        return
    ids = check_unique(vectors, "id", relative + ".vectors", errors)
    for missing in sorted(MATH_CASES - ids):
        errors.append(f"{relative}: missing numeric boundary fixture {missing}")
    if len(vectors) != 25:
        errors.append(f"{relative}: expected 25 deterministic math fixtures")


def _check_brand_names(root: Path, errors: list[str]) -> None:
    relative = "Docs/Preparation/BrandResearch/names.csv"
    rows, header = load_csv(root, relative, errors)
    _required_columns(header, ("candidate_id", "english_candidate", "chinese_candidate", "japanese_candidate", "language_review_status"), relative, errors)
    ids = check_unique(rows, "candidate_id", relative, errors)
    if len(rows) != 5 or len(ids) != 5:
        errors.append(f"{relative}: expected five unselected name candidates")


def _check_cosmetics(root: Path, errors: list[str]) -> None:
    relative = "Docs/Preparation/CosmeticStates/state_transitions.json"
    data = load_json(root, relative, errors)
    if not isinstance(data, dict):
        return
    if data.get("paidStoreEnabled") is not False:
        errors.append(f"{relative}: paidStoreEnabled must remain false")
    if data.get("artifactStatus") != "proposal_not_frozen":
        errors.append(f"{relative}: state artifact must remain a proposal")
    if data.get("authority") != "official_platform_entitlement_only":
        errors.append(f"{relative}: optional entitlement must use official platform authority only")
    free_default = data.get("freeDefault", {})
    if not isinstance(free_default, dict) or any(free_default.get(field) is not True for field in ("alwaysAvailable", "selectedWhenNoUsableSelection", "requiredDuringUnknownEntitlement")):
        errors.append(f"{relative}: free default must remain available for unresolved/no-selection states")
    states = data.get("states")
    transitions = data.get("transitionRules")
    if not isinstance(states, list) or not isinstance(transitions, list):
        errors.append(f"{relative}: states and transitionRules must be lists")
        return
    state_ids = check_unique(states, "id", relative + ".states", errors)
    if len(states) != 14 or len(transitions) != 18:
        errors.append(f"{relative}: expected 14 states and 18 transition rules")
    seen_edges: set[tuple[str, str]] = set()
    for index, transition in enumerate(transitions):
        if not isinstance(transition, dict):
            errors.append(f"{relative}.transitionRules[{index}]: expected object")
            continue
        source, event, destination = transition.get("from"), transition.get("event"), transition.get("to")
        if source not in state_ids or destination not in state_ids:
            errors.append(f"{relative}.transitionRules[{index}]: endpoint must name a declared state")
        edge = (source, event)
        if edge in seen_edges:
            errors.append(f"{relative}.transitionRules[{index}]: duplicate from/event rule")
        seen_edges.add(edge)
        if destination == "SELECTED_CONFIRMED_OPTIONAL" and event != "official_owned":
            errors.append(f"{relative}.transitionRules[{index}]: optional entitlement requires official_owned")

    cases_path = "Docs/Preparation/CosmeticStates/cases.csv"
    rows, header = load_csv(root, cases_path, errors)
    _required_columns(header, ("caseId", "actual", "evidence", "status"), cases_path, errors)
    check_unique(rows, "caseId", cases_path, errors)
    if len(rows) != 18:
        errors.append(f"{cases_path}: expected 18 review cases")
    _check_schema_version(rows, cases_path, errors)
    _check_status_and_blanks(rows, "status", ("actual", "evidence"), cases_path, errors)


def _check_runtime_evidence(root: Path, errors: list[str]) -> None:
    base = "Docs/Preparation/RuntimeEvidence/"
    cases_path = base + "cases.csv"
    cases, case_header = load_csv(root, cases_path, errors)
    _required_columns(case_header, ("schemaVersion", "case_id", "status", "observed", "evidence"), cases_path, errors)
    check_unique(cases, "case_id", cases_path, errors)
    if len(cases) != 25:
        errors.append(f"{cases_path}: expected 25 prepared runtime cases")
    _check_schema_version(cases, cases_path, errors)
    _check_numeric_fields(cases, ("client_count",), cases_path, errors)
    for index, row in enumerate(cases, start=2):
        try:
            count = int(row.get("client_count", ""))
        except ValueError:
            continue
        if count < 2 or count > 4:
            errors.append(f"{cases_path}:{index}: client_count must be between 2 and 4")
    _check_status_and_blanks(cases, "status", ("observed", "evidence"), cases_path, errors)

    metrics_path = base + "metrics.csv"
    metrics, metrics_header = load_csv(root, metrics_path, errors)
    _required_columns(metrics_header, ("schemaVersion", "metric_id", "status", "sampling_interval_ms_per_client", "sample_count", "raw_data_evidence_path", "p50", "p95", "p99"), metrics_path, errors)
    check_unique(metrics, "metric_id", metrics_path, errors)
    if len(metrics) != 8:
        errors.append(f"{metrics_path}: expected eight prepared metrics")
    _check_schema_version(metrics, metrics_path, errors)
    _check_status_and_blanks(metrics, "status", ("sampling_interval_ms_per_client", "sample_count", "raw_data_evidence_path", "p50", "p95", "p99"), metrics_path, errors)

    env_path = base + "session/environment.json"
    env = load_json(root, env_path, errors)
    if not isinstance(env, dict):
        return
    if env.get("schemaVersion") != 1 or env.get("templateStatus") != "TEMPLATE_NOT_RUN":
        errors.append(f"{env_path}: template must remain schemaVersion=1/TEMPLATE_NOT_RUN")
    if env.get("runId") is not None or env.get("testDateTime") is not None:
        errors.append(f"{env_path}: runId/testDateTime must remain null in an unrun template")
    for section_name, fields in {
        "unity": ("editorVersion", "buildTarget", "projectBuildId"),
        "vrchat": ("worldBuildId", "worldSdkPackageVersion", "baseSdkPackageVersion", "udonSharpPackageVersion"),
        "network": ("transportPathOrTestHarness", "configuredLatencyMs", "configuredJitterMs", "configuredLossPercent", "measuredConditionsMethod"),
        "authorityAndSnapshot": ("authorityObjectIdentityPseudonym", "initialEpoch", "snapshotRevision", "snapshotCompletenessEvidence", "initialCommitSequence"),
        "collection": ("frameSampleMethod", "networkSampleMethod", "memoryMetricAndMethod", "samplingIntervalMsPerClient", "warmupPolicy", "outlierPolicy", "rawDataEvidencePath"),
    }.items():
        section = env.get(section_name)
        if not isinstance(section, dict):
            errors.append(f"{env_path}: missing {section_name} object")
            continue
        for field in fields:
            if section.get(field) not in (None, ""):
                errors.append(f"{env_path}: unrun field {section_name}.{field} must be null/blank")
    clients = env.get("clients")
    if not isinstance(clients, list) or len(clients) != 4:
        errors.append(f"{env_path}: expected four client template slots")
    else:
        for index, client in enumerate(clients):
            if not isinstance(client, dict):
                errors.append(f"{env_path}: clients[{index}] must be an object")
                continue
            for field in ("roleDescription", "observedRoles"):
                if field == "roleDescription" and client.get(field) not in (None, ""):
                    errors.append(f"{env_path}: clients[{index}].roleDescription must be blank")
            hardware = client.get("hardware")
            if isinstance(hardware, dict) and any(value not in (None, "") for value in hardware.values()):
                errors.append(f"{env_path}: clients[{index}] hardware must be blank in template")
            if client.get("avatarCondition") != "unknown":
                errors.append(f"{env_path}: clients[{index}] avatarCondition must be unknown")
            clock = client.get("clock", {})
            if not isinstance(clock, dict) or clock.get("source") != "unknown" or clock.get("timebase") != "unknown" or clock.get("offsetToReferenceMs") is not None or clock.get("uncertaintyMs") is not None:
                errors.append(f"{env_path}: clients[{index}] clock observations must remain unknown/null")
            roles = client.get("observedRoles", {})
            if not isinstance(roles, dict) or any(roles.get(field) != "unknown" for field in ("matchAuthorityOwnerLabel", "playerObjectOwnerLabel", "vrchatMasterLabel", "instanceOwnerLabel")) or roles.get("observedAt") is not None:
                errors.append(f"{env_path}: clients[{index}] observed roles must remain unknown/null")
    privacy = env.get("privacyReview", {})
    if not isinstance(privacy, dict) or privacy.get("pseudonymousClientLabelsUsed") is not True or privacy.get("personalIdentifiersExcluded") is not True or privacy.get("reviewedByUserBeforeExternalSharing") is not None:
        errors.append(f"{env_path}: privacy template must use pseudonyms, exclude personal identifiers, and remain unreviewed")


def _check_baseline_metadata(root: Path, errors: list[str]) -> None:
    relative = "Docs/Preparation/Baseline/metadata.json"
    data = load_json(root, relative, errors)
    if not isinstance(data, dict):
        return
    if data.get("schemaVersion") != 1:
        errors.append(f"{relative}: schemaVersion must be 1")
    source_files = data.get("sourceFiles")
    if not isinstance(source_files, list) or len(source_files) != 7:
        errors.append(f"{relative}: expected seven repository source-file records")
        return
    expected_source_paths = {
        "VRChatKatamariDanacyUnity/ProjectSettings/ProjectVersion.txt",
        "VRChatKatamariDanacyUnity/Packages/manifest.json",
        "VRChatKatamariDanacyUnity/Packages/vpm-manifest.json",
        "VRChatKatamariDanacyUnity/Packages/packages-lock.json",
        "VRChatKatamariDanacyUnity/Packages/.gitignore",
        "VRChatKatamariDanacyUnity/Assets/Scenes/VRCDefaultWorldScene.unity",
        "VRChatKatamariDanacyUnity/Assets/Scenes/VRCDefaultWorldScene.unity.meta",
    }
    seen_paths: set[str] = set()
    for index, record in enumerate(source_files):
        label = f"{relative}.sourceFiles[{index}]"
        if not isinstance(record, dict):
            errors.append(f"{label}: expected object")
            continue
        path_text = record.get("path")
        if not isinstance(path_text, str) or not path_text:
            errors.append(f"{label}: path must be repository-relative")
            continue
        path = PurePosixPath(path_text)
        if path.is_absolute() or ".." in path.parts or path.as_posix() != path_text:
            errors.append(f"{label}: unsafe or non-normalized repository-relative path")
            continue
        if path_text in seen_paths:
            errors.append(f"{label}: duplicate source path {path_text}")
            continue
        seen_paths.add(path_text)
        byte_count = record.get("bytes")
        digest = record.get("sha256")
        if isinstance(byte_count, bool) or not isinstance(byte_count, int) or byte_count < 0:
            errors.append(f"{label}: bytes must be a nonnegative integer")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            errors.append(f"{label}: sha256 must be 64 lowercase hex characters")
        source_path = root.joinpath(*path.parts)
        try:
            contents = source_path.read_bytes()
        except OSError as exc:
            errors.append(f"{label}: cannot read source file ({exc})")
            continue
        if isinstance(byte_count, int) and not isinstance(byte_count, bool) and len(contents) != byte_count:
            errors.append(f"{label}: recorded byte count does not match source")
        if isinstance(digest, str) and re.fullmatch(r"[0-9a-f]{64}", digest):
            if hashlib.sha256(contents).hexdigest() != digest:
                errors.append(f"{label}: recorded SHA-256 does not match source")

    if seen_paths != expected_source_paths:
        errors.append(f"{relative}: sourceFiles must inventory the expected project metadata/scene paths")

    observation = data.get("projectObservation", {})
    editor = observation.get("observedEditor", {}) if isinstance(observation, dict) else {}
    if editor.get("version") != "2022.3.22f1" or editor.get("revision") != "887be4894c44":
        errors.append(f"{relative}: project Editor version/revision observation is missing or unexpected")
    dependencies = observation.get("observedVpmDependencies", {}) if isinstance(observation, dict) else {}
    for package in ("com.vrchat.base", "com.vrchat.worlds"):
        entry = dependencies.get(package, {}) if isinstance(dependencies, dict) else {}
        if entry.get("declaredVersion") != "3.10.5" or entry.get("lockedVersion") != "3.10.5":
            errors.append(f"{relative}: {package} must be recorded as declared/locked 3.10.5")
    if observation.get("vccVersion") != "unknown":
        errors.append(f"{relative}: VCC version must remain unknown")
    udon = observation.get("udonSharp", {})
    if not isinstance(udon, dict) or udon.get("independentVersion") != "unknown":
        errors.append(f"{relative}: independent UdonSharp version must remain unknown")

    support = data.get("official_support_evidence", [])
    unity_support = next((item for item in support if isinstance(item, dict) and item.get("subject") == "Unity Editor recommendation for VRChat content"), None) if isinstance(support, list) else None
    sdk_support = next((item for item in support if isinstance(item, dict) and item.get("subject") == "VRChat Worlds/Base SDK release support/currentness for 3.10.5"), None) if isinstance(support, list) else None
    if not unity_support or unity_support.get("publishedValue") != "2022.3.22f1" or not isinstance(unity_support.get("url"), str):
        errors.append(f"{relative}: official Unity recommendation evidence must remain separate from project observation")
    if not sdk_support or sdk_support.get("status") != "unknown" or sdk_support.get("publishedValue") is not None:
        errors.append(f"{relative}: do not assert official-current support for SDK 3.10.5")

    resolver = data.get("resolver_observation", {})
    repository = resolver.get("repositoryCheckout", {}) if isinstance(resolver, dict) else {}
    if not isinstance(repository.get("sdkSourceSearch"), str) or "checkout" not in repository.get("sdkSourceSearch", "").lower():
        errors.append(f"{relative}: local checkout SDK-source observation must be scoped to that checkout")
    ignore_rules = str(repository.get("packageIgnoreRules", ""))
    if "com.vrchat.*" not in ignore_rules or "com.vrchat.core.*" not in ignore_rules:
        errors.append(f"{relative}: SDK-source ignore and resolver exception must be distinguished")
    if "NOT_RUN" not in str(repository.get("vpmResolverSourceOrExecution", "")):
        errors.append(f"{relative}: resolver execution must remain NOT_RUN")
    if not str(resolver.get("windowsInstallation", "")).startswith("NOT_INSPECTED"):
        errors.append(f"{relative}: Windows installation must remain explicitly NOT_INSPECTED")
    scene = resolver.get("defaultScene", {}) if isinstance(resolver, dict) else {}
    if not isinstance(scene, dict) or scene.get("fileObserved") is not True or scene.get("metaObserved") is not True:
        errors.append(f"{relative}: default-scene and .meta presence observations are required")
    elif scene.get("openedOrValidatedInUnity") != "NOT_RUN" or "unknown" not in str(scene.get("licenseAndTemplateProvenance", "")).lower():
        errors.append(f"{relative}: Unity validation and scene redistribution provenance must remain unresolved")
    runtime_checks = data.get("runtimeChecks")
    expected_runtime_checks = {"unityEditorLaunch", "vccInspectionOrRestore", "vpmResolverExecution", "packageRestore", "defaultSceneOpenOrValidation", "unityConsoleCompile", "udonSharpCompile", "clientSim", "buildAndTest", "vrComfortOrInput", "multiplayerOrNetwork"}
    if not isinstance(runtime_checks, dict) or set(runtime_checks) != expected_runtime_checks or any(value != "NOT_RUN" for value in runtime_checks.values()):
        errors.append(f"{relative}: expected runtime-check fields must all remain NOT_RUN")
    boundary = data.get("environmentBoundary", {})
    if not isinstance(boundary, dict) or boundary.get("editorOwner") != "user" or boundary.get("unityWasStarted") is not False or boundary.get("unityMcp") != "unknown/unconfirmed":
        errors.append(f"{relative}: user-owned Windows Editor/MCP boundary must be explicit")


def _check_release_dag(root: Path, errors: list[str]) -> None:
    tasks_path = "tasks.json"
    tasks_data = load_json(root, tasks_path, errors)
    proposal_path = "Docs/Preparation/ReleaseRFC/proposed_dag.json"
    proposal = load_json(root, proposal_path, errors)
    if not isinstance(tasks_data, dict) or not isinstance(proposal, dict):
        return
    tasks = tasks_data.get("tasks")
    if not isinstance(tasks, list):
        errors.append(f"{tasks_path}: tasks must be a list")
        return
    task_ids = check_unique(tasks, "id", tasks_path, errors)
    base_graph: dict[str, list[str]] = {}
    for task in tasks:
        if not isinstance(task, dict) or not isinstance(task.get("id"), str):
            continue
        deps = task.get("dependencies", [])
        if not isinstance(deps, list) or not all(isinstance(dep, str) for dep in deps):
            errors.append(f"{tasks_path}: {task['id']} dependencies must be string IDs")
            deps = []
        base_graph[task["id"]] = deps
    check_dag(base_graph, "tasks.json DAG", errors)

    if proposal.get("status") != "PROPOSAL_ONLY_NOT_APPLIED" or proposal.get("root_task_file_modified") is not False:
        errors.append(f"{proposal_path}: proposal must remain unapplied")
    baseline = proposal.get("current_baseline", {}).get("tasks", {})
    if not isinstance(baseline, dict):
        errors.append(f"{proposal_path}: current_baseline.tasks must be a map")
        return
    for task_id, item in baseline.items():
        if task_id not in base_graph or not isinstance(item, dict):
            errors.append(f"{proposal_path}: baseline task {task_id} is not in tasks.json")
            continue
        if set(item.get("dependencies", [])) != set(base_graph[task_id]):
            errors.append(f"{proposal_path}: baseline dependencies for {task_id} differ from tasks.json")

    for alternative in proposal.get("alternatives", []):
        if not isinstance(alternative, dict):
            continue
        label = f"{proposal_path} {alternative.get('id', '<alternative>')} DAG"
        overlays = alternative.get("proposed_dependencies")
        if overlays is None:
            overlays = alternative.get("proposed_dependencies_after_approval")
        if not isinstance(overlays, dict):
            continue
        overlaid = {node: list(deps) for node, deps in base_graph.items()}
        for task_id, deps in overlays.items():
            if task_id not in task_ids:
                errors.append(f"{label}: unknown task {task_id}")
            if not isinstance(deps, list) or not all(isinstance(dep, str) for dep in deps):
                errors.append(f"{label}: {task_id} dependencies must be string IDs")
                continue
            overlaid[task_id] = deps
        check_dag(overlaid, label, errors)


def validate_workspace(root: Path) -> list[str]:
    """Return focused validation errors without mutating the workspace."""
    errors: list[str] = []
    _check_package_config(root, errors)
    coverage_ids = _check_asset_coverage(root, errors)
    _check_asset_authoring(root, coverage_ids, errors)
    _check_decisions(root, errors)
    _check_protocol(root, errors)
    _check_math(root, errors)
    _check_brand_names(root, errors)
    _check_cosmetics(root, errors)
    _check_runtime_evidence(root, errors)
    _check_baseline_metadata(root, errors)
    _check_release_dag(root, errors)
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Check focused P09–P18 host-side data integrity; no runtime acceptance.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[3], help="repository root (default: this checkout)")
    args = parser.parse_args(argv)
    errors = validate_workspace(args.root.resolve())
    if errors:
        print("Third-batch data checks: FAIL", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1
    print("Third-batch data checks: PASS (focused P09–P18 host data; P16 repository metadata verified)")
    print("No Unity/runtime, gameplay, performance, store, license, or publication acceptance is implied.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
