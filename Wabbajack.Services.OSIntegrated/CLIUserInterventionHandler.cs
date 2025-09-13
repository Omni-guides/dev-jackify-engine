using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Wabbajack.DTOs.Interventions;

namespace Wabbajack.Services.OSIntegrated;

public class CLIUserInterventionHandler : IUserInterventionHandler
{
    private readonly ILogger<CLIUserInterventionHandler> _logger;
    private readonly List<ManualDownload> _manualDownloads = new();

    public CLIUserInterventionHandler(ILogger<CLIUserInterventionHandler> logger)
    {
        _logger = logger;
    }

    public void Raise(IUserIntervention intervention)
    {
        switch (intervention)
        {
            case ManualDownload manualDownload:
                _logger.LogInformation("Manual download required: {FileName} - {Url}", 
                    manualDownload.Archive.Name, 
                    manualDownload.Archive.State is DTOs.DownloadStates.Manual manualState ? manualState.Url?.ToString() : "Unknown URL");
                _manualDownloads.Add(manualDownload);
                break;
            default:
                _logger.LogWarning("Unhandled user intervention: {InterventionType}", intervention.GetType().Name);
                break;
        }
    }

    public List<ManualDownload> GetManualDownloads()
    {
        return new List<ManualDownload>(_manualDownloads);
    }
    
    public bool HasManualDownloads()
    {
        return _manualDownloads.Count > 0;
    }
}
