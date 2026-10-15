using Microsoft.AspNetCore.WebSockets;
using Server.Handler;
using Server.Manager;
using WebServer.Controller;
using WebServer.Manager;

namespace WebServer
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Register Handlers
            builder.Services.AddScoped<GameConnectionHandler>();

            // Register Managers/Coordinators
            builder.Services.AddSingleton<QueueManager>();
            builder.Services.AddSingleton<GameConnectionManager>();
            builder.Services.AddSingleton<GameCoordinator>();

            // Read the URL from appsettings.json
            var webServerUrl = builder.Configuration["WebServer:Url"] ?? "http://localhost:5000";
            Console.WriteLine($"Web server url: {webServerUrl}");
            builder.WebHost.UseUrls(webServerUrl);

            var app = builder.Build();


            app.UseHttpsRedirection();
            app.UseWebSockets(); // **TODO** Remove UseWebSockets and replace with RUDP implementation for better performance.

            /* Configure custom middlewares */
            // Web socket middleware
            app.UseMiddleware<WebSocketMiddleware>();


            /* Handle server shutdown */
            app.Lifetime.ApplicationStopping.Register(async () =>
            {
                // Shutdown all open Session RUDP connections
                using var scope = app.Services.CreateScope();
                var webSocketMiddleware = scope.ServiceProvider.GetRequiredService<WebSocketMiddleware>();
                // await webSocketMiddleware.HandleServerShutdown();
            });



            var host = app.RunAsync(); // Run the application asynchronously

            // Wait for application to shutdown
            await host;
        }
    }


}