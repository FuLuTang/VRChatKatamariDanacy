# P02 direct-download and package-license pilot — handoff

## Result

- Verified official Kenney pages and unauthenticated direct archive routes for Food Kit 2.0 and Nature Kit 2.1; downloaded exactly two small archives (15,143,791 bytes combined; below the 150 MB ceiling).
- Official source pages and each included package `License.txt` separately state CC0. Both package notices expressly allow commercial use and say Kenney credit is optional. This is package-level evidence only; it is **not** final T02 authorization acceptance or approval of any individual asset in the paid-world context.
- Archive SHA-256 values, source-page facts, archive-license facts, source/redistribution caveats, and 36 concept mappings are in [`manifest.json`](manifest.json). Web routes and limitations are in [`routes.md`](routes.md). Verbatim package notices are in [`kenney_food-kit_License.txt`](kenney_food-kit_License.txt) and [`kenney_nature-kit_License.txt`](kenney_nature-kit_License.txt). Complete ZIP-member metadata inventory is [`inventory.csv`](inventory.csv).
- No Blender/Unity, archive installer, account, payment, or bundled executable/script was used. No asset files were extracted, rendered, imported, or placed in the Unity project. Only the two License.txt notices were read/copied as evidence. The approved scanner was metadata-only and reported no extraction. A distinct, bounded `zipfile.testzip()` check read/decompressed member payload streams in memory to verify CRC; it wrote no archive members to disk and is not a safe-extraction guarantee. The detailed route separates scanner and CRC limits. Inventory paths/metadata show no symlinks/traversal/absolute paths under the described checks. HTML and URL metadata were inventoried but not opened or executed.
- Date bookkeeping: sample date is 2026-10-10 UTC; local date 2026-10-11. Only date-level session evidence is available, not a separately recorded local clock time.
- Candidate-to-file mappings are based on exact archive filenames only. Some proposals remain `unknown`; available filenames do not establish shape/visual identity, dimensions, mesh/material quality, scale, collider suitability, or per-file third-party clearance.

## Storage and scope

- Downloaded binaries are under `Docs/AssetIntake/Downloads/`, confirmed ignored by repository `.gitignore`; evidence deliverables are under `Docs/AssetIntake/DirectDownload/` (this task's write scope). `Downloads/` is not to be committed or copied into the Unity project. No tracked project file or original candidate CSV/license-gap record was changed.
- No content was placed in `VRChatKatamariDanacyUnity/`. No SSH or Unity MCP call was made; Unity MCP remains unconfirmed.

## Environment / restrictions

- Unity runs on Windows; the user owns and operates the Editor.
- Confirmed Unity project path: `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity` (repository root `D:\VRChatKatamariDanacy`).
- No Unity, Blender, account, payment, publication, commit, or push operation occurred.
- This is the P02 small access trial; not batch asset intake and not full T02 acceptance. Final human review is still needed for asset appearance/suitability, exact distribution context, third-party marks/rights, and release inclusion.
