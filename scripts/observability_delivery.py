"""Immutable inputs and receipt checks shared by observability packaging and verification."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import tempfile
import re
import subprocess
import zipfile


EVIDENCE = "docs/evidence/observability/"
RECEIPT = EVIDENCE + "verification.json"
BLOCKED = {".git", ".superpowers", ".runtime", ".secrets", "node_modules", "bin", "obj", "TestResults", "deliverables"}
CHECK_FILES = {"loop": "loop.json", "rule-loop": "rule-loop.json", "boundaries": "boundaries.json"}
FAULT_FIXTURE = "faults.json"
FAULT_OUTPUT = "faults-run-output.json"
NETWORK_COUNTS = {
    "fault-run.json": "networksRemaining",
    "browser-qa.json": "remainingNetworks",
    "final-loss-live.json": "networksRemaining",
    "final-permission-browser.json": "networksRemaining",
}
OTHER_FILES = (*NETWORK_COUNTS, "final-review.md")
LEGACY_DRAFT_REVISION = "09746afd0acf497ce024cac88914d6a5185895fd"
LEGACY_SEAL_REVISION = "ac5a115495e1df9ad0df6ebb4d0639bbeffed1a0"
RULE_CHECKS = {"Actual60SecondCompleteCoverageAndEstimatedRates",
               "ThreeActualMetricRulesTriggerAckSilenceRecoverAndAudit", "ActualYarpHealthyGaugeRecovers"}
BOUNDARY_CHECKS = {"ActualSameTraceparentTwoEnvironmentsOnlyReturnCurrentEnvironmentSpans",
                   "RealLokiSingleNanosecondProviderLimitExplicitlyTruncated"}
FAULT_CHECKS = {"OldARequestAcrossBSwitchKeepsActualADestinationAndTelemetry",
                "ActualApprovedRollbackSurvivesObservationStack", "lokiStopAndRecoveryKeepProxyAndApprovedPublishing",
                "tempoStopAndRecoveryKeepProxyAndApprovedPublishing", "prometheusStopAndRecoveryKeepProxyAndApprovedPublishing",
                "CollectorOutageQueueFullProxySuccessAndRealMiddleGap", "SingleNodeCollectionExpiryAndActualProcessRecovery",
                "ActualWorkerKillPgLeaseExpiresSecondWorkerTakesOver", "ActualGatewayLkgRestartWithControlPlaneRedisOffline"}
ARCHIVE_PENDING = ["immutable archive verification"]
CLEANUP = ("exitCode", "secretFilesRemaining", "containersRemaining", "volumesRemaining")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def git(*args):
    return subprocess.check_output(["git", "--no-replace-objects", *args])


ARCHIVE_STEM = "WebAPI_Enterprise_可观测与告警源码及验收"


def production_static_files(source):
    root = Path.cwd()
    node = os.environ.get("WEBAPI_NODE") or shutil.which("node")
    require(node, "Install Node to rebuild the production console")
    for name in ("console/package.json", "console/pnpm-lock.yaml"):
        require(source[name] == (root / name).read_bytes(), "Install the archived frozen dependencies before building")
    with tempfile.TemporaryDirectory(prefix="webapi-observability-console-") as temporary:
        checkout = Path(temporary)
        for name, content in source.items():
            if name.startswith("console/"):
                target = checkout / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(content)
        console = checkout / "console"
        modules = root / "console/node_modules"
        (console / "node_modules").symlink_to(modules, target_is_directory=True)
        env = dict(os.environ, PATH=str(Path(node).parent) + os.pathsep + os.environ["PATH"], NODE_ENV="production")
        subprocess.run([node, modules / "typescript/bin/tsc", "--noEmit"], cwd=console, env=env, check=True)
        subprocess.run([node, modules / "vite/bin/vite.js", "build", "--mode", "production"], cwd=console, env=env, check=True)
        static = {file.relative_to(checkout).as_posix(): file.read_bytes()
                  for file in sorted((console / "dist").rglob("*")) if file.is_file()}
        require("console/dist/index.html" in static, "Missing rebuilt console/dist/index.html")
        return static


def commit(ref):
    return git("rev-parse", "--verify", "--end-of-options", ref + "^{commit}").decode().strip()


def recorded_commit(ref):
    if not isinstance(ref, str) or not re.fullmatch(r"[0-9a-f]{7,64}", ref):
        return None
    matches = git("rev-parse", "--disambiguate=" + ref).decode().splitlines()
    if len(matches) == 1 and git("cat-file", "-t", matches[0]).strip() == b"commit":
        return matches[0]
    return None


def source_files(revision, *paths):
    """Read literal Git blobs, including paths that git archive attributes could alter."""
    entries = git("ls-tree", "-rz", "--full-tree", revision, "--", *paths)
    files = {}
    with subprocess.Popen(["git", "--no-replace-objects", "cat-file", "--batch"],
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE) as blobs:
        try:
            for entry in filter(None, entries.split(b"\0")):
                header, name = entry.split(b"\t", 1)
                mode, kind, oid = header.split()
                path = PurePosixPath(name.decode())
                if (kind != b"blob" or mode not in (b"100644", b"100755")
                        or BLOCKED.intersection(path.parts) or path.parts[:2] == ("console", "dist")):
                    continue
                blobs.stdin.write(oid + b"\n")
                blobs.stdin.flush()
                blob_id, kind, length = blobs.stdout.readline().split()
                size = int(length)
                require(blob_id == oid and kind == b"blob" and size < 64 * 1024 * 1024,
                        f"Invalid source blob: {path}")
                content = blobs.stdout.read(size)
                require(len(content) == size and blobs.stdout.read(1) == b"\n", f"Incomplete Git blob: {path}")
                files[str(path)] = content
        finally:
            blobs.stdin.close()
        require(blobs.wait() == 0, "Git source export failed")
    return files


def sha256(content):
    return hashlib.sha256(content).hexdigest()


def document(content):
    try:
        record = json.loads(content)
    except (TypeError, ValueError):
        return {}
    return record if isinstance(record, dict) else {}


def zeros(record, *fields):
    return isinstance(record, dict) and all(type(record.get(k)) is int and record[k] == 0 for k in fields)


def positive(value):
    return type(value) is int and value > 0


def green_checks(checks, required):
    if not isinstance(checks, list) or not all(isinstance(c, dict) and isinstance(c.get("name"), str)
                                               and c.get("status") == "passed" for c in checks):
        return False
    names = {c["name"] for c in checks}
    return names.issuperset(required) and len(names) == len(checks)


def fault_file(proof):
    baseline = proof.get("baselineFaultEvidence")
    if isinstance(baseline, dict) and baseline.get("fixtureFile") == FAULT_OUTPUT:
        return FAULT_OUTPUT
    return FAULT_FIXTURE


def reference_paths(proof, material):
    paths = [EVIDENCE + name for name in (*CHECK_FILES.values(), fault_file(proof), *OTHER_FILES)]
    browser = document(material.get(EVIDENCE + "browser-qa.json"))
    screenshots = browser.get("screenshots")
    if (isinstance(screenshots, list) and all(isinstance(name, str) and re.fullmatch(r"[\w.-]+\.(?:jpg|png)", name)
                                              for name in screenshots) and len(set(screenshots)) == len(screenshots)):
        paths.extend(EVIDENCE + "ui/" + name for name in screenshots)
    return paths


def same_inputs(tested, candidate, references):
    if tested == candidate:
        return True
    # Everything is an input by default: tests, fixture data, build imports, dependencies and
    # deployment/harness scripts. faults.json is explicitly consumed by ObservabilityFaultTests.
    outputs = set(references) - {EVIDENCE + FAULT_FIXTURE}
    outputs.update((RECEIPT, EVIDENCE + "execution-ledger.md", EVIDENCE + "verification-output.md",
                    "README.md", "docs/deployment/observability-runbook.md", "scripts/package-observability.py",
                    "scripts/verify-observability-delivery.py", "deliverables/manifest-observability.json"))
    changes = git("diff", "--name-only", "--no-renames", "--no-ext-diff", "-z", tested, candidate, "--")
    return all(path.decode() in outputs for path in changes.split(b"\0") if path)


def closed_loop_passed(project, material):
    loop, rules, bounds = (document(material[EVIDENCE + name]) for name in CHECK_FILES.values())
    return (all(p.get("complete") is True and p.get("project") == project for p in (loop, rules, bounds))
            and loop.get("ordinaryRequestTraceComplete") is True and loop.get("counterDelta") == 8
            and loop.get("logCount") == 8 and loop.get("spanCount") == 16 and loop.get("ordinaryRequestSpanCount") == 2
            and green_checks(rules.get("checks"), RULE_CHECKS) and green_checks(bounds.get("checks"), BOUNDARY_CHECKS))


def faults_passed(baseline, material, output, tested):
    faults = document(material[EVIDENCE + "fault-run.json"])
    fixture = document(material[EVIDENCE + output])
    fault_revision = recorded_commit(baseline.get("sourceRevision"))
    if "fixtureFile" in baseline and (output != FAULT_OUTPUT or baseline.get("sourceRevision") != tested):
        return False
    return (baseline.get("file") == "fault-run.json" and baseline.get("complete") is True and fault_revision
            and recorded_commit(faults.get("sourceRevision")) == fault_revision
            and faults.get("command") == "./scripts/check-observability.sh faults" and zeros(faults, "exitCode")
            and zeros(faults.get("cleanup"), *CLEANUP) and faults.get("complete") is True
            and green_checks(faults.get("checks"), FAULT_CHECKS) and baseline.get("checks") == len(faults["checks"])
            and fixture.get("complete") is True and fixture.get("project") == faults.get("project")
            and fixture.get("checks") == faults["checks"])


def browser_passed(material, paths):
    browser = document(material[EVIDENCE + "browser-qa.json"])
    pages = browser.get("pages")
    return (browser.get("complete") is True and browser.get("normalConsoleErrors") == []
            and isinstance(browser.get("checks"), list) and len(browser["checks"]) > 0
            and any(path.startswith(EVIDENCE + "ui/") for path in paths)
            and isinstance(pages, list) and all(isinstance(page, dict) and type(page.get("page")) is int
                                                and page.get("horizontalOverflow") is False for page in pages)
            and {page["page"] for page in pages}.issuperset(range(28, 34))
            and zeros(browser.get("cleanup"), "remainingContainers", "remainingVolumes", "remainingSyntheticSecretFiles")
            and browser["cleanup"].get("complete") is True)


def repairs_passed(material):
    loss = document(material[EVIDENCE + "final-loss-live.json"])
    permission = document(material[EVIDENCE + "final-permission-browser.json"])
    checks = loss.get("checks")
    if (not isinstance(checks, list) or not all(isinstance(c, dict) and c.get("sourceState") == "Partial"
                                                and isinstance(c.get("reason"), str) for c in checks)):
        return False
    reasons = {c["reason"] for c in checks}
    return ({"collector_logs_collection_gap", "collector_traces_collection_gap", "known_logs_collection_gap"} <= reasons
            and zeros(permission.get("green"), "kpiCards", "tables")
            and isinstance(permission.get("red"), dict) and permission["red"].get("resultsRetained") is True
            and isinstance(loss.get("project"), str) and loss["project"] and permission.get("project") == loss["project"]
            and all(p.get("complete") is True and zeros(p.get("cleanup"), *CLEANUP) for p in (loss, permission)))


def current_identity_and_networks_passed(proof, review, material, tested):
    if (proof.get("sourceRevision") != tested or review.get("reviewedRevision") != tested
            or not zeros(proof.get("cleanup"), "networksRemaining")):
        return False
    for name, field in NETWORK_COUNTS.items():
        report = document(material[EVIDENCE + name])
        if report.get("sourceRevision") != tested or not zeros(report.get("cleanup"), field):
            return False
    return True


def complete_run(proof, material, paths, tested):
    project = proof.get("project")
    review = proof.get("finalReview")
    baseline = proof.get("baselineFaultEvidence")
    if not isinstance(review, dict) or not isinstance(baseline, dict) or not all(material.get(path) for path in paths):
        return False
    output = fault_file(proof)
    if output == FAULT_OUTPUT and not current_identity_and_networks_passed(proof, review, material, tested):
        return False
    suite = review.get("fullSuite")
    checks = proof.get("checks")
    return (isinstance(project, str) and project and proof.get("criticalAcceptanceComplete") is True
            and proof.get("mode") == "e2e" and proof.get("classification") == "real-isolated-gateway-three-source"
            and proof.get("command") == "./scripts/check-observability.sh e2e" and zeros(proof, "exitCode")
            and zeros(proof.get("cleanup"), *CLEANUP) and green_checks(checks, CHECK_FILES)
            and {c["name"]: c.get("evidence") for c in checks} == CHECK_FILES
            and review.get("repairValidationComplete") is True and review.get("typescriptAndProductionBuild") is True
            and positive(review.get("independentReviews")) and recorded_commit(review.get("reviewedRevision"))
            and zeros(suite, "skipped") and all(zeros(suite, k) for k in ("failed", "errors") if k in suite)
            and all(positive(suite.get(k)) for k in ("domain", "integration", "gateway", "frontend"))
            and proof.get("browserEvidence") == "browser-qa.json" and review.get("realLossProof") == "final-loss-live.json"
            and review.get("permissionProof") == "final-permission-browser.json" and closed_loop_passed(project, material)
            and faults_passed(baseline, material, output, tested) and browser_passed(material, paths)
            and repairs_passed(material))


def matches_legacy_evidence(revision, material, paths):
    if not recorded_commit(revision):
        return False
    paths = (RECEIPT, *paths)
    original = source_files(revision, *paths)
    return all(path in original and original[path] == material.get(path) for path in paths)


def eligibility(proof, source, evidence, tested, material, paths):
    review = proof.get("finalReview", {})
    fresh = fault_file(proof) == FAULT_OUTPUT
    if not zeros(proof.get("cleanup"), "secretFilesRemaining"):
        raise ValueError("Observability receipt lacks a zero remaining secret-file count")
    if not proof.get("complete"):
        require(proof.get("complete") is False and isinstance(review, dict) and review.get("pending") == ARCHIVE_PENDING
                and tested and complete_run(proof, material, paths, tested), "Required acceptance is pending")
        require(same_inputs(tested, source, paths), "Provisional source inputs changed")
        if fresh:
            require(tested == source and "archiveGate" not in proof and same_inputs(tested, evidence, paths),
                    "Invalid provisional evidence provenance")
        else:
            require(matches_legacy_evidence(LEGACY_DRAFT_REVISION, material, paths),
                    "Unrecognized historical observability draft")
        return "archive-verification-pending"
    if (proof.get("complete") is not True or not isinstance(review, dict) or review.get("pending") != []
            or not tested or not complete_run(proof, material, paths, tested)):
        return "incomplete-evidence"
    if (fresh and not same_inputs(tested, evidence, paths)) or not same_inputs(tested, source, paths):
        return "source-inputs-changed"
    gate = proof.get("archiveGate")
    if (not isinstance(gate, dict) or gate.get("verified") is not True or not positive(gate.get("files"))
            or not isinstance(gate.get("provisionalSha256"), str)
            or not re.fullmatch(r"[0-9a-f]{64}", gate["provisionalSha256"])
            or recorded_commit(gate.get("productSourceRevision")) != tested):
        return "unverified-archive-gate"
    if fresh:
        provisional = recorded_commit(gate.get("provisionalEvidenceRevision"))
        if (gate.get("productSourceRevision") != tested or gate.get("provisionalSourceRevision") != tested
                or not provisional or gate.get("provisionalEvidenceRevision") != provisional):
            return "unverified-archive-gate"
    else:
        if not matches_legacy_evidence(LEGACY_SEAL_REVISION, material, paths):
            return "unrecognized-legacy-evidence"
        provisional = recorded_commit(gate.get("provisionalSourceRevision"))
    if not provisional or not same_inputs(tested, provisional, paths):
        return "unverified-archive-gate"
    # Sealing is a second commit. Bind the results to the immutable pre-seal run;
    # editing the declared tested SHA or replacing an attachment must not transplant it.
    previous = source_files(provisional, RECEIPT, *paths)
    draft = document(previous.get(RECEIPT))
    expected_review = {**review, "pending": ARCHIVE_PENDING}
    if fresh:
        expected_draft = {key: value for key, value in proof.items() if key != "archiveGate"}
        expected_draft.update(complete=False, finalReview=expected_review)
        if draft.get("complete") is not False or draft != expected_draft or not complete_run(draft, previous, paths, tested):
            return "unverified-archive-gate"
    elif (draft.get("complete") is not False or draft.get("finalReview") != expected_review
          or any(draft.get(k) != proof.get(k) for k in (
              "sourceRevision", "project", "checks", "cleanup", "exitCode", "command", "classification", "mode",
              "criticalAcceptanceComplete", "baselineFaultEvidence", "browserEvidence"))):
        return "unverified-archive-gate"
    if any(previous.get(path) != material[path] for path in paths):
        return "unverified-archive-gate"
    return "complete"


def acceptance(source, evidence, material):
    data = material.get(RECEIPT)
    require(data, "Required observability receipt is missing")
    proof = document(data)
    tested = recorded_commit(proof.get("sourceRevision"))
    paths = reference_paths(proof, material)
    status = eligibility(proof, source, evidence, tested, material, paths)
    prefix = "enterprise/" if source == evidence else "package-evidence/"
    refs = {name: {"archivePath": prefix + name, "sha256": sha256(material[name])}
            for name in paths if name in material}
    receipt_revision = git("log", "-1", "--format=%H", evidence, "--", RECEIPT).decode().strip()
    metadata = {"revision": evidence, "receiptRevision": receipt_revision, "testedSourceRevision": tested,
                "path": RECEIPT, "archivePath": prefix + RECEIPT, "sha256": sha256(data),
                "status": status, "references": refs}
    attachments = {} if source == evidence else {prefix + name: material[name] for name in (RECEIPT, *refs)}
    return metadata, attachments


def verify_archive(archive, digest, count, files, static):
    require(sha256(archive.read_bytes()) == digest, "Archive SHA256 mismatch")
    with zipfile.ZipFile(archive) as z:
        require(z.testzip() is None and len(z.infolist()) == count, "Invalid ZIP contents")
        names = set(files) | {"enterprise/" + name for name in static}
        require(len(names) == len(z.infolist()) and set(z.namelist()) == names, "Unexpected or missing ZIP entries")
        require(not any(BLOCKED.intersection(PurePosixPath(name).parts) for name in names), "Excluded ZIP entry")
        for name, content in files.items():
            require(z.read(name) == content, f"ZIP differs from Git: {name}")
        for name, digest in static.items():
            require(sha256(z.read("enterprise/" + name)) == digest, f"Static file SHA256 mismatch: {name}")


def verify_provisional_archive(out, material, static):
    proof = document(material[RECEIPT])
    if fault_file(proof) != FAULT_OUTPUT:
        return
    gate = proof["archiveGate"]
    paths = (RECEIPT, *reference_paths(proof, material))
    draft = source_files(gate["provisionalEvidenceRevision"], *paths)
    # The tested source can differ in receipt bytes from an evidence-only final HEAD.
    source = source_files(gate["provisionalSourceRevision"])
    files = {"enterprise/" + name: data for name, data in source.items()}
    files.update({"package-evidence/" + name: draft[name] for name in paths})
    digest = gate["provisionalSha256"]
    verify_archive(out / f"{ARCHIVE_STEM}-{digest}.zip", digest, gate["files"], files, static)
