﻿using System.Reactive.Disposables;
using ReactiveUI;
using ReactiveUI.XamForms;
using Chaincase.ViewModels;
using Xamarin.Forms;
using System.Reactive.Linq;
using System.Reactive;
using System;

namespace Chaincase.Views
{
	public partial class MainPage : ReactiveContentPage<MainViewModel>
	{

        public MainPage()
        {
            InitializeComponent();

            this.WhenActivated(d =>
            {
                this.OneWayBind(ViewModel,
                    vm => vm.Balance,
                    v => v.Balance.Text)
                    .DisposeWith(d);
                this.OneWayBind(ViewModel,
                    vm => vm.CoinList,
                    v => v.CoinList.ViewModel)
                    .DisposeWith(d);
                this.BindCommand(ViewModel,
                    vm => vm.NavSendCommand,
                    v => v.NavSendCommand)
                    .DisposeWith(d);
                this.BindCommand(ViewModel,
                    vm => vm.NavReceiveCommand,
                    v => v.NavReceiveCommand)
                    .DisposeWith(d);
                this.BindCommand(ViewModel,
                    vm => vm.InitCoinJoin,
                    v => v.CoinJoinButton)
                    .DisposeWith(d);
            });
        }
    }
}
