import {test,after} from 'node:test';
import assert from 'node:assert/strict';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {createServer} from 'vite';
import {fileURLToPath} from 'node:url';
import {toDraft,newRule} from '../src/governance/scope-state.mjs';
const root=fileURLToPath(new URL('..',import.meta.url));let server,views;
after(async()=>server?.close());
async function load(){if(!views){server=await createServer({root,configFile:false,optimizeDeps:{noDiscovery:true,include:[]},server:{middlewareMode:true,hmr:false,ws:false},appType:'custom'});views=await server.ssrLoadModule('/src/governance/ScopeRules.tsx');}return views;}
const tree={organizations:[{id:'o',name:'组织 A',code:'ORG'}],projects:[{id:'p',name:'项目 A',code:'PROJ',organizationId:'o'},{id:'other-p',name:'不可跨选项目',code:'OTHER',organizationId:'other'}],environments:[{id:'e',name:'环境 A',code:'DEV',projectId:'p'},{id:'other-e',name:'不可跨选环境',code:'OTHER',projectId:'other-p'}]};
const grant={scope:{organizationId:'o',projectId:'p',environmentId:'e'},accessMode:'read'};
test('structured scope selects render accessible labels and only descendants of the selected parent',async()=>{
 const {ScopeRules}=await load();const html=renderToStaticMarkup(createElement(ScopeRules,{rows:toDraft([grant]),tree,disabled:false,onChange(){},onRemove(){}}));
 assert.match(html,/aria-label="规则 1组织"/);assert.match(html,/aria-label="规则 1环境"/);assert.match(html,/环境 A/);assert.doesNotMatch(html,/不可跨选/);assert.doesNotMatch(html,/<textarea/);
});
test('invisible original scope remains selectable, exposes its literal ID and preservation explanation',async()=>{
 const {ScopeRules}=await load();const html=renderToStaticMarkup(createElement(ScopeRules,{rows:toDraft([{scope:{organizationId:'retained-org',projectId:null,environmentId:null},accessMode:'read_write'}]),tree,disabled:false,onChange(){},onRemove(){}}));
 assert.match(html,/value="retained-org" selected/);assert.match(html,/原始 ID 已保留/);assert.match(html,/今后新增/);
});
test('locked editing disables the rule group and new rules display only read by default',async()=>{
 const {ScopeRules}=await load();const html=renderToStaticMarkup(createElement(ScopeRules,{rows:[newRule('new')],tree,disabled:true,onChange(){},onRemove(){}}));
 assert.match(html,/<fieldset[^>]* disabled/);assert.match(html,/value="read" selected/);assert.match(html,/value="environment" selected/);
});
test('empty review states explicitly revoke business scope and change summaries show all four counts',async()=>{
 const {GrantList,ScopeSummary}=await load();const html=renderToStaticMarkup(createElement(GrantList,{grants:[],tree}));assert.match(html,/业务资源访问将被撤销/);
 const summary=renderToStaticMarkup(createElement(ScopeSummary,{before:[grant],after:[]}));assert.match(summary,/移除 <strong>1/);assert.match(summary,/保存后 <strong>0/);
});
