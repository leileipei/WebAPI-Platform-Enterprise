#!/usr/bin/env python3
"""Build the core source delivery from a selected Git commit."""
import argparse
import contextlib
import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tempfile
import zipfile


ROOT = Path(__file__).resolve().parents[1]
ARCHIVE = "WebAPI_Enterprise_核心闭环源码与验收.zip"
RECEIPT = "docs/evidence/core-loop/verification.json"
EXTERNAL_RECEIPT = "package-evidence/core-loop/verification.json"
REGRESSION_GROUPS = ("domain", "integration", "gateway", "consoleTransport", "consolePaging", "consoleNavigation")
SCENARIOS = "docs/evidence/core-loop/scenarios.json"
FAULTS = "docs/evidence/core-loop/faults.json"
BLOCKED = {".git", ".worktrees", "worktrees", ".superpowers", ".runtime", ".secrets",
           "secrets", "backup", "backups", "node_modules", "bin", "obj", "testresults",
           "deliverables", "__pycache__", ".pnpm-store", ".cache", ".aws", ".ssh", ".gnupg"}
PRIVATE = {".npmrc", ".netrc", ".env", ".ds_store", "owner.json", "bootstrap-password", "credential", "credentials"}
EXCLUDED_SUFFIXES = {".trx", ".user", ".log", ".dump", ".tar", ".tgz", ".zip", ".pfx",
                     ".p12", ".key", ".pem", ".dll", ".pdb", ".pyc"}


def git(*args):
    return subprocess.check_output(["git", "--no-replace-objects", *args], cwd=ROOT)


def commit(ref):
    return git("rev-parse", "--verify", "--end-of-options", ref + "^{commit}").decode().strip()


def excluded(path):
    parts = [part.lower() for part in path.parts]
    return (any(part in BLOCKED for part in parts) or parts[-1] in PRIVATE
            or parts[-1].startswith(".env.") or path.suffix.lower() in EXCLUDED_SUFFIXES)


def source_blobs(revision):
    # Read literal blobs. git archive could substitute or omit bytes via export attributes.
    records = git("ls-tree", "-rz", revision).split(b"\0")
    with subprocess.Popen(["git", "--no-replace-objects", "cat-file", "--batch"], cwd=ROOT,
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE) as blobs:
        try:
            for record in filter(None, records):
                header, name = record.split(b"\t", 1)
                mode, kind, oid = header.split()
                path = PurePosixPath(name.decode())
                if kind != b"blob" or excluded(path) or path.parts[:2] == ("console", "dist"):
                    continue
                if mode not in (b"100644", b"100755"):
                    raise ValueError(f"Unsupported source file mode {mode.decode()}: {path}")
                blobs.stdin.write(oid + b"\n")
                blobs.stdin.flush()
                blob_id, blob_type, length = blobs.stdout.readline().split()
                content = blobs.stdout.read(int(length))
                if (blob_id != oid or blob_type != b"blob" or len(content) != int(length)
                        or blobs.stdout.read(1) != b"\n"):
                    raise ValueError(f"Incomplete Git blob: {path}")
                yield path, int(mode, 8), content
        finally:
            blobs.stdin.close()
        if blobs.wait() != 0:
            raise ValueError("Git source export failed")


def zero(record, field):
    return type(record.get(field)) is int and record[field] == 0


def completed_checks(proof):
    checks = proof.get("checks")
    cleanup = proof.get("temporaryFixtureCleanup")
    if not isinstance(checks, dict) or not isinstance(cleanup, dict):
        return False
    counts = [checks.get(group) for group in REGRESSION_GROUPS]
    if not all(isinstance(group, dict) and type(group.get("passed")) is int
               and group["passed"] > 0 and zero(group, "failed") for group in counts):
        return False
    total = checks.get("automatedRegressionTotal")
    release = checks.get("lockedReleaseBuild")
    return (proof.get("complete") is True and proof.get("testsComplete") is True
            and isinstance(total, dict) and type(total.get("passed")) is int
            and total["passed"] == sum(group["passed"] for group in counts)
            and zero(total, "failed") and zero(total, "skipped")
            and all(zero(checks[group], "skipped") for group in ("domain", "integration", "gateway"))
            and all(zero(group, key) for group in checks.values() if isinstance(group, dict)
                    for key in ("failed", "errors", "skipped") if key in group)
            and checks.get("consoleBuild") == "TypeScript + Vite passed"
            and isinstance(release, dict) and zero(release, "warnings") and zero(release, "errors")
            and cleanup.get("complete") is True
            and all(zero(cleanup, key) for key in ("containersRemaining", "volumesRemaining",
                                                   "secretFilesRemaining")))


def read_json(data):
    try:
        document = json.loads(data)
    except (TypeError, ValueError):
        return {}
    return document if isinstance(document, dict) else {}


def matches_revision(tested, revision):
    return (isinstance(tested, str) and re.fullmatch("[0-9a-f]{7,64}", tested)
            and revision.startswith(tested)
            and git("rev-parse", "--disambiguate=" + tested).decode().splitlines() == [revision])


def evidence_blob(revision, name):
    path = PurePosixPath(name)
    if (path.parts[:2] != ("docs", "evidence") or name != str(path)
            or ".." in path.parts or chr(92) in name or excluded(path)):
        return None
    entry = git("--literal-pathspecs", "ls-tree", "-z", revision, "--", name)
    if not entry.startswith((b"100644 blob ", b"100755 blob ")):
        return None
    return git("cat-file", "blob", revision + ":" + name)


def verified_references(proof, evidence, revision):
    checks = proof["checks"]
    paths = [checks.get("browser"), checks.get("browserFinalReview"), proof.get("reviewRecord"), SCENARIOS, FAULTS]
    if not all(isinstance(path, str) and path for path in paths) or len(set(paths)) != len(paths):
        return {}
    blobs = {path: evidence_blob(evidence, path) for path in paths}
    if not all(blobs.values()):
        return {}
    browser_initial = read_json(blobs[paths[0]])
    browser_final = read_json(blobs[paths[1]])
    if (not browser_initial.get("checks") or browser_initial.get("pending") != []
            or browser_final.get("complete") is not True or not browser_final.get("checks")
            or not matches_revision(browser_final.get("testedSourceRevision"), revision)):
        return {}
    scenarios = read_json(blobs[SCENARIOS])
    faults = read_json(blobs[FAULTS])
    green_cases = scenarios.get("checks")
    fault_cases = faults.get("checks")
    if (scenarios.get("complete") is not True or not isinstance(green_cases, list) or len(green_cases) < 3
            or faults.get("complete") is not True or not isinstance(fault_cases, list) or len(fault_cases) < 7
            or type(checks.get("containerFaults")) is not int or checks["containerFaults"] != len(fault_cases)
            or not all(isinstance(case, dict) and case.get("result") == "passed" for case in fault_cases)):
        return {}
    return blobs


def acceptance(evidence, revision):
    metadata = {"revision": evidence, "path": RECEIPT}
    data = evidence_blob(evidence, RECEIPT)
    if data is None:
        return False, metadata, {}
    metadata["sha256"] = hashlib.sha256(data).hexdigest()
    proof = read_json(data)
    tested = proof.get("testedSourceRevision")
    if isinstance(tested, str):
        metadata["testedSourceRevision"] = tested
    if evidence == revision:
        metadata["archivePath"] = "enterprise/" + RECEIPT
    if not matches_revision(tested, revision) or not completed_checks(proof):
        return False, metadata, {}
    references = verified_references(proof, evidence, revision)
    if not references:
        return False, metadata, {}
    attachments = {}
    prefix = "enterprise/"
    if evidence != revision:
        metadata["archivePath"] = EXTERNAL_RECEIPT
        attachments[EXTERNAL_RECEIPT] = data
        prefix = "package-evidence/"
        attachments.update((prefix + name, blob) for name, blob in references.items())
    metadata["references"] = {name: {"archivePath": prefix + name, "sha256": hashlib.sha256(blob).hexdigest()}
                              for name, blob in references.items()}
    return True, metadata, attachments


def build(console):
    node = os.environ.get("WEBAPI_NODE") or shutil.which("node")
    pnpm = os.environ.get("WEBAPI_PNPM") or shutil.which("pnpm")
    if not (node and pnpm and (console / "pnpm-lock.yaml").is_file()
            and (console / "package.json").is_file()):
        raise ValueError("The selected console requires Node, pnpm, package.json and pnpm-lock.yaml")
    env = {name: value for name, value in os.environ.items()
           if name not in {"NODE_OPTIONS", "NODE_PATH", "NODE_ENV"} and not name.startswith("VITE_")}
    env["CI"] = "true"
    env["PATH"] = str(Path(node).absolute().parent) + os.pathsep + os.environ["PATH"]
    subprocess.run([pnpm, "install", "--frozen-lockfile", "--verify-store-integrity", "--prod=false",
                    "--virtual-store-dir", "node_modules/.pnpm", "--package-import-method", "copy"], cwd=console, env=env, check=True)
    subprocess.run([pnpm, "run", "build"], cwd=console, env=env, check=True)
    dist = console / "dist"
    if dist.is_symlink() or not (dist / "index.html").is_file():
        raise ValueError("Missing rebuilt console/dist/index.html")
    for path in sorted(dist.rglob("*")):
        if path.is_symlink() or excluded(path.relative_to(dist)):
            raise ValueError(f"Unexpected static build output: {path.relative_to(dist)}")
        if path.is_file():
            yield PurePosixPath("console/dist") / path.relative_to(dist), path.read_bytes()


def zip_entry(archive, path, mode, data):
    entry = zipfile.ZipInfo(path)  # fixed 1980 epoch
    entry.create_system = 3
    entry.external_attr = mode << 16
    archive.writestr(entry, data, compress_type=zipfile.ZIP_DEFLATED)


def make_archive(target, console, revision, attachments):
    with zipfile.ZipFile(target, "w") as archive:
        for path, mode, data in source_blobs(revision):
            zip_entry(archive, "enterprise/" + str(path), mode, data)
            if path.parts[0] == "console":
                local = console.parent / path
                local.parent.mkdir(parents=True, exist_ok=True)
                local.write_bytes(data)
                local.chmod(mode & 0o777)
        for path, data in build(console):
            zip_entry(archive, "enterprise/" + str(path), 0o100644, data)
        for name, data in attachments.items():
            zip_entry(archive, name, 0o100644, data)
    with zipfile.ZipFile(target) as archive:
        if archive.testzip() or "enterprise/README.md" not in archive.namelist():
            raise ValueError("Archive verification failed")
        return len(archive.infolist())


def publish(staging, out):
    # A manifest is the publication marker. Hide the previous marker before changing
    # its ZIP, and restore the previous pair if either replacement fails.
    with contextlib.ExitStack() as resources:
        directory = os.open(out, os.O_RDONLY)
        resources.callback(os.close, directory)
        fcntl.flock(directory, fcntl.LOCK_EX)
        archive, manifest = out / ARCHIVE, out / "manifest.json"
        for path in (archive, manifest):
            if path.is_symlink() or (path.exists() and not path.is_file()):
                raise ValueError(f"Invalid delivery destination: {path.name}")
        old_archive, old_manifest = staging / "previous.zip", staging / "previous.json"
        if archive.exists():
            os.link(archive, old_archive)
        if manifest.exists():
            os.replace(manifest, old_manifest)
        try:
            os.replace(staging / ARCHIVE, archive)
            os.replace(staging / "manifest.json", manifest)
        except BaseException:
            if old_archive.exists():
                os.replace(old_archive, archive)
            elif archive.exists():
                archive.unlink()
            if old_manifest.exists():
                os.replace(old_manifest, manifest)
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("commit", nargs="?", default="HEAD", help="source Git revision (default HEAD)")
    parser.add_argument("--evidence-revision", help="Git revision holding the core-loop acceptance receipt")
    args = parser.parse_args()
    revision = commit(args.commit)
    evidence = commit(args.evidence_revision) if args.evidence_revision else revision
    accepted, receipt, attachments = acceptance(evidence, revision)
    out = ROOT / "deliverables"
    if out.is_symlink():
        raise ValueError("Invalid delivery directory")
    out.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".core-package-", dir=out) as staging_dir, \
            tempfile.TemporaryDirectory(prefix="core-console-") as checkout:
        staging = Path(staging_dir)
        target = staging / ARCHIVE
        count = make_archive(target, Path(checkout) / "console", revision, attachments)
        digest = hashlib.sha256()
        with target.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        manifest = {"file": ARCHIVE, "sha256": digest.hexdigest(), "files": count,
                    "secretsExcluded": True, "sourceRevision": revision,
                    "staticSourceRevision": revision, "acceptanceComplete": accepted,
                    "acceptanceEvidence": receipt}
        (staging / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        publish(staging, out)
    print(out / ARCHIVE)
    print(f"Files: {count}; SHA256: {digest.hexdigest()}")


if __name__ == "__main__":
    main()
