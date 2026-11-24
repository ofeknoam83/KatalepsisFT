using Microsoft.AspNetCore.Identity;

namespace KatalepsisFT.Persistence.Identity;

public class AppUser : IdentityUser<int>
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Set for accounts created with a password someone else chose. Until it is cleared the account can only change its password.</summary>
    public bool MustChangePassword { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Set while this person has been put forward as an administrator and a second administrator has not yet agreed:
    /// the name of whoever put them forward. Until it is cleared the person keeps the role they had.
    /// </summary>
    public string? AdminProposedBy { get; set; }
}
