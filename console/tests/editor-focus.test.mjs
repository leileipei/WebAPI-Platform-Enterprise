import test from 'node:test';
import assert from 'node:assert/strict';
import {tabFocusTarget,recoveryFocusTarget} from '../src/editor-focus.mjs';

test('Tab wraps only at the active dialog boundaries and preserves native traversal inside',()=>{
 const items=['close','code','name','save'],fallback='dialog';
 assert.equal(tabFocusTarget(items,'save',false,fallback),'close');
 assert.equal(tabFocusTarget(items,'close',true,fallback),'save');
 assert.equal(tabFocusTarget(items,'code',false,fallback),null);
 assert.equal(tabFocusTarget(items,'name',true,fallback),null);
});
test('lost or removed focus re-enters the current confirmation in the requested direction',()=>{
 const items=['continue','discard'],fallback='dialog';
 assert.equal(tabFocusTarget(items,'body',false,fallback),'continue');
 assert.equal(tabFocusTarget(items,'body',true,fallback),'discard');
 assert.equal(tabFocusTarget(items,'disabled-input',false,fallback),'continue');
});
test('a busy dialog without available controls traps Tab on its focusable container',()=>{
 assert.equal(tabFocusTarget([],'disabled-save',false,'dialog'),'dialog');
 assert.equal(tabFocusTarget([],'body',true,'dialog'),'dialog');
 assert.equal(recoveryFocusTarget([],'body','dialog'),'dialog');
});
test('cancelling a removed confirmation restores focus without stealing it from an active input',()=>{
 const items=['close','code','save'];
 assert.equal(recoveryFocusTarget(items,'removed-continue','dialog'),'close');
 assert.equal(recoveryFocusTarget(items,'code','dialog'),null);
 assert.equal(recoveryFocusTarget(['continue','discard'],'code','dialog'),'continue');
});
