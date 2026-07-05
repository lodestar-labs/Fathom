namespace Fathom.SqlServer;

/// <summary>
/// Configuration-driven registration for a <see cref="CodeListLookupProvider"/> — bind an
/// array of these from e.g. <c>Fathom:CodeListLookups</c> in appsettings and register each
/// with <c>AddCodeListLookup(options)</c>, wiring a code list into both export rendering and
/// request-filter resolution without writing code.
/// </summary>
public sealed class CodeListLookupOptions
{
    /// <summary>Name other configuration refers to this lookup by (a field's <c>lookup</c>, a filter's <c>requestLookup</c>).</summary>
    public required string Name { get; set; }

    /// <summary>The <c>CodeType</c> row this list corresponds to in <see cref="CodeTypeTable"/>.</summary>
    public required string CodeType { get; set; }

    public string CodeTable { get; set; } = "TblCode";

    public string CodeTypeTable { get; set; } = "TblCodeType";

    public string IdColumn { get; set; } = "TblCodeID";

    public string CodeColumn { get; set; } = "Code";

    public string CodeTypeIdColumn { get; set; } = "TblCodeTypeID";

    public string CodeTypeNameColumn { get; set; } = "CodeType";
}
