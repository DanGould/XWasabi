﻿using System.IO;
using System.Threading.Tasks;
using Chaincase.Common;
using Chaincase.Common.Contracts;
using ReactiveUI;

namespace Chaincase.UI.ViewModels
{
	public class WalletInfoViewModel : ReactiveObject
    {
	    private readonly IFileShare _fileShare;
	    protected Global Global { get; }

        public WalletInfoViewModel(Global global, IFileShare fileShare)
        {
	        _fileShare = fileShare;
	        Global = global;

	        // var canBackUp = this.WhenAnyValue(x => x.Global.UiConfig.HasSeed, hs => hs == true);

	        //NavBackUpCommand = ReactiveCommand.CreateFromObservable(() =>
	        //{
	        //	ViewStackService.PushPage(new StartBackUpViewModel()).Subscribe();
	        //	return Observable.Return(Unit.Default);
	        //}, canBackUp);

	        //ShareLogsCommand = ReactiveCommand.CreateFromTask(ShareLogs);
	        //ExportWalletCommand = ReactiveCommand.CreateFromTask(ExportWallet);
        }

        public async Task ShareDebugLog()
        {
            var file = Path.Combine(Global.DataDir, "Logs.txt");

            await _fileShare.ShareFile(file, "Share Debug Logs");
        }

        public async Task ExportWallet()
        {
            var file = Path.Combine(Global.DataDir, $"Wallets/{Global.Network}.json");

            await _fileShare.ShareFile(file, "Export Wallet");
        }

        public string ExtendedAccountPublicKey => Global.Wallet.KeyManager.ExtPubKey.ToString(Global.Network) ?? "";
        public string AccountKeyPath => $"m/{ Global.Wallet.KeyManager.AccountKeyPath}";
    }
}
