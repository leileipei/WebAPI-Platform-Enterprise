"""Immutable inputs and receipt checks shared by observability packaging and verification."""
import hashlib
import json
from pathlib import PurePosixPath
import re
import subprocess


EVIDENCE = "docs/evidence/observability/"
RECEIPT = EVIDENCE + "verification.json"
BLOCKED = {".git", ".superpowers", ".runtime", ".secrets", "node_modules", "bin", "obj", "TestResults", "deliverables"}
CHECK_FILES = {"loop": "loop.json", "rule-loop": "rule-loop.json", "boundaries": "boundaries.json"}
OTHER_FILES = ("faults.json", "fault-run.json", "browser-qa.json", "final-loss-live.json",
               "final-permission-browser.json", "final-review.md")
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


def reference_paths(material):
    paths = [EVIDENCE + name for name in (*CHECK_FILES.values(), *OTHER_FILES)]
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
    outputs = set(references) - {EVIDENCE + "faults.json"}
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


def faults_passed(baseline, material):
    faults = document(material[EVIDENCE + "fault-run.json"])
    fixture = document(material[EVIDENCE + "faults.json"])
    fault_revision = recorded_commit(baseline.get("sourceRevision"))
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


def complete_run(proof, material, paths):
    project = proof.get("project")
    review = proof.get("finalReview")
    baseline = proof.get("baselineFaultEvidence")
    if not isinstance(review, dict) or not isinstance(baseline, dict) or not all(material.get(path) for path in paths):
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
            and faults_passed(baseline, material) and browser_passed(material, paths) and repairs_passed(material))


def eligibility(proof, source, tested, material, paths):
    review = proof.get("finalReview", {})
    if not zeros(proof.get("cleanup"), "secretFilesRemaining"):
        raise ValueError("Observability receipt lacks a zero remaining secret-file count")
    if not proof.get("complete"):
        require(proof.get("complete") is False and proof.get("criticalAcceptanceComplete") is True
                and isinstance(review, dict) and review.get("repairValidationComplete") is True
                and review.get("pending") == ARCHIVE_PENDING, "Required acceptance is pending")
        return "archive-verification-pending"
    if (proof.get("complete") is not True or not isinstance(review, dict) or review.get("pending") != []
            or not tested or not complete_run(proof, material, paths)):
        return "incomplete-evidence"
    if not same_inputs(tested, source, paths):
        return "source-inputs-changed"
    gate = proof.get("archiveGate")
    if (not isinstance(gate, dict) or gate.get("verified") is not True or not positive(gate.get("files"))
            or not isinstance(gate.get("provisionalSha256"), str)
            or not re.fullmatch(r"[0-9a-f]{64}", gate["provisionalSha256"])
            or recorded_commit(gate.get("productSourceRevision")) != tested):
        return "unverified-archive-gate"
    provisional = recorded_commit(gate.get("provisionalSourceRevision"))
    if not provisional or not same_inputs(tested, provisional, paths):
        return "unverified-archive-gate"
    # Sealing is a second commit. Bind the results to the immutable pre-seal run;
    # editing the declared tested SHA or replacing an attachment must not transplant it.
    previous = source_files(provisional, RECEIPT, *paths)
    draft = document(previous.get(RECEIPT))
    expected_review = {**review, "pending": ARCHIVE_PENDING}
    if (draft.get("complete") is not False or draft.get("finalReview") != expected_review
            or any(draft.get(k) != proof.get(k) for k in (
                "sourceRevision", "project", "checks", "cleanup", "exitCode", "command", "classification", "mode",
                "criticalAcceptanceComplete", "baselineFaultEvidence", "browserEvidence"))
            or any(previous.get(path) != material[path] for path in paths)):
        return "unverified-archive-gate"
    return "complete"


def acceptance(source, evidence, material):
    data = material.get(RECEIPT)
    require(data, "Required observability receipt is missing")
    proof = document(data)
    tested = recorded_commit(proof.get("sourceRevision"))
    paths = reference_paths(material)
    status = eligibility(proof, source, tested, material, paths)
    prefix = "enterprise/" if source == evidence else "package-evidence/"
    refs = {name: {"archivePath": prefix + name, "sha256": sha256(material[name])}
            for name in paths if name in material}
    receipt_revision = git("log", "-1", "--format=%H", evidence, "--", RECEIPT).decode().strip()
    metadata = {"revision": evidence, "receiptRevision": receipt_revision, "testedSourceRevision": tested,
                "path": RECEIPT, "archivePath": prefix + RECEIPT, "sha256": sha256(data),
                "status": status, "references": refs}
    attachments = {} if source == evidence else {prefix + name: material[name] for name in (RECEIPT, *refs)}
    return metadata, attachments
