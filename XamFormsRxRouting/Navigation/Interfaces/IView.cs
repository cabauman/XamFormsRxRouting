﻿using System;
using System.Reactive;

namespace XamFormsRxRouting.Navigation.Interfaces
{
    public interface IView
    {
        IObservable<IPageViewModel> PagePopped { get; }

        IObservable<Unit> PushPage(
            IPageViewModel pageViewModel,
            string contract,
            bool resetStack,
            bool animate);

        IObservable<Unit> PopPage(
            bool animate);

        IObservable<Unit> PushModal(
            IModalViewModel modalViewModel,
            string contract);

        IObservable<Unit> PopModal();
    }
}