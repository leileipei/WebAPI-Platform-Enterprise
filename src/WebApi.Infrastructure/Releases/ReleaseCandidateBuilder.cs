using WebApi.Infrastructure.Comparisons;
using System.Security.Cryptography;
using System.Text.Json;
using WebApi.Contracts.Runtime;
using WebApi.Contracts.Policies;
using WebApi.Domain.Applications;
using WebApi.Domain.Policies;
using WebApi.Infrastructure.Policies;
using WebApi.Infrastructure.Gateway;
using WebApi.Domain.Runtime;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Releases;
using WebApi.Contracts.Common;
using WebApi.Contracts.Security;
using WebApi.Infrastructure.Applications;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.CredentialSecurity;
using WebApi.Infrastructure.Governance;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
using WebApi.Infrastructure.Routing;
namespace WebApi.Infrastructure.Releases;
public sealed class ReleaseCandidateBuilder(WebApiDbContext db,ScopeResolver scopes,RouteService routes,VersionRiskReviewService reviews,JwtApplicationBindingService mappings,GatewayPolicyDeploymentRules deployment,SnapshotCompiler compiler,PublishSettings publishSettings,HistoricalSnapshotService history,IGovernanceRecoveryContinuity? continuity=null)
{
    public async Task<FrozenReleaseCandidate> BuildAsync(Guid environmentId,CreateReleaseRequest request,CancellationToken ct,ActorContext? actor=null)
    {
        var candidate=await PreviewAsync(environmentId,new(request.BaseConfigVersion,request.VersionIds),ct);
        RequireRevisions(request.ResourceRevisions,candidate.ResourceRevisions);
        if(request.RiskReviewIds is {Count:>0}){if(actor is null)throw new ApiException(401,"review_actor_required","评审引用需要当前用户身份。");candidate=candidate with {RiskReviewReferences=await reviews.ResolveReferencesAsync(new(candidate.OrganizationId,candidate.ProjectId,candidate.EnvironmentId),candidate.VersionIds,request.RiskReviewIds,actor,ct)};}return candidate;
    }
    public async Task<FrozenReleaseCandidate> BuildPromotionAsync(Guid environmentId,PreviewReleaseRequest request,IReadOnlyList<Guid> routeIds,WebApi.Infrastructure.Delivery.PromotionCredentialResult selection,CancellationToken ct)
    {
        var candidate=await PreviewCoreAsync(environmentId,request,ct,routeIds,selection);
        if(routeIds.Count==0||routeIds.Distinct().Count()!=routeIds.Count||routeIds.Any(id=>!candidate.Routes.Any(r=>r.Id==id)))throw new ApiException(422,"invalid_promotion_routes","晋级目标路由准备不完整。");
        if(candidate.Governance is not null) return candidate with { SharedCredentialImpact=selection.Impact };
        var routes=candidate.Routes.Where(r=>routeIds.Contains(r.Id)).ToArray();var bindings=candidate.Bindings.Where(b=>routeIds.Contains(b.RouteId)).ToArray();var policyIds=bindings.Select(b=>b.PolicyId).ToHashSet();var clusterIds=routes.Select(r=>r.ClusterId).ToHashSet();
        var revisions=candidate.ResourceRevisions.Where(r=>r.Type is not("application" or "credential" or "authorization")&&(r.Type!="route"||routeIds.Contains(r.Id))&&(r.Type!="policy"||policyIds.Contains(r.Id))&&(r.Type!="cluster"||clusterIds.Contains(r.Id))&&(r.Type!="destination"||candidate.Clusters.Where(c=>clusterIds.Contains(c.Id)).Any(c=>c.Destinations.Any(d=>d.Id==r.Id)))).Concat(selection.ResourceRevisions).DistinctBy(r=>(r.Type,r.Id)).OrderBy(r=>r.Type,StringComparer.Ordinal).ThenBy(r=>r.Id).ToArray();
        return candidate with{Routes=routes,Bindings=bindings,Policies=candidate.Policies.Where(p=>policyIds.Contains(p.Id)).ToArray(),Clusters=candidate.Clusters.Where(c=>clusterIds.Contains(c.Id)).ToArray(),Applications=selection.Applications,ResourceRevisions=revisions,SharedCredentialImpact=selection.Impact};
    }
    public static void RequireRevisions(IReadOnlyList<ResourceRevision> expected,IReadOnlyList<ResourceRevision> actual)
    {
        if(expected.Count>50000||expected.Select(r=>(r.Type,r.Id)).Distinct().Count()!=expected.Count) throw new ApiException(422,"invalid_revisions","资源修订重复或超过限制。");
        foreach(var revision in expected) {var current=actual.SingleOrDefault(r=>r.Type==revision.Type&&r.Id==revision.Id);if(current is not null&&current.Revision!=revision.Revision) throw new ApiException(412,"stale_revision","候选资源已更新，请重新预览。");}
        if(expected.Any(e=>!actual.Any(a=>a.Type==e.Type&&a.Id==e.Id))) throw new ApiException(422,"foreign_revision","revision对象不属于候选资源。");
        if(actual.Any(a=>!expected.Any(e=>e.Type==a.Type&&e.Id==a.Id))) throw new ApiException(422,"missing_revision","必须提供预览中的全部资源修订。");
    }
    public static bool RevisionsCurrent(IReadOnlyList<ResourceRevision> expected,IReadOnlyList<ResourceRevision> actual)
    {try {RequireRevisions(expected,actual);return true;}catch(ApiException e) when(e.Status is 412 or 422) {return false;}}
    public async Task<FrozenReleaseCandidate> PreviewAsync(Guid environmentId,PreviewReleaseRequest request,CancellationToken ct)
        =>await PreviewCoreAsync(environmentId,request,ct);
    private async Task<FrozenReleaseCandidate> PreviewCoreAsync(Guid environmentId,PreviewReleaseRequest request,CancellationToken ct,IReadOnlyList<Guid>? selectedRoutes=null,WebApi.Infrastructure.Delivery.PromotionCredentialResult? selection=null)
    {
        var scope=await scopes.EnvironmentAsync(environmentId,ct);var env=await db.Set<EnvironmentRecord>().AsNoTracking().SingleAsync(e=>e.Id==environmentId,ct);
        if(request.BaseConfigVersion!=(env.DesiredConfigVersion??0)) throw new ApiException(409,"stale_baseline","环境基准版本已变化。");
        if(request.VersionIds.Count is <1 or >100||request.VersionIds.Distinct().Count()!=request.VersionIds.Count) throw new ApiException(422,"invalid_selection","请为每个API选择一个版本。");
        var governed = publishSettings.EnablesGovernanceSnapshot24(environmentId);
        SecurityLedgerMaterial? ledger = null;
        GatewayNode[]? targets = null;
        if (governed)
        {
            if (env.Status != "Active") throw new ApiException(409, "governance_environment_inactive", "环境停用时不能发布治理快照。");
            if (continuity is null) throw ContinuityRequired();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM environments WHERE id={environmentId} FOR NO KEY UPDATE", ct);
            try { ledger = (await continuity.RequireAsync(db, environmentId, ct)).Ledger; }
            catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException)
            {
                ct.ThrowIfCancellationRequested();
                throw ContinuityRequired();
            }
            if (ledger.EnvironmentId != environmentId || ledger.OrganizationId != scope.OrganizationId || ledger.ProjectId != env.ProjectId ||
                env.RequireEnvironmentCredential && !ledger.RequireEnvironmentCredentials)
                throw ContinuityRequired();
            targets = await PublishCoordinator.TargetsAsync(db, publishSettings, environmentId, ct);
            SnapshotSchemaCapabilities.RequireSupported(targets, "2.4");
        }
        else await RequireLegacyEnvironmentAsync(db, env, ct);
        var versions=new List<FrozenApiVersion>();var revisions=new List<ResourceRevision>();var apiIds=new HashSet<Guid>();
        foreach(var id in request.VersionIds.OrderBy(x=>x))
        {
            var v=await db.Set<ApiVersion>().AsNoTracking().SingleOrDefaultAsync(v=>v.Id==id,ct)??throw new ApiException(422,"invalid_version","所选版本不存在。");var a=await db.Set<Api>().AsNoTracking().SingleAsync(a=>a.Id==v.ApiId,ct);
            if(a.ProjectId!=scope.ProjectId||a.OrganizationId!=scope.OrganizationId||!apiIds.Add(a.Id)||v.Status=="Retired") throw new ApiException(422,"foreign_version","版本跨范围、重复选择同一API或已停用。");
            var parameters=await db.Set<ApiParameter>().AsNoTracking().Where(p=>p.ApiVersionId==id).Select(p=>new ParameterDto(p.Id,p.ApiVersionId,p.Location,p.Name,p.DataType,p.Required,p.Schema,p.Description,p.ExampleJson)).ToArrayAsync(ct);
            var schemas=await db.Set<ApiSchema>().AsNoTracking().Where(s=>s.ApiVersionId==id).Select(s=>new SchemaDto(s.Id,s.ApiVersionId,s.SchemaType,s.Name,s.StatusCode,s.ContentType,s.SchemaJson,s.SchemaHash,s.ExampleJson)).ToArrayAsync(ct);
            versions.Add(new(CatalogService.Dto(a),CatalogService.Dto(v),governed ? parameters.OrderBy(p => p.Id).ToArray() : parameters,
                governed ? schemas.OrderBy(s => s.Id).ToArray() : schemas));revisions.Add(new("version",id,v.Revision));revisions.Add(new("api",a.Id,a.VersionNo));
        }
        var routeEntities=await db.Set<ApiRoute>().AsNoTracking().Where(r=>r.EnvironmentId==environmentId&&request.VersionIds.Contains(r.ApiVersionId)&&(selectedRoutes==null||selectedRoutes.Contains(r.Id))).OrderBy(r=>r.Id).ToArrayAsync(ct);var routeDtos=new List<RouteDto>();
        foreach(var r in routeEntities) {var effective=await routes.EffectiveAsync(r.Id,ct);routeDtos.Add(RouteService.Dto(r,effective.RequireApiKey) with {EffectiveAuthenticationMode=effective.Authentication?.Mode==AuthenticationMode.JWT?"JWT":null});revisions.Add(new("route",r.Id,r.Revision));}
        var clusterIds=routeEntities.Select(r=>r.ClusterId).Distinct().ToArray();var clusters=new List<ClusterDto>();
        foreach(var c in await db.Set<UpstreamCluster>().AsNoTracking().Where(c=>clusterIds.Contains(c.Id)).OrderBy(c=>c.Id).ToArrayAsync(ct))
        {if(c.EnvironmentId!=environmentId||c.ProjectId!=scope.ProjectId) throw new ApiException(422,"foreign_cluster","候选Cluster跨环境。");var destinations=await db.Set<UpstreamDestination>().AsNoTracking().Where(d=>d.ClusterId==c.Id).OrderBy(d=>d.Id).ToArrayAsync(ct);clusters.Add(new(c.Id,c.ProjectId,c.EnvironmentId,c.Name,c.LoadBalancingPolicy,c.HealthCheckEnabled,c.HealthCheckPath,c.HealthCheckIntervalSec,c.Status,c.Revision,destinations.Select(ClusterService.Dto).ToArray()));revisions.Add(new("cluster",c.Id,c.Revision));revisions.AddRange(destinations.Select(d=>new ResourceRevision("destination",d.Id,d.Revision)));}
        var routeIds=routeEntities.Select(r=>r.Id).ToArray();var bindings=await db.Set<RoutePolicyBinding>().AsNoTracking().Where(b=>routeIds.Contains(b.RouteId)).Select(b=>new FrozenBinding(b.RouteId,b.PolicyId,b.Priority)).ToArrayAsync(ct);var policyIds=bindings.Select(b=>b.PolicyId).ToArray();var policies=await db.Set<Policy>().AsNoTracking().Where(p=>policyIds.Contains(p.Id)).ToArrayAsync(ct);
        if(policies.Any(p=>p.OrganizationId!=scope.OrganizationId||p.ProjectId is Guid project&&project!=scope.ProjectId)) throw new ApiException(422,"foreign_policy","候选Policy跨范围。");revisions.AddRange(policies.Select(p=>new ResourceRevision("policy",p.Id,p.VersionNo)));
        var mappedIds=new HashSet<Guid>();
        foreach(var policy in policies)
        {
            deployment.Validate(policy.Type,policy.Config);
            if(policy.Type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(policy.Config).Jwt is {} jwt)
            {
                await mappings.ValidateAsync(new(policy.OrganizationId,policy.ProjectId),jwt,null,ct);
                mappedIds.UnionWith(jwt.ApplicationMappings.Select(m=>m.ApplicationId));
            }
        }
        RuntimeSnapshot? baseline=null;
        RuntimeRoute[] retainedRoutes = [];
        if(request.BaseConfigVersion>0)
        {
            try { baseline = (await history.ReadAsync(environmentId, request.BaseConfigVersion, ct)).Snapshot; }
            catch (ApiException error) when (error.Status == 404) { throw new ApiException(409, "baseline_unavailable", "环境基准快照不可用。"); }
            if (!governed && baseline.RequiredSecurityGeneration is not null) throw GovernanceRequired();
            retainedRoutes = SnapshotCompiler.RetainedRoutes(baseline, apiIds, selectedRoutes?.ToHashSet());
            if (governed && baseline.SchemaVersion != "2.4" && retainedRoutes.Length > 0)
                throw new ApiException(409, "governance_snapshot_full_freeze_required", "首次发布 2.4 必须将所有仍在运行的 API 纳入同一冻结范围。");
            var retainedPolicyIds=retainedRoutes.SelectMany(r=>r.PolicyBindings??[]).Select(b=>b.PolicyId).ToHashSet();
            foreach(var policy in baseline.Policies.Where(p=>retainedPolicyIds.Contains(p.Id)))
                if(policy.Type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(policy.Config).Jwt is {} jwt)
                {
                    deployment.Validate(policy.Type,policy.Config);
                    await mappings.ValidateAsync(scope,jwt,null,ct);
                    mappedIds.UnionWith(jwt.ApplicationMappings.Select(m=>m.ApplicationId));
                }
        }
        var publishedVersions = versions.Where(v => v.Api.LifecycleStatus != "Retired").Select(v => v.Version.Id).ToHashSet();
        var selectedRuntimeRoutes = routeEntities.Where(r => r.Enabled && publishedVersions.Contains(r.ApiVersionId)).ToArray();
        var publishedApiIds = selectedRuntimeRoutes.Select(r => r.ApiId).Concat(retainedRoutes.Select(r => r.ApiId)).Distinct().ToArray();
        var selectedGrantIds = governed && selection is not null
            ? selection.Applications.SelectMany(x => x.Permissions).Select(x => x.Id).ToArray() : null;
        var grants=await db.Set<ApplicationApiPermission>().AsNoTracking().Where(p=>p.EnvironmentId==environmentId &&
            (governed ? publishedApiIds.Contains(p.ApiId) : p.Mode=="LegacyApi") &&
            (selectedGrantIds == null || selectedGrantIds.Contains(p.Id))).OrderBy(p=>p.Id).ToArrayAsync(ct);var applications=new List<FrozenApplication>();
        if(selection is not null)
        {
            if (!governed) await RequireLegacyApplicationsAsync(db, selection.Applications, ct);
            applications.AddRange(selection.Applications);
            revisions.AddRange(selection.ResourceRevisions);
            if(mappedIds.Except(selection.Applications.Select(a=>a.Application.Id)).Any())
                throw new ApiException(422,"jwt_application_not_selected","目标JWT映射应用须明确选择或由保留业务基线提供。");
        }
        else foreach(var appId in grants.Select(g=>g.ApplicationId).Concat(mappedIds).Distinct().OrderBy(x=>x))
        {
            var app=await db.Set<ApplicationRecord>().AsNoTracking().SingleAsync(a=>a.Id==appId,ct);if(app.OrganizationId!=scope.OrganizationId||app.ProjectId is Guid p&&p!=scope.ProjectId) throw new ApiException(422,"foreign_application","候选应用跨范围。");
            var allowsGlobal = ledger?.RequireEnvironmentCredentials != true;
            var credentials = await db.Set<ApplicationCredential>().AsNoTracking().Where(c => c.ApplicationId == appId &&
                (allowsGlobal && c.BindingMode == "LegacyGlobal" && c.EnvironmentId == null ||
                 governed && c.BindingMode == "EnvironmentBound" && c.EnvironmentId == environmentId)).OrderBy(c => c.Id).ToArrayAsync(ct);
            if (credentials.Any(c => c.OrganizationId != scope.OrganizationId || c.BindingMode == "EnvironmentBound" && c.ProjectId != env.ProjectId))
                throw new ApiException(422, "foreign_credential", "候选凭证不属于该环境范围。");
            var permissions=grants.Where(g=>g.ApplicationId==appId).ToArray();foreach(var g in permissions) if((await scopes.ApiAsync(g.ApiId,ct)).ProjectId!=scope.ProjectId) throw new ApiException(422,"foreign_authorization","候选授权跨项目。");
            applications.Add(new(ApplicationService.Dto(app),credentials.Select(c=>new FrozenCredential(c.Id,c.AccessKey,c.SecretHash,c.SecretLast4,c.Status,c.ValidFrom,c.ExpiresAt,c.Revision,
                governed ? c.EnvironmentId : null, governed ? c.BindingMode : null)).ToArray(),permissions.Select(ApplicationService.Dto).ToArray()));revisions.Add(new("application",appId,app.Revision));revisions.AddRange(credentials.Select(c=>new ResourceRevision("credential",c.Id,c.Revision)));revisions.AddRange(permissions.Select(g=>new ResourceRevision("authorization",g.Id,g.Revision)));
        }
        FrozenGovernance24? governance = null;
        if (ledger is not null)
        {
            governance = await FreezeGovernanceAsync(ledger, targets!, selectedRuntimeRoutes, retainedRoutes, grants, revisions, ct);
            revisions.Add(new("environment", env.Id, env.Revision));
            if (env.IsProduction)
            {
                if (env.ReleasePolicyId is not Guid flowId)
                    throw new ApiException(422, "approval_policy_required", "生产环境必须配置两级审批流程。");
                var revision = await db.Set<ApprovalFlow>().AsNoTracking()
                    .Where(flow => flow.Id == flowId && flow.OrganizationId == scope.OrganizationId && flow.Enabled)
                    .Select(flow => (long?)flow.Revision).SingleOrDefaultAsync(ct);
                if (revision is null) throw new ApiException(422, "invalid_approval_policy", "生产审批流程不可用。");
                revisions.Add(new("approval_flow", flowId, revision.Value));
            }
            // The preview carries the exact governance facts forward through the existing revision contract.
            var governanceFingerprint = SHA256.HashData(CanonicalJson.Serialize(governance));
            revisions.Add(new("governance_snapshot", new Guid(governanceFingerprint.AsSpan(0, 16)), 1));
            revisions = revisions.OrderBy(revision => revision.Type, StringComparer.Ordinal).ThenBy(revision => revision.Id).ToList();
            bindings = bindings.OrderBy(binding => binding.RouteId).ThenBy(binding => binding.Priority).ThenBy(binding => binding.PolicyId).ToArray();
            policies = policies.OrderBy(policy => policy.Id).ToArray();
        }
        var candidate=new FrozenReleaseCandidate(environmentId,scope.OrganizationId,scope.ProjectId!.Value,request.BaseConfigVersion,request.VersionIds,versions,routeDtos,clusters,policies.Select(p=>new FrozenPolicy(p.Id,p.Type,p.Config,p.Enabled,p.VersionNo)).ToArray(),bindings,applications,revisions,Governance:governance,
            PreserveUnmappedPromotionRoutes:retainedRoutes.Any(route => apiIds.Contains(route.ApiId)));
        var newSemantics=policies.Any(p=>p.Enabled&&routeEntities.Any(r=>r.Enabled&&bindings.Any(b=>b.RouteId==r.Id&&b.PolicyId==p.Id))&&RequiresModernSchema(p.Type,p.Config))
            || baseline is not null&&retainedRoutes.Any(r=>r.AuthenticationMode==AuthenticationMode.JWT||(r.PolicyBindings??[]).Any(b=>baseline.Policies.Any(p=>p.Id==b.PolicyId&&RequiresModernSchema(p.Type,p.Config))));
        if(newSemantics || governed || candidate.PreserveUnmappedPromotionRoutes)
        {
            var compileTime = DateTimeOffset.UtcNow;
            var compiled=compiler.Compile(candidate,baseline??new RuntimeSnapshot("2.0",environmentId,0,compileTime,[],[],[],[]),request.BaseConfigVersion+1,compileTime);
            if (ledger is not null && selection is not null)
                RequireGovernedPromotionAccess(SnapshotValidator.ParsePayload(compiled.Payload.Span, environmentId),
                    baseline, apiIds, selectedRuntimeRoutes.Select(route => route.Id).ToHashSet(), ledger, compileTime);
            using var document=JsonDocument.Parse(compiled.Payload);
            var schema=document.RootElement.GetProperty("schemaVersion").GetString()!;
            if(schema is "2.2" or "2.3") await SnapshotSchemaCapabilities.RequireOnlineSchemaAsync(db,publishSettings,environmentId,schema,ct);
        }
        return candidate;
    }

    private static void RequireGovernedPromotionAccess(RuntimeSnapshot current, RuntimeSnapshot? baseline,
        HashSet<Guid> selectedApis, HashSet<Guid> mappedRouteIds, SecurityLedgerMaterial ledger, DateTimeOffset now)
    {
        var revoked = ledger.Revocations.Select(x => x.CredentialId).ToHashSet();
        var policies = current.Policies.ToDictionary(x => x.Id);
        foreach (var route in current.Routes.Where(x => mappedRouteIds.Contains(x.Id)))
        {
            IEnumerable<RuntimeApplication> eligible;
            if (route.RequireApiKey)
            {
                eligible = current.Applications.Where(app => app.Credentials.Any(key =>
                    key.BindingMode == "EnvironmentBound" && key.EnvironmentId == current.EnvironmentId && key.Status == "Active" &&
                    now >= key.ValidFrom && now < key.ExpiresAt && !revoked.Contains(key.Id)));
            }
            else if (route.AuthenticationMode == AuthenticationMode.JWT)
            {
                var mappings = (route.PolicyBindings ?? []).Select(x => policies[x.PolicyId])
                    .Where(x => x.Type == "authentication")
                    .SelectMany(x => PolicyConfigurationValidator.ParseAuthentication(x.Config).Jwt?.ApplicationMappings ?? [])
                    .Select(x => x.ApplicationId).ToHashSet();
                eligible = current.Applications.Where(app => mappings.Contains(app.Id));
            }
            else continue;

            if (route.Methods.Any(method => !eligible.Any(app => PermissionEvaluator.IsAllowed(app, route, method, now))))
                throw new ApiException(409, "governance_promotion_access_unverified", "每个受保护的晋级路由及方法都须有当前目标环境的明确授权。");
        }

        if (baseline is null) return;
        var retainedApis = baseline.Routes.Select(x => x.ApiId).Where(id => !selectedApis.Contains(id)).ToHashSet();
        foreach (var app in baseline.Applications)
        {
            var original = app.Permissions.Where(x => retainedApis.Contains(x.ApiId)).OrderBy(x => x.ApiId).ToArray();
            if (original.Length == 0) continue;
            var actual = current.Applications.SingleOrDefault(x => x.Id == app.Id)?.Permissions
                .Where(x => retainedApis.Contains(x.ApiId)).OrderBy(x => x.ApiId).ToArray();
            if (actual is null || !CanonicalJson.Serialize(actual).AsSpan().SequenceEqual(CanonicalJson.Serialize(original)))
                throw new ApiException(409, "governance_promotion_retained_grants_changed", "保留路由的当前授权与基线不符，请先按既有审批流程单独更新。");
        }
    }

    private async Task<FrozenGovernance24> FreezeGovernanceAsync(SecurityLedgerMaterial ledger, GatewayNode[] targets,
        ApiRoute[] selected, RuntimeRoute[] retained, ApplicationApiPermission[] grants, List<ResourceRevision> revisions, CancellationToken ct)
    {
        var retainedIds = retained.Select(route => route.Id).ToArray();
        var current = await db.Set<ApiRoute>().AsNoTracking().Where(route => retainedIds.Contains(route.Id))
            .ToDictionaryAsync(route => route.Id, ct);
        foreach (var route in retained)
        {
            if (!current.TryGetValue(route.Id, out var entity) || entity.EnvironmentId != ledger.EnvironmentId ||
                entity.ApiId != route.ApiId || entity.ApiVersionId != route.ApiVersionId || entity.AuthorizationRevision != route.AuthorizationRevision)
                throw new ApiException(409, "governance_retained_route_changed", "保留路由的授权修订已变化，须将相应 API 纳入本次冻结范围。");
            revisions.Add(new("route", entity.Id, entity.Revision));
        }

        var routes = selected.Concat(current.Values).Select(route => new FrozenGovernanceRoute(route.Id, route.AuthorizationRevision))
            .OrderBy(route => route.RouteId).ToArray();
        var revisionsByRoute = routes.ToDictionary(route => route.RouteId, route => route.AuthorizationRevision);
        var runtimeIds = revisionsByRoute.Keys.ToArray();
        var permissionIds = grants.Select(permission => permission.Id).ToArray();
        var methods = await db.Set<ApplicationRouteMethodGrant>().AsNoTracking()
            .Where(grant => permissionIds.Contains(grant.PermissionId) && runtimeIds.Contains(grant.RouteId)).ToArrayAsync(ct);
        var byPermission = methods.ToLookup(method => method.PermissionId);
        var permissions = new List<FrozenGovernancePermission>();
        foreach (var grant in grants)
        {
            var frozenMethods = new List<RuntimeRouteMethodGrant>();
            foreach (var methodSet in byPermission[grant.Id].GroupBy(method => method.RouteId).OrderBy(methods => methods.Key))
            {
                var latestGrant = methodSet.Max(method => method.AuthorizationRevision);
                if (latestGrant > revisionsByRoute[methodSet.Key] || methodSet.Any(method => method.EnvironmentId != ledger.EnvironmentId || method.ApiId != grant.ApiId))
                    throw new ApiException(409, "governance_route_grant_reapproval_required", "路由授权范围或修订与当前环境不一致，须重新确认授权。");

                // The wire format carries one revision per route. If none of the methods is current,
                // keep the newest stale revision so that the runtime still denies every method.
                var frozen = methodSet.Where(method => method.AuthorizationRevision == latestGrant)
                    .Select(method => method.Method).Order(StringComparer.Ordinal).ToArray();
                frozenMethods.Add(new(methodSet.Key, latestGrant, frozen));
            }
            permissions.Add(new(grant.Id, grant.Mode, frozenMethods));
        }
        var cohort = targets.Select(node => new FrozenGovernanceTarget(node.Id, node.InstanceId, SnapshotSchemaCapabilities.Read(node))).ToArray();
        return new(ledger.SecurityGeneration, ledger.Epoch, ledger.HeadHash, ledger.RequireEnvironmentCredentials,
            ledger.MinimumSnapshotSchema, routes, permissions, cohort);
    }

    internal static async Task RequireLegacyEnvironmentAsync(WebApiDbContext db, EnvironmentRecord environment, CancellationToken ct)
    {
        if (environment.RequireEnvironmentCredential || await GlobalCredentialPublicationGuard.RequiresGovernance24Async(db, environment.Id, ct))
            throw GovernanceRequired();
        if (!await LegacyEnvironmentCompatibility.IsVerifiedAsync(db, environment.Id, ct))
            throw new ApiException(409, "legacy_environment_unverified", "环境尚未证实兼容旧版快照，请先核验初始化证据。");
    }

    internal static async Task RequireLegacyApplicationsAsync(WebApiDbContext db, IReadOnlyList<FrozenApplication> applications, CancellationToken ct)
    {
        var credentialIds = applications.SelectMany(app => app.Credentials.Select(credential => credential.Id)).ToArray();
        var permissionIds = applications.SelectMany(app => app.Permissions.Select(permission => permission.Id)).ToArray();
        if (await db.Set<ApplicationCredential>().AnyAsync(credential => credentialIds.Contains(credential.Id) &&
                (credential.BindingMode != "LegacyGlobal" || credential.EnvironmentId != null), ct) ||
            await db.Set<ApplicationApiPermission>().AnyAsync(permission => permissionIds.Contains(permission.Id) && permission.Mode != "LegacyApi", ct))
            throw GovernanceRequired();
    }

    internal static ApiException GovernanceRequired() => new(409, "governance_snapshot_24_required", "环境或所选资源需要通过当前安全账本和授权重新冻结 2.4 候选。");
    internal static ApiException ContinuityRequired() => new(503, "credential_governance_recovery_required", "安全账本连续性无法核验。");

    private static bool RequiresModernSchema(string type,string config)=>type is "retry" or "cache" or "cors" or "ip_filter" or "transform"||type=="authentication"&&PolicyConfigurationValidator.ParseAuthentication(config).Mode==AuthenticationMode.JWT;
}
