using System;
using System.CommandLine;
using System.CommandLine.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
using NLog.Targets;
using Octokit;
using Wabbajack.DTOs.Interventions;
using Wabbajack.Networking.Http;
using Wabbajack.Networking.Http.Interfaces;
using Wabbajack.Paths.IO;
using Wabbajack.Server.Lib;
using Wabbajack.Services.OSIntegrated;
using Wabbajack.VFS;
using Client = Wabbajack.Networking.GitHub.Client;
using Wabbajack.CLI.Builder;
using CG.Web.MegaApiClient;

namespace Wabbajack.CLI;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Check for debug mode
        bool debugMode = Array.IndexOf(args, "--debug") >= 0;
        
        var host = Host.CreateDefaultBuilder(Array.Empty<string>())
            .ConfigureLogging(builder => AddLogging(builder, debugMode))
            .ConfigureServices((host, services) =>
            {
                services.AddSingleton(new JsonSerializerOptions());
                services.AddSingleton<HttpClient, HttpClient>();
                services.AddResumableHttpDownloader();
                services.AddSingleton<IConsole, SystemConsole>();
                services.AddSingleton<CommandLineBuilder, CommandLineBuilder>();
                services.AddSingleton<TemporaryFileManager>();
                services.AddSingleton<FileExtractor.FileExtractor>();
                services.AddSingleton(new ParallelOptions {MaxDegreeOfParallelism = Environment.ProcessorCount});
                services.AddSingleton<Client>();
                services.AddSingleton<Networking.WabbajackClientApi.Client>();
                services.AddSingleton(s => new GitHubClient(new ProductHeaderValue("wabbajack")));
                services.AddSingleton<TemporaryFileManager>();
                services.AddSingleton<MegaApiClient>();
                services.AddSingleton<IUserInterventionHandler, CLIUserInterventionHandler>();

                services.AddOSIntegrated();
                services.AddServerLib();


                services.AddTransient<Context>();
                
                services.AddSingleton<CommandLineBuilder>();
                services.AddCLIVerbs();
            }).Build();

        var service = host.Services.GetService<CommandLineBuilder>();
        return await service!.Run(args);
    }
    
    private static void AddLogging(ILoggingBuilder loggingBuilder, bool debugMode = false)
    {
        var config = new NLog.Config.LoggingConfiguration();

        var fileTarget = new FileTarget("file")
        {
            FileName = "logs/wabbajack-cli.current.log",
            ArchiveFileName = "logs/wabbajack-cli.{##}.log",
            ArchiveOldFileOnStartup = true,
            MaxArchiveFiles = 10,
            Layout = "${processtime} [${level:uppercase=true}] (${logger}) ${message:withexception=true}",
            Header = "############ Wabbajack log file - ${longdate} ############"
        };

        var consoleTarget = new ConsoleTarget("console")
        {
            Layout = "${message:withexception=true}",
        };
        
        config.AddRuleForAllLevels(fileTarget);
        
        if (debugMode)
        {
            // In debug mode, show all log levels on console
            config.AddRuleForAllLevels(consoleTarget);
        }
        else
        {
            // In non-debug mode, show info, warnings and errors on console
            config.AddRule(NLog.LogLevel.Info, NLog.LogLevel.Fatal, consoleTarget);
        }

        loggingBuilder.ClearProviders();
        loggingBuilder.SetMinimumLevel(LogLevel.Trace);
        loggingBuilder.AddNLog(config);
    }
}