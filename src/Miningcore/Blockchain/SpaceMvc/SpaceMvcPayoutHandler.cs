using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.SpaceMvc.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Miningcore.Util;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.SpaceMvc;

[CoinFamily(CoinFamily.Bitcoin)]
public class SpaceMvcPayoutHandler : BitcoinPayoutHandler
{
    public SpaceMvcPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);

        this.ctx = ctx;
    }

    protected new readonly IComponentContext ctx;
    protected new RpcClient rpcClient;
    protected new SpaceMvcPoolConfigExtra extraPoolConfig;
    protected new BitcoinDaemonEndpointConfigExtra extraPoolEndpointConfig;
    protected new SpaceMvcPoolPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;

    protected override string LogCategory => "Space MVC Payout Handler";

    #region IPayoutHandler

    public override Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;

        extraPoolConfig = pc.Extra.SafeExtensionDataAs<SpaceMvcPoolConfigExtra>();
        extraPoolEndpointConfig = pc.Extra.SafeExtensionDataAs<BitcoinDaemonEndpointConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<SpaceMvcPoolPaymentProcessingConfigExtra>();

        logger = LogUtil.GetPoolScopedLogger(typeof(SpaceMvcPayoutHandler), pc);

        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
        rpcClient = new RpcClient(pc.Daemons.First(), jsonSerializerSettings, messageBus, pc.Id);

        return Task.CompletedTask;
    }

    public override async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        var coin = poolConfig.Template.As<CoinTemplate>();
        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();
        int minConfirmations;

        if(coin is BitcoinTemplate bitcoinTemplate)
            minConfirmations = extraPoolEndpointConfig?.MinimumConfirmations ?? bitcoinTemplate.CoinbaseMinConfimations ?? BitcoinConstants.CoinbaseMinConfimations;
        else
            minConfirmations = extraPoolEndpointConfig?.MinimumConfirmations ?? BitcoinConstants.CoinbaseMinConfimations;

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            // get block infos from daemon
            var blockInfos = await GetBlockInfoAsync(ct, page);

            for(var j = 0; j < blockInfos.Length; j++)
            {
                var blockInfo = blockInfos[j];
                var block = page[j];

                if(blockInfo == null)
                {
                    result.Add(block);
                    continue;
                }

                // update progress
                block.ConfirmationProgress = Math.Min(1.0d, (double) blockInfo.Confirmations / minConfirmations);
                result.Add(block);

                messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);

                // matured and spendable?
                if(blockInfo.Confirmations >= minConfirmations)
                {
                    block.Status = BlockStatus.Confirmed;
                    block.Reward = GetBaseBlockReward(blockInfo.Height); // base reward
                    block.ConfirmationProgress = 1;

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                }
            }
        }

        return result.ToArray();
    }

    public override async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        // build args
        var amounts = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => Math.Round(x.Amount, 4));

        if(amounts.Count == 0)
            return;

        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

        object[] args;

        var identifier = !string.IsNullOrEmpty(clusterConfig.PaymentProcessing?.CoinbaseString) ?
            clusterConfig.PaymentProcessing.CoinbaseString.Trim() : "Miningcore";

        var comment = $"{identifier} Payment";

        if(!(extraPoolConfig?.HasBrokenSendMany == true || poolConfig.Template is BitcoinTemplate { HasBrokenSendMany: true }))
        {
            if(extraPoolPaymentProcessingConfig?.MinersPayTxFees == true)
            {
                var subtractFeesFrom = amounts.Keys.ToArray();

                args = new object[]
                {
                    string.Empty, // default account
                    amounts, // addresses and associated amounts
                    1, // only spend funds covered by this many confirmations
                    comment, // tx comment
                    subtractFeesFrom, // distribute transaction fee equally over all recipients
                };
            }

            else
            {
                args = new object[]
                {
                    string.Empty, // default account
                    amounts, // addresses and associated amounts
                    1, // only spend funds covered by this many confirmations
                    comment, // tx comment
                };
            }

            var didUnlockWallet = false;

            tryTransfer:
            var result = await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.SendMany, ct, args);

            if(result.Error == null)
            {
                var txId = result.Response.Value<string>();

                NotifyPayoutSuccess(poolConfig.Id, balances, new[]
                {
                    txId
                }, null);
            }

            else
            {
                if(result.Error.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED && !didUnlockWallet)
                {
                    if(!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
                    {
                        logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                        var unlockResult = await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletPassphrase, ct, new[]
                        {
                            extraPoolPaymentProcessingConfig.WalletPassword,
                            (object) 5 // unlock for N seconds
                        });

                        if(unlockResult.Error == null)
                        {
                            didUnlockWallet = true;
                            goto tryTransfer;
                        }

                        else
                            logger.Error(() => $"[{LogCategory}] {BitcoinCommands.WalletPassphrase} returned error: {result.Error.Message} code {result.Error.Code}");
                    }

                    else
                        logger.Error(() => $"[{LogCategory}] Wallet is locked but walletPassword was not configured. Unable to send funds.");
                }

                else
                {
                    logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}");

                    NotifyPayoutFailure(poolConfig.Id, balances, $"{BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}", null);
                }
            }
        }

        else
        {
            // send one by one
            foreach(var pair in amounts)
            {
                var amount = pair.Value;
                var address = pair.Key;

                object[] sendArgs;

                if(extraPoolPaymentProcessingConfig?.MinersPayTxFees == true)
                {
                    sendArgs = new object[]
                    {
                        string.Empty, // default account
                        address, // address
                        amount, // amount
                        comment, // tx comment
                        address, // subtract fee from
                    };
                }

                else
                {
                    sendArgs = new object[]
                    {
                        string.Empty, // default account
                        address, // address
                        amount, // amount
                        comment, // tx comment
                    };
                }

                var didUnlockWallet = false;

                tryTransfer:
                var result = await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.SendToAddress, ct, sendArgs);

                if(result.Error == null)
                {
                    var txId = result.Response.Value<string>();

                    NotifyPayoutSuccess(poolConfig.Id, new[]
                    {
                        balances.First(x => x.Address == address)
                    }, new[]
                    {
                        txId
                    }, null);
                }

                else
                {
                    if(result.Error.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED && !didUnlockWallet)
                    {
                        if(!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
                        {
                            logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                            var unlockResult = await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletPassphrase, ct, new[]
                            {
                                extraPoolPaymentProcessingConfig.WalletPassword,
                                (object) 5 // unlock for N seconds
                            });

                            if(unlockResult.Error == null)
                            {
                                didUnlockWallet = true;
                                goto tryTransfer;
                            }

                            else
                                logger.Error(() => $"[{LogCategory}] {BitcoinCommands.WalletPassphrase} returned error: {result.Error.Message} code {result.Error.Code}");
                        }

                        else
                            logger.Error(() => $"[{LogCategory}] Wallet is locked but walletPassword was not configured. Unable to send funds.");
                    }

                    else
                    {
                        logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendToAddress} returned error: {result.Error.Message} code {result.Error.Code}");

                        NotifyPayoutFailure(poolConfig.Id, new[]
                        {
                            balances.First(x => x.Address == address)
                        }, $"{BitcoinCommands.SendToAddress} returned error: {result.Error.Message} code {result.Error.Code}", null);
                    }
                }
            }
        }
    }

    public new double AdjustBlockEffort(double effort)
    {
        return effort;
    }

    #endregion // IPayoutHandler

    private async Task<Block[]> GetBlockInfoAsync(CancellationToken ct, Block[] blocks)
    {
        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            // build args batch
            var argsBatch = page.Select(block => new object[]
            {
                block.TransactionConfirmationData
            }).ToArray();

            // execute batch
            var results = await rpcClient.ExecuteBatchAsync(logger, ct, BitcoinCommands.GetBlock, argsBatch, null);

            for(var j = 0; j < results.Length; j++)
            {
                var result = results[j];
                var block = page[j];

                if(result.Error == null)
                {
                    var blockInfo = result.Response.ToObject<Block>();

                    // coinbase transaction ids might be in the following format:
                    // "b4a216ed0d4e959510dfa676434e8f6ce8e0af4b7b7d9b52b1e713d7ba665d19-0"
                    // we need to strip the index
                    if(blockInfo?.Transactions?.Length > 0)
                    {
                        var txId = blockInfo.Transactions[0];
                        var dashIndex = txId.IndexOf('-');

                        if(dashIndex != -1)
                            txId = txId.Substring(0, dashIndex);

                        blockInfo.Transactions[0] = txId;
                    }

                    result.Add(blockInfo);
                }

                else
                {
                    result.Add(null);
                }
            }
        }

        return result.ToArray();
    }

    private decimal GetBaseBlockReward(int height)
    {
        // Space MVC имеет начальную награду 25 SPACE и уменьшение вдвое каждые 131250 блоков
        var halvings = height / 131250;
        
        // Максимальное количество уменьшений - 64 (как в Bitcoin)
        if(halvings >= 64)
            return 0;

        // Начальная награда 25 SPACE
        var reward = 25m;

        // Уменьшаем награду вдвое за каждый пройденный интервал
        reward = reward / (decimal)Math.Pow(2, halvings);

        return reward;
    }
} 