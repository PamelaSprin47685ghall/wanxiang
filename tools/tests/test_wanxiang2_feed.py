"""包交付入口的反例。临时 Git 仓库与故障命令只验证脚本，不冒充真实 NuGet 消费。"""

import fcntl
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


SOURCE = Path(__file__).resolve().parents[2]


class FeedTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="wanxiang-feed-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "checkout with spaces"
        self.vendor = self.root / "wanxiang2"
        self.feed = self.vendor / "artifacts/feed"
        self.binary = self.root / "tests/Wanxiang.Tests/bin/Debug/net10.0/Wanxiang.Tests"
        self.root.mkdir()
        for name in ["run-tests.sh", "start.sh", "tools/wanxiang2-feed.sh", "tools/wanxiang2_feed.py", "tools/test_inventory.py"]:
            source = SOURCE / name
            if source.exists():
                target = self.root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
        self.write(".gitignore", ".scratch/\n.nuget/\n.Herdchestra/\ntests/**/bin/\ntests/**/obj/\nsrc/**/bin/\nsrc/**/obj/\ncommands/\nran\nbuilt\nlaunched\n")
        self.write(".gitmodules", '[submodule "wanxiang2"]\n\tpath = wanxiang2\n\turl = ../upstream\n\tbranch = main\n')
        self.write("tests/test-counts.baseline", "UI=0\nNonUI=1\nLargeHistory=0\n")
        self.write("tests/Wanxiang.Tests/Test.fs", "[<Fact>]\nlet probe () = ()\n")
        self.write("wanxiang2/.gitignore", "artifacts/\n")
        for name in ["LedgerStore", "Client"]:
            self.write(f"wanxiang2/libs/{name}/{name}.fsproj", "<Project/>\n")
        self.write("wanxiang2/revision.txt", "first")
        self.write("wanxiang2/x", """#!/usr/bin/env python3
import os
from pathlib import Path
import sys
root = Path(__file__).resolve().parent
feed = root / 'artifacts/feed'
feed.mkdir(parents=True, exist_ok=True)
with (root / 'artifacts/pack-calls').open('a') as log:
    log.write('pack\\n')
for name in ['LedgerStore', 'Client']:
    (feed / f'Wanxiang2.{name}.0.0.0-local.nupkg').write_bytes((root / 'revision.txt').read_bytes())
    if os.environ.get('PACK_FAIL'):
        sys.exit(31)
    if os.environ.get('PACK_PARTIAL'):
        sys.exit(0)
if os.environ.get('CHANGE_SOURCE'):
    (root / 'revision.txt').write_text('changed during pack')
""", executable=True)
        self.write(str(self.binary.relative_to(self.root)), """#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[5]
with (root / 'ran').open('a') as log:
    log.write('test\\n')
if '-list' in sys.argv:
    print(json.dumps([{'Class': 'Fixture', 'Method': 'probe', 'Traits': {}}]))
else:
    if os.environ.get('TEST_CHANGE_SOURCE'):
        (root / 'wanxiang2/revision.txt').write_text('changed during tests')
    print('Fixture Total: 1, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0')
""", executable=True)
        self.write("commands/dotnet", """#!/usr/bin/env python3
import os
from pathlib import Path
import sys
root = Path(__file__).resolve().parents[1]
(root / 'built').write_text('build')
sys.exit(42 if os.environ.get('BUILD_FAIL') else 0)
""", executable=True)
        self.write("src/Wanxiang.App/bin/Debug/net10.0/wanxiang", """#!/usr/bin/env python3
from pathlib import Path
root = Path(__file__).resolve().parents[5]
(root / 'launched').write_text('launch')
""", executable=True)
        self.env = dict(os.environ, HOME=str(Path(self.temp.name) / "home"),
                        PATH=str(self.root / "commands") + os.pathsep + os.environ["PATH"])
        self.git(self.vendor, "init", "-q", "-b", "main")
        self.commit(self.vendor)
        self.git(self.root, "init", "-q")
        self.commit(self.root)

    def write(self, name, text, executable=False):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
        if executable:
            target.chmod(0o755)

    def git(self, root, *args):
        return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True, text=True).stdout.strip()

    def commit(self, root):
        self.git(root, "add", ".")
        self.git(root, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "fixture")

    def run_script(self, script, *args, **env):
        return subprocess.run(["bash", str(self.root / script), *args], cwd=self.temp.name,
                              env=dict(self.env, **env), capture_output=True, text=True, timeout=20)

    def prepare(self, *args, **env):
        return self.run_script("tools/wanxiang2-feed.sh", *args, **env)

    def ok(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_pack_failure_stops_before_build_or_old_test_binary(self):
        result = self.run_script("run-tests.sh", "--no-build", "--no-ui", PACK_FAIL="1")
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse((self.root / "built").exists())
        self.assertFalse((self.root / "ran").exists())

    def test_receipt_checks_every_package_and_reuses_unchanged_feed(self):
        self.ok(self.prepare())
        self.ok(self.prepare("--check"))
        self.ok(self.prepare())
        self.assertEqual((self.vendor / "artifacts/pack-calls").read_text(), "pack\n")
        package = self.feed / "Wanxiang2.Client.0.0.0-local.nupkg"
        package.write_bytes(b"different bytes")
        self.assertNotEqual(self.prepare("--check").returncode, 0)
        self.ok(self.prepare())
        package.unlink()
        self.assertNotEqual(self.prepare("--check").returncode, 0)

    def test_foreign_package_and_bad_receipt_are_not_ready(self):
        self.ok(self.prepare())
        extra = self.feed / "Wanxiang2.Obsolete.0.0.0-local.nupkg"
        extra.write_bytes(b"old")
        self.assertNotEqual(self.prepare("--check").returncode, 0)
        extra.unlink()
        (self.feed / "receipt.json").write_text("null")
        self.assertNotEqual(self.prepare("--check").returncode, 0)

    def test_rebuild_clears_only_application_library_cache_and_assets(self):
        self.ok(self.prepare())
        self.write(".nuget/packages/wanxiang2.client/old", "stale")
        self.write(".nuget/packages/third.party/keep", "keep")
        self.write("src/App/obj/project.assets.json", "stale")
        self.write("tests/Wanxiang.Tests/obj/project.assets.json", "stale")
        self.ok(self.prepare("--rebuild"))
        self.assertFalse((self.root / ".nuget/packages/wanxiang2.client").exists())
        self.assertFalse((self.root / "src/App/obj/project.assets.json").exists())
        self.assertFalse((self.root / "tests/Wanxiang.Tests/obj/project.assets.json").exists())
        self.assertEqual((self.root / ".nuget/packages/third.party/keep").read_text(), "keep")

    def test_dirty_or_unpinned_source_is_not_built(self):
        self.write("wanxiang2/revision.txt", "second")
        self.assertNotEqual(self.prepare().returncode, 0)
        self.assertFalse(self.feed.exists())
        self.commit(self.vendor)
        self.assertNotEqual(self.prepare().returncode, 0)
        self.assertFalse(self.feed.exists())
        self.git(self.root, "add", "wanxiang2")
        self.ok(self.prepare())

    def test_source_change_during_pack_does_not_leave_a_receipt(self):
        self.assertNotEqual(self.prepare(CHANGE_SOURCE="1").returncode, 0)
        self.assertFalse((self.feed / "receipt.json").exists())

    def test_success_exit_with_incomplete_packages_is_not_ready(self):
        self.assertNotEqual(self.prepare(PACK_PARTIAL="1").returncode, 0)
        self.assertFalse((self.feed / "receipt.json").exists())

    def test_redirected_cache_is_not_cleared(self):
        outside = Path(self.temp.name) / "external-cache"
        (outside / "packages/wanxiang2.keep").mkdir(parents=True)
        (outside / "packages/wanxiang2.keep/value").write_text("keep")
        (self.root / ".nuget").symlink_to(outside, target_is_directory=True)
        self.assertNotEqual(self.prepare().returncode, 0)
        self.assertEqual((outside / "packages/wanxiang2.keep/value").read_text(), "keep")
        self.assertFalse((self.feed / "receipt.json").exists())

    def test_external_cache_override_cannot_bypass_a_ready_receipt(self):
        self.ok(self.prepare())
        outside = str(Path(self.temp.name) / "other-cache")
        self.assertNotEqual(self.prepare(NUGET_PACKAGES=outside).returncode, 0)
        self.assertNotEqual(self.prepare("--check", NUGET_PACKAGES=outside).returncode, 0)
        self.assertEqual((self.vendor / "artifacts/pack-calls").read_text(), "pack\n")

    def test_failed_rebuild_invalidates_previous_completion(self):
        self.ok(self.prepare())
        self.assertNotEqual(self.prepare("--rebuild", PACK_FAIL="1").returncode, 0)
        self.assertFalse((self.feed / "receipt.json").exists())
        self.assertNotEqual(self.prepare("--check").returncode, 0)

    def test_parallel_prepare_is_refused_without_touching_feed(self):
        self.ok(self.prepare())
        receipt = (self.feed / "receipt.json").read_bytes()
        with (self.root / ".scratch/wanxiang2-feed.lock").open("rb") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.assertNotEqual(self.prepare("--rebuild").returncode, 0)
        self.assertEqual(receipt, (self.feed / "receipt.json").read_bytes())

    def test_no_build_requires_binary_receipt_then_accepts_same_feed(self):
        self.ok(self.prepare())
        result = self.run_script("run-tests.sh", "--no-build", "--no-ui")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.root / "ran").exists())
        self.ok(self.run_script("run-tests.sh", "--no-ui"))
        self.ok(self.run_script("run-tests.sh", "--no-build", "--no-ui"))

    def test_pin_change_refuses_old_binary_even_after_successful_pack(self):
        self.ok(self.run_script("run-tests.sh", "--no-ui"))
        (self.root / "ran").unlink()
        self.write("wanxiang2/revision.txt", "second")
        self.commit(self.vendor)
        self.git(self.root, "add", "wanxiang2")
        result = self.run_script("run-tests.sh", "--no-build", "--no-ui")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.root / "ran").exists())
        self.ok(self.run_script("run-tests.sh", "--no-ui"))

    def test_failed_build_never_reuses_previous_binary_receipt(self):
        self.ok(self.run_script("run-tests.sh", "--no-ui"))
        (self.root / "ran").unlink()
        result = self.run_script("run-tests.sh", "--no-ui", BUILD_FAIL="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.root / "ran").exists())
        self.assertFalse((self.binary.parent / "wanxiang2-feed.sha256").exists())

    def test_passing_test_output_cannot_hide_source_change_during_run(self):
        result = self.run_script("run-tests.sh", "--no-ui", TEST_CHANGE_SOURCE="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Failed: 0", result.stdout)
        self.assertIn("包源在测试期间变化", result.stderr)

    def test_launcher_stops_on_pack_failure_before_build_or_data_creation(self):
        config = self.root / ".scratch/runtime/config.toml"
        data = self.root / ".scratch/data"
        result = self.run_script("start.sh", str(config), str(data), PACK_FAIL="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.root / "built").exists())
        self.assertFalse((self.root / "launched").exists())
        self.assertFalse(data.exists())

    def test_launcher_no_build_refuses_old_pin_and_accepts_its_own_build(self):
        args = [str(self.root / ".scratch/runtime/config.toml"), str(self.root / ".scratch/data")]
        self.assertNotEqual(self.run_script("start.sh", *args, WANXIANG_SKIP_BUILD="1").returncode, 0)
        self.assertFalse((self.root / "launched").exists())
        self.ok(self.run_script("start.sh", *args, WANXIANG_SKIP_BUILD="0"))
        self.ok(self.run_script("start.sh", *args, WANXIANG_SKIP_BUILD="1"))
        (self.root / "launched").unlink()
        self.write("wanxiang2/revision.txt", "second")
        self.commit(self.vendor)
        self.git(self.root, "add", "wanxiang2")
        self.assertNotEqual(self.run_script("start.sh", *args, WANXIANG_SKIP_BUILD="1").returncode, 0)
        self.assertFalse((self.root / "launched").exists())

    def test_source_check_needs_no_packages_and_rejects_old_location(self):
        self.ok(self.prepare("--source-check"))
        self.assertFalse(self.feed.exists())
        self.git(self.root, "config", "-f", ".gitmodules", "submodule.wanxiang2.path", "vendor/wanxiang2")
        self.assertNotEqual(self.prepare("--source-check").returncode, 0)

    def test_tracked_binary_packages_are_rejected_in_both_repositories(self):
        for directory in [self.root, self.vendor]:
            for name in ["small.tgz", "small.TGZ", "small.nupkg", "small.snupkg"]:
                with self.subTest(directory=directory, name=name):
                    package = directory / name
                    package.write_bytes(b"x")
                    self.git(directory, "add", name)
                    result = self.prepare("--source-check")
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn("binary packages must not enter Git", result.stderr)
                    self.git(directory, "rm", "-f", "--", name)

    def upstream(self):
        remote = self.root.parent / "upstream"
        self.git(self.root.parent, "clone", "--bare", str(self.vendor), str(remote))
        self.git(self.vendor, "remote", "add", "origin", str(remote))
        self.git(self.root, "config", "-f", ".gitmodules", "submodule.wanxiang2.url", str(remote))
        self.commit(self.root)
        return remote

    def test_update_fast_forwards_without_building_or_staging(self):
        remote = self.upstream()
        publisher = self.root.parent / "publisher"
        self.git(self.root.parent, "clone", str(remote), str(publisher))
        (publisher / "revision.txt").write_text("remote successor")
        self.commit(publisher)
        self.git(publisher, "push", "origin", "main")
        pin = self.git(self.root, "ls-files", "--stage", "--", "wanxiang2")
        self.ok(self.prepare("--update"))
        self.assertEqual(self.git(self.vendor, "rev-parse", "HEAD"), self.git(publisher, "rev-parse", "HEAD"))
        self.assertEqual(pin, self.git(self.root, "ls-files", "--stage", "--", "wanxiang2"))
        self.assertFalse(self.feed.exists())
        self.assertNotEqual(self.prepare("--source-check").returncode, 0)
        self.git(self.root, "add", "wanxiang2")
        self.ok(self.prepare("--source-check"))

    def test_update_preserves_unpublished_commits(self):
        self.upstream()
        self.write("wanxiang2/revision.txt", "local work")
        self.commit(self.vendor)
        self.git(self.root, "add", "wanxiang2")
        before = self.git(self.vendor, "rev-parse", "HEAD")
        result = self.prepare("--update")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("local wanxiang2 commits", result.stderr)
        self.assertEqual(before, self.git(self.vendor, "rev-parse", "HEAD"))
        self.assertEqual((self.vendor / "revision.txt").read_text(), "local work")
        self.assertFalse(self.feed.exists())

    def test_update_does_not_adopt_binary_packages_from_remote(self):
        remote = self.upstream()
        publisher = self.root.parent / "publisher"
        self.git(self.root.parent, "clone", str(remote), str(publisher))
        (publisher / "small.TGZ").write_bytes(b"x")
        self.commit(publisher)
        self.git(publisher, "push", "origin", "main")
        before = self.git(self.vendor, "rev-parse", "HEAD")
        result = self.prepare("--update")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("origin/main contains tracked binary packages", result.stderr)
        self.assertEqual(before, self.git(self.vendor, "rev-parse", "HEAD"))
        self.assertFalse((self.vendor / "small.TGZ").exists())


if __name__ == "__main__":
    unittest.main()
