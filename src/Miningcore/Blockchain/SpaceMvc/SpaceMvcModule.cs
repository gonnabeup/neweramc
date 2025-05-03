using Autofac;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;

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