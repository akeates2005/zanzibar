using Microsoft.Extensions.DependencyInjection;
using Poc.Application.Authorization;

namespace Poc.Infrastructure.SpiceDb;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the SpiceDB-backed <see cref="IAuthorizer"/>, <see cref="IRelationshipStore"/> and <see cref="ISchemaStore"/>.</summary>
    public static IServiceCollection AddSpiceDbAuthorization(this IServiceCollection services, SpiceDbOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new SpiceDbConnection(options));
        services.AddSingleton<IAuthorizer, SpiceDbAuthorizer>();
        services.AddSingleton<IRelationshipStore, SpiceDbRelationshipStore>();
        services.AddSingleton<ISchemaStore, SpiceDbSchemaStore>();
        return services;
    }
}
