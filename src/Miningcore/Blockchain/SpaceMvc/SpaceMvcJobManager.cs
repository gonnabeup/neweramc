using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.SpaceMvc.Configuration;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using Autofac;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.SpaceMvc;

public class SpaceMvcJobManager : BitcoinJobManagerBase<SpaceMvcJob>
{
    public SpaceMvcJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, clock, messageBus, extraNonceProvider)
    {
    }

    private SpaceMvcJob CreateJob()
    {
        return new();
    }

    protected override void PostChainIdentifyConfigure()
    {
        base.PostChainIdentifyConfigure();

        if(poolConfig.EnableInternalStratum == true && coin.HeaderHasherValue is IHashAlgorithmInit hashInit)
        {
            if(!hashInit.DigestInit(poolConfig))
                logger.Error(()=> $"{hashInit.GetType().Name} initialization failed");
        }
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
    {
        return await base.UpdateJob(ct, forceUpdate, via, json);
    }

    protected override object GetJobParamsForStratum(bool isNew)
    {
        return base.GetJobParamsForStratum(isNew);
    }

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        base.Configure(pc, cc);
    }
} 