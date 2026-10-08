using AISupportOps.Application.Identity;
using AISupportOps.Application.Tenants;
using Microsoft.Extensions.DependencyInjection;

namespace AISupportOps.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AuthService>();
        services.AddScoped<TeamService>();
        services.AddScoped<TenantService>();
        return services;
    }
}
