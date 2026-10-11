# P22 — final push-scope audit and remaining allowlist

**Review date:** 2026-10-11. **Disposition:** the reviewed first-two-batch content has no identified privacy/credential hold. Lead reports the first push was successful with remote OID `78c49e632ad989439c24d8606833cc39a2850282`; P09/P13/P14/P17 were subsequently pushed at `414c177`, and P12/P15/P16/P18 at `c9f5871`. Those Git writes were Lead-owned; this audit/checker performed no staging, commit, push, SSH, Unity, Blender, MCP, download, account, or publication action.

## Candidate and exclusion checks

Read-only Git checks used:

```sh
git ls-files --cached --others --exclude-standard
git ls-files --cached --ignored --exclude-standard
git ls-files --cached -- Docs/tools/dsh_local_rpc.py Docs/AssetIntake/Downloads Docs/AssetIntake/Quarantine 'Docs/Source/ChatGPT-VRC块魂-20261011-0332.txt'
git diff --cached --name-only
git check-ignore -v --no-index <representative sensitive/generated paths>
```

The exact cached+ignored query, explicit cached sensitive-path query, and cached-diff names query were empty in the initial audit. An earlier combined query accidentally used `--others -i`; it did **not** establish index membership and was corrected. The private chat export, local RPC helper, raw Downloads, Quarantine, SDK source directories, and generated caches matched ignore rules; no index/staging evidence for those paths was found. The first-two-batch candidate inventory contained no `.zip`, `.glb`, `.blend`, or SDK source binary. P21 PNGs are asset review renders, not desktop screenshots or Unity/game acceptance evidence; the contact sheet contains only the 12 asset renders.

The first-two-batch content groups previously reviewed and reported to Lead were P01–P08, P20, P21 and associated evidence/tests. P21 render bytes/hashes were not changed for anonymization; revised README/handoff and Lead review notes were reread, and the identified workstation/process/MCP endpoint details were removed. The confirmed Windows Unity project path is an approved operational contract, not a credential or host-admin identity.

## Final P09–P18 review and host checks

- P09/P13/P14/P17 content was reviewed; Lead reports those packages pushed at `414c177`. P13's final 36 tests independently passed; its attachment-root fixture is explicitly an assumed sibling/local-pose model, not Unity evidence.
- P10/P11 were reread after correction. `python3 Docs/Preparation/AssetAuthoring/validate_authoring.py` passed; a separate Draft 2020-12 validation of P11 `examples.json` against its schema reported zero schema errors. P10 has 36 coverage rows and 17 gated needs with download authorization `NO`; P11 has the checked source/reference/support-graph draft. Per-file rights remain pending; no asset binary or download is approved by this audit.
- P12's updated host model is bounded to single-part snapshots and clears prior-epoch response cache while keeping business/item idempotency state. Its 34 targeted tests passed. The README now records that the full suite passed after P22 corrected its own expected math-fixture ID from nonexistent `V01-radius-diameter` to actual `V03-radius-diameter-distinct`.
- P15 has 14 states, 18 transitions, 18 `NOT_RUN` cases with blank actual/evidence fields, and `paidStoreEnabled: false`.
- P16 `metadata.json` is schemaVersion 1; all seven repository source-file byte counts and SHA-256 values independently matched this checkout. It separates project metadata from official Unity recommendation evidence, leaves SDK 3.10.5 official-current support and VCC/UdonSharp versions unknown, scopes SDK-source/resolver observations to this checkout, states Windows installation was not inspected, and leaves runtime checks `NOT_RUN`.
- P18's 25 cases and 8 metrics remain unrun. The strengthened standard-library template checker passed, including null/blank checks, non-finite JSON rejection and negative self-checks; it is host-template validation only.
- P22's focused third-batch CLI passed against P09–P18 data, including package/release DAG links, selected IDs, source/instance references, finite JSON values, paid-store-off state, unrun fields, and P16 source hashes. Its 5 focused tests passed. `python3 -m unittest discover -s Docs/tests -v` passed all 132 tests.

None of these host checks establish Unity/Udon compilation, asset import, real multiplayer/network behavior, performance, comfort, platform commerce, legal clearance, or publication readiness.

## Remaining public push allowlist

At the final read-only working-tree check, there were no staged paths. The Lead's exact staged snapshot test remains required before any push. The following remaining content was reviewed for public repository inclusion:

1. **P10 AssetCoverage:** `Docs/Preparation/AssetCoverage/{README.md,coverage.csv,coverage.schema.json,handoff.md,next_download_needs.csv,next_download_needs.schema.json}`. These are metadata/requirements records; all future package-download authorization is `NO`, and per-file rights remain pending.
2. **P11 AssetAuthoring:** `Docs/Preparation/AssetAuthoring/{README.md,asset_authoring.schema.json,examples.json,handoff.md,validate_authoring.py}`. These are host-only examples/schema/tooling, not Unity assets or frozen runtime contracts; source hashes/paths do not include source model payloads.
3. **P22 checker and tests:** `Docs/tools/third_batch/{README.md,__init__.py,validate.py}` and `Docs/tests/third_batch/{__init__.py,test_validate.py}`. The checker is focused, does not scan prose/test strings for secret-like patterns, and explicitly disclaims runtime/license/publication acceptance.
4. **P22 audit report:** `Docs/Preparation/ReleaseAudit/first_two_batches.md` (this file).
5. **P12 QA-note update:** `Docs/Preparation/ProtocolExamples/{README.md,handoff.md}` have unstaged tracked changes clarifying the corrected P22 fixture expectation and current 132-test result. Both changes were reviewed and are accurate; they are outside P10/P11/P22's new files, so the Lead may include or exclude them as separate exact-path decisions.

A read-only `git status --short` snapshot listed these two P12 tracked changes and the four untracked directories above (AssetAuthoring, AssetCoverage, ReleaseAudit, and P22 tools/tests); `git diff --cached --name-only` was empty. The exact cached sensitive-path query was empty. No other unreviewed path was granted blanket release approval. Before pushing, Lead should re-list the exact candidates, inspect the exact staged diff, and keep licensed source, ignored/private files, or any newly added path out of the stage unless separately reviewed.

## Final gate

This is a repository-document/code push allowlist only. It does not authorize public-world upload, marketing publication, paid listings, account actions, Unity operation, or redistribution of third-party asset payloads. Lead retains all Git writes and final staged-file approval.
