using Miningcore.Blockchain.Bitcoin;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Extensions;
using NBitcoin;
using NBitcoin.DataEncoders;
using System.Globalization;
using System.Text;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.SpaceMvc;

public class SpaceMvcJob : BitcoinJob
{
    protected new uint txVersion = 1;
    protected new uint txInputCount = 1;
    protected new uint256 sha256Empty = uint256.Zero;
    protected new uint txInIndex = 0;
    protected new uint txInSequence = 0;
    protected new uint txLockTime = 0;

    protected override Transaction CreateOutputTransaction()
    {
        rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);
        var tx = Transaction.Create(network);

        // Space MVC не имеет специальных выходов для payee, masternodes и т.д.
        // Весь награда идет в пул
        tx.Outputs.Add(rewardToPool, poolAddressDestination);

        return tx;
    }

    protected override void BuildCoinbase()
    {
        var script = TxIn.CreateCoinbase((int)BlockTemplate.Height).ScriptSig;

        // output transaction
        txOut = CreateOutputTransaction();

        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // version
            bs.ReadWrite(ref txVersion);

            // serialize (simulated) input transaction
            bs.ReadWriteAsVarInt(ref txInputCount);
            bs.ReadWrite(ref sha256Empty);
            bs.ReadWrite(ref txInIndex);
            bs.ReadWrite(ref script);
            bs.ReadWrite(ref txInSequence);

            // serialize output transaction
            var txOutBytes = SerializeOutputTransaction(txOut);
            bs.ReadWrite(ref txOutBytes);

            // misc
            bs.ReadWrite(ref txLockTime);

            coinbaseInitial = stream.ToArray();
            coinbaseInitialHex = Encoders.Hex.EncodeData(coinbaseInitial);
        }

        // build merkle root
        merkleBranchesHex = Array.Empty<string>();
        mt = new MerkleTree(merkleBranchesHex.Select(x => Encoders.Hex.DecodeData(x)).ToArray());
    }
} 