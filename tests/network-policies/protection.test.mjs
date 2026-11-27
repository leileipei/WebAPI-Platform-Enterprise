import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { docker } from '../../scripts/runtime/docker.mjs';
import { readProtectedTableMultisets, assertProtectedTableMultisets } from '../../scripts/network-policies/fixtures.mjs';

const postgres = 'postgres@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24';
const hash = value => createHash('sha256').update(value).digest('hex');

test('original protection returns only hashes from one read-only repeatable snapshot with full multiplicity', async () => {
    const ownerId = randomUUID(), projectName = 'webapi-network-protection-' + ownerId;
    const name = projectName + '-postgres-1';
    const { stdout } = await docker(['run', '-d', '--pull', 'never', '--network', 'none', '--name', name,
        '--label', 'com.docker.compose.project=' + projectName, '--label', 'com.webapi.runtime.owner=' + ownerId,
        '--label', 'com.docker.compose.service=postgres', '--tmpfs', '/var/lib/postgresql/data',
        '-e', 'POSTGRES_HOST_AUTH_METHOD=trust', '-e', 'POSTGRES_DB=webapi', postgres]);
    const id = stdout.trim();
    const sql = async query => (await docker(['exec', '-i', id, 'psql', '-XqAt', '-v', 'ON_ERROR_STOP=1',
        '-U', 'postgres', '-d', 'webapi'], { input: query })).stdout.trim();
    try {
        for (let i = 0; i < 200; i++) {
            // Docker's entrypoint also starts a temporary socket-only initialization server.
            if ((await docker(['exec', id, 'pg_isready', '-h', '127.0.0.1', '-d', 'webapi'], { allowFailure: true })).exitCode === 0) break;
            await delay(100);
        }
        await sql(`CREATE ROLE webapi LOGIN;
            CREATE TABLE duplicates (a int, b int, note text);
            INSERT INTO duplicates SELECT 1, 2, E'SYNTHETIC_readback 中文 "quoted"\\nsecond line' FROM generate_series(1,2);
            CREATE TABLE join_keys (a int, b int, PRIMARY KEY (a,b));
            INSERT INTO join_keys VALUES (1,2),(1,3);
            CREATE TABLE gateway_nodes (name text, last_heartbeat_at text, metadata text);
            INSERT INTO gateway_nodes VALUES ('a','volatile','ignored');
            CREATE TABLE audit_logs (note text);
            INSERT INTO audit_logs VALUES ('original'),('original');
            CREATE TABLE sso_login_attempts (note text);
            INSERT INTO sso_login_attempts VALUES ('SYNTHETIC_ephemeral');
            CREATE TABLE empty_table (id int);
            CREATE TABLE refs (ref regclass);
            INSERT INTO refs VALUES ('join_keys');
            GRANT SELECT ON ALL TABLES IN SCHEMA public TO webapi;
            ALTER TABLE duplicates ENABLE ROW LEVEL SECURITY;
            CREATE POLICY proper_snapshot ON duplicates FOR SELECT TO webapi USING
              (current_setting('transaction_read_only')='on' AND current_setting('transaction_isolation')='repeatable read');`);
        const raw = await sql('SELECT json_agg(to_jsonb(d)::text) FROM duplicates d;');
        const expected = JSON.parse(raw).map(hash).sort();
        assert.equal(expected.length, 2);
        assert.equal(expected[0], expected[1]);
        const responses = [];
        const observedDocker = async (args, options) => {
            const result = await docker(args, options);
            if (args.includes('psql')) responses.push(result.stdout);
            return result;
        };
        const before = await readProtectedTableMultisets({ ownerId, projectName }, observedDocker);
        assert.deepEqual(before.duplicates, { mode: 'exact', rowHashes: expected });
        assert.deepEqual(before.join_keys.rowHashes, ['{"a": 1, "b": 2}', '{"a": 1, "b": 3}'].map(hash).sort());
        assert.deepEqual(before.gateway_nodes, { mode: 'exact', rowHashes: [hash('{"name": "a"}')] });
        assert.deepEqual(before.sso_login_attempts, { mode: 'ephemeral', rowHashes: [] });
        assert.deepEqual(before.empty_table, { mode: 'exact', rowHashes: [] });
        assert.deepEqual(before.refs.rowHashes, [hash('{"ref": "join_keys"}')]);
        assert.equal(before.audit_logs.mode, 'append-preserve');
        assert.equal(responses.length, 1);
        assert(!responses[0].includes('SYNTHETIC_') && !responses[0].includes('quoted'));
        await sql("UPDATE gateway_nodes SET metadata='another', last_heartbeat_at='later'; INSERT INTO audit_logs VALUES ('new'); DELETE FROM sso_login_attempts;");
        const snapshot = () => readProtectedTableMultisets({ ownerId, projectName });
        assert.equal(assertProtectedTableMultisets(before, await snapshot()), true);
        await sql('DELETE FROM duplicates WHERE ctid IN (SELECT ctid FROM duplicates LIMIT 1);');
        const after = await snapshot();
        assert.equal(after.duplicates.rowHashes.length, 1);
        assert.throws(() => assertProtectedTableMultisets(before, after));
        await docker(['exec', '-d', id, 'psql', '-XqAt', '-U', 'postgres', '-d', 'webapi', '-c',
            'BEGIN; LOCK TABLE join_keys IN ACCESS EXCLUSIVE MODE; SELECT pg_sleep(15);']);
        let locked = false;
        for (let i = 0; i < 100 && !locked; i++) {
            locked = await sql("SELECT EXISTS(SELECT FROM pg_locks WHERE relation='join_keys'::regclass AND mode='AccessExclusiveLock' AND granted);") === 't';
            if (!locked) await delay(50);
        }
        assert(locked, 'Dedicated fixture failed to acquire its DDL lock');
        await assert.rejects(snapshot, e => /could not obtain lock/.test(e.result?.stderr));
    } finally {
        const owner = await docker(['container', 'inspect', '--format', '{{index .Config.Labels "com.webapi.runtime.owner"}}', id]);
        assert.equal(owner.stdout.trim(), ownerId);
        await docker(['rm', '-f', '-v', id]);
    }
});
