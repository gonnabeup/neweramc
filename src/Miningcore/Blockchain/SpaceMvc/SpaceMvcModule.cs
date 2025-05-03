using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Mining;
using Miningcore.Payments;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.SpaceMvc.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
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
public class SpaceMvcModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<SpaceMvcJobManager>()
            .As<BitcoinJobManagerBase<SpaceMvcJob>>()
            .SingleInstance();

        builder.RegisterType<SpaceMvcPayoutHandler>()
            .As<IPayoutHandler>()
            .SingleInstance();

        builder.RegisterType<SpaceMvcPool>()
            .As<IMiningPool>()
            .SingleInstance();
    }
} 