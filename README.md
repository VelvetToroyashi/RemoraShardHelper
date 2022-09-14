# Remora Shard Helper
---
This library is a small proof-of-concept to implement in-process sharding in [Remora](https://github.com/Remora/Remora.Discord).

## Usage:

Setting this library up is relatively easy.
Right now there's a concrete dependency on `Microsoft.Extensions.Hosting.Abstractions` and `Remora.Discord.Hosting`.

First add the project (or NuGet package if it exists) to your project.
Then in your `Program.cs`, you'll configure the client almost in the same manner you would for the normal gateway client.

```cs
var services = new ServiceCollection();

var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN") ?? throw new InvalidOperationException("No token provided.");

services
.AddDiscordGateway(_ => token)
.Configure<ShardedGatewayOptions>(opt => 
{
    opt.ShardsCount = 2;
})
.AddSingleton<ShardedGatewayClient>();

var provider = services.BuildServiceProvider();

var sharder = provider.GetRequiredService<ShardedGatewayClient>();

await sharder.RunAsync();
```

---

If you prefer using the Generic Host, you can do so as well.

```cs
var builder = new HostBuilder()
    .ConfigureServices((hostContext, services) =>
    {
        var token = hostContext.Configuration.Get<string>("DISCORD_TOKEN") ?? throw new InvalidOperationException("No token provided.");
        services
        .Configure<ShardedGatewayOptions>(opt => 
        {
            opt.ShardsCount = 2;
        })
        .AddShardedDiscordService(_ => token);
    });

var host = builder.Build();

await host.RunAsync();
```

---

## Mixing In-Process and Out-Of-Process Sharding:

This library supports mixing in-process and out-of-process sharding via 'striding'. 
To support out-of-process sharding, you'll need to set the `ShardsCount` to the total number of shards you want to run.
This number is representive of the *total* amount of shards, independent of clustering.

For example, if you have 2 clusters, each with 2 shards, you'll set the `ShardsCount` to 4.
Then, on the first cluster, the stride will be 0, and on the second cluster, the stride will be 2.

It's as simple as specifying shard identification in the `ShardedGatewayOptions`:

```diff
.Configure<ShardedGatewayOptions>(opt => 
{
    opt.ShardsCount = 2;
+   opt.ShardIdentification = new ShardIdentification(2, 4);
})
```
