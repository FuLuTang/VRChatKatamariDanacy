using System;
using System.Collections.Generic;
using System.Globalization;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRCKatamari.BallLayers;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// Creates and wires <see cref="PropFallHost"/> (<c>LevelContent/&lt;id&gt;/PropFallHost</c>)
    /// for a level whose props stand on each other.
    /// </summary>
    /// <remarks>
    /// Wiring: <c>PropFallHost.pickupField</c> to the level's <see cref="PickupFieldHost"/>,
    /// <c>PropFallHost.terrain</c> to the level's <see cref="TerrainFieldHost"/> (the
    /// <c>TerrainRoot</c> the terrain import created), and <c>PickupFieldHost.fallHost</c> back to
    /// the new host. A level with no parent edge gets no host, like a level with no motion gets no
    /// MotionHost rows; an existing host is left as it is. Idempotent: references already set are
    /// never rewritten (a hand-set one is kept), the component is created once, and a second import
    /// reports no writes. Runs after the terrain import so <c>TerrainRoot</c> exists on the first
    /// import.
    /// </remarks>
    public static partial class PlacementImporter
    {
        public const string PropFallHostObjectName = "PropFallHost";

        /// <summary>
        /// Props whose <c>parent</c> names another prop of the same document: the edges the
        /// pickup sync turns into <c>propParentIndex</c>.
        /// </summary>
        public static int CountParentEdges(PlacementV2Document doc)
        {
            if (doc == null || doc.Props == null) return 0;

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < doc.Props.Length; i++)
            {
                PlacementV2Prop prop = doc.Props[i];
                if (prop != null && !string.IsNullOrEmpty(prop.ItemId)) ids.Add(prop.ItemId);
            }

            int edges = 0;
            for (int i = 0; i < doc.Props.Length; i++)
            {
                PlacementV2Prop prop = doc.Props[i];
                if (prop == null || !prop.HasParent || string.IsNullOrEmpty(prop.Parent)) continue;
                if (string.Equals(prop.Parent, prop.ItemId, StringComparison.Ordinal)) continue;
                if (ids.Contains(prop.Parent)) edges++;
            }

            return edges;
        }

        private static void ApplyPropFallHost(PlacementV2Document doc, Transform levelRoot,
            PlacementImportReport report)
        {
            int edges = CountParentEdges(doc);
            PropFallHost host = levelRoot.GetComponentInChildren<PropFallHost>(true);
            if (edges == 0)
            {
                report.Note(V2Tag + "no prop has a parent: no PropFallHost" +
                            (host != null ? " (the existing host stays as it is)" : ""));
                return;
            }

            PickupFieldHost pickup = FirstUnder<PickupFieldHost>(levelRoot);
            if (pickup == null)
            {
                report.Error(V2Tag + "no PickupFieldHost under " + HierarchyPath(levelRoot) +
                             "; the PropFallHost was not wired");
                return;
            }

            int writes = 0;
            if (host == null)
            {
                var go = new GameObject(PropFallHostObjectName);
                Undo.RegisterCreatedObjectUndo(go, UndoLabel);
                Undo.SetTransformParent(go.transform, levelRoot, UndoLabel);
                try
                {
                    host = UdonSharpUndo.AddComponent<PropFallHost>(go);
                    writes++;
                }
                catch (Exception exception)
                {
                    report.Error(V2Tag + "could not add PropFallHost to '" + go.name + "': " +
                                 exception.Message + " (does PropFallHost.asset, its UdonSharp program " +
                                 "asset, exist next to PropFallHost.cs? Unity generates it on import)");
                    return;
                }
            }

            TerrainFieldHost terrain = FirstUnder<TerrainFieldHost>(levelRoot);
            if (terrain == null)
            {
                report.Note(V2Tag + "no TerrainFieldHost under " + HierarchyPath(levelRoot) +
                            ": PropFallHost.terrain stays empty (props fall to floorY without prop support)");
            }

            var fall = new SerializedObject(host);
            int hostWrites = WriteObject(fall, "pickupField", pickup, true, report);
            hostWrites += WriteObject(fall, "terrain", terrain, true, report);
            if (hostWrites > 0)
            {
                fall.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(host);
            }

            var field = new SerializedObject(pickup);
            int fieldWrites = WriteObject(field, "fallHost", host, true, report);
            if (fieldWrites > 0)
            {
                field.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(pickup);
            }

            writes += hostWrites + fieldWrites;
            report.V2SceneWiringWrites += writes;

            if (writes > 0)
            {
                try
                {
                    UdonSharpProgramAsset.CompileAllCsPrograms(false, false);
                    UdonSharpEditorUtility.CopyProxyToUdon(host);
                    UdonSharpEditorUtility.CopyProxyToUdon(pickup);
                }
                catch (Exception exception)
                {
                    report.Error(V2Tag + "CopyProxyToUdon threw for PropFallHost/PickupFieldHost: " +
                                 exception.Message + ". The references are on the proxies but not on " +
                                 "the backing UdonBehaviours; re-run the import once the compile has finished");
                }
            }

            report.Note(V2Tag + "prop fall host: parent edges=" + edges.ToString(CultureInfo.InvariantCulture) +
                        ", terrain=" + (terrain != null ? "wired" : "none") + ", writes=" +
                        writes.ToString(CultureInfo.InvariantCulture));
        }

        // The first component of the type under the level root in hierarchy order.
        private static T FirstUnder<T>(Transform levelRoot) where T : Component
        {
            T[] all = levelRoot.GetComponentsInChildren<T>(true);
            if (all == null || all.Length == 0) return null;
            Array.Sort(all, (left, right) => string.CompareOrdinal(HierarchyPath(left.transform),
                HierarchyPath(right.transform)));
            return all[0];
        }
    }
}
