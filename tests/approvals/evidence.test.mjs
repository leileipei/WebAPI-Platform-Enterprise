import {readFileSync} from 'node:fs';
import {deflateSync} from 'node:zlib';
import test from 'node:test';import assert from 'node:assert/strict';import {createHash} from 'node:crypto';
import {validateApprovalEvidence,requiredApprovalChecks,requiredApprovalUiActions} from '../../scripts/approvals/evidence.mjs';
const revision='a'.repeat(40),imageId='sha256:'+'b'.repeat(64),hash=value=>createHash('sha256').update(value).digest('hex');
// Unit samples exercise the evidence gate only; they are not real browser acceptance.
function png(width,seed){const chunk=(kind,data)=>{const body=Buffer.concat([Buffer.from(kind),data]),out=Buffer.alloc(body.length+8);out.writeUInt32BE(data.length);body.copy(out,4);let crc=0xffffffff;for(const byte of body){crc^=byte;for(let bit=0;bit<8;bit++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);}out.writeUInt32BE((crc^0xffffffff)>>>0,out.length-4);return out;};const header=Buffer.alloc(13);header.writeUInt32BE(width);header.writeUInt32BE(900,4);header[8]=8;header[9]=2;const raw=Buffer.alloc(900*(width*3+1),seed);for(let row=0;row<900;row++)raw[row*(width*3+1)]=0;return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]),chunk('IHDR',header),chunk('IDAT',deflateSync(raw)),chunk('IEND',Buffer.alloc(0))]);}
function sample(){const files=requiredApprovalUiActions.map((id,i)=>{const content=png(i===1?1280:1440,i+1);return{path:id+'.png',content,sha256:hash(content)};});return{files,proof:{sourceRevision:revision,imageId,clone:{actual:true,ownerVerified:true,projectName:'webapi-enterprise-local-test-10000000-0000-4000-8000-000000000001',consolePort:4282},checks:requiredApprovalChecks.map(id=>({id,passed:true,sourceRevision:revision,imageId,evidence:{serviceResponse:true,expected:1,actual:1}})),pagination:{sourceRevision:revision,imageId,total:51,pageSize:50,secondPageId:'release-51',pendingMine:1,actionableId:'release-51',countsFromService:true},environments:['env-one','env-two'],ui:{sourceRevision:revision,imageId,viewports:[1440,1280],actions:requiredApprovalUiActions.map((id,i)=>({id,passed:true,sourceRevision:revision,imageId,screenshot:id+'.png',viewport:i===1?1280:1440})),screenshots:files.map(f=>f.path)},manifest:files.map(({path,sha256})=>({path,sha256}))}};}
test('validEvidenceReturnsFixedAcceptanceShape',()=>{const {proof,files}=sample();assert.deepEqual(validateApprovalEvidence(proof,files,{revision,imageId}),{passed:true,errors:[]});});
for(const [name,change]of [
 ['wrongInternalRevision',(p)=>{p.pagination.sourceRevision='c'.repeat(40);}],
 ['missingServiceCounts',(p)=>{p.pagination.countsFromService=false;}],
 ['fakeSecondPage',(p)=>{p.pagination.total=50;}],
 ['wrongActualAction',(p)=>{p.checks[0].evidence.actual=9;}],
 ['oldScreenshotRelabelled',(p)=>{p.ui.actions[0].sourceRevision='c'.repeat(40);}],
 ['foreignConsole',(p)=>{p.clone.consolePort=4192;}],
 ['missingUiAction',(p)=>{p.ui.actions.pop();}]
])test(name,()=>{const {proof,files}=sample();change(proof);assert.throws(()=>validateApprovalEvidence(proof,files,{revision,imageId}));});
test('fakeOrModifiedScreenshotRejected',()=>{const {proof,files}=sample();files[0].content=Buffer.from('fake screenshot');files[0].sha256=hash(files[0].content);proof.manifest[0].sha256=files[0].sha256;assert.throws(()=>validateApprovalEvidence(proof,files,{revision,imageId}));});
test('onePixelOrReusedScreenshotCannotProveDesktopActions',()=>{const {proof,files}=sample();files[0].content=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jhD0AAAAASUVORK5CYII=','base64');files[0].sha256=hash(files[0].content);proof.manifest[0].sha256=files[0].sha256;assert.throws(()=>validateApprovalEvidence(proof,files,{revision,imageId}));});
test('oneScreenshotCannotBeReusedForDifferentActions',()=>{const {proof,files}=sample();proof.ui.actions[1].screenshot=proof.ui.actions[0].screenshot;assert.throws(()=>validateApprovalEvidence(proof,files,{revision,imageId}));});
test('nativeDesktopJpegIsAcceptedWithoutReencoding',()=>{const {proof,files}=sample();files[0].content=readFileSync(new URL('../../docs/evidence/approvals/65a656f/inbox-1440.jpg',import.meta.url));files[0].sha256=hash(files[0].content);proof.manifest[0].sha256=files[0].sha256;assert.deepEqual(validateApprovalEvidence(proof,files,{revision,imageId}),{passed:true,errors:[]});});
