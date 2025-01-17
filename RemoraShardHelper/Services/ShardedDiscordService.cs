using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Remora.Discord.Gateway.Results;
using Remora.Discord.Hosting.Options;

namespace RemoraShardHelper.Services;

public class ShardedDiscordService
(
    ShardedGatewayClient client,
    IOptions<DiscordServiceOptions> options,
    IHostApplicationLifetime lifetime
)
: BackgroundService
{

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runResult = await client.RunAsync(stoppingToken);

        if (runResult.Error is GatewayError { IsCritical: true } && options.Value.TerminateApplicationOnCriticalGatewayErrors)
        {
            lifetime.StopApplication();
        }
    }
}
