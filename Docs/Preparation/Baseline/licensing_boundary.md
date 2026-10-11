# SDK and template licensing/distribution boundary

**schemaVersion: 1** · Updated 2026-10-11.

VRChat's official [VPM source-control guidance](https://vcc.docs.vrchat.com/vpm/source-control/) states the VRChat SDK is only allowed to be distributed by VRChat and must never be committed to source control. It directs projects to keep manifests and the VPM Resolver in source control while excluding VPM package source folders; its specific exception is `Packages/com.vrchat.core.vpm-resolver`, which restores packages without VCC. The repository's `Packages/.gitignore` and root `.gitignore` implement the broad `com.vrchat.*` exclusion plus `com.vrchat.core.*` inclusion; this exception is for the official restore tool, not permission to redistribute SDK sources.

Practical boundary for this project:

- Commit only necessary project-authored `Assets/`, `ProjectSettings/`, package manifests/lock data, `.meta` files, and the permitted resolver tool per current official policy.
- Never add restored `com.vrchat.worlds`, `com.vrchat.base`, UdonSharp, or other SDK package source to a distributable archive or repository. VPM is the restore route.
- Unity/VRChat default scene/template presence is not evidence that bundled SDK source may be redistributed. This inspection did not identify a separate `metadata.json` or template license record in the project tree; provenance and license for any specific default/template asset remain **unverified**. Keep such content in its originating project unless its own applicable terms permit redistribution.
- A version string such as `3.10.5` from the project manifest is an observed dependency declaration, not an official-current statement and not a grant of license.
- This project has no license choice recorded here. This file does not assign an open-source license or approve any third-party asset for release.
