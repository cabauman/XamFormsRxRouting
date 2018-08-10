﻿using CoreAnimation;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using UIKit;

namespace RxNavigation
{
    public sealed class MainView : RxNavigationController, IView
    {
        private readonly IScheduler backgroundScheduler;
        private readonly IScheduler mainScheduler;
        private readonly IViewLocator viewLocator;
        private readonly IObservable<IPageViewModel> pagePopped;
        private readonly Subject<Unit> modalPopped;
        private readonly Subject<RxNavigationController> navigationPagePushed;
        private readonly Stack<UINavigationController> navigationPages;

        public MainView(IScheduler backgroundScheduler, IScheduler mainScheduler, IViewLocator viewLocator)
        {
            this.backgroundScheduler = backgroundScheduler ?? RxApp.TaskpoolScheduler;
            this.mainScheduler = mainScheduler ?? RxApp.MainThreadScheduler;
            this.viewLocator = viewLocator ?? ViewLocator.Current;

            this.navigationPagePushed = new Subject<RxNavigationController>();
            this.navigationPages = new Stack<UINavigationController>();
            this.modalPopped = new Subject<Unit>();

            // Each time a RxNavigationController is presented (modally), add a new "controller popped" listener.
            this.pagePopped = navigationPagePushed
                .StartWith(this)
                .Do(
                    x =>
                    {
                        navigationPages.Push(x);
                    })
                .SelectMany(
                    navigationController =>
                    {
                        return navigationController.ControllerPopped
                            .Select(
                                viewController =>
                                {
                                    var viewFor = viewController as IViewFor;
                                    return viewFor?.ViewModel as IPageViewModel;
                                });
                    });
        }

        public IObservable<IPageViewModel> PagePopped => this.pagePopped;

        public IObservable<Unit> ModalPopped => this.modalPopped.AsObservable();

        public IObservable<Unit> PushModal(IPageViewModel modalViewModel, string contract, bool withNavStack)
        {
            return Observable
                .Start(
                    () =>
                    {
                        UIViewController page = this.LocatePageFor(modalViewModel, contract);
                        return page;
                    },
                    this.backgroundScheduler)
                .ObserveOn(this.mainScheduler)
                .SelectMany(
                    viewController =>
                    {
                        this.SetPageTitle(viewController, modalViewModel.Id);
                        RxNavigationController navigationPage = null;
                        if(withNavStack)
                        {
                            viewController = navigationPage = new RxNavigationController(viewController);
                        }

                        return this
                            .navigationPages.Peek()
                            .PresentViewControllerAsync(viewController, true)
                            .ToObservable()
                            .Do(
                                x =>
                                {
                                    if(withNavStack)
                                    {
                                        navigationPagePushed.OnNext(navigationPage);
                                    }
                                });
                    });
        }

        public IObservable<Unit> PopModal()
        {
            var controller = this.navigationPages.Peek();

            return controller
                .PresentingViewController
                .DismissViewControllerAsync(true)
                .ToObservable()
                .Do(
                    x =>
                    {
                        this.modalPopped.OnNext(Unit.Default);
                        if(controller is RxNavigationController)
                        {
                            this.navigationPages.Pop();
                        }
                    });
        }

        public IObservable<Unit> PushPage(IPageViewModel pageViewModel, string contract, bool resetStack, bool animate)
        {
            return Observable
                .Start(
                    () =>
                    {
                        var page = this.LocatePageFor(pageViewModel, contract);
                        return page;
                    },
                    backgroundScheduler)
                .ObserveOn(mainScheduler)
                .SelectMany(
                    page =>
                    {
                        this.SetPageTitle(page, pageViewModel.Id);

                        return Observable
                            .Create<Unit>(
                                observer =>
                                {
                                    CATransaction.Begin();
                                    CATransaction.CompletionBlock = () =>
                                    {
                                        observer.OnNext(Unit.Default);
                                        observer.OnCompleted();
                                    };

                                    if(resetStack)
                                    {
                                        this.navigationPages.Peek().SetViewControllers(null, false);
                                    }

                                    this.navigationPages.Peek().PushViewController(page, animated: animate);

                                    CATransaction.Commit();
                                    return Disposable.Empty;
                                });
                    });
        }

        public IObservable<Unit> PopPage(bool animate) =>
            Observable
                .Create<Unit>(
                    observer =>
                    {
                        CATransaction.Begin();
                        CATransaction.CompletionBlock = () =>
                        {
                            observer.OnNext(Unit.Default);
                            observer.OnCompleted();
                        };

                        this.navigationPages.Peek().PopViewController(animated: animate);

                        CATransaction.Commit();
                        return Disposable.Empty;
                    });

        public void InsertPage(int index, IPageViewModel pageViewModel, string contract = null)
        {
            var page = this.LocatePageFor(pageViewModel, contract);
            this.SetPageTitle(page, pageViewModel.Id);
            var viewControllers = this.navigationPages.Peek().ViewControllers;
            viewControllers = InsertIndices(viewControllers, page, index);
            this.navigationPages.Peek().SetViewControllers(viewControllers, false);
        }

        public void RemovePage(int index)
        {
            var viewControllers = this.navigationPages.Peek().ViewControllers;
            viewControllers = RemoveIndices(viewControllers, index);
            this.navigationPages.Peek().SetViewControllers(viewControllers, false);
        }

        private UIViewController[] RemoveIndices(UIViewController[] indicesArray, int removeAt)
        {
            UIViewController[] newIndicesArray = new UIViewController[indicesArray.Length - 1];

            int i = 0;
            int j = 0;
            while(i < indicesArray.Length)
            {
                if(i != removeAt)
                {
                    newIndicesArray[j] = indicesArray[i];
                    j++;
                }

                i++;
            }

            return newIndicesArray;
        }

        private UIViewController[] InsertIndices(UIViewController[] indicesArray, UIViewController viewController, int index)
        {
            UIViewController[] newIndicesArray = new UIViewController[indicesArray.Length + 1];

            int i = 0;
            int j = 0;
            while(i < indicesArray.Length)
            {
                if(j == index)
                {
                    newIndicesArray[j] = viewController;
                }
                else
                {
                    newIndicesArray[j] = indicesArray[i];
                    i++;
                }

                j++;
            }

            return newIndicesArray;
        }

        private UIViewController LocatePageFor(object viewModel, string contract)
        {
            var viewFor = viewLocator.ResolveView(viewModel, contract);
            var page = viewFor as UIViewController;

            if(viewFor == null)
            {
                throw new InvalidOperationException($"No view could be located for type '{viewModel.GetType().FullName}', contract '{contract}'. Be sure Splat has an appropriate registration.");
            }

            if(page == null)
            {
                throw new InvalidOperationException($"Resolved view '{viewFor.GetType().FullName}' for type '{viewModel.GetType().FullName}', contract '{contract}' is not a Page.");
            }

            viewFor.ViewModel = viewModel;

            return page;
        }

        private void SetPageTitle(UIViewController page, string resourceKey)
        {
            //var title = Localize.GetString(resourceKey);
            page.Title = resourceKey; // title;
        }
    }
}
