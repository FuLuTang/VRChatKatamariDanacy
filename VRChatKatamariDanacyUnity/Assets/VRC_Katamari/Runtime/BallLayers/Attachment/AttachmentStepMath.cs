using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallLayers
{
    /// <summary>Contact-frame event storage and attached-prop needle registration over caller-owned columns.</summary>
    public static class AttachmentStepMath
    {
        public const int EventCapacity = 16;
        public const int ProxyItemCapacity = 256;
        public const int ProxyEndpointCapacity = ProxyItemCapacity * RadialProxyShellMath.AabbCornerCount;
        public const int ProxySkipNone = 0;
        public const int ProxySkipInsideCore = 2;
        public const int ProxySkipPoolFull = 4;
        public const int ProxySkipInvalidInput = 5;
        public const int ProxySkipShellRefused = 6;

        /// <summary>
        /// Records a committed candidate's contact pose using pre-growth size and roll.
        /// A full ring evicts its oldest payload; an invalid pose remains an event but cannot be attached.
        /// </summary>
        public static void RecordAbsorbedEvent(string itemId, Vector3 position, Quaternion rotation,
            Vector3 previousBallPosition, Quaternion previousBallRotation, Vector3 contactCenter,
            Vector3 supportNormal, float coreRadius, float collisionRadius, float contactRadius,
            bool discontinuous, float maxRollDisplacementM, int authorityFrame,
            string[] eventItemIds, Vector3[] eventPositions, Vector3[] eventLocalPositions,
            Quaternion[] eventLocalRotations, Vector3[] eventBallCenters, float[] eventCollisionRadii,
            float[] eventContactRadii, int[] eventSteps, bool[] eventPoseValid,
            ref int eventCount, ref int ringCount)
        {
            if (eventItemIds == null) return;
            int slot = eventCount % EventCapacity;
            eventItemIds[slot] = itemId;
            eventPositions[slot] = position;
            Vector3 localPosition;
            Quaternion localRotation;
            eventPoseValid[slot] = AttachmentMath.TryCaptureContactPose(previousBallPosition,
                previousBallRotation, contactCenter, supportNormal, coreRadius, discontinuous,
                maxRollDisplacementM, position, rotation, Vector3.one, out localPosition,
                out localRotation);
            eventLocalPositions[slot] = localPosition;
            eventLocalRotations[slot] = localRotation;
            eventBallCenters[slot] = contactCenter;
            eventCollisionRadii[slot] = collisionRadius;
            eventContactRadii[slot] = contactRadius;
            eventSteps[slot] = authorityFrame;
            eventCount = eventCount + 1;
            if (ringCount < EventCapacity) ringCount = ringCount + 1;
        }

        /// <summary>Maps an absolute event index to a retained ring slot; reset occupancy never rewinds eventCount.</summary>
        public static int AbsorbedEventSlot(int absoluteIndex, string[] eventItemIds,
            int eventCount, int ringCount)
        {
            if (eventItemIds == null) return -1;
            if (absoluteIndex < 0) return -1;
            if (absoluteIndex >= eventCount) return -1;
            int firstRetained = eventCount - ringCount;
            if (absoluteIndex < firstRetained) return -1;
            return absoluteIndex % EventCapacity;
        }

        /// <summary>Reads the frozen ball-local pose and contact dimensions, never the consumer's current ball pose.</summary>
        public static bool TryReadAbsorbedEventAttachment(int absoluteIndex, int authorityFrame,
            string[] eventItemIds, Vector3[] eventLocalPositions, Quaternion[] eventLocalRotations,
            Vector3[] eventBallCenters, float[] eventCollisionRadii, float[] eventContactRadii,
            int[] eventSteps, bool[] eventPoseValid, int eventCount, int ringCount,
            out Vector3 localPosition, out Quaternion localRotation, out Vector3 contactCenter,
            out float collisionRadius, out float contactRadius, out int delaySteps)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            contactCenter = Vector3.zero;
            collisionRadius = 0f;
            contactRadius = 0f;
            delaySteps = 0;
            int slot = AbsorbedEventSlot(absoluteIndex, eventItemIds, eventCount, ringCount);
            if (slot < 0) return false;
            if (!eventPoseValid[slot]) return false;
            localPosition = eventLocalPositions[slot];
            localRotation = eventLocalRotations[slot];
            contactCenter = eventBallCenters[slot];
            collisionRadius = eventCollisionRadii[slot];
            contactRadius = eventContactRadii[slot];
            delaySteps = authorityFrame - eventSteps[slot];
            return true;
        }

        /// <summary>
        /// Registers a prop's eight corners with the needle shell. Existing identities succeed without mutation,
        /// including at capacity; no-protrusion and capacity refusals leave the shell unchanged.
        /// </summary>
        public static bool TryAttachRadialProxy(string itemInstanceId, Vector3 bounds,
            Vector3 ballLocalCenter, Quaternion ballLocalRotation, float coreRadiusWorldM,
            float minProxyProtrusionM, Vector3[] directions, float[] supports,
            Vector3[] endpointPool, ref int endpointCount, int[] itemStarts, int[] itemLengths,
            string[] itemKeys, bool[] itemIsCandidate, ref int itemCount, string[] candidateKeys,
            ref int candidateCount, int[] affectedIndices, float[] solveScratch,
            Vector3[] corners, out int skipReason)
        {
            skipReason = ProxySkipNone;
            if (endpointPool == null)
            {
                skipReason = ProxySkipInvalidInput;
                return false;
            }
            if (itemInstanceId == null || itemInstanceId.Length == 0)
            {
                skipReason = ProxySkipInvalidInput;
                return false;
            }
            for (int i = 0; i < itemCount; i++)
            {
                if (string.CompareOrdinal(itemKeys[i], itemInstanceId) == 0) return true;
            }
            if (!KatamariMath.IsFiniteVector(bounds) || bounds.x < 0f || bounds.y < 0f || bounds.z < 0f)
            {
                skipReason = ProxySkipInvalidInput;
                return false;
            }
            if (!KatamariMath.IsFiniteVector(ballLocalCenter) ||
                !RadialProxyMath.IsUnitRotation(ballLocalRotation))
            {
                skipReason = ProxySkipInvalidInput;
                return false;
            }
            if (!KatamariMath.IsFiniteFloat(coreRadiusWorldM) || coreRadiusWorldM < 0f)
            {
                skipReason = ProxySkipInvalidInput;
                return false;
            }
            float farthestDistance;
            if (!RadialProxyShellMath.TryBuildAabbCorners(ballLocalCenter, ballLocalRotation,
                bounds, corners, out farthestDistance))
            {
                skipReason = ProxySkipInvalidInput;
                return false;
            }
            float minProtrusion = minProxyProtrusionM;
            if (!KatamariMath.IsFiniteFloat(minProtrusion) || minProtrusion < 0f) minProtrusion = 0f;
            if (farthestDistance <= coreRadiusWorldM + minProtrusion)
            {
                skipReason = ProxySkipInsideCore;
                return false;
            }
            if (itemCount >= ProxyItemCapacity)
            {
                skipReason = ProxySkipPoolFull;
                return false;
            }
            if (endpointCount > ProxyEndpointCapacity - RadialProxyShellMath.AabbCornerCount)
            {
                skipReason = ProxySkipPoolFull;
                return false;
            }
            if (!RadialProxyShellMath.TryAttachItem(directions, supports, endpointPool,
                ref endpointCount, itemStarts, itemLengths, itemKeys, itemIsCandidate,
                ref itemCount, candidateKeys, ref candidateCount, affectedIndices, solveScratch,
                itemInstanceId, corners, RadialProxyShellMath.AabbCornerCount, false))
            {
                skipReason = ProxySkipShellRefused;
                return false;
            }
            return true;
        }
    }
}
