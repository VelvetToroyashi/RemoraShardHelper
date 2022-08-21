using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Gateway.Commands;
using Remora.Discord.Gateway;
using Remora.Discord.Gateway.Services;
using Remora.Discord.Gateway.Transport;
using Remora.Discord.Rest;
using Remora.Results;

namespace RemoraShardHelper.Services;

public class ShardedGatewayClient : IDisposable
{
    private bool _isRunning;
    
    private readonly IServiceProvider _services;
    private readonly IDiscordRestGatewayAPI _gatewayAPI;
    private readonly IOptions<ShardedGatewayClientOptions> _gatewayOptions;
    private readonly ITokenStore _tokenStore;
    private readonly Random _random;
    private readonly ResponderDispatchService _responderDispatch;
    private readonly ILogger<ShardedGatewayClient> _logger;
    
    private readonly ConcurrentDictionary<int, DiscordGatewayClient> _gatewayClients = new();
    
    public IReadOnlyDictionary<int, DiscordGatewayClient> Shards => _gatewayClients;

    public ShardedGatewayClient
    (
        IServiceProvider services,
        IDiscordRestGatewayAPI gatewayAPI,
        IOptions<ShardedGatewayClientOptions> gatewayOptions,
        ITokenStore tokenStore,
        Random random,
        ResponderDispatchService responderDispatch,
        ILogger<ShardedGatewayClient> logger
    )
    {
        _services = services;
        _gatewayAPI = gatewayAPI;
        _gatewayOptions = gatewayOptions;
        _tokenStore = tokenStore;
        _random = random;
        _responderDispatch = responderDispatch;
    }

    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        _isRunning = true;
        
        var gatewayResult = await _gatewayAPI.GetGatewayBotAsync();
        
        if (!gatewayResult.IsSuccess)
        {
            _logger.LogError("Failed to retrieve gateway endpoint");
            return (Result)gatewayResult;
        }

        var gatewayOptions = _gatewayOptions.Value;
        
        if (gatewayResult.Entity.Shards.IsDefined(out var shards))
        {
            gatewayOptions.ShardsCount ??= shards;
        }
        
        if (gatewayOptions.ShardIdentification is null && gatewayOptions.ShardsCount is {} shardCount)
        {
            gatewayOptions.ShardIdentification = new ShardIdentification(1, shardCount);
        }
        
        var shardDelta = gatewayOptions.ShardIdentification?.ShardCount ?? 1 - (gatewayOptions.ShardIdentification?.ShardID ?? 1 + gatewayOptions.ShardsCount ?? 1);

        if (shardDelta > 0)
        {
            _logger.LogWarning("The specified shard count exceeds the set or recommended shard count " +
                               "of {ShardCount}, and only {Shards} will be started.",
                gatewayOptions.ShardIdentification.ShardCount, 
                gatewayOptions.ShardIdentification.ShardCount - shardDelta);
        }
        
        var startupShards = gatewayOptions.ShardsCount ?? 1 - shardDelta;

        var maxStartup = gatewayResult.Entity.SessionStartLimit.IsDefined(out var sessionLimit) ? sessionLimit.MaxConcurrency : 1;

        var clients = Enumerable
            .Range(gatewayOptions.ShardIdentification?.ShardID ?? 0, startupShards)
            .Select
            (
                s =>
                {
                    var client = new DiscordGatewayClient
                    (
                        _gatewayAPI,
                        _services.GetRequiredService<IPayloadTransportService>(),
                        CloneOptions(gatewayOptions, s),
                        _tokenStore,
                        _random,
                        _services.GetRequiredService<ILogger<DiscordGatewayClient>>(),
                        _services,
                        _responderDispatch
                    );

                    _gatewayClients[s] =  client;
                    
                    return client;
                });

        var tasks = new List<Task<Result>>();

        if (maxStartup is 1)
        {
            foreach (var client in clients)
            {
                var res = client.RunAsync(ct);
                
                tasks.Add(res);

                await Task.Delay(3000, ct);
                
                if (res.IsCompleted && !res.Result.IsSuccess)
                {
                    return res.Result;
                }
            }
        }
        else
        {
            var bursts = clients.Chunk(maxStartup);

            foreach (var burst in bursts)
            {
                var startTasks = burst.Select(c => c.RunAsync(ct));
                tasks.AddRange(startTasks);

                await Task.Delay(10_000, ct);
                
                if (startTasks.FirstOrDefault(t => !t.IsCompleted || !t.Result.IsSuccess) is {} failed)
                {
                    return failed.Result;
                }
            }
        }

        return await await Task.WhenAny(tasks);
    }

    private IOptions<DiscordGatewayClientOptions> CloneOptions(DiscordGatewayClientOptions options, in int shardID)
    {
        var ret = new DiscordGatewayClientOptions();
        
        ret.ShardIdentification   = new ShardIdentification(shardID, options.ShardIdentification.ShardCount);
        ret.Intents               = options.Intents;
        ret.Presence              = options.Presence;
        ret.ConnectionProperties  = options.ConnectionProperties;
        ret.HeartbeatHeadroom     = options.HeartbeatHeadroom;
        ret.LargeThreshold        = options.LargeThreshold;
        ret.CommandBurstRate      = options.CommandBurstRate;
        ret.HeartbeatSafetyMargin = options.HeartbeatSafetyMargin;
        ret.MinimumSafetyMargin   = options.MinimumSafetyMargin;

        return Options.Create(ret);
    }

    public void Dispose()
    {
        foreach (var client in _gatewayClients.Values)
        {
            client.Dispose();
        }
    }
}