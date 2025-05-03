using Miningcore.Configuration;

namespace Miningcore.Blockchain.SpaceMvc.Configuration;

public class SpaceMvcTemplate : BitcoinTemplate
{
    public override string GetAlgorithmName()
    {
        return "SHA256";
    }
} 