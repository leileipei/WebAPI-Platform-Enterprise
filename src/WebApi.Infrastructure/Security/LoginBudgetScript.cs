namespace WebApi.Infrastructure.Security;
internal static class LoginBudgetScript
{
    internal const string Value="""
        local time=redis.call('TIME')
        local now=tonumber(time[1])*1000+math.floor(tonumber(time[2])/1000)
        local counts={} local deadlines={} local retry=0
        for i=1,2 do
          local state=redis.call('HMGET',KEYS[i],'count','deadline')
          local count=tonumber(state[1]) local deadline=tonumber(state[2])
          if (state[1] and not count) or (state[2] and not deadline) or (count and not deadline) or (deadline and not count) then return {-1,0} end
          if not deadline or deadline<=now then count=0 deadline=now+tonumber(ARGV[i*2])*1000 end
          counts[i]=count deadlines[i]=deadline
          if count>=tonumber(ARGV[i*2-1]) then retry=math.max(retry,deadline-now) end
        end
        if retry>0 then return {0,math.max(1,math.ceil(retry/1000))} end
        for i=1,2 do
          redis.call('HSET',KEYS[i],'count',counts[i]+1,'deadline',deadlines[i])
          redis.call('PEXPIREAT',KEYS[i],deadlines[i])
        end
        return {1,0}
        """;
}
