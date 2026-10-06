using WebApi.Contracts.OpenApi;
namespace WebApi.Contracts.Catalog;
public sealed record ImportSourceAllowance(string Origin, string PathPrefix, IReadOnlyList<string> PrivateCidrs);
public sealed record ImportSourcePolicyDto(Guid ProjectId, long Revision, IReadOnlyList<ImportSourceAllowance> Allowances, ContractLimits Limits);
public sealed record SaveImportSourcePolicyRequest(IReadOnlyList<ImportSourceAllowance> Allowances, ContractLimits Limits);
