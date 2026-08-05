using CallTree.Application.Abstractions;
using CallTree.Infrastructure.Configuration;
using CallTree.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CallTree.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("CallTree")
            ?? throw new InvalidOperationException("Connection string 'CallTree' is not configured.");

        services.AddDbContext<CallTreeDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<ICallRepository, CallRepository>();
        services.AddScoped<ICallQueries, CallQueries>();
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        return services;
    }
}
