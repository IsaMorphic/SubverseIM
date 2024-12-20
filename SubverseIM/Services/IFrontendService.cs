﻿using SubverseIM.Models;
using SubverseIM.ViewModels.Pages;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SubverseIM.Services
{
    public interface IFrontendService
    {
        bool NavigatePreviousView();

        void NavigateLaunchedUri();

        void NavigateContactView(MessagePageViewModel? parentOrNull = null);

        void NavigateContactView(SubverseContact contact);

        void NavigateMessageView(IEnumerable<SubverseContact> contacts);

        Task RunOnceBackgroundAsync();

        Task RunOnceAsync(CancellationToken cancellationToken = default);
    }
}
