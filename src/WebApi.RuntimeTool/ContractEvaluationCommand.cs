using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Contracts;
using WebApi.Infrastructure.Comparisons;
internal static class ContractEvaluationCommand
{
    public static async Task<int> RunAsync(Stream input,Stream output)
    {
        ContractProcessResponse response;
        try {
            using var data=new MemoryStream();var buffer=new byte[8192];int count;
            while((count=await input.ReadAsync(buffer))!=0){if(data.Length+count>ContractProcessProtocol.MaxInputBytes)throw new InvalidDataException();await data.WriteAsync(buffer.AsMemory(0,count));}
            var request=JsonSerializer.Deserialize<ContractProcessRequest>(data.ToArray(),ContractProcessProtocol.JsonOptions)??throw new JsonException();
            if(request.Operation=="schema"){
                var schema=ContractProcessProtocol.DecodeSchema(request.Payload,out var limits,default);
                var result=new SchemaEvaluator().Evaluate(schema,limits,default);
                response=new(result.Status,JsonSerializer.SerializeToElement(result,ContractProcessProtocol.JsonOptions),[]);
            }else if(request.Operation=="compare"){
                var comparison=ContractProcessProtocol.DecodeComparison(request.Payload,out var limits);
                var result=new ContractComparisonEngine().Compare(comparison,limits,default);
                response=new(result.Coverage=="Invalid"?"Invalid":"Valid",JsonSerializer.SerializeToElement(result,ContractProcessProtocol.JsonOptions),[]);
            }else response=ContractProcessProtocol.Incomplete("contract_process_operation");
        }catch(Exception error)when(error is JsonException or ApiException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException){response=ContractProcessProtocol.Incomplete("contract_process_input");}
        var bytes=JsonSerializer.SerializeToUtf8Bytes(response,ContractProcessProtocol.JsonOptions);
        if(bytes.Length>ContractProcessProtocol.MaxOutputBytes)bytes=JsonSerializer.SerializeToUtf8Bytes(ContractProcessProtocol.Incomplete("contract_process_output_budget"),ContractProcessProtocol.JsonOptions);
        await output.WriteAsync(bytes);await output.FlushAsync();return 0;
    }
}
