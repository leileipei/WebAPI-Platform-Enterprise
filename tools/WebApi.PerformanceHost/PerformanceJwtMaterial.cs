using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebApi.Contracts.Common;
using WebApi.Contracts.Policies;
using WebApi.Domain.Policies;
namespace WebApi.PerformanceHost;
public sealed class PerformanceJwtMaterial(RSA rsa,ECDsa ec) : IDisposable
{
    private const string Issuer="https://performance.webapi.invalid";
    private const string Audience="webapi-performance";
    private readonly byte[] credentialSeed=SHA256.HashData(rsa.ExportPkcs8PrivateKey());
    private static string Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static PerformanceJwtMaterial Create()=>new(RSA.Create(2048),ECDsa.Create(ECCurve.NamedCurves.nistP256));
    public static PerformanceJwtMaterial LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        if(new DirectoryInfo(directory).LinkTarget is not null)throw new InvalidOperationException("Private fixture directory must not be a link.");
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var rsa=RSA.Create(2048);var ec=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try {
            Load(Path.Combine(directory,"rsa.pem"),rsa.ExportPkcs8PrivateKeyPem,rsa.ImportFromPem);
            Load(Path.Combine(directory,"ec.pem"),ec.ExportPkcs8PrivateKeyPem,ec.ImportFromPem);
            return new(rsa,ec);
        }catch {rsa.Dispose();ec.Dispose();throw;}
    }
    private delegate void ImportPem(ReadOnlySpan<char> pem);
    private static void Load(string file,Func<string> export,ImportPem import)
    {
        if(File.Exists(file)) {
            var info=new FileInfo(file);
            if(info.LinkTarget is not null||info.Length>16384)throw new InvalidOperationException("Invalid fixture private key file.");
            if(!OperatingSystem.IsWindows()&&File.GetUnixFileMode(file)!=(UnixFileMode.UserRead|UnixFileMode.UserWrite))
                throw new InvalidOperationException("Fixture private key is not private.");
            import(File.ReadAllText(file));return;
        }
        using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write))using(var writer=new StreamWriter(stream))writer.Write(export());
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
    public string CredentialSecret(int index)=>Url(HMACSHA256.HashData(credentialSeed,Encoding.ASCII.GetBytes(index.ToString(CultureInfo.InvariantCulture))));
    private object Jwk(string algorithm)
    {
        if(algorithm=="RS256") {var p=rsa.ExportParameters(false);return new {kid="perf-rs256",kty="RSA",alg="RS256",use="sig",n=Url(p.Modulus!),e=Url(p.Exponent!)};}
        if(algorithm=="ES256") {var p=ec.ExportParameters(false);return new {kid="perf-es256",kty="EC",alg="ES256",use="sig",crv="P-256",x=Url(p.Q.X!),y=Url(p.Q.Y!)};}
        throw new ArgumentException("Unsupported fixture algorithm.");
    }
    public string Configuration(string algorithm,IReadOnlyList<JwtApplicationMapping> mappings)=>PolicyConfigurationValidator.Normalize("authentication",JsonSerializer.Serialize(new {
        mode="JWT",issuer=Issuer,audiences=new[]{Audience},allowedAlgorithms=new[]{algorithm},allowedTokenTypes=new[]{"JWT"},clockSkewSeconds=0,
        maxTokenLifetimeSeconds=86400,applicationClaim="application",applicationMappings=mappings,jwks=new {keys=new[]{Jwk(algorithm)}},forwardBearer=false},CanonicalJson.Options));
    public string Token(string algorithm,string claimValue,DateTimeOffset start,DateTimeOffset end)
    {
        if(end<=start||end-start>TimeSpan.FromHours(24))throw new ArgumentException("Invalid fixture JWT time window.");
        var header=Url(JsonSerializer.SerializeToUtf8Bytes(new {alg=algorithm,kid=algorithm=="RS256"?"perf-rs256":"perf-es256",typ="JWT"}));
        var body=Url(JsonSerializer.SerializeToUtf8Bytes(new {iss=Issuer,aud=Audience,sub="perf-subject",iat=start.ToUnixTimeSeconds(),nbf=start.ToUnixTimeSeconds(),exp=end.ToUnixTimeSeconds(),application=claimValue}));
        var signing=Encoding.ASCII.GetBytes(header+"."+body);
        var signature=algorithm switch {
            "RS256"=>rsa.SignData(signing,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1),
            "ES256"=>ec.SignData(signing,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
            _=>throw new ArgumentException("Unsupported fixture algorithm.")};
        return header+"."+body+"."+Url(signature);
    }
    public void Dispose(){rsa.Dispose();ec.Dispose();CryptographicOperations.ZeroMemory(credentialSeed);}
}
