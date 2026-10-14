// Unity 2022.3 / Udon-portable algorithm sketch; NOT compiled or attached to a scene.
// Deliberately owns records only: no renderer budget, visibility or recycling.
using UnityEngine;

namespace VrcKatamari.BallLayers.AttachmentDraft
{
    [System.Serializable]
    public struct ConfirmedAttachment
    {
        public string contextId;
        public string ballId;
        public string instanceId;
        public int generation;
        public long attachSequence;
        public string eventId;
        public Vector3 ballPosition;
        public Quaternion ballRotation;
        public Vector3 objectPosition;
        public Quaternion objectRotation;
        public Vector3 objectScale;
    }

    [System.Serializable]
    public struct AttachmentRecordDraft
    {
        public string contextId;
        public string ballId;
        public string instanceId;
        public int generation;
        public long attachSequence;
        public string eventId;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 objectScale;
        public bool active;
        public bool revoked;
    }

    // Bounded fixed storage. Caller provides authoritative sequence and event identity.
    public sealed class AttachmentRecordsDraft
    {
        public const int Capacity = 128;
        private AttachmentRecordDraft[] records = new AttachmentRecordDraft[Capacity];
        private int count;

        public int Count { get { return count; } }
        public AttachmentRecordDraft Get(int index) { return records[index]; }

        private bool SameIdentity(AttachmentRecordDraft a, ConfirmedAttachment b)
        {
            return a.contextId == b.contextId && a.ballId == b.ballId &&
                a.instanceId == b.instanceId && a.generation == b.generation &&
                a.attachSequence == b.attachSequence && a.eventId == b.eventId;
        }

        private bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        private bool Valid(ConfirmedAttachment e)
        {
            if (string.IsNullOrEmpty(e.contextId) || string.IsNullOrEmpty(e.ballId) ||
                string.IsNullOrEmpty(e.instanceId) || string.IsNullOrEmpty(e.eventId) ||
                e.generation < 0 || e.attachSequence < 0) return false;
            if (!Finite(e.ballPosition.x) || !Finite(e.ballPosition.y) || !Finite(e.ballPosition.z) ||
                !Finite(e.objectPosition.x) || !Finite(e.objectPosition.y) || !Finite(e.objectPosition.z) ||
                !Finite(e.objectScale.x) || !Finite(e.objectScale.y) || !Finite(e.objectScale.z) ||
                e.objectScale.x <= 0f || e.objectScale.y <= 0f || e.objectScale.z <= 0f) return false;
            float bq = e.ballRotation.x*e.ballRotation.x + e.ballRotation.y*e.ballRotation.y +
                       e.ballRotation.z*e.ballRotation.z + e.ballRotation.w*e.ballRotation.w;
            float oq = e.objectRotation.x*e.objectRotation.x + e.objectRotation.y*e.objectRotation.y +
                       e.objectRotation.z*e.objectRotation.z + e.objectRotation.w*e.objectRotation.w;
            return Finite(bq) && Finite(oq) && bq > 0.000001f && oq > 0.000001f;
        }

        // Return -1 invalid/conflict/full, existing/new index otherwise. Validate fully before write.
        public int Apply(ConfirmedAttachment e)
        {
            if (!Valid(e)) return -1;
            Quaternion ballQ = e.ballRotation.normalized;
            Quaternion objectQ = e.objectRotation.normalized;
            Vector3 localPosition = Quaternion.Inverse(ballQ) * (e.objectPosition - e.ballPosition);
            Quaternion localRotation = Quaternion.Inverse(ballQ) * objectQ;
            if (!Finite(localPosition.x) || !Finite(localPosition.y) || !Finite(localPosition.z) ||
                !Finite(localRotation.x) || !Finite(localRotation.y) ||
                !Finite(localRotation.z) || !Finite(localRotation.w)) return -1;
            for (int i = 0; i < count; i++)
            {
                if (!SameIdentity(records[i], e)) continue;
                AttachmentRecordDraft old = records[i];
                if (old.revoked) return -1;
                if (old.localPosition != localPosition || old.localRotation != localRotation ||
                    old.objectScale != e.objectScale) return -1;
                return i; // identical confirmation replay
            }
            if (count >= Capacity) return -1;
            AttachmentRecordDraft candidate = new AttachmentRecordDraft();
            candidate.contextId = e.contextId; candidate.ballId = e.ballId;
            candidate.instanceId = e.instanceId; candidate.generation = e.generation;
            candidate.attachSequence = e.attachSequence; candidate.eventId = e.eventId;
            candidate.localPosition = localPosition; candidate.localRotation = localRotation;
            candidate.objectScale = e.objectScale; candidate.active = true; candidate.revoked = false;
            records[count] = candidate;
            count++;
            return count - 1;
        }

        public bool Revoke(string contextId, string ballId, string instanceId,
            int generation, long sequence, string eventId)
        {
            for (int i = 0; i < count; i++)
            {
                AttachmentRecordDraft r = records[i];
                if (r.contextId == contextId && r.ballId == ballId && r.instanceId == instanceId &&
                    r.generation == generation && r.attachSequence == sequence && r.eventId == eventId)
                {
                    r.active = false; r.revoked = true; records[i] = r; return true;
                }
            }
            return false;
        }

        public void ClearContext(string contextId)
        {
            int write = 0;
            for (int read = 0; read < count; read++)
            {
                if (records[read].contextId == contextId) continue;
                records[write] = records[read]; write++;
            }
            for (int i = write; i < count; i++) records[i] = new AttachmentRecordDraft();
            count = write;
        }

        // Atomic in-memory snapshot replacement. Inputs represent one already-assembled,
        // same-version complete context snapshot; transport chunk assembly is outside T51.
        public bool ReplaceSnapshot(string contextId, ConfirmedAttachment[] active,
            ConfirmedAttachment[] revoked)
        {
            if (string.IsNullOrEmpty(contextId) || active == null || revoked == null ||
                active.Length + revoked.Length > Capacity) return false;
            AttachmentRecordsDraft candidate = new AttachmentRecordsDraft();
            for (int i = 0; i < active.Length; i++)
            {
                if (active[i].contextId != contextId || candidate.Apply(active[i]) < 0) return false;
            }
            for (int i = 0; i < revoked.Length; i++)
            {
                ConfirmedAttachment e = revoked[i];
                if (e.contextId != contextId || !candidate.Valid(e)) return false;
                for (int j = 0; j < candidate.count; j++)
                    if (candidate.SameIdentity(candidate.records[j], e)) return false;
                if (candidate.count >= Capacity) return false;
                AttachmentRecordDraft tombstone = new AttachmentRecordDraft();
                tombstone.contextId = e.contextId; tombstone.ballId = e.ballId;
                tombstone.instanceId = e.instanceId; tombstone.generation = e.generation;
                tombstone.attachSequence = e.attachSequence; tombstone.eventId = e.eventId;
                tombstone.active = false; tombstone.revoked = true;
                candidate.records[candidate.count] = tombstone; candidate.count++;
            }
            // Preserve records for unrelated contexts while replacing this one.
            for (int i = 0; i < count; i++)
            {
                if (records[i].contextId == contextId) continue;
                if (candidate.count >= Capacity) return false;
                candidate.records[candidate.count] = records[i]; candidate.count++;
            }
            records = candidate.records;
            count = candidate.count;
            return true;
        }
    }
}
