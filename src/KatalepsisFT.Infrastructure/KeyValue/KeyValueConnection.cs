using StackExchange.Redis;

namespace KatalepsisFT.Infrastructure.KeyValue;

/// <summary>Turns the URL Render hands out for Key Value (redis://[user:password@]host:port) into client options.</summary>
public static class KeyValueConnection
{
    public static ConfigurationOptions Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "redis" && uri.Scheme != "rediss"))
        {
            throw new FormatException("The Key Value address must look like redis://host:port.");
        }

        ConfigurationOptions options = new()
        {
            EndPoints = { { uri.Host, uri.Port > 0 ? uri.Port : 6379 } },
            Ssl = uri.Scheme == "rediss",
            AbortOnConnectFail = false,
            ConnectTimeout = 5000,
        };

        string[] userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length == 2)
        {
            options.User = userInfo[0].Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : null;
            options.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        return options;
    }
}
