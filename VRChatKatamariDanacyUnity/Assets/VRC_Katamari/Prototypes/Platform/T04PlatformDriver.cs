using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;

public class T04PlatformDriver : UdonSharpBehaviour
{
    public VRC.SDK3.Components.VRCStation station;
    public Transform carrier;
    public Transform followRig;
    public Transform visualRoll;
    // VRChat VRCWorld prefab sets jump impulse to 3; the live getter is unreliable in this environment.
    private const float defaultJumpImpulse = 3f;
    private VRCPlayerApi localPlayer;
    private float savedJumpImpulse;
    private bool haveSavedJumpImpulse;
    private bool isLocalPlayerSeated;
    private bool jumpDown;
    private int jumpCount;
    private float lastJumpDownTime = -1f;
    private Vector3 carrierBaseLocalPosition;
    private float phase;
    private Renderer visualRollRenderer;
    private Color visualRollOriginalColor;
    private float jumpCueEndTime;
    private bool jumpCueActive;
    private float[] axisLastLogTime = new float[5];
    private float[] axisPreviousValue = new float[5];
    private bool[] axisHasPreviousValue = new bool[5];

    private void Start()
    {
        localPlayer = Networking.LocalPlayer;
        if (carrier == null) carrier = transform;
        carrierBaseLocalPosition = carrier.localPosition;
        CacheJumpImpulse(localPlayer);
        if (visualRoll != null)
        {
            visualRollRenderer = visualRoll.GetComponent<Renderer>();
            if (visualRollRenderer != null)
                visualRollOriginalColor = visualRollRenderer.material.color;
        }
        Debug.Log("[T04] JumpImpulse initial=" + savedJumpImpulse + " canJump=" + (savedJumpImpulse > 0f));
        Debug.Log("[T04] Driver ready");
    }

    private void Update()
    {
        phase += Time.deltaTime;
        if (!isLocalPlayerSeated && localPlayer == null)
            localPlayer = Networking.LocalPlayer;
        if (!isLocalPlayerSeated)
            CacheJumpImpulse(localPlayer);
        if (carrier != null)
        {
            // Prototype-only motion: 0.75 m lateral amplitude and a slow ~42 s cycle.
            Vector3 offset = new Vector3(Mathf.Sin(phase * 0.15f) * 0.75f, 0f, 0f);
            carrier.localPosition = carrierBaseLocalPosition + offset;
        }
        if (visualRoll != null)
            visualRoll.Rotate(Vector3.forward, 45f * Time.deltaTime, Space.Self);
        if (jumpCueActive && Time.time >= jumpCueEndTime)
        {
            if (visualRollRenderer != null)
                visualRollRenderer.material.color = visualRollOriginalColor;
            jumpCueActive = false;
        }
    }

    public override void Interact()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (station != null && player != null && !isLocalPlayerSeated)
            station.UseStation(player);
    }

    public override void OnStationEntered(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        localPlayer = player;
        isLocalPlayerSeated = true;
        DisableInteractive = true;
        Debug.Log("[T04] Interact disabled (seated)");
        jumpDown = false;
        Debug.Log("[T04] StationEntered seated=true; JumpImpulse saved=" + savedJumpImpulse + " set=0");
        // Disable Station Exit is primary protection against Jump exiting; zero jump is secondary.
        player.SetJumpImpulse(0f);
    }

    public override void OnStationExited(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        bool wasSeated = isLocalPlayerSeated;
        isLocalPlayerSeated = false;
        DisableInteractive = false;
        if (wasSeated)
        {
            Debug.Log("[T04] Interact enabled (left station) reason=station-exit");
        }
        RestoreJumpImpulse(player, "station-exit");
    }

    public override void OnPlayerRespawn(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        bool exitStation = isLocalPlayerSeated && station != null;
        isLocalPlayerSeated = false;
        DisableInteractive = false;
        Debug.Log("[T04] Interact enabled (left station) reason=respawn");
        RestoreJumpImpulse(player, "respawn");
        if (exitStation)
            station.ExitStation(player);
        Debug.Log("[T04] Respawn requested station exit");
    }

    public override void InputJump(bool value, UdonInputEventArgs args)
    {
        if (!isLocalPlayerSeated) return;
        UdonInputEventType eventType = args.eventType;
        bool counted = false;
        if (eventType == UdonInputEventType.BUTTON)
        {
            if (value && !jumpDown)
            {
                jumpDown = true;
                if (Time.time - lastJumpDownTime >= 0.2f)
                {
                    lastJumpDownTime = Time.time;
                    jumpCount++;
                    counted = true;
                    ShowJumpCue();
                }
            }
            else if (!value)
            {
                jumpDown = false;
            }
        }
        Debug.Log("[T04] InputJump value=" + value + " type=" + eventType + " frame=" + Time.frameCount +
            " count=" + jumpCount + " counted=" + counted + " seated=" + isLocalPlayerSeated);
    }

    public override void InputLookHorizontal(float value, UdonInputEventArgs args)
    {
        if (!IsLocalInputPlayer()) return;
        if (ShouldLogAxis(0, value))
            LogAxisEvent("LookH", value, args);
    }

    public override void InputLookVertical(float value, UdonInputEventArgs args)
    {
        if (!IsLocalInputPlayer()) return;
        if (ShouldLogAxis(1, value))
            LogAxisEvent("LookV", value, args);
    }

    public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
    {
        if (!IsLocalInputPlayer()) return;
        if (ShouldLogAxis(2, value))
            LogAxisEvent("MoveH", value, args);
    }

    public override void InputMoveVertical(float value, UdonInputEventArgs args)
    {
        if (!IsLocalInputPlayer()) return;
        if (ShouldLogAxis(3, value))
            LogAxisEvent("MoveV", value, args);
    }

    public override void InputGrab(bool value, UdonInputEventArgs args)
    {
        if (!IsLocalInputPlayer()) return;
        float numericValue = value ? 1f : 0f;
        if (ShouldLogAxis(4, numericValue))
            Debug.Log("[T04] AXIS name=Grab value=" + value + " type=" + args.eventType +
                " hand=" + args.handType + " seated=" + isLocalPlayerSeated + " frame=" + Time.frameCount);
    }

    private bool IsLocalInputPlayer()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        return player != null && player.isLocal;
    }

    private bool ShouldLogAxis(int index, float value)
    {
        bool transition = !axisHasPreviousValue[index];
        if (axisHasPreviousValue[index])
        {
            float previous = axisPreviousValue[index];
            transition = (previous == 0f && value != 0f) ||
                (previous != 0f && value == 0f) ||
                (previous < 0f && value > 0f) ||
                (previous > 0f && value < 0f);
        }
        axisPreviousValue[index] = value;
        axisHasPreviousValue[index] = true;
        if (transition || Time.time - axisLastLogTime[index] >= 0.25f)
        {
            axisLastLogTime[index] = Time.time;
            return true;
        }
        return false;
    }

    private void LogAxisEvent(string axisName, float value, UdonInputEventArgs args)
    {
        Debug.Log("[T04] AXIS name=" + axisName + " value=" + value + " type=" + args.eventType +
            " hand=" + args.handType + " seated=" + isLocalPlayerSeated + " frame=" + Time.frameCount);
    }

    public void LeaveStation()
    {
        if (!isLocalPlayerSeated)
        {
            Debug.Log("[T04] ExitButton pressed seated=false nothing-to-exit");
            return;
        }
        Debug.Log("[T04] ExitButton pressed seated=true exiting");
        bool exitStation = localPlayer != null && station != null;
        isLocalPlayerSeated = false;
        DisableInteractive = false;
        Debug.Log("[T04] Interact enabled (left station) reason=world-exit-button");
        RestoreJumpImpulse(localPlayer, "world-exit-button");
        if (exitStation)
            station.ExitStation(localPlayer);
    }

    private void ShowJumpCue()
    {
        if (visualRollRenderer != null)
        {
            visualRollRenderer.material.color = Color.yellow;
            jumpCueEndTime = Time.time + 0.3f;
            jumpCueActive = true;
        }
        Debug.Log("[T04] JumpCue shown (no item system in prototype)");
    }

    private void CacheJumpImpulse(VRCPlayerApi player)
    {
        if (player == null) return;
        savedJumpImpulse = player.GetJumpImpulse();
        haveSavedJumpImpulse = true;
    }

    private void RestoreJumpImpulse(VRCPlayerApi player, string reason)
    {
        if (player != null && haveSavedJumpImpulse)
        {
            // Restore the documented VRCWorld default, not the untrustworthy getter snapshot.
            player.SetJumpImpulse(defaultJumpImpulse);
            float rereadJumpImpulse = player.GetJumpImpulse();
            Debug.Log("[T04] JumpImpulse restored=" + defaultJumpImpulse + " reread=" + rereadJumpImpulse + " reason=" + reason + " baseline=default");
            haveSavedJumpImpulse = false;
        }
        jumpDown = false;
    }
}

