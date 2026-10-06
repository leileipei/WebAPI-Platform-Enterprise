import {test, after} from 'node:test';
import assert from 'node:assert/strict';
import {readFile, writeFile, rm} from 'node:fs/promises';
import {createElement} from 'react';
import {renderToStaticMarkup} from 'react-dom/server';
import ts from 'typescript';
import {sidebarGroupForPath, toggleSidebarGroup, isSidebarItemActive} from '../src/sidebar-model.mjs';

const compiledUrl = new URL(`.sidebar-test-${process.pid}.mjs`, import.meta.url);
after(() => rm(compiledUrl, {force:true}));
let componentPromise;
function component() {
  return componentPromise ??= (async () => {
    const source = await readFile(new URL('../src/SidebarNav.tsx', import.meta.url), 'utf8');
    const result = ts.transpileModule(source, {compilerOptions:{jsx:ts.JsxEmit.ReactJSX, module:ts.ModuleKind.ESNext, target:ts.ScriptTarget.ES2022}});
    await writeFile(compiledUrl, result.outputText.replace("'./sidebar-model.mjs'", "'../src/sidebar-model.mjs'"));
    return (await import(compiledUrl.href)).SidebarNav;
  })();
}

test('a repeated group click closes its links and opening another replaces it', () => {
  let expanded = toggleSidebarGroup(null, 'api');
  assert.equal(expanded, 'api');
  expanded = toggleSidebarGroup(expanded, 'api');
  assert.equal(expanded, null);
  assert.equal(toggleSidebarGroup('api', 'governance'), 'governance');
});

test('detail routes and settings select their original group without prefix collisions', () => {
  for (const [path, expected] of [
    ['/apis/customer/versions', 'api'], ['/imports', 'api'], ['/routes', 'api'],
    ['/clusters/main', 'traffic'], ['/policies/rate-limit', 'policy'],
    ['/applications/customer/credentials', 'applications'], ['/approvals', 'release'],
    ['/nodes/node-1', 'gateway'], ['/observability/apis/api-1', 'observability'],
    ['/settings/sso', 'governance'], ['/scopes', 'governance'], ['/coverage', 'governance'],
    ['/apis-unrelated', null], ['/unknown', null]
  ]) assert.equal(sidebarGroupForPath(path), expected, path);
  assert.equal(isSidebarItemActive('/apis/customer', '/apis'), true);
  assert.equal(isSidebarItemActive('/apis-unrelated', '/apis'), false);
});

test('collapsed navigation hides its links; opening API exposes actual entries and current page', async () => {
  const SidebarNav = await component();
  const props = {path:'/apis/customer', onToggle:()=>{}, onNavigate:()=>{}};
  const closed = renderToStaticMarkup(createElement(SidebarNav, {...props, expanded:null}));
  assert.doesNotMatch(closed, /href="\/apis"/);
  const open = renderToStaticMarkup(createElement(SidebarNav, {...props, expanded:'api'}));
  assert.match(open, /aria-expanded="true"[^>]*>.*?API 管理/s);
  assert.match(open, /href="\/apis"[^>]*aria-current="page"/);
  assert.match(open, /href="\/imports"/);
  assert.match(open, /href="\/routes"/);
  assert.doesNotMatch(open, /href="\/settings\/sso"/);
});

test('settings remain reachable in platform governance and workbench does not link to an unrelated page', async () => {
  const SidebarNav = await component();
  const html = renderToStaticMarkup(createElement(SidebarNav, {path:'/settings/sso', expanded:'governance', onToggle:()=>{}, onNavigate:()=>{}}));
  assert.match(html, /href="\/settings\/sso"[^>]*aria-current="page"/);
  assert.match(html, /href="\/permissions"/);
  assert.match(html, /href="\/coverage"/);
  assert.match(html, /disabled=""[^>]*>.*?工作台.*?待接入/s);
  assert.doesNotMatch(html, /href="\/dashboard"/);
});

test('the signed-in root page highlights the organization list it actually renders', async () => {
  const SidebarNav = await component();
  const html = renderToStaticMarkup(createElement(SidebarNav, {
    path:'/', expanded:sidebarGroupForPath('/'), onToggle:()=>{}, onNavigate:()=>{}
  }));
  assert.match(html, /href="\/organizations"[^>]*aria-current="page"/);
  assert.equal(isSidebarItemActive('/', '/projects'), false);
  assert.equal(sidebarGroupForPath('/unknown'), null);
});
