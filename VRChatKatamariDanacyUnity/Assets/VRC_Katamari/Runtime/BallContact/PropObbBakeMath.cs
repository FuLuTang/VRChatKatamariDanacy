using UnityEngine;
using VRCKatamari.Core;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// Turns one prop's two size columns into the solid the v2 solver can query: a TRUE
    /// oriented box on the prop's own axes (CONTACT_MODEL_V2 sections 4A.4, 5.2 and the
    /// "prop obstacles" note in 7.5.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why two columns. <c>propBounds</c> is the prop's WORLD AABB, measured after the prop
    /// was turned, and it is what the absorb gate and the v1 blocking boxes consume.
    /// <c>propLocalBounds</c> is the same prop measured along its OWN axes, before the root
    /// rotation. Building an OBB by rotating the world AABB would apply the rotation a
    /// second time: the radial-proxy double-rotation bug that
    /// <c>RadialProxyShellMath.TryResolvePropExtents</c> and
    /// <c>Tests/EditMode/BallLayers/RadialProxyLocalBoundsTests</c> pin. So the local column
    /// is rotated exactly once (by the prop's world rotation) and the world column is used
    /// as an axis-aligned box only when the local column is unusable - which is bit-for-bit
    /// the box the v1 path builds from the same numbers.
    /// </para>
    /// <para>
    /// The fallback is not a silent one: <see cref="TryBakeProp"/> returns which column it
    /// used (<see cref="SourceLocalAxes"/> / <see cref="SourceWorldAabb"/>) or
    /// <see cref="SourceNone"/>, so a host can count the rows that fell back and report them
    /// once. Nothing here reads scene state, allocates, or has static state; every out value
    /// is zeroed first so a caller that ignores the result cannot read half a box.
    /// </para>
    /// <para>
    /// UdonSharp limits: static method, scalars/Vector3/Quaternion only, no struct, no
    /// overload, no property, four <c>out</c> values plus an int return (a sixth out would
    /// be the design's preferred shape but is not needed: the axes are one rotation).
    /// </para>
    /// </remarks>
    public static class PropObbBakeMath
    {
        /// <summary>
        /// How many prop solids one ball's v2 table holds: the design's 6 blocking + 6
        /// support slots, in one table, because a prop is exactly one of the two per scan
        /// (CONTACT_MODEL_V2 sections 2.1 and 5.2).
        /// </summary>
        public const int PropObbCapacity = 12;

        /// <summary>No box: the row could not be baked from either column.</summary>
        public const int SourceNone = 0;

        /// <summary>True OBB on the prop's own axes (the local-bounds column).</summary>
        public const int SourceLocalAxes = 1;

        /// <summary>Axis-aligned world box (the world-AABB column, the v1 shape).</summary>
        public const int SourceWorldAabb = 2;

        /// <summary>
        /// |q|^2 must be within this of 1 before the rotation is used to build axes. A
        /// transform's rotation is unit to about 1e-7; anything further off is data, and a
        /// non-unit rotation would produce axes the solver refuses
        /// (<c>ObbContactMath.IsValidObb</c> wants 1e-5), so the world AABB is used instead.
        /// </summary>
        private const float UnitRotationTolerance = 1e-6f;

        /// <summary>
        /// Bakes one prop row into a solid, preferring the prop's own axes.
        /// </summary>
        /// <param name="position">The prop's current world position (the box centre).</param>
        /// <param name="rotation">The prop's current world rotation (root rotation included).</param>
        /// <param name="hasLocalRow">False when the scene has no <c>propLocalBounds</c> row.</param>
        /// <param name="localRow">The prop's full edge lengths along its own axes, metres.</param>
        /// <param name="worldRow">The prop's full edge lengths as a world AABB, metres.</param>
        /// <returns>
        /// <see cref="SourceLocalAxes"/> for a true OBB, <see cref="SourceWorldAabb"/> for
        /// the axis-aligned fallback, <see cref="SourceNone"/> when neither column can be
        /// used (non-finite or negative extents, non-finite position).
        /// </returns>
        /// <remarks>
        /// A zero extent is legal on both columns: it is the point/plane box the v1 path
        /// also accepts (<c>BlockingPropMath.TryBoxFromCentreAndExtents</c>). Only a
        /// negative or non-finite number refuses a column.
        /// </remarks>
        public static int TryBakeProp(Vector3 position, Quaternion rotation, bool hasLocalRow,
            Vector3 localRow, Vector3 worldRow, out Vector3 axisX, out Vector3 axisY,
            out Vector3 axisZ, out Vector3 halfExtents)
        {
            axisX = Vector3.zero;
            axisY = Vector3.zero;
            axisZ = Vector3.zero;
            halfExtents = Vector3.zero;
            if (!KatamariMath.IsFiniteVector(position)) return SourceNone;

            if (hasLocalRow && IsUsableExtents(localRow) && IsUnitRotation(rotation))
            {
                Vector3 unitX = rotation * new Vector3(1f, 0f, 0f);
                Vector3 unitY = rotation * new Vector3(0f, 1f, 0f);
                Vector3 unitZ = rotation * new Vector3(0f, 0f, 1f);
                Vector3 halves = Halves(localRow);
                if (KatamariMath.IsFiniteVector(unitX) && KatamariMath.IsFiniteVector(unitY) &&
                    KatamariMath.IsFiniteVector(unitZ) && KatamariMath.IsFiniteVector(halves))
                {
                    axisX = unitX;
                    axisY = unitY;
                    axisZ = unitZ;
                    halfExtents = halves;
                    return SourceLocalAxes;
                }
            }

            if (IsUsableExtents(worldRow))
            {
                Vector3 halves = Halves(worldRow);
                if (KatamariMath.IsFiniteVector(halves))
                {
                    axisX = new Vector3(1f, 0f, 0f);
                    axisY = new Vector3(0f, 1f, 0f);
                    axisZ = new Vector3(0f, 0f, 1f);
                    halfExtents = halves;
                    return SourceWorldAabb;
                }
            }

            return SourceNone;
        }

        /// <summary>True for full edge lengths a box can have: finite and non-negative.</summary>
        public static bool IsUsableExtents(Vector3 fullExtents)
        {
            if (!KatamariMath.IsFiniteVector(fullExtents)) return false;
            if (fullExtents.x < 0f || fullExtents.y < 0f || fullExtents.z < 0f) return false;
            return true;
        }

        /// <summary>True when a quaternion is finite and of unit length to float noise.</summary>
        public static bool IsUnitRotation(Quaternion rotation)
        {
            if (!KatamariMath.IsFiniteQuaternion(rotation)) return false;
            float normSquared = rotation.x * rotation.x + rotation.y * rotation.y +
                rotation.z * rotation.z + rotation.w * rotation.w;
            if (!KatamariMath.IsFiniteFloat(normSquared)) return false;
            return Mathf.Abs(normSquared - 1f) <= UnitRotationTolerance;
        }

        private static Vector3 Halves(Vector3 fullExtents)
        {
            return new Vector3(fullExtents.x * 0.5f, fullExtents.y * 0.5f, fullExtents.z * 0.5f);
        }
    }
}
