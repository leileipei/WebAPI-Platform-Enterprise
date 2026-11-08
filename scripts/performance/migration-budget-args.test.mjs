import test from 'node:test';
import assert from 'node:assert/strict';
import * as runner from './migration-runner.mjs';

test('isolated migration diagnostics receive explicit test-only budgets',()=>{
 assert.equal(typeof runner.isolatedMigrationCommand,'function');
 for(const operation of ['migration-install','migration-upgrade']){
  const args=runner.isolatedMigrationCommand(operation,'candidate/result.json');
  const environment=args.filter(a=>a.startsWith('WEBAPI_MIGRATION_'));
  assert.deepEqual(environment.sort(),[
   'WEBAPI_MIGRATION_TRANSACTION_SECONDS=900','WEBAPI_MIGRATION_SETTINGS_MAX_BYTES=65536',
   'WEBAPI_MIGRATION_RULE_MAX_BYTES=16384','WEBAPI_MIGRATION_RULE_MAX_COUNT=100000',
   'WEBAPI_MIGRATION_TOTAL_MAX_BYTES=67108864'].sort());
  assert.ok(args.indexOf('controlplane')>args.indexOf(environment[0]));
 }
 assert.deepEqual(runner.isolatedMigrationCommand('migration-inspect','baseline/result.json').filter(a=>a.startsWith('WEBAPI_MIGRATION_')),[]);
});
