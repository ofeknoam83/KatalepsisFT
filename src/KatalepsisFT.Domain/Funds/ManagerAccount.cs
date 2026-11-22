namespace KatalepsisFT.Domain.Funds;

public enum ManagerDataSource
{
    /// <summary>Invented positions of the built-in example fund.</summary>
    Demo = 1,

    /// <summary>Positions entered by hand or uploaded.</summary>
    Manual = 2,

    /// <summary>Positions read from venue connections.</summary>
    Venue = 3,
}

/// <summary>One manager's book inside a sleeve.</summary>
public class ManagerAccount
{
    public int Id { get; set; }
    public int SleeveId { get; set; }
    public Sleeve Sleeve { get; set; } = null!;
    public required string Name { get; set; }
    public DateOnly InceptionDate { get; set; }
    public decimal AllocatedCapitalUsd { get; set; }
    public ManagerDataSource DataSource { get; set; }
    public bool IsActive { get; set; } = true;
}
