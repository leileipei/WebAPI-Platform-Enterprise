using System.Security.Cryptography;
using System.Text;
namespace WebApi.PerformanceHost;
public sealed class PrivateSqlShapeAudit
{
    private readonly string directory;
    private readonly object gate=new();
    private readonly HashSet<string> recorded=new(StringComparer.Ordinal);
    public PrivateSqlShapeAudit(string directory){this.directory=Path.GetFullPath(directory);CheckDirectory();}
    private void CheckDirectory(){
        if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("SQL audit requires Unix private-file modes.");
        var info=new DirectoryInfo(directory);
        if(!info.Exists||info.LinkTarget is not null||File.GetUnixFileMode(directory)!=(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute))throw new InvalidDataException("SQL audit directory is not private.");
    }
    public void Record(string hash,string sql){
        if(OperatingSystem.IsWindows())throw new PlatformNotSupportedException("SQL audit requires Unix private-file modes.");
        var bytes=Encoding.UTF8.GetBytes(sql);
        if(bytes.Length>1024*1024||hash!=Convert.ToHexStringLower(SHA256.HashData(bytes)))throw new InvalidDataException("Invalid SQL audit hash or size.");
        CheckDirectory();
        lock(gate){
            if(recorded.Contains(hash))return;
            var path=Path.Combine(directory,hash+".sql");var info=new FileInfo(path);
            if(info.LinkTarget is not null)throw new InvalidDataException("SQL audit file must not be symbolic.");
            if(info.Exists){
                if(File.GetUnixFileMode(path)!=(UnixFileMode.UserRead|UnixFileMode.UserWrite)||info.Length!=bytes.Length||!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))throw new InvalidDataException("Existing SQL audit file is invalid.");
            }else{
                using var file=new FileStream(path,new FileStreamOptions {Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite});file.Write(bytes);
            }
            recorded.Add(hash);
        }
    }
}
