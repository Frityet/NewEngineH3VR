#!/usr/bin/env python3
"""Controlled native subprocesses only; never launch or signal Unity."""

import os, signal, subprocess, sys, tempfile, time, unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "script"))
import run_native as runner


class CleanupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.root = Path(cls.temp.name)
        cls.exe = cls.root / "fake player"
        cls.other = cls.root / "unrelated player"
        source = cls.root / "fixture.c"
        source.write_text("""#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
int main(int argc,char **argv){signal(SIGTERM,SIG_IGN);for(int i=1;i+1<argc;i++)if(!strcmp(argv[i],"--ready")){FILE*f=fopen(argv[i+1],"w");fputs("ready",f);fclose(f);}for(;;)pause();}
""")
        subprocess.run(["clang", str(source), "-o", str(cls.exe)], check=True)
        cls.other.write_bytes(cls.exe.read_bytes())
        cls.other.chmod(0o755)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def setUp(self):
        self.children = []
        self.log = self.root / "current run.log"

    def tearDown(self):
        for p in self.children:
            if p.poll() is None:
                p.kill()
            p.wait(timeout=2)

    def child(self, exe=None, log=None):
        ready = self.root / ("ready-" + str(time.monotonic_ns()))
        p = subprocess.Popen(
            [str(exe or self.exe), "-logFile", str(log or self.log), "--ready", str(ready)]
        )
        self.children.append(p)
        end = time.monotonic() + 2
        while not ready.exists() and time.monotonic() < end:
            time.sleep(0.01)
        self.assertTrue(ready.exists())
        return p

    def launcher(self, early=False):
        p = subprocess.Popen(
            [sys.executable, "-c", "pass" if early else "import time;time.sleep(30)"]
        )
        self.children.append(p)
        if early:
            p.wait(timeout=2)
        return p

    def test_ignores_term_escalates_only_exact_new_instance(self):
        existing = self.child()
        baseline = runner.player_instances(self.exe)
        target = self.child()
        other_log = self.child(log=self.root / "unrelated.log")
        other_exe = self.child(exe=self.other)
        launch = self.launcher()
        r = runner.stop_player(launch, self.exe, self.log, baseline, grace=0.1, kill_wait=0.3)
        self.assertTrue(r["verifiedExited"], r)
        self.assertEqual(r["termPids"], [target.pid])
        self.assertEqual(r["killPids"], [target.pid])
        self.assertIsNotNone(launch.poll())
        self.assertEqual(target.wait(timeout=2), -signal.SIGKILL)
        for p in [existing, other_log, other_exe]:
            self.assertIsNone(p.poll())

    def test_launchservices_early_exit_still_cleans_player_without_diagnostic_pid(self):
        baseline = runner.player_instances(self.exe)
        target = self.child()
        launch = self.launcher(early=True)
        r = runner.stop_player(launch, self.exe, self.log, baseline, grace=0.1, kill_wait=0.3)
        self.assertTrue(r["verifiedExited"], r)
        self.assertEqual(r["killPids"], [target.pid])
        self.assertEqual(target.wait(timeout=2), -signal.SIGKILL)

    def test_snapshot_failure_is_reported_and_launcher_still_reaped(self):
        launch = self.launcher()
        with mock.patch.object(
            runner, "player_instances", side_effect=OSError("controlled inventory failure")
        ):
            r = runner.stop_player(launch, self.exe, self.log, {}, grace=0.1, kill_wait=0.1)
        self.assertFalse(r["verifiedExited"])
        self.assertTrue(r["errors"])
        self.assertIsNotNone(launch.poll())

    def test_no_player_is_a_verified_noop_even_after_early_exit(self):
        launch = self.launcher(early=True)
        r = runner.stop_player(launch, self.exe, self.log, {}, grace=0.05, kill_wait=0.1)
        self.assertTrue(r["verifiedExited"], r)
        self.assertEqual(r["termPids"], [])
        self.assertEqual(r["killPids"], [])

    def test_changed_identity_is_not_signalled(self):
        launch = self.launcher(early=True)
        pid = 99999999
        with (
            mock.patch.object(
                runner,
                "player_instances",
                side_effect=[{pid: "first-start"}, {pid: "replacement-start"}, {}, {}],
            ),
            mock.patch.object(
                runner, "process_arguments", return_value=["fixture", "-logFile", str(self.log)]
            ),
            mock.patch.object(runner.os, "kill") as send,
        ):
            r = runner.stop_player(launch, self.exe, self.log, {}, grace=0, kill_wait=0)
        self.assertEqual(r["termPids"], [])
        self.assertEqual(r["killPids"], [])
        send.assert_not_called()


if __name__ == "__main__":
    unittest.main()
