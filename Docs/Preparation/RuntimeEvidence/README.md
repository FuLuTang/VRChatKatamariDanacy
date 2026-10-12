# P18 Runtime Evidence Templates

**State: preparation only; all runtime cases are `NOT_RUN`.** This directory defines evidence fields and repeatable test procedures. It is not Udon/runtime code, not an implementation claim, not a specification-derived `PASS`, and does not freeze public interfaces. No Unity, ClientSim, VRChat network session, VR headset, or multi-client test was run for this work package.

## Environment and authority identity

Fill `session/environment.json` once per test session, then preserve the exact filled copy with the report. Record Unity and VRChat SDK build identifiers as actually observed; do not infer them from a repository note. Keep `Owner`, `Master`, and `InstanceOwner` as distinct roles/identities: note the observed role-holder per client, who held the match authority object, who held each player representation where observable, and transitions. Do not copy personal account identifiers; use locally assigned pseudonymous client labels. Record the clock source/timebase for every timestamp stream (client monotonic, wall clock, SDK/server-relative, or unknown) and measured offset/uncertainty if known. Never compare timestamps across timebases without a documented conversion.

All values in the committed environment template are `null` or `unknown` placeholders, not observed sample values. Hardware, display/headset, role, build, network conditions and actual clocks must be entered for each run.

## CSV and JSON data contract

Machine-readable files in this directory use **schemaVersion 1**. CSVs are UTF-8, comma-delimited, one header row, RFC 4180 quoting, LF line endings; empty field means not recorded, never zero. Use exact status values `NOT_RUN`, `RUNNING`, `PASS`, `FAIL`, or `BLOCKED`; a status is a report of actual evidence only. `observed` and `evidence` stay empty until a real run produces them. Do not backfill expected results into observation fields. Preserve original records and create a new `run_id` for each attempt rather than overwriting prior evidence.

- `cases.csv`: 25 unexecuted test cases spanning 2–4 clients, authority/epoch, join, transport/retry/order/cutoff, bank/KO, input cleanup, and distinct pool stability tracks.
- `session/environment.json`: blank run environment template, with explicit role and timebase distinctions.
- `metrics.csv`: metric definitions and blank per-client/run observation slots. Add one observation row per client, metric and run; collect raw samples externally/evidentially and identify their evidence path.
- `report_template.md`: evidence funnel, reproduction, recovery, limitations, and decisions.

Percentiles are only meaningful for a sufficiently sampled, well-defined per-client population. Report the raw sample count, sampling interval, warm-up exclusion and outlier policy. Do not calculate or advertise P50/P95/P99 from a handful of runs, across pooled clients, across mixed phases, or from derived demo values. This package sets **no TPS, frame-time, network-rate, queue-depth, memory, or latency acceptance threshold**. Do not claim host algorithm demonstrations are runtime measurements.

## Run and safety boundaries

Use actual Unity/VRChat test clients only after the user owns and authorizes that session. Do not start Unity as part of this offline preparation. If a human VR test is later planned, follow the existing [P08 human safety and stop conditions](../InputExperiments/human_safety.md): participation is voluntary; stop immediately on any discomfort, instability, inability to exit/restore, or participant request. No retry without the participant's affirmative choice.

A Master/InstanceOwner/Owner transition is not proof that the match's committed business snapshot was recovered. Test and report snapshot revision/completeness, commit sequence, authority epoch, unresolved requests and final ledger independently. If a complete consistent snapshot is unavailable, document cancellation/return-to-lobby behavior; do not infer or invent business state.

## Offline template structure check

Run `python3 Docs/Preparation/RuntimeEvidence/validate_templates.py`. It uses Python standard library only to validate CSV row shape/schemaVersion, status-dependent placeholders, and environment JSON with non-finite constants/nested values rejected. While a template remains `NOT_RUN` / `TEMPLATE_NOT_RUN`, run/build/network/hardware/authority/collection observations and metric sampling interval/sample count/raw evidence/percentiles must remain blank, null, or `unknown`. The `warmup_policy` CSV column is static methodology guidance (not an observed warm-up duration/count), so it must match the approved template text; measured warm-up exclusions belong only in a filled report. Small built-in negative probes verify the guardrails reject representative prefilled values. Its `PASS` means template integrity only; it provides no runtime, performance, networking, comfort, or Unity/Udon evidence.
