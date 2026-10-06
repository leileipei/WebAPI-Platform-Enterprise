class ExactNumber {constructor(raw){this.raw=raw;}}
const owns=(value,key)=>value!==null&&typeof value==='object'&&Object.hasOwn(value,key);
const put=(object,key,value)=>Object.defineProperty(object,key,{value,writable:true,enumerable:true,configurable:true});
const escape=value=>String(value).replaceAll('~','~0').replaceAll('/','~1');
export function parseSchemaDocument(text,{locations={}}={}){
 if(new TextEncoder().encode(text).length>2*1024*1024)throw new Error('Schema JSON 超过 2 MiB。');
 let at=0,count=0;const whitespace=()=>{while(/[\t\r\n ]/.test(text[at]??'!'))at++;};
 const fail=message=>{const prefix=text.slice(0,at),line=prefix.split('\n').length,column=at-(prefix.lastIndexOf('\n')+1)+1;const error=new Error(`JSON ${message}（第 ${line} 行，第 ${column} 列）`);Object.assign(error,{line,column});throw error;};
 function string(){const start=at++;while(at<text.length){const char=text[at++];if(char==='"'){try{return JSON.parse(text.slice(start,at));}catch{fail('字符串无效');}}if(char==='\\')at++;}fail('字符串未结束');}
 function value(pointer,depth){
  whitespace();if(depth>64||++count>50000)fail('深度或节点超过预算');const start=at;let result;
  if(text[at]==='{'){
   at++;result={};whitespace();if(text[at]==='}')at++;else while(true){whitespace();if(text[at]!== '"')fail('对象键必须为字符串');const key=string();if(owns(result,key))fail('对象键重复');whitespace();if(text[at++]!==':')fail('缺少冒号');put(result,key,value(pointer+'/'+escape(key),depth+1));whitespace();const token=text[at++];if(token==='}')break;if(token!==',')fail('对象分隔符无效');}
  }else if(text[at]==='['){
   at++;result=[];whitespace();if(text[at]===']')at++;else while(true){result.push(value(pointer+'/'+result.length,depth+1));whitespace();const token=text[at++];if(token===']')break;if(token!==',')fail('数组分隔符无效');}
  }else if(text[at]==='"')result=string();
  else if(text.startsWith('true',at)){at+=4;result=true;}else if(text.startsWith('false',at)){at+=5;result=false;}else if(text.startsWith('null',at)){at+=4;result=null;}
  else {const token=/^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(text.slice(at))?.[0];if(!token)fail('值不完整或无效');at+=token.length;const number=Number(token);result=Number.isFinite(number)&&JSON.stringify(number)===token?number:new ExactNumber(token);}
  put(locations,pointer,{start,end:at});return result;
 }
 const document=value('',0);whitespace();if(at!==text.length)fail('根值后有多余内容');if(typeof document!=='boolean'&&(document===null||Array.isArray(document)||typeof document!=='object'||document instanceof ExactNumber))fail('Schema 根必须为对象或 boolean');return document;
}
const clone=value=>value instanceof ExactNumber?new ExactNumber(value.raw):Array.isArray(value)?value.map(clone):value!==null&&typeof value==='object'?Object.fromEntries(Object.entries(value).map(([k,v])=>[k,clone(v)])):value;
export function serializeSchemaDocument(document){
 function encode(value,depth){
  const indent='  '.repeat(depth),next='  '.repeat(depth+1);if(value instanceof ExactNumber)return value.raw;
  if(Array.isArray(value))return value.length?'[\n'+value.map(v=>next+encode(v,depth+1)).join(',\n')+'\n'+indent+']':'[]';
  if(value!==null&&typeof value==='object'){const entries=Object.entries(value);return entries.length?'{\n'+entries.map(([k,v])=>next+JSON.stringify(k)+': '+encode(v,depth+1)).join(',\n')+'\n'+indent+'}':'{}';}
  if(typeof value==='number'&&!Number.isFinite(value))throw new Error('数值必须有限。');return JSON.stringify(value);
 }return encode(document,0);
}
function tokens(pointer){if(pointer==='')return [];if(!pointer.startsWith('/'))throw new Error('Pointer 必须以 / 开始。');return pointer.slice(1).split('/').map(token=>{if(/~(?![01])/.test(token))throw new Error('Pointer 转义无效。');return token.replaceAll('~1','/').replaceAll('~0','~');});}
function index(key,length,add=false){if(add&&key==='-')return length;if(!/^(?:0|[1-9]\d*)$/.test(key))throw new Error('数组索引无效。');const result=Number(key);if(result>=(add?length+1:length))throw new Error('数组索引不存在。');return result;}
export function applySchemaPatch(document,patch){
 const keys=tokens(patch.pointer);if(keys.length===0){if(patch.op!=='replace')throw new Error('根 Schema 只允许替换。');return clone(patch.value);}
 const next=clone(document);let parent=next,owner;
 for(const key of keys.slice(0,-1)){owner=parent;if(Array.isArray(parent))parent=parent[index(key,parent.length)];else if(owns(parent,key))parent=parent[key];else throw new Error('Pointer 路径不存在。');}
 if(parent===null||typeof parent!=='object'||parent instanceof ExactNumber)throw new Error('Pointer 父级不是结构节点。');const key=keys.at(-1);
 if(Array.isArray(parent)){const at=index(key,parent.length,patch.op==='add');if(patch.op==='add')parent.splice(at,0,clone(patch.value));else if(patch.op==='remove')parent.splice(at,1);else if(patch.op==='replace')parent[at]=clone(patch.value);else throw new Error('数组不能按属性改名。');return next;}
 const present=owns(parent,key);if(patch.op!=='add'&&!present)throw new Error('Pointer 目标不存在。');
 if(patch.op==='add'){if(present)throw new Error('属性已存在。');put(parent,key,clone(patch.value));}
 else if(patch.op==='replace')put(parent,key,clone(patch.value));
 else if(patch.op==='remove')delete parent[key];
 else if(patch.op==='rename'){if(typeof patch.newName!=='string'||!patch.newName.length)throw new Error('新名称不能为空。');if(patch.newName!==key&&owns(parent,patch.newName))throw new Error('属性已存在。');const value=parent[key];delete parent[key];put(parent,patch.newName,value);}
 else throw new Error('补丁操作未登记。');
 if(keys.at(-2)==='properties'&&Array.isArray(owner?.required)&&(patch.op==='rename'||patch.op==='remove'))owner.required=patch.op==='remove'?owner.required.filter(x=>x!==key):owner.required.map(x=>x===key?patch.newName:x);
 return next;
}
const maps=new Set(['properties','patternProperties','$defs','definitions','dependentSchemas']);
const branches=new Set(['allOf','anyOf','oneOf','prefixItems']);
const singles=new Set(['items','contains','additionalProperties','unevaluatedProperties','unevaluatedItems','propertyNames','not','if','then','else','contentSchema']);
const constraints=new Set(['enum','const','format','pattern','minimum','maximum','exclusiveMinimum','exclusiveMaximum','multipleOf','minLength','maxLength','minItems','maxItems','uniqueItems','minContains','maxContains','minProperties','maxProperties','required','dependentRequired','readOnly','writeOnly','nullable','default','deprecated']);
function summary(value,depth=0){
 if(value instanceof ExactNumber)return value.raw;
 if(typeof value==='string')return JSON.stringify(value.length>180?value.slice(0,180)+'…':value);
 if(value===null||typeof value!=='object')return JSON.stringify(value);
 if(depth>=2)return Array.isArray(value)?`[${value.length} 项]`:'{…}';
 const entries=Array.isArray(value)?value:Object.entries(value);const body=entries.slice(0,12).map(x=>Array.isArray(value)?summary(x,depth+1):JSON.stringify(x[0])+': '+summary(x[1],depth+1)).join(', ')+(entries.length>12?', …':'');return Array.isArray(value)?'['+body+']':'{'+body+'}';
}
export function buildSchemaNodes(document,{budget=500}={}){
 const nodes=[],ancestors=new Set();let truncated=false;const cap=Math.min(500,Math.max(1,budget));
 function visit(schema,pointer,label,depth,required=false){
  if(nodes.length>=cap){truncated=true;return;}const boolean=typeof schema==='boolean';const reference=typeof schema?.$ref==='string'?schema.$ref:typeof schema?.$dynamicRef==='string'?schema.$dynamicRef:undefined;
  nodes.push({pointer,label:label||'根 Schema',depth,required,kind:boolean?'boolean':reference?'reference':'schema',type:boolean?String(schema):Array.isArray(schema?.type)?schema.type.join(' | '):schema?.type??'未限定',reference,constraints:boolean?[]:Object.entries(schema??{}).filter(([key])=>constraints.has(key)).map(([key,value])=>({key,value:summary(value)})),keywords:boolean?[]:Object.keys(schema??{})});
  if(boolean||schema===null||typeof schema!=='object'||ancestors.has(schema))return;ancestors.add(schema);
  for(const [key,value]of Object.entries(schema)){
   if(maps.has(key)&&value&&typeof value==='object'&&!Array.isArray(value))for(const [name,child]of Object.entries(value)){visit(child,pointer+'/'+escape(key)+'/'+escape(name),name,depth+1,key==='properties'&&Array.isArray(schema.required)&&schema.required.includes(name));if(truncated)break;}
   else if(branches.has(key)&&Array.isArray(value))for(let i=0;i<value.length;i++){visit(value[i],pointer+'/'+escape(key)+'/'+i,key+' ['+i+']',depth+1);if(truncated)break;}
   else if(singles.has(key)&&value!==undefined)visit(value,pointer+'/'+escape(key),key,depth+1);
   if(truncated)break;
  }ancestors.delete(schema);
 }visit(document,'','',0);return{nodes,truncated};
}
export function schemaDraftState(state,action){
 if(action.type==='edit')return {...state,raw:action.raw,dirty:true,validation:undefined};
 if(action.type==='switch'){if(state.dirty)return {...state,pending:action,blocked:true};return {...state,definitionId:action.definitionId,raw:action.raw,dirty:false,blocked:false,validation:undefined};}
 if(action.type==='discard-switch'&&state.pending)return {...state,definitionId:state.pending.definitionId,raw:state.pending.raw,dirty:false,blocked:false,pending:undefined,validation:undefined};
 if(action.type==='saved')return {...state,dirty:false,blocked:false};return state;
}
export function validationMatches(response,identity){return !!response?.identity&&Object.keys(identity).every(key=>response.identity[key]===identity[key])&&response.evaluatedVersionRevision===identity.revision&&response.result?.formatMode===identity.formatMode;}
const validationContextKeys=['versionId','definitionId','revision','rawSchema','example','formatMode','direction','authority','contentType','schemaType'];
export function validationContextMatches(previous,current){return !!previous&&validationContextKeys.every(key=>previous[key]===current[key]);}
export function exampleOptions(definition){
 const options=[];if(definition.exampleJson!==null&&definition.exampleJson!==undefined)options.push({name:'维护示例',json:definition.exampleJson});
 for(const entry of definition.examples??[]){if(entry.unverifiedReason||entry.externalValue)options.push({...entry,unverified:true});else if(entry.json!==undefined)options.push({...entry});else if(Object.hasOwn(entry,'value'))options.push({...entry,json:JSON.stringify(entry.value)});}
 return options;
}
