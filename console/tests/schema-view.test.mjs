import {test,before,after} from 'node:test';import assert from 'node:assert/strict';import {createElement} from 'react';import {renderToStaticMarkup} from 'react-dom/server';import {createServer} from 'vite';import {fileURLToPath} from 'node:url';
let server,Workbench,Tree,Diagnostics,ContractIssues,Provider;const root=fileURLToPath(new URL('..',import.meta.url));
before(async()=>{server=await createServer({root,configFile:false,optimizeDeps:{noDiscovery:true,include:[]},server:{middlewareMode:true,hmr:false,ws:false},appType:'custom'});Workbench=(await server.ssrLoadModule('/src/contracts/SchemaWorkbench.tsx')).SchemaWorkbench;Tree=(await server.ssrLoadModule('/src/contracts/SchemaTree.tsx')).SchemaTree;Diagnostics=(await server.ssrLoadModule('/src/contracts/ExampleValidation.tsx')).SchemaDiagnostics;ContractIssues=(await server.ssrLoadModule('/src/contracts/ExampleValidation.tsx')).ContractIssues;Provider=(await server.ssrLoadModule('/src/auth/SessionProvider.tsx')).SessionProvider;});after(async()=>server?.close());
const render=element=>renderToStaticMarkup(createElement(Provider,null,element));
const props={version:{id:'v',revision:2},definition:{id:'s',schemaType:'response',name:'Body',contentType:'application/json',statusCode:200,schemaJson:'{"properties":{"a/b":false}}',exampleJson:'0'},save:async()=>{},loadLatest:async()=>({revision:3}),close(){}};
test('sealed and readonly workbenches show tree, raw and validation without a save entry',()=>{
 for(const flags of [{readOnly:true},{version:{...props.version,sealedAt:'2026-10-07'}}]){const html=render(createElement(Workbench,{...props,...flags}));assert.match(html,/role="dialog"/);assert.match(html,/tabindex="-1"/);assert.match(html,/结构树/);assert.match(html,/Schema 原文/);assert.match(html,/示例验证/);assert.match(html,/Annotation/);assert.match(html,/Strict/);assert.match(html,/readonly=""/i);assert.doesNotMatch(html,/>保存<|>保存工作定义</);}
});
test('editable workbench uses an explicit save action and invalid raw stays visible with paused tree',()=>{
 const html=render(createElement(Workbench,props));assert.match(html,/<button[^>]*type="button"[^>]*>保存工作定义<\/button>/);assert.match(html,/未发布/);
 const invalid=render(createElement(Workbench,{...props,definition:{...props.definition,schemaJson:'{"type":'}}));assert.match(invalid,/role="alert"/);assert.match(invalid,/结构编辑已暂停/);assert.match(invalid,/{&quot;type&quot;:/);assert.match(invalid,/aria-describedby="schema-json-error"/);
});
test('tree labels boolean and references and announces bounded truncation',()=>{
 const document={properties:{'a/b':false},$defs:{Loop:{$ref:'#/$defs/Loop'}}};let html=render(createElement(Tree,{document,onSelect(){},readOnly:true}));assert.match(html,/data-pointer="\/properties\/a~1b"/);assert.match(html,/false/);assert.match(html,/#\/\$defs\/Loop/);
 html=render(createElement(Tree,{document:{properties:Object.fromEntries(Array.from({length:600},(_,i)=>['p'+i,true]))},onSelect(){},readOnly:true}));assert.match(html,/500/);assert.match(html,/截断/);assert.match(html,/role="status"/);
});
test('validation diagnostics expose accessible instance and schema locations without sample values',()=>{
 const html=render(createElement(Diagnostics,{result:{status:'Invalid',issues:[{instancePointer:'/name',schemaPointer:'/properties/name/type',keyword:'type',message:'实例不满足该Schema约束。'}],coverageIssues:[]},onSelect(){}}));assert.match(html,/aria-label="定位 Schema \/properties\/name\/type"/);assert.match(html,/\/name/);assert.match(html,/实例不满足/);assert.doesNotMatch(html,/PRIVATE-EXAMPLE/);
});

test('server maintenance impact diagnostics show accessible repair pointers',()=>{
 const html=render(createElement(ContractIssues,{issues:[{code:'schema_reference_impact',pointer:'/components/schemas/Old',message:'删除或改名影响引用。',line:2,column:3}],onSelect(){}}));assert.match(html,/aria-label="定位受影响位置 \/components\/schemas\/Old"/);assert.match(html,/2:3/);assert.match(html,/schema_reference_impact/);
});
