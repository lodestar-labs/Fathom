using System.Globalization;
using Fathom.Core.Lookups;
using Microsoft.Data.SqlClient;

namespace Fathom.SqlServer;

/// <summary>
/// The built-in reference-data lookup: a TblCode/TblCodeType-shaped table — the same
/// convention Loadstone's code lists use, so the same reference data serves both import and
/// export. Loaded once per code type and cached in memory (reference data changes rarely;
/// restart the host, or extend this class, if you need live invalidation).
///
/// Implements both lookup directions from one cache: <see cref="IExportLookupProvider"/>
/// renders a database id as its code string for output, <see cref="IRequestLookupProvider"/>
/// resolves a client-supplied code string back to the id a filter needs. Table and column
/// names are configurable for schemas that don't match the default convention exactly; for
/// anything structurally different, implement the two interfaces directly instead — each is
/// a single method.
/// </summary>
public sealed class CodeListLookupProvider(
    string name,
    string codeType,
    SqlConnectionFactory connectionFactory,
    string codeTable = "TblCode",
    string codeTypeTable = "TblCodeType",
    string idColumn = "TblCodeID",
    string codeColumn = "Code",
    string codeTypeIdColumn = "TblCodeTypeID",
    string codeTypeNameColumn = "CodeType")
    : IExportLookupProvider, IRequestLookupProvider
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Dictionary<string, string>? _idToCode;
    private Dictionary<string, string>? _codeToId;

    public string Name { get; } = name;

    public async ValueTask<string?> ResolveAsync(string rawValue, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken);
        return _idToCode!.TryGetValue(rawValue, out var code) ? code : null;
    }

    async ValueTask<string> IRequestLookupProvider.ResolveAsync(string rawValue, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        if (!_codeToId!.TryGetValue(rawValue, out var id))
        {
            throw new LookupResolutionException($"'{rawValue}' is not a known {codeType} code.");
        }

        return id;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _idToCode) is not null)
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_idToCode is not null)
            {
                return;
            }

            var idToCode = new Dictionary<string, string>();
            var codeToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            await using var connection = await connectionFactory.OpenAsync(cancellationToken);
            var sql = $"""
                SELECT {SqlIdentifier.Quote(codeTable)}.{SqlIdentifier.Quote(idColumn)}, {SqlIdentifier.Quote(codeTable)}.{SqlIdentifier.Quote(codeColumn)}
                FROM {SqlIdentifier.Quote(codeTable)}
                INNER JOIN {SqlIdentifier.Quote(codeTypeTable)}
                  ON {SqlIdentifier.Quote(codeTable)}.{SqlIdentifier.Quote(codeTypeIdColumn)} = {SqlIdentifier.Quote(codeTypeTable)}.{SqlIdentifier.Quote(codeTypeIdColumn)}
                WHERE {SqlIdentifier.Quote(codeTypeTable)}.{SqlIdentifier.Quote(codeTypeNameColumn)} = @codeType;
                """;
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@codeType", codeType);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                var code = reader.GetString(1);
                idToCode[id] = code;
                codeToId[code] = id;
            }

            _codeToId = codeToId;
            Volatile.Write(ref _idToCode, idToCode);
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
