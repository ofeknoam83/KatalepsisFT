using System.Security.Cryptography;
using KatalepsisFT.Domain.Audit;
using Microsoft.AspNetCore.Identity;

namespace KatalepsisFT.Persistence.Identity;

/// <summary>
/// The way back in when the only people who could reset a password are locked out themselves. It is not reachable
/// from the website: it runs as a command on the server, by someone who already holds the hosting account.
/// </summary>
public sealed class PasswordRecovery(UserManager<AppUser> users, FtDbContext database, TimeProvider clock)
{
    /// <summary>
    /// Makes an existing account an administrator. This is how a second administrator is appointed when only one is
    /// left: from the server, by someone who holds the hosting account, and not by the remaining administrator alone.
    /// </summary>
    /// <returns>False when there is no such account.</returns>
    /// <summary>
    /// Lets an administrator approve their own trading requests, or takes that away again. Only an administrator
    /// can have it; a person who is not one is refused.
    /// </summary>
    /// <returns>Null when done; otherwise why not.</returns>
    public async Task<string?> SetApprovesAloneAsync(string email, bool allow, CancellationToken cancellationToken)
    {
        AppUser? user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            return "There is no account with that email.";
        }

        if (allow && !await users.IsInRoleAsync(user, KatalepsisFT.Domain.Access.Roles.Admin))
        {
            return "That account is not an administrator. Only an administrator can approve requests.";
        }

        user.ApprovesAlone = allow;
        IdentityResult changed = await users.UpdateAsync(user);
        if (!changed.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", changed.Errors.Select(e => e.Description)));
        }

        database.AuditEntries.Add(new AuditEntry { At = clock.GetUtcNow(), UserName = "Server command", Action = allow ? "Allowed to approve own trading requests, from the server" : "No longer allowed to approve own trading requests, from the server", Subject = user.DisplayName.Length > 0 ? user.DisplayName : "User " + user.Id });
        await database.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<bool> MakeAdminAsync(string email, CancellationToken cancellationToken)
    {
        AppUser? user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            return false;
        }

        user.AdminProposedBy = null;
        IList<string> roles = await users.GetRolesAsync(user);
        IdentityResult changed = await users.RemoveFromRolesAsync(user, roles);
        changed = changed.Succeeded ? await users.AddToRoleAsync(user, KatalepsisFT.Domain.Access.Roles.Admin) : changed;
        changed = changed.Succeeded ? await users.UpdateSecurityStampAsync(user) : changed;
        if (!changed.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", changed.Errors.Select(e => e.Description)));
        }

        database.AuditEntries.Add(new AuditEntry { At = clock.GetUtcNow(), UserName = "Server command", Action = "Made an administrator from the server", Subject = user.Email });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Gives the account a new temporary password and returns it; null when there is no such account.</summary>
    public async Task<string?> ResetAsync(string email, CancellationToken cancellationToken)
    {
        AppUser? user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            return null;
        }

        string password = "Kf-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + "-" + RandomNumberGenerator.GetInt32(100, 999) + "A";
        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        user.MustChangePassword = true;
        user.AccessFailedCount = 0;
        DateTimeOffset now = clock.GetUtcNow();
        // A lock from too many wrong passwords is lifted. An account an Admin switched off stays off.
        if (user.LockoutEnd is DateTimeOffset end && end <= now + TimeSpan.FromDays(1))
        {
            user.LockoutEnd = null;
        }

        // A new security stamp signs the account out everywhere it is still signed in.
        IdentityResult stamped = await users.UpdateSecurityStampAsync(user);
        if (!stamped.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", stamped.Errors.Select(e => e.Description)));
        }

        database.AuditEntries.Add(new AuditEntry { At = now, UserName = "Server command", Action = "Password reset from the server", Subject = user.Email });
        await database.SaveChangesAsync(cancellationToken);
        return password;
    }
}
