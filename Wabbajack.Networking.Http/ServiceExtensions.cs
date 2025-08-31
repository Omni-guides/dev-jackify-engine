using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using System;
using System.Net.Http;
using Wabbajack.Networking.Http.Interfaces;

namespace Wabbajack.Networking.Http;

public static class ServiceExtensions
{
    public static void AddResumableHttpDownloader(this IServiceCollection services)
    {
        services.AddHttpClient("ResumableClient")
            .ConfigureHttpClient(c => 
            {
                c.Timeout = TimeSpan.FromHours(1);
                c.DefaultRequestHeaders.ConnectionClose = false; // Keep connections alive
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler()
            {
                MaxConnectionsPerServer = 16, // Increase connection pool
                UseCookies = false // Reduce overhead
            });
        services.AddSingleton<IHttpDownloader, ResumableDownloader>();
        services.RemoveAll<IHttpMessageHandlerBuilderFilter>();
    }
}