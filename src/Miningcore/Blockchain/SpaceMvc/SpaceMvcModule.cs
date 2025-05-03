using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Mining;
using Miningcore.Payments;

namespace Miningcore.Blockchain.SpaceMvc;

[CoinFamily(CoinFamily.Bitcoin)]
public class SpaceMvcModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<SpaceMvcJobManager>()
            .AsSelf()
            .SingleInstance();

        builder.RegisterType<SpaceMvcPayoutHandler>()
            .As<IPayoutHandler>()
            .SingleInstance();

        builder.RegisterType<SpaceMvcPool>()
            .As<IPool>()
            .SingleInstance();
    }
} 