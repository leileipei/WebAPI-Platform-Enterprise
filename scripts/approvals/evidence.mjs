import assert from 'node:assert/strict';
import {createHash} from 'node:crypto';
import {inflateSync} from 'node:zlib';
function screenshotSize(content){
 assert(content.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10])),'截图不是 PNG。');
 let offset=8,width=0,height=0,channels=0,ended=false;const compressed=[];
 while(offset+12<=content.length){const size=content.readUInt32BE(offset),end=offset+12+size;assert(end<=content.length,'PNG 块截断。');const chunk=content.subarray(offset+4,end-4),kind=chunk.subarray(0,4).toString();let crc=0xffffffff;for(const byte of chunk){crc^=byte;for(let bit=0;bit<8;bit++)crc=(crc>>>1)^((crc&1)?0xedb88320:0);}assert(((crc^0xffffffff)>>>0)===content.readUInt32BE(end-4),'PNG 校验错误。');
  if(kind==='IHDR'){assert(offset===8&&size===13,'PNG 头无效。');width=content.readUInt32BE(offset+8);height=content.readUInt32BE(offset+12);const bits=content[offset+16],type=content[offset+17];assert(bits===8&&[2,6].includes(type)&&content[offset+20]===0,'截图编码无效。');channels=type===6?4:3;assert([1280,1440].includes(width)&&height>=600&&height<=2000,'截图未证明桌面视口。');}
  if(kind==='IDAT')compressed.push(content.subarray(offset+8,end-4));offset=end;if(kind==='IEND'){assert(size===0&&offset===content.length,'PNG 尾无效。');ended=true;break;}
 }
 assert(ended&&compressed.length&&channels,'PNG 内容不完整。');const raw=inflateSync(Buffer.concat(compressed),{maxOutputLength:16*1024*1024});assert(raw.length===height*(width*channels+1),'PNG 像素长度不一致。');return {width,height};
}
export const requiredApprovalChecks=['cross-environment','second-page','independent-seats','read-only','self-approval','revocation','mixed-scope'];
export const requiredApprovalUiActions=['inbox-1440','filters-1280','approval-dialog-keyboard','conflict-comment','detail-return','scope-read-revoked'];
export function validateApprovalEvidence(proof,files,{revision,imageId}){
 const same=value=>assert(value?.sourceRevision===revision&&value?.imageId===imageId,'审批材料内部身份不一致。');
 assert(/^[a-f0-9]{40}$/.test(revision)&&/^sha256:[a-f0-9]{64}$/.test(imageId),'源码或镜像身份无效。');same(proof);
 assert(proof.clone?.actual&&proof.clone.ownerVerified&&/^webapi-enterprise-local-test-[a-f0-9-]{36}$/.test(proof.clone.projectName),'缺少实际 UUID 自有克隆。');
 assert(Number.isInteger(proof.clone.consolePort)&&proof.clone.consolePort>1024&&![4180,4181,4192,4193,4194,4196,4197,5090].includes(proof.clone.consolePort),'审批验收不能指向原实例。');
 assert(new Set(proof.environments).size>=2,'缺少跨环境查询。');same(proof.pagination);
 const p=proof.pagination;assert(p.countsFromService&&p.total>=51&&p.pageSize===50&&p.pendingMine===1&&p.secondPageId===p.actionableId,'缺少服务端计数或第51条可办任务。');
 for(const id of requiredApprovalChecks){const check=proof.checks?.find(c=>c.id===id);same(check);assert(check?.passed&&check.evidence?.serviceResponse&&check.evidence.actual===check.evidence.expected,'审批实际行为证据缺失：'+id);}
 same(proof.ui);assert(proof.ui.viewports.includes(1440)&&proof.ui.viewports.includes(1280),'缺少两种实际视口。');
 const indexed=new Map();for(const file of files){assert(!indexed.has(file.path)&&Buffer.isBuffer(file.content),'文件清单不完整或重复。');const digest=createHash('sha256').update(file.content).digest('hex');assert(file.sha256===digest&&proof.manifest?.some(f=>f.path===file.path&&f.sha256===digest),'文件摘要不一致。');indexed.set(file.path,file);}
 const used=new Set();for(const id of requiredApprovalUiActions){const action=proof.ui.actions?.find(a=>a.id===id);same(action);assert(action?.passed&&proof.ui.screenshots.includes(action.screenshot),'缺少实际浏览器动作：'+id);const file=indexed.get(action.screenshot);assert(file,'缺少动作截图。');assert(!used.has(file.sha256),'不同动作必须提供各自截图。');used.add(file.sha256);assert.equal(screenshotSize(file.content).width,action.viewport,'截图宽度与动作视口不一致。');}
 return {passed:true,errors:[]};
}
