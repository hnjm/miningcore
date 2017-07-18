﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autofac;
using CodeContracts;
using Microsoft.Extensions.Logging;
using MiningForce.Blockchain;
using MiningForce.Configuration;
using MiningForce.Configuration.Extensions;
using MiningForce.JsonRpc;
using MiningForce.Stratum;
using MiningForce.VarDiff;
using Newtonsoft.Json;
using NLog.LayoutRenderers.Wrappers;

// TODO:
// - periodic job re-broadcast
// - periodic update of pool hashrate
// - banning based on invalid shares

namespace MiningForce.MininigPool
{
    public class Pool : StratumServer
    {
        public Pool(IComponentContext ctx, 
            ILogger<Pool> logger, 
            JsonSerializerSettings serializerSettings) : 
            base(ctx, logger, serializerSettings)
        {
        }

        private readonly PoolStats poolStats = new PoolStats();
        private object currentJobParams;
        private readonly object currentJobParamsLock = new object();
        private IBlockchainJobManager manager;

        protected readonly Dictionary<PoolEndpoint, VarDiffManager> varDiffManagers = 
            new Dictionary<PoolEndpoint, VarDiffManager>();

        protected readonly ConditionalWeakTable<StratumClient, PoolClientContext> miningContexts = 
            new ConditionalWeakTable<StratumClient, PoolClientContext>();

        private static readonly string[] HashRateUnits = { " KH", " MH", " GH", " TH", " PH" };

        #region API-Surface

        public NetworkStats NetworkStats => manager.NetworkStats;
        public PoolStats PoolStats => poolStats;

        public async Task StartAsync(PoolConfig poolConfig)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            this.poolConfig = poolConfig;

            logger.Info(() => $"Pool '{poolConfig.Coin.Name}' starting ...");

            StartListeners(poolConfig);
            await InitializeJobManager(poolConfig);

            OutputPoolInfo();
        }

        #endregion // API-Surface

        private async Task InitializeJobManager(PoolConfig poolConfig)
        {
            manager = ctx.ResolveNamed<IBlockchainJobManager>(poolConfig.Coin.Name.ToLower());
            await manager.StartAsync(poolConfig, this);

            manager.Jobs.Subscribe(OnNewJob);
        }

        protected override void OnClientConnected(StratumClient client)
        {
            // expect miner to establish communication within a certain time
            EnsureNoZombieClient(client);

            // update stats
            lock (clients)
            {
                poolStats.ConnectedMiners = clients.Count;
            }
        }

        protected override void OnClientDisconnected(string subscriptionId)
        {
            // update stats
            lock (clients)
            {
                poolStats.ConnectedMiners = clients.Count;
            }
        }

        protected override async void OnClientSubscribe(StratumClient client, JsonRpcRequest request)
        {
            // var requestParams = request.Params?.ToObject<string[]>();
            // var userAgent = requestParams?.Length > 0 ? requestParams[0] : null;
            var response = await manager.HandleWorkerSubscribeAsync(client);

            var data = new object[]
            {
                new object[]
                {
                    new object[]
                    {
                        StratumConstants.MsgMiningNotify, client.ConnectionId
                    },
                },
            }
            .Concat(response)
            .ToArray();

            // send response
            client.Respond(data, request.Id);

            // get or create context
            var context = GetMiningContext(client);

            // send difficulty
            client.Notify(StratumConstants.MsgSetDifficulty, new object[] { context.Difficulty });

            // Send current job if available
            lock (currentJobParamsLock)
            {
                if (currentJobParams != null)
                {
                    client.Notify(StratumConstants.MsgMiningNotify,
                        //new object[]
                        //{
                        //    "2", "08d0655c78d07c0602b3c937cd316604b64e54bfeb9e7968aa4c110800000001",
                        //    "01000000010000000000000000000000000000000000000000000000000000000000000000ffffffff1f02d50104da536c5908",
                        //    "0d2f6e6f64655374726174756d2f00000000040000000000000000266a24aa21a9ede2f61c3f71d1defd3fa999dfa36953755c690689799962b48bebd836974e8cf9c027a824000000001976a9141bf1c824cd6028bcd65732374baea5dca5fed57088ac180d8f00000000001976a91446504cf062e1f820789c752d336923c2b80fdcee88ac68890900000000001976a91446504cf062e1f820789c752d336923c2b80fdcee88ac00000000",
                        //    new object[0], "20000000", "207fffff", "596c53da", false
                        //});
                        currentJobParams);
                }
            }
        }

        protected override async void OnClientAuthorize(StratumClient client, JsonRpcRequest request)
        {
            var context = GetMiningContext(client);

            var requestParams = request.Params?.ToObject<string[]>();
            var workername = requestParams?.Length > 0 ? requestParams[0] : null;
            var password = requestParams?.Length > 1 ? requestParams[1] : null;

            context.IsAuthorized = await manager.HandleWorkerAuthenticateAsync(client, workername, password);
            client.Respond(context.IsAuthorized, request.Id);
        }

        protected override async void OnClientSubmitShare(StratumClient client, JsonRpcRequest request)
        {
            var context = GetMiningContext(client);

            context.LastActivity = DateTime.UtcNow;

            if (!context.IsAuthorized)
                client.RespondError(StratumError.UnauthorizedWorker, "Unauthorized worker", request.Id);
            else if (!context.IsSubscribed)
                client.RespondError(StratumError.NotSubscribed, "Not subscribed", request.Id);
            else
            {
                UpdateVarDiff(client);

                // submit 
                var requestParams = request.Params?.ToObject<string[]>();
                var accepted = await manager.HandleWorkerSubmitAsync(client, requestParams);
                client.Respond(accepted, request.Id);

                // update client stats
                if (accepted && poolConfig.Banning != null)
                    context.Stats.ValidShares++;
                else
                    context.Stats.InvalidShares++;
            }
        }

        private PoolClientContext GetMiningContext(StratumClient client)
        {
            PoolClientContext context;

            lock (miningContexts)
            {
                if (!miningContexts.TryGetValue(client, out context))
                {
                    context = new PoolClientContext(client, poolConfig);
                    miningContexts.Add(client, context);
                }
            }

            return context;
        }

        private void EnsureNoZombieClient(StratumClient client)
        {
            var isAlive = client.Requests
                .Take(1)
                .Select(_ => true);

            var timeout = Observable.Timer(DateTime.UtcNow.AddSeconds(10))
                .Select(_ => false);

            Observable.Merge(isAlive, timeout)
                .Take(1)
                .Subscribe(alive =>
                {
                    if (!alive)
                    {
                        logger.Debug(() => $"[{client.ConnectionId}] Booting miner because it failed to establish communication within the alloted time");

                        DisconnectClient(client);
                    }

                    else
                    {
                        OnClientConnected(client);
                    }
                });
        }

        private void UpdateVarDiff(StratumClient client)
        {
            var context = GetMiningContext(client);

            if (context.VarDiff != null)
            {
                // get or create manager
                VarDiffManager varDiffManager;

                lock (varDiffManagers)
                {
                    if (!varDiffManagers.TryGetValue(client.PoolEndpoint, out varDiffManager))
                    {
                        varDiffManager = new VarDiffManager(client.PoolEndpoint.VarDiff);
                        varDiffManagers[client.PoolEndpoint] = varDiffManager;
                    }
                }

                // update it
                var newDiff = varDiffManager.Update(context.VarDiff, context.Difficulty);
                if (newDiff != null)
                    context.EnqueueNewDifficulty(newDiff.Value);
            }
        }

        private void OnNewJob(object jobParams)
        {
            logger.Info(() => $"[{poolConfig.Coin.Name}] Received new job params from manager");

            lock (currentJobParamsLock)
            {
                currentJobParams = jobParams;
            }

            if (jobParams != null)
                BroadcastJob(jobParams);
        }

        private void BroadcastJob(object jobParams)
        {
            BroadcastNotification(StratumConstants.MsgMiningNotify, jobParams, client =>
            {
                var context = GetMiningContext(client);

                if (context.IsSubscribed)
                {
                    // if the client has a pending difficulty change, apply it now
                    if(context.ApplyPendingDifficulty())
                        client.Notify(StratumConstants.MsgSetDifficulty, new object[] { context.Difficulty });
                }

                return false;
            });
        }

        private static string FormatHashRate(double hashrate)
        {
            var i = -1;

            do
            {
                hashrate = hashrate / 1024;
                i++;
            } while (hashrate > 1024);
            return (int)Math.Abs(hashrate) + HashRateUnits[i];
        }

        private void OutputPoolInfo()
        {
            var msg = $@"

Mining Pool:            {poolConfig.Coin.Name} 
Network Connected:      {NetworkStats.Network}
Detected Reward Type:   {NetworkStats.RewardType}
Current Block Height:   {NetworkStats.BlockHeight}
Current Connect Peers:  {NetworkStats.ConnectedPeers}
Network Difficulty:     {NetworkStats.Difficulty}
Network Hash Rate:      {FormatHashRate(NetworkStats.HashRate)}
Stratum Port(s):        {string.Join(", ", poolConfig.Ports.Keys)}
Pool Fee:               {poolConfig.RewardRecipients.Sum(x => x.Percentage)}%
";

            logger.Info(()=> msg);
        }
    }
}