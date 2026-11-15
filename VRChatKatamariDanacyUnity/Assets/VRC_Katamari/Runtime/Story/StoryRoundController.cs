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
    /// Runs one timed story round: countdown, HUD, freeze at the bell, present the
    /// ball, then show the stars the level's thresholds award.
    /// </summary>
    /// <remarks>
    /// <para>
    /// States. Idle (the scene has loaded and the player has not sat down yet) to
    /// Running (the countdown) to Presenting (the ball is offered to the quest giver) to
    /// Result (stars, the measured number and one line of comment). The order is the
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
    /// Measuring. The design evaluates the settled ball diameter and the settled
    /// count of confirmed attachments, never a local prediction (GameModes README
    /// section 2.7). The diameter is the collision diameter,
    /// <c>2 * KatamariBallMotor.GetCollisionRadiusWorldM()</c>, which is the value
    /// the plan names (section 4) and today equals the semantic diameter because
    /// both ratios are 1; the count comes from the pickup host's per-kind confirmed
    /// counter, which is the only owner of "what was really absorbed".
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
    /// carries. Tier 1 of the star table is the objective itself, which validator
    /// rule 9 of the GameModes README enforces, so it is not a separate field.
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

        /// <summary>The result card is on screen. Nothing else changes.</summary>
        public const int StateResult = 3;

        // ---- Scene links (the operator wires these; a null link is skipped) ----
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private DesktopMoveInput input;
        [SerializeField] private PickupFieldHost pickupHost;

        [Header("HUD (uGUI Text)")]
        [SerializeField] private Text targetText;
        [SerializeField] private Text currentText;
        [SerializeField] private Text remainingText;
        [SerializeField] private Text resultText;

        // ---- Level data (one file under Docs/Design/GameModes/Levels/) ----
        // story_01_bench_size.json: reach_target_diameter, 1.25 m, 120 s,
        // stars 1.25 / 1.45 / 1.60, partial_band 0.90, bank off.
        // story_02_fruit_ten.json: collect_category_count, fruit x 10, 120 s,
        // stars 10 / 20 / 30, partial_band 0.90, bank off.
        // A level sets exactly one goal type; the fields of the other one are left
        // at their defaults and are never read.
        [SerializeField] private string levelId = "story_01_bench_size";
        [SerializeField] private StoryGoalType goalType = StoryGoalType.ReachTargetDiameter;

        // quest.objective.target_diameter_m, which is also stars[0].requires.
        [SerializeField] private float targetDiameterM = 1.25f;

        // stars[1].requires.target_diameter_m / stars[2].requires.target_diameter_m.
        [SerializeField] private float star2DiameterM = 1.45f;
        [SerializeField] private float star3DiameterM = 1.6f;

        // quest.objective.count, which is also stars[0].requires.count.
        [SerializeField] private int targetCount = 10;

        // stars[1].requires.count / stars[2].requires.count.
        [SerializeField] private int star2Count = 20;
        [SerializeField] private int star3Count = 30;

        // time_limit_s.
        [SerializeField] private float timeLimitSeconds = 120f;

        // quest.objective.category_id. The host counts by ordinal text match against
        // each prop row's own kind label (PickupFieldHost.GetAttachedCountForKind), so
        // the scene must label every prop of the category with this same string, and
        // what that label has to cover is the level's categories.json entry (fruit is
        // its seven explicit definition ids, GameModes README section 3.2). An empty
        // key counts every confirmed attachment.
        [SerializeField] private string countGoalKind = "fruit";

        // evaluation.partial_band: the "just barely" line, never a star.
        [SerializeField] private float partialBand = 0.9f;

        [Header("Presentation")]
        // Shown while the ball is offered; the result card repeats it on its first line.
        [SerializeField] private string presenterLine = "呈给国王";

        // Level 1's result comments: GameModes README section 3.1, "评语（结算）"
        // (0/1/2/3 星), plus the "just barely" band branch of section 2.4. The level
        // JSON (Docs/Design/GameModes/Levels/) does not carry comment text yet, so
        // these serialized defaults are the level-1 wording; a later level sets its
        // own lines on its scene instance (level 2's king-counts-fruit wording is
        // section 3.2). Stars are keyed the same way as the star thresholds above.
        [SerializeField] private string star3Comment = "好！这才是朕要的球！";
        [SerializeField] private string star2Comment = "不错不错，有点样子了。";
        [SerializeField] private string star1Comment = "嗯，勉强能看。";
        [SerializeField] private string partialComment = "就差一点点，再来一次。";
        [SerializeField] private string failComment = "……就这样？朕的野餐会还没开始呢。再来一次。";

        // How long Presenting lasts before the result card appears. Zero or less
        // skips the beat instead of waiting forever.
        [SerializeField] private float presentingSeconds = 3f;

        [Header("Start flow")]
        // Off for a played level: the scene waits in Idle until the local player sits
        // in the ball's station, which calls NotifyRiderSeated. On for a headless lab
        // (no station, no player to sit down) that must start the clock at load, which
        // is the pre-lobby behaviour.
        [SerializeField] private bool startOnLoad = false;

        // Shown on the result line while Idle: what the player has to do to begin.
        // A level rewording it changes this string on its scene instance only.
        [SerializeField] private string startPrompt = "点击球坐上去开始";

        // ---- Round state ----
        private int state = StateIdle;
        private float startTimeSeconds;
        private float presentingStartTimeSeconds;

        // The settled measurement, frozen when the clock ran out. These are what the
        // result card shows; nothing recomputes them afterwards.
        private int resultStars = StoryRoundMath.StarsNone;
        private bool resultInsidePartialBand;
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
            if (resultText != null)
            {
                // The result line is the one the player already reads for "呈给国王" and
                // for the card, so the start prompt appears where the outcome will.
                resultText.text = startPrompt;
                // A countdown that has not started must not sit on the HUD looking
                // frozen; BeginRound writes the real one back (lastRemainingShown = -1).
                if (remainingText != null) remainingText.text = "";
            }
            else if (remainingText != null)
            {
                // Fallback for a HUD with no result line: the prompt takes the line that
                // would otherwise show a countdown that has not started.
                remainingText.text = startPrompt;
            }

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
        /// only thing still moving; the result card appears on time whether the player is
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
        /// <see cref="RequestRetry"/>. It resets this behaviour's own state and the two
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
            resultStars = StoryRoundMath.StarsNone;
            resultInsidePartialBand = false;
            resultDiameterM = 0f;
            resultCount = 0;

            if (input != null) input.SetRoundGate(true);
            if (pickupHost != null) pickupHost.SetCollecting(true);
            if (resultText != null) resultText.text = "";

            WriteTargetText();
            float remainingSeconds;
            if (!StoryRoundMath.TryRemainingSeconds(startTimeSeconds, Time.time,
                timeLimitSeconds, out remainingSeconds))
            {
                remainingSeconds = 0f;
            }
            WriteCurrentHud();
            WriteRemainingHud(remainingSeconds);
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
            if (state != StatePresenting) return;
            if (Time.time - presentingStartTimeSeconds < presentingSeconds) return;
            ShowResult();
            state = StateResult;
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

        /// <summary>The settled star count (0..3); 0 until the clock has run out.</summary>
        public int GetResultStars()
        {
            return resultStars;
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

        private void UpdateRunning()
        {
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
        /// Freezes the round at the bell and offers the ball.
        /// </summary>
        /// <remarks>
        /// Order matters and is the plan's: input, then collection, then the candidate
        /// queue. Reads the measurement only after the freeze, so the numbers on the
        /// card are the numbers at the bell.
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

            EvaluateSettledRound();

            // Re-show the frozen value on the HUD so the progress line and the result
            // card can never disagree by one frame.
            lastCurrentShown = -1;
            WriteCurrentHud();

            if (resultText != null) resultText.text = presenterLine;
        }

        private void EvaluateSettledRound()
        {
            resultStars = StoryRoundMath.StarsNone;
            resultInsidePartialBand = false;
            resultDiameterM = 0f;
            resultCount = 0;

            if (goalType == StoryGoalType.CollectCategoryCount)
            {
                resultCount = MeasuredCount();
                int stars;
                bool insidePartialBand;
                if (!StoryRoundMath.TryEvaluateCountStars(resultCount, targetCount,
                    star2Count, star3Count, partialBand, out stars, out insidePartialBand))
                {
                    return;
                }
                resultStars = stars;
                resultInsidePartialBand = insidePartialBand;
                return;
            }

            if (motor == null) return;
            // Collision diameter: the evaluated quantity (GameModes README section 2.1)
            // and the one the plan pins to the motor's getter. A motor that never
            // started reads 0, which reads as a diameter below every threshold rather
            // than a false success.
            resultDiameterM = motor.GetCollisionRadiusWorldM() * 2f;
            int sizeStars;
            bool sizeInsidePartialBand;
            if (!StoryRoundMath.TryEvaluateSizeStars(resultDiameterM, targetDiameterM,
                star2DiameterM, star3DiameterM, partialBand, out sizeStars,
                out sizeInsidePartialBand))
            {
                return;
            }
            resultStars = sizeStars;
            resultInsidePartialBand = sizeInsidePartialBand;
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

        private void ShowResult()
        {
            if (resultText == null) return;
            string measuredLabel;
            if (goalType == StoryGoalType.CollectCategoryCount)
            {
                measuredLabel = "件数 " + resultCount + " / " + targetCount;
            }
            else
            {
                measuredLabel = "直径 " + HundredthsText(resultDiameterM) + " m";
            }
            resultText.text = presenterLine + "\n" + StarGlyphs(resultStars) + "  "
                + measuredLabel + "\n" + CommentLine(resultStars, resultInsidePartialBand);
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
        /// string every frame. The measured value is the same one the settled
        /// evaluation reads, so the HUD cannot show a different quantity than the one
        /// the king judges.
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
            float diameterM = motor.GetCollisionRadiusWorldM() * 2f;
            int hundredths = Mathf.FloorToInt(diameterM * 100f);
            if (hundredths == lastCurrentShown) return;
            lastCurrentShown = hundredths;
            if (currentText == null) return;
            currentText.text = "当前 " + HundredthsText(diameterM) + " m";
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

        /// <summary>
        /// A metric length to two decimals, truncated rather than rounded.
        /// </summary>
        /// <remarks>
        /// Truncation is the same convention the playground HUD uses, and it is the
        /// honest one here: a shown number must never claim a size the ball did not
        /// reach, so 1.2499 m reads 1.24 m next to a 1.25 m threshold it missed.
        /// </remarks>
        private string HundredthsText(float value)
        {
            return (Mathf.Floor(value * 100f) / 100f).ToString();
        }

        private string StarGlyphs(int stars)
        {
            if (stars >= StoryRoundMath.StarsMax) return "★★★";
            if (stars == 2) return "★★☆";
            if (stars == 1) return "★☆☆";
            return "☆☆☆";
        }

        /// <summary>
        /// One line of comment for the result card.
        /// </summary>
        /// <remarks>
        /// The lines are presentation data, not rules: they live in the serialized
        /// fields above so a level can reword them without touching this file. The
        /// defaults are level 1's lines from GameModes README section 3.1
        /// ("评语（结算）"); the "just barely" branch is the band branch of section 2.4.
        /// Level 2 has its own wording in section 3.2 (the king counting fruit), which
        /// arrives with that level's instance rather than a second rule here.
        /// </remarks>
        private string CommentLine(int stars, bool insidePartialBand)
        {
            if (stars >= StoryRoundMath.StarsMax) return star3Comment;
            if (stars == 2) return star2Comment;
            if (stars == 1) return star1Comment;
            if (insidePartialBand) return partialComment;
            return failComment;
        }
    }
}
