using FluentValidation;
using FluentValidation.Results;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Persistence.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace KatalepsisFT.Web.Auth;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    SignInManager<AppUser> signIn,
    UserManager<AppUser> users,
    IValidator<LoginRequest> loginValidator,
    IValidator<ChangePasswordRequest> changePasswordValidator,
    KatalepsisFT.Application.Audit.AuditLog audit
) : ControllerBase
{
    private const string WrongCredentials = "Email or password is incorrect.";

    /// <summary>
    /// Said whenever sign-in is refused, whatever the reason, so that the answer does not tell a stranger which emails
    /// have an account or which of them are locked. The hint about locking is there for the person who is locked out.
    /// </summary>
    private const string Refused = WrongCredentials + " After five wrong attempts an account is locked for 15 minutes.";

    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<AppUser>().HashPassword(new AppUser(), "not a real password"));

    [AllowAnonymous]
    [EnableRateLimiting("sign-in")]
    [HttpPost("login")]
    public async Task<IActionResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await loginValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return BadRequest(new ErrorResponse(validation.Errors[0].ErrorMessage));
        }

        AppUser? user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // What was typed is not written down: anyone can send anything here, and the audit trail is not theirs to fill.
            await NoteAsync("(unknown email)", "Sign-in refused", "unknown email", cancellationToken);
            // The same work as for a real account, so that the time the answer takes does not give the email away.
            users.PasswordHasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, request.Password);
            return Unauthorized(new ErrorResponse(Refused));
        }

        SignInResult result = await signIn.PasswordSignInAsync(user, request.Password, request.RememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            await NoteAsync(user.Email ?? request.Email, "Sign-in refused", "account locked after repeated failures", cancellationToken);
            return Unauthorized(new ErrorResponse(Refused));
        }

        if (!result.Succeeded)
        {
            await NoteAsync(user.Email ?? request.Email, "Sign-in refused", "wrong password", cancellationToken);
            return Unauthorized(new ErrorResponse(Refused));
        }

        // The same name every other entry carries for this user.
        await NoteAsync(user.UserName ?? user.Email ?? request.Email, "Signed in", null, cancellationToken);
        return Ok(await DescribeAsync(user));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync()
    {
        await NoteAsync(User.Identity?.Name ?? "Unknown", "Signed out", null, HttpContext.RequestAborted);
        await signIn.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    public async Task<IActionResult> MeAsync()
    {
        AppUser? user = await users.GetUserAsync(User);
        return user is null ? Unauthorized() : Ok(await DescribeAsync(user));
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await changePasswordValidator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return BadRequest(new ErrorResponse(validation.Errors[0].ErrorMessage));
        }

        AppUser? user = await users.GetUserAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        IdentityResult changed = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!changed.Succeeded)
        {
            return BadRequest(new ErrorResponse(string.Join(" ", changed.Errors.Select(e => e.Description))));
        }

        if (user.MustChangePassword)
        {
            user.MustChangePassword = false;
            await users.UpdateAsync(user);
        }

        await NoteAsync(User.Identity?.Name ?? user.Email ?? "Unknown", "Password changed", null, cancellationToken);
        // Issues a new cookie: the old one carried the must-change flag and the previous security stamp.
        await signIn.RefreshSignInAsync(user);
        return Ok(await DescribeAsync(user));
    }

    /// <summary>Signing in and out belongs in the audit trail too. A trail that cannot be written must not lock anyone out.</summary>
    private async Task NoteAsync(string who, string action, string? detail, CancellationToken cancellationToken)
    {
        try
        {
            await audit.RecordAsync(who, action, KatalepsisFT.Application.Audit.AuditTarget.None, detail, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<AuthController>>().LogError(e, "An audit entry for \"{Action}\" could not be written.", action);
        }
    }

    private async Task<SessionResponse> DescribeAsync(AppUser user)
    {
        IList<string> roles = await users.GetRolesAsync(user);
        return new SessionResponse(user.Email ?? string.Empty, user.DisplayName, roles.Order().ToList(), user.MustChangePassword);
    }
}
