import fs from 'node:fs/promises';import path from 'node:path';import assert from 'node:assert/strict';import {execFile} from 'node:child_process';import {promisify} from 'node:util';
const exec=promisify(execFile);
export function validateSsoManifest(manifest){
 assert.equal(manifest.classification,'actual-uncommitted-build-source');assert.match(manifest.baseCommit,/^[a-f0-9]{40}$/);assert.match(manifest.sourceManifestHash,/^[a-f0-9]{64}$/);assert.match(manifest.toolManifestHash,/^[a-f0-9]{64}$/);assert.equal(manifest.image.sourceManifestHash,manifest.sourceManifestHash);assert.match(manifest.image.digest,/^sha256:[a-f0-9]{64}$/);assert.equal(manifest.productionDeployment,false);
 for(const name of ['realIdp','browser','visualReview','independentReview','regression','cleanup'])assert.equal(manifest.evidence[name],true,'Missing evidence: '+name);
 for(const [name,hash]of Object.entries(manifest.files)){assert.ok(!name.startsWith('/')&&!name.split('/').includes('..')&&!name.includes('\\'));assert.ok(!/(^|\/)(node_modules|\.runtime|\.secrets|\.git|\.superpowers|keyring|data-protection)(\/|$)/i.test(name));assert.ok(!/(^|\/)(review-context\.json|owner\.json|realm\.json|postgres-password|admin-password|idp-password|client-secret|secret\.key)$/i.test(name));assert.match(hash,/^[a-f0-9]{64}$/);}
 for(const suffix of ['src/WebApi.ControlPlane/Sso/OidcSchemeRegistry.cs','deploy/sso-images.lock.json','docs/evidence/sso/ui/qa.json'])assert.ok(Object.keys(manifest.files).some(name=>name.endsWith(suffix)),suffix);return true;
}
export async function verifySsoDelivery({root,packagePath}){
 const manifest=JSON.parse(await fs.readFile(packagePath+'.manifest.json'));validateSsoManifest(manifest);
 const result=await exec('python3',[root+'/scripts/package-sso.py','--verify',packagePath],{cwd:root,maxBuffer:4*1024*1024});return JSON.parse(result.stdout);
}
if(process.argv[1]&&path.resolve(process.argv[1])===path.resolve(import.meta.filename)){
 const root=path.resolve(import.meta.dirname,'../..'),index=JSON.parse(await fs.readFile(root+'/docs/evidence/sso/delivery-index.json'));console.log(JSON.stringify(await verifySsoDelivery({root,packagePath:index.packagePath}),null,2));
}
