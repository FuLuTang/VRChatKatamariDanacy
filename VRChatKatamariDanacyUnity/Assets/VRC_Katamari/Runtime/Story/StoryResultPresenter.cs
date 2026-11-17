using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace VRCKatamari.Story
{
    /// <summary>
    /// Optional result card for one story round: reads the round's finished facts when
    /// it enters <see cref="StoryRoundController.StatePresenting"/>, applies the level's
    /// star table (<see cref="StoryStarMath"/>) and writes the card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this is a separate behaviour. Scoring is the "final grading" module, not part
    /// of the round: the round has to run, time, settle, freeze the ball and be retryable
    /// whether or not a king is ever shown a number. So the dependency points one way -
    /// this behaviour reads the round through its public getters, and the round has no
    /// field, call or log line that names this behaviour. A scene that does not attach it
    /// is a supported configuration, not a broken one: the round settles and simply has
    /// no result card.
    /// </para>
    /// <para>
    /// Polling, not callbacks. The round owns its state machine and must not know who is
    /// watching, so there is no event to subscribe to: every frame this behaviour reads
    /// <c>GetState()</c> and acts only on a state change. Polling is what keeps the arrow
    /// one-way; an event or a "presenter" field on the controller would put scoring back
    /// inside the round. The price is that the order of the two behaviours' Update calls in
    /// a frame is not defined, so the card can appear one frame after the state changed; it
    /// can never appear before the round has frozen the facts, because the round freezes
    /// them inside the same call that changes the state.
    /// </para>
    /// <para>
    /// Written once. The card text is built on the transition into a state, never per
    /// frame: Presenting writes the presenter line ("呈给国王"), Result writes the stars,
    /// the settled number and one comment. The facts are captured on the Presenting
    /// transition, which is the frame after the round froze them, so the card cannot read
    /// a value the round moved on from. Leaving Presenting/Result (a retry, or the next
    /// round's countdown) hides the card and clears its text, so a Text placed outside the
    /// card cannot show last round's outcome.
    /// </para>
    /// <para>
    /// What it owns: the result card panel, its Text, the star thresholds of tiers 2 and
    /// 3, the partial band and the comment lines. Tier 1 is the round's own goal
    /// (<c>GetTargetDiameterM()</c> / <c>GetTargetCount()</c>), read from the round rather
    /// than duplicated, so the card cannot grade against a target the HUD does not show.
    /// The HUD, the start prompt and the retry button stay on the round.
    /// </para>
    /// <para>
    /// Assembly. Own GameObject by project convention, wired to the scene's
    /// <see cref="StoryRoundController"/>. A missing controller link is logged once and
    /// leaves the card alone; a missing card link is skipped, so a presenter that is
    /// wired to the round but has no Text yet cannot throw.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class StoryResultPresenter : UdonSharpBehaviour
    {
        // ---- Scene links (the operator wires these; a null link is skipped) ----
        // The round this card reports on. Read-only in the other direction: the round
        // never holds a reference to this behaviour.
        [SerializeField] private StoryRoundController controller;

        // The panel behind the result card: visible in Presenting and Result only. This
        // behaviour is the single writer of its active state; the round hides the retry
        // button inside it in every state except Result and knows nothing of the panel.
        [SerializeField] private GameObject resultCardRoot;

        // The card's Text: the presenter line during Presenting, the full outcome in
        // Result. Kept apart from the HUD's own lines (target/current/remaining), which
        // stay on the round.
        [SerializeField] private Text resultText;

        // ---- Star table (level data; tier 1 is the round's goal) ----
        // stars[1].requires.target_diameter_m / stars[2].requires.target_diameter_m.
        // Level 1: 1.45 / 1.60 m.
        [SerializeField] private float star2DiameterM = 1.45f;
        [SerializeField] private float star3DiameterM = 1.6f;

        // stars[1].requires.count / stars[2].requires.count. Level 2: 20 / 30.
        [SerializeField] private int star2Count = 20;
        [SerializeField] private int star3Count = 30;

        // evaluation.partial_band: the "just barely" line, never a star.
        [SerializeField] private float partialBand = 0.9f;

        // ---- Per-level star table (multi-level scenes; every array is optional) ----
        // Indexed by the controller's GetLevelIndex(). Empty arrays, or a level index the
        // arrays do not reach (including -1, "no level selected"), use the single fields
        // above: today's behaviour. levelHasStars false = this level has no star
        // thresholds, so the card shows its measurement only and draws no stars or comment.
        [Header("Per-level star table (optional, parallel arrays)")]
        [SerializeField] private bool[] levelHasStars;
        [SerializeField] private float[] levelStar2Diameters;
        [SerializeField] private float[] levelStar3Diameters;
        [SerializeField] private int[] levelStar2Counts;
        [SerializeField] private int[] levelStar3Counts;
        [SerializeField] private float[] levelPartialBands;

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

        // The state observed on the previous frame. A sentinel, not StateIdle, so the
        // first frame counts as a change and clears whatever the scene authored.
        private int lastObservedState = -1;

        // The facts captured on the Presenting transition, and their evaluation. They are
        // read once and never recomputed, so the card cannot disagree with itself between
        // the presenting beat and the result.
        private bool factsCaptured;
        private float settledDiameterM;
        private int settledCount;
        private int stars = StoryStarMath.StarsNone;
        private bool insidePartialBand;

        // False when the level has no star thresholds: the card then draws the measurement only.
        private bool usesStars = true;

        // A missing controller is the only wire this behaviour cannot work around, so it
        // is reported once instead of every frame.
        private bool warnedMissingController;

        public void Update()
        {
            if (controller == null)
            {
                if (!warnedMissingController)
                {
                    warnedMissingController = true;
                    Debug.LogWarning("[StoryResultPresenter] no round controller is wired; the"
                        + " result card stays as the scene authored it. The round itself is"
                        + " unaffected.");
                }
                return;
            }

            int state = controller.GetState();
            // Written once per state, not once per frame: this early return is what keeps
            // the card (and its string building) out of every idle frame.
            if (state == lastObservedState) return;
            lastObservedState = state;

            if (state == StoryRoundController.StatePresenting
                || state == StoryRoundController.StateResult)
            {
                // A practice level has nothing to grade, and an abandoned round has nobody
                // to show it to: no card. Hidden and cleared like Idle/Running below.
                if (controller.GetGoalType() == StoryGoalType.FreeRoam || controller.WasAborted())
                {
                    SetCardVisible(false);
                    if (resultText != null) resultText.text = "";
                    factsCaptured = false;
                    return;
                }
            }

            if (state == StoryRoundController.StatePresenting)
            {
                CaptureFacts();
                if (resultText != null) resultText.text = presenterLine;
                SetCardVisible(true);
                return;
            }
            if (state == StoryRoundController.StateResult)
            {
                // A presenter enabled after the beat already started still reports the
                // round it finds, instead of waiting for the next round to be useful.
                if (!factsCaptured) CaptureFacts();
                WriteResultLine();
                SetCardVisible(true);
                return;
            }

            // Idle and Running: no outcome exists yet, so the card is hidden and its text
            // cleared. Clearing matters for a Text that a scene placed outside the panel.
            SetCardVisible(false);
            if (resultText != null) resultText.text = "";
            factsCaptured = false;
        }

        /// <summary>
        /// Reads the round's settled facts and applies the level's star table to them.
        /// </summary>
        /// <remarks>
        /// The round reports the measurement as a fact and never a star, so this is the
        /// only place the two thresholds (tier 2, tier 3) meet the one number the round
        /// froze. A refused evaluation leaves zero stars and no band, which is the same
        /// verdict as an unmet quest; the measured number is still shown, because it is
        /// what the round actually measured.
        /// </remarks>
        private void CaptureFacts()
        {
            settledDiameterM = 0f;
            settledCount = 0;
            stars = StoryStarMath.StarsNone;
            insidePartialBand = false;
            factsCaptured = true;

            int evaluatedStars;
            bool evaluatedInsidePartialBand;

            int levelIndex = controller.GetLevelIndex();
            usesStars = StoryLevelMath.LevelUsesStars(levelHasStars, levelIndex);
            float band = StoryLevelMath.PickFloat(levelPartialBands, levelIndex, partialBand);

            if (controller.GetGoalType() == StoryGoalType.CollectCategoryCount)
            {
                settledCount = controller.GetResultCount();
                if (!usesStars) return;
                if (!StoryStarMath.TryEvaluateCountStars(settledCount,
                    controller.GetTargetCount(),
                    StoryLevelMath.PickInt(levelStar2Counts, levelIndex, star2Count),
                    StoryLevelMath.PickInt(levelStar3Counts, levelIndex, star3Count), band,
                    out evaluatedStars, out evaluatedInsidePartialBand))
                {
                    return;
                }
                stars = evaluatedStars;
                insidePartialBand = evaluatedInsidePartialBand;
                return;
            }

            settledDiameterM = controller.GetResultDiameterM();
            if (!usesStars) return;
            if (!StoryStarMath.TryEvaluateSizeStars(settledDiameterM,
                controller.GetTargetDiameterM(),
                StoryLevelMath.PickFloat(levelStar2Diameters, levelIndex, star2DiameterM),
                StoryLevelMath.PickFloat(levelStar3Diameters, levelIndex, star3DiameterM), band,
                out evaluatedStars, out evaluatedInsidePartialBand))
            {
                return;
            }
            stars = evaluatedStars;
            insidePartialBand = evaluatedInsidePartialBand;
        }

        private void WriteResultLine()
        {
            // Logged before the null check: whether the round was graded is worth seeing in
            // a lab even when the Text link is not wired yet.
            Debug.Log("[StoryResultPresenter] result level=" + controller.GetLevelId()
                + " stars=" + stars + " partial=" + insidePartialBand
                + " attempt=" + controller.GetAttemptNumber());

            if (resultText == null) return;
            string measuredLabel;
            if (controller.GetGoalType() == StoryGoalType.CollectCategoryCount)
            {
                measuredLabel = "件数 " + settledCount + " / " + controller.GetTargetCount();
            }
            else
            {
                measuredLabel = "直径 " + StoryRoundMath.HundredthsText(settledDiameterM) + " m";
            }
            if (!usesStars)
            {
                // No thresholds for this level: no stars and no grading comment, only what was measured.
                resultText.text = presenterLine + "\n" + measuredLabel;
                return;
            }
            resultText.text = presenterLine + "\n" + StarGlyphs(stars) + "  "
                + measuredLabel + "\n" + CommentLine(stars, insidePartialBand);
        }

        /// <summary>
        /// Shows or hides the card panel for the state just entered.
        /// </summary>
        /// <remarks>
        /// Called only on a state change, so SetActive is not called per frame. A null link
        /// is skipped: a scene may run this behaviour with only the Text wired while the
        /// panel is built.
        /// </remarks>
        private void SetCardVisible(bool visible)
        {
            if (resultCardRoot == null) return;
            resultCardRoot.SetActive(visible);
        }

        private string StarGlyphs(int stars)
        {
            if (stars >= StoryStarMath.StarsMax) return "★★★";
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
            if (stars >= StoryStarMath.StarsMax) return star3Comment;
            if (stars == 2) return star2Comment;
            if (stars == 1) return star1Comment;
            if (insidePartialBand) return partialComment;
            return failComment;
        }
    }
}
