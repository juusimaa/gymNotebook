using Npgsql;

namespace GymNotebook.Api;

// Checks ConnectionStrings:Default at startup, before EF Core or Npgsql touch it.
//
// Why this exists: Npgsql reports an unparseable connection string with an
// ArgumentException that quotes the offending text, and a URL-style string
// (postgresql://user:password@host/db) is parsed as one giant key, so the whole value,
// password included, ends up in the message. Unhandled at startup, that message went
// straight to the container console and into Log Analytics (research R7, T075 finding
// of 2026-09-28). Parsing it here first lets us fail just as early but with a message
// that names the setting and never its value.
public static class ConnectionStringValidation
{
    public static void EnsureValid(string connectionString)
    {
        try
        {
            // Only the parse matters; the builder itself is thrown away. This opens no
            // connection, so a valid string for an unreachable server still passes.
            _ = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // Deliberately no inner exception: its message is the one that leaks the value.
            // Npgsql expects "Host=...;Username=...;Password=...", not a postgres:// URL.
            throw new InvalidOperationException(
                "ConnectionStrings:Default is not a valid Npgsql connection string " +
                "(expected Key=Value; pairs, not a postgres:// URL). The value is not shown.");
        }
    }
}
