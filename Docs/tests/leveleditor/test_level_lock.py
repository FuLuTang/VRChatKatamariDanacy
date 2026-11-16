#!/usr/bin/env python3
"""The one-writer lock's state machine, without an HTTP server.

These are the rules the editor's open/reload path depends on:

* the flock, not the body, is the authority;
* a dead holder's file is taken over and the takeover is logged;
* a live holder's body is reported with pid/port/level;
* the filename scheme is rotated so an old lock file cannot be mistaken for the
  current holder;
* two different level files never block each other.
"""

from __future__ import annotations

import contextlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from Tools.leveleditor import server as web  # noqa: E402


class LevelLockTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="leveleditor-lock-"))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.state = self.tmp / "state"
        self.first_path = self.tmp / "levels" / "one" / "placement.json"
        self.second_path = self.tmp / "levels" / "two" / "placement.json"
        for path in (self.first_path, self.second_path):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("{}", encoding="utf-8")

    def lock(self, path=None, **kwargs):
        return web.LevelLock(path or self.first_path, self.state, **kwargs)

    def test_body_has_pid_port_level_and_a_rotated_name(self):
        lock = self.lock(level="story_01_bench_size", port=8777)
        lock.acquire()
        self.addCleanup(lock.release)
        self.assertTrue(lock.path.name.startswith("v2-"), lock.path.name)
        self.assertNotIn("48595ce43597134b", lock.path.name)
        body = json.loads(lock.path.read_text(encoding="utf-8"))
        self.assertEqual(body["pid"], os.getpid())
        self.assertEqual(body["port"], 8777)
        self.assertEqual(body["level"], "story_01_bench_size")
        self.assertEqual(Path(body["placement"]), self.first_path.resolve())
        lock.announce(port=9000, level="renamed")
        body = json.loads(lock.path.read_text(encoding="utf-8"))
        self.assertEqual(body["port"], 9000)
        self.assertEqual(body["level"], "renamed")

    def test_a_second_instance_is_refused_and_names_the_holder(self):
        first = self.lock(level="story_01_bench_size", port=8777)
        first.acquire()
        self.addCleanup(first.release)
        second = self.lock(level="story_01_bench_size", port=8778)
        with self.assertRaises(RuntimeError) as caught:
            second.acquire()
        message = str(caught.exception)
        self.assertIn("one level, one writer", message)
        self.assertIn("pid %d" % os.getpid(), message)
        self.assertIn("port 8777", message)
        self.assertIn("level story_01_bench_size", message)
        self.assertIsNone(second._fd)

    def test_a_stale_pid_is_taken_over_and_logged(self):
        lock = self.lock(level="old_level")
        lock.path.parent.mkdir(parents=True, exist_ok=True)
        stale = {"pid": 999999, "port": 8123, "level": "old_level"}
        lock.path.write_text(json.dumps(stale), encoding="utf-8")
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            lock.acquire()
        self.addCleanup(lock.release)
        self.assertEqual(lock.taken_over["pid"], 999999)
        body = json.loads(lock.path.read_text(encoding="utf-8"))
        self.assertEqual(body["pid"], os.getpid())
        self.assertIn("took over", stderr.getvalue())
        self.assertIn("stale lock", stderr.getvalue())
        self.assertIn("pid 999999", stderr.getvalue())

    def test_a_stale_legacy_lock_file_does_not_block(self):
        lock = self.lock()
        lock.path.parent.mkdir(parents=True, exist_ok=True)
        legacy = lock.legacy_path
        legacy.write_text("1653621\n", encoding="utf-8")
        lock.acquire()
        self.addCleanup(lock.release)
        self.assertNotEqual(lock.path, legacy)
        self.assertTrue(lock.path.is_file())
        # The old body was stale (no flock), so it is taken over and replaced.
        self.assertEqual(legacy.read_text(encoding="utf-8").strip(), str(os.getpid()))

    def test_a_live_legacy_holder_still_blocks_a_new_editor(self):
        lock = self.lock()
        lock.path.parent.mkdir(parents=True, exist_ok=True)
        legacy = lock.legacy_path
        descriptor = os.open(str(legacy), os.O_RDWR | os.O_CREAT, 0o600)
        import fcntl
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        os.write(descriptor, b"1653621\n")
        try:
            with self.assertRaises(RuntimeError) as caught:
                lock.acquire()
            message = str(caught.exception)
            self.assertIn("older lock format", message)
            self.assertIn("pid 1653621", message)
            self.assertIsNone(lock._fd)
            self.assertIsNone(lock._legacy_fd)
        finally:
            fcntl.flock(descriptor, fcntl.LOCK_UN)
            os.close(descriptor)

    def test_different_level_files_do_not_block_each_other(self):
        first = self.lock(self.first_path)
        second = self.lock(self.second_path)
        first.acquire()
        self.addCleanup(first.release)
        second.acquire()
        self.addCleanup(second.release)
        self.assertNotEqual(first.path, second.path)
        self.assertIsNotNone(first._fd)
        self.assertIsNotNone(second._fd)

    def test_release_allows_a_new_acquire_and_double_acquire_is_refused(self):
        lock = self.lock()
        lock.acquire()
        with self.assertRaises(RuntimeError):
            lock.acquire()
        lock.release()
        self.assertIsNone(lock._fd)
        lock.acquire()
        lock.release()

    def test_dead_pid_helper(self):
        self.assertFalse(web.LevelLock._pid_alive(999999))
        self.assertTrue(web.LevelLock._pid_alive(os.getpid()))


if __name__ == "__main__":
    unittest.main()
