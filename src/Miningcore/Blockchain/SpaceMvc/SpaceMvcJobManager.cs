using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.SpaceMvc.Configuration;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto;
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

        if(poolConfig.EnableInternalStratum == true && poolConfig.Template.As<BitcoinTemplate>().HeaderHasherValue is IHashAlgorithmInit hashInit)
        {
            if(!hashInit.DigestInit(poolConfig))
                logger.Error(()=> $"{hashInit.GetType().Name} initialization failed");
        }
    }

    protected override async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
    {
        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            var response = string.IsNullOrEmpty(json) ?
                await GetBlockTemplateAsync(ct) :
                GetBlockTemplateFromJson(json);

            if(response.Error != null)
            {
                logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                return (false, forceUpdate);
            }

            var blockTemplate = response.Response;
            var job = currentJob;

            var isNew = job == null ||
                (blockTemplate != null &&
                    (job.BlockTemplate?.PreviousBlockhash != blockTemplate.PreviousBlockhash ||
                        blockTemplate.Height > job.BlockTemplate?.Height));

            if(isNew)
                messageBus.SendMessage(new ChainHeightNotification(poolConfig.Id, blockTemplate.Height, poolConfig.Template));

            if(isNew || forceUpdate)
            {
                job = CreateJob();

                job.Init(blockTemplate, NextJobId(),
                    poolConfig, extraPoolConfig, clusterConfig, clock, poolAddressDestination, network, isPoS,
                    ShareMultiplier, poolConfig.Template.As<BitcoinTemplate>().CoinbaseHasherValue, 
                    poolConfig.Template.As<BitcoinTemplate>().HeaderHasherValue,
                    !isPoS ? poolConfig.Template.As<BitcoinTemplate>().BlockHasherValue : 
                    poolConfig.Template.As<BitcoinTemplate>().PoSBlockHasherValue ?? poolConfig.Template.As<BitcoinTemplate>().BlockHasherValue);

                lock(jobLock)
                {
                    validJobs.Insert(0, job);

                    while(validJobs.Count > maxActiveJobs)
                        validJobs.RemoveAt(validJobs.Count - 1);
                }

                if(isNew)
                {
                    if(via != null)
                        logger.Info(() => $"Detected new block {blockTemplate.Height} [{via}]");
                    else
                        logger.Info(() => $"Detected new block {blockTemplate.Height}");

                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = blockTemplate.Height;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }

                else
                {
                    if(via != null)
                        logger.Debug(() => $"Template update {blockTemplate?.Height} [{via}]");
                    else
                        logger.Debug(() => $"Template update {blockTemplate?.Height}");
                }

                currentJob = job;
            }

            return (isNew, forceUpdate);
        }

        catch(Exception ex)
        {
            logger.Error(() => $"Error during {nameof(UpdateJob)}: {ex.Message}");
            throw;
        }
    }

    protected override object GetJobParamsForStratum(bool isNew)
    {
        try
        {
            var job = currentJob;
            return job?.GetJobParams(isNew);
        }
        catch(Exception ex)
        {
            logger.Error(() => $"Error in GetJobParamsForStratum: {ex.Message}");
            throw;
        }
    }

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        base.Configure(pc, cc);
    }
} 