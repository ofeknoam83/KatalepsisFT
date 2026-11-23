using System.Security.Cryptography;
using KatalepsisFT.Application.Auth;
using KatalepsisFT.Domain.Access;
using KatalepsisFT.Persistence.Identity;
using KatalepsisFT.Web.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KatalepsisFT.Web.Auth;

public sealed record UserDto(int Id, string Email, string DisplayName, string Role, bool IsDisabled, bool MustChangePassword, DateTimeOffset CreatedAt);

/// <param name="TemporaryPassword">Shown this once. The person must change it the first time they sign in.</param>
public sealed record UserCreatedDto(UserDto User, string TemporaryPassword);

public sealed record SaveUserRequest(string Email, string DisplayName, string Role);

public sealed record SetRoleRequest(string Role);

public sealed record SetDisabledRequest(bool Disabled);

/// <summary>
/// The people who can sign in. Two-person approval needs at least two administrators, so an administrator can add
/// others here. Nobody can change their own role or switch themselves off, and the last administrator cannot be removed.
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize(Roles = Roles.Admin)]
public sealed class UsersController(UserManager<AppUser> users) : ControllerBase
{
    [HttpGet]
    public async Task<List<UserDto>> ListAsync(CancellationToken cancellationToken)
    {
        List<AppUser> all = await users.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(cancellationToken);
        List<UserDto> result = [];
        foreach (AppUser user in all)
        {
            result.Add(await DtoAsync(user));
        }

        return result;
    }

    [Audit("User added")]
    [HttpPost]
    public async Task<IActionResult> AddAsync(SaveUserRequest request)
    {
        string email = (request.Email ?? string.Empty).Trim();
        string name = (request.DisplayName ?? string.Empty).Trim();
        if (email.Length is 0 or > 254 || !global::System.Net.Mail.MailAddress.TryCreate(email, out _) || name.Length is 0 or > 100 || !Roles.All.Contains(request.Role))
        {
            return BadRequest(new ErrorResponse("Give an email address, a name, and a role: Admin, Trader or Viewer."));
        }

        if (await users.FindByEmailAsync(email) is not null)
        {
            return Conflict(new ErrorResponse("Somebody already signs in with that email."));
        }

        string password = TemporaryPassword();
        AppUser user = new() { UserName = email, Email = email, EmailConfirmed = true, DisplayName = name, MustChangePassword = true, CreatedAt = DateTimeOffset.UtcNow };
        IdentityResult created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return BadRequest(new ErrorResponse(string.Join(" ", created.Errors.Select(e => e.Description))));
        }

        await users.AddToRoleAsync(user, request.Role);
        return Ok(new UserCreatedDto(await DtoAsync(user), password));
    }

    [Audit("User's role changed")]
    [HttpPost("{id:int}/role")]
    public async Task<IActionResult> SetRoleAsync(int id, SetRoleRequest request)
    {
        if (!Roles.All.Contains(request.Role))
        {
            return BadRequest(new ErrorResponse("The role is Admin, Trader or Viewer."));
        }

        AppUser? user = await users.FindByIdAsync(id.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        if (user is null)
        {
            return NotFound();
        }

        if (IsSelf(user) || await IsLastAdminAsync(user, request.Role))
        {
            return Conflict(new ErrorResponse(IsSelf(user) ? "Your own role has to be changed by another administrator." : "There must always be an administrator."));
        }

        await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user));
        await users.AddToRoleAsync(user, request.Role);
        // Whatever the person had open stops working with the old role.
        await users.UpdateSecurityStampAsync(user);
        return Ok(await DtoAsync(user));
    }

    [Audit("User switched off or on")]
    [HttpPost("{id:int}/disabled")]
    public async Task<IActionResult> SetDisabledAsync(int id, SetDisabledRequest request)
    {
        AppUser? user = await users.FindByIdAsync(id.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        if (user is null)
        {
            return NotFound();
        }

        if (request.Disabled && (IsSelf(user) || await IsLastAdminAsync(user, Roles.Viewer)))
        {
            return Conflict(new ErrorResponse(IsSelf(user) ? "You cannot switch yourself off." : "There must always be an administrator."));
        }

        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, request.Disabled ? DateTimeOffset.MaxValue : null);
        await users.UpdateSecurityStampAsync(user);
        return Ok(await DtoAsync(user));
    }

    /// <summary>Gives the person a new temporary password, for when they have lost theirs.</summary>
    [Audit("User's password reset")]
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPasswordAsync(int id)
    {
        AppUser? user = await users.FindByIdAsync(id.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
        if (user is null)
        {
            return NotFound();
        }

        if (IsSelf(user))
        {
            return Conflict(new ErrorResponse("Change your own password from the menu at the top right."));
        }

        string password = TemporaryPassword();
        IdentityResult reset = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
        if (!reset.Succeeded)
        {
            return BadRequest(new ErrorResponse(string.Join(" ", reset.Errors.Select(e => e.Description))));
        }

        user.MustChangePassword = true;
        await users.UpdateAsync(user);
        return Ok(new UserCreatedDto(await DtoAsync(user), password));
    }

    private bool IsSelf(AppUser user) => string.Equals(user.UserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this user is the only administrator who can still sign in and would stop being one.</summary>
    private async Task<bool> IsLastAdminAsync(AppUser user, string newRole)
    {
        if (newRole == Roles.Admin || !await users.IsInRoleAsync(user, Roles.Admin))
        {
            return false;
        }

        IList<AppUser> admins = await users.GetUsersInRoleAsync(Roles.Admin);
        return !admins.Any(a => a.Id != user.Id && (a.LockoutEnd is null || a.LockoutEnd <= DateTimeOffset.UtcNow));
    }

    private async Task<UserDto> DtoAsync(AppUser user) =>
        new(user.Id, user.Email ?? string.Empty, user.DisplayName, (await users.GetRolesAsync(user)).FirstOrDefault() ?? Roles.Viewer, user.LockoutEnd is DateTimeOffset end && end > DateTimeOffset.UtcNow, user.MustChangePassword, user.CreatedAt);

    /// <summary>Twenty random characters with every kind the password rules ask for.</summary>
    private static string TemporaryPassword() => "Kf-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + "-" + RandomNumberGenerator.GetInt32(100, 999) + "A";
}
