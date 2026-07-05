namespace Fathom.SqlServer;

public sealed class FathomOptions
{
    public const string SectionName = "Fathom";

    /// <summary>Connection string for the database being exported from, and Fathom's system tables.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Directory where registered export definitions are persisted as JSON.</summary>
    public string DefinitionDirectory { get; set; } = "data/exports";

    /// <summary>
    /// Per-SQL-command timeout for an export run: bounds each staging statement and the start
    /// of each level's read. Guards against a pathological query; it does not (yet) bound a
    /// client stalling mid-stream — that is tracked on the roadmap.
    /// </summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
