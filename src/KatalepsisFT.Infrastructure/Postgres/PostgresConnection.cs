using Npgsql;

namespace KatalepsisFT.Infrastructure.Postgres;

/// <summary>Turns the URL Render hands out for Postgres (postgresql://user:password@host[:port]/database) into a connection string.</summary>
public static class PostgresConnection
{
    public static string FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
        {
            throw new FormatException("The Postgres address must look like postgresql://user:password@host/database.");
        }

        string[] userInfo = uri.UserInfo.Split(':', 2);
        NpgsqlConnectionStringBuilder builder = new()
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length == 2 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };

        return builder.ConnectionString;
    }
}
