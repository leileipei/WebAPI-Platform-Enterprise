import assert from 'node:assert/strict';
import {test} from 'node:test';
import {credentialWindowState} from '../src/applications/application-state.mjs';

const start = Date.parse('2026-11-22T04:00:00Z'), end = start + 1000;
const key = {status:'Active',validFrom:'2026-11-22T12:00:00+08:00',expiresAt:'2026-11-22T04:00:01Z'};

test('credential window starts inclusively and expires exclusively across timezone offsets', () => {
  for (const [at, state, nextChangeAt] of [
    [start - 1, 'Pending', start], [start, 'Active', end], [end - 1, 'Active', end],
    [end, 'Expired', null], [end + 1, 'Expired', null]
  ]) assert.deepEqual(credentialWindowState(key,at), {state,nextChangeAt});
});

test('a fractional database boundary changes on the first browser millisecond at or after it', () => {
  const precise = {...key,validFrom:'2026-11-22T04:00:00.0000001Z',expiresAt:'2026-11-22T04:00:01.000001Z'};
  assert.deepEqual(credentialWindowState(precise,start), {state:'Pending',nextChangeAt:start + 1});
  assert.deepEqual(credentialWindowState(precise,start + 1), {state:'Active',nextChangeAt:end + 1});
  assert.deepEqual(credentialWindowState(precise,end), {state:'Active',nextChangeAt:end + 1});
  assert.deepEqual(credentialWindowState(precise,end + 1), {state:'Expired',nextChangeAt:null});
});

test('working revocation takes precedence at every time and survives an incomplete legacy record', () => {
  for (const at of [start - 1, start, end, end + 1]) {
    assert.deepEqual(credentialWindowState({...key,status:'Revoked'},at), {state:'Revoked',nextChangeAt:null});
  }
  assert.deepEqual(credentialWindowState({status:'Revoked'},start), {state:'Revoked',nextChangeAt:null});
});

test('an unrecognized status, invalid, reversed or missing required window stays unknown', () => {
  for (const patch of [
    {status:'Archived'}, {status:undefined}, {validFrom:undefined}, {expiresAt:null}, {expiresAt:'invalid'},
    {validFrom:'2026-02-30T00:00:00Z'}, {validFrom:'2026-11-22T04:00:00'}, {expiresAt:key.validFrom}, {validFrom:'2026-11-23T00:00:00Z'}
  ]) assert.deepEqual(credentialWindowState({...key,...patch},start), {state:'Unknown',nextChangeAt:null});
});
