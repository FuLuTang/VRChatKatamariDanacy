using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRCKatamari.Core;

namespace VRCKatamari.Labs.HubLoop
{
    /// <summary>
    /// Lab probe for the hub loop plan's first step: does <c>VRCPlayerApi.TeleportTo</c> actually
    /// move the local player in this scene? One serialized list of destination Transforms, one
    /// entry point, and a logged measurement of where the player really ended up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scope: this is the whole file. No round state, no scoring, no HUD, no room/level knowledge,
    /// no <c>Start()</c>-time teleport and no respawn hook. The room/level flow that will use it
    /// later (<c>StoryRoundController.RequestStart()</c>, HUB_LOOP_PLAN §5) owns all of that; the
    /// probe only answers "does the move happen, and does it stick". Delete this file, its .meta,
    /// its program asset and its component once the answer is written down
    /// (<c>Labs/README.md</c>: lab scripts are removable and must stay removable).
    /// </para>
    /// <para>
    /// Why this exists / why a new script was needed: nothing in <c>Runtime/</c> teleports a
    /// player today. <c>KatamariBallMotor.TeleportTo(Vector3)</c> moves the <em>ball</em>, not the
    /// player, and the only player-teleport call sites in the plan are the future ones on
    /// <c>StoryRoundController</c>. So there is no existing behaviour to reuse instead of writing
    /// this one.
    /// </para>
    /// <para>
    /// The signature, checked rather than recalled. The probe calls the four-argument form:
    /// <code>
    /// void TeleportTo(Vector3 teleportPos, Quaternion teleportRot,
    ///                 VRC_SceneDescriptor.SpawnOrientation teleportOrientation, bool lerpOnRemote)
    /// </code>
    /// Evidence, checked in this order:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// Local creator library, <c>entities/world/udon/players/player-positions.md</c> §传送 API:
    /// "签名 <c>TeleportTo(Vector3 teleportPos, Quaternion teleportRot, ...)</c>", third input
    /// <c>TeleportOrientation</c> of type <c>SceneDescriptorSpawnOrientation</c>, fourth
    /// <c>lerpOnRemote</c> (bool, default false). The same page also says Udon can only teleport the
    /// local player and that Stations can block a teleport.
    /// </description></item>
    /// <item><description>
    /// <c>entities/api/player-api.md</c> ("<c>player.TeleportTo(Vector3 position, Quaternion rotation)</c>
    /// 暴露 ✅") and <c>entities/world/udon/graph/type-nodes.md</c> §12, which spells the enum's
    /// full name as <c>VRC.SDKBase.VRC_SceneDescriptor+SpawnOrientation</c> - that is why this file
    /// writes <c>VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint</c> under
    /// <c>using VRC.SDKBase;</c>.
    /// </description></item>
    /// <item><description>
    /// Official docs, <see href="https://creators.vrchat.com/worlds/udon/players/player-positions/#teleportto"/>
    /// (fetched 2026-11-15): inputs are <c>Vector3 teleportPos</c>, <c>Quaternion teleportRot</c>,
    /// <c>SceneDescriptorSpawnOrientation TeleportOrientation</c> (optional) and
    /// <c>bool lerpOnRemote</c> (optional). It also states: only the local player can be
    /// teleported, Stations can prevent it, and teleporting more than 1 m / 45 degrees resets
    /// avatar animation and IK unless <c>lerpOnRemote</c> is true.
    /// </description></item>
    /// <item><description>
    /// Cannot be checked here: the VPM package source (<c>Packages/com.vrchat.*</c>) is installed on
    /// the Windows machine and is not in this checkout, so the exact C# declaration in SDK 3.10.5
    /// (in particular whether the parameters carry defaults) is <em>not</em> read from source. The
    /// call therefore passes all four arguments explicitly, which compiles against both a
    /// four-parameter method and a two-parameter method with defaults.
    /// </description></item>
    /// </list>
    /// <para>
    /// Correction worth keeping: there is no type named <c>TeleportRotationType</c> in the VRChat
    /// SDK, and the third parameter is not that type. The shipped enum is
    /// <c>VRC_SceneDescriptor.SpawnOrientation</c>, and in the documented API it only appears
    /// together with <c>lerpOnRemote</c> (the four-argument form), so a call written as
    /// <c>TeleportTo(pos, rot, TeleportRotationType.X)</c> would not compile. Whether the C#
    /// declaration carries default values (which would also allow a three-argument call omitting
    /// <c>lerpOnRemote</c>) is part of the unverified package source noted above; this file passes
    /// all four arguments so it does not depend on that answer.
    /// </para>
    /// <para>
    /// Why <c>AlignPlayerWithSpawnPoint</c> and <c>lerpOnRemote=false</c>: the hub loop's two
    /// destinations (room, level entry) are 1200 m apart and both want "face the level / face the
    /// panel" (HUB_LOOP_PLAN §3), which is exactly "align the player with the point's rotation".
    /// <c>Default</c> currently equals that too, but the SDK docs call <c>Default</c> a moving
    /// target ("Currently Align Player With Spawn Point"), so the probe names the behaviour it
    /// wants instead of inheriting a future default. <c>lerpOnRemote=false</c> is the honest
    /// choice for an instant scene-to-scene hop: with true, remote clients would see the player
    /// fly 1200 m as normal movement and keep animation/IK across it.
    /// </para>
    /// <para>
    /// Arrival facing comes from the destination Transform's own rotation, not from a separate
    /// angle field. Reason: the plan already writes the future calls as
    /// <c>TeleportTo(levelEntry.position, levelEntry.rotation)</c> and
    /// <c>TeleportTo(roomSpawn.position, roomSpawn.rotation)</c> (HUB_LOOP_PLAN §5), so one
    /// Transform per point is the shape the product code will have too. The operator rotates the
    /// empty GameObject in the Scene view - no second number per point that can drift out of sync
    /// with the position it belongs to.
    /// </para>
    /// <para>
    /// Pitfall 1, the player being pushed back out of the destination. <c>TeleportTo</c> moves the
    /// player capsule; it does not check whether the capsule now overlaps geometry. A point placed
    /// exactly on a floor surface can start the capsule a few centimetres inside the floor
    /// collider, and the client then depenetrates the player (up, or sideways out of a wall) - it
    /// looks exactly like "the teleport did nothing" or "the teleport slid me away". Two guards:
    /// (a) <c>arrivalLiftMeters</c> is added to the target's Y, so the Transform can mark the floor
    /// surface and the player starts slightly above it (the probe then falls the last few
    /// centimetres onto the floor); (b) the destination must not be inside any collider volume -
    /// that is a scene-authoring rule, not something this script can detect, and it is why the
    /// probe logs two readings instead of one. Note the scene already levitates its own spawn:
    /// <c>StoryLevel.unity</c>'s <c>Spawn</c> sits at y = 0.5 above the arena floor at y = 0, so
    /// "a bit above the floor" is this scene's existing convention, not a new invention. The lift
    /// default here is 0.1 m: small enough that the landing is imperceptible, big enough that the
    /// capsule does not start inside the floor. Raise it toward 0.5 m if the settled reading shows
    /// the player being pushed.
    /// </para>
    /// <para>
    /// Two readings per teleport, because one cannot tell the two failure modes apart. At frame 2
    /// the client has applied the move but physics has barely run: a large
    /// <c>distanceToArrival</c> there means the teleport did not move the player at all. At frame
    /// 30 (~0.5 s at 60 fps) physics has settled: a small distance at frame 2 that grew by frame
    /// 30 means the teleport worked and something pushed the player afterwards. A probe that
    /// re-teleports on the second reading would hide exactly the bug it exists to find, so it
    /// does not: it only logs. Both lines carry the plan's own acceptance tolerance (0.5 m,
    /// HUB_LOOP_PLAN §9) as <c>arrived=True/False</c>.
    /// </para>
    /// <para>
    /// Pitfall 2, teleporting during a network update. The KB page warns not to call
    /// <c>TeleportTo</c> from <c>OnDeserialization</c> (the avatar can collide with geometry
    /// mid-teleport) and recommends <c>SendCustomEventDelayedFrames(..., 1)</c>. The probe has no
    /// network callbacks at all (<c>BehaviourSyncMode.None</c>) and only teleports from
    /// <c>Interact()</c>/a direct public call, which are not network updates, so no delay shim is
    /// needed; adding one would delay the measurement instead.
    /// </para>
    /// <para>
    /// Entry points, both on this one behaviour. <c>Interact()</c> is the world-space press: the
    /// plan's §3.1 picks in-world <c>Interact</c> buttons for the first pass because the scene has
    /// no <c>EventSystem</c> and the world-space button path has precedent
    /// (<c>BallRideExitButton</c>, <c>StationLabExitButton</c>: object + collider + behaviour).
    /// <c>TeleportLocalPlayerToIndex(int)</c> is the seam the later room button / round controller
    /// calls instead of pressing anything. <c>Interact()</c> is a one-line forward to it, so there
    /// is still one implementation of "how a teleport happens".
    /// </para>
    /// <para>
    /// No <c>OnPlayerRespawn</c> override, on purpose. A respawn already puts the local player back
    /// at the world spawn; a probe that also teleported on respawn would add a second, invisible
    /// teleport path and would mask the plan's own fallback ("if the return-to-room teleport fails,
    /// respawn from the menu", HUB_LOOP_PLAN §5). The probe must be able to <em>observe</em> a
    /// respawn, not fight it.
    /// </para>
    /// <para>
    /// Refusals are reason codes plus one log line each, never exceptions (UdonSharp has no
    /// try/catch and a probe must not take the world down): 0 = none, 1 = no local player,
    /// 2 = no targets wired, 3 = index out of range, 4 = that slot is empty, 5 = the computed
    /// arrival point is not finite.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PlayerTeleportProbe : UdonSharpBehaviour
    {
        // ------------------------------------------------------------------ serialized

        [Header("Destinations")]
        // The Transform position is the floor-surface point; the Transform rotation is the
        // arrival facing (see the class remarks for why rotation is not a separate angle field).
        [SerializeField] private Transform[] targets;

        [Header("World-space button")]
        // Only Interact() reads this: one probe instance per button means each button owns its
        // destination and no extra shim script is needed. A single probe holding many targets is
        // driven through TeleportLocalPlayerToIndex instead.
        [SerializeField] private int interactTargetIndex = 0;

        [Header("Arrival")]
        // Metres added to the target's Y before the teleport. See pitfall 1 in the class remarks.
        [SerializeField] private float arrivalLiftMeters = 0.1f;

        [Header("Optional arrival notification (leave empty for the probe)")]
        // Nothing in this file needs it: the future flow calls into the probe, not out of it. It is
        // here so a later step can hook "player arrived" without the probe growing a typed
        // dependency on StoryRoundController.
        [SerializeField] private UdonSharpBehaviour arrivalListener;
        [SerializeField] private string arrivalListenerEvent = "";

        // ------------------------------------------------------------------ reason codes

        private const int ReasonNone = 0;
        private const int ReasonNoLocalPlayer = 1;
        private const int ReasonNoTargets = 2;
        private const int ReasonIndexOutOfRange = 3;
        private const int ReasonTargetUnwired = 4;
        private const int ReasonArrivalNotFinite = 5;

        // The plan's acceptance test is "within 0.5 m of the point" (HUB_LOOP_PLAN §9).
        private const float ArrivalToleranceMeters = 0.5f;

        // Frame 2: the move has been applied, physics has not settled. Frame 30: ~0.5 s at 60 fps.
        private const int ImmediateCheckFrames = 2;
        private const int SettledCheckFrames = 30;

        private const string LogPrefix = "[hub-teleport]";

        // ------------------------------------------------------------------ pending reading

        private bool teleportPending;
        private int framesSinceTeleport;
        private bool loggedImmediateCheck;
        private bool loggedSettledCheck;
        private Vector3 requestedArrival;
        private Vector3 positionBeforeTeleport;
        private int requestedIndex;

        // ------------------------------------------------------------------ lifecycle

        private void Start()
        {
            // A negative or NaN lift would start the capsule under the floor, which reads as a
            // broken teleport. Refuse the value once, loudly, instead of mis-measuring later.
            if (!KatamariMath.IsFiniteFloat(arrivalLiftMeters) || arrivalLiftMeters < 0f)
            {
                Debug.LogWarning(LogPrefix + " arrivalLiftMeters=" + arrivalLiftMeters
                    + " is not a usable lift; using 0");
                arrivalLiftMeters = 0f;
            }

            if (targets == null || targets.Length == 0)
            {
                Debug.LogWarning(LogPrefix + " no targets wired; every press is refused with reason="
                    + ReasonNoTargets);
                return;
            }

            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] == null)
                {
                    Debug.LogWarning(LogPrefix + " targets[" + i + "] is an empty slot; that index is"
                        + " refused with reason=" + ReasonTargetUnwired);
                }
            }

            VRCPlayerApi player = Networking.LocalPlayer;
            // String concatenation rather than Vector3.ToString(): the repo already logs positions
            // that way (StoryLevelLabBootstrap.Report), and only float.ToString is a proven extern here.
            string startPosition = "unavailable";
            if (player != null) startPosition = "" + player.GetPosition();
            Debug.Log(LogPrefix + " ready targets=" + targets.Length
                + " interactTargetIndex=" + interactTargetIndex
                + " lift=" + arrivalLiftMeters
                + " startPos=" + startPosition);
        }

        // Runs every frame but returns on the first line unless a teleport is being measured.
        private void Update()
        {
            if (!teleportPending) return;
            framesSinceTeleport++;

            if (!loggedImmediateCheck && framesSinceTeleport >= ImmediateCheckFrames)
            {
                loggedImmediateCheck = true;
                LogArrivalCheck("immediate");
            }

            if (loggedSettledCheck || framesSinceTeleport < SettledCheckFrames) return;

            loggedSettledCheck = true;
            teleportPending = false;
            LogArrivalCheck("settled");
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>
        /// World-space press: this object's own collider was interacted with, so send the local
        /// player to <c>interactTargetIndex</c>. One probe instance per button keeps this
        /// a no-argument event, which is all <c>Interact</c> can be.
        /// </summary>
        public override void Interact()
        {
            TeleportLocalPlayerToIndex(interactTargetIndex);
        }

        /// <summary>
        /// The probe's only action, and the seam later product code calls (a room button or
        /// <c>StoryRoundController</c>) instead of pressing a collider. Logs one line on a refusal
        /// and always schedules the two arrival readings on success.
        /// </summary>
        public void TeleportLocalPlayerToIndex(int index)
        {
            int reason;
            if (TryTeleportLocalPlayerToIndex(index, out reason)) return;

            Debug.LogWarning(LogPrefix + " refused index=" + index + " reason=" + reason
                + " (" + DescribeReason(reason) + ")");
        }

        /// <summary>
        /// Same action as <see cref="TeleportLocalPlayerToIndex"/>, but reports the refusal to the
        /// caller instead of only logging it. Not an overload: UdonSharp's supported surface is
        /// one method per name, so the two live under different names.
        /// </summary>
        public bool TryTeleportLocalPlayerToIndex(int index, out int reason)
        {
            reason = ReasonNone;

            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null)
            {
                reason = ReasonNoLocalPlayer;
                return false;
            }

            if (targets == null || targets.Length == 0)
            {
                reason = ReasonNoTargets;
                return false;
            }

            if (index < 0 || index >= targets.Length)
            {
                reason = ReasonIndexOutOfRange;
                return false;
            }

            Transform target = targets[index];
            if (target == null)
            {
                reason = ReasonTargetUnwired;
                return false;
            }

            Vector3 arrival = target.position;
            arrival.y = arrival.y + arrivalLiftMeters;
            if (!KatamariMath.IsFiniteVector(arrival))
            {
                reason = ReasonArrivalNotFinite;
                return false;
            }

            positionBeforeTeleport = player.GetPosition();
            requestedArrival = arrival;
            requestedIndex = index;
            framesSinceTeleport = 0;
            loggedImmediateCheck = false;
            loggedSettledCheck = false;
            teleportPending = true;

            player.TeleportTo(arrival, target.rotation,
                VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, false);

            Debug.Log(LogPrefix + " request index=" + index
                + " before=" + positionBeforeTeleport
                + " arrival=" + arrival);

            // Optional outbound hook; unused by the probe itself and silent while unwired.
            if (arrivalListener != null && arrivalListenerEvent != null
                && arrivalListenerEvent.Length > 0)
            {
                arrivalListener.SendCustomEvent(arrivalListenerEvent);
            }

            return true;
        }

        // ------------------------------------------------------------------ measurement

        private void LogArrivalCheck(string phase)
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null)
            {
                Debug.LogWarning(LogPrefix + " " + phase + " check skipped: no local player");
                return;
            }

            Vector3 now = player.GetPosition();
            float moved = Vector3.Distance(positionBeforeTeleport, now);
            float distanceToArrival = Vector3.Distance(requestedArrival, now);
            bool arrived = distanceToArrival <= ArrivalToleranceMeters;

            Debug.Log(LogPrefix + " " + phase + " index=" + requestedIndex
                + " frames=" + framesSinceTeleport
                + " pos=" + now
                + " movedFromRequest=" + moved
                + " distanceToArrival=" + distanceToArrival
                + " arrived=" + arrived);
        }

        private string DescribeReason(int reason)
        {
            if (reason == ReasonNoLocalPlayer) return "Networking.LocalPlayer is null";
            if (reason == ReasonNoTargets) return "the targets array is empty or not wired";
            if (reason == ReasonIndexOutOfRange) return "the index is outside the targets array";
            if (reason == ReasonTargetUnwired) return "the target slot is empty";
            if (reason == ReasonArrivalNotFinite) return "the arrival point is not finite";
            return "unknown reason";
        }
    }
}
