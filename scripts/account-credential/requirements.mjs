import {createHash} from 'node:crypto';
import {readFileSync,lstatSync,realpathSync} from 'node:fs';
import {resolve,relative,isAbsolute,sep,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';

const statuses=['已关闭','部分','缺失','待复验','经确认延期'];
const identityFields=['id','page','kind','originalRequirement','originalParagraphIndexZeroBased'];
const sha=value=>typeof value==='string'&&/^[a-f0-9]{64}$/.test(value);
const revision=value=>typeof value==='string'&&/^[a-f0-9]{40}$/.test(value);
const timestamp=value=>typeof value==='string'&&/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?Z$/.test(value)&&Number.isFinite(Date.parse(value));
const text=value=>typeof value==='string'&&value.trim().length>0;
function requireThat(condition,message){if(!condition)throw new Error(`Invalid requirements closeout: ${message}`);}
function safeRelative(path){return text(path)&&!isAbsolute(path)&&!path.includes('\\')&&path.split('/').every(p=>p!==''&&p!=='.'&&p!=='..');}
function bytesOf(artifacts,path){const artifact=artifacts instanceof Map?artifacts.get(path):artifacts?.[path];requireThat(artifact?.bytes instanceof Uint8Array,`missing file ${path}`);return artifact.bytes;}
function verifyFile(artifacts,reference){
 requireThat(reference&&safeRelative(reference.path)&&sha(reference.sha256),'unsafe path or invalid hash');
 const bytes=bytesOf(artifacts,reference.path);requireThat(createHash('sha256').update(bytes).digest('hex')===reference.sha256,`hash mismatch ${reference.path}`);return bytes;
}
function document(bytes,path){try{return JSON.parse(Buffer.from(bytes).toString('utf8'));}catch{throw new Error(`Invalid requirements closeout: invalid evidence JSON ${path}`);}}
function strings(values){return Array.isArray(values)&&new Set(values).size===values.length&&values.every(text);}

/** This gate checks traceability, identity and integrity; scenario execution is gated by the C5 acceptance verifier. */
export function validateRequirementsCloseout(source,closeout,artifacts){
 requireThat(source&&Array.isArray(source.requirements)&&Array.isArray(source.pages),'missing source index');
 requireThat(closeout?.schemaVersion===1&&closeout.originalDocumentSha256===source.originalDocument.sha256,'source document mismatch');
 requireThat(Array.isArray(closeout.requirements)&&closeout.requirements.length===source.requirements.length,'unit count mismatch');
 const originals=new Map(source.requirements.map(u=>[u.id,u]));requireThat(originals.size===source.requirements.length,'duplicate source unit');
 const pages=new Set(source.pages.map(p=>p.page));requireThat(pages.size===source.pages.length,'duplicate source page');
 for(const page of source.pages){const ids=source.requirements.filter(u=>u.page===page.page).map(u=>u.id).sort();requireThat(JSON.stringify(ids)===JSON.stringify([...page.requirements].sort()),`source page mapping ${page.page}`);}
 const seen=new Set(),statusCounts=Object.fromEntries(statuses.map(s=>[s,0]));
 for(const unit of closeout.requirements){
  const original=originals.get(unit?.id);requireThat(original&&!seen.has(unit.id),`unknown/duplicate unit ${unit?.id}`);seen.add(unit.id);
  requireThat(identityFields.every(field=>unit[field]===original[field]),`original requirement changed ${unit.id}`);
  requireThat(statuses.includes(unit.status)&&text(unit.reason),`status or reason ${unit.id}`);statusCounts[unit.status]++;
  requireThat(Array.isArray(unit.implementationPaths)&&strings(unit.testNames)&&Array.isArray(unit.evidenceRefs),`missing evidence fields ${unit.id}`);
  requireThat(unit.sourceRevision===null||revision(unit.sourceRevision),`invalid source revision ${unit.id}`);
  requireThat(unit.runtimeOwner===null||text(unit.runtimeOwner),`invalid runtime owner ${unit.id}`);
  requireThat(unit.verifiedAt===null||timestamp(unit.verifiedAt),`invalid verified time ${unit.id}`);
  for(const ref of unit.implementationPaths)verifyFile(artifacts,ref);
  const proofs=unit.evidenceRefs.map(ref=>({path:ref.path,proof:document(verifyFile(artifacts,ref),ref.path)}));
  requireThat(new Set(unit.implementationPaths.map(x=>x.path)).size===unit.implementationPaths.length&&new Set(unit.evidenceRefs.map(x=>x.path)).size===unit.evidenceRefs.length,`duplicate reference ${unit.id}`);
  if(unit.status==='已关闭'){
   requireThat(revision(unit.sourceRevision)&&text(unit.runtimeOwner)&&timestamp(unit.verifiedAt)&&unit.implementationPaths.length>0&&unit.testNames.length>0,`closed metadata missing ${unit.id}`);
   const matching=proofs.map(x=>x.proof).filter(p=>p.sourceRevision===unit.sourceRevision&&p.runtimeOwner===unit.runtimeOwner&&timestamp(p.verifiedAt)&&Date.parse(p.verifiedAt)<=Date.parse(unit.verifiedAt));
   const tests=matching.filter(p=>p.kind==='tests'&&Number.isSafeInteger(p.passed)&&p.passed>0&&p.failed===0&&p.skipped===0&&strings(p.testNames));
   requireThat(unit.testNames.every(name=>tests.some(p=>p.testNames.includes(name))),`missing passing named tests ${unit.id}`);
   requireThat(matching.some(p=>p.kind==='runtime'&&p.result==='passed'&&text(p.scenario)&&Array.isArray(p.requirementIds)&&p.requirementIds.includes(unit.id)),`missing runtime scenario ${unit.id}`);
   // Every original unit describes one of the 40 UI pages. Screenshots alone are insufficient, but page evidence is required.
   const ui=matching.filter(p=>p.kind==='ui'&&p.viewport?.width===1440&&p.viewport?.height>=600&&p.requirementIds?.includes(unit.id)&&Array.isArray(p.screenshots)&&p.screenshots.length>0);
   requireThat(ui.length>0,`missing 1440px page evidence ${unit.id}`);for(const proof of ui)for(const screenshot of proof.screenshots)verifyFile(artifacts,screenshot);
  }
  if(unit.status==='经确认延期')requireThat(proofs.some(({proof:p})=>p.kind==='user-decision'&&p.actor==='user'&&p.decision==='defer'&&text(p.text)&&timestamp(p.recordedAt)&&Array.isArray(p.requirementIds)&&p.requirementIds.includes(unit.id)),`missing recorded user deferral ${unit.id}`);
 }
 requireThat(seen.size===originals.size,'missing original units');return {pages:pages.size,units:seen.size,statusCounts};
}

function readContained(root,path){
 requireThat(safeRelative(path),'unsafe artifact path');const base=realpathSync(root),target=resolve(base,path);const rel=relative(base,target);requireThat(rel!==''&&!rel.startsWith(`..${sep}`)&&rel!=='..'&&!isAbsolute(rel),'artifact escaped root');
 let current=base;for(const part of path.split('/')){current=resolve(current,part);requireThat(!lstatSync(current).isSymbolicLink(),'symlink artifact');}
 requireThat(lstatSync(target).isFile()&&lstatSync(target).size<=32*1024*1024,'invalid artifact size/type');return readFileSync(target);
}
export function validateRequirementsFile(closeoutFile,evidenceRoot){
 const repo=resolve(dirname(fileURLToPath(import.meta.url)),'../..');
 const source=JSON.parse(readFileSync(resolve(repo,'docs/evidence/work-package4/2026-10-15-readiness/original-40-page-requirements.json'),'utf8'));
 const closeout=JSON.parse(readFileSync(closeoutFile,'utf8')),artifacts=new Map();
 for(const unit of closeout.requirements??[]){
  for(const ref of unit.implementationPaths??[])artifacts.set(ref.path,{bytes:readContained(repo,ref.path)});
  for(const ref of unit.evidenceRefs??[]){
   const bytes=readContained(evidenceRoot,ref.path);artifacts.set(ref.path,{bytes});const proof=document(bytes,ref.path);
   if(proof.kind==='ui')for(const image of proof.screenshots??[])artifacts.set(image.path,{bytes:readContained(evidenceRoot,image.path)});
  }
 }
 return validateRequirementsCloseout(source,closeout,artifacts);
}
if(process.argv[1]&&import.meta.url===pathToFileURL(resolve(process.argv[1])).href){
 try{requireThat(process.argv.length===4,'usage: requirements.mjs <closeout.json> <evidence-root>');process.stdout.write(JSON.stringify(validateRequirementsFile(process.argv[2],process.argv[3]))+'\n');}
 catch(error){process.stderr.write(`${error.message}\n`);process.exitCode=1;}
}
