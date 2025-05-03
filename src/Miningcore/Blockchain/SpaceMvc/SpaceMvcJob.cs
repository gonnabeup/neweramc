using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
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
            coinbaseInitialHex = coinbaseInitial.ToHexString();
        }

        // build merkle root
        merkleBranchesHex = new string[0];
        mt = new MerkleTree(merkleBranchesHex);
    }

    protected override byte[] SerializeHeader(Span<byte> coinbaseHash, uint nTime, uint nonce, uint? versionMask, uint? versionBits)
    {
        // build merkle-root
        var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());

        // serialize block header
        var blockHeader = new BlockHeader
        {
            Version = (int)BlockTemplate.Version,
            Bits = new Target(Encoders.Hex.DecodeData(BlockTemplate.Bits)),
            HashPrevBlock = uint256.Parse(BlockTemplate.PreviousBlockhash),
            HashMerkleRoot = new uint256(merkleRoot),
            NTime = nTime,
            Nonce = nonce
        };

        return blockHeader.ToBytes();
    }
} 