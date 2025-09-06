using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.DTOs;
using Wabbajack.Networking.WabbajackClientApi;

namespace Wabbajack.CLI.Verbs;

public class GetModlistUrl
{
    private readonly ILogger<GetModlistUrl> _logger;
    private readonly Client _client;

    public GetModlistUrl(ILogger<GetModlistUrl> logger, Client wjClient)
    {
        _logger = logger;
        _client = wjClient;
    }

    public static VerbDefinition Definition =
        new("get-modlist-url", "Get the machineURL for a modlist by name", new[]
        {
            new OptionDefinition(typeof(string), "n", "name", "Modlist name to search for (required)")
        });

    public async Task<int> Run(string name, CancellationToken token)
    {
        if (string.IsNullOrEmpty(name))
        {
            _logger.LogError("Modlist name is required. Use --name or -n to specify the modlist name.");
            return 1;
        }

        _logger.LogInformation("Loading all modlist definitions");
        var modlists = await _client.LoadLists();
        _logger.LogInformation("Loaded {Count} lists", modlists.Length);

        // Search for modlist by title, namespaced name, or author
        var matchingModlist = modlists.FirstOrDefault(m => 
            (m.Title?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (m.NamespacedName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (m.Author?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false));

        if (matchingModlist == null)
        {
            _logger.LogError("No modlist found matching '{Name}'", name);
            _logger.LogInformation("Try searching with a partial name or check available modlists with 'list-modlists'");
            return 1;
        }

        // Output the namespaced name (machineURL)
        _logger.LogInformation("{MachineURL}", matchingModlist.NamespacedName);
        return 0;
    }
}
