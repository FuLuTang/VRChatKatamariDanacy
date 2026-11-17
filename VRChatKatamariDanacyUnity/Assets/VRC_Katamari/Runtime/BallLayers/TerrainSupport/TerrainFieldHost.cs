using UdonSharp;
using UnityEngine;
using VRCKatamari.BallContact;
using VRCKatamari.BallMotor;

namespace VRCKatamari.BallLayers
{
    /// <summary>
    /// The scene-side owner of one baked terrain mesh. The editor importer fills the
    /// serialized arrays; <see cref="Start"/> copies them into the kinematic motor once via
    /// <see cref="KatamariBallMotor.SetTerrainMesh"/> and toggles the motor's terrain flag.
    /// </summary>
    /// <remarks>
    /// <para>
    /// UdonSharp has no disk access and no dynamic large-array allocation in the normal
    /// path, so the arrays live serialized on this component (writer-allocates rule: the
    /// editor importer creates them, the runtime only reads). The motor's own
    /// <c>SetTerrainMesh</c> copies them into its fixed 8192-triangle/16384-vertex buffers,
    /// validates capacities and computes the chunk AABBs.
    /// </para>
    /// <para>
    /// One terrain per level. The component does not import, edit or save; it only hands
    /// data over. The reference level uses the same component inside its git-ignored
    /// <c>Assets/_RefUnlicensed/</c> scene; no terrain asset ever enters git.
    /// </para>
    /// </remarks>
    public class TerrainFieldHost : UdonSharpBehaviour
    {
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private bool terrainMeshContactEnabled = true;

        // When true, Start does not hand the terrain to the motor; something else (the hub's
        // level switch) calls RegisterTerrain() when this terrain's level becomes active.
        // False is the default and the value an older scene reads, so those scenes still
        // register in Start exactly as before.
        [SerializeField] private bool deferRegistration = false;

        [Header("Baked terrain (world metres; filled by the editor importer)")]
        [SerializeField] private Vector3[] vertices;
        [SerializeField] private int[] indices;
        [SerializeField] private int[] chunkStarts;
        [SerializeField] private int[] chunkTriangleCounts;
        [SerializeField] private int[] chunkZoneIds;

        [SerializeField] private int vertexCount;
        [SerializeField] private int indexCount;
        [SerializeField] private int triangleCount;
        [SerializeField] private int chunkCount;

        [SerializeField] private string terrainId;
        [SerializeField] private string sourceSha256;

        /// <summary>Returned by <see cref="SampleSupportHeight"/> when nothing is under the probe.</summary>
        public const float NoSupportHeight = -1.0e9f;

        // Chunk AABBs for the height query below, built once on first use from the same
        // serialized arrays the motor was given (the motor's own copy is not readable).
        private Vector3[] queryChunkMin;
        private Vector3[] queryChunkMax;
        private bool queryReady;

        /// <summary>
        /// Height of the highest upward-facing terrain triangle under (x, z) whose plane is at
        /// or below <paramref name="ceilingY"/>; <see cref="NoSupportHeight"/> when there is
        /// none (or the terrain has no data). Read-only and allocation-free after the first
        /// call; it is the stack-fall behaviour's ground query, not a ball-contact query.
        /// </summary>
        /// <remarks>
        /// A full chunk scan (no grid): fine for the handful of props a stack drops, wrong
        /// for per-frame ball sampling, which stays in the motor.
        /// </remarks>
        public float SampleSupportHeight(float x, float z, float ceilingY)
        {
            if (!EnsureQueryBounds()) return NoSupportHeight;
            Vector3 point;
            Vector3 normal;
            int surfaceId;
            if (!TerrainMeshMath.TrySupportProbe(new Vector3(x, ceilingY, z), ceilingY, vertices,
                indices, triangleCount, queryChunkMin, queryChunkMax, chunkStarts,
                chunkTriangleCounts, chunkZoneIds, chunkCount, out point, out normal,
                out surfaceId)) return NoSupportHeight;
            return point.y;
        }

        /// <summary>True when this host carries terrain triangles to sample.</summary>
        public bool HasTerrainData()
        {
            return terrainMeshContactEnabled && triangleCount > 0 && chunkCount > 0 &&
                   vertices != null && indices != null;
        }

        private bool EnsureQueryBounds()
        {
            if (queryReady) return true;
            if (!HasTerrainData() || chunkStarts == null || chunkTriangleCounts == null ||
                chunkZoneIds == null) return false;
            if (chunkStarts.Length < chunkCount || chunkTriangleCounts.Length < chunkCount)
                return false;
            queryChunkMin = new Vector3[chunkCount];
            queryChunkMax = new Vector3[chunkCount];
            for (int c = 0; c < chunkCount; c++)
            {
                Vector3 low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 high = new Vector3(-float.MaxValue, -float.MaxValue, -float.MaxValue);
                int end = chunkStarts[c] + chunkTriangleCounts[c];
                for (int t = chunkStarts[c]; t < end; t++)
                {
                    for (int corner = 0; corner < 3; corner++)
                    {
                        Vector3 v = vertices[indices[t * 3 + corner]];
                        low = Vector3.Min(low, v);
                        high = Vector3.Max(high, v);
                    }
                }
                queryChunkMin[c] = low;
                queryChunkMax[c] = high;
            }
            queryReady = true;
            return true;
        }

        private void Start()
        {
            if (deferRegistration) return;
            RegisterTerrain();
        }

        /// <summary>
        /// Hands this host's terrain to the motor, replacing whatever terrain it holds.
        /// Start calls it unless <c>deferRegistration</c> is set. Returns true when the
        /// motor accepted the mesh (or when terrain contact is disabled and the motor was
        /// told so), false when nothing was registered.
        /// </summary>
        public bool RegisterTerrain()
        {
            if (motor == null)
            {
                Debug.LogWarning("[TerrainFieldHost] no motor linked; terrain '" + terrainId +
                                 "' was not registered");
                return false;
            }

            motor.SetTerrainMeshContactEnabled(terrainMeshContactEnabled);
            if (!terrainMeshContactEnabled)
            {
                motor.ClearTerrainMesh();
                return true;
            }

            if (vertices == null || indices == null || chunkStarts == null ||
                chunkTriangleCounts == null || chunkZoneIds == null)
            {
                Debug.LogWarning("[TerrainFieldHost] terrain '" + terrainId +
                                 "' has no arrays; clearing the motor mesh");
                motor.ClearTerrainMesh();
                return false;
            }

            // One motor holds ONE terrain mesh: SetTerrainMesh replaces the buffers, so a
            // second host on the same motor silently wins and the first level's triangles are
            // gone. That is a wiring mistake, not a supported mode (a multi-cell field must
            // re-register on cell switch), so it is reported once instead of being invisible.
            int alreadyHeld = motor.GetTerrainMeshTriangleCount();
            if (alreadyHeld > 0)
            {
                Debug.LogWarning("[TerrainFieldHost] motor already holds a " + alreadyHeld +
                                 "-triangle terrain; '" + terrainId + "' replaces it (one " +
                                 "terrain per motor - link only one host, or re-register on " +
                                 "a cell switch)");
            }

            if (!motor.SetTerrainMesh(vertices, vertexCount, indices, indexCount,
                chunkStarts, chunkTriangleCounts, chunkZoneIds, chunkCount))
            {
                Debug.LogError("[TerrainFieldHost] motor refused terrain '" + terrainId +
                               "' (triangles=" + triangleCount + ", vertices=" + vertexCount +
                               ", chunks=" + chunkCount + ")");
                return false;
            }

            // The grid is a speed structure: if it was refused, the mesh is still registered
            // and the queries fall back to the full scan. Report it once at setup, with the
            // number that broke the cap, so a silent performance cliff cannot happen.
            int gridRefusal = motor.GetTerrainGridRefusalCode();
            if (gridRefusal != 0)
            {
                Debug.LogWarning("[TerrainFieldHost] terrain '" + terrainId +
                                 "' registered, but its grid is unavailable (code " +
                                 gridRefusal + ", detail " + motor.GetTerrainGridRefusalDetail() +
                                 ": " + motor.GetTerrainGridRefusalReason() +
                                 "); support queries use the full chunk scan");
            }
            return true;
        }
    }
}
