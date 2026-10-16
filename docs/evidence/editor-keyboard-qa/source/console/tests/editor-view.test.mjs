import {test,after} from 'node:test';
import assert from 'node:assert/strict';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {createServer} from 'vite';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('..',import.meta.url));let server;
after(async()=>server?.close());
test('the editor exposes a labelled modal with a focusable fallback for blocked controls',async()=>{
 server=await createServer({root,configFile:false,optimizeDeps:{noDiscovery:true,include:[]},server:{middlewareMode:true,hmr:false,ws:false},appType:'custom'});
 const {Editor}=await server.ssrLoadModule('/src/ui.tsx'),{SessionProvider}=await server.ssrLoadModule('/src/auth/SessionProvider.tsx');
 const html=renderToStaticMarkup(createElement(SessionProvider,null,createElement(Editor,{title:'编辑项目',fields:[],save:async()=>{},close(){}})));
 assert.match(html,/<form[^>]*role="dialog"[^>]*tabindex="-1"/);
 assert.match(html,/aria-modal="true"/);assert.match(html,/aria-label="编辑项目"/);
});
