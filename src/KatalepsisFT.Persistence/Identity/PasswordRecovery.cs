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
