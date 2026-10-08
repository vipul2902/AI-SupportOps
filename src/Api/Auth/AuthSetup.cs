using AISupportOps.Application.Common;
using AISupportOps.Application.Identity;
using AISupportOps.Domain.Tenants;
using AISupportOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AISupportOps.Api.Auth;

/// <summary>
/// Authorization policies map to a minimum role. Roles are hierarchical, so "Agent" means
/// Agent, Admin, or Owner. Every policy also requires a tenant claim.
/// </summary>
public static class Policies
{
    public const string Viewer = nameof(TenantRole.Viewer);
    public const string Agent = nameof(TenantRole.Agent);
    public const string Admin = nameof(TenantRole.Admin);
    public const string Owner = nameof(TenantRole.Owner);
}

internal static class AuthSetup
{
    public static IServiceCollection AddApiAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantScope>(sp => sp.GetRequiredService<TenantContext>());

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                var settings = auth.Value;
                jwt.MapInboundClaims = false; // keep "sub", "role", "tid" as issued
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = settings.Issuer,
                    ValidAudience = settings.Audience,
                    IssuerSigningKey = JwtSigningKey.Create(settings),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = AppClaims.Role,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Viewer, p => RequireAtLeast(p, TenantRole.Viewer))
            .AddPolicy(Policies.Agent, p => RequireAtLeast(p, TenantRole.Agent))
            .AddPolicy(Policies.Admin, p => RequireAtLeast(p, TenantRole.Admin))
            .AddPolicy(Policies.Owner, p => RequireAtLeast(p, TenantRole.Owner));

        return services;
    }

    private static void RequireAtLeast(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy, TenantRole minimum) =>
        policy.RequireAuthenticatedUser()
            .RequireClaim(AppClaims.TenantId)
            .RequireRole(Enum.GetValues<TenantRole>().Where(r => r >= minimum).Select(r => r.ToString()));
}
