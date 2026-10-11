import test from 'node:test';
import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
const digest=b=>createHash('sha256').update(b).digest('hex');
const revision='a'.repeat(40),image='sha256:'+'b'.repeat(64),hash='c'.repeat(64);
function fixture(){const files={'cp.dll':Buffer.from('control-plane'),'gateway.dll':Buffer.from('gateway'),'report.txt':Buffer.from('actual private report'),'console.png':Buffer.from('image')};const sums=Object.fromEntries(Object.entries(files).map(([n,b])=>[n,digest(b)]));const node=(name,env)=>({nodeId:name,instanceId:name+'-process',environmentId:env,configVersion:2,deploymentSequence:2,snapshotHash:hash,acknowledged:true});return {files,proof:{schemaVersion:1,classification:'actual-isolated-cross-environment-promotion',sourceRevision:revision,sourceVerified:true,isolatedAcceptance:true,productionAcceptance:false,build:{sourceRevision:revision,imageId:image,observedImageId:image,applications:{'cp.dll':sums['cp.dll'],'gateway.dll':sums['gateway.dll']},observedApplications:{'cp.dll':sums['cp.dll'],'gateway.dll':sums['gateway.dll']}},source:{environmentId:'TEST',publicUrl:'http://127.0.0.1:4100',snapshotHash:hash,artifactHash:'d'.repeat(64),nodes:[node('test-a','TEST'),node('test-b','TEST')]},target:{environmentId:'PROD',publicUrl:'https://127.0.0.1:4101/edge',snapshotHash:'e'.repeat(64),nodes:[node('prod-a','PROD'),node('prod-b','PROD')].map(n=>({...n,snapshotHash:'e'.repeat(64)}))},actors:{applicant:'one',acceptor:'two',publisher:'one',approvers:['three','four'],verifier:'five'},acceptance:{status:'Accepted',acceptedBy:'two'},approvals:[{status:'Approved',actorId:'three',stepOrder:1},{status:'Approved',actorId:'four',stepOrder:2}],verifications:['EntryConnectivity','AuthenticationAuthorization','CriticalBusinessCall'].map(type=>({type,result:'Passed',actorId:'five',configVersion:2,deploymentSequence:2,contextHash:hash})),responses:[...['test-a','test-b'].map(nodeId=>({nodeId,environmentId:'TEST',status:200,backendId:'backend-a'})),...['prod-a','prod-b'].map(nodeId=>({nodeId,environmentId:'PROD',status:200,backendId:'backend-b'})),{environmentId:'PROD',status:200,tlsVerified:true,backendId:'backend-b'},{environmentId:'PROD',status:401,rejectedSourceCredential:true}],faults:Object.fromEntries(['partialAck','timeout','workerRestart','queuedRevocation','queuedAuthority','policyRevision','sharedApplication','verificationFailure','approvedRollback'].map(n=>[n,{passed:true,actual:true}])),backupRestore:{passed:true,actual:true,freshOwner:true,reportId:'report',reportHash:sums['report.txt'],originalHash:sums['report.txt'],restoredHash:sums['report.txt'],restoredReportPath:'report.txt',metadataPreserved:true},screenshots:[{path:'console.png',sha256:sums['console.png'],width:1440,visuallyInspected:true},{path:'console.png',sha256:sums['console.png'],width:1280,visuallyInspected:true}],cleanup:{containersRemaining:0,volumesRemaining:0,secretFilesRemaining:0}}};}
async function validate(f){const {validatePromotionProof}=await import('../../scripts/delivery/promotion-evidence.mjs');return validatePromotionProof(f.proof,f.files);}
test('complete actual observations and matching files pass',async()=>assert.equal((await validate(fixture())).passed,true));
for(const [name,mutate] of [
 ['missing TEST actual request',p=>p.responses=p.responses.filter(r=>r.environmentId!=='TEST')],
 ['missing PROD actual response',p=>p.responses=p.responses.filter(r=>r.environmentId!=='PROD')],
 ['all ACK with no business facts',p=>p.verifications=[]],
 ['self verification despite different role',p=>{p.actors.verifier=p.actors.publisher;for(const v of p.verifications)v.actorId=p.actors.publisher;}],
 ['duplicate production approvers',p=>p.actors.approvers[1]=p.actors.approvers[0]],
 ['wrong source revision',p=>p.build.sourceRevision='f'.repeat(40)],
 ['wrong observed image',p=>p.build.observedImageId='sha256:'+'f'.repeat(64)],
 ['wrong binary digest',p=>p.build.observedApplications['cp.dll']='f'.repeat(64)],
 ['missing report restore',p=>delete p.backupRestore],
 ['report hash mismatch',p=>p.backupRestore.restoredHash='f'.repeat(64)],
 ['secret bearing proof',p=>p.target.apiKey='never-disclose-this'],
 ['foreign target node scope',p=>p.target.nodes[0].environmentId='TEST'],
 ['same node process reused across environments',p=>p.target.nodes[0].instanceId=p.source.nodes[0].instanceId],
 ['same upstream',p=>{for(const r of p.responses)if(r.environmentId==='PROD'&&r.status===200)r.backendId='backend-a';}],
 ['missing fault observation',p=>delete p.faults.queuedRevocation],
 ['uninspected screenshot',p=>p.screenshots[0].visuallyInspected=false],
 ['business verification bound to wrong deployment',p=>p.verifications[0].deploymentSequence=3],
 ['target node snapshot differs from deployed snapshot',p=>p.target.nodes[0].snapshotHash='f'.repeat(64)],
 ['same approver in both actual approval facts',p=>p.approvals[1].actorId=p.approvals[0].actorId],
 ['nonzero cleanup',p=>p.cleanup.volumesRemaining=1]
])test(name+' is rejected',async()=>{const f=fixture();mutate(f.proof);const r=await validate(f);assert.equal(r.passed,false);assert.ok(r.errors.length>0);});
test('changed actual report file is rejected',async()=>{const f=fixture();f.files['report.txt']=Buffer.from('wrong content');assert.equal((await validate(f)).passed,false);});
