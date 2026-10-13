using System.IO.Compression;
using WebApi.Gateway.Policies;
using System.Text;
using WebApi.Gateway.Configuration;
using Xunit;

namespace WebApi.Gateway.Tests;
public sealed class JsonBodyCodecTests
{
    internal static byte[] Encode(byte[] input,string? encoding)
    {if(encoding is null or "identity")return input;using var target=new MemoryStream();using(Stream writer=encoding=="gzip"?new GZipStream(target,CompressionLevel.Fastest,true):new BrotliStream(target,CompressionLevel.Fastest,true))writer.Write(input);return target.ToArray();}
    private static async Task<(byte[]? Utf8,string? Reason)> Read(byte[] input,string? encoding,int limit=1024,CancellationToken ct=default)
    {await using var stream=new MemoryStream(input);var result=await BoundedJsonBodyCodec.ReadAsync(stream,encoding,limit,ct);return(result.Utf8,result.Reason);}
    [Theory][InlineData(null)][InlineData("identity")][InlineData("gzip")][InlineData("br")]
    public async Task JsonCodec_ValidBoundedSingleLayer_IsDecoded(string? encoding)
    {var input=Encoding.UTF8.GetBytes("{\"user_id\":123}");var result=await Read(Encode(input,encoding),encoding);Assert.Null(result.Reason);Assert.Equal(input,result.Utf8);}
    [Theory][InlineData("gzip, br")][InlineData("deflate")]
    public async Task JsonCodec_UnsupportedEncoding_IsControlled(string encoding)
    {var result=await Read("{}"u8.ToArray(),encoding);Assert.Null(result.Utf8);Assert.Equal("transform_body_encoding_unsupported",result.Reason);}
    [Theory][InlineData("gzip")][InlineData("br")]
    public async Task JsonCodec_BrokenEncoding_IsControlled(string encoding)
    {var result=await Read(new byte[]{255,255,255,255,255},encoding);Assert.Null(result.Utf8);Assert.Equal("transform_body_encoding_unsupported",result.Reason);}
    [Theory][InlineData(null)][InlineData("gzip")][InlineData("br")]
    public async Task JsonCodec_InputAndDecodedLimits_AreSeparate(string? encoding)
    {var result=await Read(Encode(Encoding.UTF8.GetBytes("\""+new string('a',1024)+"\""),encoding),encoding);Assert.Null(result.Utf8);Assert.Equal("transform_body_too_large",result.Reason);var encoded=await Read(new byte[1025],encoding);Assert.Equal("transform_body_too_large",encoded.Reason);}
    [Theory][InlineData("")][InlineData("{\"x\":1,\"x\":2}")][InlineData("{} trailing")][InlineData("\"\\uD800\"")]
    public async Task JsonCodec_InvalidJson_IsControlled(string json)
    {var result=await Read(Encoding.UTF8.GetBytes(json),null);Assert.Equal("transform_json_invalid",result.Reason);Assert.Null(result.Utf8);}
    [Fact]
    public async Task JsonCodec_InvalidUtf8_IsControlled()
    {var result=await Read(new byte[]{34,255,34},null);Assert.Equal("transform_json_invalid",result.Reason);}
    [Theory][InlineData("gzip")][InlineData("br")]
    public async Task JsonCodec_TruncatedEncoding_IsRejected(string encoding)
    {var input=Encode("{\"user_id\":123}"u8.ToArray(),encoding);var result=await Read(input[..^1],encoding);Assert.Null(result.Utf8);Assert.Equal("transform_body_encoding_unsupported",result.Reason);}
    [Fact]
    public async Task JsonCodec_ConcatenatedGzipMembers_IsOneBoundedDecodedJson()
    {var input=Encode("{\"x\":"u8.ToArray(),"gzip").Concat(Encode("1}"u8.ToArray(),"gzip")).ToArray();var result=await Read(input,"gzip");Assert.Null(result.Reason);Assert.Equal("{\"x\":1}",Encoding.UTF8.GetString(result.Utf8!));}
    [Fact]
    public async Task JsonCodec_PreCanceledRead_PropagatesCancellation()
    {using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await Read("{}"u8.ToArray(),null,ct:cancel.Token));}

    [Fact]
    public async Task JsonCodec_GzipOptionalFilename_IsAccepted()
    {var source=Encode("{}"u8.ToArray(),"gzip");source[3]|=8;var input=source[..10].Concat("file.json\0"u8.ToArray()).Concat(source[10..]).ToArray();var result=await Read(input,"gzip");Assert.Null(result.Reason);Assert.Equal("{}",Encoding.UTF8.GetString(result.Utf8!));}
    [Theory][InlineData("crc")][InlineData("size")][InlineData("trailing")]
    public async Task JsonCodec_GzipInvalidContainer_IsRejected(string kind)
    {var input=Encode("{}"u8.ToArray(),"gzip");if(kind=="crc")input[^8]^=1;if(kind=="size")input[^4]^=1;if(kind=="trailing")input=input.Concat(new byte[]{0x12}).ToArray();var result=await Read(input,"gzip");Assert.Equal("transform_body_encoding_unsupported",result.Reason);Assert.Null(result.Utf8);}

}
