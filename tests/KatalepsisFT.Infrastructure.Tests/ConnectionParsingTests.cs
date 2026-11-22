using System.Net;
using FluentAssertions;
using KatalepsisFT.Infrastructure.KeyValue;
using KatalepsisFT.Infrastructure.Postgres;
using Npgsql;
using StackExchange.Redis;

namespace KatalepsisFT.Infrastructure.Tests;

public class ConnectionParsingTests
{
    [Fact]
    public void KeyValue_InternalUrlWithoutCredentials_UsesHostAndPort()
    {
        ConfigurationOptions options = KeyValueConnection.Parse("redis://red-abc123:6379");

        DnsEndPoint endpoint = options.EndPoints.Should().ContainSingle().Which.Should().BeOfType<DnsEndPoint>().Subject;
        endpoint.Host.Should().Be("red-abc123");
        endpoint.Port.Should().Be(6379);
        options.Password.Should().BeNull();
        options.Ssl.Should().BeFalse();
    }

    [Fact]
    public void KeyValue_UrlWithUserAndEscapedPassword_DecodesBoth()
    {
        ConfigurationOptions options = KeyValueConnection.Parse("rediss://default:p%40ss%3Aword@kv.example.com:6380");

        options.User.Should().Be("default");
        options.Password.Should().Be("p@ss:word");
        options.Ssl.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("red-abc123:6379")]
    [InlineData("http://red-abc123:6379")]
    public void KeyValue_NotARedisUrl_IsRejected(string url)
    {
        Action parse = () => KeyValueConnection.Parse(url);

        parse.Should().Throw<FormatException>();
    }

    [Fact]
    public void Postgres_UrlWithoutPort_UsesDefaultPortAndDecodesPassword()
    {
        NpgsqlConnectionStringBuilder builder = new(PostgresConnection.FromUrl("postgresql://ft_user:s%2Fecret@dpg-abc123-a/katalepsis_ft"));

        builder.Host.Should().Be("dpg-abc123-a");
        builder.Port.Should().Be(5432);
        builder.Database.Should().Be("katalepsis_ft");
        builder.Username.Should().Be("ft_user");
        builder.Password.Should().Be("s/ecret");
    }

    [Fact]
    public void Postgres_NotAPostgresUrl_IsRejected()
    {
        Action parse = () => PostgresConnection.FromUrl("Host=localhost;Database=x");

        parse.Should().Throw<FormatException>();
    }
}
