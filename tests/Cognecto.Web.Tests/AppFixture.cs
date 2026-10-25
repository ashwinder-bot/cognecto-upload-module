using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Cognecto.Web.Tests;

/// <summary>
/// Runs the real app against a throwaway Postgres in Docker, created from database/schema.sql,
/// so tests exercise the same schema, constraints and HTTP pipeline that production uses.
/// </summary>
public sealed partial class AppFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private WebApplicationFactory<Program> _factory = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public IServiceProvider Services => _factory.Services;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema.sql")));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Supabase", ConnectionString));
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    public HttpClient AnonymousClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<HttpClient> SignedInAs(string userId)
    {
        var client = AnonymousClient();
        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["userId"] = userId,
            ["__RequestVerificationToken"] = await AntiforgeryToken(client, "/Account/Login"),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    public static async Task<HttpResponseMessage> Upload(
        HttpClient client, string fileName, string csv, IDictionary<string, string>? extraFields = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(await AntiforgeryToken(client, "/Migration")), "__RequestVerificationToken" },
        };
        foreach (var (name, value) in extraFields ?? new Dictionary<string, string>())
            form.Add(new StringContent(value), name);

        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);

        return await client.PostAsync("/Migration/Upload", form);
    }

    public static async Task<string> AntiforgeryToken(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        return WebUtility.HtmlDecode(AntiforgeryTokenPattern().Match(html).Groups[1].Value);
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
            rows.Add(map(reader));
        return rows;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryTokenPattern();
}

[CollectionDefinition(Name)]
public class AppCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "app";
}
