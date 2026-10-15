"""核对锁定源码和本地包的完成回执；构建仍由 vendor 的 x pack 负责。"""

import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


class FeedError(RuntimeError):
    pass


def digest(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


class PinnedSource:
    def __init__(self, root: Path):
        self.root = root
        self.vendor = root / "vendor/wanxiang2"

    @staticmethod
    def git(root: Path, *args: str) -> str:
        return subprocess.run(["git", "-C", str(root), *args], check=True,
                              capture_output=True, text=True).stdout.strip()

    def initialize(self) -> None:
        if not (self.vendor / ".git").exists():
            subprocess.run(["git", "-C", str(self.root), "submodule", "update", "--init", "--", "vendor/wanxiang2"], check=True)

    def revision(self) -> str:
        entries = self.git(self.root, "ls-files", "--stage", "--", "vendor/wanxiang2").splitlines()
        if len(entries) != 1:
            raise FeedError("vendor/wanxiang2 must have one staged gitlink")
        entry = entries[0].split(maxsplit=3)
        if len(entry) != 4 or entry[0] != "160000" or entry[2] != "0":
            raise FeedError("vendor/wanxiang2 has no resolved gitlink")
        if Path(self.git(self.vendor, "rev-parse", "--show-toplevel")).resolve() != self.vendor.resolve():
            raise FeedError("vendor/wanxiang2 is not initialized")
        if self.git(self.vendor, "rev-parse", "HEAD") != entry[1]:
            raise FeedError("vendor checkout differs from the staged pin; stage the intended gitlink first")
        if self.git(self.vendor, "status", "--porcelain", "--untracked-files=normal"):
            raise FeedError("vendor source has uncommitted changes; do not certify it as the pinned commit")
        return entry[1]

    def packages(self) -> set[str]:
        names: set[str] = set()
        # 与锁定 x pack 的主项目/包命名相同；不再实现依赖排序或增量构建。
        for group in ["libs", "compose"]:
            directory = self.vendor / group
            if not directory.exists():
                continue
            for part in directory.iterdir():
                if not part.is_dir():
                    continue
                if not any((part / (part.name + ext)).is_file() for ext in [".fsproj", ".csproj"]):
                    raise FeedError(f"missing package project: {part.name}")
                name = f"Wanxiang2.{part.name}.0.0.0-local.nupkg"
                if name in names:
                    raise FeedError(f"duplicate package: {name}")
                names.add(name)
        if not names:
            raise FeedError("pinned source declares no packages")
        return names


class FeedReceipt:
    def __init__(self, source: PinnedSource):
        self.source = source
        self.feed = source.vendor / "artifacts/feed"
        self.path = self.feed / "receipt.json"

    def measure(self, revision: str) -> dict:
        expected = self.source.packages()
        actual = {p.name for p in self.feed.glob("*.nupkg")}
        if actual != expected:
            missing = sorted(expected - actual)
            extra = sorted(actual - expected)
            raise FeedError(f"package set differs: missing={missing}, extra={extra}")
        if any((self.feed / name).is_symlink() for name in actual):
            raise FeedError("feed packages must be regular files, not links")
        return {"schema": 1, "revision": revision,
                "packages": {name: digest(self.feed / name) for name in sorted(actual)}}

    def check(self, revision: str) -> str:
        try:
            data = self.path.read_bytes()
            saved = json.loads(data)
        except (OSError, ValueError) as error:
            raise FeedError("feed has no readable completion receipt") from error
        if saved != self.measure(revision):
            raise FeedError("feed bytes or pinned revision differ from the completion receipt")
        return hashlib.sha256(data).hexdigest()

    def write(self, measured: dict) -> None:
        data = json.dumps(measured, sort_keys=True, separators=(",", ":")) + "\n"
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=self.feed, delete=False) as output:
            temporary = Path(output.name)
            try:
                output.write(data)
                output.flush()
                os.fsync(output.fileno())
                os.replace(temporary, self.path)
            finally:
                temporary.unlink(missing_ok=True)


@contextmanager
def feed_lock(root: Path, readonly: bool):
    path = root / ".scratch/wanxiang2-feed.lock"
    if readonly and not path.exists():
        yield
        return
    if not readonly:
        path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("rb" if readonly else "a+b") as handle:
        try:
            mode = fcntl.LOCK_SH if readonly else fcntl.LOCK_EX
            fcntl.flock(handle, mode | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise FeedError("another package preparation is active") from error
        yield


def consumer_cache(root: Path) -> Path:
    cache = root / ".nuget/packages"
    if cache.resolve() != cache:
        raise FeedError("application package cache is redirected; refusing to clear files outside its declared location")
    override = os.environ.get("NUGET_PACKAGES")
    if override and Path(override).expanduser().resolve() != cache:
        raise FeedError("NUGET_PACKAGES overrides the repository cache; unset it before preparing or using this feed")
    return cache


def clear_consumer_cache(root: Path) -> None:
    # 只清本仓的生成物，不碰用户全局包源或其他第三方包。
    cache = consumer_cache(root)
    for path in cache.glob("wanxiang2.*"):
        if path.is_symlink() or path.is_file():
            path.unlink()
        else:
            shutil.rmtree(path)
    for group in ["src", "tests"]:
        for path in (root / group).rglob("project.assets.json"):
            if "obj" in path.relative_to(root / group).parts:
                path.unlink()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--check", action="store_true", help="check only; never initialize or build")
    mode.add_argument("--fingerprint", action="store_true", help="check and print the receipt digest")
    mode.add_argument("--rebuild", action="store_true", help="force the existing x pack path")
    args = parser.parse_args()
    source = PinnedSource(Path(__file__).resolve().parents[1])
    receipt = FeedReceipt(source)
    readonly = args.check or args.fingerprint
    try:
        with feed_lock(source.root, readonly):
            consumer_cache(source.root)
            if not readonly:
                source.initialize()
            revision = source.revision()
            if not args.rebuild:
                try:
                    fingerprint = receipt.check(revision)
                    print(fingerprint if args.fingerprint else f"feed ready: {revision} ({len(source.packages())} packages)")
                    return 0
                except FeedError:
                    if readonly:
                        raise
            # 成功回执先撤销。半途失败即使留下旧包或部分新包，也不能再被接受。
            receipt.path.unlink(missing_ok=True)
            subprocess.run([str(source.vendor / "x"), "pack"], cwd=source.vendor, check=True)
            measured = receipt.measure(revision)
            if source.revision() != revision:
                raise FeedError("pin changed during package preparation")
            clear_consumer_cache(source.root)
            receipt.write(measured)
            print(f"feed ready: {revision} ({len(measured['packages'])} packages)")
            return 0
    except (FeedError, OSError, subprocess.CalledProcessError) as error:
        print(f"feed not ready: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
