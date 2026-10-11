from pathlib import Path
import json,hashlib,re,subprocess
root=Path.cwd();revision="a445534f82f11ae9a45c3b2904bd07108bd165a9";base=root/"docs/evidence/release-promotion"/revision
meta=json.loads((base/"review-fix-evidence.json").read_text());assert meta["sourceRevision"]==revision and meta["suiteTotal"]==2266
for e in meta["logs"]:
 b=(base/e["path"]).read_bytes();assert hashlib.sha256(b).hexdigest()==e["sha256"]
for file,count in [("final-full-domain.log",753),("final-full-integration.log",971),("final-full-gateway.log",142),("final-focused-green.log",6)]:
 s=(base/"tests"/file).read_text();assert re.search(r"Passed:\s+"+str(count)+r"\b",s) and re.search(r"Failed:\s+0\b",s) and re.search(r"Skipped:\s+0\b",s)
for file,count in [("final-console.log",245),("final-runtime-unit.log",153),("final-native-backup.log",2)]:
 s=(base/"tests"/file).read_text();assert f"pass {count}" in s and "fail 0" in s and "skipped 0" in s
runtime=json.loads((base/"b11-runtime/proof.json").read_text());assert runtime["sourceRevision"]==revision and runtime["phase"]=="passed" and runtime["isolatedAcceptance"] and len(runtime["screenshots"])==18
assert len(runtime["faults"])==9 and all(v["passed"] and v["actual"] for v in runtime["faults"].values());assert all(v==0 for v in runtime["cleanup"].values())
for s in runtime["screenshots"]:assert s["visuallyInspected"] and hashlib.sha256((base/"b11-runtime"/s["path"]).read_bytes()).hexdigest()==s["sha256"]
node="/Users/leo.cui/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node"
subprocess.run([node,"scripts/delivery/cli.mjs","verify",str(base/"b11-runtime")],check=True)
installed=json.loads((base/"original-install/proof.json").read_text());assert installed["phase"]=="passed" and installed["localInstalled"] and installed["sourceRevision"]==revision and not installed["productionAcceptance"]
assert installed["originalGitStatusUnchanged"] and not installed["originalDatabaseRestored"] and not installed["originalCheckoutMerged"] and not installed["pushed"]
assert installed["catalogActivation"]["newPermissions"]==3 and installed["catalogActivation"]["newRolePermissionLinks"]==3 and installed["catalogActivation"]["onlyPlatformAdminDefault"] and installed["catalogActivation"]["actualAdminIdentityRefreshed"] and not installed["catalogActivation"]["passwordReset"]
assert installed["finalOldData"]["protectedTables"]==57 and installed["finalOldData"]["protectedRows"]==891
assert len(installed["observedApplications"])==231 and installed["staticFilesVerified"]==3 and installed["observedImageId"]==installed["imageId"]
assert installed["applicationContainers"]==8 and installed["historicalMaintenanceFilesPreserved"]==353 and installed["finalProtection"]["protectedFiles"]==802 and installed["finalOldData"]["allOldRowsPreserved"]
assert installed["privateSecretBytesPreserved"] and installed["backupAfter"]["reportsIncluded"] and installed["backupAfter"]["volumes"]==12 and installed["originalProjectLegacyPreserved"]
assert installed["syntheticProject"]["mode"]=="Legacy" and not installed["syntheticProject"]["realProductionPublished"] and installed["syntheticCommandReceipt"]["actualScopeAndActorAndResponseVerified"]
assert installed["browser"]["passed"] and installed["browser"]["actualOidcBrowserLogin"] and installed["browser"]["adminActualBrowserLogin"] and installed["browser"]["browserErrors"]==[] and len(installed["browser"]["screenshots"])==10
for s in installed["browser"]["screenshots"]:assert s["visuallyInspected"] and hashlib.sha256((base/"original-install"/s["path"]).read_bytes()).hexdigest()==s["sha256"]
assert installed["rehearsal"]["passed"] and all(v==0 for v in installed["rehearsal"]["cleanup"].values())
assert subprocess.check_output(["git","diff",revision,"--","src","console","scripts","tests","deploy"])==b""
assert not list((root/".runtime/tests").iterdir())
print("B11: fixed-source suites2266/2266 zeroSkip; actual dual-environment9faults/reportrestore/18inspected images; original4192 fixed candidate installed, old data/identity/SSO/2Gateways/4sources/privatefiles/353historictools preserved,10inspected original/rehearsal images; ownedcleanup0; productionAcceptance=false.")
