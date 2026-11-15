using UnityEngine;
using VRCKatamari.Core;
using VRCKatamari.Hub;

namespace VRCKatamari.Story.Hub
{
    /// <summary>
    /// The room side of the room/level loop: which navigation phase the player is in,
    /// which level circle is being stood in, whether the circle may start a round this
    /// frame, and which one-line prompt belongs on the room's screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Navigation is not the round. <see cref="StoryRoundController"/> keeps the only
    /// round state machine (Idle/Running/Presenting/Result) and
    /// <see cref="HubLevelLauncher"/> keeps the only transfer machine (teleport, arrival
    /// check, return). This class projects those two into the four things a room has to
    /// show - Hub, LevelLoading, Playing, Result - and owns nothing else: no timer, no
    /// teleport, no result, no score. The projection is here (and not in the host) so the
    /// whole loop can be driven in EditMode without a scene, which is what the tests do.
    /// </para>
    /// <para>
    /// Why a second phase value is not a second state machine. The round states answer
    /// "what is the game doing"; the phases answer "what is the room showing and is the
    /// player allowed to start again". They are updated from readings, never written by
    /// the round, and the round is never driven by a phase: the only transition that
    /// starts anything is <see cref="PhaseLoading"/>, which the host enters after
    /// <see cref="HubLevelLauncher.TryStartLevel"/> accepted the press - the launcher then
    /// calls the round's own <c>BeginRound()</c>/<c>RequestRetry()</c>. So there is one
    /// owner per rule: the round owns the round, the launcher owns moving, this class owns
    /// the room's phase and prompt.
    /// </para>
    /// <para>
    /// The start is a circle, not a Station, not a key. VRChat delivers <c>InputUse</c>
    /// globally (there is no "pressed E while looking at this object" event), the project's
    /// in-world interaction is a collider plus a left click, and both of those need a
    /// physical button and a ray. A circle only needs <c>VRCPlayerApi.GetPosition()</c>,
    /// which the launcher already uses and which ClientSim and a real client both answer,
    /// so it works wherever a player can walk at all. The trade-off is accidental starts,
    /// which the re-arm rule handles: one start disarms the circle, and it only arms again
    /// after the player has been seen outside every circle. The room spawn is outside every
    /// circle, so returning from a round does not start another one.
    /// </para>
    /// <para>
    /// No input gate is added. The hub never reads a move axis and never writes
    /// <c>DesktopMoveInput</c>; the two existing gates (ride, owned by
    /// <c>BallRideStation</c>, and round, owned by <c>StoryRoundController</c>) keep their
    /// owners and their meaning. The circle is a navigation trigger, not a driving input.
    /// </para>
    /// <para>
    /// Fail closed. An unknown phase or round state, a non-finite point and an unusable
    /// radius all answer "do not start / no prompt", because a NaN distance compares false
    /// against every tolerance and would otherwise read as "outside forever" or "inside
    /// forever" depending on the comparison. UdonSharp has no exceptions, so every refusal
    /// is a false return plus a reason value the caller can log.
    /// </para>
    /// </remarks>
    public static class HubRoomMath
    {
        // ---- Phases: what the room is showing ----
        /// <summary>The player is in the room and may start a level.</summary>
        public const int PhaseHub = 0;

        /// <summary>A start was accepted; the launcher is moving the player into the level.</summary>
        public const int PhaseLoading = 1;

        /// <summary>The round is running (the player is in the level).</summary>
        public const int PhasePlaying = 2;

        /// <summary>The round settled; the result card is on the room's board.</summary>
        public const int PhaseResult = 3;

        // ---- Prompts: one line per situation, so the room never has to invent text ----
        /// <summary>Nothing to say.</summary>
        public const int PromptNone = 0;

        /// <summary>In the room, near a circle and outside it: walk in to start.</summary>
        public const int PromptApproach = 1;

        /// <summary>Result phase, near the circle: walk in for another attempt.</summary>
        public const int PromptRetryApproach = 2;

        /// <summary>Armed and inside the circle, but the dwell time has not elapsed.</summary>
        public const int PromptStandIn = 3;

        /// <summary>Armed, inside the circle, dwell done: the start fires this frame.</summary>
        public const int PromptStarting = 4;

        /// <summary>Inside the circle but not armed: leave and come back.</summary>
        public const int PromptReArm = 5;

        /// <summary>The launcher is moving the player into the level.</summary>
        public const int PromptLoading = 6;

        /// <summary>The clock is running.</summary>
        public const int PromptPlaying = 7;

        /// <summary>Result phase, in the room, away from the circle.</summary>
        public const int PromptResult = 8;

        /// <summary>Result phase, but the player is not back in the room yet.</summary>
        public const int PromptResultAway = 9;

        /// <summary>A value that is not one of the four phases.</summary>
        public static bool IsPhase(int phase)
        {
            if (phase == PhaseHub) return true;
            if (phase == PhaseLoading) return true;
            if (phase == PhasePlaying) return true;
            if (phase == PhaseResult) return true;
            return false;
        }

        /// <summary>One short name per phase, for a log line. Never called per frame.</summary>
        public static string PhaseText(int phase)
        {
            if (phase == PhaseHub) return "hub";
            if (phase == PhaseLoading) return "loading";
            if (phase == PhasePlaying) return "playing";
            if (phase == PhaseResult) return "result";
            return "unknown";
        }

        /// <summary>
        /// The next phase, from the current phase plus the readings the host has this
        /// frame.
        /// </summary>
        /// <param name="phase">The current room phase.</param>
        /// <param name="roundState">The round's state constant (read, never written).</param>
        /// <param name="startAccepted">A start was accepted this frame.</param>
        /// <param name="startFailed">
        /// A loading start was measured as not progressing (the host's frame budget ran out
        /// without the round running). The launcher owns its own, shorter, arrival timeout;
        /// this one only stops the room from saying "loading" forever.
        /// </param>
        /// <param name="playerInHub">The local player is inside the room's own radius.</param>
        /// <param name="holdElapsed">The result has been readable for its hold time.</param>
        /// <param name="next">The phase to show this frame.</param>
        /// <returns>
        /// True when the inputs were usable. False means a value was outside its domain and
        /// <paramref name="next"/> is <see cref="PhaseHub"/> (fail closed, never a guess).
        /// </returns>
        /// <remarks>
        /// The round state wins when the two disagree, because it is measured from the owner
        /// of the round: a start that already began (the player was at the entry, so the
        /// launcher opened the round in the same call) must not be held in Loading, and a
        /// round that was started by a sit-down in a scene with no start press must still
        /// show Playing. <c>startFailed</c> is consulted after the round state for the same
        /// reason: a slow frame must not erase a round that is actually running.
        /// </remarks>
        public static bool TryAdvance(int phase, int roundState, bool startAccepted,
            bool startFailed, bool playerInHub, bool holdElapsed, out int next)
        {
            next = PhaseHub;

            if (!IsPhase(phase)) return false;
            if (!HubLoopMath.IsRoundState(roundState)) return false;

            if (phase == PhaseHub)
            {
                if (startAccepted)
                {
                    next = PhaseLoading;
                    return true;
                }
                if (roundState == StoryRoundController.StateRunning)
                {
                    next = PhasePlaying;
                    return true;
                }
                if (roundState == StoryRoundController.StatePresenting ||
                    roundState == StoryRoundController.StateResult)
                {
                    next = PhaseResult;
                    return true;
                }
                next = PhaseHub;
                return true;
            }

            if (phase == PhaseLoading)
            {
                if (roundState == StoryRoundController.StateRunning)
                {
                    next = PhasePlaying;
                    return true;
                }
                if (roundState == StoryRoundController.StatePresenting ||
                    roundState == StoryRoundController.StateResult)
                {
                    next = PhaseResult;
                    return true;
                }
                if (startFailed)
                {
                    next = PhaseHub;
                    return true;
                }
                next = PhaseLoading;
                return true;
            }

            if (phase == PhasePlaying)
            {
                if (roundState == StoryRoundController.StatePresenting ||
                    roundState == StoryRoundController.StateResult)
                {
                    next = PhaseResult;
                    return true;
                }
                if (roundState == StoryRoundController.StateIdle)
                {
                    // The round went back to Idle without the room seeing it settle (a
                    // scene that resets the controller by hand). Showing Playing over an
                    // idle round would be a lie; the room can start again.
                    next = PhaseHub;
                    return true;
                }
                next = PhasePlaying;
                return true;
            }

            // PhaseResult: the round is settled and the card is on the board. A retry is
            // the same start path as the first attempt (the launcher turns Result into
            // RequestRetry), and going back to Hub only needs the player to be back in the
            // room and the result to have been readable for its hold time.
            if (startAccepted)
            {
                next = PhaseLoading;
                return true;
            }
            if (roundState == StoryRoundController.StateRunning)
            {
                next = PhasePlaying;
                return true;
            }
            if (playerInHub && holdElapsed)
            {
                next = PhaseHub;
                return true;
            }
            next = PhaseResult;
            return true;
        }

        /// <summary>
        /// The level circle the local player is standing in, or -1 when none contains
        /// them. At most one index is returned: when two circles overlap, the nearer centre
        /// wins, so "the selected level" is always exactly one level.
        /// </summary>
        /// <remarks>
        /// The caller passes world positions, not Transforms, so the rule can be tested with
        /// three numbers and so the host can keep its own position cache (no allocation per
        /// frame). Entries that are not finite are skipped; an empty or all-invalid array
        /// selects nothing. The radius is inclusive, matching the launcher's arrival
        /// tolerance convention.
        /// </remarks>
        public static bool TryPickZone(Vector3 player, Vector3[] zoneCenters, float radiusMeters,
            out int index)
        {
            index = -1;
            if (zoneCenters == null) return false;
            if (zoneCenters.Length == 0) return false;
            if (!KatamariMath.IsFiniteVector(player)) return false;
            if (!KatamariMath.IsFiniteFloat(radiusMeters)) return false;
            if (radiusMeters <= 0f) return false;

            float best = radiusMeters;
            for (int i = 0; i < zoneCenters.Length; i++)
            {
                Vector3 center = zoneCenters[i];
                if (!KatamariMath.IsFiniteVector(center)) continue;
                float distance = Vector3.Distance(player, center);
                if (!KatamariMath.IsFiniteFloat(distance)) continue;
                if (distance <= best)
                {
                    best = distance;
                    index = i;
                }
            }

            return index >= 0;
        }

        /// <summary>
        /// The armed flag after this frame's reading: standing outside every circle arms the
        /// start again; standing inside keeps the current flag.
        /// </summary>
        /// <remarks>
        /// This is what stops a return to the room from immediately starting another round
        /// if the room spawn happens to be inside a circle: the start disarms, and only a
        /// measured frame outside re-arms. It also means a player who is returned into a
        /// circle has to step out and back in, which the prompt says.
        /// </remarks>
        public static bool NextArmed(bool armed, bool insideAnyZone)
        {
            if (!insideAnyZone) return true;
            return armed;
        }

        /// <summary>
        /// Whether the circle may start (or retry) a level this frame. The launcher still
        /// performs its own checks; this one only answers the room's half.
        /// </summary>
        public static bool CanAutoStart(int phase, bool armed, bool insideZone,
            float dwellSeconds, float dwellRequiredSeconds)
        {
            if (phase != PhaseHub && phase != PhaseResult) return false;
            if (!armed) return false;
            if (!insideZone) return false;
            if (!KatamariMath.IsFiniteFloat(dwellSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(dwellRequiredSeconds)) return false;
            if (dwellRequiredSeconds <= 0f) return true;
            return dwellSeconds >= dwellRequiredSeconds;
        }

        /// <summary>The one line the room's prompt should show, as a prompt id.</summary>
        /// <remarks>
        /// Gated by phase and proximity rather than by an input event, so the same answer is
        /// valid in ClientSim and in a real client. In Hub the approach line is always
        /// readable (the failure this loop removes is "I cannot find how to start"); inside
        /// the circle it becomes the stand-in line, or "leave and come back" when the circle
        /// is not armed. In Result the near/far split returns: near the circle the line is
        /// "another attempt", away from it the line is "the result is on the board", and
        /// outside the room it points at the level's return button. The loading/playing lines
        /// describe the round, not the circle, so they do not depend on position.
        /// </remarks>
        public static int PromptFor(int phase, bool nearZone, bool insideZone, bool armed,
            bool startReady, bool playerInHub)
        {
            if (!IsPhase(phase)) return PromptNone;

            if (phase == PhaseLoading) return PromptLoading;
            if (phase == PhasePlaying) return PromptPlaying;

            if (phase == PhaseResult)
            {
                if (!playerInHub) return PromptResultAway;
                if (insideZone)
                {
                    if (!armed) return PromptReArm;
                    if (startReady) return PromptStarting;
                    return PromptStandIn;
                }
                if (nearZone) return PromptRetryApproach;
                return PromptResult;
            }

            // PhaseHub. The approach line is shown everywhere in the room, not only
            // next to the circle: a player who cannot find the start is the exact
            // failure this loop exists to remove, and a HUD line costs nothing. The
            // near/far split is kept for the result phase, where the card is the
            // important thing and the retry line should not compete with it from afar.
            if (insideZone)
            {
                if (!armed) return PromptReArm;
                if (startReady) return PromptStarting;
                return PromptStandIn;
            }
            return PromptApproach;
        }
    }
}
