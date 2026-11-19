using UdonSharp;
using UnityEngine;
using VRCKatamari.BallMotor;
using VRCKatamari.Hub;

namespace VRCKatamari.BallContact
{
    /// <summary>
    /// Local, opt-in view of the world geometry registered in the ball motor (setting
    /// "show colliders"). Three line meshes: static boxes, terrain triangles, prop proxies.
    /// Polls the settings revision, so it needs no entry in the settings listeners.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CollisionWireframe : UdonSharpBehaviour
    {
        [SerializeField] private HubSettingsHost settings;
        [SerializeField] private KatamariBallMotor motor;
        [SerializeField] private Renderer staticLines;
        [SerializeField] private Renderer terrainLines;
        [SerializeField] private Renderer propLines;
        [SerializeField] private MeshFilter staticFilter;
        [SerializeField] private MeshFilter terrainFilter;
        [SerializeField] private MeshFilter propFilter;

        private const float IntervalSeconds = 0.2f;

        private Mesh staticMesh;
        private Mesh terrainMesh;
        private Mesh propMesh;
        private Vector3[] staticBuffer;
        private Vector3[] terrainBuffer;
        private Vector3[] propBuffer;
        private int[] indices;
        private bool drawing;
        private int settingsRevision = -1;
        private float nextDrawTime;

        private void Start() { Hide(); }

        private void OnDisable()
        {
            Hide();
            settingsRevision = -1;
        }

        private void Hide()
        {
            if (staticLines != null) staticLines.enabled = false;
            if (terrainLines != null) terrainLines.enabled = false;
            if (propLines != null) propLines.enabled = false;
        }

        private void LateUpdate()
        {
            if (settings == null || motor == null) return;
            int revision = settings.GetRevision();
            if (revision != settingsRevision)
            {
                settingsRevision = revision;
                bool next = settings.IsCollisionWireframeEnabled();
                if (next != drawing)
                {
                    drawing = next;
                    nextDrawTime = 0f;
                    if (!drawing) Hide();
                }
            }
            if (!drawing || Time.time < nextDrawTime) return;
            nextDrawTime = Time.time + IntervalSeconds;
            if (indices == null)
            {
                indices = new int[CollisionWireMath.TerrainVertexCapacity];
                for (int i = 0; i < indices.Length; i++) indices[i] = i;
            }
            if (staticMesh == null && staticFilter != null) staticMesh = staticFilter.mesh;
            if (terrainMesh == null && terrainFilter != null) terrainMesh = terrainFilter.mesh;
            if (propMesh == null && propFilter != null) propMesh = propFilter.mesh;
            if (staticBuffer == null) staticBuffer = new Vector3[CollisionWireMath.StaticVertexCapacity];
            if (terrainBuffer == null) terrainBuffer = new Vector3[CollisionWireMath.TerrainVertexCapacity];
            if (propBuffer == null) propBuffer = new Vector3[CollisionWireMath.PropVertexCapacity];

            Vector3 center = motor.GetCenterWorld();
            float range = CollisionWireMath.WindowRange(motor.GetSemanticDiameterWorldM());
            Draw(staticMesh, staticLines, CollisionWireMath.StaticBoxes, center, range, staticBuffer);
            Draw(terrainMesh, terrainLines, CollisionWireMath.Terrain, center, range, terrainBuffer);
            Draw(propMesh, propLines, CollisionWireMath.Props, center, range, propBuffer);
        }

        private void Draw(Mesh mesh, Renderer lines, int layer, Vector3 center, float range,
            Vector3[] buffer)
        {
            if (mesh == null || lines == null) return;
            int count = motor.CopyCollisionWire(layer, center, range, buffer);
            if (count > 0)
            {
                // Unused endpoints collapse onto the ball, so stale lines never linger.
                for (int i = count; i < buffer.Length; i++) buffer[i] = center;
                mesh.vertices = buffer;
                mesh.SetIndices(indices, 0, count, MeshTopology.Lines, 0, true, 0);
            }
            lines.enabled = count > 0;
        }
    }
}
