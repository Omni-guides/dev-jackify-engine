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

        var matches = modlists.Where(m =>
            (m.Title?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (m.NamespacedName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        if (matches.Count == 0)
        {
            _logger.LogError("No modlist found matching '{Name}'", name);
            _logger.LogInformation("Try searching with a partial name or check available modlists with 'list-modlists'");
            return 1;
        }

        // Prefer exact title match to resolve ambiguity (e.g. "Morrowind Remastered" vs "Morrowind Remastered Legacy Edition")
        var exactMatches = matches.Where(m =>
            string.Equals(m.Title, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.NamespacedName, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactMatches.Count == 1)
            matches = exactMatches;

        if (matches.Count > 1)
        {
            _logger.LogError("Multiple modlists match '{Name}'. Use a more specific name:", name);
            foreach (var m in matches)
                _logger.LogInformation("  {Title} ({MachineURL})", m.Title, m.NamespacedName);
            return 1;
        }

        Console.WriteLine(matches[0].NamespacedName);
        _logger.LogInformation("{MachineURL}", matches[0].NamespacedName);
        return 0;
    }
}
