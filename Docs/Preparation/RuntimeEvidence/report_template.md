# Runtime evidence report (P18 template)

> Copy per actual session. Template is not evidence. Do not mark results from expected behavior or offline models as runtime `PASS`.

## Run identity and gate

- schemaVersion: 1
- run_id:
- date/time + timezone:
- test build / world build / ruleset:
- Unity editor version:
- VRChat Worlds SDK / Base SDK / UdonSharp versions as observed:
- clients (pseudonymous labels; 2–4):
- cases.csv case IDs / statuses:
- environment.json evidence copy:
- observer / collector tools and versions:
- scope: ☐ Unity compile ☐ ClientSim ☐ VRChat multi-client ☐ headset human test ☐ other (describe)
- Status: `NOT_RUN` until actual evidence exists. Each gate is reported separately.

## Environment and identity map

Attach completed environment.json and explain any `unknown` / nulls. Distinguish explicitly:

| Client label | Match authority object Owner | Player object Owner | VRChat Master | InstanceOwner | Effective interval / evidence |
|---|---|---|---|---|---|
| | | | | | |

- Timebase for each log/source; wall-clock vs monotonic mapping and uncertainty:
- Network topology/region/access and fault injection method:
- Hardware/OS/CPU/GPU/RAM/display/headset/refresh rate per client:
- Avatar condition, resolution, relevant render settings:
- Consent/safety steps and any stop or withdrawal (no personal/health details beyond what is needed):

## Reproduction protocol

1. State exact build/config and starting state; include a sanitized setup path, not private URLs or identifiers.
2. List clients, roles, joining order, initial authority epoch, complete snapshot revision and commitSequence (if observable).
3. Describe one variable changed per fault case and how it was applied/removed. Record actual timing source and units.
4. Execute case IDs in order; log send/receive/ack/retry, request identity, epoch, phase, snapshot chunks/revision and ledger before/after as applicable.
5. Capture raw per-client frame/network/memory/object samples at the declared intervals; preserve raw data and identify evidence paths.
6. Repeat only according to predeclared repetition count and stop conditions. Do not fill missing values with zero or infer from another client.

## Results and evidence funnel

| Stage | Count / bytes / measure | Evidence path + method | Notes / excluded scope |
|---|---:|---|---|
| Before: scene/world objects (by relevant type) | | | |
| Before: visual pool active / inactive / in-use | | | |
| Before: transaction queue pending / acknowledged / canceled | | | |
| Before: process/runtime memory metric | | | |
| Before: network queue/serialization observations | | | |
| After setup/join | | | |
| After each gameplay round (attach per-round table) | | | |
| After cleanup/round reset | | | |
| After final round and settling interval | | | |

Keep visual/attachment/render pools and transaction/request pools as separate inventories. Describe object identities/counting method; pooled inactive objects are not automatically leaks, and zero pending transactions does not prove the visual pool is clean.

## 20-round stability (required separate tracks)

Attach all 20 round-boundary observations, including initial baseline and post-cleanup state. Do not report only first/last or a single aggregate.

- Visual pool: initial count; per-round created/reused/returned/active/inactive/in-use; residual objects and reconciliation:
- Transaction pool/queue: initial pending work; per-round submitted/acked/retried/canceled/expired; residual work and ledger revision reconciliation:
- Per-client memory method and raw samples:
- Missing round observations / interruptions:
- Result is descriptive only; do not assert stable/bounded without a declared rule and supporting observations.

## Percentile calculation and performance record

For each client and named phase/metric, report sampling interval, warm-up duration/excluded sample count, raw retained `sample_count`, raw-data path, tool/method, outlier policy, and P50/P95/P99 only when sampling is adequate. Use nearest-rank: sort the `n` retained observations ascending; percentile `p` is element `ceil(p*n)` using one-based indexing (P50 `.50`, P95 `.95`, P99 `.99`). Record method/version. Preserve raw data and report missing/invalid samples. As a preparation policy, require at least 1,000 valid retained observations for any percentile summary; otherwise leave percentile cells blank and state insufficient samples. This count is a reporting rule, not a frame/network budget or runtime target.

Warm-up must be identified before measurement; report exclusions and do not silently remove spikes. Primary summaries retain outliers. If a separately justified instrumentation-invalid sample is excluded, keep it in raw evidence and disclose the reason/count. Never pool clients, phases, hardware, or distinct runs into one percentile. No fixed TPS/frame/network threshold is defined here.

| Client | Metric + phase | Interval | Warm-up excluded | sample_count retained | P50 | P95 | P99 | Raw evidence path |
|---|---|---:|---:|---:|---:|---:|---:|---|
| | | | | | | | | |

No host-generated percentile demonstration or synthetic sample is a runtime measurement. Do not copy demo numbers into this report.

## Fault, recovery, and integrity findings

For every failed/interrupted/blocked case, include exact reproduction steps, initial state, action, observed sequence, expected invariant, final state and evidence. Describe recovery actually observed and what remained unresolved.

- Owner/authority transition: role changes and authorityEpoch:
- Snapshot revision, completeness and commitSequence before/after:
- Pending requests, retries and deduplication outcomes:
- Late join behavior and partial snapshot behavior:
- Congestion/queueing and client divergence:
- Cutoff, bank/KO race ordering and ledger reconciliation:
- Input/Station/settings cleanup and user exit/Respawn path:
- Recovery action; any canceled round / lobby fallback:

**Important:** Owner, Master, or InstanceOwner migration is not proof that committed business state recovered. If no complete consistent snapshot is demonstrated, state `business snapshot recovery NOT ESTABLISHED`; do not claim recovered scores/items. Record cancellation/fallback if observed.

## Safety, limitations, and disposition

- Participant-controlled stop/exit available and verified:
- Any stop, discomfort report, or recovery failure: stop test; do not resume without participant choice. Follow [P08 human safety rules](../InputExperiments/human_safety.md).
- Limitations and unmeasured clients/devices/network conditions:
- Unity/Udon compile status and separate evidence:
- ClientSim status and separate evidence:
- Real multi-client/network status and separate evidence:
- Human VR comfort status:
- Defects / severity / owner / next reproducible action:
- Release/gate implication (no auto-pass):
