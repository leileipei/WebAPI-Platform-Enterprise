using System.Text;
using static WebApi.Domain.Policies.PolicyConfigurationValidator;

namespace WebApi.Domain.Policies;

public static class JsonPointerPath
{
    public static IReadOnlyList<string> Parse(string pointer)
    {
        if (pointer is null || pointer.Length > 512 || pointer.Any(char.IsControl)) throw Invalid();
        if (pointer.Length == 0) return Array.AsReadOnly(Array.Empty<string>());
        if (pointer[0] != '/') throw Invalid();
        var raw = pointer[1..].Split('/');
        if (raw.Length > 32) throw Invalid();
        var tokens = new string[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var text = new StringBuilder();
            for (var j = 0; j < raw[i].Length; j++)
            {
                var c = raw[i][j];
                if (c != '~') { text.Append(c); continue; }
                if (++j >= raw[i].Length || raw[i][j] is not ('0' or '1')) throw Invalid();
                text.Append(raw[i][j] == '0' ? '~' : '/');
            }
            tokens[i] = text.ToString();
        }
        return Array.AsReadOnly(tokens);
    }
}
