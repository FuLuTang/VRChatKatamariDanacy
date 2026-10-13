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

    private void Start()
    {
        localPlayer = Networking.LocalPlayer;
        if (carrier == null) carrier = transform;
        carrierBaseLocalPosition = carrier.localPosition;
        CacheJumpImpulse(localPlayer);
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
        jumpDown = false;
        Debug.Log("[T04] StationEntered seated=true; JumpImpulse saved=" + savedJumpImpulse + " set=0");
        // Disable Station Exit is primary protection against Jump exiting; zero jump is secondary.
        player.SetJumpImpulse(0f);
    }

    public override void OnStationExited(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        isLocalPlayerSeated = false;
        RestoreJumpImpulse(player, "station-exit");
    }

    public override void OnPlayerRespawn(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        bool exitStation = isLocalPlayerSeated && station != null;
        isLocalPlayerSeated = false;
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
        RestoreJumpImpulse(localPlayer, "world-exit-button");
        if (exitStation)
            station.ExitStation(localPlayer);
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
