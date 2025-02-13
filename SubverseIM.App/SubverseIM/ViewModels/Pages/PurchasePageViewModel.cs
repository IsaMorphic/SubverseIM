﻿using Plugin.InAppBilling;
using SubverseIM.Services;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace SubverseIM.ViewModels.Pages
{
    public class PurchasePageViewModel : PageViewModelBase<PurchasePageViewModel>
    {
        public override bool HasSidebar => false;

        public override string Title => "Products View";

        public ObservableCollection<InAppBillingProduct> ProductsList { get; }

        public PurchasePageViewModel(IServiceManager serviceManager) : base(serviceManager)
        {
            ProductsList = new();
        }

        public async Task InitializeAsync() 
        {
            IBillingService billingService = await ServiceManager.GetWithAwaitAsync<IBillingService>();
            foreach (InAppBillingProduct product in await billingService.GetAllProductsAsync()) 
            {
                ProductsList.Add(product);
            }
        }

        public async Task PurchaseCommand(string productId) 
        {
            IBillingService billingService = await ServiceManager.GetWithAwaitAsync<IBillingService>();
            ILauncherService launcherService = await ServiceManager.GetWithAwaitAsync<ILauncherService>();
            if (await billingService.PurchaseItemAsync(productId))
            {
                await launcherService.ShowAlertDialogAsync("Thank you!", "Your donation has been processed successfully. Much love!");
            }
            else 
            {
                await launcherService.ShowAlertDialogAsync("Oopsie!", "Your donation could not be validated. Please try again soon!");
            }
        }
    }
}
