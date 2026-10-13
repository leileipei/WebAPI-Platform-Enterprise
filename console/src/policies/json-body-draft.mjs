import {parseSchemaDocument} from '../contracts/schema-state.mjs';
const fields=['requestHeaders','responseHeaders','path','query','requestBody','responseBody'];
export function parseTransformDraft(source){
 const locations={},draft=parseSchemaDocument(source,{locations});
 if(!draft||Array.isArray(draft)||typeof draft!=='object')throw new Error('Transform 配置须为对象');
 for(const direction of ['requestBody','responseBody'])for(const [index,operation] of (draft[direction]?.operations??[]).entries()){
  const position=locations[`/${direction}/operations/${index}/value`];
  if(position){operation.valueRaw=source.slice(position.start,position.end);delete operation.value;}
 }
 return draft;
}
function valueSource(operation){
 if(Object.hasOwn(operation,'valueRaw')){
  if(typeof operation.valueRaw!=='string'||!operation.valueRaw.trim())throw new Error('请填写 value JSON，null 也须明确输入');
  parseSchemaDocument('{"value":'+operation.valueRaw+'}');return operation.valueRaw.trim();
 }
 if(Object.hasOwn(operation,'value'))return JSON.stringify(operation.value);
 throw new Error('新增 / 替换需要明确 value，null 与缺字段不同');
}
function bodySource(body,direction){
 if(body===null)return 'null';if(!body||!Array.isArray(body.operations)||body.operations.length<1||body.operations.length>32)throw new Error('每方向需要 1–32 条有序操作');
 const selector=direction==='requestBody'?'methods':'statusCodes';
 const operations=body.operations.map(operation=>{
  if(!['add','replace','remove','copy','move'].includes(operation.op))throw new Error('不支持此 JSON 操作');
  const names=['op','path',...(['add','replace'].includes(operation.op)?['value','valueRaw']:['copy','move'].includes(operation.op)?['from']:[])];
  if(Object.keys(operation).some(key=>!names.includes(key)))throw new Error('JSON 操作包含未知字段');
  let json='{"op":'+JSON.stringify(operation.op)+',"path":'+JSON.stringify(operation.path);
  if(['add','replace'].includes(operation.op))json+=',"value":'+valueSource(operation);
  else if(['copy','move'].includes(operation.op)){if(typeof operation.from!=='string')throw new Error('复制 / 移动需要 from Pointer');json+=',"from":'+JSON.stringify(operation.from);}
  return json+'}';
 });
 return '{'+JSON.stringify(selector)+':'+JSON.stringify(body[selector])+',"maxBodyBytes":'+JSON.stringify(body.maxBodyBytes)+',"operations":['+operations.join(',')+']}';
}
export function serializeTransformDraft(draft){
 if(Object.keys(draft).some(key=>!fields.includes(key))||fields.some(key=>!Object.hasOwn(draft,key)))throw new Error('Transform 配置包含未知或缺失字段');
 const source='{'+fields.map(key=>JSON.stringify(key)+':'+(['requestBody','responseBody'].includes(key)?bodySource(draft[key],key):JSON.stringify(draft[key]))).join(',')+'}';
 parseSchemaDocument(source);if(new TextEncoder().encode(source).length>32768)throw new Error('策略配置不能超过 32KiB');return source;
}
export function policyConfigSource(draft){return draft.type==='transform'?serializeTransformDraft(draft.config):JSON.stringify(draft.config);}
export function parsePolicyConfig(type,source){return type==='transform'?parseTransformDraft(source):JSON.parse(source);}
export function previewReducer(state,event){switch(event.type){
 case 'context':return {...state,scope:event.scope,direction:event.direction,epoch:(state.epoch??0)+1,sampleJson:'',outputJson:'',error:'',busy:false};
 case 'sample':return {...state,sampleJson:event.value,outputJson:'',error:'',epoch:(state.epoch??0)+1,busy:false};
 case 'started':return {...state,epoch:event.epoch,busy:true,outputJson:'',error:''};
 case 'completed':return event.epoch===state.epoch?{...state,busy:false,outputJson:event.outputJson,error:''}:state;
 case 'failed':return event.epoch===state.epoch?{...state,busy:false,error:event.error,outputJson:''}:state;
 default:return state;
}}
