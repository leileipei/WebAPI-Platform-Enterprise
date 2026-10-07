namespace WebApi.Gateway.Policies;
public static class ResponseCacheScripts
{
    public const string Text = """
local seal=redis.call('GET',KEYS[5]); local counter=redis.call('GET',KEYS[1]); local count=redis.call('HLEN',KEYS[2]); local cardinal=redis.call('ZCARD',KEYS[3]);
if (seal and (seal~='v1' or not counter)) or (not seal and (counter or count>0 or cardinal>0)) then return {'unavailable'} end
if count~=cardinal or count>tonumber(ARGV[4]) then return {'unavailable'} end
local total=tonumber(redis.call('GET',KEYS[1]) or '0'); if not total or total<0 or math.floor(total)~=total then return {'unavailable'} end
local rows=redis.call('HGETALL',KEYS[2]); local sum=0
for i=2,#rows,2 do local size=tonumber(rows[i]); local expiry=tonumber(redis.call('ZSCORE',KEYS[3],rows[i-1])); if not size or size<1 or math.floor(size)~=size or not expiry or expiry<1 or expiry==math.huge or expiry~=expiry or math.floor(expiry)~=expiry then return {'unavailable'} end; sum=sum+size end
if sum~=total then return {'unavailable'} end
local time=redis.call('TIME'); local now=tonumber(time[1])*1000+math.floor(tonumber(time[2])/1000)
local expired=redis.call('ZRANGEBYSCORE',KEYS[3],'-inf',now,'LIMIT',0,128)
for _,member in ipairs(expired) do local size=tonumber(redis.call('HGET',KEYS[2],member)); if not size then return {'unavailable'} end
 total=total-size; redis.call('HDEL',KEYS[2],member); redis.call('ZREM',KEYS[3],member); redis.call('DEL',ARGV[6]..member)
end
count=redis.call('HLEN',KEYS[2]); if #expired>0 then redis.call('SET',KEYS[1],total,'KEEPTTL') end
if ARGV[1]=='get' then
 local expected=redis.call('HGET',KEYS[2],ARGV[2]); local expiry=tonumber(redis.call('ZSCORE',KEYS[3],ARGV[2]) or '0'); local payload=redis.call('GET',KEYS[4])
 if not expected then if payload then return {'unavailable'} end; return {'miss'} end
 if not payload or expiry<=now then return {'miss'} end
 if #payload~=tonumber(expected) then return {'unavailable'} end
 return {'hit',payload}
end
local size=#ARGV[7]; local prior=tonumber(redis.call('HGET',KEYS[2],ARGV[2]) or '0'); local next=total-prior+size
if size<1 or next>tonumber(ARGV[3]) or (prior==0 and count>=tonumber(ARGV[4])) then return {'bypass'} end
redis.call('SET',KEYS[4],ARGV[7],'PX',ARGV[5]); redis.call('HSET',KEYS[2],ARGV[2],size); redis.call('ZADD',KEYS[3],now+tonumber(ARGV[5]),ARGV[2]); redis.call('SET',KEYS[1],next); redis.call('SET',KEYS[5],'v1')
local last=redis.call('ZREVRANGE',KEYS[3],0,0,'WITHSCORES'); local ttl=math.max(1000,tonumber(last[2])-now+60000)
redis.call('PEXPIRE',KEYS[1],ttl); redis.call('PEXPIRE',KEYS[2],ttl); redis.call('PEXPIRE',KEYS[3],ttl); redis.call('PEXPIRE',KEYS[5],ttl)
return {'stored'}
""";
}
