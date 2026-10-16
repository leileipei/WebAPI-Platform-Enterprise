using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Security;
public static class PasswordPolicyValidator
{
    public static void Validate(string password,int minLength,string complexity)
    {
        if(password is null||password.Length<minLength||password.Length>1024)throw Invalid();
        var complex=complexity switch
        {
            "LengthOnly"=>true,
            "LettersAndDigits"=>password.Any(char.IsLetter)&&password.Any(char.IsDigit),
            "UpperLowerDigitSpecial"=>password.Any(char.IsUpper)&&password.Any(char.IsLower)&&password.Any(char.IsDigit)&&password.Any(c=>!char.IsLetterOrDigit(c)&&!char.IsWhiteSpace(c)),
            _=>false
        };
        if(!complex)throw Invalid();
    }
    private static ApiException Invalid()=>new(422,"invalid_fields","密码不符合当前长度或复杂度策略。");
}
