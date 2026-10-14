# Attachment research and evidence (DRAFT)

Research date: 2026-10-14. Scope is public-source review, not reverse engineering.

## Source availability

The original Katamari game source was not obtained. The repository contains no original source, verified decompilation or executable implementation evidence, and this work did not access private materials or download/execute code. US7402104B2 is a historical patent document, not the game's source code and not evidence that *Once Upon A KATAMARI* uses the same implementation.

## Relevant evidence

- Project contract: [`Docs/02_TECHNICAL_CONTRACTS.md`](../../../../02_TECHNICAL_CONTRACTS.md) §§2–3. It specifies local attachment pose, object scale independent of core growth, and stable summary identity as project design. It is not original-game evidence.
- Prior local draft and handoff: [`attachment.py`](../attachment.py) and [`SuiteFoundation/HANDOFF.md`](../../SizeModel/SuiteFoundation/HANDOFF.md). The older implementation assigned a local incrementing sequence and mixed renderer retention into the record store; the new code changes those responsibilities.
- Historical patent: [US7402104B2](https://patents.google.com/patent/US7402104B2/en), filed 2004-09-20, granted 2008-07-22. It describes a technique involving radial displacement points in its claim/description. This is background only; it does not document actual game source, confirmed pickup identities, attachment pose lifecycle, or the 2025 game's internals.

## Actual access record

On 2026-10-14, a local Python standard-library `urllib.request` call fetched the public Google Patents URL above; HTTP status 200, 4096 response bytes read for a bounded connectivity/content check, and the returned prefix contained the word `radial`. This check does not claim a full text audit or legal analysis. No original-game source was obtained.

## Project-specific choice

T51 consumes caller-supplied context/ball/item identity, generation, authoritative stable attach sequence, confirmation event ID and poses. Those are project draft semantics, not claims about the original game. Transaction identity (including context and event ID) is encoded separately from the remote proxy key. The latter hashes only the four §3.4 fields through a narrow caller-supplied `ball_seed` function; the seed's source/value semantics remain OPEN for upstream/Lead decision. Neither digest is authentication or a proof of zero collision risk. T51 stores complete identity and local rigid pose only; renderer budget, hiding, pooling and recycling belong to T56. Nonuniform parent scale/shear is outside this transform model and must be normalized by an adapter or rejected.
