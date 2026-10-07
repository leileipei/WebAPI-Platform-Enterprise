import {test,before,after} from 'node:test';import assert from 'node:assert/strict';import {createElement} from 'react';import {renderToStaticMarkup} from 'react-dom/server';import {createServer} from 'vite';import {fileURLToPath} from 'node:url';
let server,Input,Mapping,Policy,Provider;before(async()=>{server=await createServer({root:fileURLToPath(new URL('..',import.meta.url)),configFile:false,optimizeDeps:{noDiscovery:true,include:[]},server:{middlewareMode:true,hmr:false,ws:false},appType:'custom'});Input=(await server.ssrLoadModule('/src/contracts/ImportSourceInput.tsx')).ImportSourceInput;Mapping=(await server.ssrLoadModule('/src/contracts/ImportOperationMapping.tsx')).ImportOperationMapping;Policy=(await server.ssrLoadModule('/src/contracts/ImportSourcePolicyEditor.tsx')).ImportSourcePolicyEditor;Provider=(await server.ssrLoadModule('/src/auth/SessionProvider.tsx')).SessionProvider;});after(async()=>server?.close());const render=(c,p)=>renderToStaticMarkup(createElement(Provider,null,createElement(c,p)));
test('source UI offers only active text/file/url fields and explicitly accepts YAML',()=>{
 for(const mode of ['text','file','url']){const html=render(Input,{value:{mode,text:'openapi:',url:'https://allowed.invalid/openapi.yaml',files:[],format:'auto'},onChange(){},disabled:false,onError(){}});assert.match(html,/JSON \/ YAML/);assert.match(html,/来源方式/);if(mode==='url'){assert.match(html,/type="url"/);assert.doesNotMatch(html,/type="file"|<textarea/);}else{assert.match(html,/<textarea/);assert.doesNotMatch(html,/type="url"/);}}
});
test('unsupported operations cannot be selected or mapped and show server warnings',()=>{
 const html=render(Mapping,{operations:[{operationId:'bad',supported:false,method:'GET',path:'/bad',warnings:['缺少固定引用']}],targets:[],onToggle(){},onEdit(){},disabled:false});assert.match(html,/缺少固定引用/);assert.match(html,/<input[^>]*disabled[^>]*type="checkbox"|<input[^>]*type="checkbox"[^>]*disabled/);assert.match(html,/不可导入/);
});
test('source policy readonly or failed reads have no save entry',()=>{
 for(const flags of [{allowed:false},{allowed:true,error:'读取失败'}]){const html=render(Policy,{...flags,policy:{revision:0,allowances:[],limits:{}},onSaved(){},loadLatest:async()=>{},save:async()=>{}});assert.match(html,/默认禁止 URL/);assert.doesNotMatch(html,/配置来源规则|>保存</);}
 const html=render(Policy,{allowed:true,policy:{revision:1,allowances:[{origin:'https://allowed.invalid',pathPrefix:'/spec/',privateCidrs:[]}],limits:{}},onSaved(){},loadLatest:async()=>{},save:async()=>{}});assert.match(html,/配置来源规则/);assert.match(html,/https:\/\/allowed.invalid/);assert.match(html,/\/spec\//);
});
test('attached source text can be reviewed and corrected without replacing the whole bundle',()=>{
 const html=render(Input,{value:{mode:'file',text:'{}',files:[{name:'schemas/a.yaml',content:'type: string',format:'yaml'}],format:'auto'},onChange(){},onError(){}});assert.match(html,/aria-label="附带来源 schemas\/a.yaml"/);assert.match(html,/type: string/);
});
test('existing draft mapping identifies the version rather than displaying an empty new API name',()=>{
 const html=render(Mapping,{operations:[{operationId:'ok',supported:true}],targets:[{operationId:'ok',existingVersionId:'version-id',version:'1'}],onToggle(){},onEdit(){}});assert.match(html,/草稿版本 version-id/);
});
