using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Comparisons;
public static class CompatibilityEngineRegistry
{
    public static bool IsRegistered(string version)=>version is LegacyV1Fingerprint.EngineVersion or ContractComparisonEngine.EngineVersion;
    public static string Fingerprint(ComparisonInput input,string version)=>version switch{
        LegacyV1Fingerprint.EngineVersion=>LegacyV1Fingerprint.Compute(input),
        ContractComparisonEngine.EngineVersion=>ContractNormalizer.Fingerprint(input,version),
        _=>throw new ApiException(409,"risk_review_stale","冻结证据引擎版本未登记。")
    };
}
