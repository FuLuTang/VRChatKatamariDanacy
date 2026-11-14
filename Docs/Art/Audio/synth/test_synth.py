#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""占位音效的轻量校验：确定性、峰值/格式、循环接缝、清单一致性。

运行::

    cd Docs/Art/Audio/synth && python3 -m unittest -v test_synth

只检查本目录生成器与 ``../rendered/`` 的产物；不检查 Unity 导入、VRChat 内实际
音量、空间化或任何玩法行为。
"""

import array
import csv
import os
import sys
import tempfile
import unittest
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import synth  # noqa: E402  (需要先把本目录放进 sys.path)

RENDERED = os.path.abspath(os.path.join(HERE, "..", "rendered"))
PEAK_INT_LIMIT = int(synth.PEAK_LIMIT * 32767)   # 29163 ≈ -1.01 dBFS
Q_ONE_LSB = 1.0 / 32768.0


def read_wav(path):
    """读单声道 16-bit WAV，返回 (array('h'), 参数 dict)。"""
    with wave.open(path, "rb") as w:
        params = {
            "channels": w.getnchannels(),
            "sample_width": w.getsampwidth(),
            "frame_rate": w.getframerate(),
            "frames": w.getnframes(),
        }
        raw = w.readframes(w.getnframes())
    pcm = array.array("h")
    pcm.frombytes(raw)
    if sys.byteorder == "big":
        pcm.byteswap()
    return pcm, params


def read_bytes(path):
    with open(path, "rb") as fh:
        return fh.read()


def table_entries():
    return {e["file"]: e for e in synth.SOUND_TABLE}


class TestRenderedFiles(unittest.TestCase):
    """检查已提交的 rendered/ 产物（不是现场生成）。"""

    def test_all_files_exist_and_are_small(self):
        for entry in synth.SOUND_TABLE:
            path = os.path.join(RENDERED, entry["file"])
            self.assertTrue(os.path.isfile(path), "缺少 %s" % entry["file"])
            self.assertLess(os.path.getsize(path), 300 * 1024,
                            "%s 超过 300 KB" % entry["file"])

    def test_wav_format_and_peak(self):
        for entry in synth.SOUND_TABLE:
            path = os.path.join(RENDERED, entry["file"])
            pcm, p = read_wav(path)
            self.assertEqual(p["channels"], 1, entry["file"])
            self.assertEqual(p["sample_width"], 2, entry["file"])
            self.assertEqual(p["frame_rate"], synth.SR, entry["file"])
            self.assertGreater(p["frames"], 0, entry["file"])
            self.assertLessEqual(len(pcm), p["frames"], entry["file"])
            top = max(abs(v) for v in pcm)
            self.assertLessEqual(top, PEAK_INT_LIMIT,
                                 "%s 峰值 %d 超过 %.2f" % (entry["file"], top, synth.PEAK_LIMIT))
            self.assertGreater(top, 0.05 * 32767, "%s 几乎无声" % entry["file"])

    def test_loop_seams(self):
        checked = 0
        for entry in synth.SOUND_TABLE:
            if not entry["loop"]:
                continue
            pcm, _ = read_wav(os.path.join(RENDERED, entry["file"]))
            delta, ratio = synth.seam_info(pcm)
            checked += 1
            # 回绕跳变不得大于接缝附近任何一步相邻采样差（<1 = 听不出接缝）
            self.assertLess(ratio, 1.0,
                            "%s 接缝比 %.2f（delta %.1f dBFS）" %
                            (entry["file"], ratio, synth.db(delta)))
        self.assertEqual(checked, sum(1 for e in synth.SOUND_TABLE if e["loop"]))

    def test_manifest_matches(self):
        path = os.path.join(RENDERED, "manifest.csv")
        self.assertTrue(os.path.isfile(path), "缺少 manifest.csv")
        with open(path, newline="", encoding="utf-8") as fh:
            rows = list(csv.DictReader(fh))
        entries = table_entries()
        self.assertEqual(len(rows), len(entries))
        self.assertEqual({r["file"] for r in rows}, set(entries))
        for row in rows:
            entry = entries[row["file"]]
            self.assertTrue(row["event_ids"].strip(), row["file"])
            self.assertEqual(int(row["seed"]), entry["seed"], row["file"])
            self.assertEqual(row["loop"], "yes" if entry["loop"] else "no", row["file"])
            self.assertEqual(row["generator"], entry["fn"].__name__, row["file"])
            self.assertAlmostEqual(float(row["duration_s"]),
                                   int(row["file_bytes"]) / 96000.0, places=3)

    def test_total_size_reported(self):
        total = sum(os.path.getsize(os.path.join(RENDERED, e["file"]))
                    for e in synth.SOUND_TABLE)
        self.assertLess(total, 3 * 1024 * 1024, "合计 %.1f KB" % (total / 1024.0))


class TestGenerator(unittest.TestCase):
    """检查生成器本身：确定性、峰值上限、与已提交产物一致。"""

    def test_same_seed_same_bytes(self):
        for entry in synth.SOUND_TABLE:
            a, _ = synth.render_pcm(entry)
            b, _ = synth.render_pcm(entry)
            self.assertEqual(a.tobytes(), b.tobytes(), entry["file"])

    def test_rendered_files_match_generator(self):
        """防止 rendered/ 与 synth.py 脱节（改了合成器却忘了重新渲染）。"""
        for entry in synth.SOUND_TABLE:
            pcm, _ = synth.render_pcm(entry)
            shipped, _ = read_wav(os.path.join(RENDERED, entry["file"]))
            self.assertEqual(shipped.tobytes(), pcm.tobytes(),
                             "%s 与生成器不一致，请重新运行 synth.py" % entry["file"])

    def test_render_all_is_deterministic(self):
        with tempfile.TemporaryDirectory() as tmp:
            a = synth.render_all(os.path.join(tmp, "a"), verbose=False)
            b = synth.render_all(os.path.join(tmp, "b"), verbose=False)
            self.assertEqual([r["file"] for r in a], [r["file"] for r in b])
            for row_a, row_b in zip(a, b):
                for field in ("peak_dbfs", "duration_s", "loop", "seed",
                              "rms_dbfs", "loop_seam_ratio"):
                    self.assertEqual(row_a[field], row_b[field], row_a["file"])
            self.assertEqual(
                read_bytes(os.path.join(tmp, "a", "manifest.csv")),
                read_bytes(os.path.join(tmp, "b", "manifest.csv")))

    def test_seam_metric_is_sane(self):
        # 故意造一个不连续的"循环"：接缝比应远大于 1
        n = 4000
        x = [0.5 * (1.0 if i < n // 2 else -1.0) for i in range(n)]
        pcm = synth.quantize(x)
        delta, ratio = synth.seam_info(pcm)
        self.assertGreater(delta, 0.5)
        self.assertGreater(ratio, 1.0)


if __name__ == "__main__":
    unittest.main(verbosity=2)
