﻿using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using Avalonia.ReactiveUI;
using BackgroundTasks;
using Foundation;
using SubverseIM.Models;
using SubverseIM.Services;
using SubverseIM.Services.Implementation;
using UIKit;

namespace SubverseIM.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    private readonly ServiceManager serviceManager;

    private WrappedPeerService? wrappedPeerService;

    public AppDelegate()
    {
        serviceManager = new();
        UIApplication.Notifications.ObserveDidFinishLaunching(HandleApplicationDidLaunch);
    }

    [Export("application:willFinishLaunchingWithOptions:")]
    public bool WillFinishLaunchingWithOptions(UIApplication application, NSDictionary launchOptions)
    {
        BGTaskScheduler.Shared.Register("com.chosenfewsoftware.SubverseIM.bootstrap", null, HandleAppRefresh);
        
        wrappedPeerService = new(application);
        serviceManager.GetOrRegister<IPeerService>(
            (PeerService)wrappedPeerService
            );

        return true;
    }

    private void ScheduleAppRefresh()
    {
        BGAppRefreshTaskRequest request = new BGAppRefreshTaskRequest("com.chosenfewsoftware.SubverseIM.bootstrap");
        request.EarliestBeginDate = NSDate.Now.AddSeconds(60.0);
        BGTaskScheduler.Shared.Submit(request, out NSError? _);
    }

    public async void HandleAppRefresh(BGTask task) 
    {
        ScheduleAppRefresh();

        using CancellationTokenSource cts = new();

        BGAppRefreshTask refreshTask = (BGAppRefreshTask)task;
        refreshTask.ExpirationHandler += cts.Cancel;

        IFrontendService frontendService = await serviceManager.GetWithAwaitAsync<IFrontendService>();
        await frontendService.RunAsync(cts.Token);
    }

    public async void HandleApplicationDidLaunch(object? sender, UIApplicationLaunchEventArgs e)
    {
        ScheduleAppRefresh();
        
        IFrontendService frontendService = await serviceManager.GetWithAwaitAsync<IFrontendService>();
        await frontendService.RunAsync();
    }

    protected override AppBuilder CreateAppBuilder()
    {
        return AppBuilder.Configure(() => new App(serviceManager)).UseiOS();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI();
    }
}
