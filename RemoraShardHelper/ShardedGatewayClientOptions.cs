using Remora.Discord.Gateway;

namespace RemoraShardHelper;

public class ShardedGatewayClientOptions : DiscordGatewayClientOptions
{
    public int? ShardsCount { get; set; }
}