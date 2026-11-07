namespace WebApi.Infrastructure.Persistence;

// Generated from two independent migrations of the locked PostgreSQL 17.11 image.
// A new supported migration requires its own independently verified fingerprint.
// Only the exact two SQL-installer function bodies observed to differ from EF by
// indentation are mapped to their EF originals. All other body changes remain visible.
internal static class GovernanceSchemaFingerprints
{
    internal static readonly IReadOnlyDictionary<string, string> Expected = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["20261003182012_InitialCore"] = "fe52425b83f9af0a549474b72f66d9fa289e21439476cad5c55b3915d186abf7",
        ["20261003201632_PublishCoordinatorIdentity"] = "a617755e26fdb12067dd6841567a630bb270de8c702abd4491c5b94ad7138983",
        ["20261003213457_RecoveryReference"] = "0212ebfc95f13892e5cf31ef8046e57de0db4e5a53a7ae0ff46f791303a2ca51",
        ["20261004030000_ObservabilityAlerts"] = "8f1db7ab46bdf8aee98b7eaa12d94e95d1d155ed08a0079654222955c44b697c",
        ["20261005090000_SystemSettings"] = "9f26f635180641eecd18c74004f036dade54fab0185115c43f9faf40e6647239",
        ["20261005120000_SsoOidc"] = "0de24a53b793610521b20a81f1a118162a4c8f3888fe616532c99e8cfe18c95b",
        ["20261005123210_SsoDisplayEmail"] = "2c7aa41de00dd3532a80d6e6d263d0b6bcd7afc27132acdd62a6773feb065949",
        ["20261005152401_SsoSecretVersion"] = "8837d28545ac4debb271bc6db26adab8ccf53bae232ec664d3b646ffa62d9169",
        ["20261006090000_ApiVersionComparisons"] = "2d6ca65b43e2a2528403e11839caca9b6541d7abd2c0267d05fc513c67628a21",
        ["20261006165518_ContractManagement"] = "a5c22b728870446573cc809e9922c97ba01120c432accb86a66d3272131b49a0",
        ["20261008020000_ApprovalInboxIndexes"] = "c940a2a82b167200c6fd7de8ca4c31b5b945993611dc114527ee22116d143370",
        ["20261008030000_ExternalNotifications"] = "0a44412db53e587ee76a2fd50bb96ae5847e9cce4e02736eee2b95739ecd33dc",
        ["20261009010000_EnvironmentAccessAddresses"] = "9bec84df3efb17dd12ec6a1ec3aa38174c6e3c7c85b0ad323da1d66967dd323f",
        ["20261009011000_ReleaseAccessContexts"] = "8b60204041d0b5b56c156efb6717cda984a47b9cc3043788976c53eadb79f63f",
        ["20261009020000_ReleaseDelivery"] = "576c4931ea93b3e04946d96180c3e8772ac61d31d01a1ce24d1a6b57fb442a4a",
        ["20261009030000_ReleasePipelines"] = "fea6abd90bf547e102d1e0393116778d4386645823ac9d54debec7ee3e9678c6",
        ["20261010010000_PipelineProjectionCursor"] = "0ee9b8dfc47768d2ed7a4ffc38f994aa096847358618bd6e99bbe74e3b063af2",
        ["20261010091247_GatewayQueryIndexes"] = "c5f0183ba93eeff1531997c7db642e7d3fc4287c072fc3a8240fea08fb9cb864",
        ["20261012004548_ReleaseFrozenContracts"] = "daf385188ac734468e7b6d0c16dde4600175a0c45a8ab66168c8cadc657ea04f",
        ["20261015010000_AccountPasswordLifecycle"] = "6987551e4582450323242cc56ac4a461c52a6c8d2f15c7804cd7600784a90714",
        ["20261015020000_CredentialUsageTracking"] = "b9676942e2e462605b1f00d56dd5e27dcf2910c8b9a78c812383a65079a31560",
        ["20261103183543_Governance24CredentialCore"] = "9c96618dcba54dab59ee3a740e91970d48284abfaae6cb29dd8001c00a3fcfbd",
        ["20261103184504_Governance24EnvironmentTrust"] = "26c001b3c0e5cd57ed33af97d1c91a12a32154e75fb00abb8e835ef5d3479419",
        ["20261103185114_Governance24RouteMethods"] = "80083dfb61ce0614cb8e00117d2eb6700373bf021093f3fe436cd14c6fe9b66b",
        ["20261103185741_Governance24SecurityLedger"] = "2d7c0ec9707b8fae5654b11b4a7467843973d0a2292da60b3f6d1c923d442ac3",
        ["20261103190424_Governance24TrustLedgerBinding"] = "3f2537f9c04b51de748a908ff46e617280e7eb53774e7fdb2709d52eb1ce06ec",
        ["20261103191515_Governance24SourceReceipt"] = "64639c7c941f1e7d9383793b4be0c40e5fe1a09fa9c6550d5c98b8de7148913f",
        ["20261104010000_Governance24LegacyPermissionMove"] = "21c61fde4ca30060aebeea608252826716138b94dde58054ce3b1f8368ee4e41",
        ["20261104020000_Governance24RegistrationHistory"] = "9e4a91d7eb05bd48c341cc1629873bfd05a849be3d41d9db0bf32461a6985b2f",
        ["20261104030000_Governance24LeaseIssuance"] = "db2f4200c0f71b86983a9e83070822262f99039f8ebfcc7d356a6ee7b064e058",
        ["20261104040000_Governance24AckVerification"] = "d00bf5921f0625a6e05273027728b249219d6cbecb6f15e28171d8b95797fcc1",
        ["20261104050000_Governance24AckRevalidation"] = "3c270ca544dddb38008c81326919c55c90b7d9d7f5ecd83bb9979f52c4850d36",
        ["20261104060000_Governance24FailureObservation"] = "7024ec9146dee7db4543876aec46ec14d0ac89dec7833888fc44b7c9b65ddff4",
        ["20261104070000_Governance24LeaseExpiryObservation"] = "3f978fb52f0009a5ccde4aa35c91e32755913ec1568dec2676c267a366383ea0",
        ["20261104080000_Governance24DatabaseClock"] = "2ed7307ab78b49bd8c1cca47ed5af4a1567460bb2a2b4fb9214f6d5011f93aec",
        ["20261104090000_Governance24MigrationReceipts"] = "22543989e453281197c4f6e4ca568823de70b528e7abfd55efc0cdde83302309",
    };

    internal const string Query = """
        WITH facts(fact) AS (
            SELECT pg_catalog.jsonb_build_array('relation',c.relname,c.relkind::text,c.relpersistence::text,c.relrowsecurity,c.relforcerowsecurity)::text
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f')
            UNION ALL
            SELECT pg_catalog.jsonb_build_array('column',c.relname,a.attnum,a.attname,pg_catalog.format_type(a.atttypid,a.atttypmod),
                a.attnotnull,a.attidentity::text,a.attgenerated::text,pg_catalog.pg_get_expr(d.adbin,d.adrelid))::text
            FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
            WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','f') AND a.attnum>0 AND NOT a.attisdropped
            UNION ALL
            SELECT pg_catalog.jsonb_build_array('constraint',c.relname,k.conname,k.contype::text,pg_catalog.pg_get_constraintdef(k.oid,true),
                k.convalidated,k.condeferrable,k.condeferred)::text
            FROM pg_catalog.pg_constraint k JOIN pg_catalog.pg_class c ON c.oid=k.conrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
            UNION ALL
            SELECT pg_catalog.jsonb_build_array('index',c.relname,i.relname,pg_catalog.pg_get_indexdef(i.oid),x.indisunique,x.indisprimary,x.indisvalid)::text
            FROM pg_catalog.pg_index x JOIN pg_catalog.pg_class c ON c.oid=x.indrelid
            JOIN pg_catalog.pg_class i ON i.oid=x.indexrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public'
            UNION ALL
            SELECT pg_catalog.jsonb_build_array('trigger',c.relname,t.tgname,pg_catalog.pg_get_triggerdef(t.oid,true),t.tgenabled::text)::text
            FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid=t.tgrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND NOT t.tgisinternal
            UNION ALL
            SELECT pg_catalog.jsonb_build_array('function',p.proname,pg_catalog.pg_get_function_identity_arguments(p.oid),CASE WHEN p.proname='forbid_notification_profile_mutation' AND pg_catalog.pg_get_function_identity_arguments(p.oid)='' AND pg_catalog.pg_get_functiondef(p.oid)=pg_catalog.convert_from(pg_catalog.decode('Q1JFQVRFIE9SIFJFUExBQ0UgRlVOQ1RJT04gcHVibGljLmZvcmJpZF9ub3RpZmljYXRpb25fcHJvZmlsZV9tdXRhdGlvbigpCiBSRVRVUk5TIHRyaWdnZXIKIExBTkdVQUdFIHBscGdzcWwKQVMgJGZ1bmN0aW9uJAogICAgQkVHSU4gUkFJU0UgRVhDRVBUSU9OICdOb3RpZmljYXRpb24gcHJvZmlsZXMgYXJlIGltbXV0YWJsZScgVVNJTkcgRVJSQ09ERT0nMjM1MTQnOyBFTkQgJGZ1bmN0aW9uJAo=','base64'),'UTF8') THEN pg_catalog.convert_from(pg_catalog.decode('Q1JFQVRFIE9SIFJFUExBQ0UgRlVOQ1RJT04gcHVibGljLmZvcmJpZF9ub3RpZmljYXRpb25fcHJvZmlsZV9tdXRhdGlvbigpCiBSRVRVUk5TIHRyaWdnZXIKIExBTkdVQUdFIHBscGdzcWwKQVMgJGZ1bmN0aW9uJApCRUdJTiBSQUlTRSBFWENFUFRJT04gJ05vdGlmaWNhdGlvbiBwcm9maWxlcyBhcmUgaW1tdXRhYmxlJyBVU0lORyBFUlJDT0RFPScyMzUxNCc7IEVORCAkZnVuY3Rpb24kCg==','base64'),'UTF8') WHEN p.proname='pipeline_immutable_fact' AND pg_catalog.pg_get_function_identity_arguments(p.oid)='' AND pg_catalog.pg_get_functiondef(p.oid)=pg_catalog.convert_from(pg_catalog.decode('Q1JFQVRFIE9SIFJFUExBQ0UgRlVOQ1RJT04gcHVibGljLnBpcGVsaW5lX2ltbXV0YWJsZV9mYWN0KCkKIFJFVFVSTlMgdHJpZ2dlcgogTEFOR1VBR0UgcGxwZ3NxbApBUyAkZnVuY3Rpb24kCiAgICBCRUdJTgogICAgICAgIFJBSVNFIEVYQ0VQVElPTiAnUGlwZWxpbmUgdmVyc2lvbnMgYW5kIGV2ZW50cyBhcmUgYXBwZW5kLW9ubHknIFVTSU5HIEVSUkNPREU9JzIzNTE0JzsKICAgIEVORDsKICAgICRmdW5jdGlvbiQK','base64'),'UTF8') THEN pg_catalog.convert_from(pg_catalog.decode('Q1JFQVRFIE9SIFJFUExBQ0UgRlVOQ1RJT04gcHVibGljLnBpcGVsaW5lX2ltbXV0YWJsZV9mYWN0KCkKIFJFVFVSTlMgdHJpZ2dlcgogTEFOR1VBR0UgcGxwZ3NxbApBUyAkZnVuY3Rpb24kCkJFR0lOCiAgICBSQUlTRSBFWENFUFRJT04gJ1BpcGVsaW5lIHZlcnNpb25zIGFuZCBldmVudHMgYXJlIGFwcGVuZC1vbmx5JyBVU0lORyBFUlJDT0RFPScyMzUxNCc7CkVORDsKJGZ1bmN0aW9uJAo=','base64'),'UTF8') ELSE pg_catalog.pg_get_functiondef(p.oid) END)::text
            FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND p.prokind='f'
            UNION ALL
            SELECT pg_catalog.jsonb_build_array('sequence',c.relname,s.seqstart,s.seqincrement,s.seqmax,s.seqmin,s.seqcache,s.seqcycle)::text
            FROM pg_catalog.pg_sequence s JOIN pg_catalog.pg_class c ON c.oid=s.seqrelid
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
            UNION ALL
            SELECT pg_catalog.jsonb_build_array('type',t.typname,t.typtype::text,pg_catalog.format_type(t.oid,NULL))::text
            FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace
            WHERE n.nspname='public' AND t.typtype IN ('e','d','r','m')
        )
        SELECT pg_catalog.encode(pg_catalog.sha256(pg_catalog.convert_to(COALESCE(pg_catalog.string_agg(fact,E'\n' ORDER BY fact COLLATE "C"),''),'UTF8')),'hex') AS "Value" FROM facts
        """;
}
