using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using Wabbajack.Downloaders;
using Wabbajack.DTOs.JsonConverters;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.Services.OSIntegrated.TokenProviders;

public class MegaTokenProvider : EncryptedJsonTokenProvider<MegaToken>
{
    public MegaTokenProvider(ILogger<MegaTokenProvider> logger, DTOSerializer dtos) : base(logger, dtos, "mega-login")
    {
    }
    
    // Override the KeyPath to use Jackify's config directory instead of Wabbajack's
    protected override AbsolutePath KeyPath => GetJackifyConfigPath().Combine("encrypted", "mega-login");
    
    private static AbsolutePath GetJackifyConfigPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var home = System.Environment.GetEnvironmentVariable("HOME");
            return (home + "/.config/jackify").ToAbsolutePath();
        }
        else
        {
            return KnownFolders.WabbajackAppLocal.Combine("saved_settings");
        }
    }
}