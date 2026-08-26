namespace Tests.Common;

public class LairAssignment
{
    public Guid LairID { get; set; }
    public Lair? Lair { get; set; }

    public Guid MinionID { get; set; }
    public Minion? Minion { get; set; }
}