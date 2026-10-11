# Low-effects mode: fairness and information invariants

Status: proposal for T23/T24 review; **not implemented or tested**. Low-effects is a presentation preference only. It must not alter gameplay inputs, collision, timing, authority, resource availability, ledger outcomes, or match rules.

## Must remain legible in every effect mode

1. **Claim truth:** predicted, pending, confirmed, and rejected pickups/deposits remain distinguishable. Only a confirmed ledger event may increment displayed confirmed value or be presented as a reward. Rejection must retract the pending representation without a success cue.
2. **Threat/safety:** retain a clear opponent/threat direction and status, protection active/expired status, and KO/life/respawn state. Use shape + text/icon + spatial/position cue where practical; color alone, a brief flash, an audio cue, or vibration alone is insufficient. No cosmetic/trail/preview layer may occlude or recolor these states into ambiguity.
3. **Round truth:** retain phase (lobby/countdown/preparing/playing/settling/results/reset/recovery when applicable), remaining-time/cutoff, roster/queue/lock/cancel outcome, and final committed score/rank/tie. Settling must not appear to accept new rewards; result celebration begins only after committed results.
4. **Powerups:** retain held/active/empty state and applicable duration/availability status. Magnet/rocket particles can be suppressed, but active state and matchup-relevant signaling remain understandable to opponents.
5. **Banking and recovery:** active bank target, request pending/rejected/committed status, last committed total, safe exit/recovery state and controls-return status remain accessible. A successful deposit animation is optional; correctness is not.
6. **Input and exit:** Jump/controls help and the explicit exit route remain usable; no full-screen effect or transition can trap/block the user. Do not infer a specific controller button label.
7. **Gameplay equivalence and world visibility:** low-FX/mute/haptic-off cannot change object spawn/collision, pickup checks, confirmed score, event timestamps/cutoff, power duration, or opponent-relevant visibility. It must not hide, disable, or remove collectible entities, their gameplay collision proxies, or render/state cues needed for opponents to judge pickup eligibility, threat, protection, KO, bank or cutoff. Keep the same gameplay-relevant object/render representation across effect modes. This does **not** require drawing debug collider geometry; do not make debug colliders visible just to satisfy this rule. Remote effects remain decoration, not authority.

## Bounded persistent HUD fallback

If transient audio/VFX/haptic pools are full, fairness information falls back to ordinary fixed HUD/world-state widgets, not an unbounded queue or per-event widget allocation. Proposal for later implementation review: per local player use a fixed set of at most **6 persistent status regions** (round/time/roster; confirmed score + unbanked growth; life/protection/powerup; bank/transaction; threat direction/status; controls/queue/exit) plus at most **1 short transient notice**. These counts are a bounded layout proposal, not measured. Each region displays latest authoritative state; it is not allocated once per event. Coalesce equivalent notices into the one notice slot; do not evict or replace a still-relevant P0 state with ambient/reward text. If exact text cannot fit, retain the compact labeled state/icon within its fixed region and an accessible summary; never hide the underlying world entity/proxy or downgrade its collision/visibility behavior.

## May be reduced or removed

- Ambient particles, dust, leaves, trails and looping fields (subject to keeping a separate persistent powerup-state marker when needed for counterplay).
- Nonessential camera/screen effects (camera roll/shake is not allowed in the baseline at all), bloom/flash, ornamental score particles, victory bursts, decorative previews and background motion.
- Lower-priority spatialized ambience, repeated small pickup one-shots, nonessential haptic pulses, duplicated cues and stale/predicted event flourishes.
- Effects count, duration and opacity may be reduced; do not shorten or hide a warning before its rule-relevant state expires.

Audio, SFX, ambience and haptics need independent controls. Haptics must be able to be fully disabled. Accessibility and critical-rule state must not depend on hearing, color discrimination, high refresh rate, or precise timing of a short particle.

## Priority / overload fallback

Use the proposal labels from `events.csv`: P0 critical safety/fairness/phase truth > P1 confirmed gameplay > P2 useful local detail > P3 ambience/decor. These are not final code priorities. When a transient pool is exhausted:

1. Drop ambience/decor and duplicate/repeated cues first.
2. Coalesce only equivalent presentation cues with a stable source key; preserve distinct ledger/state outcomes.
3. Do not queue a stale effect until it becomes misleading. Cancel it when the source state is rejected, superseded or reset.
4. Keep critical state in persistent readable HUD/world indicators even if audio, particle or haptic output is unavailable.
5. Never let optional cosmetics, paid effects or high-priority local reward sound mask opponent threat/protection/bank/cutoff status.

## Proposed starting caps for later profiling

These values are **unmeasured pool proposals**, not a hardware guarantee or pass threshold:

- Per client: up to 8 transient audio voices; reserve 2 for critical rules/threat, 4 for core gameplay, 1 for UI, 1 for ambience.
- Per client: up to 12 transient visual-effect groups; reserve 4 for critical state feedback, 6 for gameplay, 2 for ambience. Persistent HUD/world state markers use the separate fixed-region fallback above (6 status regions + 1 notice proposal) and still need actual layout/readability testing.
- Merge small pickup accents in a proposed 150 ms presentation window; cap/duck summed mix. Authority and commit order remain untouched by coalescing.
- Optional haptic: no prediction/rejection pulses; one short meaningful confirmed pattern at most once per 600 ms per player, user intensity/off setting available. Device capabilities and API are unknown, so disable or no-op safely if unsupported.

Actual counts, mix behavior, readability and resource costs require T23/T24 implementation, PC/VR devices and the T38 performance conditions; no such evidence is present here.

## Acceptance prompts for future tests

- Replay the same committed pickup/bank/KO/round sequence at normal, low-FX, muted, and haptic-off settings; confirmed ledger, timing and rank must be identical.
- Compete with multiple players: verify one player's prediction/rejection never appears as another player's confirmed gain; simultaneous confirmed small pickups must not drown threat warnings.
- Trigger threat/protection/cutoff while cosmetic previews/trails/results are visible; every state must remain distinguishable without color alone.
- Join late, lose readiness, cancel countdown, enter settling, recover/reset and exit; low-FX must not remove phase/cancellation/exit truth.
- Check audio, visuals and vibration independently; test actual devices only when available. If not tested, leave status NOT_RUN.

## Compound-sequence review matrix (future tests; all NOT_RUN)

| Sequence | Required state/ledger assertion | Presentation/cleanup assertion |
|---|---|---|
| Magnet partial win/loss | Resolve a batch where some items commit to this player and others are rejected/won by another. Each `(round, item, generation)` has at most one confirmed winner; only confirmed gains reach growth/score. | Confirmed and rejected items remain distinguishable locally/remotely as rules require; rejected predictions retract without reward cue; coalescing never changes the result. |
| Bank commit + growth reset | A single committed bank transaction adds only eligible unbanked value to the safe score and applies the specified growth reset exactly once. | On duplicate response/retry, no duplicate deposit animation/sound; confirmed total remains truthful and any remaining/new growth is represented per state. |
| KO + respawn protection | One committed KO transaction applies the configured loss/attacker award once; subsequent safe respawn protection follows actual committed state, without assuming specific duration here. | Clear stale attack/rocket/magnet loops and KO transient presentation; retain protection/life state and eligibility cue, even with low-FX or haptics off. |
| Deadline + pending claim/deposit | At cutoff, resolve pending requests only by the approved authority timestamp/settling policy; no late or speculative gain is silently promoted. Final score waits for valid committed outcomes or explicit recovery/cancel. | Keep pending vs committed/rejected visible; do not play winner/deposit success cues from pending status; no post-deadline repeated prompt alters the transaction. |
| Duplicate snapshot/state replay | Reapplying the same state revision is idempotent: confirmed ledger, life, phase and roster do not double-advance. | No duplicate one-shots, timers, trails, haptics, transient notices, event subscriptions or pooled references; current UI derives from latest state. |
| Exit/reset, including repeated cleanup | Cleanup is idempotent and does not mutate, invent or erase already-confirmed ledger results. Any next-round ledger reset is an explicit round transition/generation, not a side effect of presentation cleanup. | Clear held input/drive, looping audio, trails/particles, duration timers, haptic requests, predicted pickups, callbacks/subscriptions and transient object/reference/pool ownership; preserve the explicit exit path and show resulting lobby/reset state. Repeated exit/reset/respawn cleanup leaves no new effects or residual control. |

These are deterministic review assertions, not an implementation plan or claimed test pass. They should be exercised at normal and reduced-effects settings, including overlapping multi-player events and pool saturation.
