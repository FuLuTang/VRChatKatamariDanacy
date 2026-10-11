# P18 handoff — runtime evidence preparation

- Work package: P18 / shared task reference `task-23`.
- Write scope used: `Docs/Preparation/RuntimeEvidence/` only.
- Delivered: `README.md`, `cases.csv`, `session/environment.json`, `metrics.csv`, `report_template.md`, `validate_templates.py`, this handoff.
- Case inventory: 25 cases, all seeded `NOT_RUN`; intended coverage includes authority/epoch, late join, stale epochs, retries/reordering, latency/loss/congestion, cutoff, bank/KO races, input cleanup, distinct visual and transaction pool 20-round checks, combined four-client soak, and disconnect/retry. `observed` and `evidence` columns are blank throughout.
- Environment template: one 2–4-client scaffold (client labels 1–4); actual Unity editor/build, VRChat World SDK/Base SDK/UdonSharp builds, hardware, headset/roles, network, and timebase values remain `null`/`unknown`. Owner, Master, InstanceOwner and player-object Owner are separate fields.
- Percentiles: per-client/per-metric/per-phase samples; nearest-rank `ceil(p*n)`, sample count/interval, declared warm-up and outlier handling required; at least 1,000 retained observations before publishing a percentile summary. This is a reporting adequacy rule, not a runtime threshold. No TPS/FPS/frame-time/network/queue budget is set. No host demo/runtime sample supplied.
- Safety and recovery limits: linked P08 stop rules. Authority-owner migration alone does not establish committed business snapshot recovery; snapshot completeness/revision/commit sequence/epoch must be independently evidenced, otherwise explicitly report recovery NOT ESTABLISHED or actual cancellation fallback.

## Reproducible checks run

1. `python3 Docs/Preparation/RuntimeEvidence/validate_templates.py`: **PASS** — cases.csv 25 rows/12 columns (all `NOT_RUN`, observed/evidence empty); metrics.csv 8 rows/16 columns (runtime interval/count/raw evidence/percentiles blank; static warm-up policy text unchanged); environment JSON schemaVersion 1 with four blank client slots and run/build/network/hardware/authority/collection observations null/unknown. Built-in negative self-checks reject representative populated environment and metric observations, substituted warm-up measurement text, JSON NaN/Infinity/-Infinity, and nested non-finite numbers. This is host-only template validation, not runtime evidence.
2. `python3 -m unittest discover -s Docs/tests -v`: **NOT_RUN** for this handoff; no code or planning configuration changed and the check is not required to establish these templates.
3. Unity / UdonSharp compile / ClientSim / VRChat multi-client / Owner migration / late join / congestion / headset comfort / 20-round runtime checks: **NOT_RUN**. No runtime measurements, logs, screenshots, or sample run are claimed.

## Limitations / handoff

These templates are not runtime implementation, SDK evidence, a frozen Udon contract, or proof of correctness/performance. The 1,000-sample policy does not guarantee statistical representativeness; raw sample provenance and per-client conditions remain essential. Hardware/SDK/client-specific measurement methods must be selected and documented by the eventual test operator. `task-23` template-preparation work was independently reviewed by offline-quality and accepted; this does not mark runtime execution, P18, or T27/T28/T29/T39 complete.
