using System.Collections;
namespace WebApi.Gateway.Configuration;
/// <summary>Counterfactual lookup view: every lookup scans its frozen generation.</summary>
public sealed class RuntimeLinearLookupMap<TKey,TValue> : IReadOnlyDictionary<TKey,TValue> where TKey:notnull
{
    private readonly Func<TKey,(bool Found,TValue Value)> find;
    private readonly Func<IEnumerable<KeyValuePair<TKey,TValue>>> enumerate;
    private readonly Func<int> count;
    public RuntimeLinearLookupMap(Func<TKey,(bool Found,TValue Value)> find,Func<IEnumerable<KeyValuePair<TKey,TValue>>> enumerate,Func<int> count)
    {
        this.find=find??throw new ArgumentNullException(nameof(find));this.enumerate=enumerate??throw new ArgumentNullException(nameof(enumerate));this.count=count??throw new ArgumentNullException(nameof(count));
    }
    public int Count=>count();
    public IEnumerable<TKey> Keys=>enumerate().Select(pair=>pair.Key);
    public IEnumerable<TValue> Values=>enumerate().Select(pair=>pair.Value);
    public TValue this[TKey key]=>TryGetValue(key,out var value)?value:throw new KeyNotFoundException();
    public bool ContainsKey(TKey key)=>TryGetValue(key,out _);
    public bool TryGetValue(TKey key,out TValue value){ArgumentNullException.ThrowIfNull(key);var result=find(key);value=result.Value;return result.Found;}
    public IEnumerator<KeyValuePair<TKey,TValue>> GetEnumerator()=>enumerate().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}
