using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRCKatamari.Pickup;
using VRCKatamari.Story;

namespace VRCKatamari.Hub
{
    /// <summary>
    /// The local player's per-level records: best and last ball diameter and piece count, and
    /// how many rounds finished; kept in VRChat PlayerData so they survive a rejoin.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read-only observer of the round: it watches <see cref="StoryRoundController.GetState"/>
    /// and records once each time a round enters Result without having been aborted. It never
    /// calls into the round, the launcher or the motor's write side, so it cannot change the
    /// hub flow. Which round counts and which is better is <see cref="HubRecordMath"/>'s.
    /// </para>
    /// <para>
    /// Facts recorded: the controller's settled value for the level's own goal (collision
    /// diameter for a size level, confirmed count for a count level); the other fact is read
    /// at the same moment from the frozen ball (motor collision diameter) and the level's
    /// pickup host (confirmed attachment count). No stars: scoring is a detachable module
    /// (OPEN_DECISIONS (m)) and Cancer has none.
    /// </para>
    /// <para>
    /// Records live in memory from the start; PlayerData is written only after the local
    /// <c>OnPlayerRestored</c>, which also loads the saved values of every level that has
    /// not been played in this session yet.
    /// </para>
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubRecordBook : UdonSharpBehaviour
    {
        [SerializeField] private StoryRoundController controller;

        [Tooltip("Level ids in launcher order (the record keys).")]
        [SerializeField] private string[] levelIds;
        [SerializeField] private int[] levelRecordModes;

        [Tooltip("Each level's pickup host, launcher order (piece count of a size level).")]
        [SerializeField] private PickupFieldHost[] levelPickupHosts;

        [Tooltip("Get SendCustomEvent(\"OnHubRecordsChanged\") after a record or a restore.")]
        [SerializeField] private UdonSharpBehaviour[] listeners;

        private bool[] hasRecord;
        private float[] bestDiameters;
        private int[] bestCounts;
        private float[] lastDiameters;
        private int[] lastCounts;
        private int[] plays;
        private bool[] playedThisSession;

        private int lastState = -1;
        private bool restored;

        private void Start()
        {
            EnsureArrays();
        }

        private void Update()
        {
            if (controller == null) return;
            int state = controller.GetState();
            if (state == lastState) return;
            int previous = lastState;
            lastState = state;
            if (state != StoryRoundController.StateResult) return;
            if (previous == -1) return; // scene start in Result: nothing was played here
            if (controller.WasAborted()) return;
            RecordSettledRound();
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.isLocal) return;
            EnsureArrays();
            for (int i = 0; i < hasRecord.Length; i++)
            {
                if (playedThisSession[i] || !IsLevelRecorded(i)) continue;
                string id = levelIds[i];
                int storedPlays;
                if (!PlayerData.TryGetInt(player, HubRecordMath.StorageKey(id,
                    HubRecordMath.FieldPlays), out storedPlays) || storedPlays <= 0) continue;
                float bestD;
                int bestN;
                float lastD;
                int lastN;
                PlayerData.TryGetFloat(player, HubRecordMath.StorageKey(id, HubRecordMath.FieldBestDiameter), out bestD);
                PlayerData.TryGetInt(player, HubRecordMath.StorageKey(id, HubRecordMath.FieldBestCount), out bestN);
                PlayerData.TryGetFloat(player, HubRecordMath.StorageKey(id, HubRecordMath.FieldLastDiameter), out lastD);
                PlayerData.TryGetInt(player, HubRecordMath.StorageKey(id, HubRecordMath.FieldLastCount), out lastN);
                hasRecord[i] = true;
                plays[i] = storedPlays;
                bestDiameters[i] = bestD;
                bestCounts[i] = bestN;
                lastDiameters[i] = lastD;
                lastCounts[i] = lastN;
            }
            restored = true;
            Announce();
        }

        private void RecordSettledRound()
        {
            EnsureArrays();
            int index = controller.GetLevelIndex();
            if (index < 0 || index >= hasRecord.Length) return;
            int mode = StoryLevelMath.PickInt(levelRecordModes, index, HubRecordMath.None);
            if (!HubRecordMath.IsRecorded(mode, false)) return;

            float diameter = controller.GetResultDiameterM();
            int count = controller.GetResultCount();
            if (mode == HubRecordMath.DiameterAndTotal)
            {
                PickupFieldHost host = null;
                if (levelPickupHosts != null && index < levelPickupHosts.Length) host = levelPickupHosts[index];
                count = host != null ? host.GetAttachedCount() : 0;
            }

            bool better = HubRecordMath.IsBetter(mode, diameter, count, hasRecord[index],
                bestDiameters[index], bestCounts[index]);
            if (better)
            {
                bestDiameters[index] = diameter;
                bestCounts[index] = count;
            }
            hasRecord[index] = true;
            lastDiameters[index] = diameter;
            lastCounts[index] = count;
            plays[index]++;
            playedThisSession[index] = true;
            Debug.Log("[hub-record] level=" + levelIds[index] + " d=" + diameter + " n=" + count
                + " best=" + better + " plays=" + plays[index]);

            if (restored)
            {
                string id = levelIds[index];
                PlayerData.SetFloat(HubRecordMath.StorageKey(id, HubRecordMath.FieldBestDiameter), bestDiameters[index]);
                PlayerData.SetInt(HubRecordMath.StorageKey(id, HubRecordMath.FieldBestCount), bestCounts[index]);
                PlayerData.SetFloat(HubRecordMath.StorageKey(id, HubRecordMath.FieldLastDiameter), diameter);
                PlayerData.SetInt(HubRecordMath.StorageKey(id, HubRecordMath.FieldLastCount), count);
                PlayerData.SetInt(HubRecordMath.StorageKey(id, HubRecordMath.FieldPlays), plays[index]);
            }
            Announce();
        }

        public bool IsLevelRecorded(int index)
        {
            return HubRecordMath.IsRecorded(StoryLevelMath.PickInt(levelRecordModes, index,
                HubRecordMath.None), false);
        }

        public int GetLevelCount() { EnsureArrays(); return hasRecord.Length; }

        public bool HasRecord(int index)
        {
            EnsureArrays();
            return index >= 0 && index < hasRecord.Length && hasRecord[index];
        }

        public float GetBestDiameterM(int index) { return HasRecord(index) ? bestDiameters[index] : 0f; }
        public int GetBestCount(int index) { return HasRecord(index) ? bestCounts[index] : 0; }
        public float GetLastDiameterM(int index) { return HasRecord(index) ? lastDiameters[index] : 0f; }
        public int GetLastCount(int index) { return HasRecord(index) ? lastCounts[index] : 0; }
        public int GetPlays(int index) { return HasRecord(index) ? plays[index] : 0; }
        public bool IsRestored() { return restored; }

        private void EnsureArrays()
        {
            if (hasRecord != null) return;
            int n = levelIds == null ? 0 : levelIds.Length;
            hasRecord = new bool[n];
            bestDiameters = new float[n];
            bestCounts = new int[n];
            lastDiameters = new float[n];
            lastCounts = new int[n];
            plays = new int[n];
            playedThisSession = new bool[n];
        }

        private void Announce()
        {
            if (listeners == null) return;
            for (int i = 0; i < listeners.Length; i++)
            {
                if (listeners[i] != null) listeners[i].SendCustomEvent("OnHubRecordsChanged");
            }
        }
    }
}
