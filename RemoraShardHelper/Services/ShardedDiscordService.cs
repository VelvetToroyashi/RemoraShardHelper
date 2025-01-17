using Microsoft.Extensions.Hosting;
using Remora.Discord.Gateway.Results;
using Remora.Discord.Hosting.Options;

namespace RemoraShardHelper.Services;

public class ShardedDiscordService
(
    ShardedGatewayClient client,
    DiscordServiceOptions options,
    IHostApplicationLifetime lifetime
)
: BackgroundService
{

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runResult = await client.RunAsync(stoppingToken);

        if (runResult.Error is GatewayError { IsCritical: true } && options.TerminateApplicationOnCriticalGatewayErrors)
        {
            lifetime.StopApplication();
        }
    }
}
