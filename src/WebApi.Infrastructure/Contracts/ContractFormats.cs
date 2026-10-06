using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
namespace WebApi.Infrastructure.Contracts;
public static class ContractFormats
{
    public static IReadOnlySet<string> Registered {get;}=new[]{"uuid","date","time","date-time","duration","email","hostname","idn-email","idn-hostname","ipv4","ipv6","uri","uri-reference","uri-template","iri","iri-reference","json-pointer","relative-json-pointer","regex"}.ToFrozenSet(StringComparer.Ordinal);
    public static FormatRegistry Create()
    {
        var registry=new FormatRegistry();
        Add("uuid",value=>Match(value,@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z"));
        Add("date-time",value=>Match(value,@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})\z")&&Builtin(Formats.DateTime,value));
        Add("duration",value=>(!value.Contains('W')||Match(value,@"\AP[0-9]+W\z"))&&Builtin(Formats.Duration,value));
        Add("email",Email);Add("hostname",value=>Host(value,false));Add("idn-hostname",value=>Host(value,true));Add("ipv6",Ipv6);
        Add("uri",value=>LiteralIpValid(value)&&Builtin(Formats.Uri,value));
        Add("uri-reference",value=>LiteralIpValid(value)&&Builtin(Formats.UriReference,value));
        Add("iri",value=>LiteralIpValid(value)&&Builtin(Formats.Iri,value));
        Add("iri-reference",value=>LiteralIpValid(value)&&Builtin(Formats.IriReference,value));
        Add("uri-template",Template);
        return registry;
        void Add(string name,Func<string,bool> check)=>registry.Register(new PredicateFormat(name,node=>node.ValueKind!=JsonValueKind.String||check(node.GetString()!)));
    }
    private static bool Builtin(Format format,string value)=>format.Validate(JsonSerializer.SerializeToElement(value),out _);
    private static bool Match(string value,string pattern)=>Regex.IsMatch(value,pattern,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(250));
    private static bool Ipv4(string value)
    {
        var parts=value.Split('.');return parts.Length==4&&parts.All(x=>x.Length is >=1 and <=3&&x.All(char.IsAsciiDigit)&&(x.Length==1||x[0]!='0')&&int.Parse(x,CultureInfo.InvariantCulture)<=255);
    }
    private static bool Ipv6(string value)
    {
        if(value.Length==0||value.Any(c=>!char.IsAsciiHexDigit(c)&&c is not(':' or '.')))return false;
        if(value.Contains('.')&&!Ipv4(value[(value.LastIndexOf(':')+1)..]))return false;
        return IPAddress.TryParse(value,out var address)&&address.AddressFamily==AddressFamily.InterNetworkV6;
    }
    private static bool LiteralIpValid(string value)
    {
        var offset=value.StartsWith("//",StringComparison.Ordinal)?2:value.IndexOf("://",StringComparison.Ordinal) is var position&&position>=0?position+3:-1;
        if(offset<0)return true;var authority=value[offset..].Split('/','?','#')[0];var host=authority[(authority.LastIndexOf('@')+1)..];
        if(!host.StartsWith('['))return true;var close=host.IndexOf(']');if(close<0)return false;var address=host[1..close];
        return address.StartsWith('v')||address.StartsWith('V')||Ipv6(address);
    }
    private static bool Email(string value)
    {
        if(value.Length==0||value.Any(c=>c is <' ' or >'~'))return false;
        var separator=value.LastIndexOf('@');if(separator<=0||separator==value.Length-1)return false;var local=value[..separator];var domain=value[(separator+1)..];
        if(local.StartsWith('"')) {
            if(local.Length<2||!local.EndsWith('"'))return false;
            for(var i=1;i<local.Length-1;i++){if(local[i]=='\\'){if(++i>=local.Length-1)return false;}else if(local[i]=='"')return false;}
        }else {
            const string special="!#$%&'*+-/=?^_`{|}~";
            if(local.Split('.').Any(part=>part.Length==0||part.Any(c=>!char.IsAsciiLetterOrDigit(c)&&!special.Contains(c))))return false;
        }
        if(domain.StartsWith('[')) {
            if(!domain.EndsWith(']'))return false;var address=domain[1..^1];
            return address.StartsWith("IPv6:",StringComparison.OrdinalIgnoreCase)?Ipv6(address[5..]):Ipv4(address);
        }
        return Host(domain,false);
    }
    private static bool Host(string value,bool unicode)
    {
        if(value.Length==0||!unicode&&value.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('.' or '-'))||value.Any(char.IsControl))return false;
        try {
            var mapping=new IdnMapping{UseStd3AsciiRules=true,AllowUnassigned=false};var ascii=mapping.GetAscii(value);
            if(ascii.Length>253||ascii.StartsWith('.')||ascii.EndsWith('.'))return false;
            var decoded=mapping.GetUnicode(ascii);
            if(!string.Equals(mapping.GetAscii(decoded),ascii,StringComparison.OrdinalIgnoreCase))return false;
            foreach(var label in ascii.Split('.'))if(label.Length is <1 or >63||label.StartsWith('-')||label.EndsWith('-')||label.Length>=4&&label[2]=='-'&&label[3]=='-'&&!label.StartsWith("xn--",StringComparison.OrdinalIgnoreCase))return false;
            var labels=decoded.Split('.');
            if(labels.Any(label=>!ContextValid(label)))return false;
            if(labels.Any(label=>label.EnumerateRunes().Any(rune=>IdnaUnicodeData.Bidi(rune.Value) is IdnaUnicodeData.BidiClass.R or IdnaUnicodeData.BidiClass.AL or IdnaUnicodeData.BidiClass.AN))&&labels.Any(label=>!BidiValid(label)))return false;
            return true;
        }catch(ArgumentException){return false;}
    }
    private static bool ContextValid(string label)
    {
        var runes=label.EnumerateRunes().ToArray();if(runes.Length==0)return false;
        if(runes.Any(r=>r.Value is >=0x660 and <=0x669)&&runes.Any(r=>r.Value is >=0x6f0 and <=0x6f9))return false;
        for(var i=0;i<runes.Length;i++) {
            var c=runes[i].Value;
            if(c is 0x640 or 0x7fa or 0x302e or 0x302f or >=0x3031 and <=0x3035 or 0x303b)return false;
            if(c==0xb7&&(i==0||i==runes.Length-1||runes[i-1].Value!='l'||runes[i+1].Value!='l'))return false;
            if(c==0x375&&(i==runes.Length-1||IdnaUnicodeData.Script(runes[i+1].Value)!=IdnaUnicodeData.ScriptClass.Greek))return false;
            if(c is 0x5f3 or 0x5f4&&(i==0||IdnaUnicodeData.Script(runes[i-1].Value)!=IdnaUnicodeData.ScriptClass.Hebrew))return false;
            if(c==0x30fb&&!runes.Any(r=>IdnaUnicodeData.Script(r.Value) is IdnaUnicodeData.ScriptClass.Hiragana or IdnaUnicodeData.ScriptClass.Katakana or IdnaUnicodeData.ScriptClass.Han))return false;
            if(c is '-' or 0xb7 or 0x375 or 0x5f3 or 0x5f4 or 0x30fb or 0x200c or 0x200d or 0x6fd or 0x6fe or 0xf0b or 0x3007)continue;
            if(Rune.GetUnicodeCategory(runes[i]) is not(UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.OtherLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark))return false;
        }
        return true;
    }
    private static bool BidiValid(string label)
    {
        var classes=label.EnumerateRunes().Select(r=>IdnaUnicodeData.Bidi(r.Value)).ToArray();
        var right=classes[0] is IdnaUnicodeData.BidiClass.R or IdnaUnicodeData.BidiClass.AL;
        if(!right&&classes[0]!=IdnaUnicodeData.BidiClass.L)return false;
        if(classes.Any(c=>c is not(IdnaUnicodeData.BidiClass.EN or IdnaUnicodeData.BidiClass.ES or IdnaUnicodeData.BidiClass.CS or IdnaUnicodeData.BidiClass.ET or IdnaUnicodeData.BidiClass.ON or IdnaUnicodeData.BidiClass.BN or IdnaUnicodeData.BidiClass.NSM)&&!(right?c is IdnaUnicodeData.BidiClass.R or IdnaUnicodeData.BidiClass.AL or IdnaUnicodeData.BidiClass.AN:c==IdnaUnicodeData.BidiClass.L)))return false;
        var end=classes.Length-1;while(end>=0&&classes[end]==IdnaUnicodeData.BidiClass.NSM)end--;
        if(end<0)return false;
        if(right){if(classes.Contains(IdnaUnicodeData.BidiClass.EN)&&classes.Contains(IdnaUnicodeData.BidiClass.AN))return false;return classes[end] is IdnaUnicodeData.BidiClass.R or IdnaUnicodeData.BidiClass.AL or IdnaUnicodeData.BidiClass.EN or IdnaUnicodeData.BidiClass.AN;}
        return classes[end] is IdnaUnicodeData.BidiClass.L or IdnaUnicodeData.BidiClass.EN;
    }
    private static bool Template(string value)
    {
        for(var i=0;i<value.Length;i++) {
            var c=value[i];
            if(c=='{') {
                var close=value.IndexOf('}',i+1);if(close<0)return false;var expression=value[(i+1)..close];
                if(expression.Length==0)return false;if("+#./;?&".Contains(expression[0]))expression=expression[1..];
                foreach(var item in expression.Split(',')) {
                    if(item.Length==0)return false;var name=item;
                    if(name.EndsWith('*'))name=name[..^1];
                    else if(name.IndexOf(':') is var colon&&colon>=0){var prefix=name[(colon+1)..];if(prefix.Length is <1 or >4||prefix[0]=='0'||!prefix.All(char.IsAsciiDigit))return false;name=name[..colon];}
                    if(!Match(name,@"\A(?:[A-Za-z0-9_]|%[0-9a-fA-F]{2})+(?:\.(?:[A-Za-z0-9_]|%[0-9a-fA-F]{2})+)*\z"))return false;
                }
                i=close;
            }else if(c=='%'){if(i+2>=value.Length||!char.IsAsciiHexDigit(value[++i])||!char.IsAsciiHexDigit(value[++i]))return false;}
            else if(c<=127&&!char.IsAsciiLetterOrDigit(c)&&!"!#$&'()*+,-./:;=?@[]_~".Contains(c)||c>127&&char.IsWhiteSpace(c))return false;
        }
        return true;
    }
}
