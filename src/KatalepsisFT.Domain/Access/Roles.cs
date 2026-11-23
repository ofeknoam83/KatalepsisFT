namespace KatalepsisFT.Domain.Access;

/// <summary>
/// The roles of the system. Viewer can read. Trader can also send and cancel orders and pull a kill switch. Admin
/// can change settings, keys and limits, and everything a Trader can.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";
    public const string Trader = "Trader";

    /// <summary>Who may send orders.</summary>
    public const string CanTrade = Admin + "," + Trader;

    public static readonly IReadOnlyList<string> All = [Admin, Viewer, Trader];
}
