import test from 'node:test';
import assert from 'node:assert/strict';
import * as model from '../src/applications/permission-matrix-model.mjs';
const {permissionWindow,matrixChange,matchesMatrixPreview,toLocalInput} = model;

process.env.TZ = 'UTC';

const grant = {id:'grant', revision:7, validFrom:'2026-11-01T01:30:59.1234567+00:00', expiresAt:'2026-12-01T04:45:27.7654321+00:00'};
const cell = {api:{id:'api'}, environment:{id:'prod',isProduction:true}, grant};
const change = {apiId:'api', environmentId:'prod', action:'Revoke', permissionId:'grant', revision:7};

test('unchanged controls retain submillisecond precision, including the untouched half of a window edit', () => {
  assert.deepEqual(permissionWindow(grant,'2026-11-01T01:30:59',undefined),{
    validFrom:'2026-11-01T01:30:59.1234567+00:00',expiresAt:'2026-12-01T04:45:27.7654321+00:00'
  });
  assert.deepEqual(permissionWindow(grant,undefined,''),{
    validFrom:'2026-11-01T01:30:59.1234567+00:00',expiresAt:null
  });
  assert.equal(permissionWindow(grant,undefined,'2026-12-01T06:45').expiresAt,'2026-12-01T06:45:00.000Z');
});

test('editor displays real seconds and retains original zero-second precision after browser normalization', () => {
  assert.equal(toLocalInput(grant.validFrom),'2026-11-01T01:30:59');
  const zeroSecondGrant = {...grant, expiresAt:'2026-12-01T04:45:00.7654321+00:00'};
  assert.equal(toLocalInput(zeroSecondGrant.expiresAt),'2026-12-01T04:45:00');
  assert.equal(permissionWindow(zeroSecondGrant,undefined,'2026-12-01T04:45').expiresAt,zeroSecondGrant.expiresAt);
  assert.equal(permissionWindow(grant,undefined,'2026-12-01T04:45').expiresAt,'2026-12-01T04:45:00.000Z');
});

test('revoke includes only its cell identity and concurrency precondition', () => {
  assert.deepEqual(matrixChange(cell,'Revoke'),change);
});

test('confirmation requires one server preview item per exact cell', () => {
  const dev = {...change,environmentId:'dev'};
  assert.equal(matchesMatrixPreview([change,dev],[dev,change]),true);
  assert.equal(matchesMatrixPreview([change,dev],[change,change]),false);
  assert.equal(matchesMatrixPreview([change,dev],[change]),false);
  assert.equal(matchesMatrixPreview([change,dev],[change,{...dev,apiId:'another-api'}]),false);
});

const source = {...cell,environment:{id:'prod',canManage:true,canGrantLegacy:true},grant:{...grant,mode:'LegacyApi'}};
const destination = {api:{id:'other-api'},environment:{id:'test',canManage:true,canGrantLegacy:true}};

test('legacy move recognizes the source and empty destination across selection order and checks both scopes', () => {
  assert.equal(typeof model.matrixLegacyMoveCells,'function');
  assert.deepEqual(model.matrixLegacyMoveCells([destination,source]),[source,destination]);
  assert.equal(model.matrixLegacyMoveCells([source,{...destination,grant:source.grant}]),undefined);
  assert.equal(model.matrixLegacyMoveCells([{...source,grant:{...source.grant,mode:'RouteMethod'}},destination]),undefined);
  assert.equal(model.matrixLegacyMoveCells([source,{...destination,environment:{...destination.environment,canManage:false}}]),undefined);
  assert.equal(model.matrixLegacyMoveCells([{...source,environment:{...source.environment,canGrantLegacy:false}},destination]),undefined);
});

test('legacy move sends one command with the source permission identity and exact window', () => {
  assert.deepEqual(matrixChange(source,'MoveLegacy',permissionWindow(source.grant),{destination}),{
    apiId:'api',environmentId:'prod',action:'MoveLegacy',permissionId:'grant',revision:7,
    destinationApiId:'other-api',destinationEnvironmentId:'test',
    validFrom:'2026-11-01T01:30:59.1234567+00:00',expiresAt:'2026-12-01T04:45:27.7654321+00:00'
  });
});

test('the preview for one move needs both exact cells and rejects incomplete or unrelated impacts', () => {
  const move = {...change,action:'MoveLegacy',destinationApiId:'other-api',destinationEnvironmentId:'test'};
  const sourceImpact = {apiId:'api',environmentId:'prod'};
  const targetImpact = {apiId:'other-api',environmentId:'test'};
  assert.equal(matchesMatrixPreview([move],[targetImpact,sourceImpact]),true);
  assert.equal(matchesMatrixPreview([move],[sourceImpact]),false);
  assert.equal(matchesMatrixPreview([move],[sourceImpact,sourceImpact]),false);
  assert.equal(matchesMatrixPreview([move],[sourceImpact,{...targetImpact,apiId:'unexpected'}]),false);
  assert.equal(matchesMatrixPreview([move,{...change,apiId:'third'}],[sourceImpact,targetImpact,{...sourceImpact,apiId:'third'}]),false);
});
