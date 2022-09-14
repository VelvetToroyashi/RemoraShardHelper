using System.Collections.Concurrent;
using System.Reflection;
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
    private readonly ShardedGatewayClientOptions _gatewayOptions;
    private readonly ILogger<ShardedGatewayClient> _logger;

    private readonly ConcurrentDictionary<int, DiscordGatewayClient> _gatewayClients = new();
    
    private static readonly FieldInfo _field = typeof(DiscordGatewayClient).GetField("_connectionStatus", BindingFlags.Instance | BindingFlags.NonPublic);
    
    private static readonly Func<DiscordGatewayClient, GatewayConnectionStatus> GetConnectionStatus = client => (GatewayConnectionStatus)_field.GetValue(client); 
    
    public IReadOnlyDictionary<int, DiscordGatewayClient> Shards => _gatewayClients;

    public ShardedGatewayClient
    (
        IServiceProvider services,
        IDiscordRestGatewayAPI gatewayAPI,
        IOptions<ShardedGatewayClientOptions> gatewayOptions,
        ILogger<ShardedGatewayClient> logger
    )
    {
        _logger = logger;
        _services = services;
        _gatewayAPI = gatewayAPI;
        _gatewayOptions = gatewayOptions.Value;
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

        if (gatewayResult.Entity.Shards.IsDefined(out var shards))
        {
            _gatewayOptions.ShardsCount ??= shards;
        }
        
        if (_gatewayOptions.ShardIdentification is null && _gatewayOptions.ShardsCount is {} shardCount)
        {
            _gatewayOptions.ShardIdentification = new ShardIdentification(0, shardCount);
        }
        
        var shardDelta = (_gatewayOptions.ShardIdentification?.ShardCount ?? _gatewayOptions.ShardsCount ?? 1) - ((_gatewayOptions.ShardIdentification?.ShardID ?? 0) + (_gatewayOptions.ShardsCount ?? 1));

        if (shardDelta > 0)
        {
            _logger.LogWarning("The specified shard count exceeds the set or recommended shard count " +
                               "of {ShardCount}, and only {Shards} will be started.",
                _gatewayOptions.ShardIdentification.ShardCount, 
                _gatewayOptions.ShardIdentification.ShardCount - shardDelta);
        }
        
        var startupShards = (_gatewayOptions.ShardsCount ?? 1) - shardDelta;

        var maxStartup = gatewayResult.Entity.SessionStartLimit.IsDefined(out var sessionLimit) ? sessionLimit.MaxConcurrency : 1;

        var shardStride = _gatewayOptions.ShardIdentification?.ShardID ?? 0;
        
        var clients = Enumerable
            .Range(shardStride, startupShards)
            .Select
            (
                s =>
                {
                    var client = ActivatorUtilities.CreateInstance<DiscordGatewayClient>(_services, CloneOptions(_gatewayOptions, s));

                    _gatewayClients[s] = client;
                    
                    return client;
                }
            )
            .ToArray();

        var tasks = new List<Task<Result>>();

        // *Technically* Discord wants you to start n % max_concurrency, but this is *generally* fine, and I don't
        // think that's even strictly enforced on Discord's side either. It would be a pain to handle because we'd
        // have to track for overflows, and only start shards that land in the first bucket, and then re-bucket and
        // it's just a mess, frankly. Here's the docs about that: https://discord.dev/topics/gateway#sharding-max-concurrency
        
        if (maxStartup is 1)
        {
            for (var shardIndex = 0; shardIndex < clients.Length; shardIndex++)
            {
                DiscordGatewayClient? client = clients[shardIndex];
                var res = client.RunAsync(ct);

                tasks.Add(res);

                while (GetConnectionStatus(client) is not GatewayConnectionStatus.Connected || res.IsCompleted)
                {
                    await Task.Delay(100, ct);
                }

                if (res.IsCompleted && !res.Result.IsSuccess)
                {
                    return res.Result;
                }
                
                _logger.LogInformation("Started shard [{Shard}, {RealShardId}]", shardIndex, shardStride + shardIndex);
            }
        }
        else
        {
            var bursts = clients.Chunk(maxStartup).ToArray();

            for (var burstIndex = 0; burstIndex < bursts.Length; burstIndex++)
            {
                var burst = bursts[burstIndex];
                
                var now = DateTime.UtcNow;
                const int startupDelay = 5000;

                var startTasks = burst.Select(c => c.RunAsync(ct)).ToArray();
                tasks.AddRange(startTasks);

                while (burst.Any(c => GetConnectionStatus(c) is not GatewayConnectionStatus.Connected))
                {
                    if (startTasks.Any(t => t.IsCompleted && !t.Result.IsSuccess))
                    {
                        var errors = startTasks.Where(t => t.IsCompleted && !t.Result.IsSuccess).Select(r => (IResult)r.Result).ToArray();

                        return Result.FromError(new AggregateError("Failed to start one or more shards.", errors));
                    }

                    await Task.Delay(100, ct);
                }

                var startedShards = bursts.Take(burstIndex).Sum(b => b.Length);

                for (int i = 0; i < burst.Length; i++)
                {
                    _logger.LogInformation("Started shard [{Burst}, {Shard}, {RealShardId}]", burstIndex, startedShards + i, startedShards + shardStride + i);
                }
                
                var requiredDelay = startupDelay - (DateTime.UtcNow - now).TotalMilliseconds;

                if (requiredDelay > 0)
                {
                    await Task.Delay((int)requiredDelay, ct);
                }
            }
        }

        return await await Task.WhenAny(tasks);
    }

    private IOptions<DiscordGatewayClientOptions> CloneOptions(DiscordGatewayClientOptions options, int shardID)
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