﻿using System.Threading.Tasks;
using Chaincase.Common;
using Chaincase.Common.Contracts;
using WalletWasabi.TorSocks5;

namespace Chaincase.SSB
{
	public class DesktopTorManager : ITorManager
	{
		private readonly Config _config;
		private TorProcessManager _torProcessManager;

		public DesktopTorManager(Config config)
		{
			_config = config;
		}

		public TorState State => _torProcessManager?.IsRunning is true ? TorState.Connected : TorState.None;
		public Task StopAsync()
		{
			return _torProcessManager.StopAsync();
		}

		public Task StartAsync(bool ensureRunning, string dataDir)
		{
			_torProcessManager ??= _config.UseTor
				? new TorProcessManager(_config.TorSocks5EndPoint, null)
				: TorProcessManager.Mock();
			_torProcessManager.Start(ensureRunning, dataDir);
			return Task.CompletedTask;
		}

		string ITorManager.CreateHiddenService()
		{
			// hidden service support for desktop may follow _torProcessManager
			// update with @kiminuo's changes
			throw new System.NotImplementedException();
		}

		void ITorManager.DestroyHiddenService(string serviceId)
		{
			throw new System.NotImplementedException();
		}
	}
}
