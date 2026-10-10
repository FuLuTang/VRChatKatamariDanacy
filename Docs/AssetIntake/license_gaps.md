# Picnic asset prescreen — license gaps

Status: **public-page prescreen only; no asset has passed intake or authorization acceptance.** No files were downloaded, unpacked, hashed, previewed, or imported. Accessed 2026-10-10 (UTC, session sample; date only).

## Public page evidence

- [Kenney Food Kit](https://kenney.nl/assets/food-kit): page identifies 3D, 200 files, and Creative Commons CC0; page links the CC0 legal code and offers a download. Page claim recorded, not package-verified.
- [Kenney Furniture Kit](https://kenney.nl/assets/furniture-kit): page identifies 3D, 140 files, and Creative Commons CC0; page links the CC0 legal code. Page claim recorded, not package-verified.
- [Kenney Nature Kit](https://kenney.nl/assets/nature-kit): page identifies 3D, 330 files, and Creative Commons CC0; page links the CC0 legal code. Page claim recorded, not package-verified.
- [Quaternius Ultimate Food Pack](https://quaternius.com/packs/ultimatefood.html): page says 103 models, offers FBX/OBJ/Blend formats, links CC0, and describes 100+ food/consumable models as free for personal and commercial projects. Page claim recorded, not package-verified.
- [CC0 1.0 legal code](https://creativecommons.org/publicdomain/zero/1.0/legalcode.en): states the waiver/fallback includes commercial purposes; §4 limits effect on trademark/patent and disclaims warranties/right clearance, including third-party rights. CC0 claim alone does not establish rights in every depicted element.

## Must remain unknown until a specific package is lawfully obtained and inspected

| Field | Current state | Required evidence before intake acceptance |
|---|---|---|
| Exact archive/download version and retrieval date | unknown (page inspected only) | Record selected download and date; retain source snapshot/URL |
| Package-included LICENSE and exact text/path | unknown | Inspect archive contents; preserve verbatim license and provenance |
| SHA-256 | unknown | Hash the exact retained archive and record algorithm/value |
| Whether each CSV concept exists as a discrete model/file | unknown | Inspect package inventory; map object to exact filename |
| Per-file license exceptions, bundled third-party content, trademarks/likeness | unknown | Review package notices and individual assets; quarantine uncertain items |
| Attribution requirement/text | unknown from package; do not infer a requirement from CC0 alone | Check package documentation and asset-specific notices; record exact rule/text |
| Commercial use and redistribution of the exact files in this paid-world context | unaccepted | Review package license and applicable platform distribution format against intended use |
| File format/mesh health/topology/texture/materials/collider suitability | unknown | Inspect selected files in isolated asset-review workflow; do not import into UnityProject |

## Decision

Treat all listed packs as **candidates only**. Keep assets out of the production asset directory until package-level evidence is archived and reviewed. Do not claim commercial authorization passed based only on a marketing/page label. No download/import/preview was performed in this pass.
