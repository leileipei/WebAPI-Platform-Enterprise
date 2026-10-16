import {test,after} from 'node:test';
import assert from 'node:assert/strict';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {createServer} from 'vite';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('..',import.meta.url));let server;
after(async()=>server?.close());
test('an alert rule editor has a labelled focusable fallback while its save controls are blocked',async()=>{
 server=await createServer({root,configFile:false,optimizeDeps:{noDiscovery:true,include:[]},server:{middlewareMode:true,hmr:false,ws:false},appType:'custom'});
 const {RuleEditor}=await server.ssrLoadModule('/src/observability/RuleEditor.tsx'),{Shell}=await server.ssrLoadModule('/src/Shell.tsx'),{SessionProvider}=await server.ssrLoadModule('/src/auth/SessionProvider.tsx');
 const initial={organizationId:'o',projectId:'p',environmentId:'e',name:'规则',severity:'Warning',metric:'latency_p95_ms',targetType:'Environment',targetId:null,expression:'latency_p95_ms > 800',windowSeconds:60,forSeconds:300,enabled:true,notification:{inConsole:true,requestedChannels:[]}};
 const previous=globalThis.localStorage;globalThis.localStorage={getItem(){return null;}};
 try{
  const html=renderToStaticMarkup(createElement(SessionProvider,null,createElement(Shell,{path:'/observability/alert-rules'},createElement(RuleEditor,{initial,onClose(){},onSaved(){}}))));
  const form=html.match(/<form[^>]*role="dialog"[^>]*>/)?.[0];assert.ok(form);
  assert.match(form,/tabindex="-1"/);
  assert.match(form,/aria-modal="true" aria-label="新建告警规则"/);
 }finally{if(previous===undefined)delete globalThis.localStorage;else globalThis.localStorage=previous;}
});
