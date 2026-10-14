// RuntimeDraft only: not compiled in Unity/UdonSharp; see module gate notes.
// All identity/digest fields are opaque upstream values. No local sequence,
// hash, network event, ledger, collider, or gameplay outcome is created here.
using UnityEngine;

namespace VrcKatamari.RuntimeDraft
{
    public struct SyntheticConfirmedProxySummary
    {
        public string ContextId;
        public string BallId;
        public string InstanceId;
        public int Generation;
        public string StableAttachmentId; // opaque identity supplied by the producer
        public string EventId;
        public int Revision;
        public string SummaryDigest;       // opaque conflict token; not authentication
        public string VariantId;
        public int VariantSeed;
        public Vector3 Position;
        public Quaternion Rotation;
        public bool Visible;
        public bool Buried;
        public bool Removed;
        public int PresentationPriority;
    }

    public sealed class RemoteProxyRuntimeDraft
    {
        public const int MaxStoredSummaries = 64; // scratch bound only; not a product budget
        public const int ApplyAccepted = 1;
        public const int ApplyUpdated = 2;
        public const int ApplyDuplicate = 3;
        public const int ApplyStale = 4;
        public const int ApplyConflict = -1;
        public const int ApplyInvalid = -2;
        public const int ApplyCapacity = -3;
        public const int ApplyRemoved = -4;
        public const int ApplyClosedContext = -5;

        private struct Slot
        {
            public bool Used;
            public SyntheticConfirmedProxySummary Summary;
        }

        private struct GenerationWater
        {
            public bool Used;
            public string ContextId;
            public string BallId;
            public string InstanceId;
            public int Generation;
        }

        private Slot[] _slots = new Slot[MaxStoredSummaries];
        private GenerationWater[] _generationWater = new GenerationWater[MaxStoredSummaries];
        private string[] _closedContexts = new string[MaxStoredSummaries];
        private int _rendererBudget;
        private int _closedCount;
        public bool ConfigurationValid;

        public RemoteProxyRuntimeDraft(int rendererBudget)
        {
            ConfigurationValid = rendererBudget >= 0;
            _rendererBudget = rendererBudget < 0 ? 0 : rendererBudget;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool NonEmpty(string value)
        {
            return value != null && value.Length > 0;
        }

        private static bool NormalizeRotation(Quaternion value, out Quaternion result)
        {
            result = Quaternion.identity;
            if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w)) return false;
            float scale = Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y),
                         Mathf.Max(Mathf.Abs(value.z), Mathf.Abs(value.w))));
            if (!Finite(scale) || scale <= 0f) return false;
            float x = value.x / scale;
            float y = value.y / scale;
            float z = value.z / scale;
            float w = value.w / scale;
            float norm = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (!Finite(norm) || norm <= 0f || scale <= 1e-12f / norm) return false;
            result = new Quaternion(x / norm, y / norm, z / norm, w / norm);
            return Finite(result.x) && Finite(result.y) && Finite(result.z) && Finite(result.w);
        }

        private static bool Validate(ref SyntheticConfirmedProxySummary value)
        {
            if (!NonEmpty(value.ContextId) || !NonEmpty(value.BallId) ||
                !NonEmpty(value.InstanceId) || !NonEmpty(value.StableAttachmentId) ||
                !NonEmpty(value.EventId) || !NonEmpty(value.SummaryDigest) ||
                !NonEmpty(value.VariantId) || value.Generation < 0 || value.Revision < 0 ||
                value.VariantSeed < 0 || value.PresentationPriority < 0 ||
                !Finite(value.Position.x) || !Finite(value.Position.y) || !Finite(value.Position.z)) return false;
            Quaternion normalized;
            if (!NormalizeRotation(value.Rotation, out normalized)) return false;
            value.Rotation = normalized;
            return true;
        }

        private static bool SameBase(SyntheticConfirmedProxySummary value,
            string context, string ball, string instance)
        {
            return value.ContextId == context && value.BallId == ball && value.InstanceId == instance;
        }

        private static bool SameIdentity(SyntheticConfirmedProxySummary a,
            SyntheticConfirmedProxySummary b)
        {
            return SameBase(a, b.ContextId, b.BallId, b.InstanceId) &&
                a.Generation == b.Generation && a.StableAttachmentId == b.StableAttachmentId;
        }

        private static bool SamePayload(SyntheticConfirmedProxySummary a,
            SyntheticConfirmedProxySummary b)
        {
            return a.ContextId == b.ContextId && a.BallId == b.BallId &&
                a.InstanceId == b.InstanceId && a.Generation == b.Generation &&
                a.StableAttachmentId == b.StableAttachmentId && a.EventId == b.EventId &&
                a.Revision == b.Revision && a.SummaryDigest == b.SummaryDigest &&
                a.VariantId == b.VariantId && a.VariantSeed == b.VariantSeed &&
                a.Position.x == b.Position.x && a.Position.y == b.Position.y && a.Position.z == b.Position.z &&
                a.Rotation.x == b.Rotation.x && a.Rotation.y == b.Rotation.y &&
                a.Rotation.z == b.Rotation.z && a.Rotation.w == b.Rotation.w &&
                a.Visible == b.Visible && a.Buried == b.Buried && a.Removed == b.Removed &&
                a.PresentationPriority == b.PresentationPriority;
        }

        private bool IsClosed(string contextId)
        {
            for (int i = 0; i < _closedCount; i++)
                if (_closedContexts[i] == contextId) return true;
            return false;
        }

        // Applies one synthetic record. Status codes distinguish stale, duplicate,
        // conflict, invalid and capacity cases; failures never partially mutate.
        public bool TryApply(SyntheticConfirmedProxySummary input, out int status)
        {
            status = ApplyInvalid;
            if (!ConfigurationValid) return false;
            SyntheticConfirmedProxySummary candidate = input;
            if (!Validate(ref candidate)) return false;
            if (IsClosed(candidate.ContextId))
            {
                status = ApplyClosedContext;
                return false;
            }

            int waterIndex = -1;
            int highGeneration = -1;
            for (int i = 0; i < MaxStoredSummaries; i++)
            {
                if (_generationWater[i].Used && _generationWater[i].ContextId == candidate.ContextId &&
                    _generationWater[i].BallId == candidate.BallId &&
                    _generationWater[i].InstanceId == candidate.InstanceId)
                {
                    waterIndex = i;
                    highGeneration = _generationWater[i].Generation;
                    break;
                }
            }
            if (waterIndex >= 0 && candidate.Generation < highGeneration)
            {
                status = ApplyStale;
                return false;
            }

            int existingIndex = -1;
            for (int i = 0; i < MaxStoredSummaries; i++)
                if (_slots[i].Used && SameIdentity(_slots[i].Summary, candidate))
                {
                    existingIndex = i;
                    break;
                }

            if (existingIndex >= 0)
            {
                SyntheticConfirmedProxySummary old = _slots[existingIndex].Summary;
                if (old.EventId != candidate.EventId)
                {
                    status = ApplyConflict;
                    return false;
                }
                if (candidate.Revision < old.Revision)
                {
                    status = ApplyStale;
                    return false;
                }
                if (candidate.Revision == old.Revision)
                {
                    if (SamePayload(old, candidate))
                    {
                        status = ApplyDuplicate;
                        return true;
                    }
                    status = ApplyConflict;
                    return false;
                }
                if (old.Removed)
                {
                    status = ApplyRemoved;
                    return false;
                }
                _slots[existingIndex].Summary = candidate;
                status = ApplyUpdated;
                return true;
            }

            int reusableIndex = -1;
            int freeIndex = -1;
            for (int i = 0; i < MaxStoredSummaries; i++)
            {
                if (!_slots[i].Used && freeIndex < 0) freeIndex = i;
                if (_slots[i].Used && waterIndex >= 0 && candidate.Generation > highGeneration &&
                    SameBase(_slots[i].Summary, candidate.ContextId, candidate.BallId, candidate.InstanceId) &&
                    _slots[i].Summary.Generation < candidate.Generation && reusableIndex < 0)
                    reusableIndex = i;
            }
            int targetIndex = reusableIndex >= 0 ? reusableIndex : freeIndex;
            if (targetIndex < 0)
            {
                status = ApplyCapacity;
                return false;
            }

            int freeWaterIndex = -1;
            if (waterIndex < 0)
            {
                for (int i = 0; i < MaxStoredSummaries; i++)
                    if (!_generationWater[i].Used) { freeWaterIndex = i; break; }
                if (freeWaterIndex < 0)
                {
                    status = ApplyCapacity;
                    return false;
                }
            }

            // Commit generation rollover only after a slot and high-water row exist.
            if (waterIndex >= 0 && candidate.Generation > highGeneration)
            {
                for (int i = 0; i < MaxStoredSummaries; i++)
                    if (_slots[i].Used &&
                        SameBase(_slots[i].Summary, candidate.ContextId, candidate.BallId, candidate.InstanceId) &&
                        _slots[i].Summary.Generation < candidate.Generation)
                        _slots[i].Used = false;
                targetIndex = reusableIndex >= 0 ? reusableIndex : freeIndex;
                _generationWater[waterIndex].Generation = candidate.Generation;
            }
            else if (waterIndex < 0)
            {
                _generationWater[freeWaterIndex].Used = true;
                _generationWater[freeWaterIndex].ContextId = candidate.ContextId;
                _generationWater[freeWaterIndex].BallId = candidate.BallId;
                _generationWater[freeWaterIndex].InstanceId = candidate.InstanceId;
                _generationWater[freeWaterIndex].Generation = candidate.Generation;
            }
            _slots[targetIndex].Used = true;
            _slots[targetIndex].Summary = candidate;
            status = ApplyAccepted;
            return true;
        }

        public bool TrySetRendererBudget(int budget)
        {
            if (budget < 0) return false;
            _rendererBudget = budget;
            ConfigurationValid = true;
            return true;
        }

        public int RendererCount
        {
            get
            {
                int eligible = 0;
                for (int i = 0; i < MaxStoredSummaries; i++)
                    if (_slots[i].Used && !_slots[i].Summary.Removed &&
                        _slots[i].Summary.Visible && !_slots[i].Summary.Buried) eligible++;
                return eligible < _rendererBudget ? eligible : _rendererBudget;
            }
        }

        private static int CompareText(string a, string b)
        {
            int count = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < count; i++)
            {
                if (a[i] < b[i]) return -1;
                if (a[i] > b[i]) return 1;
            }
            if (a.Length < b.Length) return -1;
            if (a.Length > b.Length) return 1;
            return 0;
        }

        private static bool Before(SyntheticConfirmedProxySummary a,
            SyntheticConfirmedProxySummary b)
        {
            if (a.PresentationPriority != b.PresentationPriority)
                return a.PresentationPriority > b.PresentationPriority;
            int c = CompareText(a.ContextId, b.ContextId);
            if (c != 0) return c < 0;
            c = CompareText(a.BallId, b.BallId);
            if (c != 0) return c < 0;
            c = CompareText(a.InstanceId, b.InstanceId);
            if (c != 0) return c < 0;
            if (a.Generation != b.Generation) return a.Generation < b.Generation;
            c = CompareText(a.StableAttachmentId, b.StableAttachmentId);
            if (c != 0) return c < 0;
            return CompareText(a.EventId, b.EventId) < 0;
        }

        // Query the stable summary at a rendered rank; the caller owns any actual Renderer.
        public bool TryGetRenderedAt(int rank, out SyntheticConfirmedProxySummary result)
        {
            result = new SyntheticConfirmedProxySummary();
            if (rank < 0 || rank >= RendererCount) return false;
            for (int i = 0; i < MaxStoredSummaries; i++)
            {
                if (!_slots[i].Used || _slots[i].Summary.Removed ||
                    !_slots[i].Summary.Visible || _slots[i].Summary.Buried) continue;
                int beforeCount = 0;
                for (int j = 0; j < MaxStoredSummaries; j++)
                    if (i != j && _slots[j].Used && !_slots[j].Summary.Removed &&
                        _slots[j].Summary.Visible && !_slots[j].Summary.Buried &&
                        Before(_slots[j].Summary, _slots[i].Summary)) beforeCount++;
                if (beforeCount == rank)
                {
                    result = _slots[i].Summary;
                    return true;
                }
            }
            return false;
        }

        // Close-and-clear is intentionally permanent for this context identifier.
        // A later round must have a new context ID.
        public bool TryClearContext(string contextId, out int removedCount)
        {
            removedCount = 0;
            if (!NonEmpty(contextId) || IsClosed(contextId) || _closedCount >= MaxStoredSummaries) return false;
            for (int i = 0; i < MaxStoredSummaries; i++)
            {
                if (_slots[i].Used && _slots[i].Summary.ContextId == contextId)
                {
                    _slots[i].Used = false;
                    removedCount++;
                }
                if (_generationWater[i].Used && _generationWater[i].ContextId == contextId)
                    _generationWater[i].Used = false;
            }
            _closedContexts[_closedCount] = contextId;
            _closedCount++;
            return true;
        }
    }
}
