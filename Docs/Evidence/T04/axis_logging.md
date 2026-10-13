# T04 Axis Input Logging

Status: source changed, not compiled. Compile and runtime verification are for the Lead to trigger later. No Unity Editor, Client, Blender, or GUI operation was performed.

## Scope

Only added input-event logging to `T04PlatformDriver.cs`. The original source snapshot was saved before replacement at `/tmp/T04PlatformDriver.before_axis.cs`. The Windows replacement was staged under `%TEMP%` and moved over the target in one operation.

## SDK signature basis

Installed SDK source: `%USERPROFILE%\Documents\Github\VRChatKatamariDanacy\VRChatKatamariDanacyUnity\Packages\com.vrchat.worlds\Integrations\UdonSharp\Runtime\UdonSharpBehaviour.cs`, lines 459-464. It declares:

```csharp
public virtual void InputGrab(bool value, VRC.Udon.Common.UdonInputEventArgs args)
public virtual void InputMoveHorizontal(float value, VRC.Udon.Common.UdonInputEventArgs args)
public virtual void InputMoveVertical(float value, VRC.Udon.Common.UdonInputEventArgs args)
public virtual void InputLookHorizontal(float value, VRC.Udon.Common.UdonInputEventArgs args)
public virtual void InputLookVertical(float value, VRC.Udon.Common.UdonInputEventArgs args)
```

These match the five override signatures used. `UdonInputEventArgs` is supplied by the SDK's compiled Udon assembly; its type declaration was not present in the scanned `.cs` sources. Existing `InputJump` already reads `args.eventType`. The new log uses `args.eventType` and `args.handType`; `floatValue`/`boolValue` are not used because the event callback's `value` parameter is logged. The `handType` member could not be independently confirmed from SDK source and should be checked if compilation reports a member mismatch.

## Added diff

```diff
+    private float[] axisLastLogTime = new float[5];
+    private float[] axisPreviousValue = new float[5];
+    private bool[] axisHasPreviousValue = new bool[5];
+
+    public override void InputLookHorizontal(float value, UdonInputEventArgs args)
+    {
+        if (!IsLocalInputPlayer()) return;
+        if (ShouldLogAxis(0, value))
+            LogAxisEvent("LookH", value, args);
+    }
+
+    public override void InputLookVertical(float value, UdonInputEventArgs args)
+    {
+        if (!IsLocalInputPlayer()) return;
+        if (ShouldLogAxis(1, value))
+            LogAxisEvent("LookV", value, args);
+    }
+
+    public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
+    {
+        if (!IsLocalInputPlayer()) return;
+        if (ShouldLogAxis(2, value))
+            LogAxisEvent("MoveH", value, args);
+    }
+
+    public override void InputMoveVertical(float value, UdonInputEventArgs args)
+    {
+        if (!IsLocalInputPlayer()) return;
+        if (ShouldLogAxis(3, value))
+            LogAxisEvent("MoveV", value, args);
+    }
+
+    public override void InputGrab(bool value, UdonInputEventArgs args)
+    {
+        if (!IsLocalInputPlayer()) return;
+        float numericValue = value ? 1f : 0f;
+        if (ShouldLogAxis(4, numericValue))
+            Debug.Log("[T04] AXIS name=Grab value=" + value + " type=" + args.eventType +
+                " hand=" + args.handType + " seated=" + isLocalPlayerSeated + " frame=" + Time.frameCount);
+    }
+
+    private bool IsLocalInputPlayer()
+    {
+        VRCPlayerApi player = Networking.LocalPlayer;
+        return player != null && player.isLocal;
+    }
+
+    private bool ShouldLogAxis(int index, float value)
+    {
+        bool transition = !axisHasPreviousValue[index];
+        if (axisHasPreviousValue[index])
+        {
+            float previous = axisPreviousValue[index];
+            transition = (previous == 0f && value != 0f) ||
+                (previous != 0f && value == 0f) ||
+                (previous < 0f && value > 0f) ||
+                (previous > 0f && value < 0f);
+        }
+        axisPreviousValue[index] = value;
+        axisHasPreviousValue[index] = true;
+        if (transition || Time.time - axisLastLogTime[index] >= 0.25f)
+        {
+            axisLastLogTime[index] = Time.time;
+            return true;
+        }
+        return false;
+    }
+
+    private void LogAxisEvent(string axisName, float value, UdonInputEventArgs args)
+    {
+        Debug.Log("[T04] AXIS name=" + axisName + " value=" + value + " type=" + args.eventType +
+            " hand=" + args.handType + " seated=" + isLocalPlayerSeated + " frame=" + Time.frameCount);
+    }
```

## Static checks

- Balanced delimiters: parentheses 112/112, braces 33/33, brackets 28/28.
- ASCII-only source: pass.
- Five overrides present: pass; signatures match installed `UdonSharpBehaviour.cs` source.
- Existing marker counts unchanged (before -> after): `InputJump value=` 1 -> 1; `JumpCue shown` 1 -> 1; `JumpImpulse initial=` 1 -> 1; `JumpImpulse restored=` 1 -> 1; `Interact disabled (seated)` 1 -> 1; `Interact enabled` 3 -> 3; `ExitButton pressed` 2 -> 2; carrier movement assignment 1 -> 1.
- `args.handType` type/member and UdonSharp compilation: not verified yet.
- No Unity compile or headset test performed.

## Follow-up

Lead should compile the modified source in the user-owned Unity Editor, resolve any SDK member/array restrictions if needed, then collect headset evidence comparing seated and standing A/B input event logs. No gameplay behavior change is intended; existing InputJump and station/jump/visual/exit code was not edited.
