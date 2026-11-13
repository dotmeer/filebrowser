using Microsoft.Data.Sqlite;

namespace dotmeer.filebrowser.Tasks;

internal static class SqliteErrors
{
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    public static bool IsUniqueConstraint(SqliteException exception) =>
        exception.SqliteExtendedErrorCode == SqliteConstraintUnique;

    public static bool IsPrimaryKeyConstraint(SqliteException exception) =>
        exception.SqliteExtendedErrorCode == SqliteConstraintPrimaryKey;
}
