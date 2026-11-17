"""The importer/sync console contract (source pins, no Unity).

The design note promises two console lines. One of them is the sync's own summary (which
carries `growthMm3: written=..., read-back: verified`): it must be logged from the importer
path as well as the menu path (`PickupFieldSyncReport.Log`), so the operator can check the
total without decoding the host array.

These are STRING pins on the editor sources, the same kind the PTF tests use: a log line
the operator is told to expect is a contract, and it must not disappear silently again.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
IMPORTER = (ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/LevelBuild/"
                   "PlacementImporter.cs")
SYNC = (ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Pickup/"
               "PickupFieldSync.cs")
SYNC_REPORT = (ROOT / "VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Editor/Pickup/"
                      "PickupFieldSyncReport.cs")


class ImporterLogContractTests(unittest.TestCase):
    def test_the_importer_logs_the_sync_summary(self):
        text = IMPORTER.read_text(encoding="utf-8")
        self.assertIn("sync.Log();", text,
                      "the importer path must log the sync report: its summary carries the "
                      "growthMm3 read-back line the operator is told to expect")
        # It must be logged before the counters are copied, i.e. right after the call.
        call = text.index("PickupFieldSync.SyncFromChildren(host, propsRoot,")
        log = text.index("sync.Log();")
        ran = text.index("report.SyncRan = true;")
        self.assertLess(call, log)
        self.assertLess(log, ran)
        # The note names the authored growth column, not only propVolumeK.
        self.assertIn("propGrowthMm3", text)
        # The CS0165 fix (out var potentially unassigned when the map is null) stays.
        self.assertIn("long declaredGrowth = 0L;", SYNC.read_text(encoding="utf-8"))

    def test_the_sync_summary_has_the_growth_line(self):
        text = SYNC_REPORT.read_text(encoding="utf-8")
        for fragment in ("growthMm3: written=", "fromFile=", "fromBounds=",
                         "read-back: ", "verified"):
            self.assertIn(fragment, text)
        # The old names lied about the quantity and must not come back.
        self.assertNotIn("preservedRows=", text)
        self.assertNotIn("unpricedRows=", text)


if __name__ == "__main__":
    unittest.main()
