namespace Fathom.SqlServer;

public sealed class FathomOptions
{
    public const string SectionName = "Fathom";

    /// <summary>Connection string for the database being exported from, and Fathom's system tables.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Directory where registered export definitions are persisted as JSON.</summary>
    public string DefinitionDirectory { get; set; } = "data/exports";

    /// <summary>
    /// Abandon an export run that has produced no progress for this long. Guards against a
    /// pathological query or a client that never reads its response stream.
    /// </summary>
    public TimeSpan ExportTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Batch size for SqlBulkCopy-style staging reads (rows per fetch), tuned for throughput over latency.</summary>
    public int FetchBufferSize { get; set; } = 2000;
}
