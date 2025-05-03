using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Stratum;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
namespace Miningcore.Blockchain.SpaceMvc.Configuration;

public class SpaceMvcPoolPaymentProcessingConfigExtra
{
    /// <summary>
    /// Wallet Password if the daemon is running with an encrypted wallet (used for unlocking wallet during payment processing)
    /// </summary>
    public string WalletPassword { get; set; }

    /// <summary>
    /// if True, miners pay payment tx fees
    /// </summary>
    public bool MinersPayTxFees { get; set; }
} 