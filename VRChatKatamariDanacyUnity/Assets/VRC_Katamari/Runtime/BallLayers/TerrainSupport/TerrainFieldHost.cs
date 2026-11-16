using UdonSharp;
using UnityEngine;
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

        private void Start()
        {
            if (motor == null)
            {
                Debug.LogWarning("[TerrainFieldHost] no motor linked; terrain '" + terrainId +
                                 "' was not registered");
                return;
            }

            motor.SetTerrainMeshContactEnabled(terrainMeshContactEnabled);
            if (!terrainMeshContactEnabled)
            {
                motor.ClearTerrainMesh();
                return;
            }

            if (vertices == null || indices == null || chunkStarts == null ||
                chunkTriangleCounts == null || chunkZoneIds == null)
            {
                Debug.LogWarning("[TerrainFieldHost] terrain '" + terrainId +
                                 "' has no arrays; clearing the motor mesh");
                motor.ClearTerrainMesh();
                return;
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
            }
        }
    }
}
