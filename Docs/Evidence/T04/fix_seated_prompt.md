# T04 seated Interact prompt fix

**Status:** 源码已改，未编译。编译与运行验证由 Lead 之后触发；用户手测仍待完成。未操作 GUI、Unity/VRChat/Blender 进程或 `/tmp/cua` 脚本。

## Scope

- Windows source: `%USERPROFILE%\Documents\Github\VRChatKatamariDanacy\VRChatKatamariDanacyUnity\Assets\VRC_Katamari\Prototypes\Platform\T04PlatformDriver.cs`
- Linux rollback copy: `/tmp/T04PlatformDriver.before.cs`
- Changed file count: one source file. This evidence file is the only Linux workspace addition.

## API basis

The installed UdonSharp source declares `public bool DisableInteractive { get; set; }` on `UdonSharpBehaviour` and documents it as disabling Interact events and the interact outline:

`%USERPROFILE%\Documents\Github\VRChatKatamariDanacy\VRChatKatamariDanacyUnity\Packages\com.vrchat.worlds\Integrations\UdonSharp\Runtime\UdonSharpBehaviour.cs`, declaration at line 247 (summary at lines 243–245). The package's `Tests~/TestScripts/Core/MethodCallsTest.cs` also sets the property both on `selfUdon` and directly on `self`, and asserts true/false values (lines 175–188). Thus the property is available and directly assignable from this `UdonSharpBehaviour` in the installed package.

## Focused diff (before → after)

```diff
 public override void OnStationEntered(VRCPlayerApi player)
 {
     if (player == null || !player.isLocal) return;
     localPlayer = player;
     isLocalPlayerSeated = true;
+    DisableInteractive = true;
+    Debug.Log("[T04] Interact disabled (seated)");
     jumpDown = false;
     ...
 }

 public override void OnStationExited(VRCPlayerApi player)
 {
     if (player == null || !player.isLocal) return;
+    bool wasSeated = isLocalPlayerSeated;
     isLocalPlayerSeated = false;
+    DisableInteractive = false;
+    if (wasSeated)
+    {
+        Debug.Log("[T04] Interact enabled (left station) reason=station-exit");
+    }
     RestoreJumpImpulse(player, "station-exit");
 }

 public override void OnPlayerRespawn(VRCPlayerApi player)
 {
     ...
     isLocalPlayerSeated = false;
+    DisableInteractive = false;
+    Debug.Log("[T04] Interact enabled (left station) reason=respawn");
     RestoreJumpImpulse(player, "respawn");
     ...
 }

 public void LeaveStation()
 {
     ...
     isLocalPlayerSeated = false;
+    DisableInteractive = false;
+    Debug.Log("[T04] Interact enabled (left station) reason=world-exit-button");
     RestoreJumpImpulse(localPlayer, "world-exit-button");
     ...
 }
```

The `wasSeated` guard prevents a second station-exit message when `LeaveStation()` or Respawn already cleared the local seated state before calling `station.ExitStation()`. The exit callback still restores `DisableInteractive = false` regardless.

## Static checks (offline; no Unity)

- Installed API source and package test usage inspected; property exists on UdonSharpBehaviour.
- Setter counts in modified source: `true` once on local station entry; `false` once in each of the three exit handlers.
- Required reason tokens each occur once: `station-exit`, `respawn`, `world-exit-button`. The station-exit log is guarded against duplicate reporting after another path already cleared `isLocalPlayerSeated`.
- Source inspection confirmed the existing Jump debounce counter, JumpCue call, saved/default/readback JumpImpulse handling, carrier sway, and seated/unseated exit-button branch logs remain present and unchanged.
- The edit adds only basic UdonSharp 1.x constructs: property assignments, a `bool` local, and an `if` block; no LINQ, generics, lambdas, reflection, or editor API added.
- Delimiters `()`, `{}`, `[]` are balanced after stripping comments and string literals.
- Unity compilation, Build & Test, and desktop/VR user retest: **NOT RUN**.
