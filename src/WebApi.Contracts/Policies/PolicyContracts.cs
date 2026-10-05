namespace WebApi.Contracts.Policies;

public sealed record PolicyScopeRequest(Guid OrganizationId, Guid? ProjectId = null);
public sealed record SavePolicyRequest(string Name, string Type, string Config, bool Enabled = true);
public sealed record CopyPolicyRequest(string Name, Guid? TargetProjectId);
public sealed record PolicyDto(Guid Id, Guid OrganizationId, Guid? ProjectId, string Name, string Type,
    string Config, bool Enabled, long VersionNo);
public sealed record PolicyBindingInput(Guid PolicyId, int Priority);
public sealed record SaveRoutePoliciesRequest(IReadOnlyList<PolicyBindingInput> Bindings);
public sealed record PolicyBindingDto(Guid PolicyId, int Priority, PolicyDto Policy);
public sealed record RoutePoliciesDto(Guid RouteId, long Revision, IReadOnlyList<PolicyBindingDto> Bindings);
public sealed record PolicyReferenceDto(string ReferenceKind, Guid EnvironmentId, Guid? ApiId,
    Guid? RouteId, long? ConfigVersion, long? SourceRevision, string Name);
public sealed record ValidatePolicyRequest(PolicyScopeRequest Scope, string Type, string Config);
public sealed record PolicyValidationDto(bool Valid, string NormalizedConfig);

public sealed record RateLimitConfiguration(string Algorithm, string KeyBy, int RefillTokens,
    int WindowMs, int Burst, string RedisFailureMode);
public sealed record CircuitBreakerConfiguration(int SamplingWindowMs, int MinimumRequests,
    double FailureRatio, int OpenDurationMs, int HalfOpenMaxRequests, int HalfOpenSuccesses,
    IReadOnlyList<int> FailureStatusCodes, bool CountTimeouts, bool CountConnectionFailures);
public sealed record PolicyBindingConfiguration(Guid PolicyId, string Type, string Config, bool Enabled, int Priority);
public sealed record EffectiveRoutePolicies(bool RequireApiKey, int? TimeoutMs,
    RateLimitConfiguration? RateLimit, CircuitBreakerConfiguration? CircuitBreaker);
