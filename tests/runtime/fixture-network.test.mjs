import test from 'node:test';import assert from 'node:assert/strict';import {fixtureSubnet} from '../../scripts/runtime/fixture-network.mjs';
const state={projectName:'webapi-enterprise-local-test-12345678-1234-1234-1234-123456789abc',ownerId:'12345678-1234-1234-1234-123456789abc'};
test('explicit test network refuses persistent runtime',()=>assert.throws(()=>fixtureSubnet({...state,projectName:'webapi-enterprise-local'},[]),/fixture/));
test('test subnet selection skips occupied ranges',()=>{const first=fixtureSubnet(state,[]);assert.match(first,/^10\.248\.\d+\.0\/24$/);assert.notEqual(fixtureSubnet(state,[first]),first);});
