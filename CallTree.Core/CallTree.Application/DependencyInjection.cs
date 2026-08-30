using CallTree.Application.Calls;
using CallTree.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CallTree.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<CallLifecycleService>();
        services.AddScoped<RecordingService>();

        // Singleton: it holds only the scope factory and opens a scope per command.
        services.AddSingleton<ICallCommands, ScopedCallCommands>();

        // Bound here rather than in either consumer: Infrastructure resolves the database directory from
        // it and Telephony resolves the recordings root, and whichever bound it would be an odd
        // dependency for the other.
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        return services;
    }
}
