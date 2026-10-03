import {test} from 'node:test';import assert from 'node:assert/strict';
import {bindNavigation,navigate} from '../src/navigation.mjs';
function browser(initial='/clusters'){
 const target=new EventTarget();target.Event=Event;target.location=new URL('http://test.local'+initial);
 const entries=[{url:target.location.href,state:null}];let current=0;
 const move=(url)=>{target.location=new URL(url,target.location);};
 target.history={get state(){return entries[current].state;},replaceState(state,_title,url){move(url);entries[current]={url:target.location.href,state};},pushState(state,_title,url){move(url);entries.splice(++current,entries.length,{url:target.location.href,state});},go(delta){current+=delta;assert.ok(current>=0&&current<entries.length);move(entries[current].url);queueMicrotask(()=>{const event=new Event('popstate');event.state=entries[current].state;target.dispatchEvent(event);});}};
 return target;
}
test('ordinary navigation updates accepted path and clean Back works',async()=>{
 const target=browser(),paths=[],dispose=bindNavigation(target,path=>paths.push(path));
 navigate('/imports',false,target);assert.equal(target.location.pathname,'/imports');
 target.history.go(-1);await Promise.resolve();assert.equal(target.location.pathname,'/clusters');assert.deepEqual(paths,['/imports','/clusters']);dispose();
});
test('unsaved Back restores original URL without changing rendered page; accepted discard enables Back',async()=>{
 const target=browser(),paths=[];bindNavigation(target,path=>paths.push(path));navigate('/organizations',false,target);
 let dirty=true;target.addEventListener('app-navigation',event=>{if(dirty)event.preventDefault();});
 target.history.go(-1);await Promise.resolve();await Promise.resolve();assert.equal(target.location.pathname,'/organizations');assert.deepEqual(paths,['/organizations']);
 dirty=false;target.history.go(-1);await Promise.resolve();assert.equal(target.location.pathname,'/clusters');assert.deepEqual(paths,['/organizations','/clusters']);
});
test('dirty import blocks sidebar navigation, while successful committed save may navigate',()=>{
 const target=browser('/imports'),paths=[];bindNavigation(target,path=>paths.push(path));target.addEventListener('app-navigation',event=>event.preventDefault());
 navigate('/clusters',false,target);assert.equal(target.location.pathname,'/imports');assert.deepEqual(paths,[]);
 navigate('/apis/new',true,target);assert.equal(target.location.pathname,'/apis/new');assert.deepEqual(paths,['/apis/new']);
});
