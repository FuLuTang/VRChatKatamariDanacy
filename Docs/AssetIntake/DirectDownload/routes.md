# P02 direct-download trial — routes and evidence

Checked: 2026-10-10 UTC; local date 2026-10-11 (date-level session evidence; not a separately recorded clock time). This is a two-package access/safety pilot, not a bulk intake or T02 acceptance.

## Selected packages and reproducible routes

| Package | Official source/page | Free direct route (no login/donation) | Result |
|---|---|---|---|
| Kenney Food Kit 2.0 | https://kenney.nl/assets/food-kit | https://kenney.nl/media/pages/assets/food-kit/83086fa91c-1719418518/kenney_food-kit.zip | HTTP 200; `application/zip`; Content-Length 4,606,270 bytes. Official page labels the 3D pack CC0 and exposes “Continue without donating” to this URL. |
| Kenney Nature Kit 2.1 | https://kenney.nl/assets/nature-kit | https://kenney.nl/media/pages/assets/nature-kit/37ac38a37b-1677698939/kenney_nature-kit.zip | HTTP 200; `application/zip`; Content-Length 10,537,521 bytes. Official page labels the 3D pack CC0 and exposes “Continue without donating” to this URL. |

Download route verification used ordinary unauthenticated HTTP HEAD/redirect checks before retrieval; both archives were then fetched directly with a 150,000,000-byte cap into the ignored `Docs/AssetIntake/Downloads/` directory. Combined archive size: 15,143,791 bytes (~14.44 MiB), well under the 150 MB ceiling. SHA-256 and exact byte counts are in `manifest.json`.

## License evidence

- Official package pages: [Food Kit](https://kenney.nl/assets/food-kit) and [Nature Kit](https://kenney.nl/assets/nature-kit), each labels the pack “Creative Commons CC0” and links https://creativecommons.org/publicdomain/zero/1.0/.
- The archive-included verbatim notices were read from within each ZIP without extracting the archive contents: [`kenney_food-kit_License.txt`](kenney_food-kit_License.txt) and [`kenney_nature-kit_License.txt`](kenney_nature-kit_License.txt). Each states CC0, allows commercial projects, and says crediting Kenney is not mandatory.
- CC0 legal code: https://creativecommons.org/publicdomain/zero/1.0/legalcode.en — §2 covers any purpose including commercial; §4 says trademark/patent rights are unaffected and the affirmer does not clear third-party rights or provide warranties. Accordingly, the package notices support commercial use of covered copyrightable content, but do not establish trademark/likeness/third-party clearance or project-specific legal acceptance.

## Selection rationale

Food Kit directly supports picnic food pickups. Nature Kit is a small environment/support pack useful for flowers, mushrooms, pots, rocks and foliage; its archive is only about 10 MB. Furniture Kit and Quaternius were not downloaded. No Blender, Unity, importer, installer, account, payment, or bundled executable/script was run.

## Archive inspection protocol and findings

Inspection had two distinct stages. First, the official metadata-only scanner [`archive_inventory.py`](../../tools/offline_quality/archive_inventory.py) ran with limits of 5,000 entries, 100,000,000 bytes total declared expansion, 20,000,000 bytes per member, and a compression ratio of 200. It checked directory metadata and SHA-256 of each archive; it does not read member payloads and reports `extraction_performed=false`. The complete member-name, declared-size, central-directory CRC32, and extension inventory is [`inventory.csv`](inventory.csv); its CRC32 column is ZIP directory metadata, not a claim that the scanner validated member data.

Separately, after a custom precheck had capped each ZIP at 5,000 entries and 500,000,000 total declared expansion and rejected absolute/traversal/backslash paths, symlinks, and ratios above 1,000, Python `zipfile.testzip()` streamed/decompressed member payloads in memory to verify their CRCs; both passed. This was an additional bounded payload read, not part of the metadata-only scanner and not a safe-extraction guarantee. No model/content member was written out or unpacked to its archive path. Only the small (≤64 KiB) `License.txt` members were read and their bytes copied verbatim as separate license evidence files; no other member payload was inspected.

The filename-only script/executable extension scan (`.py`, `.cs`, `.js`, `.bat`, `.cmd`, `.sh`, `.ps1`, `.exe`, `.dll`, `.jar`, `.msi`, `.app`, `.command`) found no candidates; file contents were not searched for embedded code. The archives do include an HTML overview and URL shortcut metadata; these were only inventoried and not opened or executed. No asset was imported, rendered, opened in an asset editor, or installed.

## Intake boundary

Assets remain only as ignored ZIPs in `Docs/AssetIntake/Downloads/`; no assets were copied into Git-tracked content or the Unity project. Exact concept/file matches are only filename-level presence, not confirmed visual identity or suitability. Dimensions, topology, materials, texture dimensions, scale, collision proxies, per-file third-party rights, and actual visual render are unknown. Package-level license evidence is not final human legal/asset approval.
