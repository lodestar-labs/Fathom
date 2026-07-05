using Fathom.Core;
using Fathom.Core.Lookups;
using Microsoft.Extensions.DependencyInjection;

namespace Fathom.SqlServer;

public static class FathomSqlServerExtensions
{
    public static FathomBuilder UseSqlServer(this FathomBuilder builder)
    {
        builder.Services.AddSingleton<SqlConnectionFactory>();
        builder.Services.AddSingleton<FilterResolver>();
        builder.Services.AddSingleton<ExportQueryEngine>();
        return builder;
    }

    /// <summary>
    /// Registers the built-in TblCode/TblCodeType reference-data lookup under
    /// <paramref name="name"/> for both output rendering and request-filter resolution. One
    /// call per code list (e.g. "countries", "species").
    /// </summary>
    public static FathomBuilder AddCodeListLookup(
        this FathomBuilder builder,
        string name,
        string codeType,
        string codeTable = "TblCode",
        string codeTypeTable = "TblCodeType",
        string idColumn = "TblCodeID",
        string codeColumn = "Code",
        string codeTypeIdColumn = "TblCodeTypeID",
        string codeTypeNameColumn = "CodeType")
    {
        CodeListLookupProvider? shared = null;
        CodeListLookupProvider Factory(IServiceProvider sp) => shared ??= new CodeListLookupProvider(
            name, codeType, sp.GetRequiredService<SqlConnectionFactory>(),
            codeTable, codeTypeTable, idColumn, codeColumn, codeTypeIdColumn, codeTypeNameColumn);

        // Both registrations resolve to the same instance (see Factory's shared-instance
        // capture), so the export- and request-side caches for this code list are one cache.
        builder.Services.AddSingleton<IExportLookupProvider>(Factory);
        builder.Services.AddSingleton<IRequestLookupProvider>(Factory);
        return builder;
    }

    /// <summary>Registers a code list from a bound <see cref="CodeListLookupOptions"/> — the config-driven equivalent of the named-parameter overload.</summary>
    public static FathomBuilder AddCodeListLookup(this FathomBuilder builder, CodeListLookupOptions options) =>
        builder.AddCodeListLookup(
            options.Name, options.CodeType, options.CodeTable, options.CodeTypeTable,
            options.IdColumn, options.CodeColumn, options.CodeTypeIdColumn, options.CodeTypeNameColumn);
}
