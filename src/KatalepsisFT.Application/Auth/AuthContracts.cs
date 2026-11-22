namespace KatalepsisFT.Application.Auth;

public sealed record LoginRequest(string Email, string Password, bool RememberMe);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record SessionResponse(string Email, string DisplayName, IReadOnlyList<string> Roles, bool MustChangePassword);

public sealed record ErrorResponse(string Message);
