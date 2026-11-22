namespace KatalepsisFT.Domain.Access;

/// <summary>The two roles of the system. Admin can change settings and act on alerts; Viewer can read.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> All = [Admin, Viewer];
}
