using Xunit;
using WebApi.Contracts.Governance;
using WebApi.Infrastructure.Governance;
namespace WebApi.Domain.Tests;
public sealed class AuditCsvFormatterTests
{
    [Theory][InlineData("=SUM(1,2)")][InlineData("+1")][InlineData("-1")][InlineData("@call")][InlineData("  =1")][InlineData("\ttext")][InlineData("\rtext")][InlineData("\ntext")]
    public void CsvFormulaAndQuotingAreSafe(string input){var row=AuditCsvFormatter.Row(new(1,null,null,null,null,input,"r","id","{\"secret\":1}",null,"127.0.0.1",null,DateTimeOffset.UnixEpoch));Assert.Contains("\"'"+input.Replace("\"","\"\"")+"\"",row);Assert.DoesNotContain("secret",row);Assert.DoesNotContain("127.0.0.1",row);Assert.EndsWith("\r\n",row);}
}
