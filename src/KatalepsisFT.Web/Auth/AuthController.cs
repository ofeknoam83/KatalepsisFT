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
    IValidator<ChangePasswordRequest> changePasswordValidator
) : ControllerBase
{
    private const string WrongCredentials = "Email or password is incorrect.";

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
            return Unauthorized(new ErrorResponse(WrongCredentials));
        }

        SignInResult result = await signIn.PasswordSignInAsync(user, request.Password, request.RememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new ErrorResponse("Too many failed attempts. Try again in 15 minutes."));
        }

        if (!result.Succeeded)
        {
            return Unauthorized(new ErrorResponse(WrongCredentials));
        }

        return Ok(await DescribeAsync(user));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync()
    {
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

        // Issues a new cookie: the old one carried the must-change flag and the previous security stamp.
        await signIn.RefreshSignInAsync(user);
        return Ok(await DescribeAsync(user));
    }

    private async Task<SessionResponse> DescribeAsync(AppUser user)
    {
        IList<string> roles = await users.GetRolesAsync(user);
        return new SessionResponse(user.Email ?? string.Empty, user.DisplayName, roles.Order().ToList(), user.MustChangePassword);
    }
}
