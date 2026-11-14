# Merge CI Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve the observed merge blockers with evidence, preserving local work and all existing gates.
**Architecture:** Work from current main plus PR #11 in an isolated checkout. Retain original diagnostics, correct their completeness contract, diagnose cache outcomes, and add Python checks to an existing required job.
**Tech Stack:** .NET 10.0.401, C#, xUnit, PostgreSQL, Redis, Python 3.9+, GitHub Actions.
**Spec:** docs/superpowers/specs/2026-11-14-merge-ci-repair-design.md

## Global Constraints
- Login 1000ms and cache 100ms budgets remain unchanged.
- Existing 1 second / 2 second timing assertions, four required checks, and branch protection remain unchanged.
- No automatic retry to obtain green; preserve failures and investigate them.
- Original worktrees and ignored evidence remain intact. Only task-owned test resources may be removed.
- Merge only after the fixed revision passes all required checks and blocking failures have an evidence-backed explanation.

## Review Focus
- Budget cancellation versus caller cancellation must retain distinct classification and completeness.
- Missing or mismatched trace records must stay incomplete; diagnostics must not replace the original response.
- Bypassed cache writes must not count as successful stored entries.
- Cold connection, cache read failure, and write failure must retain distinct evidence without changing budgets.
- Python failures must propagate through an existing required job.

### Task 1: Preservation and baseline
- [ ] Archive all dirty non-ignored files, staged/unstaged patches, refs, and repository bundles; verify hashes and symlinks.
- [ ] Create an isolated branch at PR #11, whose parent is current main; retain source directories.

### Task 2: Login diagnostics
Files: existing diagnostic draft in Infrastructure/Security, ControlPlane, TestSupport, DeliveryPersistenceTests; LoginDiagnosticPreparationTests.
Interface: IAuthenticationDiagnosticSink and LoginDiagnosticCollector.BuildFailureAsync preserve response and safely classify phases.
- [ ] Import only the backed-up login diagnostic files.
- [ ] Add a regression with real OperationCanceledException budget classification; assert complete evidence for internal budget expiry and incomplete evidence for missing phases/request cancellation.
- [ ] Run the regression and observe failure, minimally fix CompletePath, then run all diagnostic and login/delivery tests.
- [ ] Retain red/green logs and safe phase evidence, then commit.

### Task 3: Cache test truthfulness and failure diagnosis
Files: CachePipelineFixture.cs, NetworkPolicyCacheTests.cs, related cache store/pipeline tests as justified by evidence.
Interface: ObservedStore distinguishes attempts, Stored/Bypass outcomes, read outcomes, and elapsed times; WaitWrites waits for successful storage.
- [ ] Add regression proving a bypassed real cache write cannot satisfy the write gate; observe failure.
- [ ] Correct counting, attach bounded safe operation diagnostics to the failing cache path, and preserve existing byte/partition/cross-node assertions.
- [ ] Reproduce connection/read/write failure mechanisms using controlled inputs and exercise the actual failing network-policy cache test with real Redis.
- [ ] Fix only a confirmed underlying defect; document remaining historical uncertainty; run the relevant cache suite and commit.

### Task 4: Python CI and completion
Files: .github/workflows/console-ci.yml; concise diagnosis report under docs/deployment.
Interface: required console job runs Python unittest discovery before existing console checks.
- [ ] Add Python checks, run the exact command, and validate workflow structure; no extra mirror test.
- [ ] Run focused C# checks and obtain independent whole-branch review; resolve material findings with regression tests.
- [ ] Push a fixed revision to the existing PR head with an expected-head check; update the PR description with scope and actual evidence.
- [ ] Follow all four required GitHub checks. Investigate any failure without automatic retries or relaxed assertions.
- [ ] Merge only when the user's root-cause and all-green conditions are met; follow the merge-triggered checks and report actual results.
