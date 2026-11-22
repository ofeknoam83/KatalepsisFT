using System.Security.Claims;
using KatalepsisFT.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace KatalepsisFT.Web.Auth;

public static class FtClaims
{
    /// <summary>Present on a session whose account still has a password someone else chose.</summary>
    public const string MustChangePassword = "kft:must_change_password";
}

public sealed class FtClaimsPrincipalFactory(UserManager<AppUser> users, RoleManager<IdentityRole<int>> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<int>>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        ClaimsIdentity identity = await base.GenerateClaimsAsync(user);
        if (user.MustChangePassword)
        {
            identity.AddClaim(new Claim(FtClaims.MustChangePassword, "true"));
        }

        return identity;
    }
}
