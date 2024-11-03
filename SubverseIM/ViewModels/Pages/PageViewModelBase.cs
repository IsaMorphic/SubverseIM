﻿using SubverseIM.Services;

namespace SubverseIM.ViewModels.Pages
{
    public abstract class PageViewModelBase : ViewModelBase
    {
        public IServiceManager ServiceManager { get; }

        public PageViewModelBase(IServiceManager serviceManager)
        {
            ServiceManager = serviceManager;
        }
    }
}
