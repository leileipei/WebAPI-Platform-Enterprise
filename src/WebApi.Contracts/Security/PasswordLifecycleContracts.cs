namespace WebApi.Contracts.Security;
public sealed record ChangePasswordRequest(string CurrentPassword,string NewPassword,string ConfirmPassword);
public sealed record ResetPasswordRequest(string TemporaryPassword);
public sealed record PasswordResetDto(Guid UserId,long Revision,bool MustChangePassword);
