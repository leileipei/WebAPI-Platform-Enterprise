using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
namespace WebApi.Infrastructure.Contracts;
public sealed class ContractProcessRunner(SchemaValidationSettings settings)
{
    private readonly SemaphoreSlim active=new(2,2);
    private int admitted;
    public async Task<ContractProcessResponse> RunAsync(ContractProcessRequest request,TimeSpan deadline,CancellationToken ct)
    {
        if(request.Operation is not("schema" or "compare"))return ContractProcessProtocol.Incomplete("contract_process_operation");
        if(Interlocked.Increment(ref admitted)>4){Interlocked.Decrement(ref admitted);return ContractProcessProtocol.Incomplete("contract_process_capacity");}
        var acquired=false;Process? process=null;Task<byte[]>? output=null;Task<byte[]>? errors=null;Task? input=null;var bytesRead=0;
        using var expires=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var maximum=TimeSpan.FromSeconds(request.Operation=="schema"?5:10);
        if(deadline<=TimeSpan.Zero)expires.Cancel();else expires.CancelAfter(deadline<maximum?deadline:maximum);
        try {
            var payload=JsonSerializer.SerializeToUtf8Bytes(request,ContractProcessProtocol.JsonOptions);
            if(payload.Length>ContractProcessProtocol.MaxInputBytes)return ContractProcessProtocol.Incomplete("contract_process_input_budget");
            await active.WaitAsync(expires.Token);acquired=true;
            var start=new ProcessStartInfo(settings.DotnetPath){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
            var path=Environment.GetEnvironmentVariable("PATH");var dotnetRoot=Environment.GetEnvironmentVariable("DOTNET_ROOT");start.Environment.Clear();
            if(path is not null)start.Environment["PATH"]=path;if(dotnetRoot is not null)start.Environment["DOTNET_ROOT"]=dotnetRoot;
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]="1";
            start.ArgumentList.Add(settings.RuntimeToolPath);start.ArgumentList.Add("contract-evaluate");
            process=new(){StartInfo=start};if(!process.Start())return ContractProcessProtocol.Incomplete("contract_process_unavailable");
            output=Read(process.StandardOutput.BaseStream,expires.Token);errors=Read(process.StandardError.BaseStream,expires.Token);input=Write(process.StandardInput.BaseStream,payload,expires.Token);
            await Task.WhenAll(output,errors,input,process.WaitForExitAsync(expires.Token));
            if(process.ExitCode!=0)return ContractProcessProtocol.Incomplete("contract_process_failed");
            var response=JsonSerializer.Deserialize<ContractProcessResponse>(await output,ContractProcessProtocol.JsonOptions);
            return response is not null&&response.Status is "Valid" or "Invalid" or "Incomplete"?response:ContractProcessProtocol.Incomplete("contract_process_protocol");
        }catch(OperationCanceledException){return ContractProcessProtocol.Incomplete(ct.IsCancellationRequested?"contract_process_cancelled":"contract_process_timeout");}
        catch(ProcessOutputBudgetException){return ContractProcessProtocol.Incomplete("contract_process_output_budget");}
        catch(Exception error)when(error is Win32Exception or IOException or JsonException or InvalidOperationException or ArgumentException){return ContractProcessProtocol.Incomplete("contract_process_unavailable");}
        finally {
            if(process is not null){
                try{if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}catch(InvalidOperationException){}catch(Win32Exception){}
                expires.Cancel();foreach(var task in new Task?[]{output,errors,input})if(task is not null)try{await task;}catch(Exception error)when(error is OperationCanceledException or IOException or ProcessOutputBudgetException){}
                process.Dispose();
            }
            if(acquired)active.Release();Interlocked.Decrement(ref admitted);
        }
        async Task<byte[]> Read(Stream stream,CancellationToken token)
        {
            using var data=new MemoryStream();var buffer=new byte[8192];int count;
            while((count=await stream.ReadAsync(buffer,token))!=0){if(Interlocked.Add(ref bytesRead,count)>ContractProcessProtocol.MaxOutputBytes)throw new ProcessOutputBudgetException();await data.WriteAsync(buffer.AsMemory(0,count),token);}
            return data.ToArray();
        }
    }
    private static async Task Write(Stream stream,byte[] input,CancellationToken token){await stream.WriteAsync(input,token);await stream.FlushAsync(token);stream.Close();}
    private sealed class ProcessOutputBudgetException:Exception;
}
