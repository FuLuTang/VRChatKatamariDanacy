# P07 audio/VFX/haptics and low-effects draft — handoff

## Deliverables

- [`events.csv`](events.csv): 30 illustrative feedback/state rows with trigger, local/remote audience, priority, confirmation requirement, audio/visual/haptic proposals, coalescing, provisional pool budget, low-FX retention and future license needs.
- [`README.md`](README.md): scope, source contracts, event semantics, proposed unmeasured budgets, asset/license boundary, review gates.
- [`low_fx_rules.md`](low_fx_rules.md): information/fairness invariants and future review prompts for low-effects/mute/haptics-off.

## Key safety/design points

- `collect.predicted` has no reward audio, haptics, confirmed score, or remote victory cue. `collect.confirmed` requires the authority's unique committed result. `collect.rejected` retracts the prediction without a reward cue.
- Proposals cover confirmed growth/burial, magnet/rocket, threat/protection, collision/KO/respawn, bank pending/rejected/committed, countdown/cancel/start/deadline/settling/results/reset, lobby membership/exit and cosmetic preview.
- Low-FX removes/reduces decoration only; it does not hide/deactivate collectible entities or gameplay collision proxies, or hide render/state cues needed for opponents to judge eligibility/threat/status. Collider debug rendering is not required. Critical statuses persist in a bounded proposal (6 fixed HUD regions + 1 transient notice), not an unbounded event queue; cosmetics cannot mask critical information.
- Future compound test matrix explicitly includes magnet partial win/loss, bank commit + growth reset, KO + respawn protection, deadline + pending request, duplicate snapshot, and repeated exit/reset cleanup. Cleanup must be idempotent, clear inputs/loops/trails/timers/haptics/predictions/subscriptions/transient references, and preserve confirmed ledger. These are review assertions, not tests run.
- Audio pool, VFX pool, coalescing window and optional haptic-rate numbers are proposed tuning starts only; no profiling or device support is claimed. Haptics must be independently disableable and must not carry unique rule information.
- File contains examples, not API names or a frozen event contract. No sound, art, UI, scripts or other asset was generated/downloaded; no Unity/Blender action or runtime test occurred. Third-party license fields are a future per-file checklist, not authorization of any content.

## Inputs reviewed

Current game design, technical contracts, acceptance evidence, T23/T24 task cards and the offline preparation plan were reviewed. T23/T24 remain dependent/gated; public interfaces are not frozen until T06. No shared/public document, task config, Unity project, or another teammate's directory was modified.

## Environment and restrictions

- Unity runs on Windows; the user owns/operates the Editor.
- Confirmed project root: `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity` (repo root `D:\VRChatKatamariDanacy`). Unity MCP is unconfirmed.
- This task used no SSH, Unity MCP, Blender, Unity, account, payment, publication, Git commit or push.
- Write scope used only: `Docs/Preparation/Feedback/`.
- As of handoff, proposals/tests are NOT_RUN and require independent review before implementation.
