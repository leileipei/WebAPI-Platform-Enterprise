"""Run the public packaging CLI exclusively in disposable Git repositories."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[2]
ARCHIVE = "WebAPI_Enterprise_核心闭环源码与验收.zip"
RECEIPT = "docs/evidence/core-loop/verification.json"
REFERENCE_PATHS = (
    "docs/evidence/m4-console/verification.json",
    "docs/evidence/m4-console/final-review-qa.json",
    "docs/evidence/core-loop/final-review.md",
    "docs/evidence/core-loop/scenarios.json",
    "docs/evidence/core-loop/faults.json",
)
BUILD = """import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
if (existsSync('dist') || existsSync('node_modules/poison') || existsSync('untracked')) process.exit(3);
mkdirSync('dist');
writeFileSync('dist/index.html', readFileSync('input.txt'));
"""


class CorePackageTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.repo = Path(temp.name)
        self.out = self.repo / "deliverables"
        self.git("init", "-q")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "user.name", "Package fixture")
        for script in ("package.sh", "package-core.py"):
            source = ROOT / "scripts" / script
            if source.exists():
                self.write("scripts/" + script, source.read_text())
        self.write("README.md", "first")
        self.write("package-evidence/core-loop/verification.json", "source namespace")
        self.write(REFERENCE_PATHS[2], "historical review still pending")
        self.package_data = {"name": "package-fixture", "version": "1.0.0",
                             "scripts": {"build": "node build.mjs"}}
        self.write("console/package.json", json.dumps(self.package_data))
        self.write("console/pnpm-lock.yaml", "lockfileVersion: '9.0'\nsettings:\n  autoInstallPeers: true\n  excludeLinksFromLockfile: false\nimporters:\n  .: {}\n")
        self.write("console/build.mjs", BUILD)
        self.write("console/input.txt", "fresh first")
        self.write("console/dist/index.html", "tracked old output")
        self.write(".worktrees/foreign/token", "other checkout")
        self.write(".aws/config", "private")
        self.write(".npmrc", "//registry.example/:_authToken=private")
        self.write("nested/.env.local", "private")
        self.write("nested/.secrets/key", "private")
        self.write("nested/dump.sql.dump", "private")
        self.write("old.log", "private")
        self.write(RECEIPT, json.dumps({"complete": True, "testedSourceRevision": "badcafe"}))
        self.first = self.commit("first")
        self.write("README.md", "second")
        self.write("console/input.txt", "fresh second")
        self.second = self.commit("second")

    def write(self, name, content):
        target = self.repo / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content)

    def write_evidence_files(self):
        for path in REFERENCE_PATHS:
            content = (ROOT / path).read_text()
            if path == REFERENCE_PATHS[1]:
                content = json.dumps({**json.loads(content), "testedSourceRevision": self.first})
            self.write(path, content)

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.repo, text=True).strip()

    def commit(self, name):
        self.git("add", "-A", "--", ".", ":(exclude)deliverables")
        self.git("commit", "-qm", name)
        return self.git("rev-parse", "HEAD")

    def package(self, *args, success=True, env=None):
        result = subprocess.run(["bash", "scripts/package.sh", *args], cwd=self.repo,
                                env={**os.environ, "CI": "true", **(env or {})}, capture_output=True, text=True)
        if success:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            return json.loads((self.out / "manifest.json").read_text())
        self.assertNotEqual(result.returncode, 0, "Unexpectedly packaged invalid input")
        return result

    def test_default_build_uses_only_selected_tracked_bytes(self):
        self.write("README.md", "dirty")
        self.write("console/input.txt", "dirty")
        self.write("console/dist/index.html", "local stale")
        self.write("console/node_modules/poison", "poison")
        self.write("console/untracked", "dirty")
        self.write("untracked.txt", "dirty")
        self.write("other-checkout/.git", "gitdir: elsewhere")
        self.write("local-preload.cjs", "throw Error('ambient dependency')")
        manifest = self.package(env={"NODE_OPTIONS": "--require=" + str(self.repo / "local-preload.cjs"),
                                     "NODE_PATH": str(self.repo / "console/node_modules")})
        self.assertEqual(manifest["sourceRevision"], self.second)
        with zipfile.ZipFile(self.out / ARCHIVE) as archive:
            self.assertEqual(archive.read("enterprise/README.md"), b"second")
            self.assertEqual(archive.read("enterprise/console/dist/index.html"), b"fresh second")
            names = archive.namelist()
            self.assertFalse(any(name in names for name in [
                "enterprise/" + path for path in (".worktrees/foreign/token", "nested/.env.local", ".aws/config", ".npmrc",
                "nested/.secrets/key", "nested/dump.sql.dump", "old.log", "console/node_modules/poison",
                "console/untracked", "untracked.txt", "other-checkout/.git")]))
            self.assertEqual(manifest["files"], len(names))
        self.assertIs(manifest["acceptanceComplete"], False)
        archive_bytes = (self.out / ARCHIVE).read_bytes()
        self.assertEqual(manifest["sha256"], hashlib.sha256(archive_bytes).hexdigest())
        self.assertEqual(self.package(), manifest)
        self.assertEqual((self.out / ARCHIVE).read_bytes(), archive_bytes)
        self.assertEqual((self.repo / "console/dist/index.html").read_text(), "local stale")

    def test_positional_revision_and_independently_committed_receipt(self):
        proof = json.loads((ROOT / RECEIPT).read_text())
        proof["testedSourceRevision"] = self.first[:9]
        self.write(RECEIPT, json.dumps(proof))
        self.write_evidence_files()
        evidence = self.commit("test results")
        manifest = self.package(self.first, "--evidence-revision", evidence)
        self.assertEqual(manifest["sourceRevision"], self.first)
        self.assertIs(manifest["acceptanceComplete"], True)
        with zipfile.ZipFile(self.out / ARCHIVE) as archive:
            self.assertEqual(archive.read("enterprise/README.md"), b"first")
            self.assertEqual(archive.read("enterprise/console/dist/index.html"), b"fresh first")
            # Both receipts and the similarly named tracked source keep their exact bytes.
            info = manifest["acceptanceEvidence"]
            self.assertEqual(info["revision"], evidence)
            self.assertEqual(info["path"], RECEIPT)
            self.assertIn("archivePath", info)
            included = archive.read(info["archivePath"])
            self.assertEqual(included, (self.repo / RECEIPT).read_bytes())
            self.assertEqual(info["sha256"], hashlib.sha256(included).hexdigest())
            self.assertEqual(json.loads(archive.read("enterprise/" + RECEIPT))["testedSourceRevision"], "badcafe")
            self.assertEqual(archive.read("enterprise/package-evidence/core-loop/verification.json"), b"source namespace")
            self.assertEqual(archive.read("enterprise/" + REFERENCE_PATHS[2]), b"historical review still pending")
            for path in REFERENCE_PATHS:
                expected = (self.repo / path).read_bytes()
                self.assertEqual(archive.read("package-evidence/" + path), expected)
                self.assertEqual(info["references"][path]["sha256"], hashlib.sha256(expected).hexdigest())

        self.assertIs(self.package(self.second, "--evidence-revision", evidence)["acceptanceComplete"], False)
        for invalid in ({"complete": 1}, {"testsComplete": False},
                        {"checks": {}}, {"checks": {"automatedRegressionTotal": {"passed": 1, "failed": 1, "skipped": 0}}},
                        {"temporaryFixtureCleanup": {"complete": False}},
                        {"testedSourceRevision": "HEAD"}, {"testedSourceRevision": self.first[:2]}):
            with self.subTest(invalid=invalid):
                self.write(RECEIPT, json.dumps({**proof, **invalid}))
                bad = self.commit("invalid receipt")
                self.assertIs(self.package(self.first, "--evidence-revision", bad)["acceptanceComplete"], False)

    def test_acceptance_requires_all_regression_counts_and_linked_evidence(self):
        proof = json.loads((ROOT / RECEIPT).read_text())
        proof["testedSourceRevision"] = self.first
        self.write(RECEIPT, json.dumps(proof))
        missing_evidence = self.commit("receipt without evidence")
        self.assertIs(self.package(self.first, "--evidence-revision", missing_evidence)["acceptanceComplete"], False)

        self.write_evidence_files()
        for checks in (
            {"automatedRegressionTotal": {"passed": 1, "failed": 0, "skipped": 0}},
            {**proof["checks"], "automatedRegressionTotal": {"passed": 1, "failed": 0, "skipped": 0}},
            {**proof["checks"], "browser": "docs/evidence/m4-console"},
        ):
            with self.subTest(checks=checks):
                self.write(RECEIPT, json.dumps({**proof, "checks": checks}))
                evidence = self.commit("incomplete regression")
                self.assertIs(self.package(self.first, "--evidence-revision", evidence)["acceptanceComplete"], False)

    def test_missing_or_malformed_receipt_still_packages_unaccepted(self):
        (self.repo / RECEIPT).unlink()
        self.commit("missing receipt")
        self.assertIs(self.package()["acceptanceComplete"], False)
        self.write(RECEIPT, "{")
        self.commit("unreadable receipt")
        self.assertIs(self.package()["acceptanceComplete"], False)

    def test_frozen_install_or_build_failure_preserves_previous_pair(self):
        self.package()
        previous = {path: path.read_bytes() for path in self.out.iterdir() if path.is_file()}
        self.write("console/package.json", json.dumps({**self.package_data, "dependencies": {"missing": "1.0.0"}}))
        mismatch = self.commit("outdated lockfile")
        self.package(mismatch, success=False)
        self.write("console/package.json", json.dumps(self.package_data))
        self.write("console/build.mjs", "process.exit(23)")
        failure = self.commit("broken build")
        self.package(failure, success=False)
        self.assertEqual({path: path.read_bytes() for path in self.out.iterdir() if path.is_file()}, previous)

    def test_manifest_publication_failure_restores_previous_archive_and_manifest(self):
        before = self.package(self.first)
        previous_bytes = {p.name: p.read_bytes() for p in self.out.iterdir()}
        injection = "fault-injection/sitecustomize.py"
        self.write(injection, """import os
from pathlib import Path
replace = os.replace
def fail_manifest(source, target):
    if Path(source).name == 'manifest.json' and Path(target).name == 'manifest.json':
        raise OSError('fixture manifest publication failure')
    return replace(source, target)
os.replace = fail_manifest
""")
        result = self.package(self.second, success=False,
                              env={"PYTHONPATH": str((self.repo / injection).parent)})
        self.assertIn("fixture manifest publication failure", result.stderr)
        self.assertEqual({p.name: p.read_bytes() for p in self.out.iterdir()}, previous_bytes)
        self.assertEqual(before["sourceRevision"], self.first)
        self.assertEqual(self.package(self.second)["sourceRevision"], self.second)


if __name__ == "__main__":
    unittest.main()
