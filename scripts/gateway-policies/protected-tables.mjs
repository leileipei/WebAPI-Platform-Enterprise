import assert from 'node:assert/strict';
import { assertOwnership, docker, inspectResources } from '../runtime/docker.mjs';

const dynamicColumns = {
    gateway_nodes: ['instance_id', 'app_version', 'status', 'last_heartbeat_at', 'metadata'],
    alert_evaluation_states: ['phase', 'pending_since', 'last_evaluated_slot', 'last_success_at', 'last_condition', 'last_event_id',
        'next_occurrence_no', 'evaluation_state', 'suppressed_at', 'lease_owner', 'lease_until', 'lease_token', 'revision'],
    alert_events: ['status', 'message', 'resolved_at', 'last_observed_at', 'last_value', 'last_condition', 'evaluation_state', 'revision'],
    outbox_messages: ['processed_at', 'lease_until', 'attempts']
};
const appendTables = new Set(['audit_logs', 'gateway_node_events', 'alert_event_transitions', 'alert_events', 'alert_evaluation_states', 'sso_provider_tests']);
const ephemeralTable = 'sso_login_attempts';

export function deliveryTableRule(table) {
    const mode = table === ephemeralTable ? 'ephemeral' : appendTables.has(table) ? 'append-preserve' : 'exact';
    return { mode, ignoredColumns: Object.hasOwn(dynamicColumns, table) ? [...dynamicColumns[table]] : [] };
}

export async function deliveryPostgres(state, run = docker) {
    const resources = await inspectResources(state, run);
    assertOwnership(state, resources);
    const postgres = resources.find(r => r.Kind === 'container' && r.Config?.Labels?.['com.docker.compose.service'] === 'postgres');
    assert(postgres?.State.Running);
    return postgres;
}

// Match pg_tables' regular and partitioned relations. The first catalog read only plans
// ACCESS SHARE locks. Take them before the data snapshot so concurrent rewrites cannot
// make an existing table appear empty. Ordinary DML is compatible with these locks.
const catalog = `FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p')`;
const inventory = "coalesce(jsonb_object_agg(c.relname, c.oid::text), '{}'::jsonb)";
const snapshotSql = String.raw`
SET default_transaction_read_only = on;
SELECT count(*) > 0 AS has_tables, ${inventory} AS plan,
       string_agg(format('%I.%I', n.nspname, c.relname), ', ' ORDER BY c.relname) AS lock_list
    ${catalog}
\gset
BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;
\if :has_tables
LOCK TABLE :lock_list IN ACCESS SHARE MODE NOWAIT;
\endif
SELECT ${inventory} = :'plan'::jsonb AS inventory_matches,
       coalesce(json_agg(c.relname ORDER BY c.relname), '[]'::json) AS names
    ${catalog}
\gset
SELECT json_build_object('matches', :'inventory_matches'::boolean, 'tables', :'names'::json,
    'readOnly', current_setting('transaction_read_only'), 'isolation', current_setting('transaction_isolation'));
\if :inventory_matches
SELECT format($query$
    SELECT json_build_array(%L, coalesce(json_agg(digest ORDER BY digest COLLATE "C"), '[]'::json))
    FROM (SELECT encode(sha256(convert_to((to_jsonb(t) - %L::text[])::text, 'UTF8')), 'hex') AS digest
          FROM %I.%I t) AS hashes
    $query$, c.relname, ARRAY(SELECT jsonb_array_elements_text(:'columns'::jsonb -> c.relname::text)),
    n.nspname, c.relname)
    ${catalog} AND c.relname <> :'ephemeral' ORDER BY c.relname
\gexec
\endif
COMMIT;
`;

export async function readProtectedTableMultisets(state, run = docker) {
    const postgres = await deliveryPostgres(state, run);
    const { stdout } = await run(['exec', '-i', postgres.Id, 'psql', '-XqAt', '-U', 'webapi', '-d', 'webapi',
        '-v', 'ON_ERROR_STOP=1', '-v', 'columns=' + JSON.stringify(dynamicColumns), '-v', 'ephemeral=' + ephemeralTable],
        { input: snapshotSql });
    const [header, ...rows] = stdout.trimEnd().split('\n').map(line => JSON.parse(line));
    assert(header.matches, 'Protected table inventory changed before the snapshot');
    assert.equal(header.readOnly, 'on');
    assert.equal(header.isolation, 'repeatable read');
    assert(header.tables.length > 0, 'No public tables in the protection snapshot');
    const tables = new Map(header.tables.map(name => [name, { mode: deliveryTableRule(name).mode, rowHashes: [] }]));
    const expected = header.tables.filter(name => tables.get(name).mode !== 'ephemeral');
    assert.deepEqual(rows.map(([name]) => name), expected, 'Incomplete protected table snapshot');
    for (const [name, hashes] of rows) {
        assert(Array.isArray(hashes) && hashes.every(value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value)));
        tables.get(name).rowHashes = hashes;
    }
    return Object.fromEntries(tables);
}
