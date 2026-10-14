# Size-model research and evidence (DRAFT)

Research date: 2026-10-14. Scope is public-source review, not reverse engineering.

## Source availability

The original Katamari game source was not obtained. The local repository contains no original source, verified decompilation or executable implementation evidence. No private material or executable code was accessed. Historical patent US7402104B2 is not source code and does not prove that the 2025 game retained its method.

## Relevant evidence

- Project contract: [`Docs/02_TECHNICAL_CONTRACTS.md`](../../../../02_TECHNICAL_CONTRACTS.md) §§1–3 distinguishes core radius, collision radius, semantic diameter and confirmed volume. This is the project's design contract, not original-game evidence.
- Prior local draft and handoff: [`size_model.py`](../size_model.py) and [`SuiteFoundation/HANDOFF.md`](../../Attachment/SizeModel/SuiteFoundation/HANDOFF.md) preserve equivalent-sphere volume mapping while identifying overflow risk and display-cap/ledger separation.
- Historical patent: [US7402104B2](https://patents.google.com/patent/US7402104B2/en), filed 2004-09-20, granted 2008-07-22. It discusses a historical rolling/shape representation. It does not establish the 2025 game's source, volume-to-radius mapping, confirmed ledger values, or size cap.

## Actual access record

On 2026-10-14, a local Python standard-library `urllib.request` call fetched the public Google Patents URL above; HTTP status 200, 4096 response bytes read for a bounded connectivity/content check, and the returned prefix contained the word `radial`. This was not a full page/text audit or legal analysis. Original-game source was not obtained.

## Project-specific choice

T52 maps an upstream confirmed volume and explicit caller parameters to three separately typed presentation outputs. Log-domain intermediate math avoids overflowing the naïve `volume * scale`, `radius ** 3` and `base + confirmed` operations. The radius cap affects only mapped presentation; the authority ledger remains external and unchanged. All parameters remain provisional until T06/experience gates.
