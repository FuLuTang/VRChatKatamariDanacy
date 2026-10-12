# Future clone and VPM restore checklist

**schemaVersion: 1** · Applies to the existing project only: `VRChatKatamariDanacyUnity/`. This is a user-run future procedure, not an execution log.

## Before restoring

- Clone the repository on the Windows host; keep its existing directory structure and project root.
- Confirm the existing project root is `VRChatKatamariDanacyUnity/`. Do not initialize a new Unity project over it, move Assets, or overwrite `.meta` files.
- In VCC, choose **Add Existing Project** and select this root. Do not migrate, upgrade, or change package versions as part of this baseline restore.
- Compare the project Editor metadata against the then-current [VRChat supported Unity version](https://creators.vrchat.com/sdk/upgrade/current-unity-version/) and VCC's compatibility display; ask the user before any version change.

## Restore missing packages

- Let VCC's package manager or the official VPM Resolver handle dependencies declared by the project manifests. If Unity offers to restore missing packages, inspect that it is restoring the expected project packages before the user accepts.
- Do not fetch or copy VRChat SDK source manually. Do not edit `manifest.json`, `vpm-manifest.json`, or lock files merely to silence an error. Stop and record the exact warning if package resolution fails.
- Wait for restoration/import to finish. The user is the sole Editor operator and decides whether to open the project and run Console/Build & Test checks.
- VPM source-control policy: commit project Assets/settings/manifests and the official resolver exception; exclude generated caches and VPM SDK package source. See [Using Source Control with the VPM](https://vcc.docs.vrchat.com/vpm/source-control/).

## Evidence to record after a user-run restore

Record observed VCC version, Unity Editor version/revision, VPM Worlds/Base versions, standalone UdonSharp status (or `unknown`), restore outcome, Console compile results, Build & Test result, log/evidence paths, and date. Do not report PASS without actual user-provided evidence. Until then all runtime checks are **NOT_RUN**.
