# P16 — Unity/VPM baseline notes

**schemaVersion: 1** (documentation set; updated 2026-10-11). These notes record repository observations and a future, user-operated recovery checklist. They do not establish a runtime baseline or authorize changing the Windows project. Paths below are relative to the repository root unless stated otherwise.

## Observed repository metadata (not a support claim)

| Input | Observed value | SHA-256 | Meaning / limit |
|---|---|---|---|
| `VRChatKatamariDanacyUnity/ProjectSettings/ProjectVersion.txt` | Editor `2022.3.22f1`; revision `887be4894c44` | `0599d0a5e5f574ce3aaf02f6f98a477bd75b97ddd6a1cf9a9466c603c3d7b6ca` | Project metadata only; not proof Editor is installed or project compiles. |
| `VRChatKatamariDanacyUnity/Packages/manifest.json` | direct dependencies include Unity packages; no `com.vrchat.*` entry in the inspected manifest | `97df847876109e53bd83c60f16355cb74f194db6934e203e8839bb664fc22e48` | Unity Package Manager manifest, distinct from VPM manifest. |
| `VRChatKatamariDanacyUnity/Packages/vpm-manifest.json` | dependency `com.vrchat.worlds` `3.10.5`; locked `com.vrchat.base` and `com.vrchat.worlds` both `3.10.5` | `cb11a76e25d63a46e28e992559c2280ec1eb42248028069b644d021747c0b0d1` | Repository-declared VPM versions, not independently verified install/runtime versions. |
| `VRChatKatamariDanacyUnity/Packages/packages-lock.json` | present; Unity UPM lock data | `627489d82f39582be07c2b5157455850d30ae8834e681b9ac4608bdb774c629f` | Does not itself establish VPM SDK support. |
| `VRChatKatamariDanacyUnity/Packages/.gitignore` | excludes `com.vrchat.*`, re-includes `com.vrchat.core.*` | `2b486fbeb517efc8ab6e9e767cc1126f6dc9296439c057e3ace10a5eb3bab0b2` | Allows core resolver exception while preventing SDK package source tracking. |

`Assets/Scenes/VRCDefaultWorldScene.unity` and its `.meta` exist in this repository checkout; their presence is not proof of successful Unity validation or redistribution rights. The machine-readable report is [`metadata.json`](metadata.json); no SDK `metadata.json` was found by repository glob under `VRChatKatamariDanacyUnity/`, so there is no local SDK package metadata source/hash to report. `Packages/.gitignore` excludes `com.vrchat.*` while allowing `com.vrchat.core.*`; the local checkout search found no SDK package source files/resolver directory. This describes this checkout only: the Windows installation was **not inspected**, and no claim that Windows SDK files are missing or broken is made. The VPM manifest is the only observed source for the `3.10.5` Base/Worlds declarations here. VCC and independent UdonSharp version/state: **unknown**. Do not infer either from Worlds SDK version or package IDs.

## Official support evidence — separate from project observation

As of the date checked, VRChat's [Current Unity Version](https://creators.vrchat.com/sdk/upgrade/current-unity-version/) page explicitly names Unity `2022.3.22f1` as its current version and warns that upgrading can make content fail to load on VRChat. This corroborates the project Editor version as supported by the current published recommendation; it is not a compile result. VRChat's [VPM source-control guidance](https://vcc.docs.vrchat.com/vpm/source-control/) says to commit `Assets`, `ProjectSettings`, `Packages` manifests and resolver, exclude VPM package contents other than the resolver, and let VPM restore missing packages. It specifically says SDK source is only distributable by VRChat and must not be committed.

The `3.10.5` declaration is recorded strictly as observed project metadata. This note does **not** claim it is the official current SDK release or assert a separate UdonSharp version. VCC installation/version and restoration success were not observed. Sources are cited for future use, not treated as commands to run in this worker session.

## Future, user-operated restore sequence

1. Clone the repository on the Windows host and preserve the existing project directory; use the repository-contained `VRChatKatamariDanacyUnity` project root. Do not copy Unity caches or overwrite user settings.
2. In VRChat Creator Companion (VCC), use **Add Existing Project** and select that existing project root; do not create a replacement project or migrate/upgrade it implicitly. If VCC cannot recognize it, stop and have the user resolve VCC configuration rather than hand-editing project manifests.
3. Review the current official Unity version page and VCC's compatibility indication. Install/use the supported Editor version only as directed by the user. Do not switch versions as part of restore.
4. Use VCC's project management/SDK package UI or the official VPM Resolver prompt to restore declared missing packages. Accept only the restore for this project's declared dependencies; do not download package sources manually, modify version numbers, or commit restored SDK source. If an official dialog is unclear, stop for user review.
5. Let restoration/import finish. The user alone opens/operates Unity Editor, inspects Console, and runs the local VRChat Build & Test gate. Record exact VCC, Unity and SDK versions plus logs and unresolved errors in the authorized evidence location.
6. Until those user-run checks are supplied and reviewed, T00, Udon compilation, scene validation and Build & Test remain **NOT_RUN**; presence of manifests or a default scene does not pass them.

This is procedural preparation only. No restore, VCC action, Editor launch or project modification was performed by this work package.
