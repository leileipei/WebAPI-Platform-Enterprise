import {createHash} from 'node:crypto';
import {inflateSync} from 'node:zlib';
const sha=/^[a-f0-9]{40}$/,hash=/^[a-f0-9]{64}$/,image=/^sha256:[a-f0-9]{64}$/,uuid=/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/;
const required=['metadata_no_network','proxy_prefix_call','dual_gateway_ack','history_immutable','legacy_client_preserve','document_copy_preserve','authorization_projection','rollback_recovery'];
const digest=b=>createHash('sha256').update(b).digest('hex');
function crc32(b){let crc=0xffffffff;for(const byte of b){crc^=byte;for(let j=0;j<8;j++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);}return (crc^0xffffffff)>>>0;}
function validPng(bytes,width,height){
 try{if(!Buffer.isBuffer(bytes)||bytes.length<100||!bytes.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10])))return false;let offset=8,header=false,end=false,channels=0;const compressed=[];while(offset<bytes.length){if(offset+12>bytes.length)return false;const length=bytes.readUInt32BE(offset),next=offset+12+length;if(next>bytes.length)return false;const type=bytes.toString('ascii',offset+4,offset+8),data=bytes.subarray(offset+8,offset+8+length);if(crc32(bytes.subarray(offset+4,next-4))!==bytes.readUInt32BE(next-4))return false;if(type==='IHDR'){if(header||offset!==8||length!==13||data.readUInt32BE(0)!==width||data.readUInt32BE(4)!==height||data[8]!==8||![2,6].includes(data[9])||data[10]||data[11]||data[12])return false;header=true;channels=data[9]===6?4:3;}if(type==='IDAT')compressed.push(data);if(type==='IEND'){if(length||next!==bytes.length)return false;end=true;}offset=next;}if(!header||!end||width<320||height<240||width*height>16000000)return false;const decoded=inflateSync(Buffer.concat(compressed),{maxOutputLength:(width*channels+1)*height+1});if(decoded.length!==(width*channels+1)*height)return false;for(let y=0;y<height;y++)if(decoded[y*(width*channels+1)]>4)return false;return true;}catch{return false;}
}
function hasCredential(value){if(!value||typeof value!=='object')return false;return Object.entries(value).some(([key,v])=>/^(password|apiKey|secret|authorization|cookie|token|credential)$/i.test(key)||hasCredential(v));}
export function validateEnvironmentAccessProof(proof,files={}){
 const errors=[];const require=(ok,message)=>{if(!ok)errors.push(message);};
 if(!proof||typeof proof!=='object')return{passed:false,errors:['Missing proof']};
 require(proof.schemaVersion===1&&proof.classification==='actual-isolated-environment-access','Wrong proof classification');
 require(sha.test(proof.sourceRevision)&&proof.sourceRevision===proof.expectedRevision,'Wrong immutable source revision');
 require(image.test(proof.imageId)&&proof.image?.id===proof.imageId&&proof.image?.sourceRevision===proof.sourceRevision&&proof.image?.binaryIdentityVerified===true,'Image/binary identity unverified');
 require(/^webapi-enterprise-local-test-[a-f0-9-]{36}$/.test(proof.projectName)&&uuid.test(proof.ownerId),'Not an owned UUID fixture');
 require(Array.isArray(proof.migrations)&&['20261009010000_EnvironmentAccessAddresses','20261009011000_ReleaseAccessContexts'].every(x=>proof.migrations.includes(x)),'Missing actual applied migrations');
 require(Array.isArray(proof.checks)&&required.every(name=>proof.checks.some(c=>c.name===name&&c.passed===true&&c.actual===true))&&proof.checks.every(c=>c.passed===true&&c.actual===true),'Missing, failed or simulated checks');
 let url;try{url=new URL(proof.proxy?.url);}catch{}
 require(url?.protocol==='https:'&&!url?.username&&!url?.password&&!url?.search&&!url?.hash&&proof.proxy?.status===200&&proof.proxy?.backendPath==='/orders/qa'&&hash.test(proof.proxy?.tlsCertificateSha256),'Missing actual TLS/prefix proxy request');
 const ui=proof.ui;require(ui?.sourceRevision===proof.sourceRevision&&ui?.imageId===proof.imageId&&ui?.projectName===proof.projectName&&ui?.ownerId===proof.ownerId,'Foreign UI instance');
 require(ui?.visualReviewComplete===true&&Array.isArray(ui?.checks)&&ui.checks.length>=8&&ui.checks.every(c=>c.passed===true),'Browser checks/visual review incomplete');
 require(Array.isArray(ui?.screenshots)&&ui.screenshots.length>0,'Missing screenshots');
 for(const shot of ui?.screenshots??[]){const safe=typeof shot.file==='string'&&!shot.file.startsWith('/')&&!shot.file.includes('\\')&&!shot.file.split('/').includes('..');const bytes=safe?files[shot.file]:null;require(safe&&hash.test(shot.sha256)&&Buffer.isBuffer(bytes)&&digest(bytes)===shot.sha256&&validPng(bytes,shot.width,shot.height),'Missing, forged or invalid screenshot: '+shot.file);}
 require(proof.cleanup&&['containersRemaining','volumesRemaining','secretFilesRemaining'].every(k=>proof.cleanup[k]===0),'Owned fixture cleanup incomplete');
 require(!hasCredential(proof),'Credential in public evidence');
 require(proof.sourceVerified===true&&proof.isolatedAcceptance===true&&proof.productionAcceptance===false,'Acceptance boundaries not recorded');
 return{passed:errors.length===0,errors};
}
