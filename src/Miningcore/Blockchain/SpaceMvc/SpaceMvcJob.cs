using Miningcore.Blockchain.Bitcoin;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Contract = Miningcore.Contracts.Contract;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Blockchain.SpaceMvc;

public class SpaceMvcJob : BitcoinJob
{
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
            bs.ReadWrite(ref coinbaseIndex);
            bs.ReadWrite(ref script);
            bs.ReadWrite(ref coinbaseSequence);

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
        mt = new MerkleTree(merkleBranchesHex);
    }
} 