using System.Net;
using AISupportOps.Infrastructure.Persistence;
using AISupportOps.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AISupportOps.IntegrationTests;

[Collection(ApiCollection.Name)]
public class HealthCheckTests(ApiFactory factory)
{
    [Fact]
    public async Task Live_returns_ok()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_returns_ok_when_postgres_and_redis_are_reachable()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Migrations_install_the_pgvector_extension()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var installed = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*)::int AS \"Value\" FROM pg_extension WHERE extname = 'vector'")
            .SingleAsync();

        Assert.Equal(1, installed);
    }

    [Fact]
    public async Task Unknown_route_returns_problem_details()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/does-not-exist", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
