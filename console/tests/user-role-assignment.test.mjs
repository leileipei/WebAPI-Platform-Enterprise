import {test,after} from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'vite';
import {fileURLToPath} from 'node:url';

const server = await createServer({root:fileURLToPath(new URL('..',import.meta.url)),configFile:false,
  optimizeDeps:{noDiscovery:true,include:[]},server:{middlewareMode:true,hmr:false,ws:false},appType:'custom'});
after(() => server.close());
const {roleReplacement} = await server.ssrLoadModule('/src/governance/UserRolesEditor.tsx');

const roles = [{id:'reader'},{id:'writer'}];
test('a full replacement requires every displayed checkbox and a real change', () => {
  for (const values of [{},{reader:false},{reader:false,writer:undefined},{reader:0,writer:false},
    {reader:true,writer:false}]) {
    assert.equal(roleReplacement(['reader'],roles,values),undefined);
  }
  assert.equal(roleReplacement([],roles,{reader:false,writer:false}),undefined);
  assert.equal(roleReplacement(['reader'],[],{}),undefined);
});
test('replacements apply explicit additions and removals while carrying undisplayed grants forward', () => {
  assert.deepEqual(roleReplacement(['outside','reader'],roles,{reader:false,writer:true}),{roleIds:['outside','writer']});
  assert.deepEqual(roleReplacement(['outside','reader'],roles,{reader:false,writer:false}),{roleIds:['outside']});
  assert.deepEqual(roleReplacement(['reader'],roles,{reader:false,writer:false}),{roleIds:[]});
  assert.deepEqual(roleReplacement([],roles,{reader:true,writer:false}),{roleIds:['reader']});
});
