using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentFTP.Helpers;
using Microsoft.Extensions.Logging;
using Wabbajack.CLI.Builder;
using Wabbajack.DTOs;
using Wabbajack.Networking.WabbajackClientApi;

namespace Wabbajack.CLI.Verbs;

public class ListModlists
{
    private readonly ILogger<ListCreationClubContent> _logger;
    private readonly Client _client;

    public ListModlists(ILogger<ListCreationClubContent> logger, Client wjClient)
    {
        _logger = logger;
        _client = wjClient;
    }

    public static VerbDefinition Definition =
        new("list-modlists", "Lists all known modlists", new[]
        {
            new OptionDefinition(typeof(string), "g", "game", "Filter by game name"),
            new OptionDefinition(typeof(string), "a", "author", "Filter by author name"),
            new OptionDefinition(typeof(string), "s", "search", "Search in title and description"),
            new OptionDefinition(typeof(string), "n", "name", "Find modlist by exact name match"),
            new OptionDefinition(typeof(bool), "show-author", "show-author", "Show author names in output"),
            new OptionDefinition(typeof(bool), "show-install-size", "show-install-size", "Show install size instead of download size"),
            new OptionDefinition(typeof(bool), "show-all-sizes", "show-all-sizes", "Show download|install|total sizes"),
            new OptionDefinition(typeof(bool), "show-machine-url", "show-machine-url", "Show machineURL for each modlist")
        });

    public async Task<int> Run(string? game, string? author, string? search, string? name, bool showAuthor, bool showInstallSize, bool showAllSizes, bool showMachineUrl, CancellationToken token)
    {
        _logger.LogInformation("Loading all modlist definitions");
        var modlists = await _client.LoadLists();
        _logger.LogInformation("Loaded {Count} lists", modlists.Length);

        // Apply filters
        var filteredModlists = modlists.AsEnumerable();
        
        if (!string.IsNullOrEmpty(game))
        {
            filteredModlists = filteredModlists.Where(m => 
            {
                var gameName = m.Game.MetaData().HumanFriendlyGameName;
                // Use exact matching only - no partial matches
                // e.g., "Oblivion" should not match "Oblivion Remastered"
                return string.Equals(gameName, game, StringComparison.OrdinalIgnoreCase);
            });
        }
        
        if (!string.IsNullOrEmpty(author))
        {
            filteredModlists = filteredModlists.Where(m => 
                m.Author.Contains(author, StringComparison.OrdinalIgnoreCase));
        }
        
        if (!string.IsNullOrEmpty(search))
        {
            filteredModlists = filteredModlists.Where(m => 
                (m.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (m.NamespacedName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        
        if (!string.IsNullOrEmpty(name))
        {
            filteredModlists = filteredModlists.Where(m => 
                string.Equals(m.Title, name, StringComparison.OrdinalIgnoreCase));
        }

        var finalModlists = filteredModlists.ToArray();
        _logger.LogInformation("Showing {Count} modlists after filtering", finalModlists.Length);

        foreach (var modlist in finalModlists.OrderBy(l => l.Title ?? l.NamespacedName))
        {
            var displayTitle = string.IsNullOrEmpty(modlist.Title) ? modlist.NamespacedName : modlist.Title;
            
            // Add status indicators to title
            if (modlist.ForceDown)
            {
                displayTitle = $"[DOWN] {displayTitle}";
            }
            
            if (modlist.NSFW)
            {
                displayTitle = $"[NSFW] {displayTitle}";
            }
            
            string sizeDisplay;
            if (showAllSizes)
            {
                var downloadSize = modlist.DownloadMetadata!.SizeOfArchives.FileSizeToString();
                var installSize = modlist.DownloadMetadata!.SizeOfInstalledFiles.FileSizeToString();
                var totalSize = modlist.DownloadMetadata!.TotalSize.FileSizeToString();
                sizeDisplay = $"{downloadSize}|{installSize}|{totalSize}";
            }
            else
            {
                var sizeToShow = showInstallSize ? modlist.DownloadMetadata!.SizeOfInstalledFiles : modlist.DownloadMetadata!.SizeOfArchives;
                sizeDisplay = sizeToShow.FileSizeToString();
            }
            
            if (showMachineUrl)
            {
                if (showAuthor)
                {
                    var displayAuthor = string.IsNullOrEmpty(modlist.Author) ? "Unknown" : modlist.Author;
                    _logger.LogInformation("{Title} ({Author}) - {Game} - {Size} - {MachineURL}", 
                        displayTitle, 
                        displayAuthor, 
                        modlist.Game.MetaData().HumanFriendlyGameName, 
                        sizeDisplay,
                        modlist.NamespacedName);
                }
                else
                {
                    _logger.LogInformation("{Title} - {Game} - {Size} - {MachineURL}", 
                        displayTitle, 
                        modlist.Game.MetaData().HumanFriendlyGameName, 
                        sizeDisplay,
                        modlist.NamespacedName);
                }
            }
            else
            {
                if (showAuthor)
                {
                    var displayAuthor = string.IsNullOrEmpty(modlist.Author) ? "Unknown" : modlist.Author;
                    _logger.LogInformation("{Title} ({Author}) - {Game} - {Size}", 
                        displayTitle, 
                        displayAuthor, 
                        modlist.Game.MetaData().HumanFriendlyGameName, 
                        sizeDisplay);
                }
                else
                {
                    _logger.LogInformation("{Title} - {Game} - {Size}", 
                        displayTitle, 
                        modlist.Game.MetaData().HumanFriendlyGameName, 
                        sizeDisplay);
                }
            }
        }
        
        return 0;
    }
}