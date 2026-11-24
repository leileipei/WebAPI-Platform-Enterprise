"""Exercise the public observability packager and verifier in a disposable Git history."""
import copy
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile


ROOT = Path(__file__).resolve().parents[2]
EVIDENCE = "docs/evidence/observability/"
RECEIPT = EVIDENCE + "verification.json"
FAULT_OUTPUT = EVIDENCE + "faults-run-output.json"
MANIFEST = "deliverables/manifest-observability.json"
COLLECTOR = "src/WebApi.Infrastructure/Observability/CollectorSignalCoverage.cs"


class ObservabilityPackageTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.repo = Path(tmp.name)
        self.git("init", "-q")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("config", "user.name", "Package fixture")
        for name in ["package.sh", "package-observability.py", "verify-observability-delivery.py",
                     "observability_delivery.py"]:
            script = ROOT / "scripts" / name
            if script.is_file():
                self.write("scripts/" + name, script.read_bytes())
        for name in ["README.md", "console/src/coverage.json", "docs/console-coverage.md",
                     "docs/observability-data-dictionary.md", "docs/deployment/observability-runbook.md", COLLECTOR,
                     "console/package.json", "console/pnpm-lock.yaml"]:
            self.write(name, "fixture")
        self.write(".gitignore", "/console/node_modules/\n/deliverables/\n__pycache__/\n")
        # CI runs packaging tests before installing the console. Only the build tools are fixtures.
        self.write("console/node_modules/typescript/bin/tsc", "process.exit(0)")
        self.write("console/node_modules/vite/bin/vite.js",
                   "const fs=require('node:fs'); fs.mkdirSync('dist'); "
                   "fs.copyFileSync('src/coverage.json','dist/index.html');")
        shutil.copytree(ROOT / EVIDENCE, self.repo / EVIDENCE)
        self.write(RECEIPT, {"complete": False})
        self.tested = self.commit("product and test inputs")

        fault = self.read(EVIDENCE + "fault-run.json")
        fault["sourceRevision"] = self.tested
        self.write(EVIDENCE + "fault-run.json", fault)
        self.proof = json.loads((ROOT / RECEIPT).read_bytes())
        self.proof["sourceRevision"] = self.tested
        self.proof["baselineFaultEvidence"]["sourceRevision"] = self.tested[:9]
        self.proof["finalReview"]["reviewedRevision"] = self.tested
        provisional = copy.deepcopy(self.proof)
        del provisional["archiveGate"]
        provisional["complete"] = False
        provisional["finalReview"]["pending"] = ["immutable archive verification"]
        self.write(RECEIPT, provisional)
        self.provisional = self.commit("record passing run before archive verification")
        self.proof["archiveGate"].update(productSourceRevision=self.tested,
                                         provisionalSourceRevision=self.provisional)
        self.write(RECEIPT, self.proof)
        self.seal = self.commit("seal archive gate")

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.repo, text=True).strip()

    def commit(self, message):
        self.git("add", "-A")
        self.git("commit", "-qm", message)
        return self.git("rev-parse", "HEAD")

    def write(self, name, content):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, dict):
            content = json.dumps(content)
        path.write_bytes(content if isinstance(content, bytes) else content.encode())

    def read(self, name):
        return json.loads((self.repo / name).read_bytes())

    def cli(self, args, success):
        result = subprocess.run(args, cwd=self.repo, capture_output=True, text=True,
                                env={**os.environ, "WEBAPI_NODE": shutil.which("node"),
                                     "PYTHONDONTWRITEBYTECODE": "1"})
        if success:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

    def package(self, revision, *args, success=True):
        self.cli(["bash", "scripts/package.sh", "observability", revision, *args], success)
        if success:
            return self.read(MANIFEST)

    def verify(self, *args, success=True):
        self.cli([sys.executable, "scripts/verify-observability-delivery.py", *args], success)

    def prepare_fresh_provisional(self):
        self.git("switch", "--detach", "-q", self.provisional)
        draft = self.read(RECEIPT)
        draft["baselineFaultEvidence"].update(sourceRevision=self.tested, fixtureFile=Path(FAULT_OUTPUT).name)
        fault = self.read(EVIDENCE + "fault-run.json")
        fault["project"] = "recorded-current-fault-project"
        self.write(EVIDENCE + "fault-run.json", fault)
        output = self.read(EVIDENCE + "faults.json")
        output["project"] = fault["project"]
        self.write(FAULT_OUTPUT, output)
        for name in ("browser-qa.json", "final-loss-live.json", "final-permission-browser.json"):
            path = EVIDENCE + name
            record = self.read(path)
            self.write(path, {**record, "sourceRevision": self.tested})
        self.write(RECEIPT, draft)
        return self.commit("record new run")

    def prepare_fresh_seal(self, draft, manifest):
        receipt = self.read(RECEIPT)
        receipt["complete"] = True
        receipt["finalReview"]["pending"] = []
        receipt["archiveGate"] = {
            "verified": True,
            "productSourceRevision": self.tested,
            "provisionalSourceRevision": self.tested,
            "provisionalEvidenceRevision": draft,
            "provisionalSha256": manifest["sha256"],
            "files": manifest["files"],
        }
        return receipt

    def test_previous_manifest_keeps_a_complete_archive_after_another_publication(self):
        first = self.package(self.seal)
        old = self.repo / "deliverables" / first["file"]
        self.write(COLLECTOR, "next release")
        second = self.package(self.commit("next product"))
        self.assertNotEqual(first["file"], second["file"])
        self.assertEqual(hashlib.sha256(old.read_bytes()).hexdigest(), first["sha256"])
        with zipfile.ZipFile(old) as archive:
            self.assertIsNone(archive.testzip())
        again = self.package(self.seal)
        self.assertEqual(again["file"], first["file"])
        self.verify()

    def test_production_mode_does_not_depend_on_packagers_environment(self):
        self.write("console/node_modules/vite/bin/vite.js",
                   "const fs=require('node:fs'); fs.mkdirSync('dist'); "
                   "fs.writeFileSync('dist/index.html',process.env.NODE_ENV || 'unset');")
        with patch.dict(os.environ, {"NODE_ENV": "development"}):
            packed = self.package(self.seal)
            self.verify()
        with zipfile.ZipFile(self.repo / "deliverables" / packed["file"]) as archive:
            self.assertEqual(archive.read("enterprise/console/dist/index.html"), b"production")

    def test_concurrent_publications_and_failed_build_keep_complete_archives(self):
        first = self.package(self.seal)
        self.write(COLLECTOR, "concurrent product")
        second = self.commit("second input")
        with ThreadPoolExecutor(max_workers=2) as executor:
            results = [executor.submit(self.cli, ["bash", "scripts/package.sh", "observability", ref], True)
                       for ref in (self.seal, second)]
            for result in results:
                result.result()
        published = (self.repo / MANIFEST).read_bytes()
        head = json.loads(published)
        archives = list((self.repo / "deliverables").glob("*.zip"))
        self.assertEqual(len(archives), 2)
        for archive in archives:
            self.assertIn(hashlib.sha256(archive.read_bytes()).hexdigest(), archive.name)
            with zipfile.ZipFile(archive) as contents:
                self.assertIsNone(contents.testzip())
        self.assertTrue((self.repo / "deliverables" / first["file"]).exists())
        self.assertTrue((self.repo / "deliverables" / head["file"]).exists())
        self.write("console/node_modules/vite/bin/vite.js", "process.exit(3)")
        self.package(self.seal, success=False)
        self.assertEqual((self.repo / MANIFEST).read_bytes(), published)

    def test_verifier_independently_compares_static_bytes_with_the_production_build(self):
        manifest = self.package(self.seal)
        name = "console/dist/index.html"
        out = self.repo / "deliverables"
        path = out / manifest["file"]
        changed = out / "different-console.zip"
        with zipfile.ZipFile(path) as original, zipfile.ZipFile(changed, "w") as modified:
            for entry in original.infolist():
                data = b"different console" if entry.filename == "enterprise/" + name else original.read(entry)
                modified.writestr(entry, data)
        digest = hashlib.sha256(changed.read_bytes()).hexdigest()
        manifest["file"] = manifest["file"].replace(manifest["sha256"], digest)
        manifest["sha256"] = digest
        manifest["staticFiles"][name] = hashlib.sha256(b"different console").hexdigest()
        changed.rename(out / manifest["file"])
        self.write(MANIFEST, manifest)
        self.verify(success=False)

    def test_matching_source_can_use_separately_sealed_immutable_evidence(self):
        manifest = self.package(self.tested, "--evidence-revision", self.seal)
        self.assertIs(manifest["acceptanceComplete"], True)
        self.assertEqual(manifest["sourceRevision"], self.tested)
        evidence = manifest["acceptanceEvidence"]
        self.assertEqual(evidence["revision"], self.seal)
        self.assertEqual(evidence["testedSourceRevision"], self.tested)
        with zipfile.ZipFile(self.repo / "deliverables" / manifest["file"]) as archive:
            self.assertEqual(json.loads(archive.read("enterprise/" + RECEIPT)), {"complete": False})
            included = archive.read(evidence["archivePath"])
            self.assertEqual(included, (self.repo / RECEIPT).read_bytes())
            self.assertEqual(hashlib.sha256(included).hexdigest(), evidence["sha256"])
            ref = evidence["references"][EVIDENCE + "loop.json"]
            self.assertEqual(archive.read(ref["archivePath"]), (self.repo / (EVIDENCE + "loop.json")).read_bytes())
        self.verify()

    def test_evidence_only_revision_keeps_the_tested_product_eligible(self):
        self.write(EVIDENCE + "execution-ledger.md", "record final verification")
        source = self.commit("record delivery")
        manifest = self.package(source)
        self.assertIs(manifest["acceptanceComplete"], True)
        self.assertEqual(manifest["sourceRevision"], source)
        evidence = manifest["acceptanceEvidence"]
        self.assertEqual(evidence["testedSourceRevision"], self.tested)
        self.assertEqual(evidence.get("receiptRevision"), self.seal)
        self.assertEqual(evidence["revision"], source)
        self.verify()

    def test_changed_product_or_test_input_cannot_borrow_the_old_receipt(self):
        # Each change occurs alone relative to the same sealed product.
        for path in [COLLECTOR, "src/WebApi.ControlPlane/ControlPlaneApp.cs", "console/src/App.tsx",
                     "tests/WebApi.EndToEnd.Tests/ObservabilityLoopTests.cs", "scripts/prepare-e2e.mjs",
                     "deploy/compose.e2e.yml", "Directory.Build.targets", EVIDENCE + "faults.json"]:
            with self.subTest(path=path):
                self.git("switch", "--detach", "-q", self.seal)
                self.write(path, "changed input")
                source = self.commit("change one input")
                manifest = self.package(source)
                self.assertIs(manifest["acceptanceComplete"], False)
                evidence = manifest["acceptanceEvidence"]
                self.assertEqual(evidence["testedSourceRevision"], self.tested)
                with zipfile.ZipFile(self.repo / "deliverables" / manifest["file"]) as archive:
                    self.assertEqual(archive.read("enterprise/" + path), b"changed input")
                    self.assertEqual(archive.read(evidence["archivePath"]), (self.repo / RECEIPT).read_bytes())
                self.verify(success=False)
                manifest["acceptanceComplete"] = True
                self.write(MANIFEST, manifest)
                self.verify(success=False)

    def test_verifier_recomputes_qualification_even_for_old_manifests(self):
        self.write(COLLECTOR, "new product")
        source = self.commit("new code")
        manifest = self.package(source)
        manifest.pop("acceptanceEvidence", None)
        manifest.pop("schemaVersion", None)
        manifest["acceptanceComplete"] = True
        self.write(MANIFEST, manifest)
        self.verify(success=False)

    def test_verifier_checks_provenance_against_the_committed_receipt(self):
        manifest = self.package(self.seal)
        manifest["acceptanceEvidence"] = {"testedSourceRevision": self.seal, "revision": self.seal}
        self.write(MANIFEST, manifest)
        self.verify(success=False)

    def test_verifier_keeps_the_exclusions_for_declared_static_outputs(self):
        manifest = self.package(self.seal)
        archive_path = self.repo / "deliverables" / manifest["file"]
        name, data = "console/dist/node_modules/example", b"excluded fixture"
        with zipfile.ZipFile(archive_path, "a") as archive:
            archive.writestr("enterprise/" + name, data)
        manifest["files"] += 1
        manifest["sha256"] = hashlib.sha256(archive_path.read_bytes()).hexdigest()
        manifest["staticFiles"][name] = hashlib.sha256(data).hexdigest()
        self.write(MANIFEST, manifest)
        self.verify(success=False)

    def test_an_archive_only_pending_gate_is_provisional(self):
        self.assertIs(self.package(self.provisional)["acceptanceComplete"], False)
        self.verify("--provisional")
        self.verify(success=False)
        incomplete = copy.deepcopy(self.proof)
        incomplete["complete"] = False
        incomplete["finalReview"]["pending"] = ["real three-source loop"]
        self.write(RECEIPT, incomplete)
        self.package(self.commit("E2E still pending"), success=False)
        self.proof["cleanup"]["secretFilesRemaining"] = 1
        self.write(RECEIPT, self.proof)
        self.package(self.commit("unclean fixture"), success=False)

    def test_separate_fresh_fault_output_can_be_sealed_after_independent_provisional_verification(self):
        draft = self.prepare_fresh_provisional()
        provisional = self.package(self.tested, "--evidence-revision", draft)
        self.assertIs(provisional["acceptanceComplete"], False)
        self.verify(success=False)
        self.verify("--provisional")
        self.write(RECEIPT, self.prepare_fresh_seal(draft, provisional))
        seal = self.commit("seal the verified package")
        manifest = self.package(self.tested, "--evidence-revision", seal)
        self.assertIs(manifest["acceptanceComplete"], True)
        self.assertEqual(manifest["acceptanceEvidence"]["references"][FAULT_OUTPUT]["archivePath"],
                         "package-evidence/" + FAULT_OUTPUT)
        self.verify()
        self.verify("--provisional", success=False)
        out = self.repo / "deliverables"
        (out / provisional["file"]).write_bytes((out / manifest["file"]).read_bytes())
        self.verify(success=False)
        (out / provisional["file"]).unlink()
        self.verify(success=False)

    def test_fresh_provisional_rejects_unbound_or_arbitrary_fault_evidence(self):
        good = self.prepare_fresh_provisional()
        for path, change in [
            (RECEIPT, lambda p: p["baselineFaultEvidence"].update(fixtureFile="faults.json")),
            (RECEIPT, lambda p: p["baselineFaultEvidence"].update(fixtureFile="../faults-run-output.json")),
            (RECEIPT, lambda p: p["baselineFaultEvidence"].update(sourceRevision=self.tested[:9])),
            (RECEIPT, lambda p: p["finalReview"].update(reviewedRevision=self.provisional)),
            (EVIDENCE + "browser-qa.json", lambda p: p.update(sourceRevision=self.provisional)),
            (EVIDENCE + "fault-run.json", lambda p: p.update(sourceRevision=self.provisional)),
            (EVIDENCE + "loop.json", lambda p: p.update(counterDelta=7)),
        ]:
            with self.subTest(file=path, change=change.__code__.co_firstlineno):
                self.git("switch", "--detach", "-q", good)
                record = self.read(path)
                change(record)
                self.write(path, record)
                draft = self.commit("contradict the new run")
                self.package(self.tested, "--evidence-revision", draft, success=False)

        self.git("switch", "--detach", "-q", good)
        self.write(EVIDENCE + "faults.json", "changed tracked fixture")
        draft = self.commit("change the original test input alongside the record")
        self.package(self.tested, "--evidence-revision", draft, success=False)

    def test_final_gate_cannot_edit_run_metadata_after_provisional_verification(self):
        draft = self.prepare_fresh_provisional()
        manifest = self.package(self.tested, "--evidence-revision", draft)
        seal = self.prepare_fresh_seal(draft, manifest)
        seal["architecture"] = "unobserved architecture"
        self.write(RECEIPT, seal)
        sealed = self.commit("change the already verified receipt")
        final = self.package(self.tested, "--evidence-revision", sealed)
        self.assertIs(final["acceptanceComplete"], False)
        self.verify(success=False)

    def test_final_gate_rejects_wrong_provisional_archive_digest_and_count(self):
        draft = self.prepare_fresh_provisional()
        manifest = self.package(self.tested, "--evidence-revision", draft)
        for fields in ({"files": manifest["files"] + 1}, {"provisionalSha256": "0" * 64}):
            with self.subTest(fields=fields):
                self.git("switch", "--detach", "-q", draft)
                receipt = self.prepare_fresh_seal(draft, manifest)
                receipt["archiveGate"].update(fields)
                self.write(RECEIPT, receipt)
                seal = self.commit("claim a different archive")
                self.package(self.tested, "--evidence-revision", seal, success=False)

    def test_incomplete_or_contradictory_sealed_evidence_cannot_qualify(self):
        for name, update in [
            (RECEIPT, lambda p: p.update(complete=1)),
            (RECEIPT, lambda p: p.update(sourceRevision="HEAD")),
            (RECEIPT, lambda p: p["checks"].pop()),
            (RECEIPT, lambda p: p["cleanup"].update(containersRemaining=1)),
            (RECEIPT, lambda p: p["finalReview"]["fullSuite"].update(skipped=1)),
            (RECEIPT, lambda p: p["archiveGate"].update(productSourceRevision=self.seal)),
            (EVIDENCE + "loop.json", lambda p: p.update(project="another run")),
            (EVIDENCE + "rule-loop.json", lambda p: p["checks"][0].update(status="failed")),
            (EVIDENCE + "fault-run.json", lambda p: p["checks"].pop()),
            (EVIDENCE + "browser-qa.json", lambda p: p.update(complete=False)),
            (EVIDENCE + "final-loss-live.json", lambda p: p.update(complete=False)),
            (EVIDENCE + "final-permission-browser.json", lambda p: p.update(complete=False)),
        ]:
            with self.subTest(name=name, update=update.__code__.co_firstlineno):
                self.git("switch", "--detach", "-q", self.seal)
                data = self.read(name)
                update(data)
                self.write(name, data)
                manifest = self.package(self.commit("contradictory proof"))
                self.assertIs(manifest["acceptanceComplete"], False)
                self.verify(success=False)

    def test_missing_attachment_is_not_a_pass(self):
        (self.repo / (EVIDENCE + "boundaries.json")).unlink()
        manifest = self.package(self.commit("lost attached evidence"))
        self.assertIs(manifest["acceptanceComplete"], False)
        self.verify(success=False)

    def test_consistent_provisional_and_final_receipts_still_require_the_observability_checks(self):
        for name, change in [
            ("loop.json", lambda p: p.update(counterDelta=1)),
            ("rule-loop.json", lambda p: p["checks"][0].update(name="unrelated green check")),
            ("browser-qa.json", lambda p: p.update(pages=[])),
            ("final-loss-live.json", lambda p: p["checks"][0].update(sourceState="Complete")),
            ("final-permission-browser.json", lambda p: p["green"].update(tables=1)),
        ]:
            with self.subTest(name=name):
                self.git("switch", "--detach", "-q", self.provisional)
                attachment = self.read(EVIDENCE + name)
                change(attachment)
                self.write(EVIDENCE + name, attachment)
                provisional = self.commit("record the incomplete run")
                sealed = copy.deepcopy(self.proof)
                sealed["archiveGate"]["provisionalSourceRevision"] = provisional
                self.write(RECEIPT, sealed)
                manifest = self.package(self.commit("seal the same incomplete run"))
                self.assertIs(manifest["acceptanceComplete"], False)
                self.verify(success=False)


if __name__ == "__main__":
    unittest.main()
