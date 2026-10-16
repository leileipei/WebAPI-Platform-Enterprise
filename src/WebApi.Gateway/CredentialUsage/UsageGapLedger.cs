using WebApi.Contracts.CredentialUsage;
namespace WebApi.Gateway.CredentialUsage;
public sealed class UsageGapLedger(int maximumBytes=1024*1024)
{
    private readonly Dictionary<(DateTimeOffset Hour,string Reason),UsageGap> gaps=[];
    private static DateTimeOffset Hour(DateTimeOffset t)=>new(t.Year,t.Month,t.Day,t.Hour,0,0,TimeSpan.Zero);
    public void Add(UsageGap gap)
    {
        if(gap.From>=gap.To||gap.Reason.Length is <1 or >64||gap.Reason.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('_' or '-')))throw new ArgumentException("Invalid credential usage gap.");
        var from=gap.From.ToUniversalTime();var to=gap.To.ToUniversalTime();
        if(to-from>TimeSpan.FromHours(24)){gaps[(Hour(from),gap.Reason)]=new(from,to,gap.Reason);if(gaps.Count*192>maximumBytes)Collapse(from,to);return;}
        for(var hour=Hour(from);hour<to;hour=hour.AddHours(1))
        {
            var start=from>hour?from:hour;var end=to<hour.AddHours(1)?to:hour.AddHours(1);var summary=(hour,"GapSummaryLimit");
            var key=gaps.ContainsKey(summary)?summary:(hour,gap.Reason);
            if(gaps.TryGetValue(key,out var prior))gaps[key]=new(prior.From<start?prior.From:start,prior.To>end?prior.To:end,key.Item2);
            else gaps[key]=new(start,end,key.Item2);
        }
        // 192 bytes is an upper bound for this ASCII reason and two canonical UTC timestamps.
        if(gaps.Count*192>maximumBytes)Collapse(gaps.Values.Min(x=>x.From),gaps.Values.Max(x=>x.To));
    }
    private void Collapse(DateTimeOffset from,DateTimeOffset to)
    {
        if(gaps.Count>0){from=gaps.Values.Min(x=>x.From)<from?gaps.Values.Min(x=>x.From):from;to=gaps.Values.Max(x=>x.To)>to?gaps.Values.Max(x=>x.To):to;}
        gaps.Clear();gaps[(Hour(from),"GapSummaryLimit")]=new(from,to,"GapSummaryLimit");
    }
    public IReadOnlyList<UsageGap> Snapshot()=>Array.AsReadOnly(gaps.Values.OrderBy(x=>x.From).ThenBy(x=>x.Reason,StringComparer.Ordinal).ToArray());
}
