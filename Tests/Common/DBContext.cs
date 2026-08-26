using Microsoft.EntityFrameworkCore;

namespace Tests.Common;

public class DBContext : DbContext
{
    public DBContext(DbContextOptions<DBContext> options) : base(options)
    { }

    public DbSet<Minion> Minions { get; set; }
    public DbSet<Lair> Lairs { get; set; }
    public DbSet<LairAssignment> LairAssignments { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // Only fall back to SQLite when the caller has not chosen a provider, so that passing real options
        // (see TestDatabase.Create) can point this context at PostgreSQL instead
        if (!options.IsConfigured)
        {
            options.UseSqlite($"Data Source={Guid.NewGuid()}.db", null); // in-memory would be prefereable, but is not playing along
        }
    }

    /// <summary>
    /// Which provider this context ended up on. Checked by name so this shared model stays usable whether or not
    /// the Npgsql provider is in play.
    /// </summary>
    public bool IsPostgreSql
    {
        get { return Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true; }
    }

    /// <summary>
    /// Which provider this context ended up on, checked by name for the same reason as <see cref="IsPostgreSql"/>
    /// </summary>
    public bool IsSqlServer
    {
        get { return Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true; }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DBContext).Assembly);

        modelBuilder.Entity<Minion>(henchman =>
        {
            henchman.HasKey(henchman => henchman.MinionID);
            henchman.Property(henchman => henchman.Name).HasMaxLength(100);
            henchman.Property(henchman => henchman.Alias).HasMaxLength(100);
            henchman.Property(henchman => henchman.PreferredCurrency).HasMaxLength(20);
            henchman.Property(henchman => henchman.CauseForDeparture).HasMaxLength(200);
            henchman.HasMany(henchman => henchman.LairAssignments).WithOne(assignment => assignment.Minion);

            if (IsSqlServer)
            {
                // Stated rather than left to EF's default, which is the same decimal(18,2) but logs a warning
                // about it. Note the 2 decimal places: a test value with more precision would be truncated.
                henchman.Property(henchman => henchman.Pay).HasPrecision(18, 2);
            }

            if (IsPostgreSql)
            {
                // Npgsql maps DateTime to 'timestamp with time zone' and rejects any value whose Kind is not Utc.
                // These are plain calendar timestamps with no zone meaning, so map them without a zone.
                henchman.Property(henchman => henchman.BirthDate).HasColumnType("timestamp without time zone");
                henchman.Property(henchman => henchman.HireDate).HasColumnType("timestamp without time zone");
                henchman.Property(henchman => henchman.FireDate).HasColumnType("timestamp without time zone");
            }
        });

        modelBuilder.Entity<Lair>(lair =>
        {
            lair.HasKey(lair => lair.LairID);
            lair.Property(lair => lair.Name).HasMaxLength(100);

            lair.HasMany(lair => lair.Assignments).WithOne(assignment => assignment.Lair);
        });

        modelBuilder.Entity<LairAssignment>(entity =>
        {
            entity.HasKey([nameof(LairAssignment.LairID), nameof(LairAssignment.MinionID)]);
            entity.HasOne(assignment => assignment.Minion).WithMany(henchman => henchman.LairAssignments);
            entity.HasOne(assignment => assignment.Lair).WithMany(lair => lair.Assignments);
        });
    }

    private static List<Lair> GenerateLairs(int count)
    {
        string[] codenameAs = ["Yellow", "Orange", "Red", "Blue", "Purple", "Black", "White", "Burgandy", "Maroon"];
        string[] codenameBs = ["Alpha", "Beta", "Gamma", "Delta", "Omicron", "Iota", "Epsilon"];

        Random random = new();

        List<Lair> lairs = new(count);
        for (int i = 0; i < count; i++)
        {
            string name = $"{codenameAs[random.Next(codenameAs.Length)]} {codenameBs[random.Next(codenameBs.Length)]}";
            var cap = random.Next(3, 100);

            var lair = new Lair() { LairID = Guid.NewGuid(), Name = name, Capacity = cap };
            lairs.Add(lair);
        }

        return lairs;
    }

    private static List<Minion> GenerateMinions(int count, List<Lair> lairs)
    {
        string[] firstNames = ["Theodora", "Lyra", "Ruth", "Lane", "Nicolas", "Noah", "Myles", "Archer", "Roger", "Khloe", "Bryan", "Corbin", "Mattias"];
        string[] lastNames = ["Love", "Crane", "Ayers", "Wall", "Fox", "Hall", "West", "Yoder", "Stone", "Skinner", "Graves", "Gardner", "Mann"];
        string?[] aliases = [null, "Hammer", "Snake", "Viper", "Falcon", "Ghost", "Aardvark", "Maverick", "Klutz"];
        string?[] causes = [null, "Incompetence", "Turncoat", "MIA", "Incinerated", "Disintegrated", "Eaten"];
        string?[] currencies = [null, "US$", "€", "¥", "CN¥", "£", "₿"];

        Random random = new();

        List<Minion> minions = new(count);
        for (int i = 0; i < count; i++)
        {
            string name = $"{firstNames[random.Next(firstNames.Length)]} {lastNames[random.Next(lastNames.Length)]}";
            var alias = aliases[random.Next(aliases.Length)];
            var bday = DateTime.Today.Date - TimeSpan.FromDays((20 * 365) + random.Next(0, 40 * 365));
            var hday = DateTime.Today.Date - TimeSpan.FromDays(random.Next(0, 10 * 365));
            DateTime? fday = (random.Next(0, 5) == 4) ? null : (hday + TimeSpan.FromDays(random.Next(0, 3 * 365)));
            string? cause = (fday == null) ? null : causes[random.Next(causes.Length)];
            var pay = random.Next(500, 5000000);
            string? currency = currencies[random.Next(currencies.Length)];

            var minion = new Minion() { MinionID = Guid.NewGuid(), Name = name, Alias = alias, IsActive = (fday == null), BirthDate = bday, HireDate = hday, FireDate = fday, CauseForDeparture = cause, Pay = pay, PreferredCurrency = currency };
            minions.Add(minion);
        }

        return minions;
    }

    public void GenerateTestSet(int scale = 10)
    {
        var lairs = GenerateLairs(scale);
        lairs.ForEach(lair => Lairs.Add(lair));

        var minions = GenerateMinions(scale * 10, lairs);
        minions.ForEach(minion => Minions.Add(minion));

        Random random = new();

        foreach (var minion in minions)
        {
            var assignToLair = (!minion.IsActive) ? null : lairs[random.Next(lairs.Count)];
            if (assignToLair != null) { LairAssignment assignment = new() { MinionID = minion.MinionID, LairID = assignToLair.LairID }; LairAssignments.Add(assignment); }
        }

        SaveChanges();
    }

    public static DBContext GenerateMinionTestSet()
    {
        var context = new DBContext(new());

        context.Database.EnsureCreated();

        context.GenerateTestSet(10);

        return context;
    }
}