using System.Collections.Generic;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRCKatamari.BallMotor;
using VRCKatamari.Story;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// Links a level's <see cref="LevelArenaHost"/> to the scene's ball motor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arena host registers itself only from its own <c>Update</c>, and the first thing
    /// that does is <c>if (motor == null) return;</c> with no log (<c>LevelArenaHost.Update</c>).
    /// A host added by hand and filled by <see cref="BindArenaHost"/> therefore stayed silent
    /// forever: no <c>[arena]</c> line, no static boxes, no ground plane, no kind whitelist.
    /// The importer fills the arrays; this is the part that makes them reach the motor.
    /// </para>
    /// <para>
    /// The second hazard is another enabled host on the SAME motor. Two hosts both call
    /// <c>SetGroundPlane</c> and <c>SetAllowedKinds</c>, and the last one to run wins, so the
    /// template's empty <c>LevelArena</c> (picnic kinds, ground 0) could overwrite the level's
    /// own whitelist and floor. Several hosts on one motor is the NORMAL shape of the hub and
    /// the physics test field (a switch host suspends and re-registers them), so a shared motor
    /// is only reported, and a host is switched off only when it is a pure placeholder:
    /// a scene-root host that registers no geometry at all, in an unlicensed reference scene
    /// (a disposable copy of the template, see <c>LevelPaths.IsReferenceScene</c>).
    /// </para>
    /// </remarks>
    public static partial class PlacementImporter
    {
        private const string ArenaMotorField = "motor";
        private const string ArenaMotorTag = "[arena_motor] ";

        /// <summary>
        /// Writes the scene's motor into <c>LevelArenaHost.motor</c> when it is empty. Returns the
        /// motor the host ends up linked to, or null when there is none.
        /// </summary>
        /// <param name="motorProperty">The host's serialized <c>motor</c> property (may be null).</param>
        /// <param name="motors">Every <see cref="KatamariBallMotor"/> in the scene.</param>
        /// <remarks>
        /// A link that is already set is never rewritten (a hand-set one is kept), so a second
        /// import writes nothing. With no motor or more than one the host is left alone and one
        /// warning says why; guessing which of several motors a level belongs to would silently
        /// register its geometry with the wrong ball.
        /// </remarks>
        public static KatamariBallMotor WireArenaMotor(SerializedProperty motorProperty,
            KatamariBallMotor[] motors, PlacementImportReport report)
        {
            int count = motors == null ? 0 : motors.Length;
            if (motorProperty == null)
            {
                ArenaMotorWarning(report, "LevelArenaHost has no 'motor' field; its link to the " +
                                          "ball motor was not written");
                return null;
            }

            KatamariBallMotor existing = motorProperty.objectReferenceValue as KatamariBallMotor;
            if (existing != null) return existing;

            if (count == 1 && motors[0] != null)
            {
                motorProperty.objectReferenceValue = motors[0];
                report.V2SceneWiringWrites++;
                report.Note(ArenaMotorTag + "LevelArenaHost.motor was empty; linked it to " +
                            HierarchyPath(motors[0].transform));
                return motors[0];
            }

            ArenaMotorWarning(report, count == 0
                ? "LevelArenaHost.motor is empty and the scene has no KatamariBallMotor, so the " +
                  "arena will never register (its Update returns silently). Add the motor and " +
                  "import again"
                : "LevelArenaHost.motor is empty and the scene has " + count + " KatamariBallMotors; " +
                  "the importer will not guess which one this level belongs to. Link " +
                  "LevelArenaHost.motor by hand and import again");
            return null;
        }

        /// <summary>
        /// Looks at the other enabled arena hosts that share <paramref name="motor"/> and switches
        /// off the pure placeholders. Returns how many it disabled.
        /// </summary>
        /// <param name="host">The level's own host (never touched).</param>
        /// <param name="others">Every <see cref="LevelArenaHost"/> in the scene.</param>
        /// <param name="referenceScene">True in a scene under <c>Assets/_RefUnlicensed/</c>.</param>
        public static int HandleSharedArenaHosts(LevelArenaHost host, KatamariBallMotor motor,
            LevelArenaHost[] others, bool referenceScene, PlacementImportReport report)
        {
            if (host == null || motor == null || others == null) return 0;

            int disabled = 0;
            var kept = new List<string>();
            for (int i = 0; i < others.Length; i++)
            {
                LevelArenaHost other = others[i];
                if (other == null || other == host) continue;
                if (!other.enabled || !other.gameObject.activeInHierarchy) continue;

                var serialized = new SerializedObject(other);
                SerializedProperty link = serialized.FindProperty(ArenaMotorField);
                if (link == null || link.objectReferenceValue != motor) continue;

                string path = HierarchyPath(other.transform);
                if (referenceScene && other.transform.parent == null &&
                    RegistersNoGeometry(serialized))
                {
                    Undo.RecordObject(other, "Disable placeholder LevelArenaHost");
                    other.enabled = false;
                    UdonBehaviour backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(other);
                    if (backing != null) backing.enabled = false;
                    EditorUtility.SetDirty(other);
                    if (backing != null) EditorUtility.SetDirty(backing);
                    report.V2SceneWiringWrites++;
                    report.Note(ArenaMotorTag + "disabled the placeholder LevelArenaHost '" + path +
                                "': a scene-root host with no boxes, walls or ramps on the same " +
                                "motor would overwrite this level's ground plane and kind " +
                                "whitelist (it registers whichever runs last)");
                    disabled = disabled + 1;
                    continue;
                }

                kept.Add(path);
            }

            if (kept.Count > 0)
            {
                int shown = kept.Count < 5 ? kept.Count : 5;
                string listing = string.Join(", ", kept.GetRange(0, shown).ToArray());
                if (kept.Count > shown) listing = listing + ", ... (" + (kept.Count - shown) + " more)";
                ArenaMotorWarning(report, kept.Count + " other enabled LevelArenaHost(s) share this " +
                    "motor and were left enabled: " + listing + ". Hosts registering on one motor " +
                    "overwrite each other's ground plane and kind whitelist (last one wins); if " +
                    "one is an unused template host, disable it by hand");
            }

            return disabled;
        }

        // True when the host's three geometry arrays hold no object at all.
        private static bool RegistersNoGeometry(SerializedObject serialized)
        {
            return ArrayHoldsNothing(serialized.FindProperty(BoundaryWallsField)) &&
                   ArrayHoldsNothing(serialized.FindProperty(StaticBoxesField)) &&
                   ArrayHoldsNothing(serialized.FindProperty(RampBoxesField));
        }

        private static bool ArrayHoldsNothing(SerializedProperty array)
        {
            if (array == null || !array.isArray) return true;
            for (int i = 0; i < array.arraySize; i++)
            {
                if (array.GetArrayElementAtIndex(i).objectReferenceValue != null) return false;
            }
            return true;
        }

        private static void ArenaMotorWarning(PlacementImportReport report, string message)
        {
            report.Note(ArenaMotorTag + "warning: " + message);
            Debug.LogWarning(ArenaMotorTag + message);
        }
    }
}
