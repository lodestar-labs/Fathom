using Microsoft.Data.SqlClient;

namespace Fathom.SqlServer;

/// <summary>
/// The disposable resources one export holds open: every level's reader/command, then the
/// connection they share (disposed last, since the readers are its server-side cursors). A
/// single <see cref="IAsyncDisposable"/> so <see cref="ExportRun"/> can own the whole bundle
/// without depending on the concrete SQL types — which is also what lets the run's
/// dispose-regardless-of-enumeration guarantee be unit-tested with a fake.
/// </summary>
internal sealed class ExportResources(SqlConnection connection, IReadOnlyList<ReaderCursor> cursors) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        foreach (var cursor in cursors)
        {
            await cursor.DisposeAsync();
        }

        await connection.DisposeAsync();
    }
}
