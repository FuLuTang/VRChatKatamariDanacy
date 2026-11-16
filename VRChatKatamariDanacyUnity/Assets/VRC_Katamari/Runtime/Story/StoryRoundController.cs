using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRCKatamari.BallMotor;
using VRCKatamari.Core;
using VRCKatamari.Input;
using VRCKatamari.Pickup;

namespace VRCKatamari.Story
{
    /// <summary>
    /// Runs one timed story round: countdown, HUD, freeze at the bell, and the settled
    /// facts a presenter may read. It does not grade the round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// States. Idle (the scene has loaded and the player has not sat down yet) to
    /// Running (the countdown) to Presenting (the ball is offered to the quest giver) to
    /// Result (the round is over and its facts are final). The order is the
    /// plan's (Docs/Design/StoryMode/FIRST_PLAYABLE_PLAN.md section 4, step S4) and the
    /// design's (GameModes README section 2.3). The states are plain ints, not an enum,
    /// so a scene or a lab can log them without depending on a type.
    /// </para>
    /// <para>
    /// Where a round starts. Not in <c>Start</c>: a played level loads into Idle, shows
    /// the target and one prompt line and keeps input and collection closed until the
    /// local player sits in the ball's station, which the station reports through
    /// <see cref="NotifyRiderSeated"/>. <c>startOnLoad</c> restores the old
    /// "the clock starts with the scene" for a headless lab. The gate states per state
    /// are in Docs/Design/StoryMode/README.md, "开始流程".
    /// </para>
    /// <para>
    /// What this behaviour is not. It never banks, never awards score and never
    /// writes a ledger field: the story mode has no conversion area and no score
    /// channel at all (GameModes README section 1, "must be written in stone"), and
    /// the level data says so explicitly (bank.enabled=false, score_channel=none,
    /// ledger_write=false). The ledger and the ball are reset by the motor's own
    /// <c>TryResetForNewRound</c> seam (plan step S6); this behaviour only asks for
    /// that reset from <see cref="RequestRetry"/> and owns none of its rules.
    /// </para>
    /// <para>
    /// Scoring is not here either. Stars are the "final grading" module, not part of the
    /// core loop: the round must sit down, time, settle, freeze the ball and retry with no
    /// grader attached at all. So this behaviour reports facts only - the settled collision
    /// diameter, the settled confirmed count, the state, the remaining time and the goal -
    /// and the star thresholds, the comments, the card panel and its Text live on the
    /// optional presenter that watches <see cref="GetState"/>. The dependency is one-way by
    /// construction: nothing in this file calls, holds or names that presenter, which is
    /// why a scene without it is a working configuration (a round with no card) rather than
    /// a broken one.
    /// </para>
    /// <para>
    /// Retry. "再试一次" on the result card is the only caller of
    /// <see cref="RequestRetry"/>. It is accepted only in
    /// <see cref="StateResult"/>: before the card exists there is no button to press,
    /// and a retry during <see cref="StatePresenting"/> would silently skip the
    /// presentation beat that the result card is built from. The motor resets first
    /// (a refusal leaves the card and the round exactly as they were), then the prop
    /// field returns to its recorded start poses, then <see cref="BeginRound"/>
    /// restarts the countdown and HUD with the next attempt number. A retry never
    /// calls <c>TryBank</c> or <c>TryAwardScore</c>.
    /// </para>
    /// <para>
    /// The HUD objects this behaviour owns are the round's, not the card's:
    /// <c>targetText</c>, <c>currentText</c> and <c>remainingText</c> (the quest, the live
    /// progress and the clock), <c>startPromptText</c> (what the player has to do to
    /// begin, an instruction rather than an outcome, hence a Text of its own) and
    /// <c>retryButtonRoot</c> (the button <see cref="RequestRetry"/> accepts a press
    /// from, which restarts the round rather than grading it). The result card panel and
    /// its Text are not here: they belong to the optional presenter, which writes their
    /// active state from the same state value. Two writers for one object would fight, so
    /// this behaviour does not touch the card. Every link is optional, and
    /// <c>ApplyStateVisibility</c> is the single place that writes these objects' active
    /// state, so a scene that ships the button active, or a press from a stale button, is
    /// corrected on the next state change instead of showing a button before the first
    /// round.
    /// </para>
    /// <para>
    /// Measuring. The design evaluates the settled ball diameter and the settled
    /// count of confirmed attachments, never a local prediction (GameModes README
    /// section 2.7). The diameter is the collision diameter,
    /// <c>2 * KatamariBallMotor.GetCollisionRadiusWorldM()</c>, which is the value
    /// the plan names (section 4) and today equals the semantic diameter because
    /// both ratios are 1; the count comes from the pickup host's per-kind confirmed
    /// counter, which is the only owner of "what was really absorbed". Both are frozen
    /// once, at the bell, and read back through <see cref="GetResultDiameterM"/> and
    /// <see cref="GetResultCount"/>; the round never turns them into a verdict.
    /// </para>
    /// <para>
    /// Freezing at the bell. Input is disabled first, then collection, then the
    /// queued candidates are dropped, so the ball brakes, the host stops queuing and
    /// nothing already in flight can still be committed after the clock ran out
    /// (plan section 4). Everything after that reads frozen values only. The input
    /// close is this controller's own gate, <c>DesktopMoveInput.SetRoundGate</c>; the
    /// ride host owns a separate ride gate, so the two cannot overwrite each other and
    /// sitting down again cannot re-open input after the bell.
    /// </para>
    /// <para>
    /// Level data. The serialized fields below carry the fields of one level JSON
    /// file under Docs/Design/GameModes/Levels/; the runtime never reads disk JSON
    /// (AGENTS.md "No editor APIs or disk JSON in Udon runtime"), so until the
    /// Editor importer of GameModes README section 7 item 2 exists, the scene
    /// instance is where the numbers live and each field names the JSON key it
    /// carries. Only the fields the round itself needs are here: the goal and its
    /// target. Tier 1 of the star table is that same objective, and tiers 2 and 3 are
    /// scoring data on the optional presenter (validator rule 9 of the GameModes README
    /// is what makes "tier 1 is the objective" true).
    /// </para>
    /// <para>
    /// Assembly. Own GameObject by project convention (clear ownership and a readable
    /// self-check; a rebuilt hierarchy cannot leave a stale copy running), and it
    /// drives three other behaviours through their documented seams only. Sharing an
    /// object is legal - the engine does not disable a second behaviour on the same
    /// object (Docs/Design/BallMotor/README.md, section on assembly constraints). A null
    /// link is skipped: a missing wire shows up as a ball that keeps rolling, never as
    /// an exception.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class StoryRoundController : UdonSharpBehaviour
    {
        /// <summary>Before Start, or after a caller resets the state by hand.</summary>
        public const int StateIdle = 0;

        /// <summary>The countdown is running and the HUD is live.</summary>
        public const int StateRunning = 1;

        /// <summary>The clock ran out; the ball is offered and frozen.</summary>
        public const int StatePresenting = 2;

        /// <summary>The round is over and its facts are final. Nothing else changes.</summary>
        public const int StateResult = 3;

        // ---- Scene links (the operator wires these; a null link is skipped) ----
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private DesktopMoveInput input;
        [SerializeField] private PickupFieldHost pickupHost;

        [Header("HUD (uGUI Text)")]
        [SerializeField] private Text targetText;
        [SerializeField] private Text currentText;
        [SerializeField] private Text remainingText;

        // The start prompt's own line: what the player has to do to begin
        // ("点击球坐上去开始" by default). It is a separate Text from the result card's text
        // because the two carry different things in different states - an instruction read
        // in Idle, an outcome the card shows from Presenting on - and the result card hides
        // its own contents in Idle, which used to hide the prompt with them. Shown in
        // Idle only, by ApplyStateVisibility. It must be a dedicated object: this Text
        // alone, outside the result card's subtree. A null link is skipped, never thrown.
        [SerializeField] private Text startPromptText;

        // The uGUI Button object (or a world-space button object) that presses "再试一次":
        // visible only in Result, hidden in Idle, Running and Presenting. RequestRetry is
        // still guarded by the state, so a button left visible in the wrong state cannot
        // start or break a round. It restarts the round, which is why the round owns it and
        // the optional result presenter does not.
        [SerializeField] private GameObject retryButtonRoot;

        // ---- Level data (one file under Docs/Design/GameModes/Levels/) ----
        // story_01_bench_size.json: reach_target_diameter, 1.25 m, 120 s, bank off.
        // story_02_fruit_ten.json: collect_category_count, fruit x 10, 120 s, bank off.
        // The star tiers (1.45 / 1.60 m and 20 / 30 items) are scoring data and live on
        // the optional result presenter, not here.
        // A level sets exactly one goal type; the fields of the other one are left
        // at their defaults and are never read.
        [SerializeField] private string levelId = "story_01_bench_size";
        [SerializeField] private StoryGoalType goalType = StoryGoalType.ReachTargetDiameter;

        // quest.objective.target_diameter_m, which is also stars[0].requires.
        [SerializeField] private float targetDiameterM = 1.25f;

        // quest.objective.count, which is also stars[0].requires.count.
        [SerializeField] private int targetCount = 10;

        // time_limit_s.
        [SerializeField] private float timeLimitSeconds = 120f;

        // ---- Per-level kill plane (LEVEL_FORMAT_V2 5.2/5.3) ----
        // A ball whose CENTRE is below this world height is put back at the round's start
        // pose with its velocity cleared, and the event is counted. Off by default: a level
        // authored before this field existed has no such plane, and a plane at y=0 would fire
        // immediately on a level whose floor is 0. The importer writes both fields from
        // meta.activityArea.killPlane; the respawn target is the retry start pose the round
        // already captured, so there is no second copy of the level's start here.
        [SerializeField] private bool killPlaneEnabled = false;
        [SerializeField] private float killPlaneY = 0f;

        // quest.objective.category_id. The host counts by ordinal text match against
        // each prop row's own kind label (PickupFieldHost.GetAttachedCountForKind), so
        // the scene must label every prop of the category with this same string, and
        // what that label has to cover is the level's categories.json entry (fruit is
        // its seven explicit definition ids, GameModes README section 3.2). An empty
        // key counts every confirmed attachment.
        [SerializeField] private string countGoalKind = "fruit";

        [Header("Presentation")]
        // How long Presenting lasts before the round counts as Result. Zero or less
        // skips the beat instead of waiting forever. This is the state's own duration, so
        // it stays on the state machine: the round decides when the result exists, an
        // optional card only decides what to draw when it does.
        [SerializeField] private float presentingSeconds = 3f;

        [Header("Start flow")]
        // Off for a played level: the scene waits in Idle until the local player sits
        // in the ball's station, which calls NotifyRiderSeated. On for a headless lab
        // (no station, no player to sit down) that must start the clock at load, which
        // is the pre-lobby behaviour.
        [SerializeField] private bool startOnLoad = false;

        // Shown on startPromptText while Idle: what the player has to do to begin.
        // A level rewording it changes this string on its scene instance only.
        [SerializeField] private string startPrompt = "点击球坐上去开始";

        // ---- Round state ----
        private int state = StateIdle;
        private float startTimeSeconds;
        private float presentingStartTimeSeconds;

        // The settled measurement, frozen when the clock ran out. These are facts the round
        // reports; nothing recomputes them afterwards and nothing here grades them.
        private float resultDiameterM;
        private int resultCount;

        // What the HUD currently shows, so a per-frame refresh only writes Text when
        // the visible number changed. -1 means "nothing written yet", which is also
        // how 0 gets written on the first frame.
        private int lastCurrentShown = -1;
        private int lastRemainingShown = -1;

        // ---- Retry state (S6) ----
        // The pose the ball is put back to, captured once from the motor's own getters
        // before the first round can move the ball: in Start when the motor has already
        // produced its baseline size, otherwise on the first frame it has. Start order
        // between two behaviours on different objects is not defined, and a motor whose
        // Start has not run reads a zero centre, which would put the ball at the world
        // origin on retry.
        private Vector3 retryStartPosition;
        private float retryStartHeadingDegrees;
        private bool retryStartPoseCaptured;

        // 1-based number of the retry in progress; 0 is the first round. It is both the
        // round-id suffix (levelId + ":" + retryAttempt) and the number on the HUD.
        private int retryAttempt;

        // How many times the per-level kill plane has fired this round attempt. Diagnostics
        // only: it changes no score, no goal and no visibility.
        private int killCount;

        // Upper bound on the "skip round ids already in use" walk. The motor refuses an
        // unchanged id, and a level whose serialized roundId happens to collide must not
        // spin here.
        private const int RetryRoundIdGuard = 8;

        /// <summary>
        /// Starts the round at load, or waits in <see cref="StateIdle"/> for the player
        /// to sit in the ball's station.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The played level has a start: the HUD shows the target and one prompt line,
        /// input and collection are closed, and the clock does not run until the local
        /// player sits down (<see cref="NotifyRiderSeated"/>, called by the station).
        /// <c>startOnLoad</c> keeps the older "the clock starts with the scene" for a
        /// headless lab that has no station and no player to sit down.
        /// </para>
        /// <para>
        /// Closing the round gate here is what makes Idle mean "nothing drives yet": the
        /// ride gate is closed by its own owner and the round gate by this one, so a
        /// player standing next to the ball cannot move it before the start. Collection
        /// is closed for the same reason - a ball that cannot move cannot absorb, and a
        /// host that is still submitting would queue candidates from a stale position.
        /// </para>
        /// </remarks>
        public void Start()
        {
            CaptureRetryStartPoseIfReady();
            if (startOnLoad)
            {
                BeginRound();
                return;
            }
            EnterIdle();
        }

        /// <summary>
        /// Puts the round in Idle without starting the clock: target and start prompt on
        /// the HUD, round gate and collection closed, first sit-down opens the round.
        /// </summary>
        private void EnterIdle()
        {
            state = StateIdle;
            lastCurrentShown = -1;
            lastRemainingShown = -1;

            if (input != null) input.SetRoundGate(false);
            if (pickupHost != null) pickupHost.SetCollecting(false);

            WriteTargetText();
            WriteCurrentHud();
            if (startPromptText != null)
            {
                // The prompt has a line of its own. It must not go on the result line:
                // that Text lives in the result card, which is hidden in Idle (and is not
                // this behaviour's to write), so the prompt written there was hidden too.
                startPromptText.text = startPrompt;
                // A countdown that has not started must not sit on the HUD looking
                // frozen; BeginRound writes the real one back (lastRemainingShown = -1).
                if (remainingText != null) remainingText.text = "";
            }
            else if (remainingText != null)
            {
                // Fallback for a HUD with no prompt line yet: the prompt takes the line
                // that would otherwise show a countdown that has not started.
                remainingText.text = startPrompt;
            }

            ApplyStateVisibility();

            Debug.Log("[StoryRoundController] idle: waiting for the local player to sit in"
                + " the ball's station (startOnLoad=false); level=" + levelId);
        }

        /// <summary>
        /// The local player sat down in the ball's station: begin the round if the level
        /// is still waiting in Idle.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called by <see cref="VRCKatamari.Riding.BallRideStation"/> right after it
        /// opened its ride gate, so on that frame both gates are open and the seated
        /// player's move axes start driving the ball. Order matters: this call opens the
        /// round gate through <see cref="BeginRound"/>, and the ride gate is the other
        /// half of the input condition.
        /// </para>
        /// <para>
        /// In any other state it does nothing, on purpose. Running and Presenting are
        /// already underway and a sit-down must not restart them. In Result the only way
        /// back to a new round is the retry button: <c>BeginRound</c> alone does not reset
        /// the ball, the ledger row, the round id or the props, so a sit-down that
        /// restarted the countdown would be a "new round" played with the old ball - the
        /// button (<see cref="RequestRetry"/>) does that reset first.
        /// </para>
        /// <para>
        /// Leaving the seat mid-round calls nothing here: the round keeps running
        /// (leaving is the player's choice, and pausing would turn the clock into a
        /// resource the player can stop at will). Input is already closed by the ride
        /// gate that the station closes on exit, so the ball brakes and the clock is the
        /// only thing still moving; the round settles on time whether the player is
        /// seated or not.
        /// </para>
        /// </remarks>
        public void NotifyRiderSeated()
        {
            if (state == StateIdle)
            {
                BeginRound();
                Debug.Log("[StoryRoundController] the local player sat down: round started"
                    + " level=" + levelId);
                return;
            }
            Debug.Log("[StoryRoundController] sit-down ignored: the round is not idle"
                + " (state=" + state + "); retry is the result card's button.");
        }

        /// <summary>
        /// Starts or restarts the countdown and re-enables input and collection.
        /// </summary>
        /// <remarks>
        /// This is the seam a lab uses to run a short test level, and the last step of
        /// <see cref="RequestRetry"/>. It resets this behaviour's own state (including the
        /// frozen measurement, so a new round starts with no facts) and the two
        /// things the timeout closes - its round gate and the pickup host's collecting
        /// flag; it does not reset the ball, the ledger or the props. Those are the
        /// motor's and the pickup host's reset seams, and <see cref="RequestRetry"/>
        /// calls them before this.
        /// </remarks>
        public void BeginRound()
        {
            startTimeSeconds = Time.time;
            state = StateRunning;
            lastCurrentShown = -1;
            lastRemainingShown = -1;
            resultDiameterM = 0f;
            resultCount = 0;

            if (input != null) input.SetRoundGate(true);
            if (pickupHost != null) pickupHost.SetCollecting(true);

            WriteTargetText();
            float remainingSeconds;
            if (!StoryRoundMath.TryRemainingSeconds(startTimeSeconds, Time.time,
                timeLimitSeconds, out remainingSeconds))
            {
                remainingSeconds = 0f;
            }
            WriteCurrentHud();
            WriteRemainingHud(remainingSeconds);
            ApplyStateVisibility();
        }

        public void Update()
        {
            // See the retry field block: a Start-order race can leave the first capture
            // without a started motor. Capturing on the first frame that has one is still
            // "before the ball moved", because no axis is pushed until the player moves.
            if (!retryStartPoseCaptured) CaptureRetryStartPoseIfReady();

            if (state == StateRunning)
            {
                UpdateRunning();
                return;
            }
            if (state == StateIdle)
            {
                // EnterIdle runs in this behaviour's Start, and component Start order is not
                // guaranteed: the motor may not have derived its start diameter yet, so the
                // one write there can show 0 m. The write is a no-op while the shown number
                // is unchanged, so repeating it per idle frame costs no string and corrects
                // the line as soon as the motor can answer.
                WriteCurrentHud();
                return;
            }
            if (state != StatePresenting) return;
            if (Time.time - presentingStartTimeSeconds < presentingSeconds) return;
            // Only the state moves. The result card is drawn by whoever is watching this
            // state; announcing "the result exists" is all this behaviour does.
            state = StateResult;
            ApplyStateVisibility();
        }

        /// <summary>
        /// The current state value, <see cref="StateIdle"/>..<see cref="StateResult"/>,
        /// for HUD and lab logs.
        /// </summary>
        public int GetState()
        {
            return state;
        }

        /// <summary>The level this scene instance runs, for a log line or a lab readout.</summary>
        public string GetLevelId()
        {
            return levelId;
        }

        // ---- Facts (read-only; the round reports them, it does not grade them) ----

        /// <summary>The quest type this level sets.</summary>
        /// <remarks>
        /// Shared with whoever reports the result, so it can tell which of the two frozen
        /// measurements to look at without a second copy of the level's goal.
        /// </remarks>
        public StoryGoalType GetGoalType()
        {
            return goalType;
        }

        /// <summary>
        /// quest.objective.target_diameter_m, which is also star tier 1.
        /// </summary>
        /// <remarks>
        /// Only meaningful for a size level: a count level leaves the field at its
        /// serialized default and never reads it, so a caller asks <see cref="GetGoalType"/>
        /// first instead of trusting this number.
        /// </remarks>
        public float GetTargetDiameterM()
        {
            return targetDiameterM;
        }

        /// <summary>
        /// quest.objective.count, which is also star tier 1.
        /// </summary>
        /// <remarks>
        /// Only meaningful for a count level, for the same reason as
        /// <see cref="GetTargetDiameterM"/>: the other goal type leaves it at its
        /// serialized default.
        /// </remarks>
        public int GetTargetCount()
        {
            return targetCount;
        }

        /// <summary>How many times the per-level kill plane has fired this attempt.</summary>
        public int GetKillCount()
        {
            return killCount;
        }

        /// <summary>The settled collision diameter in metres; 0 for a count level.</summary>
        public float GetResultDiameterM()
        {
            return resultDiameterM;
        }

        /// <summary>The settled confirmed attachment count; 0 for a size level.</summary>
        public int GetResultCount()
        {
            return resultCount;
        }

        /// <summary>
        /// Seconds left in the countdown: the full limit in Idle (the clock has not
        /// started), the live value in Running, 0 once the ball is frozen.
        /// </summary>
        /// <remarks>
        /// A fact read, computed with the same clock arithmetic the HUD uses, so a lab or a
        /// result readout cannot show a different countdown than the player's line.
        /// </remarks>
        public float GetRemainingSeconds()
        {
            if (state == StateIdle) return timeLimitSeconds;
            if (state != StateRunning) return 0f;
            float remainingSeconds;
            if (!StoryRoundMath.TryRemainingSeconds(startTimeSeconds, Time.time,
                timeLimitSeconds, out remainingSeconds))
            {
                return 0f;
            }
            return remainingSeconds;
        }

        /// <summary>0 for the first round, 1 for the first retry, 2 for the next, and so on.</summary>
        public int GetAttemptNumber()
        {
            return retryAttempt;
        }

        /// <summary>
        /// "再试一次": restarts the round from its start pose with a baseline ball, the
        /// props back in the field, and the clock and HUD at zero.
        /// </summary>
        /// <returns>
        /// True when the round restarted. False when the state or the motor refused it,
        /// in which case nothing changed and the result card stays on screen.
        /// </returns>
        /// <remarks>
        /// <para>
        /// State. Accepted only in <see cref="StateResult"/>, the state whose card holds
        /// the button. Presenting is refused on purpose: the card does not exist yet, and
        /// a reset there would cut the presentation beat short without telling the player.
        /// Idle and Running are refused because there is no result to retry.
        /// </para>
        /// <para>
        /// Order. The motor's <c>TryResetForNewRound</c> runs first, because it is the
        /// only step with failure reasons that matter: it resets the ledger row, round id,
        /// candidates, rings, velocity, heading and sizes, and returns the ball to the
        /// start pose. A refusal (any reason 1..7, e.g. <c>noLocalRow</c>) leaves the
        /// result card untouched and is logged with the reason code. Only after it
        /// accepts does the prop field reset, and only after that does the countdown
        /// start; a partially reset world is never presented as a new round.
        /// </para>
        /// <para>
        /// Round id. The next id is <c>levelId + ":" + attempt</c>, with the attempt
        /// number taken from this behaviour's own counter and then advanced past any id
        /// the motor already reports through <c>GetRoundId()</c> (the motor refuses an
        /// unchanged id). The attempt number is shown on the HUD's target line.
        /// </para>
        /// <para>
        /// Never banks and never awards: the story mode has no score channel, so this path
        /// does not call <c>TryBank</c> or <c>TryAwardScore</c>.
        /// </para>
        /// </remarks>
        public bool RequestRetry()
        {
            // Re-assert the HUD objects for the state this call arrives in, so a press
            // from a button that was left visible in Idle or Running hides it again. The
            // state guard below is still what decides whether a round restarts: showing
            // or hiding the button never opens that gate.
            ApplyStateVisibility();

            if (state != StateResult)
            {
                Debug.LogWarning("[StoryRoundController] retry ignored: no result card is showing"
                    + " (state=" + state + ").");
                return false;
            }
            if (motor == null)
            {
                Debug.LogWarning("[StoryRoundController] retry refused: no motor is wired;"
                    + " the result card stays.");
                return false;
            }
            if (!retryStartPoseCaptured) CaptureRetryStartPoseIfReady();
            if (!retryStartPoseCaptured)
            {
                Debug.LogWarning("[StoryRoundController] retry refused: the start pose is not"
                    + " captured yet (the motor has not produced a baseline size); the result"
                    + " card stays.");
                return false;
            }

            int nextAttempt = retryAttempt + 1;
            string newRoundId = ComposeRoundId(nextAttempt);
            string currentRoundId = motor.GetRoundId();
            if (currentRoundId == null) currentRoundId = "";
            int guard = 0;
            while (currentRoundId == newRoundId && guard < RetryRoundIdGuard)
            {
                nextAttempt = nextAttempt + 1;
                newRoundId = ComposeRoundId(nextAttempt);
                guard = guard + 1;
            }

            int reason;
            if (!motor.TryResetForNewRound(newRoundId, retryStartPosition,
                retryStartHeadingDegrees, out reason))
            {
                Debug.LogWarning("[StoryRoundController] retry refused by the motor: reason="
                    + reason + " round=" + newRoundId
                    + " ledgerFailure=" + motor.GetLastResetLedgerFailure()
                    + " (reason codes: Docs/Design/BallMotor/README.md, retry section)."
                    + " The result card stays.");
                return false;
            }

            retryAttempt = nextAttempt;
            if (pickupHost != null)
            {
                if (!pickupHost.ResetField())
                {
                    Debug.Log("[StoryRoundController] retry: the pickup field reported nothing"
                        + " to restore (it has not started or has no props); the round still"
                        + " restarts.");
                }
            }

            BeginRound();
            Debug.Log("[StoryRoundController] retry started round=" + newRoundId
                + " attempt=" + retryAttempt);
            return true;
        }

        /// <summary>
        /// The round id of attempt <paramref name="attempt"/>. The motor's own id is read
        /// through <c>GetRoundId()</c> rather than copied here.
        /// </summary>
        private string ComposeRoundId(int attempt)
        {
            return levelId + ":" + attempt;
        }

        /// <summary>
        /// Captures the pose the ball is put back to, once, from
        /// <c>KatamariBallMotor.GetCenterWorld()</c> and <c>GetHeadingDegrees()</c>.
        /// </summary>
        /// <remarks>
        /// A motor that has not run its own <c>Start</c> reads a zero diameter and a zero
        /// centre, which is finite and therefore indistinguishable from a real pose by
        /// value alone. The baseline diameter is the readiness signal (the BallMotor
        /// README uses the same one for "Start has not run"), so the capture waits for a
        /// positive one. On the normal path the motor's Start has already run and the
        /// capture happens in this behaviour's Start; otherwise it happens on the first
        /// Update, before any input can have moved the ball.
        /// </remarks>
        private void CaptureRetryStartPoseIfReady()
        {
            if (retryStartPoseCaptured) return;
            if (motor == null) return;
            float diameter = motor.GetSemanticDiameterWorldM();
            if (!KatamariMath.IsFiniteFloat(diameter)) return;
            if (diameter <= 0f) return;
            Vector3 center = motor.GetCenterWorld();
            float heading = motor.GetHeadingDegrees();
            if (!KatamariMath.IsFiniteVector(center)) return;
            if (!KatamariMath.IsFiniteFloat(heading)) return;
            retryStartPosition = center;
            retryStartHeadingDegrees = heading;
            retryStartPoseCaptured = true;
            Debug.Log("[StoryRoundController] retry start pose captured position="
                + retryStartPosition + " heading=" + retryStartHeadingDegrees);
        }

        /// <summary>
        /// Puts the ball back at the round's start pose when its centre has fallen below the
        /// level's kill plane (LEVEL_FORMAT_V2 5.2/5.3).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The plane is per level and the round owns it: <c>killPlaneEnabled</c> and
        /// <c>killPlaneY</c> are serialized, the importer writes them from
        /// <c>activityArea.killPlane</c>, and this method is the only reader. The motor is
        /// untouched by design - it only supplies its existing <c>TeleportTo</c> and
        /// <c>GetCenterWorld</c> entry points, so a level without a round controller keeps
        /// the old no-kill behaviour.
        /// </para>
        /// <para>
        /// The respawn target is the pose the round already captures for retry, which is the
        /// ball's own start pose as the motor reported it; nothing here re-derives it from
        /// the level data, so there is one start pose in the scene and not two.
        /// </para>
        /// <para>
        /// Speed is cleared by <c>TeleportTo</c> itself (it zeroes the planar and vertical
        /// velocity and resets the contact state, exactly like a retry). The clock keeps
        /// running: a fall costs time, which is what makes the plane a rule rather than a
        /// free reset.
        /// </para>
        /// </remarks>
        private void ApplyKillPlaneIfNeeded()
        {
            if (!killPlaneEnabled) return;
            if (motor == null) return;
            if (!KatamariMath.IsFiniteFloat(killPlaneY)) return;
            if (!retryStartPoseCaptured) CaptureRetryStartPoseIfReady();
            if (!retryStartPoseCaptured) return;

            Vector3 center = motor.GetCenterWorld();
            if (!KatamariMath.IsFiniteVector(center)) return;
            if (center.y >= killPlaneY) return;

            motor.TeleportTo(retryStartPosition);
            killCount = killCount + 1;
            Debug.Log("[StoryRoundController] kill plane: y=" + center.y + " below "
                + killPlaneY + "; ball returned to the round start (kills=" + killCount + ")");
        }

        private void UpdateRunning()
        {
            ApplyKillPlaneIfNeeded();
            float remainingSeconds;
            if (!StoryRoundMath.TryRemainingSeconds(startTimeSeconds, Time.time,
                timeLimitSeconds, out remainingSeconds))
            {
                // A level whose time_limit_s is non-finite or non-positive cannot
                // produce a countdown. Fail closed and settle at once: a broken level
                // must not create a round that never ends.
                remainingSeconds = 0f;
            }
            WriteCurrentHud();
            WriteRemainingHud(remainingSeconds);
            if (remainingSeconds > 0f) return;
            SettleRound();
        }

        /// <summary>
        /// Freezes the round at the bell, records the settled facts and offers the ball.
        /// </summary>
        /// <remarks>
        /// Order matters and is the plan's: input, then collection, then the candidate
        /// queue. Reads the measurement only after the freeze, so the numbers reported
        /// afterwards are the numbers at the bell. No stars, no comment and no card text:
        /// this is where the round stops changing, not where it is judged.
        /// </remarks>
        private void SettleRound()
        {
            state = StatePresenting;
            presentingStartTimeSeconds = Time.time;
            lastRemainingShown = 0;
            WriteRemainingText(0);

            if (input != null) input.SetRoundGate(false);
            if (pickupHost != null) pickupHost.SetCollecting(false);
            if (motor != null) motor.ClearCandidates();

            MeasureSettledRound();

            // Re-show the frozen value on the HUD so the progress line and anything the
            // result reads can never disagree by one frame.
            lastCurrentShown = -1;
            WriteCurrentHud();

            ApplyStateVisibility();
        }

        /// <summary>
        /// Records the settled measurement as a fact: the collision diameter, or the
        /// confirmed count for a count level.
        /// </summary>
        /// <remarks>
        /// The two goal types measure different quantities, so the one this level does not
        /// use stays at 0. Nothing is compared against a threshold here; the star table is
        /// the optional presenter's.
        /// </remarks>
        private void MeasureSettledRound()
        {
            resultDiameterM = 0f;
            resultCount = 0;

            if (goalType == StoryGoalType.CollectCategoryCount)
            {
                resultCount = MeasuredCount();
                return;
            }

            if (motor == null) return;
            // Collision diameter: the evaluated quantity (GameModes README section 2.1)
            // and the one the plan pins to the motor's getter. A motor that never
            // started reads 0, which reads as a diameter below every threshold rather
            // than a false success.
            resultDiameterM = motor.GetCollisionRadiusWorldM() * 2f;
        }

        /// <summary>
        /// Confirmed attachments that count for this quest.
        /// </summary>
        /// <remarks>
        /// The host's counter is the only place "confirmed" exists, so this reads it
        /// instead of counting anything itself. An empty category key means the level
        /// counts every confirmed attachment; an unknown key reads 0, which fails the
        /// level instead of over-counting it.
        /// </remarks>
        private int MeasuredCount()
        {
            if (pickupHost == null) return 0;
            if (countGoalKind == null || countGoalKind.Length == 0)
            {
                return pickupHost.GetAttachedCount();
            }
            return pickupHost.GetAttachedCountForKind(countGoalKind);
        }

        /// <summary>
        /// Writes the start prompt's and the retry button's active state for the current
        /// state.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Which state owns which object. The start prompt (<c>startPromptText</c>) is
        /// owned by Idle: it says what to do to begin, so it has to be readable before the
        /// round exists, and it has to be gone once the countdown owns the HUD. The
        /// "再试一次" button (<c>retryButtonRoot</c>) is visible only in Result, the one
        /// state where <see cref="RequestRetry"/> accepts a press. Idle and Running show
        /// neither, so the scene cannot present a retry button before any round exists.
        /// </para>
        /// <para>
        /// The result card is deliberately not here. It belongs to the optional presenter,
        /// which drives its own visibility from the same state value; this behaviour could
        /// only hide a card it was not allowed to know about, and two writers of one
        /// object's active state would fight over it. The split by object is why attaching
        /// no presenter means "no card" instead of "a card stuck in whatever state the
        /// scene authored".
        /// </para>
        /// <para>
        /// One method, called from every state change, is what keeps the objects
        /// from disagreeing with the state. A null link is skipped, so the headless lab
        /// and any scene without a result card behave exactly as before.
        /// </para>
        /// </remarks>
        private void ApplyStateVisibility()
        {
            bool showRetry = state == StateResult;
            // The prompt belongs to the one state where the player has not started yet and
            // has to be gone once the countdown takes over the HUD; the button belongs to
            // the one state where a press restarts the round.
            bool showStartPrompt = state == StateIdle;
            if (retryButtonRoot != null) retryButtonRoot.SetActive(showRetry);
            if (startPromptText != null) startPromptText.gameObject.SetActive(showStartPrompt);
        }

        private void WriteTargetText()
        {
            if (targetText == null) return;
            string attemptSuffix = "";
            if (retryAttempt > 0) attemptSuffix = "（第 " + retryAttempt + " 次重试）";
            if (goalType == StoryGoalType.CollectCategoryCount)
            {
                targetText.text = "目标 " + targetCount + " 件（" + countGoalKind + "）" + attemptSuffix;
                return;
            }
            targetText.text = "目标 " + targetDiameterM + " m" + attemptSuffix;
        }

        /// <summary>
        /// The quest's progress line: the live diameter, or the live count.
        /// </summary>
        /// <remarks>
        /// Only the visible number is written, so a per-frame refresh does not build a
        /// string every frame. The settled fact is recorded with the same expression, so
        /// the HUD cannot show a different quantity than the one the result is graded
        /// against.
        /// </remarks>
        private void WriteCurrentHud()
        {
            if (goalType == StoryGoalType.CollectCategoryCount)
            {
                int count = MeasuredCount();
                if (count == lastCurrentShown) return;
                lastCurrentShown = count;
                if (currentText == null) return;
                currentText.text = "当前 " + count + " / " + targetCount + " 件";
                return;
            }
            if (motor == null) return;
            // The player-facing diameter carries the original's +0.02 cm display fudge
            // (parity G09). It is a DISPLAY reading: the gate and the size model use the
            // un-offset semantic diameter, so this cannot move a threshold.
            float diameterM = motor.GetHudDiameterWorldM();
            int hundredths = Mathf.FloorToInt(diameterM * 100f);
            if (hundredths == lastCurrentShown) return;
            lastCurrentShown = hundredths;
            if (currentText == null) return;
            currentText.text = "当前 " + StoryRoundMath.HundredthsText(diameterM) + " m";
        }

        private void WriteRemainingHud(float remainingSeconds)
        {
            int wholeSeconds = Mathf.CeilToInt(remainingSeconds);
            if (wholeSeconds == lastRemainingShown) return;
            lastRemainingShown = wholeSeconds;
            WriteRemainingText(wholeSeconds);
        }

        private void WriteRemainingText(int wholeSeconds)
        {
            if (remainingText == null) return;
            remainingText.text = "剩余 " + wholeSeconds + " s";
        }
    }
}
