import {test,after} from 'node:test';
import assert from 'node:assert/strict';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import {createServer} from 'vite';
import {access} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('..',import.meta.url));
let server,view;
after(async()=>server?.close());
async function component(){
 if(view)return view;
 try{await access(root+'/src/workbench/WorkbenchView.tsx');}catch{return undefined;}
 server=await createServer({root,configFile:false,optimizeDeps:{noDiscovery:true,include:[]},server:{middlewareMode:true,hmr:false},appType:'custom'});
 view=(await server.ssrLoadModule('/src/workbench/WorkbenchView.tsx')).WorkbenchView;return view;
}
const empty={api:{state:'denied'},metrics:{state:'denied'},nodes:{state:'denied'},alerts:{state:'denied'},releases:{state:'denied'}};
// Catches unauthorized links and showing zero/healthy status when no authorized data exists.
test('a denied workbench shows no resource detail links or invented zero counts',async()=>{
 const View=await component();assert.equal(typeof View,'function');
 const html=renderToStaticMarkup(createElement(View,{results:empty,end:'2026-10-06T10:00:00Z',refresh:()=>{}}));
 assert.match(html,/无读取权限/);assert.doesNotMatch(html,/href="\/(apis|nodes|releases|observability)/);
 assert.doesNotMatch(html,/<strong>0<\/strong>/);
});
test('workbench renders scoped counts, partial coverage and real detail links without claiming all pending tasks',async()=>{
 const View=await component();assert.equal(typeof View,'function');
 const results={...empty,api:{state:'ready',data:{total:87}},metrics:{state:'ready',data:{sourceState:'Partial',coverage:{complete:false,missingNodes:['node-b'],reason:'missing_or_stale_nodes'},observedAt:null,sampling:{ratio:1,mode:'Estimate'},data:{kpis:[{metric:'success_ratio',value:.98,unit:'ratio',state:'Available',sampleCount:5}],trends:{},groups:[]}}},nodes:{state:'ready',data:{total:99,items:[{id:'node-id',nodeName:'Node A',enabled:true,offline:true,status:'Ready',currentConfigVersion:4,currentDeploymentSequence:4,desiredConfigVersion:4,desiredDeploymentSequence:4}]}},releases:{state:'ready',data:{total:98,items:[{id:'release-id',releaseNo:'REL-123',state:'WaitingApproval',createdAt:'2026-10-06T09:00:00Z',targets:[]}]}}};
 const html=renderToStaticMarkup(createElement(View,{results,end:'2026-10-06T10:00:00Z',refresh:()=>{}}));
 assert.match(html,/87/);assert.match(html,/98.00%/);assert.match(html,/数据覆盖不完整/);assert.match(html,/Offline/);
 assert.match(html,/href="\/nodes\/node-id"/);assert.match(html,/href="\/releases\/release-id"/);
 assert.match(html,/最近.*待审批/);assert.doesNotMatch(html,/待我审批/);
 assert.match(html,/99/);assert.match(html,/展示前/);
});

test('missing request counts cannot create a Top API ranking and Trace sampling is not called request sampling',async()=>{
 const View=await component();
 const results={...empty,metrics:{state:'ready',data:{sourceState:'Partial',coverage:{complete:false,missingNodes:[],reason:'incomplete_window'},sampling:{ratio:.1,mode:'Trace'},data:{kpis:[],trends:{},groups:[{key:'cf0ec918-e4ad-44ec-9db9-c3607210fbf3',name:'Unranked Secret API',values:[{metric:'request_count',value:null,unit:'requests',state:'Partial',sampleCount:0}]}]}}}};
 const html=renderToStaticMarkup(createElement(View,{results,end:'2026-10-06T10:00:00Z',refresh:()=>{}}));
 assert.doesNotMatch(html,/Unranked Secret API/);assert.doesNotMatch(html,/采样比 0.1/);
 assert.match(html,/暂无可用 API 排名/);
});
