import {test,after} from 'node:test';
import assert from 'node:assert/strict';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {createServer} from 'vite';
import {fileURLToPath} from 'node:url';
import {readFile} from 'node:fs/promises';
import {describePermission} from '../src/governance/permission-display.mjs';
let server;after(async()=>server?.close());
test('all seeded codes have explicit Chinese labels and supported matrix columns',async()=>{
 const source=await readFile(new URL('../../src/WebApi.Infrastructure/Governance/PermissionCatalog.cs',import.meta.url),'utf8');
 const codes=[...source.split('public static readonly string[] Codes=[')[1].split('];')[0].matchAll(/"([^"]+)"/g)].map(x=>x[1]);
 assert.equal(codes.length,50);
 assert(codes.includes("release.test.record")&&codes.includes("release.test.accept")&&codes.includes("release.verify"));
 for(const code of codes){const display=describePermission({code,name:code,module:code.split('.')[0]});assert.match(display.label,/[\u4e00-\u9fff]/,code);assert.notEqual(display.column,'other',code);}
});
test('read-only matrix exposes exact assignments and accessible disabled checkboxes',async()=>{
 server=await createServer({root:fileURLToPath(new URL('..',import.meta.url)),configFile:false,optimizeDeps:{noDiscovery:true,include:[]},server:{middlewareMode:true,hmr:false,ws:false},appType:'custom'});
 const {PermissionMatrix}=await server.ssrLoadModule('/src/governance/PermissionMatrix.tsx');
 const items=[{code:'api.read',module:'api',name:'api.read'},{code:'api.version.write',module:'api',name:'api.version.write'},{code:'extension.code',module:'extension',name:'扩展能力'}];
 const html=renderToStaticMarkup(createElement(PermissionMatrix,{items,value:{'api.read':true,'api.version.write':false,'extension.code':true},readOnly:true}));
 assert.match(html,/已选 2 \/ 3 项/);assert.match(html,/权限模块与功能矩阵/);
 const inputs=html.match(/<input[^>]*type="checkbox"[^>]*>/g);assert.equal(inputs.length,3);assert(inputs.every(x=>x.includes('disabled=""')));
 assert.equal(inputs.filter(x=>x.includes('checked=""')).length,2);
 assert.match(html,/aria-label="维护 API 版本 · api.version.write"/);assert.match(html,/扩展能力/);
 assert(!html.includes('type="submit"'));
 const edit=renderToStaticMarkup(createElement(PermissionMatrix,{items,value:{'api.read':true},onChange(){}}));
 assert.equal((edit.match(/<input[^>]*type="checkbox"[^>]*>/g)||[]).length,3);
 assert(!(edit.match(/<input[^>]*type="checkbox"[^>]*>/g)||[]).some(x=>x.includes('disabled=""')));
});
test('the common editor can render matrix fields inside its disabled and unsaved guard boundary',async()=>{
 const {Editor}=await server.ssrLoadModule('/src/ui.tsx'),{SessionProvider}=await server.ssrLoadModule('/src/auth/SessionProvider.tsx'),{PermissionMatrix}=await server.ssrLoadModule('/src/governance/PermissionMatrix.tsx');
 const html=renderToStaticMarkup(createElement(SessionProvider,null,createElement(Editor,{title:'角色权限',fields:[{key:'api.read',label:'查看 API',type:'checkbox'}],initial:{'api.read':true},save:async()=>{},close(){},renderFields:(value,change)=>createElement(PermissionMatrix,{items:[{code:'api.read',module:'api'}],value,onChange:change})})));
 assert.match(html,/<fieldset class="form-grid">[\s\S]*权限模块与功能矩阵[\s\S]*<\/fieldset>/);
 assert.match(html,/aria-label="查看 API · api.read"[^>]*checked=""/);
 assert.match(html,/class="dialog permission-dialog"/);
});
