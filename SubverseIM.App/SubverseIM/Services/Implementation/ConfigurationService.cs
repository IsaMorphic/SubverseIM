using System.Threading;
using System.Threading.Tasks;
using SubverseIM.Models;

namespace SubverseIM.Services.Implementation;

public class ConfigurationService : IConfigurationService
{
    private readonly IServiceManager serviceManager;

    private SubverseConfig? config;

    public ConfigurationService(IServiceManager serviceManager)
    {
        this.serviceManager = serviceManager;
    }

    public async Task<SubverseConfig> GetConfigAsync(CancellationToken cancellationToken)
    {
        IDbService dbService = await serviceManager.GetWithAwaitAsync<IDbService>();
        return config ??= dbService.GetConfig() ?? new SubverseConfig
        { BootstrapperUriList = [BootstrapperService.DEFAULT_BOOTSTRAPPER_ROOT] };
    }

    public async Task<bool> PersistConfigAsync(CancellationToken cancellationToken = default)
    {
        IDbService dbService = await serviceManager.GetWithAwaitAsync<IDbService>();

        SubverseConfig config = await GetConfigAsync(cancellationToken);
        dbService.UpdateConfig(config);

        return true;
    }
}
