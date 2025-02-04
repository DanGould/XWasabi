using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Chaincase.Common;
using Chaincase.Common.Contracts;
using Chaincase.Common.Models;
using Chaincase.Common.Services;
using DynamicData;
using DynamicData.Binding;
using NBitcoin;
using ReactiveUI;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.CoinJoin.Client.Clients.Queuing;
using WalletWasabi.CoinJoin.Client.Rounds;
using WalletWasabi.CoinJoin.Common.Models;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;

namespace Chaincase.UI.ViewModels
{
	public class CoinJoinViewModel : ReactiveObject
    {
        public const int MaxInputsAllowed = 7; // defined in CoinJoin Controller @ Backend

        private readonly ChaincaseWalletManager _walletManager;
        private readonly Config _config;
        private readonly INotificationManager _notificationManager;

        private CompositeDisposable Disposables { get; set; }

        private string _coordinatorFeePercent;
        private int _peersRegistered;
        private int _peersNeeded;
        private int _peersQueued;

        private RoundPhaseState _roundPhaseState;
        private DateTimeOffset _roundTimesout;
        private TimeSpan _timeLeftTillRoundTimeout;
        private Money _requiredBTC;
        private Money _amountQueued;
        private bool _isDequeueBusy;
        private bool _isEnqueueBusy;
        private bool _isQueuedToCoinJoin;
        private bool _isRegistrationBusy;
        private string _balance;
        private SelectCoinsViewModel _selectCoinsViewModel;
        private ReadOnlyObservableCollection<CoinViewModel> _coinViewModels;
        private ObservableAsPropertyHelper<bool> _isRegistered;
        private ObservableAsPropertyHelper<bool> _hasMostRecentRegistrationResponse;


        private DateTimeOffset _notificationTimeOffset;

        public CoinJoinViewModel(ChaincaseWalletManager walletManager, Config config, INotificationManager notificationManager, SelectCoinsViewModel selectCoinsViewModel)
        {
            _walletManager = walletManager;
            _config = config;
            _notificationManager = notificationManager;
            CoinList = selectCoinsViewModel;

            CoinList.RootList
                .Connect()
                .ObserveOn(RxApp.MainThreadScheduler)
                .Bind(out _coinViewModels)
                .Subscribe();

            var coinSet = _coinViewModels
                .ToObservableChangeSet()
                .AutoRefresh(x => x.Status)
                .ToCollection();

            var isAnyCoinRegisteredObservable = coinSet
                .Select(x => x.Any(coin => coin.Status == SmartCoinStatus.MixingInputRegistration));

            _isRegistered = isAnyCoinRegisteredObservable
                .Throttle(TimeSpan.FromSeconds(0.5))
                .ToProperty(this, x => x.IsRegistered, scheduler: RxApp.MainThreadScheduler);

            _hasMostRecentRegistrationResponse = coinSet
                .Select(x => x.Any(coin =>
                {
                    return
                    coin.Status == SmartCoinStatus.MixingBanned ||
                    coin.Status == SmartCoinStatus.MixingWaitingForConfirmation ||
                    coin.Status == SmartCoinStatus.SpentAccordingToBackend ||
                    coin.Status == SmartCoinStatus.MixingConnectionConfirmation ||
                    coin.Status == SmartCoinStatus.MixingOutputRegistration;
                }))
                .Merge(isAnyCoinRegisteredObservable)
                .ToProperty(this, x => x.HasMostRecentRegisterationResponse, scheduler: RxApp.MainThreadScheduler);

            this.WhenAnyValue(x => x.IsRegistered)
                .Subscribe(_ =>
                {
                    ScheduleConfirmNotification(RoundTimesout);
                });

            this.WhenAnyValue(x => x.HasMostRecentRegisterationResponse)
                .Throttle(TimeSpan.FromSeconds(0.5))
                .Subscribe(_ =>
                {
                    if (IsRegistrationBusy)
                    {
                        // variable assignment inside Subscribe() is code smell
                        // I feel like this should be an ObservableAsPropertyHelper
                        // but I'm not sure how to make that fit here with the throttle
                        // https://www.reactiveui.net/docs/handbook/observable-as-property-helper/
                        IsRegistrationBusy = false;
                    }
                });

            if (Disposables != null)
            {
                throw new Exception("Wallet opened before it was closed.");
            }

            Disposables = new CompositeDisposable();

            // Infer coordinator fee
            var registrableRound = _walletManager.CurrentWallet?.ChaumianClient?.State?.GetRegistrableRoundOrDefault();

            CoordinatorFeePercent = registrableRound?.State?.CoordinatorFeePercent.ToString() ?? "0.003";

            // Select most advanced coin join round
            ClientRound mostAdvancedRound = _walletManager.CurrentWallet?.ChaumianClient?.State?.GetMostAdvancedRoundOrDefault();
            if (mostAdvancedRound != default)
            {
                RoundPhaseState = new RoundPhaseState(mostAdvancedRound.State.Phase, _walletManager.CurrentWallet.ChaumianClient?.State.IsInErrorState ?? false);
                RoundTimesout = mostAdvancedRound.State.Phase == RoundPhase.InputRegistration ? mostAdvancedRound.State.InputRegistrationTimesout : DateTimeOffset.UtcNow;
                PeersRegistered = mostAdvancedRound.State.RegisteredPeerCount;
                PeersQueued = mostAdvancedRound.State.QueuedPeerCount;
                PeersNeeded = mostAdvancedRound.State.RequiredPeerCount;
                RequiredBTC = mostAdvancedRound.State.CalculateRequiredAmount();
            }
            else
            {
                RoundPhaseState = new RoundPhaseState(RoundPhase.InputRegistration, false);
                RoundTimesout = DateTimeOffset.UtcNow;
                PeersRegistered = 0;
                PeersQueued = 0;
                PeersNeeded = 100;
                RequiredBTC = Money.Parse("0.01");
            }

            // Set time left in round 
            this.WhenAnyValue(x => x.RoundTimesout)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    TimeLeftTillRoundTimeout = TimeUntilOffset(RoundTimesout);
                });

            Task.Run(async () =>
            {
                while (_walletManager.CurrentWallet?.ChaumianClient == null)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }

                // Update view model state on chaumian client state updates
                Observable.FromEventPattern(_walletManager.CurrentWallet.ChaumianClient, nameof(_walletManager.CurrentWallet.ChaumianClient.CoinQueued))
                    .Merge(Observable.FromEventPattern(_walletManager.CurrentWallet.ChaumianClient, nameof(_walletManager.CurrentWallet.ChaumianClient.OnDequeue)))
                    .Merge(Observable.FromEventPattern(_walletManager.CurrentWallet.ChaumianClient, nameof(_walletManager.CurrentWallet.ChaumianClient.StateUpdated)))
                    .ObserveOn(RxApp.MainThreadScheduler)
                    .Subscribe(_ => UpdateStates())
                    .DisposeWith(Disposables);

                // Remove notification on unconfirming status in coin join round
                Observable.FromEventPattern(_walletManager.CurrentWallet.ChaumianClient, nameof(_walletManager.CurrentWallet.ChaumianClient.OnDequeue))
                       .Subscribe(pattern =>
                       {
                           var e = (DequeueResult)pattern.EventArgs;
                           try
                           {
                               foreach (var success in e.Successful.Where(x => x.Value.Any()))
                               {
                                   DequeueReason reason = success.Key;
                                   if (reason == DequeueReason.UserRequested)
                                   {
                                       _notificationManager.RemoveAllPendingNotifications();
                                   }
                               }
                           }
                           catch (Exception ex)
                           {
                               Logger.LogWarning(ex);
                           }
                       })
                       .DisposeWith(Disposables);
            });

            // Update timeout label
            Observable.Interval(TimeSpan.FromSeconds(1))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ =>
                {
                    TimeLeftTillRoundTimeout = TimeUntilOffset(RoundTimesout);
                }).DisposeWith(Disposables);
        }

        private TimeSpan TimeUntilOffset(DateTimeOffset offset)
        {
            TimeSpan left = offset - DateTimeOffset.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero; // Make sure cannot be less than zero.
        }

        private void UpdateStates()
        {
            var chaumianClient = _walletManager.CurrentWallet.ChaumianClient;
            if (chaumianClient is null)
            {
                return;
            }

            AmountQueued = chaumianClient.State.SumAllQueuedCoinAmounts();

            var registrableRound = chaumianClient.State.GetRegistrableRoundOrDefault();
            if (registrableRound != default)
            {
                CoordinatorFeePercent = registrableRound.State.CoordinatorFeePercent.ToString();
                UpdateRequiredBtcLabel(registrableRound);
            }

            var mostAdvancedRound = chaumianClient.State.GetMostAdvancedRoundOrDefault();
            if (mostAdvancedRound != default)
            {
                if (!chaumianClient.State.IsInErrorState)
                {
                    RoundPhaseState = new RoundPhaseState(mostAdvancedRound.State.Phase, false);
                    RoundTimesout = mostAdvancedRound.State.Phase == RoundPhase.InputRegistration ? mostAdvancedRound.State.InputRegistrationTimesout : DateTimeOffset.UtcNow;
                }
                else
                {
                    RoundPhaseState = new RoundPhaseState(RoundPhaseState.Phase, true);
                }

                this.RaisePropertyChanged(nameof(RoundPhaseState));
                this.RaisePropertyChanged(nameof(RoundTimesout));
                PeersRegistered = mostAdvancedRound.State.RegisteredPeerCount;
                PeersQueued = mostAdvancedRound.State.QueuedPeerCount;
                PeersNeeded = mostAdvancedRound.State.RequiredPeerCount;
            }
        }

        private void UpdateRequiredBtcLabel(ClientRound registrableRound)
        {
            if (_walletManager is null)
            {
                return; // Otherwise NullReferenceException at shutdown.
            }

            if (registrableRound == default)
            {
                if (RequiredBTC == default)
                {
                    RequiredBTC = Money.Zero;
                }
            }
            else
            {
                var coins = _walletManager.CurrentWallet.Coins;
                var queued = coins.CoinJoinInProcess();
                if (queued.Any())
                {
                    RequiredBTC = registrableRound.State.CalculateRequiredAmount(_walletManager.CurrentWallet.ChaumianClient.State.GetAllQueuedCoinAmounts().ToArray());
                }
                else
                {
                    var available = coins.Confirmed().Available();
                    RequiredBTC = available.Any()
                        ? registrableRound.State.CalculateRequiredAmount(available.Where(x => x.AnonymitySet < _config.PrivacyLevelStrong).Select(x => x.Amount).ToArray())
                        : registrableRound.State.CalculateRequiredAmount();
                }
            }
        }

        public async Task ExitCoinJoinAsync()
            => await DoDequeueAsync(CoinList.RootList.Items.Where(c => c.CoinJoinInProgress).Select(c => c.Model));

        private async Task DoDequeueAsync(IEnumerable<SmartCoin> coins)
        {
            IsDequeueBusy = true;
            IsRegistrationBusy = false;
            try
            {
                if (!coins.Any())
                {
                    return;
                }

                try
                {
                    await _walletManager.CurrentWallet.ChaumianClient.DequeueCoinsFromMixAsync(coins.ToArray(), DequeueReason.UserRequested);

                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex);
                }
            }
            finally
            {
                IsDequeueBusy = false;
            }
        }

        public async Task DoEnqueueAsync(string password)
        {
            IsEnqueueBusy = true;
            var coins = CoinList.CoinList.Where(c => c.IsSelected).Select(c => c.Model);
            try
            {
                if (!coins.Any())
                {
                    // should never get to this page if there aren't sufficient coins
                    throw new Exception("No coin selected. Select some coin to join.");
                }
                try
                {
                    IsRegistrationBusy = true;
                    await Task.Run(() =>
                    {
                        // If the password is incorrect this throws.
                        PasswordHelper.GetMasterExtKey(_walletManager.CurrentWallet.KeyManager, password, out string compatiblityPassword);
                        if (compatiblityPassword != null)
                        {
                            password = compatiblityPassword;
                        }
                    });

                    await _walletManager.CurrentWallet.ChaumianClient.QueueCoinsToMixAsync(password, coins.ToArray());
                    _notificationManager.RequestAuthorization();
                }
                catch (SecurityException ex)
                {
                    IsRegistrationBusy = false;
                    throw ex;
                }
                catch (Exception ex)
                {
                    IsRegistrationBusy = false;
                    var builder = new StringBuilder(ex.ToTypeMessageString());
                    if (ex is AggregateException aggex)
                    {
                        foreach (var iex in aggex.InnerExceptions)
                        {
                            builder.Append(Environment.NewLine + iex.ToTypeMessageString());
                        }
                    }
                    Logger.LogError(ex);
                    throw ex; // pass it up to the ui
                }
            }
            finally
            {
                IsEnqueueBusy = false;
            }
        }

        void ScheduleConfirmNotification(DateTimeOffset offset)
        {
            const int NOTIFY_TIMEOUT_DELTA = 90; // seconds

            var timeoutSeconds = TimeUntilOffset(offset).TotalSeconds;
            if (timeoutSeconds < NOTIFY_TIMEOUT_DELTA ||
                RoundPhaseState.Phase != RoundPhase.InputRegistration)
                // Just encourage users to keep the app open
                // & prepare CoinJoin to background if possible.
                return;

            if (offset <= _notificationTimeOffset)
                // we've already scheduled this one
                return;

            _notificationTimeOffset = offset;

            // Takes about 10 seconds to start Tor & register again
            var notificationTime = DateTime.Now.AddSeconds(timeoutSeconds);
            string title = $"Time to CoinJoin Now";
            string message = string.Format("Open Chaincase before {0:t}\n to complete the CoinJoin.", notificationTime);

            var timeToNotify = timeoutSeconds - NOTIFY_TIMEOUT_DELTA;
            _notificationManager.ScheduleNotification(title, message, timeToNotify);
        }

        public bool HasSelectedEnough => (CoinList.SelectedAmount ?? Money.Zero) >= RequiredBTC;

        public bool HasTooManyInputs => CoinList.SelectedCount > MaxInputsAllowed;

        public bool IsRegistered => _isRegistered.Value;

        public SelectCoinsViewModel CoinList
        {
            get => _selectCoinsViewModel;
            set => this.RaiseAndSetIfChanged(ref _selectCoinsViewModel, value);
        }

        public Money AmountQueued
        {
            get => _amountQueued;
            set => this.RaiseAndSetIfChanged(ref _amountQueued, value);
        }
        public Money RequiredBTC
        {
            get => _requiredBTC;
            set => this.RaiseAndSetIfChanged(ref _requiredBTC, value);
        }

        public string CoordinatorFeePercent
        {
            get => _coordinatorFeePercent;
            set => this.RaiseAndSetIfChanged(ref _coordinatorFeePercent, value);
        }

        public bool IsQueuedToCoinJoin
        {
            get => _isQueuedToCoinJoin;
            set => this.RaiseAndSetIfChanged(ref _isQueuedToCoinJoin, value);
        }

        public bool IsRegistrationBusy
        {
            get => _isRegistrationBusy;
            set => this.RaiseAndSetIfChanged(ref _isRegistrationBusy, value);
        }

        public bool HasMostRecentRegisterationResponse => _hasMostRecentRegistrationResponse.Value;

        public string Balance
        {
            get => _balance;
            set => this.RaiseAndSetIfChanged(ref _balance, value);
        }

        public int PeersNeeded
        {
            get => _peersNeeded;
            set => this.RaiseAndSetIfChanged(ref _peersNeeded, value);
        }

        public int PeersRegistered
        {
            get => _peersRegistered;
            set => this.RaiseAndSetIfChanged(ref _peersRegistered, value);
        }

        public int PeersQueued
        {
            get => _peersQueued;
            set => this.RaiseAndSetIfChanged(ref _peersQueued, value);
        }

        public RoundPhaseState RoundPhaseState
        {
            get => _roundPhaseState;
            set => this.RaiseAndSetIfChanged(ref _roundPhaseState, value);
        }

        public DateTimeOffset RoundTimesout
        {
            get => _roundTimesout;
            set => this.RaiseAndSetIfChanged(ref _roundTimesout, value);
        }

        public TimeSpan TimeLeftTillRoundTimeout
        {
            get => _timeLeftTillRoundTimeout;
            set => this.RaiseAndSetIfChanged(ref _timeLeftTillRoundTimeout, value);
        }

        public bool IsEnqueueBusy
        {
            get => _isEnqueueBusy;
            set => this.RaiseAndSetIfChanged(ref _isEnqueueBusy, value);
        }

        public bool IsDequeueBusy
        {
            get => _isDequeueBusy;
            set => this.RaiseAndSetIfChanged(ref _isDequeueBusy, value);
        }

        public string RegisteredPercentage => ((decimal)PeersRegistered / (decimal)PeersNeeded).ToString();

        public string QueuedPercentage => ((decimal)PeersQueued / (decimal)PeersNeeded).ToString();
    }
}
