# B阶段执行裁定

以下按实际决策顺序完整保留，包含取舍和误判成本。Minor0，无延期Minor。最终安装事实在各项历史裁定之后记录于[交付索引](delivery-index.md)。

1. Ruling: policy HTTP endpoints listed for B6 but B1 requires real ETag/idempotent PUT — introduce only policy endpoints now, extend same DeliveryEndpoints at B6 — cost if wrong: small endpoint sequencing change, no product scope expansion.

2. Ruling: test-acceptance revocation needs its own append event with no Promotion yet — allow exactly one scoped owner (PromotionId or AcceptanceId) in ReleasePromotionEvent with composite FKs and owner XOR check — cost if wrong: event table name remains broader than business owner, no duplicate workflow introduced.

3. Ruling: organization-wide applications have nullable ProjectId — preserve existing nullability, enforce organization with composite FK and project applicability in saved mapping service at B5 — cost if wrong: database alone does not prevent same-organization foreign-project application mapping; B5 authorized validation/test is mandatory.

4. Ruling: plan ArtifactRoute DTO cannot reference a Domain-owned template (Contracts→Domain would cycle) — put pure template data in Contracts, keep Split/Resolve in Domain ArtifactPolicyTemplates helper — cost if wrong: helper name differs but dependency direction remains valid.

5. Ruling: legacy route authentication has no policy slot in frozen candidate — add explicit ArtifactRoute.AuthenticationMode derived from real route effective mode so ApiKey/Anonymous cannot disappear from portable hash — cost if wrong: additive DTO member, must be enforced during B5 mapping.

6. Ruling: normalized logical route identity is derived, never trusted from caller — empty key derives; a supplied mismatching key rejects — cost if wrong: altered manually authored DTO fails clearly instead of silently remapping.

7. Ruling: schema contracts retain user-authored schema/description/examples; strip only platform Version source documents and operational metadata — follows immutable behavior without destructive semantic redaction — cost if wrong: authored text can contain a URL and is not a claim of universal content sanitization.

8. Ruling: successful source recovery/rollback uses its exact snapshot original publish candidate, while new artifact remains bound to current successful source release and sequence — preserves both integrity and rollback provenance — cost if wrong: missing historical original candidate blocks creation with422 rather than approximating.

9. Ruling: planned StoreAsync ScopeRef cannot encode report owner artifact/promotion or satisfy DB owner XOR — accept only artifactId/promotionId and resolve internal ReportOwner with real scope before upload and again in metadata transaction — cost if wrong: internal signature differs, client still never supplies scope.

10. Ruling: report backup inclusion is mandatory in B3, though detailed backup tooling appears in B11 — add supported-volume detection and coldbackup/restore selection now, reserve actual coldrestore proof for B3/B11 verification — cost if wrong: earlier backup helper change, old packages without reportvolume remain compatible.

11. Ruling: bounded raw-body upload avoids multipart unbounded parsing; at most2 concurrently per CP, immutable random private files, actualbytes capped10MiB, strictUTF8 and reject active HTML signatures — cost if wrong: text containing active HTML markup must be saved as a supported report, not silently executed or fetched.

12. Ruling: failed command receipt does not prove metadata rollback — independent DB confirmation retainscommittedreport; confirmedabsentfile cleaned; if DBunavailable after SaveChanges keep uncertainfile rather than erasepossiblycommittedevidence — cost if wrong: one owned unconfirmedfile can require later reconciliation after database availability, no foreign file cleanup and no data loss from uncertaincommit.

13. Ruling: applying/revoking acceptance uses governance then sorted source/currenttarget/allhistoriclinkedtargets locks — a changedconnection must not exclude old queuedtarget — cost if wrong: more owned environment locks for historic acceptance but no foreignscope locking. B6/B7 must share same ordering.

14. Ruling: revocation cancels only not-yet-downstream Draft/WaitingApproval/Ready/Building release and invalidates itsPromotion; Publishing/Succeeded never implied undone — preserves actual partialdeployment facts — cost if wrong: already emitteddesiredconfig needs explicit manual recovery/rollback, as approvedspec requires.

15. Ruling: reusable current-acceptance gate rechecks originalacceptor functionalpermission/writeScope/contractvisibility as well as frozen source/evidencehash/policy; requestingmanual evidence permission is separate from productionapproval/publish — cost if wrong: revoking acceptanceauthority blocks future queuedexecution until reapproval rather than grandfathering disabled identities.

16. Ruling: Target policy mappings reference existing target policy ID/revision solely for environment fields; private policies resolve source frozen behavior without mutating shared originals — prevents client-supplied JWT/JWKS/config secrets and behavior drift — cost if wrong: users must first create valid target policy configuration.

17. Ruling: Existing route may only match artifact API, normalized path and method set; generated private resource IDs persist in mapping parameters — stable preparation without publishing or hijacking unrelated routes — cost if wrong: legitimate route replacement needs separate configuration change.

18. Ruling: Unselected business applications retain exact verified baseline status, credentials and grants; shared selected applications require all retained credential IDs, unchanged existing bytes, and explicit impact acknowledgement — no implicit credential deletion or unrelated business change — cost if wrong: shared consumer rotation must use a separate reviewed release.

19. Ruling: Prepare runs only inside caller-owned governance/environment lock transaction; frozen candidate and secret hashes stay internal, HTTP mapping view contains IDs only — prevents nested commits and credential disclosure — cost if wrong: all future submit/precheck callers must obey locking contract.

20. Ruling: Keep superseded private policy rows as historical configuration references, without deleting policy rows on mapping edits — releases can retain references and shared-policy mutation is prohibited — cost if wrong: later owner-scoped housekeeping may be needed for unreferenced draft copies.

21. Ruling: B5 baseline is a verified SnapshotCompiler-produced persistence fixture, not a live target deployment; real two-environment Gateway validation remains B11 — separates candidate invariants from deployment evidence — cost if wrong: runtime-specific bugs must still be caught in B11.

22. Ruling: Cross-environment shared application impact exposes a boolean and requires explicit acknowledgement, without disclosing unrelated environment IDs; additional shared authorization revisions bind frozen approval preconditions — existing credentials are application-level and environment isolation is grant-based — cost if wrong: a shared grant edit in another environment may invalidate this pending promotion.

23. Ruling: Promotion rejects a Retired artifact API instead of inheriting normal publish's implicit removal semantics — a promotion must deliver its tested frozen content — cost if wrong: retiring/removing an API requires a separate reviewed normal change or a newly tested artifact.

24. Ruling: Unknown artifact IDs remain scope-safe 404; different-body idempotency regression uses a valid same-project artifact and the same target-scope key — preserve existing scope-bound idempotency/privacy rather than probing all projects for a key — cost if wrong: a key reused with an inaccessible/unknown artifact will return 404 rather than 409.

25. Ruling: Explicit precheck input records manual target-upstream health confirmation, comment and optional B3 promotion-owned report; no arbitrary health URL probe, no claimed automatic execution — source Integration evidence alone is not target health evidence — cost if wrong: human evidence cannot establish report truth or substitute production verification.

26. Ruling: Precheck reads actual persisted comparison/report freshness for each changed baseline version, requiring a matching accepted-risk reference for Unknown/Breaking/Unsupported; it never silently marks unknown risk zero and does not create repeated comparisons under one command key — reuse existing bounded comparison/report engine and review flow — cost if wrong: users may need to create and review the exact PROD baseline → artifact version comparison before submitting.

27. Ruling: Extract shared release-freezing/two-level seat creation primitive under the caller's transaction; only promotion submit may create its formal linked release, ordinary HTTP fields cannot forge linkage — avoid nested transaction/idempotency receipts and preserve existing approval engine — cost if wrong: this primitive must stay inaccessible to ordinary create endpoints.

28. Ruling: Create/submit/publish direct-production bypass guards land in B6; full promotion execution/worker queue revalidation and ACK/timeout/recovery projection remain B7 — the approved B6 explicitly requires all three direct-entry guards — cost if wrong: the intermediate B6 commit is not a deployable finished promotion workflow.

29. Ruling: Missing target rollback baseline is a blocking Unknown; initialize a valid production baseline through the existing Legacy path before enabling promotion — spec requires rollback snapshot availability, so no unreviewed first-deployment exception — cost if wrong: a completely empty production environment requires bootstrap configuration before first promotion. B6 positive tests now seed a SnapshotCompiler-verified baseline plus deterministic persistence ACK fixtures, explicitly not live target Gateway evidence; B11 must bootstrap and confirm the real production group before enabling PromotionRequired.

30. Ruling: Independent rejection may stop a source-expired pending production application while preserving evidence, provided the reviewer still has production approval eligibility and source contract visibility — an expired positive gate must not prevent a negative decision — cost if wrong: rejection can proceed on historical evidence instead of a current successful test.

31. Ruling: Historical retry/rollback keep existing authority and approvals and do not require current source acceptance; exact immutable target snapshot and recovery chain are required — approved spec preserves historical recovery while preventing new unapproved content — cost if wrong: stale source evidence could wrongly block incident recovery.

32. Ruling: ACK and timeout take governance lock before sorted source/target locks, including ordinary releases — prevents lock inversion when projecting delivery facts in the same transaction — cost if wrong: increased serialization of node receipts.

33. Ruling: Successful retry of an approved historical rollback projects RolledBack, not Verifying — recovery changes deployment sequence, not the original rollback intent — cost if wrong: UI could invite business completion against restored older content.

34. Ruling: Replaying a completed publish returns its original immutable command receipt after current command permission and source visibility checks; it does not reexecute or validate old mutable baseline — fulfills approved command semantics without granting a new deployment — cost if wrong: caller must refresh read model to see later deployment status.

35. Ruling: Reuse unchanged ReleaseBuildWorker persistent queue loop and existing ReleaseService approval hooks; add execution checks in its coordinator and register all new dependencies in Worker and ControlPlane — avoids parallel callbacks or duplicated workers, verified by actual host start/stop/rebuild and independent CP restart — cost if wrong: a different worker entry point would need the same coordinator gate.

36. Ruling: Add optional ExpectedContextHash to shared verification request, required only for production, and an authorized production verification-context GET — server-bound facts alone cannot identify a form opened before entry changed, so stale form tokens must409 and missing reviewed context must422 — cost if wrong: production clients must load a context before registering manual facts; source clients remain compatible.

37. Ruling: Production validation keeps the submitted connection validity policy frozen and checks current verifier authority and actual target deployment; it does not require the source runtime to remain unchanged after target deployment — approved checks occur before start/worker, subsequent facts verify the delivered immutable target artifact — cost if wrong: policy changes after deployment require a new delivery rather than reinterpreting old evidence expiry.

38. Ruling: A completed delivery keeps its prior completion and immutable evidence while approved rollback is pending or failed; related rollback events expose actual execution/node state, and successful ACK changes status to RolledBack — distinguishes validated historical delivery from current incident operation — cost if wrong: consumers must display deployment state alongside historical completion rather than treating Completed alone as current gateway health.

39. Ruling: Add source-scoped metadata pagination and read-only eligibility projections required by B9 actual UI, rather than inferring acceptance authority from front-end roles — projections reuse current evidence and real scope authorization; writes keep transactional B3/B4 checks — cost if wrong: extra public endpoints require contract coverage and budget/permission tests.

40. Ruling: Artifact list exposes only safe source-scoped metadata, authorizes all required contract visibility before counting, filters policy-bearing rows in SQL if policy visibility missing, and applies 50/default 100/cap plus10-second database budget — avoids downloading canonical contracts just to paginate or leaking denied counts — cost if wrong: corrupted manually inserted historical rows still require detail integrity validation before use.

41. Ruling: Commit B9 implementation before its real-browser proof so the browser uses an immutable source image, then commit public proof and complete task — fixed-source verification must not accidentally build uncommitted files — cost if wrong: two task commits rather than one, with task still incomplete until browser proof passes.

42. Ruling: Add scoped mapping-options endpoint because existing application detail APIs resolve project/organization scope and can exclude valid environment delegates; return only target choices, credential IDs/last4/expiry, target authorizations, sharing boolean and baseline retained IDs — matches B5 target-scope validation without widening grant authority — cost if wrong: extra read projection must track target resource lifecycle and never expose unrelated environment grants.

43. Ruling: Overview counts only promotions whose source contracts and both real environment scopes are currently readable, shows explicit Partial coverage, and returns null counts/page for contract restriction — prevents browser assembly and denied-data zeroes — cost if wrong: authorized-subset counts must not be described as complete project totals.

44. Ruling: Promotion approval actionability requires readable source contract plus conditional policy permission in the composable SQL before count/page — source-restricted approvers cannot inspect necessary evidence and actual approval guard rejects them — if wrong costs extra source read grants for existing production approvers; ordinary release behavior retained.

45. Ruling: New promotion CTA uses server-derived current acceptance and both source/target write/read scopes — UI roles cannot establish this qualification — if wrong costs read-only qualification queries; actual create still revalidates under locks.

46. Ruling: Persist credential impact within precheck JSON, optional for older evidence, expose only target app.read — a refreshed wizard must show actual precheck impact without mutable recomputation — if wrong costs JSON storage and old records require a fresh precheck.

47. Ruling: Target mapping is withheld in full when required route/cluster/app/policy reads are unavailable, and editor disabled — partial mapping must not overwrite hidden selections — if wrong costs extra read grants and re-opening the wizard.

48. Ruling: Mapping chooser bounds resources and aggregate credentials/grants to 1000 each with explicit truncated=true, UI blocks save — bounded query budget cannot silently select an incomplete credential set — if wrong costs reducing the directory or future paged selector work.

49. Ruling: Formal/recovery release trace resolves existing immutable chain and reuses source artifact authorization, nullable Restricted summary — target read does not grant source evidence — if wrong costs chain/read queries, preserving ordinary-release nullable behavior.

50. Ruling: Commit implementation before real browser proof, retain any failed proof against its actual source commit, then fix/rebuild before claiming B10 complete — image must be built from immutable git archive — if wrong costs an additional image build; keeps sourceVerified honest.

51. Ruling: Additional production backup scope is fixed two service names and three volume names, explicit metadata receipt, fresh restore context and own labels — no arbitrary service/volume extension or foreign adoption — cost if wrong: future third Gateway group requires explicit new typed backup support.

52. Ruling: Fix existing notification-volume cleanup whitelist with precise named owned volumes — default generic cleanup previously rejected its own notification fixtures — cost if wrong: named extension still requires UUID and owner guard; foreign volumes remain rejected.

53. Ruling: Add opt-in private10.249.x.0/24 per-owned-test network, strict validation and native overlap rejection, no pruning foreign networks — enables independent tests while preserving defaults — cost if wrong: callers must select a free private subnet.

54. Ruling: e2e/faults/browser aliases each run one full owned fixture with browser, all faults and cold restore — splitting standalone checks loses frozen deployment/identity context; one combined actual run proves all three — cost if wrong: selecting any alias incurs full scenario duration.

55. Ruling: Request the single whole-branch review after B11 code/full isolated proof is sealed, before original upgrade and Coverage — B11 explicitly requires review before final Coverage and installation must use the reviewed fixed candidate — cost if wrong: task11 completion line is delayed until post-install validation, no unreviewed original upgrade.

56. Ruling: Existing table snapshot helper collapses composite-primary-key rows under a null id — capture all real primary-key components and old-column projections in a private installation baseline; require unique count/hash for every old row — cost if wrong: extra read-only queries and private metadata, no weakened old-data preservation.

57. Ruling: Preserve every historical maintenance wrapper and add a dedicated hash-pinned delivery entry for report-aware backup/lifecycle — original fixed tooling predates the report volume and must not silently omit it — cost if wrong: users must use the new entry for current delivery maintenance; old entries retained byte-for-byte.

58. Ruling: Capture full composite-key cold-restore baseline before starting cloned applications and compare exact business rows to original; preserve that cold baseline across upgrade/conditional-old-software/forward phases — append history after backup cannot be required in an earlier restore — cost if wrong: cold-baseline and original-upgrade-baseline remain separate labelled evidence, not merged totals.

59. Ruling: Reviewer declined original4192 installed compatibility — retain localInstalled=false until fixed reviewed candidate migrates and original data/identity/SSO/dualGateway/observation/notification checks actually pass — cost if wrong: no validated installation claim.

60. Ruling: Reviewer declined enterprise DNS/TLS/LB/upstream/performance/HA — preserve productionAcceptance=false and local-isolated proof boundary — cost if wrong: enterprise acceptance remains outstanding despite local completion.

61. Ruling: Reviewer declined manual report truth/automatic test engine/arbitrary Stage — retain approved manual registration scope, fix platform-known deployment-time contradictions — cost if wrong: human report claims still require independent human review, not automatic external certification.

62. Ruling: Reviewer declined unconditional old binary downgrade after new facts — prove old software only on disposable upgraded schema before new delivery facts, then move forward; never restore old DB over new facts — cost if wrong: future new-data downgrade requires its own validation, emergency path is fix-forward.

63. Ruling: Reviewer declined repeating18-image visual review — executor has actually viewed all18 and validator checks hashes; no claim reviewer re-inspected images — cost if wrong: visual QA remains author-performed, independent reviewer attests code/evidence consistency only.

64. Ruling: Original bound LOCAL_DEMO is genuinely production at4/4; do not relabel it to generate a source artifact — keep its project Legacy and create a separate synthetic project with TEST/PROD address metadata and a Legacy connection, without adding production nodes or publishing original business — cost if wrong: original installation demonstrates actual new read/write/UI plus old flow preservation; full production promotion remains the separately labelled four-Gateway isolated proof.

65. Ruling: Preserve the failed fixed-source attempt, finish/release only this plan’s rehearsal resources, rerun the full same-source runtime scenario and native backup checks — do not prune foreign networks or change reviewed product code — cost if wrong: extra full scenario duration, honest failed-attempt history.

66. Ruling: Allow only the single actual synthetic connection command receipt after verifying exact original actor, project scope, operation, request hash and returned policy/source/target/revision; preserve every previous receipt and refuse other extra rows; resume after strict compare without recreating project or reinstalling tooling — cost if wrong: extra metadata verification, no broad append exemption or silent deletion.

67. Ruling: Explicitly execute existing immutable migrator --seed-catalog under original owner guard after migration; verify precisely3new catalog entries/3PlatformAdmin links and0other-default links, every old permission/link/account/secret unchanged, then retain a fresh report-aware cold backup — authorized planned catalog activation without password bootstrap — cost if wrong: future maintenance upgrades must include explicit catalog refresh; no blanket role grant or reset.
