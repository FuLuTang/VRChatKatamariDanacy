# P03 handoff

- Task: `task-7` / P03 offline quality gates and safe ZIP inventory.
- Input: v0.3 documents, `Docs/config/prototype_defaults.json` (provisional), `tasks.json`; no public interface freeze.
- Changes: `Docs/Preparation/Quality/offline_test_matrix.md`; standard-library read-only inventory CLI at `Docs/tools/offline_quality/archive_inventory.py`; unittest package at `Docs/tests/offline_quality/`.
- Reproduce tests: `python3 -m unittest discover -s Docs/tests -v` (30 tests, OK after the review revision).
- Reproduce inventory: `python3 Docs/tools/offline_quality/archive_inventory.py --json --sha256 /path/to/file.zip`.
- Security boundary: ZIP metadata only by default; archive-file hashing reads only the external archive. No member contents are read or extracted; no code is executed. 150 MiB archive cap, path policy, limits, rejection cases, TOCTOU/central-directory/CRC caveats, and explicit non-restore-safe wording are documented in the matrix. A successful inventory is not approval to restore or extract.
- Not done: no Unity/Udon/VRChat/ClientSim run, no implementation of game-state reducers, no validation of asset licenses, no T20 acceptance, no T06 freeze. G1/G3/network/comfort evidence remains NOT_RUN.
- Unity is on Windows at `D:\VRChatKatamariDanacy\VRChatKatamariDanacyUnity`; the user owns the Editor. Unity was not started, MCP is unconfirmed, and there was no SSH/MCP/Blender/project access.
- No downloads, account actions, payments, commits, or pushes.
