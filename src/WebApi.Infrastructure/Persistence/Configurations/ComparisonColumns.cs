using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace WebApi.Infrastructure.Persistence.Configurations;
internal static partial class ComparisonColumns
{
    [GeneratedRegex("([a-z0-9])([A-Z])")] private static partial Regex Boundary();
    internal static void Map<T>(EntityTypeBuilder<T> b,string table) where T:class
    {
        b.ToTable(table);b.HasKey("Id");
        foreach(var p in typeof(T).GetProperties())b.Property(p.Name).HasColumnName(Boundary().Replace(p.Name,"$1_$2").ToLowerInvariant());
        b.Property("Id").HasDefaultValueSql("gen_random_uuid()");
        foreach(var name in new[]{"InputFingerprint","ReportHash"})b.Property(name).HasMaxLength(64);
        b.ToTable(t=>t.HasCheckConstraint("ck_"+table+"_hashes","input_fingerprint ~ '^[0-9a-f]{64}$' AND report_hash ~ '^[0-9a-f]{64}$'"));
    }
}
