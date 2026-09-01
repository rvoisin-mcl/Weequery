using Tests.Common;

namespace Tests.Unit;

/// <summary>
/// The report a disagreement writes to disk.
/// </summary>
/// <remarks>
/// Exercised directly rather than through a real disagreement, because there isn't one to arrange: the tests it
/// serves pass, and the failure it exists for has never reproduced on demand. So the writing is tested here and
/// the report's contents are tested by being the thing that failure prints.
/// </remarks>
public class QueryAgreementCaptureTests
{
    [Fact]
    public void AReportIsWrittenWhereItCanBeReadAfterTheRun()
    {
        var report = $"a report for {nameof(AReportIsWrittenWhereItCanBeReadAfterTheRun)}";

        var path = QueryAgreement.Capture(report, "Alias IsBetween ('B', 'S')");

        try
        {
            Assert.True(File.Exists(path), $"nothing was written, Capture said '{path}'");
            Assert.Equal(report, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// The query names the file, so several failures in one run are told apart by more than the time, and
    /// whatever punctuation it held does not reach the file system
    /// </summary>
    [Fact]
    public void TheFileIsNamedForTheQuery()
    {
        var path = QueryAgreement.Capture("a report", "Alias IsBetween ('B', 'S')");

        try
        {
            var name = Path.GetFileName(path);

            Assert.Contains("Alias", name);
            Assert.Contains("IsBetween", name);
            Assert.DoesNotContain("'", name);
            Assert.DoesNotContain("(", name);
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// A query long enough to make an unusable file name is cut down rather than allowed to fail the write, which
    /// would lose the report it was meant to keep
    /// </summary>
    [Fact]
    public void AVeryLongQueryStillWrites()
    {
        var path = QueryAgreement.Capture("a report", string.Join(" || ", Enumerable.Repeat("(Pay > 1)", 200)));

        try
        {
            Assert.True(File.Exists(path), $"nothing was written, Capture said '{path}'");
        }
        finally
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
    }

    /// <summary>
    /// It never throws. Whatever goes wrong writing a diagnostic must not replace the failure being diagnosed, so
    /// the reason comes back in place of the path.
    /// </summary>
    [Fact]
    public void AFailureToWriteIsReportedRatherThanThrown()
    {
        var path = QueryAgreement.Capture("a report", new string('\0', 10));

        Assert.NotNull(path);
    }
}
