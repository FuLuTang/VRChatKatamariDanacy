using UnityEngine;
using VRCKatamari.Core;
using VRCKatamari.Story;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// The room/level loop's rules: whether a press may start a level or send the
    /// player back to the room, how a transfer is de-duplicated and paced, which
    /// round state the player is allowed to be away from the level in, and how "the
    /// player is at the point the round thinks they are" is measured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One implementation, no scene. The launcher (<see cref="HubLevelLauncher"/>)
    /// owns the timers, the pending transfer and the teleports; this class owns every
    /// decision it makes. Nothing here reads a Transform, a clock or a Udon behaviour:
    /// the caller passes its own readings, so the rules can be tested in EditMode
    /// without a world and there is no second copy of them.
    /// </para>
    /// <para>
    /// The state machine is not here. <see cref="StoryRoundController"/> keeps the only
    /// one (Idle/Running/Presenting/Result, HUB_LOOP_PLAN section 4); these calls take
    /// that state value as an input and never produce a state of their own. "In the
    /// room" is not a state - it is where the player has to be in Idle, Presenting and
    /// Result, which is what a platform cannot enforce for us (section 2: the level
    /// keeps running after the player leaves, so the invariant has to be enforced, not
    /// assumed).
    /// </para>
    /// <para>
    /// Which existing public method starts a round is a rule, not a detail of the
    /// launcher. From Idle nothing has run yet, so <c>BeginRound()</c> is enough. From
    /// Result the ball is at its settled size and the props are still attached, so the
    /// round must be reset first - the round controller already owns exactly that
    /// combination as <c>RequestRetry()</c> (reset the motor to its captured start pose,
    /// restore the pickup field, then begin), and this project does not grow a second
    /// reset. <see cref="StartModeResetThenBegin"/> is the name of that mapping so the
    /// launcher cannot pick the wrong call by accident.
    /// </para>
    /// <para>
    /// De-duplication and pacing. A transfer takes more than one frame here (the seat
    /// has to be left first, and the arrival is verified before a round is opened), so
    /// a second press must not start a second transfer: <c>transferPending</c> refuses
    /// it with <see cref="ReasonTransferPending"/>. Independently, every accepted
    /// transfer stamps a time and the next one is refused while
    /// <c>now - lastTransfer &lt; cooldown</c>. A negative
    /// <c>lastTransferSeconds</c> means "no transfer yet" - the launcher starts there,
    /// because <c>Time.time</c> itself starts near zero and "never" must not read as
    /// "just now".
    /// </para>
    /// <para>
    /// Failures are false returns plus a reason code, never exceptions (UdonSharp has
    /// no try/catch). Every refused input also leaves the out value at its defined
    /// zero. Non-finite floats are refused everywhere rather than compared: a NaN
    /// distance compares false against every tolerance, which would read as "outside
    /// the level" and make the launcher teleport forever.
    /// </para>
    /// </remarks>
    public static class HubLoopMath
    {
        // ---- What starting a round from a given state costs ----
        /// <summary>No round can be started from this state.</summary>
        public const int StartModeNone = 0;

        /// <summary>Idle: nothing ran yet, <c>BeginRound()</c> is the whole start.</summary>
        public const int StartModeBeginRound = 1;

        /// <summary>
        /// Result: the ball is settled and the props are attached, so the round has to
        /// be reset and restarted - <c>RequestRetry()</c> is that combination, already
        /// implemented once in the round.
        /// </summary>
        public const int StartModeResetThenBegin = 2;

        // ---- Reason codes (0 is success; the launcher logs the number and the text) ----
        /// <summary>Accepted.</summary>
        public const int ReasonNone = 0;

        /// <summary>A transfer (start or return) is already in flight.</summary>
        public const int ReasonTransferPending = 1;

        /// <summary>The last transfer is too recent.</summary>
        public const int ReasonCooldown = 2;

        /// <summary>The button's level index has no entry in the launcher's array.</summary>
        public const int ReasonLevelIndexOutOfRange = 3;

        /// <summary>A round can only be started from Idle or Result.</summary>
        public const int ReasonStartNotInIdleOrResult = 4;

        /// <summary>A round is still running, so the player may not leave the level.</summary>
        public const int ReasonRoundStillRunning = 5;

        /// <summary>The state is not one of the round's four values.</summary>
        public const int ReasonReturnNotAllowedInState = 6;

        /// <summary>No local player (only possible during startup/shutdown).</summary>
        public const int ReasonNoLocalPlayer = 7;

        /// <summary>The computed arrival point is not finite.</summary>
        public const int ReasonPositionNotFinite = 8;

        /// <summary>The level entry transform slot is empty.</summary>
        public const int ReasonLevelEntryUnwired = 9;

        /// <summary>The player never reached the level entry, so no round was opened.</summary>
        public const int ReasonArrivalNotReached = 10;

        /// <summary>The round changed state while a start was in flight.</summary>
        public const int ReasonRoundStateChanged = 11;

        /// <summary>The round refused the reset/restart the launcher asked for.</summary>
        public const int ReasonRoundRefusedRestart = 12;

        /// <summary>No round controller is wired.</summary>
        public const int ReasonNoController = 13;

        /// <summary>No room point is wired, so there is nowhere to return to.</summary>
        public const int ReasonNoRoomPoint = 14;

        /// <summary>
        /// The state value must be one of the round's four; anything else means a
        /// mis-wired controller reference or a state constant that moved, and a
        /// refusal is safer than guessing.
        /// </summary>
        public static bool IsRoundState(int roundState)
        {
            if (roundState == StoryRoundController.StateIdle) return true;
            if (roundState == StoryRoundController.StateRunning) return true;
            if (roundState == StoryRoundController.StatePresenting) return true;
            if (roundState == StoryRoundController.StateResult) return true;
            return false;
        }

        /// <summary>
        /// Which public call starts the round for <paramref name="roundState"/>.
        /// </summary>
        /// <remarks>
        /// Idle and Result are the only two states a start is legal from: in Running the
        /// clock is already going, and in Presenting the round has settled and is about
        /// to stand still for <c>presentingSeconds</c>, so a start there would either
        /// double-start or restart a round whose facts are being shown.
        /// </remarks>
        public static bool TryStartMode(int roundState, out int startMode)
        {
            startMode = StartModeNone;
            if (roundState == StoryRoundController.StateIdle)
            {
                startMode = StartModeBeginRound;
                return true;
            }
            if (roundState == StoryRoundController.StateResult)
            {
                startMode = StartModeResetThenBegin;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a press on the room's "start" button may begin a transfer into
        /// <paramref name="levelIndex"/> right now.
        /// </summary>
        /// <remarks>
        /// Order of the checks is the order the player can act on them: a transfer in
        /// flight is the most concrete reason, then a bad index (a wiring error the
        /// operator sees immediately), then the round state, then pacing. The start mode
        /// is asked for separately so this call has one out value; a caller that needs
        /// the mode calls <see cref="TryStartMode"/> after this returned true, which it
        /// is then guaranteed to accept.
        /// </remarks>
        public static bool TryDecideStart(int roundState, int levelIndex, int levelCount,
            bool transferPending, float nowSeconds, float lastTransferSeconds,
            float cooldownSeconds, out int reason)
        {
            reason = ReasonNone;

            if (transferPending)
            {
                reason = ReasonTransferPending;
                return false;
            }

            if (levelIndex < 0 || levelIndex >= levelCount)
            {
                reason = ReasonLevelIndexOutOfRange;
                return false;
            }

            int startMode;
            if (!TryStartMode(roundState, out startMode))
            {
                reason = ReasonStartNotInIdleOrResult;
                return false;
            }

            if (IsCoolingDown(nowSeconds, lastTransferSeconds, cooldownSeconds))
            {
                reason = ReasonCooldown;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether a press on the "back to the room" button may send the player to the
        /// room right now.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Allowed in Idle, Presenting and Result, refused in Running. The Running refusal
        /// is the plan's requirement (HUB_LOOP_PLAN section 2: a round that is Running has
        /// the player in the level) and it is a refusal rather than an abandon because the
        /// round controller exposes no public "end the round now" call - see the launcher's
        /// remarks for what that would need. The player is not trapped: the clock always
        /// ends the round, and the same button works again in Result.
        /// </para>
        /// <para>
        /// Idle is allowed because a move to the room cannot break anything there (no
        /// round is running and no facts are frozen) and it gives the operator one plain
        /// "go to the room" press while building the loop. An unknown state value is
        /// refused: nothing is known about the round, so a transfer is not risked.
        /// </para>
        /// </remarks>
        public static bool TryDecideReturn(int roundState, bool transferPending,
            float nowSeconds, float lastTransferSeconds, float cooldownSeconds, out int reason)
        {
            reason = ReasonNone;

            if (transferPending)
            {
                reason = ReasonTransferPending;
                return false;
            }

            if (roundState == StoryRoundController.StateRunning)
            {
                reason = ReasonRoundStillRunning;
                return false;
            }

            if (!IsRoundState(roundState))
            {
                reason = ReasonReturnNotAllowedInState;
                return false;
            }

            if (IsCoolingDown(nowSeconds, lastTransferSeconds, cooldownSeconds))
            {
                reason = ReasonCooldown;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether the last transfer is still too recent to allow another.
        /// </summary>
        /// <remarks>
        /// A negative <paramref name="lastTransferSeconds"/> means no transfer has
        /// happened yet and is never cooling down. A non-finite reading or cooldown is
        /// treated as cooling down (fail closed): the cost of one refused press is a log
        /// line, the cost of an unpaced transfer loop is a player being moved every
        /// frame.
        /// </remarks>
        public static bool IsCoolingDown(float nowSeconds, float lastTransferSeconds,
            float cooldownSeconds)
        {
            if (!KatamariMath.IsFiniteFloat(nowSeconds)) return true;
            if (!KatamariMath.IsFiniteFloat(lastTransferSeconds)) return true;
            if (!KatamariMath.IsFiniteFloat(cooldownSeconds)) return true;

            // "Never transferred" is the launcher's starting value, not a time.
            if (lastTransferSeconds < 0f) return false;
            if (cooldownSeconds <= 0f) return false;

            return (nowSeconds - lastTransferSeconds) < cooldownSeconds;
        }

        /// <summary>
        /// A point lifted by <paramref name="liftMeters"/> along Y, for use as a
        /// teleport destination.
        /// </summary>
        /// <remarks>
        /// A Transform marking the floor surface puts the player capsule a few
        /// centimetres inside the floor collider, and the client then depenetrates the
        /// player - which reads exactly like "the teleport did not work". The probe
        /// (Labs/HubLoop/PlayerTeleportProbe.cs) lifts by 0.1 m for that reason and this
        /// keeps the same convention, so a point that worked for the probe works here.
        /// A negative or non-finite lift is refused rather than clamped silently.
        /// </remarks>
        public static bool TryLiftedPoint(Vector3 point, float liftMeters, out Vector3 lifted)
        {
            lifted = point;

            if (!KatamariMath.IsFiniteVector(point)) return false;
            if (!KatamariMath.IsFiniteFloat(liftMeters)) return false;
            if (liftMeters < 0f) return false;

            Vector3 result = point;
            result.y = point.y + liftMeters;
            if (!KatamariMath.IsFiniteVector(result)) return false;

            lifted = result;
            return true;
        }

        /// <summary>
        /// Whether two world points are within <paramref name="toleranceMeters"/> of each
        /// other, with every unusable input answering false.
        /// </summary>
        /// <remarks>
        /// Used for two questions that must agree: "did the player actually arrive" after
        /// a teleport, and "is the player still inside the level island" while a round
        /// runs. Inclusive at the tolerance, so a measured 0.5 m against a 0.5 m tolerance
        /// counts as arrived - the plan's own acceptance test is "within 0.5 m"
        /// (HUB_LOOP_PLAN section 9). A negative or non-finite tolerance is refused
        /// instead of being read as "everything is far away".
        /// </remarks>
        public static bool IsWithin(Vector3 point, Vector3 target, float toleranceMeters)
        {
            if (!KatamariMath.IsFiniteVector(point)) return false;
            if (!KatamariMath.IsFiniteVector(target)) return false;
            if (!KatamariMath.IsFiniteFloat(toleranceMeters)) return false;
            if (toleranceMeters < 0f) return false;

            float distance = Vector3.Distance(point, target);
            if (!KatamariMath.IsFiniteFloat(distance)) return false;

            return distance <= toleranceMeters;
        }

        /// <summary>
        /// Whether the launcher has to pull the local player back into the level this
        /// frame, because a round is running and the player is outside the level island.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Running is the one state where the level must contain the player (HUB_LOOP_PLAN
        /// section 2). A respawn from the menu takes the player to the world spawn, which
        /// is the room; a fall out of the world does the same. The round keeps running
        /// through that, so without this correction the round would tick down with nobody
        /// in the level - exactly the "playing to an empty arena" the section forbids.
        /// This is not a fence around the player: the caller's radius is far larger than
        /// the island and far smaller than the room distance, so only respawn-class moves
        /// trip it.
        /// </para>
        /// <para>
        /// The attempt limit exists because a broken teleport must not become a per-frame
        /// loop: after <paramref name="holdAttemptLimit"/> attempts the launcher logs once
        /// and stops trying, leaving the round to settle on its own.
        /// </para>
        /// </remarks>
        public static bool ShouldHoldInLevel(int roundState, bool insideLevelArea,
            int holdAttemptsUsed, int holdAttemptLimit)
        {
            if (roundState != StoryRoundController.StateRunning) return false;
            if (insideLevelArea) return false;
            if (holdAttemptLimit <= 0) return false;

            // A negative counter is read as "no attempt yet" rather than as a reason to
            // stop; the caller's field is never negative.
            int attempts = holdAttemptsUsed;
            if (attempts < 0) attempts = 0;

            return attempts < holdAttemptLimit;
        }

        /// <summary>
        /// The hold-in-level rule with the two facts the 2026-11-16 ClientSim run added:
        /// a seated player is in the level no matter what <c>GetPosition</c> answers, and a
        /// start needs a short grace window before the correction may move anyone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Why seated counts as in-level. A seated avatar is carried by the station; a
        /// client can report its position at the station, at the seat, or somewhere in
        /// between, and none of those is a useful "did they leave the level" reading.
        /// The station already owns the only reliable seated flag
        /// (<c>BallRideStation.IsLocalPlayerSeated</c>), so the correction asks it instead
        /// of measuring a point. This also stops a correction from teleporting a seated
        /// player, which the station could refuse or turn into a stand-up.
        /// </para>
        /// <para>
        /// Why the grace window. The start is two-phase (teleport, then measure) and a
        /// client may spend a frame or two applying the move, landing, or respawning. A
        /// correction that fires in that window fights the start it was meant to protect;
        /// the 2026-11-16 run showed a respawn being pulled back three times because the
        /// hold saw the room spawn before the player had settled. The window starts when a
        /// round actually opens, so a retry gets its own window.
        /// </para>
        /// <para>
        /// Fail closed on non-finite readings: a NaN would compare false against every
        /// distance and turn the correction into a per-frame teleport loop. A negative
        /// <paramref name="lastStartSeconds"/> means no start yet and skips the window.
        /// </para>
        /// </remarks>
        public static bool ShouldHoldInLevelNow(int roundState, bool insideLevelArea,
            bool seated, float nowSeconds, float lastStartSeconds, float graceSeconds,
            int holdAttemptsUsed, int holdAttemptLimit)
        {
            if (roundState != StoryRoundController.StateRunning) return false;
            // Seated is the strongest "in the level" fact there is; never move a rider.
            if (seated) return false;
            if (insideLevelArea) return false;
            if (holdAttemptLimit <= 0) return false;
            if (!KatamariMath.IsFiniteFloat(nowSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(lastStartSeconds)) return false;
            if (!KatamariMath.IsFiniteFloat(graceSeconds)) return false;
            if (graceSeconds < 0f) graceSeconds = 0f;

            if (lastStartSeconds >= 0f)
            {
                float sinceStart = nowSeconds - lastStartSeconds;
                if (!KatamariMath.IsFiniteFloat(sinceStart)) return false;
                if (sinceStart < graceSeconds) return false;
            }

            int attempts = holdAttemptsUsed;
            if (attempts < 0) attempts = 0;
            return attempts < holdAttemptLimit;
        }

        /// <summary>
        /// Whether an unanswered seat request should be asked again inside its window.
        /// </summary>
        /// <remarks>
        /// <c>UseStation</c> is asynchronous, and the first request can land while the
        /// player is still falling from the arrival teleport. Retrying on a fixed interval
        /// is what makes a scripted seat robust on a slow frame; the limit stops a client
        /// without station support from calling it every frame. A negative frame delta or a
        /// non-positive interval means "no waiting information" and refuses the retry.
        /// </remarks>
        public static bool ShouldRetrySeat(bool seated, int attemptsUsed, int attemptLimit,
            int framesSinceRequest, int retryIntervalFrames)
        {
            if (seated) return false;
            if (attemptLimit <= 0) return false;
            if (framesSinceRequest < 0) return false;
            if (attemptsUsed < 0) attemptsUsed = 0;
            if (attemptsUsed >= attemptLimit) return false;
            if (retryIntervalFrames <= 0) return true;
            return framesSinceRequest >= retryIntervalFrames;
        }

        /// <summary>No usable arrival point: neither the seat nor the level entry.</summary>
        public const int ArrivalNone = 0;

        /// <summary>The player is sent to the ball's seat transform.</summary>
        public const int ArrivalSeat = 1;

        /// <summary>The player is sent to the level's authored entry (its player start).</summary>
        public const int ArrivalEntry = 2;

        /// <summary>
        /// How far the authored entry may be from the ball's seat and still be trusted.
        /// A player start sits a couple of metres behind the ball; the 2026-11-16 run had an
        /// entry 14.5 m from the ball, in the void.
        /// </summary>
        public const float DefaultEntryTrustMeters = 12f;

        /// <summary>
        /// Where a start sends the player: the level's authored entry when it is near the
        /// ball, else the ball's seat, else nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The entry is the level's player start (the importer's <c>Markers/PlayerSpawn</c>:
        /// on the floor, behind the ball, facing it), so it is the right landing spot and it
        /// does not move when a retry puts the ball back. The seat is kept as the fallback
        /// because it is always next to the ball. The guard is the distance between the two:
        /// an entry that is far from the ball it belongs to is a wiring mistake, and the seat
        /// is the safe answer then. A negative or non-finite distance (no way to measure it)
        /// also falls back to the seat. A non-positive trust radius means "use the default".
        /// </para>
        /// <para>
        /// With no seat at all (no station wired, or auto-seat off) the entry is the only
        /// destination and is used without a distance check - there is nothing to compare it
        /// with, and it is what the scene says.
        /// </para>
        /// </remarks>
        public static int ChooseArrival(bool seatAvailable, bool entryAvailable,
            float entryToSeatMeters, float trustMeters)
        {
            if (!entryAvailable) return seatAvailable ? ArrivalSeat : ArrivalNone;
            if (!seatAvailable) return ArrivalEntry;

            if (!KatamariMath.IsFiniteFloat(entryToSeatMeters) || entryToSeatMeters < 0f)
            {
                return ArrivalSeat;
            }
            if (!KatamariMath.IsFiniteFloat(trustMeters) || trustMeters <= 0f)
            {
                trustMeters = DefaultEntryTrustMeters;
            }
            return entryToSeatMeters <= trustMeters ? ArrivalEntry : ArrivalSeat;
        }

        /// <summary>One English phrase per reason code, for the launcher's log lines.</summary>
        public static string DescribeReason(int reason)
        {
            if (reason == ReasonNone) return "no refusal";
            if (reason == ReasonTransferPending) return "a transfer is already in flight";
            if (reason == ReasonCooldown) return "the last transfer is too recent";
            if (reason == ReasonLevelIndexOutOfRange) return "no level entry for that index";
            if (reason == ReasonStartNotInIdleOrResult) return "the round is not idle or finished";
            if (reason == ReasonRoundStillRunning) return "a round is still running";
            if (reason == ReasonReturnNotAllowedInState) return "the round state is not usable";
            if (reason == ReasonNoLocalPlayer) return "no local player";
            if (reason == ReasonPositionNotFinite) return "the arrival point is not finite";
            if (reason == ReasonLevelEntryUnwired) return "the level entry slot is empty";
            if (reason == ReasonArrivalNotReached) return "the player did not reach the level entry";
            if (reason == ReasonRoundStateChanged) return "the round changed state mid-transfer";
            if (reason == ReasonRoundRefusedRestart) return "the round refused the restart";
            if (reason == ReasonNoController) return "no round controller is wired";
            if (reason == ReasonNoRoomPoint) return "no room point is wired";
            return "unknown reason";
        }
    }
}
