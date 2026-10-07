const bytes=text=>new TextEncoder().encode(text??'').length;
const limits={maxDocumentBytes:2*1024*1024,maxBundleBytes:4*1024*1024,maxResources:16};
function active(input){return {mode:input.mode,format:input.format??'auto',sourceText:input.mode==='url'?undefined:input.text,sourceUrl:input.mode==='url'?input.url:undefined,files:input.mode==='url'?undefined:(input.files??[]).map(({name,content,format})=>({name,content,format}))};}
export function importIdentity(input){return JSON.stringify([input.projectId,input.environmentId,input.clusterId,input.authority,input.policyRevision,active(input)]);}
export function invalidatePreview(state,input){const identity=importIdentity(input);return state.identity===identity?state:{...state,input,identity,preview:undefined,targets:[],result:undefined,issues:[],recovery:''};}
export function canCommitPreview(state,now=Date.now()){
 const preview=state.preview;if(!preview||!state.authorized||state.busy||state.identity!==importIdentity(state.input)||!Number.isFinite(Date.parse(preview.expiresAt))||Date.parse(preview.expiresAt)<=now||state.input.policyRevision!==undefined&&state.input.policyRevision!==preview.sourcePolicyRevision||!state.targets?.length)return false;
 const seen=new Set();return state.targets.every(target=>{const operation=preview.operations.find(x=>x.operationId===target.operationId&&x.supported);if(!operation||seen.has(target.operationId))return false;seen.add(target.operationId);try{mapImportTarget(operation,target);return true;}catch{return false;}});
}
const guid=value=>typeof value==='string'&&/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
export function mapImportTarget(operation,target){
 if(!operation?.supported)throw new Error('该 Operation 不可导入，请处理来源限制后重新预览。');
 const result={operationId:operation.operationId,newApiCode:target.newApiCode||null,newApiName:target.newApiName||null,apiId:target.apiId||null,existingVersionId:target.existingVersionId||null,existingRouteId:target.existingRouteId||null,version:target.version||'1.0.0',expectedRevision:target.expectedRevision?Number(target.expectedRevision):null,expectedRouteRevision:target.expectedRouteRevision?Number(target.expectedRouteRevision):null};
 for(const key of ['apiId','existingVersionId','existingRouteId'])if(result[key]&&!guid(result[key]))throw new Error('目标 ID 必须为有效 UUID。');
 if(result.existingRouteId&&!result.existingVersionId)throw new Error('覆盖路由必须指定已有版本。');
 if(result.existingVersionId){result.apiId=null;result.newApiCode=null;result.newApiName=null;}
 if(result.apiId){result.newApiCode=null;result.newApiName=null;}else if(!result.existingVersionId&&(!result.newApiCode?.trim()||!result.newApiName?.trim()))throw new Error('新 API 编码与名称不能为空。');
 for(const [id,revision]of [['existingVersionId','expectedRevision'],['existingRouteId','expectedRouteRevision']])if(result[id]&&(!Number.isSafeInteger(result[revision])||result[revision]<1))throw new Error('覆盖目标须提供有效的最新修订。');
 return result;
}
function safeName(name){if(typeof name!=='string'||!name||name.length>2048||name!==name.trim()||/[\\:%?#\x00-\x1f\x7f]/.test(name)||name.startsWith('/')||name.split('/').some(x=>!x||x==='.'||x==='..'))throw new Error('文件名须为安全的相对逻辑路径。');if(!/\.(json|ya?ml)$/i.test(name))throw new Error('仅接收 JSON / YAML 文件，不接收 ZIP。');}
export function previewRequest(input,budget=limits){
 if(!['text','file','url'].includes(input.mode))throw new Error('请选择来源方式。');
 const request={projectId:input.projectId,environmentId:input.environmentId,clusterId:input.clusterId,format:input.format??'auto'};
 if(input.mode==='url'){let url;try{url=new URL(input.url);}catch{throw new Error('来源 URL 无效。');}if(!['http:','https:'].includes(url.protocol)||url.username||url.password||url.search||url.hash)throw new Error('URL 仅允许 HTTP(S)，不得含凭据、查询串或根片段。');return {...request,sourceUrl:input.url};}
 if(!input.text?.trim())throw new Error('请提供根 JSON / YAML 原文或文件。');if(bytes(input.text)>budget.maxDocumentBytes)throw new Error('根文档超过字节预算。');
 const files=input.files??[],seen=new Set();if(files.length>=budget.maxResources)throw new Error('附带文件数量超过预算。');let total=bytes(input.text);
 for(const file of files){safeName(file.name);if(seen.has(file.name))throw new Error('附带文件逻辑名称重复。');seen.add(file.name);const length=bytes(file.content);if(length>budget.maxDocumentBytes)throw new Error('附带文件超过单份字节预算。');total+=length;}
 if(total>budget.maxBundleBytes)throw new Error('根文档与附带文件超过总字节预算。');return {...request,sourceText:input.text,files};
}
export async function readImportFiles(files,budget=limits){
 const list=Array.from(files),seen=new Set();if(list.length>=budget.maxResources)throw new Error('附带文件数量超过预算。');let total=0;
 for(const file of list){safeName(file.webkitRelativePath||file.name);const name=file.webkitRelativePath||file.name;if(seen.has(name))throw new Error('文件逻辑名称重复。');seen.add(name);if(file.size>budget.maxDocumentBytes)throw new Error('文件超过单份字节预算。');total+=file.size;}
 if(total>budget.maxBundleBytes)throw new Error('文件超过总字节预算。');const result=[];total=0;
 for(const file of list){const content=await file.text();const length=bytes(content);if(length>budget.maxDocumentBytes||(total+=length)>budget.maxBundleBytes)throw new Error('文件原文超过字节预算。');const name=file.webkitRelativePath||file.name;result.push({name,content,format:/\.json$/i.test(name)?'json':'yaml'});}return result;
}
export function recoverImportFailure(state,error){
 const recovery=error.status===412?'目标修订已变化；映射与输入已保留，请读取最新目标修订后修改映射。':error.status===403||error.status===404?'资源或权限已失效；输入已保留，请刷新会话与目标范围。':error.status===409?'预览、政策或目标冲突已变化，请重新预览并核对映射。':'请求未完成，输入已保留；可重试或修复诊断。';
 return {...state,busy:false,recovery,issues:error.issues??[],...([403,404,409].includes(error.status)?{preview:undefined,targets:[]}:{}),...([403,404].includes(error.status)?{authorized:false}:{})};
}

export function acceptImportPreview(state,input,preview,requestSequence){
 if(state.requestSequence!==requestSequence||importIdentity(state.input)!==importIdentity(input))return state;
 return {...state,input,identity:importIdentity(input),preview,targets:preview.operations.filter(x=>x.supported).map(x=>mapImportTarget(x,{newApiCode:x.suggestedCode,newApiName:x.summary||x.operationId,version:'1.0.0'})),result:undefined,issues:preview.issues??[],busy:false,recovery:''};
}
