"""Exercise the public observability packager and verifier in a disposable Git history."""
import copy
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import re
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
THREE_SOURCE_PROJECT = "webapi-enterprise-e2e-observability-00000000-0000-4000-8000-000000000001"
FAULT_PROJECT = "webapi-enterprise-e2e-observability-00000000-0000-4000-8000-000000000002"
BROWSER_PROJECT = "webapi-enterprise-e2e-observability-00000000-0000-4000-8000-000000000003"


def passed_checks(*names):
    return [{"name": name, "status": "passed"} for name in names]


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
        self.write(".gitignore", "/console/node_modules/\n/deliverables/\n/scripts/observability_delivery.py\n__pycache__/\n")
        # CI runs packaging tests before installing the console. Only the build tools are fixtures.
        self.write("console/node_modules/typescript/bin/tsc", "process.exit(0)")
        self.write("console/node_modules/vite/bin/vite.js",
                   "const fs=require('node:fs'); fs.mkdirSync('dist'); "
                   "fs.copyFileSync('src/coverage.json','dist/index.html');")
        self.proof = self.seed_legacy_evidence()
        self.write(RECEIPT, {"complete": False})
        self.tested = self.commit("product and test inputs")

        fault = self.read(EVIDENCE + "fault-run.json")
        fault["sourceRevision"] = self.tested
        self.write(EVIDENCE + "fault-run.json", fault)
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
        # Pin this disposable history in the isolated verifier copy. The product tree
        # never includes this copy, just as a newer verifier can read the original archive.
        verifier_path = "scripts/observability_delivery.py"
        verifier = (self.repo / verifier_path).read_text()
        for constant, revision in (("LEGACY_DRAFT_REVISION", self.provisional), ("LEGACY_SEAL_REVISION", self.seal)):
            verifier = re.sub(rf'^{constant} = "[0-9a-f]+"$', f'{constant} = "{revision}"', verifier, flags=re.MULTILINE)
        self.write(verifier_path, verifier)

    def seed_legacy_evidence(self):
        # The synthetic history is independent of the current product's live evidence.
        cleanup = {"exitCode": 0, "secretFilesRemaining": 0, "containersRemaining": 0, "volumesRemaining": 0}
        loop = {"complete": True, "project": "fixture-loop"}
        self.write(EVIDENCE + "loop.json", {**loop, "ordinaryRequestTraceComplete": True, "counterDelta": 8,
                                          "logCount": 8, "spanCount": 16, "ordinaryRequestSpanCount": 2})
        self.write(EVIDENCE + "rule-loop.json", {**loop, "checks": passed_checks(
            "Actual60SecondCompleteCoverageAndEstimatedRates",
            "ThreeActualMetricRulesTriggerAckSilenceRecoverAndAudit", "ActualYarpHealthyGaugeRecovers")})
        self.write(EVIDENCE + "boundaries.json", {**loop, "checks": passed_checks(
            "ActualSameTraceparentTwoEnvironmentsOnlyReturnCurrentEnvironmentSpans",
            "RealLokiSingleNanosecondProviderLimitExplicitlyTruncated")})
        faults = {"complete": True, "project": "fixture-faults", "checks": passed_checks(
            "OldARequestAcrossBSwitchKeepsActualADestinationAndTelemetry",
            "ActualApprovedRollbackSurvivesObservationStack", "lokiStopAndRecoveryKeepProxyAndApprovedPublishing",
            "tempoStopAndRecoveryKeepProxyAndApprovedPublishing", "prometheusStopAndRecoveryKeepProxyAndApprovedPublishing",
            "CollectorOutageQueueFullProxySuccessAndRealMiddleGap", "SingleNodeCollectionExpiryAndActualProcessRecovery",
            "ActualWorkerKillPgLeaseExpiresSecondWorkerTakesOver", "ActualGatewayLkgRestartWithControlPlaneRedisOffline")}
        self.write(EVIDENCE + "faults.json", faults)
        self.write(EVIDENCE + "fault-run.json", {**faults, "command": "./scripts/check-observability.sh faults",
                                               "exitCode": 0, "cleanup": cleanup})
        self.write(EVIDENCE + "browser-qa.json", {
            "complete": True, "project": "fixture-browser", "normalConsoleErrors": [], "checks": ["fixture"],
            "pages": [{"page": page, "horizontalOverflow": False} for page in range(28, 34)],
            "screenshots": ["fixture.png"],
            "cleanup": {"remainingContainers": 0, "remainingVolumes": 0, "remainingSyntheticSecretFiles": 0,
                        "complete": True},
        })
        self.write(EVIDENCE + "ui/fixture.png", "synthetic screenshot bytes")
        repairs = {"complete": True, "project": "fixture-repairs", "cleanup": cleanup}
        self.write(EVIDENCE + "final-loss-live.json", {**repairs, "checks": [
            {"sourceState": "Partial", "reason": reason} for reason in
            ("collector_logs_collection_gap", "collector_traces_collection_gap", "known_logs_collection_gap")]})
        self.write(EVIDENCE + "final-permission-browser.json", {
            **repairs, "red": {"resultsRetained": True}, "green": {"kpiCards": 0, "tables": 0},
        })
        self.write(EVIDENCE + "final-review.md", "Synthetic independent review.")
        self.write(EVIDENCE + "execution-ledger.md", "Synthetic execution ledger.")
        return {
            **loop, "classification": "real-isolated-gateway-three-source", "mode": "e2e",
            "checks": [{"name": name, "status": "passed", "evidence": name + ".json"}
                       for name in ("loop", "rule-loop", "boundaries")],
            "cleanup": cleanup, "criticalAcceptanceComplete": True, "exitCode": 0,
            "command": "./scripts/check-observability.sh e2e",
            "baselineFaultEvidence": {"file": "fault-run.json", "checks": len(faults["checks"]), "complete": True},
            "browserEvidence": "browser-qa.json",
            "finalReview": {
                "independentReviews": 1, "repairValidationComplete": True, "typescriptAndProductionBuild": True,
                "fullSuite": {"domain": 1, "integration": 1, "gateway": 1, "frontend": 1, "skipped": 0},
                "realLossProof": "final-loss-live.json", "permissionProof": "final-permission-browser.json", "pending": [],
            },
            "archiveGate": {"verified": True, "provisionalSha256": "0" * 64, "files": 1},
        }

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
        draft["cleanup"]["networksRemaining"] = 0
        draft["project"] = THREE_SOURCE_PROJECT
        fault = self.read(EVIDENCE + "fault-run.json")
        fault["project"] = FAULT_PROJECT
        fault["cleanup"]["networksRemaining"] = 0
        self.write(EVIDENCE + "fault-run.json", fault)
        output = self.read(EVIDENCE + "faults.json")
        output.update(project=fault["project"], sourceRevision=self.tested)
        self.write(FAULT_OUTPUT, output)
        for name in ("loop.json", "rule-loop.json", "boundaries.json"):
            path = EVIDENCE + name
            record = self.read(path)
            self.write(path, {**record, "sourceRevision": self.tested, "project": THREE_SOURCE_PROJECT})
        for name, key in (("browser-qa.json", "remainingNetworks"), ("final-loss-live.json", "networksRemaining"),
                          ("final-permission-browser.json", "networksRemaining")):
            path = EVIDENCE + name
            record = self.read(path)
            record["cleanup"][key] = 0
            self.write(path, {**record, "sourceRevision": self.tested, "project": BROWSER_PROJECT})
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

    def test_unrecognized_legacy_draft_cannot_bypass_fresh_requirements(self):
        self.git("switch", "--detach", "-q", self.provisional)
        draft = self.read(RECEIPT)
        draft["startedAt"] = "2026-11-24T00:00:00Z"
        self.write(RECEIPT, draft)
        revision = self.commit("prepare another legacy-style receipt")
        self.package(self.tested, "--evidence-revision", revision, success=False)

    def test_unrecognized_legacy_seal_cannot_bypass_fresh_requirements(self):
        self.git("switch", "--detach", "-q", self.provisional)
        draft = self.read(RECEIPT)
        draft["startedAt"] = "2026-11-24T00:00:00Z"
        self.write(RECEIPT, draft)
        revision = self.commit("prepare another legacy-style receipt")
        final = copy.deepcopy(self.proof)
        final["startedAt"] = draft["startedAt"]
        final["archiveGate"]["provisionalSourceRevision"] = revision
        self.write(RECEIPT, final)
        seal = self.commit("seal another legacy-style receipt")
        manifest = self.package(self.tested, "--evidence-revision", seal)
        self.assertIs(manifest["acceptanceComplete"], False)
        self.verify(success=False)

    def test_fresh_provisional_requires_exact_zero_networks_in_each_report(self):
        good = self.prepare_fresh_provisional()
        for file, key in [(RECEIPT, "networksRemaining"), (EVIDENCE + "fault-run.json", "networksRemaining"),
                          (EVIDENCE + "browser-qa.json", "remainingNetworks"),
                          (EVIDENCE + "final-loss-live.json", "networksRemaining"),
                          (EVIDENCE + "final-permission-browser.json", "networksRemaining")]:
            for value in (None, 1, False):
                with self.subTest(file=file, value=value):
                    self.git("switch", "--detach", "-q", good)
                    record = self.read(file)
                    if value is None:
                        del record["cleanup"][key]
                    else:
                        record["cleanup"][key] = value
                    self.write(file, record)
                    draft = self.commit("record unconfirmed network cleanup")
                    self.package(self.tested, "--evidence-revision", draft, success=False)

    def test_fresh_provisional_requires_matching_full_source_revision_in_raw_attachments(self):
        good = self.prepare_fresh_provisional()
        for path in (EVIDENCE + "loop.json", EVIDENCE + "rule-loop.json", EVIDENCE + "boundaries.json", FAULT_OUTPUT):
            for value in (None, self.tested[:9], self.provisional):
                with self.subTest(file=path, value=value):
                    self.git("switch", "--detach", "-q", good)
                    report = self.read(path)
                    if value is None:
                        del report["sourceRevision"]
                    else:
                        report["sourceRevision"] = value
                    self.write(path, report)
                    draft = self.commit("record a raw attachment without the exact tested revision")
                    self.package(self.tested, "--evidence-revision", draft, success=False)

    def test_fresh_provisional_requires_distinct_real_project_names_and_coherent_browser_records(self):
        good = self.prepare_fresh_provisional()
        three_source = (RECEIPT, EVIDENCE + "loop.json", EVIDENCE + "rule-loop.json", EVIDENCE + "boundaries.json")
        faults = (EVIDENCE + "fault-run.json", FAULT_OUTPUT)
        repairs = (EVIDENCE + "final-loss-live.json", EVIDENCE + "final-permission-browser.json")
        browser = (EVIDENCE + "browser-qa.json", *repairs)
        for paths, project in (
            (three_source, "project-three-source"),
            (faults, "project-faults"),
            (browser, "project-browser"),
            ((browser[0],), None),
            (three_source, FAULT_PROJECT),
            (three_source, BROWSER_PROJECT),
            (faults, BROWSER_PROJECT),
            (repairs, FAULT_PROJECT),
        ):
            with self.subTest(paths=paths, project=project):
                self.git("switch", "--detach", "-q", good)
                for path in paths:
                    report = self.read(path)
                    if project is None:
                        del report["project"]
                    else:
                        report["project"] = project
                    self.write(path, report)
                draft = self.commit("record inconsistent or invalid project attribution")
                self.package(self.tested, "--evidence-revision", draft, success=False)

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

    def test_sealed_fresh_evidence_can_be_packaged_as_head_until_a_product_input_changes(self):
        draft = self.prepare_fresh_provisional()
        provisional = self.package(self.tested, "--evidence-revision", draft)
        self.verify("--provisional")
        self.write(RECEIPT, self.prepare_fresh_seal(draft, provisional))
        sealed = self.commit("seal the verified package")
        accepted = self.package("HEAD")
        self.assertIs(accepted["acceptanceComplete"], True)
        self.assertEqual(accepted["sourceRevision"], sealed)
        self.assertEqual(accepted["acceptanceEvidence"]["testedSourceRevision"], self.tested)
        self.assertEqual(accepted["acceptanceEvidence"]["archivePath"], "enterprise/" + RECEIPT)
        self.verify()

        self.write(COLLECTOR, "changed product after sealing")
        self.commit("change product inputs")
        stale = self.package("HEAD")
        self.assertIs(stale["acceptanceComplete"], False)
        self.assertEqual(stale["acceptanceEvidence"]["status"], "source-inputs-changed")
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
