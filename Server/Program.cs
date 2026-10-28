using Server.Handler;
using Server.Manager;
using Server.Middleware;
using SharedLibrary.Communication.Connection;
using System.Net.WebSockets;

namespace Server
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
            var webServerUrl = builder.Configuration["WebServer:Url"] ?? "https://localhost:5000";
            Console.WriteLine($"Web server url: {webServerUrl}");
            builder.WebHost.UseUrls(webServerUrl);

            var app = builder.Build();


            app.UseHttpsRedirection();
            app.UseWebSockets(); // **TODO** Remove UseWebSockets and replace with RUDP implementation for better performance.

            /* Configure custom middlewares */
            // Web socket middleware
            app.UseMiddleware<ConnectionMiddleware>();


            /* Handle server shutdown */
            app.Lifetime.ApplicationStopping.Register(async () =>
            {
                // Shutdown all open WebSocket connections via the GameConnectionManager
                using var scope = app.Services.CreateScope();
                var gameConnectionManager = scope.ServiceProvider.GetRequiredService<GameConnectionManager>();

                foreach (var webSocket in gameConnectionManager.GetAllSockets())
                {
                    if (webSocket.State == WebSocketState.Open)
                    {
                        await ConnectionMiddleware.CloseWebSocket(webSocket, WebSocketCloseStatus.NormalClosure, ConnectionMessages.ServerShutdownMessage);
                    }
                }
            });



            var host = app.RunAsync(); // Run the application asynchronously

            // Wait for application to shutdown
            await host;
        }
    }


}