using CallTree.Application.Calls;
using Microsoft.Extensions.DependencyInjection;

namespace CallTree.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<CallLifecycleService>();

        return services;
    }
}
