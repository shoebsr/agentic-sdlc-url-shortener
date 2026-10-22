using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace UrlShortener.Infrastructure.Persistence;

internal static class UniqueViolation
{
    private const int SqlServerUniqueIndex = 2601;
    private const int SqlServerUniqueConstraint = 2627;
    private const int SqliteConstraintUnique = 2067;

    public static bool IsMatch(DbUpdateException exception) => exception.InnerException switch
    {
        SqlException sql => sql.Number is SqlServerUniqueIndex or SqlServerUniqueConstraint,
        SqliteException sqlite => sqlite.SqliteExtendedErrorCode == SqliteConstraintUnique,
        _ => false,
    };
}
