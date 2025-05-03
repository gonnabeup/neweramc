using Miningcore.Blockchain.Bitcoin;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;
using Newtonsoft.Json;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.SpaceMvc;

[CoinFamily(CoinFamily.Bitcoin)]
public class SpaceMvcPool : BitcoinPool
{
    public SpaceMvcPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    protected new object currentJobParams;
    protected new SpaceMvcJobManager manager;
    private BitcoinTemplate coin;

    protected override async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        await base.OnSubscribeAsync(connection, tsRequest);
    }

    protected override async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        await base.OnAuthorizeAsync(connection, tsRequest, ct);
    }

    protected override async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        await base.OnSubmitAsync(connection, tsRequest, ct);
    }

    protected override async Task OnNewJobAsync(object jobParams)
    {
        await base.OnNewJobAsync(jobParams);
    }

    public override double HashrateFromShares(double shares, double interval)
    {
        return base.HashrateFromShares(shares, interval);
    }

    public override double ShareMultiplier => coin.ShareMultiplier;

    #region Overrides

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        base.Configure(pc, cc);
        coin = pc.Template.As<BitcoinTemplate>();
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        await base.SetupJobManager(ct);
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return base.CreateWorkerContext();
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        await base.OnRequestAsync(connection, tsRequest, ct);
    }

    #endregion // Overrides
} 