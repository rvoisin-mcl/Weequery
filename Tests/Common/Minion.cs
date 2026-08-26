namespace Tests.Common;

public class Minion
{
    public static readonly Weequery.BindingRequest[] Bindings =
    [
        new(nameof(Minion.MinionID), null),
        new(nameof(Minion.Name), null),
        new(nameof(Minion.Alias), null),
        new(nameof(Minion.Pay), null),
        new(nameof(Minion.PreferredCurrency), null),
        new(nameof(Minion.IsActive), null),
        new(nameof(Minion.IsVetted), null),
        new(nameof(Minion.BirthDate), null),
        new(nameof(Minion.HireDate), null),
        new(nameof(Minion.FireDate), null),
        new(nameof(Minion.CauseForDeparture), null),
        new(nameof(Minion.Classification), null),
        new(nameof(Minion.Morale), null),
        new(nameof(Minion.ReviewDate), null),
        new(nameof(Minion.ShiftStart), null),
    ];

    public Guid MinionID { get; set; }

    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    public decimal Pay { get; set; }
    public string? PreferredCurrency { get; set; }
    public bool IsActive { get; set; }

    /// <summary>Nullable on purpose: null means not yet assessed, so the model covers bool? as well as bool</summary>
    public bool? IsVetted { get; set; }

    public DateTime? BirthDate { get; set; }
    public DateTime HireDate { get; set; }
    public DateTime? FireDate { get; set; }
    public string? CauseForDeparture { get; set; }
    public Classification Classification { get; set; }

    /// <summary>sbyte, so the signed small integer type is covered, including its negative range</summary>
    public sbyte Morale { get; set; }

    /// <summary>DateOnly, nullable so the Nullable&lt;&gt; branch is covered too</summary>
    public DateOnly? ReviewDate { get; set; }

    /// <summary>TimeOnly</summary>
    public TimeOnly ShiftStart { get; set; }

    public List<LairAssignment>? LairAssignments { get; set; }
}