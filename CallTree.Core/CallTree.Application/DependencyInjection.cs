using CallTree.Application.Calls;
using Microsoft.Extensions.DependencyInjection;

namespace CallTree.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<CallLifecycleService>();

        // Singleton: it holds only the scope factory and opens a scope per command.
        services.AddSingleton<ICallCommands, ScopedCallCommands>();

        return services;
    }
}
