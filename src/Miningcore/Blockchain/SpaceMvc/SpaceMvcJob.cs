using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.SpaceMvc.Configuration;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Native;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Contract = Miningcore.Contracts.Contract;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Blockchain.SpaceMvc;

public class SpaceMvcJob : BitcoinJob
{
    protected uint txVersion = 1;
    protected uint txInputCount = 1;
    protected uint256 sha256Empty = uint256.Zero;
    protected uint txInIndex = 0;
    protected uint txInSequence = 0;
    protected uint txLockTime = 0;

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