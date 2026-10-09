import {createHash} from 'node:crypto';

const hash = /^[a-f0-9]{64}$/;
const types = ['EntryConnectivity','AuthenticationAuthorization','CriticalBusinessCall'];
const faults = ['partialAck','timeout','workerRestart','queuedRevocation','queuedAuthority','policyRevision','sharedApplication','verificationFailure','approvedRollback'];

export function validatePromotionProof(p, files = {}) {
  const errors = [];
  const require = (ok, reason) => { if (!ok) errors.push(reason); };
  const digest = name => files[name] === undefined ? null : createHash('sha256').update(files[name]).digest('hex');
  const scan = value => {
    if (!value || typeof value !== 'object') return;
    for (const [key, child] of Object.entries(value)) {
      require(!/^(password|apiKey|accessKey|secret|secretValue|clientSecret|authorization|cookie|privateKey|jwt|token)$/i.test(key), 'Secret field in proof: '+key);
      if (typeof child === 'string') require(!/-----BEGIN .*PRIVATE KEY-----|\bBearer\s+\S+|\beyJ[a-zA-Z0-9_-]+\.eyJ/.test(child), 'Secret text in proof');
      scan(child);
    }
  };
  scan(p);
  require(p?.schemaVersion === 1 && p.classification === 'actual-isolated-cross-environment-promotion', 'Unknown proof format');
  require(/^[a-f0-9]{40}$/.test(p?.sourceRevision) && p?.build?.sourceRevision === p.sourceRevision, 'Source revision mismatch');
  require(p?.sourceVerified === true && p?.isolatedAcceptance === true && p?.productionAcceptance === false, 'Acceptance classification missing');
  require(/^sha256:[a-f0-9]{64}$/.test(p?.build?.imageId) && p.build.imageId === p.build.observedImageId, 'Observed image mismatch');
  const binaries = Object.entries(p?.build?.applications ?? {});
  require(binaries.length >= 2, 'Missing application binary observations');
  for (const [name, sha] of binaries) require(hash.test(sha) && p.build.observedApplications?.[name] === sha && digest(name) === sha, 'Binary digest mismatch: '+name);
  const source = p?.source, target = p?.target;
  require(source?.environmentId && target?.environmentId && source.environmentId !== target.environmentId, 'Distinct environment scope missing');
  require(source?.publicUrl && target?.publicUrl && source.publicUrl !== target.publicUrl, 'Distinct environment entry missing');
  require(hash.test(source?.snapshotHash) && hash.test(target?.snapshotHash) && hash.test(source?.artifactHash), 'Snapshot/artifact hashes missing');
  const nodes = [];
  for (const env of [source, target]) {
    require(env?.nodes?.length === 2, 'Two actual nodes required per environment');
    for (const n of env?.nodes ?? []) {
      require(n.environmentId === env.environmentId && n.nodeId && n.instanceId && n.acknowledged === true && n.configVersion > 0 && n.deploymentSequence > 0 && hash.test(n.snapshotHash) && n.snapshotHash === env.snapshotHash, 'Actual scoped node receipt missing');
      nodes.push(n);
    }
  }
  require(new Set(nodes.map(n=>n.nodeId)).size === 4 && new Set(nodes.map(n=>n.instanceId)).size === 4, 'Gateway nodes/processes reused');
  const a = p?.actors ?? {};
  require(a.applicant && a.acceptor && a.publisher && a.verifier && a.approvers?.length === 2, 'Actors missing');
  require(a.acceptor !== a.applicant && a.verifier !== a.applicant && a.verifier !== a.publisher && new Set(a.approvers).size === 2 && a.approvers?.every(x=>x && x!==a.applicant), 'Independent actors required');
  require(p?.acceptance?.status === 'Accepted' && p.acceptance.acceptedBy === a.acceptor, 'Actual source acceptance missing');
  require(p?.approvals?.length === 2 && p.approvals.every(x=>x.status==='Approved' && a.approvers?.includes(x.actorId)) && new Set(p.approvals.map(x=>x.actorId)).size===2 && p.approvals.map(x=>x.stepOrder).sort().join(',')==='1,2', 'Actual two-level approval missing');
  for (const type of types) require(p?.verifications?.some(v=>v.type===type && v.result==='Passed' && v.actorId===a.verifier && v.configVersion===target?.nodes?.[0]?.configVersion && v.deploymentSequence===target?.nodes?.[0]?.deploymentSequence && hash.test(v.contextHash) && (p.verifications??[]).every(other=>other.contextHash===v.contextHash)), 'Independent business verification missing: '+type);
  const observations = p?.responses ?? [];
  for (const env of [source,target]) for (const n of env?.nodes ?? []) require(observations.some(r=>r.nodeId===n.nodeId && r.environmentId===env.environmentId && r.status===200 && r.backendId), 'Actual Gateway HTTP response missing');
  const upstream = env => new Set(observations.filter(r=>r.environmentId===env?.environmentId && r.status===200).map(r=>r.backendId));
  require(upstream(source).size===1 && upstream(target).size===1 && [...upstream(source)][0] !== [...upstream(target)][0], 'Different actual upstreams required');
  require(observations.some(r=>r.environmentId===target?.environmentId && r.tlsVerified===true && r.status===200), 'Verified target TLS response missing');
  require(observations.some(r=>r.environmentId===target?.environmentId && r.rejectedSourceCredential===true && r.status===401), 'Source credential isolation not observed');
  for (const name of faults) require(p?.faults?.[name]?.passed===true && p.faults[name].actual===true, 'Actual fault proof missing: '+name);
  const b = p?.backupRestore;
  require(b?.passed===true && b.actual===true && b.freshOwner===true && b.metadataPreserved===true && b.reportId && hash.test(b.reportHash) && b.reportHash===b.originalHash && b.reportHash===b.restoredHash && digest(b.restoredReportPath)===b.reportHash, 'Protected report backup/restore missing');
  const screenshots = p?.screenshots ?? [];
  for (const width of [1440,1280]) require(screenshots.some(s=>s.width===width && s.visuallyInspected===true && hash.test(s.sha256) && digest(s.path)===s.sha256), 'Inspected screenshot missing: '+width);
  require(['containersRemaining','volumesRemaining','secretFilesRemaining'].every(k=>p?.cleanup?.[k]===0), 'Owned cleanup incomplete');
  return {passed:errors.length===0,errors};
}
