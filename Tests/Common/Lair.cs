namespace Tests.Common;

public class Lair
{
    public Guid LairID { get; set; }
    public string Name { get; set; } = "";
    public int Capacity { get; set; }

    public List<LairAssignment>? Assignments { get; set; }
}