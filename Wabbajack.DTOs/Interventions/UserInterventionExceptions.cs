using System;

namespace Wabbajack.DTOs.Interventions;

/// <summary>
/// Exception thrown when a manual download is required
/// </summary>
public class ManualDownloadRequiredException : Exception
{
    public ManualDownloadRequiredException(string message) : base(message)
    {
    }
    
    public ManualDownloadRequiredException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when any user intervention is required
/// </summary>
public class UserInterventionRequiredException : Exception
{
    public UserInterventionRequiredException(string message) : base(message)
    {
    }
    
    public UserInterventionRequiredException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
