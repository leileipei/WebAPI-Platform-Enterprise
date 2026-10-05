namespace WebApi.Gateway.Policies;
public static class TokenBucketScript
{
    public const string Text="""
        local time=redis.call('TIME')
        local now=tonumber(time[1])*1000+math.floor(tonumber(time[2])/1000)
        local refill=tonumber(ARGV[1])
        local window=tonumber(ARGV[2])
        local burst=tonumber(ARGV[3])
        local previous=redis.call('HMGET',KEYS[1],'tokens','last')
        local tokens=tonumber(previous[1]) or burst
        local last=tonumber(previous[2]) or now
        now=math.max(now,last)
        tokens=math.min(burst,tokens+(now-last)*refill/window)
        local allowed=0
        local wait=0
        if tokens>=1 then tokens=tokens-1; allowed=1
        else wait=math.max(1,math.ceil((1-tokens)*window/refill)) end
        local ttl=math.max(1000,math.min(7200000,math.ceil(2*burst*window/refill)))
        redis.call('HSET',KEYS[1],'tokens',tostring(tokens),'last',tostring(now))
        redis.call('PEXPIRE',KEYS[1],ttl)
        return {allowed,wait}
        """;
}
