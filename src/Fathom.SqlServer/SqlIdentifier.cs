namespace Fathom.SqlServer;

/// <summary>Safe bracket-quoting for identifiers sourced from an ExportDefinition (never from request input).</summary>
internal static class SqlIdentifier
{
    public static string Quote(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}
