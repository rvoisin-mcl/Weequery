namespace Tests.Common;

/// <summary>
/// Shared in-memory test set, so tests can state expectations as sets of names
/// </summary>
public static class MinionTestData
{
    /// <summary>
    /// Alice   active,   pay 12000, born 2000, hired 2018, not fired, vetted,   morale 5,    reviewed 2024-01-15, shift 09:00
    /// Bob     active,   pay 0,     born 1990, hired 2018, fired 2024, no alias, not vetted, morale -3,   never reviewed,     shift 17:30
    /// Charlie inactive, pay 19000, born 1984, hired 2018, not fired, vetting unknown, morale -128, reviewed 2023-06-30, shift 00:00
    /// David   active,   pay 8000,  born 2012, hired 2025, not fired, Irreplacable, vetted, morale 127, reviewed 2025-03-01, shift 23:59
    /// </summary>
    public static IQueryable<Minion> Minions()
    {
        return new List<Minion>
        {
            new() { MinionID = new Guid(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1), Name = "Alice Fox", Alias = "Ghost", IsActive = true, IsVetted = true, BirthDate = new DateTime(2000, 1, 5), HireDate = new DateTime(2018, 10, 15), FireDate = null, CauseForDeparture = null, Pay = 12000, PreferredCurrency = "US$", Classification = Classification.Expendible, Morale = 5, ReviewDate = new DateOnly(2024, 1, 15), ShiftStart = new TimeOnly(9, 0) },
            new() { MinionID = new Guid(2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1), Name = "Bob Samuelson", Alias = null, IsActive = true, IsVetted = false, BirthDate = new DateTime(1990, 11, 5), HireDate = new DateTime(2018, 10, 15), FireDate = new DateTime(2024, 12, 25), CauseForDeparture = "Eaten by shark", Pay = 0, PreferredCurrency = "US$", Classification = Classification.Expendible, Morale = -3, ReviewDate = null, ShiftStart = new TimeOnly(17, 30) },
            new() { MinionID = new Guid(3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1), Name = "Charlie Smith", Alias = "Snake", IsActive = false, IsVetted = null, BirthDate = new DateTime(1984, 1, 5), HireDate = new DateTime(2018, 10, 15), FireDate = null, CauseForDeparture = null, Pay = 19000, PreferredCurrency = "US$", Classification = Classification.Expendible, Morale = -128, ReviewDate = new DateOnly(2023, 6, 30), ShiftStart = new TimeOnly(0, 0) },
            new() { MinionID = new Guid(4, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1), Name = "David Edgars", Alias = "Babyface", IsActive = true, IsVetted = true, BirthDate = new DateTime(2012, 1, 5), HireDate = new DateTime(2025, 8, 15), FireDate = null, CauseForDeparture = null, Pay = 8000, PreferredCurrency = "US$", Classification = Classification.Irreplacable, Morale = 127, ReviewDate = new DateOnly(2025, 3, 1), ShiftStart = new TimeOnly(23, 59) },
        }.AsQueryable();
    }
}
