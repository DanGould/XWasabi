﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Chaincase.Common.Contracts;
using Chaincase.Common.Services;
using Microsoft.Extensions.Caching.Memory;
using NBitcoin;
using NBitcoin.Protocol;
using NBitcoin.Protocol.Behaviors;
using NBitcoin.Protocol.Connectors;
using Nito.AsyncEx;
using WalletWasabi;
using WalletWasabi.Blockchain.Analysis.FeesEstimation;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.CoinJoin.Client;
using WalletWasabi.CoinJoin.Client.Clients.Queuing;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Stores;
using WalletWasabi.Wallets;

namespace Chaincase.Common
{
    public class Global
    {
        private readonly Config _config;
        private Network Network => _config.Network;
        private readonly UiConfig _uiConfig;
        private readonly BitcoinStore _bitcoinStore;
        private readonly ChaincaseWalletManager _walletManager;
        private readonly ITorManager _torManager;
        private readonly IDataDirProvider _dataDirProvider;
        private string DataDir => _dataDirProvider.Get();
        private CoinJoinProcessor _coinJoinProcessor;
        private readonly ChaincaseSynchronizer _synchronizer;
        public readonly FeeProviders _feeProviders;

        public string AddressManagerFilePath { get; private set; }
        public AddressManager AddressManager { get; private set; }

        public NodesGroup Nodes { get; private set; }
        public TransactionBroadcaster TransactionBroadcaster { get; set; }

        /// <summary>
        /// Responsible for keeping one life cycle event in critical sections at once
        /// </summary>
        private readonly AsyncLock LifeCycleMutex = new AsyncLock();
        private readonly AsyncLock SleepMutex = new AsyncLock();

        private bool InitializationStarted { get; set; } = false;
        public event EventHandler<AppInitializedEventArgs> Initialized = delegate { };
        public bool IsInitialized { get; private set; }
        private bool InitializationCompleted { get; set; }

        private CancellationTokenSource StoppingCts { get; set; }
        private protected CancellationTokenSource SleepCts { get; set; } = new CancellationTokenSource();
        private protected bool IsGoingToSleep = false;

        public Global(
            ITorManager torManager,
            IDataDirProvider dataDirProvider,
            Config config,
            UiConfig uiConfig,
            ChaincaseWalletManager walletManager,
            BitcoinStore bitcoinStore,
            ChaincaseSynchronizer synchronizer,
            FeeProviders feeProviders
            )
        {
            _torManager = torManager;
            _dataDirProvider = dataDirProvider;
            _config = config;
            _uiConfig = uiConfig;
            _walletManager = walletManager;
            _bitcoinStore = bitcoinStore;
            _synchronizer = synchronizer;
            _feeProviders = feeProviders;
            using (BenchmarkLogger.Measure())
            {
                StoppingCts = new CancellationTokenSource();
                Logger.InitializeDefaults(Path.Combine(DataDir, "Logs.txt"));
                _uiConfig.LoadOrCreateDefaultFile();
            }
        }

        public async Task InitializeNoWalletAsync(CancellationToken cancellationToken = default)
        {
            AddressManager = null;
            Logger.LogDebug($"{nameof(Global)}.InitializeNoWalletAsync(): Waiting for a lock");
            try
            {
                InitializationStarted = true;

                Logger.LogDebug($"{nameof(Global)}.InitializeNoWalletAsync(): Got lock");

                MemoryCache Cache = new MemoryCache(new MemoryCacheOptions
                {
                    SizeLimit = 1_000,
                    ExpirationScanFrequency = TimeSpan.FromSeconds(30)
                });
                var bstoreInitTask = _bitcoinStore.InitializeAsync();
                var addressManagerFolderPath = Path.Combine(DataDir, "AddressManager");

                AddressManagerFilePath = Path.Combine(addressManagerFolderPath, $"AddressManager{Network}.dat");
                var addrManTask = InitializeAddressManagerBehaviorAsync();

                var blocksFolderPath = Path.Combine(DataDir, $"Blocks{Network}");
                var userAgent = Constants.UserAgents.RandomElement();
                var connectionParameters = new NodeConnectionParameters { UserAgent = userAgent };

                #region TorProcessInitialization

                if (_config.UseTor)
                {
                    await _torManager.StartAsync(ensureRunning: false, DataDir);
                    Logger.LogInfo($"{nameof(_torManager)} is initialized.");
                }

                Logger.LogInfo($"{nameof(Global)}.InitializeNoWalletAsync():{nameof(_torManager)} is initialized.");

                #endregion TorProcessInitialization

                #region BitcoinStoreInitialization

                await bstoreInitTask.ConfigureAwait(false);

                // Make sure that the height of the wallets will not be better than the current height of the filters.
                _walletManager.SetMaxBestHeight(_bitcoinStore.IndexStore.SmartHeaderChain.TipHeight);

                #endregion BitcoinStoreInitialization

                #region MempoolInitialization

                connectionParameters.TemplateBehaviors.Add(_bitcoinStore.CreateUntrustedP2pBehavior());

                #endregion MempoolInitialization

                #region AddressManagerInitialization

                AddressManagerBehavior addressManagerBehavior = await addrManTask.ConfigureAwait(false);
                connectionParameters.TemplateBehaviors.Add(addressManagerBehavior);

                #endregion AddressManagerInitialization

                #region P2PInitialization
                Node regTestMempoolServingNode = null;
                if (Network == Network.RegTest)
                {
                    Nodes = new NodesGroup(Network, requirements: Constants.NodeRequirements);
                    try
                    {
                        EndPoint bitcoinCoreEndpoint = _config.GetBitcoinP2pEndPoint();

                        Node node = await Node.ConnectAsync(NBitcoin.Network.RegTest, bitcoinCoreEndpoint).ConfigureAwait(false);

                        Nodes.ConnectedNodes.Add(node);

                        regTestMempoolServingNode = await Node.ConnectAsync(NBitcoin.Network.RegTest, bitcoinCoreEndpoint).ConfigureAwait(false);

                        regTestMempoolServingNode.Behaviors.Add(_bitcoinStore.CreateUntrustedP2pBehavior());
                    }
                    catch (SocketException ex)
                    {
                        Logger.LogError(ex);
                    }
                }
                else
                {
                    Nodes = CreateAndConfigureNodesGroup(connectionParameters);
                    regTestMempoolServingNode = null;
                }

                Nodes.Connect();
                Logger.LogInfo($"{nameof(Global)}.InitializeNoWalletAsync(): Start connecting to nodes...");

                if (regTestMempoolServingNode != null)
                {
                    regTestMempoolServingNode.VersionHandshake();
                    Logger.LogInfo($"{nameof(Global)}.InitializeNoWalletAsync(): Start connecting to mempool serving regtest node...");
                }

                #endregion P2PInitialization

                #region SynchronizerInitialization


                var requestInterval = TimeSpan.FromSeconds(3);
                if (Network == Network.RegTest)
                {
                    requestInterval = TimeSpan.FromSeconds(5);
                }

                int maxFiltSyncCount = Network == Network.Main ? 1000 : 10000; // On testnet, filters are empty, so it's faster to query them together

                _synchronizer.Start(requestInterval, TimeSpan.FromMinutes(5), maxFiltSyncCount);
                Logger.LogInfo($"{nameof(Global)}.InitializeNoWalletAsync(): Start synchronizing filters...");

                #endregion SynchronizerInitialization

                TransactionBroadcaster = new TransactionBroadcaster(Network, _bitcoinStore, _synchronizer, Nodes, _walletManager, null);
                // CoinJoinProcessor maintains an event handler to process joins.
                // We need this reference.
                _coinJoinProcessor = new CoinJoinProcessor(_synchronizer, _walletManager, null);

                #region Blocks provider

                var blockProvider = new CachedBlockProvider(
                    new SmartBlockProvider(
                        new P2pBlockProvider(Nodes, null, _synchronizer, _config.ServiceConfiguration, Network),
                        Cache),
                    new FileSystemBlockRepository(blocksFolderPath, Network));

                #endregion Blocks provider

                _walletManager.RegisterServices(_bitcoinStore, _synchronizer, Nodes, _config.ServiceConfiguration, _feeProviders, blockProvider);

                Initialized(this, new AppInitializedEventArgs(this));
                IsInitialized = true;
            }
            finally
            {
                InitializationCompleted = true;
                StoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Logger.LogDebug($"Initialization Completed");
            }
        }

        private NodesGroup CreateAndConfigureNodesGroup(NodeConnectionParameters connectionParameters)
        {
            var maximumNodeConnection = 12;
            var bestEffortEndpointConnector = new BestEffortEndpointConnector(maximumNodeConnection / 2);
            connectionParameters.EndpointConnector = bestEffortEndpointConnector;
            if (_config.UseTor)
            {
                connectionParameters.TemplateBehaviors.Add(new SocksSettingsBehavior(_config.TorSocks5EndPoint, onlyForOnionHosts: false, networkCredential: null, streamIsolation: true));
            }
            var nodes = new NodesGroup(Network, connectionParameters, requirements: Constants.NodeRequirements);
            nodes.ConnectedNodes.Added += ConnectedNodes_OnAddedOrRemoved;
            nodes.ConnectedNodes.Removed += ConnectedNodes_OnAddedOrRemoved;
            nodes.MaximumNodeConnection = maximumNodeConnection;
            return nodes;
        }

        public async Task<AddressManagerBehavior> InitializeAddressManagerBehaviorAsync()
        {
            var needsToDiscoverPeers = true;
            if (Network == NBitcoin.Network.RegTest)
            {
                AddressManager = new AddressManager();
                Logger.LogInfo($"Fake {nameof(AddressManager)} is initialized on the RegTest.");
            }
            else
            {
                try
                {
                    AddressManager = await NBitcoinHelpers.LoadAddressManagerFromPeerFileAsync(AddressManagerFilePath).ConfigureAwait(false);

                    // Most of the times we do not need to discover new peers. Instead, we can connect to
                    // some of those that we already discovered in the past. In this case we assume that
                    // discovering new peers could be necessary if our address manager has less
                    // than 500 addresses. 500 addresses could be okay because previously we tried with
                    // 200 and only one user reported he/she was not able to connect (there could be many others,
                    // of course).
                    // On the other side, increasing this number forces users that do not need to discover more peers
                    // to spend resources (CPU/bandwidth) to discover new peers.
                    needsToDiscoverPeers = _config.UseTor == true || AddressManager.Count < 500;
                    Logger.LogInfo($"Loaded {nameof(AddressManager)} from `{AddressManagerFilePath}`.");
                }
                catch (DirectoryNotFoundException ex)
                {
                    Logger.LogInfo($"{nameof(AddressManager)} did not exist at `{AddressManagerFilePath}`. Initializing new one.");
                    Logger.LogTrace(ex);
                    AddressManager = new AddressManager();
                }
                catch (FileNotFoundException ex)
                {
                    Logger.LogInfo($"{nameof(AddressManager)} did not exist at `{AddressManagerFilePath}`. Initializing new one.");
                    Logger.LogTrace(ex);
                    AddressManager = new AddressManager();
                }
                catch (OverflowException ex)
                {
                    // https://github.com/zkSNACKs/WalletWasabi/issues/712
                    Logger.LogInfo($"{nameof(AddressManager)} has thrown `{nameof(OverflowException)}`. Attempting to autocorrect.");
                    File.Delete(AddressManagerFilePath);
                    Logger.LogTrace(ex);
                    AddressManager = new AddressManager();
                    Logger.LogInfo($"{nameof(AddressManager)} autocorrection is successful.");
                }
                catch (FormatException ex)
                {
                    // https://github.com/zkSNACKs/WalletWasabi/issues/880
                    Logger.LogInfo($"{nameof(AddressManager)} has thrown `{nameof(FormatException)}`. Attempting to autocorrect.");
                    File.Delete(AddressManagerFilePath);
                    Logger.LogTrace(ex);
                    AddressManager = new AddressManager();
                    Logger.LogInfo($"{nameof(AddressManager)} autocorrection is successful.");
                }
            }

            var addressManagerBehavior = new AddressManagerBehavior(AddressManager)
            {
                Mode = needsToDiscoverPeers ? AddressManagerBehaviorMode.Discover : AddressManagerBehaviorMode.None
            };
            return addressManagerBehavior;
        }

        private void ConnectedNodes_OnAddedOrRemoved(object? sender, NodeEventArgs e)
        {
            if (Nodes.NodeConnectionParameters.EndpointConnector is BestEffortEndpointConnector bestEffortEndPointConnector)
            {
                if (sender is NodesCollection nodesCollection)
                {
                    bestEffortEndPointConnector.UpdateConnectedNodesCounter(nodesCollection.Count);
                }
            }

        }

        /// <returns>If initialization is successful, otherwise it was interrupted which means stopping was requested.</returns>
        public async Task<bool> WaitForInitializationCompletedAsync(CancellationToken cancellationToken)
        {
            while (!InitializationCompleted)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            return !StoppingCts.IsCancellationRequested;
        }

        public event EventHandler Resumed;
        private protected CancellationTokenSource ResumeCts { get; set; } = new CancellationTokenSource();
        private protected bool IsResuming { get; set; } = false;

        public void HandleRemoteNotification()
        {
            if (_walletManager?.SleepingCoins is { } && _torManager?.State != TorState.Started && _torManager.State != TorState.Connected)
            {
                _ = BackgroundResumeToCoinJoin();
                // sleep instruction happens from iOS lifecycle
            }
        }

        public async Task BackgroundResumeToCoinJoin()
		{
            if (IsResuming)
            {
                Logger.LogDebug($"{MethodBase.GetCurrentMethod().Name}: SleepCts.Cancel()");
                SleepCts.Cancel();
                return;
            }

            try
            {
                Resumed?.Invoke(this, null);
                Logger.LogDebug($"{MethodBase.GetCurrentMethod().Name}: Waiting for a lock");
                var backgroundCts = new CancellationTokenSource();
                using (await LifeCycleMutex.LockAsync(backgroundCts.Token))
                {
                    // IsResuming = true; // BG => Don't flag. Pass OnResume() if foregrounded.
                    Logger.LogDebug($"{MethodBase.GetCurrentMethod().Name}: Entered critical section");

                    // don't ever cancel Init. use an ephemeral token
                    await WaitForInitializationCompletedAsync(new CancellationToken());

                    if (_torManager?.State != TorState.Started && _torManager.State != TorState.Connected)
                    {
                        await _torManager.StartAsync(false, DataDir).ConfigureAwait(false);
                    }

                    var requestInterval = TimeSpan.FromSeconds(1);
                    _synchronizer.Resume(requestInterval);
                    Logger.LogInfo($"{MethodBase.GetCurrentMethod().Name}: Start synchronizing filters...");

                    if (_walletManager.SleepingCoins is { })
                    {
                        await _walletManager.CurrentWallet.ChaumianClient.QueueCoinsToMixAsync(_walletManager.SleepingCoins);
                        _walletManager.SleepingCoins = null;
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                Logger.LogTrace($"{MethodBase.GetCurrentMethod().Name}: Exception OperationCanceledException: {ex}");
            }
            finally
            {
                Logger.LogDebug($"{MethodBase.GetCurrentMethod().Name}: Chaincase Resumed to CoinJoin");
            }
        }

        public async Task OnResuming(int syncInterval = 30)
        {
            if (IsResuming)
            {
                Logger.LogDebug($"{nameof(Global)}.OnResuming(): SleepCts.Cancel()");
                SleepCts.Cancel();
                return;
            }

            try
            {
                Resumed?.Invoke(this, null);
                Logger.LogDebug($"{nameof(Global)}.OnResuming(): Waiting for a lock");
                ResumeCts.Dispose();
                ResumeCts = new CancellationTokenSource();
                using (await LifeCycleMutex.LockAsync(ResumeCts.Token))
                {
                    #region CriticalSection
                    IsResuming = true;
                    Logger.LogDebug($"{nameof(Global)}.OnResuming(): Entered critical section");

                    // don't ever cancel Init. use an ephemeral token
                    await WaitForInitializationCompletedAsync(new CancellationToken());

                    var userAgent = Constants.UserAgents.RandomElement();
                    var connectionParameters = new NodeConnectionParameters { UserAgent = userAgent };
                    var addrManTask = InitializeAddressManagerBehaviorAsync();
                    AddressManagerBehavior addressManagerBehavior = await addrManTask.ConfigureAwait(false);
                    connectionParameters.TemplateBehaviors.Add(addressManagerBehavior);

                    if (_torManager?.State != TorState.Started && _torManager.State != TorState.Connected)
                    {
                        await _torManager.StartAsync(false, DataDir);
                    }

                    Nodes.Connect();
                    Logger.LogInfo($"{nameof(Global)}.OnResuming():Start connecting to nodes...");

                    _synchronizer.Resume();

                    Logger.LogInfo($"{nameof(Global)}.OnResuming():Start synchronizing filters...");

                    if (_walletManager.SleepingCoins is { })
                    {
                        await _walletManager.CurrentWallet.ChaumianClient.QueueCoinsToMixAsync(_walletManager.SleepingCoins);
                        _walletManager.SleepingCoins = null;
                    }

                    IsResuming = false;
                    #endregion CriticalSection
                }
            }
            catch (OperationCanceledException ex)
            {
                Logger.LogTrace($"{nameof(Global)}.OnResuming(): Exception OperationCanceledException risen: {ex}");
            }
            finally
            {
                Logger.LogDebug($"{nameof(Global)}.OnResuming():Chaincase Resumed");
            }
        }

        public async Task OnSleeping()
        {
            if (IsGoingToSleep)
            {
                Logger.LogDebug($"{nameof(Global)}.OnResuming(): ResumeCts.Cancel()");
                ResumeCts.Cancel();
                return;
            }

            try
            {
                Logger.LogDebug($"{nameof(Global)}.OnSleeping(): Waiting for a lock");
                SleepCts.Dispose();
                SleepCts = new CancellationTokenSource();
                using (await LifeCycleMutex.LockAsync(SleepCts.Token))
                {
                    #region CriticalSection
                    Logger.LogDebug($"{nameof(Global)}.OnSleeping(): Entered critical section");

                    IsGoingToSleep = true;

                    // don't ever cancel Init. use an ephemeral token
                    await WaitForInitializationCompletedAsync(new CancellationToken());


                    if (_torManager?.State != TorState.Stopped) // OnionBrowser && Dispose@Global
                    {
                        await _torManager.StopAsync();
                        Logger.LogInfo($"{nameof(Global)}.OnSleeping():{nameof(_torManager)} is stopped.");
                    }

                    try
                    {
                        using var dequeueCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
                        await _walletManager.DequeueAllCoinsGracefullyAsync(DequeueReason.ApplicationExit, dequeueCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Error during {nameof(_walletManager.DequeueAllCoinsGracefullyAsync)}: {ex}");
                    }

                    var synchronizer = _synchronizer;
                    if (synchronizer is { })
                    {
                        await synchronizer.SleepAsync();
                        Logger.LogInfo($"{nameof(Global)}.OnSleeping():{nameof(_synchronizer)} is sleeping.");
                    }

                    var addressManagerFilePath = AddressManagerFilePath;
                    if (addressManagerFilePath is { })
                    {
                        IoHelpers.EnsureContainingDirectoryExists(addressManagerFilePath);
                        var addressManager = AddressManager;
                        if (addressManager is { })
                        {
                            addressManager.SavePeerFile(AddressManagerFilePath, _config.Network);
                            Logger.LogInfo($"{nameof(Global)}.OnSleeping():{nameof(AddressManager)} is saved to `{AddressManagerFilePath}`.");
                        }
                    }

                    var nodes = Nodes;
                    if (nodes is { })
                    {
                        nodes.Disconnect();
                        while (nodes.ConnectedNodes.Any(x => x.IsConnected))
                        {
                            await Task.Delay(50);
                        }

                        Logger.LogInfo($"{nameof(Global)}.OnSleeping():{nameof(Nodes)} are disconnected.");
                    }

                    IsGoingToSleep = false;
                    #endregion CriticalSection
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"{nameof(Global)}.OnSleeping(): Error while sleeping: {ex}");
            }
            finally
            {
                Logger.LogDebug($"{nameof(Global)}.OnSleeping():Chaincase Sleeping"); // no real termination on iOS
            }
        }
    }
}
