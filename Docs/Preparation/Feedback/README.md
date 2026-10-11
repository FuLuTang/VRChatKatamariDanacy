# P07 feedback event catalogue — planning draft

This folder is a **text-only preproduction proposal** for T23 (layered sound/effects/haptics) and T24 (cosmetic slots/preview). It is neither a frozen event contract nor an implementation/performance result. [events.csv](events.csv) uses illustrative event IDs for review; they are not Udon event names, SDK APIs, or approved wire protocol. T06 is still a prerequisite for public interfaces. No audio, VFX, UI art, thumbnails, scripts, or new third-party files were created or downloaded.

## Inputs and design constraints

- [Game design](../../01_GAME_DESIGN.md): sections 2–8 describe deposit/KO/respawn, A/B/Desktop and Jump rules, magnet/rocket, lobby/round phases, audio priority, low-effects and comfort constraints.
- [Technical contracts](../../02_TECHNICAL_CONTRACTS.md): confirmed ledger vs local prediction, idempotent claims/bank requests, world state and remote summaries, plus bounded pooling and no per-item Update.
- [Acceptance evidence](../../05_ACCEPTANCE_TESTS.md): G1 requires no forced camera movement and configurable/off low effects; G3 requires one authoritative winner; G5 requires cosmetic effects not to alter gameplay or cover warnings; G6 still needs actual profiling.
- [P07/T23/T24 planning](../../14_OFFLINE_PREPARATION_PLAN.md) and [task contracts](../../../tasks.json): only a preparation draft; T23/T24 remain gated by their dependencies.

## Reading `events.csv`

Each row is an example state/feedback trigger with: example ID, trigger, local/remote audience, priority, confirmation requirement, audio/visual/haptic proposals, coalescing key/rule, provisional pool allocation, low-FX must-retain state, and future asset-license needs.

- **Authority and rewards:** local predicted pickup is presentation-only. No reward tone, success flourish, confirmed score/size, or remote pickup event before the unique ledger result. Rejection retracts the prediction and never plays a reward cue. Only confirmed state transitions can imply award, deposit, KO, respawn, round start or result.
- **Local/remote:** local player can receive close-range/material detail; remote players receive only nearby/significant summaries. No global audio announcements or per-item network effect events are implied. `local/remote` fields are audience proposals, not a networking API.
- **Priorities:** P0 = safety/fairness/phase/result truth; P1 = confirmed core gameplay; P2 = useful/local detail; P3 = ambience/decor. These are relative review labels, not a final scheduler contract.
- **Coalescing:** deduplicate by stable transaction/item-generation/state revision where available. It may combine presentation, not change or discard distinct ledger outcomes. Stale predictions and effects are canceled when their source state changes.

## Provisional resource budgets (not measured)

Starting proposal for later profiling: per-client transient audio pool of at most **8 voices** (reserve 2 for rules/threat, 4 for gameplay, 1 UI, 1 ambience); transient visual-effects pool of at most **12 groups** (reserve 4 for critical state feedback, 6 gameplay, 2 ambience). Persistent HUD fallback is also bounded: propose at most **6 fixed status regions** per local player plus **1 short-notice slot**; state is updated in fixed regions rather than allocating a widget per event. Steal/drop order is ambience → cosmetic/decorative → lower-priority gameplay detail. P0 state must still be communicated by retained readable UI even if its sound/effect voice cannot play. Coalesce small pickup accents over a proposed 150 ms grouping window; cap mix level and duck ambience so four players cannot stack sound without bound. These counts are deliberately conservative **tuning proposals** only: no profile, build, frame-time or network evidence exists.

Haptic proposal: optional, user-disableable, only for meaningful confirmed events; no haptics for pickup predictions or rejection. If later enabled, rate-limit to at most one short pattern per player every 600 ms and suppress low-priority pulses before warning/state UI. Actual controller/haptic API and device support are unknown. Always provide an independent haptic intensity/off setting; low-FX/mute options must not remove rule information.

## Low-effects and fairness invariants

See [low_fx_rules.md](low_fx_rules.md). Lowering effects may remove particles, trails, screen-space decoration, spatialized ambience, nonessential one-shots, and all haptics. It must not change collision, pickup eligibility, ledger confirmation, score, timing, power duration, threat/KO/respawn decisions, spawn generation, winner, or item visibility/occlusion rules. It must not hide or disable collectible entities, gameplay collision proxies, or render/state cues opponents need to judge eligibility/threat/status; no Collider debug drawing is required. Pool-full fallback uses the bounded 6-region + 1-notice HUD proposal, never an unbounded event queue. Warning shape+direction+text and current state remain available; color is never the only channel. Paid or optional cosmetics never cover warnings, bank state, score, timer, opponent readability, or controls.

## Asset and licensing boundary

The CSV says what a future asset may need; it does not identify or clear any file. For each eventual sound, font, icon, texture, particle texture, material, or animation, record exact creator/source URL, file/version and hash, license text/path, commercial use, attribution text/requirement, modification terms, and whether embedding/redistribution in a paid VRChat world is permitted. Review brand/trademark/likeness and third-party material separately; page labels alone are not sufficient. Prefer original project-authored motifs, do not use Katamari source audio/music/characters, and keep optional third-party decorative effects out until review. No project license is assigned here.

## Review gates / not tested

Review the examples against T06's future public contracts before converting event IDs/state triggers into code. T23 must profile/test source priority, repeated pickups, pool overflow and four-player overlap. Required compound-sequence checks include magnet partial win/loss, bank commit plus growth reset, KO plus respawn protection, deadline plus pending request, and duplicate snapshot; coalescing must not alter ledger order/results. Exit/reset cleanup must clear inputs, loops, trails, timers, haptics, predictions, subscriptions and transient references idempotently without changing confirmed ledger. T24 must test cosmetic switching across sizes and prove it cannot obscure threat/protection/status information or imply official entitlement. Low-FX, mute and haptic-off require both presentation checks and gameplay-equivalence checks, including collectible/proxy visibility and bounded HUD fallback. This document performs none of those tests; Unity and Blender were not run.
