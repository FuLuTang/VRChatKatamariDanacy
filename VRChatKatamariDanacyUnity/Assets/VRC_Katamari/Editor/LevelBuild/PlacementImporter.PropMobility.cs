using System.Collections.Generic;
using UnityEngine;
using VRCKatamari.Editor.Pickup;
using VRCKatamari.Pickup;

namespace VRCKatamari.Editor.LevelBuild
{
    /// <summary>
    /// The pickup half of the motion import: tells each <see cref="PickupFieldHost"/> of the
    /// level which of its rows the motion host may move (its <c>propMobile</c> column), so the
    /// host can find every other row by grid cell instead of reading its Transform each scan.
    /// </summary>
    /// <remarks>
    /// The rule is <see cref="PickupFieldAuthoringRules.FindMobileMotionItemIds"/> over the same
    /// <see cref="MotionRowTable"/> that fills the motion host, so the two cannot disagree. Runs
    /// even when the table is empty (then the column is all false). A failure is a note, not an
    /// error: an absent or stale column only makes the host read every row live, which is
    /// correct and slower.
    /// </remarks>
    public static partial class PlacementImporter
    {
        private static void SyncPropMobility(Transform levelRoot, MotionRowTable table,
            PlacementImportReport report)
        {
            HashSet<string> mobileIds = PickupFieldAuthoringRules.FindMobileMotionItemIds(
                table.ItemIds, table.ParentRow, table.Inherit, table.MotionClass);
            PickupFieldHost[] hosts = levelRoot.GetComponentsInChildren<PickupFieldHost>(true);
            for (int h = 0; h < hosts.Length; h++)
            {
                int rows;
                string failure;
                int mobile = PickupFieldSync.ApplyMobileDeclaration(hosts[h], mobileIds, out rows,
                    out failure);
                if (mobile < 0)
                {
                    report.Note(V2Tag + "propMobile not written on '" + hosts[h].name + "': " +
                                failure + " (the host then reads every prop live: correct, slower)");
                    continue;
                }

                report.Note(V2Tag + "propMobile on '" + hosts[h].name + "': mobile rows=" + mobile +
                            " of " + rows + " (the rest are found by grid cell)");
            }
        }
    }
}
