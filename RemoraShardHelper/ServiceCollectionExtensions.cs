using Microsoft.Extensions.DependencyInjection;
using Remora.Discord.Gateway.Extensions;
using RemoraShardHelper.Services;

namespace RemoraShardHelper;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDiscordSharding
    (
        this IServiceCollection services, 
        Func<IServiceProvider, string> tokenFactory, 
        Action<IHttpClientBuilder>? build = null
    )
    {
        services.AddDiscordGateway(tokenFactory, build);

        services.AddHostedService<ShardedDiscordService>();

        return services;
    }
}