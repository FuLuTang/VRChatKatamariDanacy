#!/usr/bin/env python3
"""Read-only, standard-library structural QA for the P10/P11 host-only drafts.

This validates the JSON Schema subset used by these files, data cross-references,
and frozen inventory paths. It is not a general JSON Schema implementation and
creates no Unity/runtime files.
"""
from __future__ import annotations

import csv
import json
import re
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[3]
COVERAGE_DIR = ROOT / "Docs/Preparation/AssetCoverage"
AUTHORING_DIR = ROOT / "Docs/Preparation/AssetAuthoring"


def fail(message: str) -> None:
    raise ValueError(message)


def load_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8") as stream:
        return json.load(stream)


def schema_type_ok(value: Any, expected: str) -> bool:
    if expected == "object":
        return isinstance(value, dict)
    if expected == "array":
        return isinstance(value, list)
    if expected == "string":
        return isinstance(value, str)
    if expected == "boolean":
        return isinstance(value, bool)
    if expected == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if expected == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if expected == "null":
        return value is None
    fail(f"unsupported schema type: {expected}")
    return False


def resolve_ref(schema: dict[str, Any], ref: str) -> dict[str, Any]:
    if not ref.startswith("#/"):
        fail(f"only local JSON Schema refs are supported, got {ref}")
    value: Any = schema
    for part in ref[2:].split("/"):
        value = value[part.replace("~1", "/").replace("~0", "~")]
    if not isinstance(value, dict):
        fail(f"schema ref is not an object: {ref}")
    return value


def validate_subset(value: Any, rule: dict[str, Any], schema: dict[str, Any], path: str) -> None:
    """Validate only the JSON Schema keywords used in the checked-in schemas."""
    if "$ref" in rule:
        validate_subset(value, resolve_ref(schema, rule["$ref"]), schema, path)
        return

    expected_type = rule.get("type")
    if expected_type is not None:
        types = expected_type if isinstance(expected_type, list) else [expected_type]
        if not any(schema_type_ok(value, kind) for kind in types):
            fail(f"{path}: expected type {types}, got {type(value).__name__}")

    if "const" in rule and value != rule["const"]:
        fail(f"{path}: expected constant {rule['const']!r}, got {value!r}")
    if "enum" in rule and value not in rule["enum"]:
        fail(f"{path}: value {value!r} is not in enum {rule['enum']!r}")
    if "pattern" in rule and isinstance(value, str) and re.fullmatch(rule["pattern"], value) is None:
        fail(f"{path}: value {value!r} does not match {rule['pattern']!r}")
    if isinstance(value, str):
        if len(value) < rule.get("minLength", 0):
            fail(f"{path}: shorter than minLength")
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if value < rule.get("minimum", float("-inf")):
            fail(f"{path}: below minimum")

    if isinstance(value, dict):
        required = rule.get("required", [])
        missing = [key for key in required if key not in value]
        if missing:
            fail(f"{path}: missing required properties {missing}")
        properties = rule.get("properties", {})
        if rule.get("additionalProperties") is False:
            extra = sorted(set(value) - set(properties))
            if extra:
                fail(f"{path}: unexpected properties {extra}")
        for key, member in value.items():
            if key in properties:
                validate_subset(member, properties[key], schema, f"{path}.{key}")
    if isinstance(value, list):
        if len(value) < rule.get("minItems", 0):
            fail(f"{path}: fewer than minItems")
        if len(value) > rule.get("maxItems", float("inf")):
            fail(f"{path}: more than maxItems")
        if rule.get("uniqueItems"):
            encodings = [json.dumps(item, sort_keys=True, ensure_ascii=False) for item in value]
            if len(encodings) != len(set(encodings)):
                fail(f"{path}: duplicate array elements")
        item_rule = rule.get("items")
        if item_rule is not None:
            for index, member in enumerate(value):
                validate_subset(member, item_rule, schema, f"{path}[{index}]")


def read_csv(path: Path) -> tuple[list[str], list[dict[str, str]]]:
    with path.open("r", encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if not reader.fieldnames or len(reader.fieldnames) != len(set(reader.fieldnames)):
            fail(f"{path}: missing or duplicate CSV header")
        rows = list(reader)
        if any(None in row or any(value is None for value in row.values()) for row in rows):
            fail(f"{path}: malformed CSV row width")
        return reader.fieldnames, rows


def validate_csv(path: Path, schema_path: Path) -> list[dict[str, str]]:
    fields, rows = read_csv(path)
    schema = load_json(schema_path)
    if schema.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
        fail(f"{schema_path}: unexpected JSON Schema dialect")
    for index, row in enumerate(rows, start=2):
        if set(row) != set(fields):
            fail(f"{path}:{index}: CSV row/header mismatch")
        validate_subset(row, schema, schema, f"{path.name}:{index}")
    return rows


def main() -> None:
    coverage = validate_csv(COVERAGE_DIR / "coverage.csv", COVERAGE_DIR / "coverage.schema.json")
    needs = validate_csv(COVERAGE_DIR / "next_download_needs.csv", COVERAGE_DIR / "next_download_needs.schema.json")
    original_fields, original = read_csv(ROOT / "Docs/Research/AssetManifest/picnic_candidates.csv")
    del original_fields
    ids = [row["candidate_id"] for row in coverage]
    original_ids = [row["candidate_id"] for row in original]
    if len(coverage) != 36 or len(ids) != len(set(ids)) or set(ids) != set(original_ids):
        fail("coverage must contain exactly the 36 unique original PIC candidate IDs")
    need_ids = [row["candidate_id"] for row in needs]
    if len(need_ids) != len(set(need_ids)) or not set(need_ids).issubset(set(ids)):
        fail("next-download needs contain duplicate or unknown candidate IDs")
    if any(row["new_package_download_authorized"] != "NO" for row in needs):
        fail("a next-download need must not authorize a download")

    inventory_fields, inventory_rows = read_csv(ROOT / "Docs/AssetIntake/DirectDownload/inventory.csv")
    del inventory_fields
    archive_for_package = {
        "KENNEY-FOOD-KIT-2.0": "kenney_food-kit.zip",
        "KENNEY-NATURE-KIT-2.1": "kenney_nature-kit.zip",
    }
    inventory_members = {
        (row["archive"], row["member_path"])
        for row in inventory_rows
        if row["directory"].lower() != "true"
    }
    selection = load_json(ROOT / "Docs/AssetIntake/Preview/selection.json")
    preview_members = {item["source_member"] for item in selection["assets"]}
    if len(preview_members) != 12:
        fail("the frozen P21 selection must contain 12 distinct source members")
    for row in coverage:
        package_ids = [] if row["source_package_ids"] in ("none", "unknown") else row["source_package_ids"].split(";")
        members = [] if row["source_member_candidates"] in ("none", "unknown") else row["source_member_candidates"].split(";")
        if len(package_ids) != 1 and members:
            fail(f"{row['candidate_id']}: source members require one explicit package ID")
        for member in members:
            archive = archive_for_package.get(package_ids[0])
            if archive is None or (archive, member) not in inventory_members:
                fail(f"{row['candidate_id']}: source member not present in frozen inventory: {member}")
        visually_reviewed = row["visual_review_status"] != "NOT_RENDERED"
        if visually_reviewed and not set(members).intersection(preview_members):
            fail(f"{row['candidate_id']}: visual status has no matching P21 selected source")
        if not visually_reviewed and row["evidence_basis"] == "METADATA_AND_RENDER":
            fail(f"{row['candidate_id']}: render evidence basis without a reviewed render")

    schema_path = AUTHORING_DIR / "asset_authoring.schema.json"
    example_path = AUTHORING_DIR / "examples.json"
    authoring_schema = load_json(schema_path)
    examples = load_json(example_path)
    if authoring_schema.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
        fail("authoring schema is not declared as JSON Schema draft 2020-12")
    validate_subset(examples, authoring_schema, authoring_schema, "examples")

    definitions = examples["definitions"]
    definition_ids = [item["definitionId"] for item in definitions]
    if len(definition_ids) != len(set(definition_ids)):
        fail("definitionId values must be unique")
    for definition in definitions:
        gameplay = definition["gameplayValues"]
        open_values = ("gainVolumeUnits", "pickupDiameterWorldM", "collisionRadiusWorldM", "surfaceClass", "canWakePhysics")
        if any(gameplay[key] is not None for key in open_values):
            fail(f"{definition['definitionId']}: gameplay values must remain null/open")
        geometry = definition["geometryLayers"]
        if geometry["gameplayColliderProxy"]["dimensionsWorldM"] is not None:
            fail(f"{definition['definitionId']}: collider dimensions must remain unset")
        if geometry["shapeSupportProxy"]["supportPointsLocal"]:
            fail(f"{definition['definitionId']}: support points must remain unset")
        if definition["renderPolicy"]["localVisualLodRef"] is not None or definition["renderPolicy"]["remoteProxyVariant"] is not None:
            fail(f"{definition['definitionId']}: visual LOD/proxy references must remain unset")
    if set(examples["instances"][i]["definitionId"] for i in range(len(examples["instances"]))) - set(definition_ids):
        fail("an instance references an unknown definitionId")
    instances = examples["instances"]
    spawn_ids = [item["spawnId"] for item in instances]
    if len(spawn_ids) != len(set(spawn_ids)):
        fail("spawnId values must be unique")
    spawn_id_set = set(spawn_ids)
    parents = {}
    for item in instances:
        parent = item["supportParentId"]
        if parent is not None and parent not in spawn_id_set:
            fail(f"{item['spawnId']}: supportParentId does not resolve")
        if parent == item["spawnId"]:
            fail(f"{item['spawnId']}: self-support cycle")
        parents[item["spawnId"]] = parent
        if item["initialPose"] is not None:
            fail(f"{item['spawnId']}: example must keep unmeasured position null")
    for spawn_id in spawn_id_set:
        seen = set()
        cursor = spawn_id
        while cursor is not None:
            if cursor in seen:
                fail(f"support-parent cycle includes {cursor}")
            seen.add(cursor)
            cursor = parents[cursor]

    # Confirm preview provenance for any source record that claims a verified render.
    preview_by_member = {item["source_member"]: item for item in selection["assets"]}
    for definition in definitions:
        for source in definition["sourceRefs"]:
            if source["evidenceType"] == "VERIFIED_PREVIEW":
                preview = preview_by_member.get(source["memberPath"])
                if preview is None:
                    fail(f"{definition['definitionId']}: no matching preview selection record")
                if source["sourceMemberSha256"] != preview["source_sha256"] or source["previewDerivativeSha256"] != preview["sha256"]:
                    fail(f"{definition['definitionId']}: source/preview hash does not match selection")

    print(
        "PASS: stdlib schema-subset validation; 36 coverage rows; "
        f"{len(needs)} gated needs; source paths and Apple preview hashes; "
        f"{len(definitions)} definitions, {len(instances)} instances, support graph acyclic."
    )


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, KeyError, OSError, TypeError, ValueError, json.JSONDecodeError) as error:
        raise SystemExit(f"FAIL: {error}") from error
