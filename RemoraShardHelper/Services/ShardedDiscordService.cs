using Microsoft.Extensions.Hosting;
using Remora.Discord.Gateway.Results;
using Remora.Discord.Hosting.Options;

namespace RemoraShardHelper.Services;

public class ShardedDiscordService : BackgroundService
{
    private readonly ShardedGatewayClient _client;
    private readonly DiscordServiceOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runResult = await _client.RunAsync(stoppingToken);
        
        if (runResult.Error is GatewayError { IsCritical: true } && _options.TerminateApplicationOnCriticalGatewayErrors)
        {
            _lifetime.StopApplication();
        }
    }
}