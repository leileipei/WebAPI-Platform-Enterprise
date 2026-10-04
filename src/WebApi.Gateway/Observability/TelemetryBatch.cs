using System.Text.Json;
namespace WebApi.Gateway.Observability;
public interface ITelemetryBatchSink
{
    Task ExportAsync(string signal, IReadOnlyList<JsonElement> items, CancellationToken cancellationToken);
}
