﻿using System.IO;
using Chaincase.Common;
using Chaincase.Common.Contracts;
using NBitcoin;
using ReactiveUI;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Helpers;

namespace Chaincase.UI.ViewModels
{
	public class NewPasswordViewModel : ReactiveObject
	{
		protected Global Global { get; }
		protected IHsmStorage Hsm { get; }
		public NewPasswordViewModel(Global global, IHsmStorage hsmStorage)
		{
			Global = global;
			Hsm = hsmStorage;
		}

		public void SetPassword(string password)
		{
			// Here we are not letting anything that will be autocorrected later.
			// Generate wallet with password exactly as entered for compatibility.
			// Todo what do we do if PasswordHelper.Guard fails?
			PasswordHelper.Guard(password);

			string walletFilePath = Path.Combine(Global.WalletManager.WalletDirectories.WalletsDir, $"{Global.Network}.json");
			KeyManager.CreateNew(out Mnemonic seedWords, password, walletFilePath);
			// MUST prompt permissions
			Hsm.SetAsync($"{Global.Network}-seedWords", seedWords.ToString());

			Global.UiConfig.HasSeed = true;
			Global.UiConfig.ToFile();
			Global.SetDefaultWallet();
		}
	}
}
