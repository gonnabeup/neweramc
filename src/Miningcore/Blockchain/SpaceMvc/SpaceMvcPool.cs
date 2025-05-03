using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.SpaceMvc;

[CoinFamily(CoinFamily.Bitcoin)]
public class SpaceMvcPool : PoolBase
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

    protected object currentJobParams;
    protected SpaceMvcJobManager manager;
    private BitcoinTemplate coin;

    protected virtual async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = request.ParamsAs<string[]>();

        if(requestParams == null || requestParams.Length < 2)
            throw new StratumException(StratumError.MinusOne, "invalid request");

        var data = new object[]
        {
            new object[]
            {
                BitcoinStratumMethods.SetDifficulty,
                new object[] { context.Difficulty }
            },
            new object[]
            {
                BitcoinStratumMethods.MiningNotify,
                currentJobParams
            }
        };

        await connection.RespondAsync(data, request.Id);

        // setup worker context
        context.IsSubscribed = true;
        context.UserAgent = requestParams[0].Trim();
        context.ExtraNonce1 = requestParams[1];
    }

    protected virtual async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = request.ParamsAs<string[]>();
        var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
        var password = requestParams?.Length > 1 ? requestParams[1] : null;

        if(string.IsNullOrEmpty(workerValue))
            throw new StratumException(StratumError.MinusOne, "missing workername");

        // extract worker/miner
        var split = workerValue?.Split('.');
        var minerName = split?.FirstOrDefault()?.Trim();
        var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

        // assumes that minerName is an address
        var address = minerName;

        if(string.IsNullOrEmpty(minerName))
            throw new StratumException(StratumError.MinusOne, "missing miner name");

        // validate address
        var isValidAddress = await manager.ValidateAddressAsync(address, ct);

        if(!isValidAddress)
            throw new StratumException(StratumError.MinusOne, "invalid address");

        context.IsAuthorized = true;
        context.Miner = minerName;
        context.Worker = workerName;

        await connection.RespondAsync(context.IsAuthorized, request.Id);

        // log association
        logger.Info(() => $"[{LogCategory}] Authorized worker {workerValue}");

        // extract control vars from password
        var staticDiff = GetStaticDiffFromPassparts(password);

        // Nicehash support
        var nicehashDiff = await GetNicehashStaticDiffAsync(connection, context.UserAgent, coin.Name, coin.GetAlgorithmName());

        if(nicehashDiff.HasValue)
        {
            if(!staticDiff.HasValue || nicehashDiff > staticDiff)
            {
                logger.Info(() => $"[{LogCategory}] Nicehash detected. Using API supplied difficulty of {nicehashDiff.Value}");

                staticDiff = nicehashDiff;
            }

            else
                logger.Info(() => $"[{LogCategory}] Nicehash detected. Using miner supplied difficulty of {staticDiff.Value} instead of API supplied {nicehashDiff.Value}");
        }

        // Static diff
        if(staticDiff.HasValue &&
            (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                context.VarDiff == null && staticDiff.Value > context.Difficulty))
        {
            context.VarDiff = null; // disable vardiff
            context.SetDifficulty(staticDiff.Value);

            logger.Info(() => $"[{LogCategory}] Setting static difficulty of {staticDiff.Value}");

            await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
        }

        // send intial update
        await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
    }

    protected virtual async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        try
        {
            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            // check age of submission (aged submissions are usually caused by high server load)
            var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

            if(requestAge > maxShareAge)
            {
                logger.Warn(() => $"[{LogCategory}] Dropping stale share submission request (server overloaded?)");
                return;
            }

            // validate worker
            if(!context.IsAuthorized)
                throw new StratumException(StratumError.UnauthorizedWorker, "unauthorized worker");
            else if(!context.IsSubscribed)
                throw new StratumException(StratumError.NotSubscribed, "not subscribed");

            // submit
            var share = await manager.SubmitShareAsync(connection, request.Params, ct);
            await connection.RespondAsync(true, request.Id);

            // publish
            messageBus.SendMessage(new ClientShare(null, poolConfig.Id, share));

            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            logger.Info(() => $"[{LogCategory}] Share accepted: D={Math.Round(share.Difficulty, 3)}");

            // update pool stats
            if(share.IsBlockCandidate)
                poolStats.LastPoolBlockTime = clock.Now;

            // update client stats
            context.Stats.ValidShares++;
            await UpdateVarDiffAsync(connection, false, ct);
        }

        catch(StratumException ex)
        {
            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

            // update client stats
            context.Stats.InvalidShares++;
            logger.Info(() => $"[{LogCategory}] Share rejected: {ex.Message} [{context.ConnectionId}]");

            // banning
            ConsiderBan(connection, context, poolConfig.Banning);

            throw;
        }
    }

    protected virtual async Task OnSuggestDifficultyAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        // acknowledge
        await connection.RespondAsync(true, request.Id);

        try
        {
            var requestedDiff = (double) Convert.ToDecimal(request.Params[0], CultureInfo.InvariantCulture);

            // client may suggest higher/lower difficulty for own reasons
            var poolDiff = context.Difficulty;

            if(requestedDiff > poolDiff)
            {
                if(requestedDiff / poolDiff > 4)
                {
                    logger.Info(() => $"[{LogCategory}] Worker {context.Miner} requested difficulty too high {requestedDiff}");
                    requestedDiff = poolDiff * 4;
                }

                context.SetDifficulty(requestedDiff);
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

                logger.Info(() => $"[{LogCategory}] Worker {context.Miner} requested higher difficulty {requestedDiff}");
            }
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"[{LogCategory}] Unable to convert suggested difficulty {request.Params[0]}");
        }
    }

    protected virtual async Task OnNewJobAsync(object jobParams)
    {
        currentJobParams = jobParams;

        logger.Info(() => $"Broadcasting job {((object[]) jobParams)[0]}");

        await Guard(() => ForEachMinerAsync(async (connection, ct) =>
        {
            var context = connection.ContextAs<BitcoinWorkerContext>();

            // varDiff: if the client has a pending difficulty change, apply it now
            if(context.ApplyPendingDifficulty())
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

            // send job
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
        }));
    }

    public override double HashrateFromShares(double shares, double interval)
    {
        var multiplier = BitcoinConstants.Pow2x32;
        var result = shares * multiplier / interval;

        if(coin.HashrateMultiplier.HasValue)
            result *= coin.HashrateMultiplier.Value;

        return result;
    }

    public override double ShareMultiplier => coin.ShareMultiplier;

    #region Overrides

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();

        base.Configure(pc, cc);
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<SpaceMvcJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider), new BitcoinExtraNonceProvider(poolConfig.Id, clusterConfig.InstanceId)));

        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() =>
                    Guard(()=> OnNewJobAsync(job),
                        ex=> logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex =>
                {
                    logger.Debug(ex, nameof(OnNewJobAsync));
                }));

            // start with initial blocktemplate
            await manager.Jobs.Take(1).ToTask(ct);
        }

        else
        {
            // keep updating NetworkStats
            disposables.Add(manager.Jobs.Subscribe());
        }
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);

        blockchainStats = manager.BlockchainStats;
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return new BitcoinWorkerContext();
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        try
        {
            switch(request.Method)
            {
                case BitcoinStratumMethods.Subscribe:
                    await OnSubscribeAsync(connection, tsRequest);
                    break;

                case BitcoinStratumMethods.Authorize:
                    await OnAuthorizeAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.SubmitShare:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.SuggestDifficulty:
                    await OnSuggestDifficultyAsync(connection, tsRequest);
                    break;

                default:
                    logger.Debug(() => $"[{LogCategory}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                    await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        catch(StratumException ex)
        {
            await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
        }
    }

    #endregion // Overrides
} 