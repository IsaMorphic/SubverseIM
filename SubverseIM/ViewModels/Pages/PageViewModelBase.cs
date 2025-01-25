﻿using Avalonia.Controls;
using Avalonia.Platform;
using ReactiveUI;
using SubverseIM.Services;

namespace SubverseIM.ViewModels.Pages
{
    public abstract class PageViewModelBase : ViewModelBase
    {
        public abstract bool HasSidebar { get; }

        public abstract string Title { get; }

        public IServiceManager ServiceManager { get; }

        protected PageViewModelBase(IServiceManager serviceManager)
        {
            ServiceManager = serviceManager;
        }

        public abstract void OnOrientationChanged(TopLevel? topLevel);

        public abstract void ToggleSidebarCommand();
    }

    public abstract class PageViewModelBase<TSelf> : PageViewModelBase
        where TSelf : PageViewModelBase<TSelf>
    {
        private bool isSidebarOpen;
        public bool IsSidebarOpen
        {
            get => isSidebarOpen;
            set
            {
                ((TSelf)this).RaiseAndSetIfChanged(ref isSidebarOpen, value);
            }
        }

        private bool isSidebarOverlay;
        public bool IsSidebarOverlay
        {
            get => isSidebarOverlay;
            set
            {
                ((TSelf)this).RaiseAndSetIfChanged(ref isSidebarOverlay, value);
            }
        }

        protected PageViewModelBase(IServiceManager serviceManager) : base(serviceManager)
        {
        }

        public override void OnOrientationChanged(TopLevel? topLevel)
        {
            bool isLandscape = topLevel?.Screens?.Primary?.CurrentOrientation switch
            {
                ScreenOrientation.Landscape => true,
                ScreenOrientation.LandscapeFlipped => true,
                _ => false
            };

            IsSidebarOverlay = !isLandscape;
            if (isLandscape)
            {
                IsSidebarOpen = true;
            }
        }

        public override void ToggleSidebarCommand()
        {
            IsSidebarOpen = !IsSidebarOpen;
        }
    }
}
