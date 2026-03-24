using System.Globalization;
using DotNetEnv;
using Microsoft.Extensions.DependencyInjection;
using ss.Internal.Management.Server.Discord;

namespace ss.Internal.Management.Server
{
    /// <summary>
    /// The main entrypoint of the management server
    /// </summary>
    public static class Program
    {
#if DEBUG
        public const string TournamentName = "SS26_Test";
#else
		public const string TournamentName = "SS26";
#endif

        /// <summary>
        /// Initialization of the environment variables and the discord bot
        /// </summary>
        public static async Task Main(string[] args)
        {
            Console.WriteLine("hola server de jd");

            Env.Load();

            CultureInfo.CurrentUICulture = new CultureInfo(Environment.GetEnvironmentVariable("LANGUAGE") ?? "en");

            var services = new ServiceCollection();
            services.AddSingleton(provider => new DiscordManager(Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? string.Empty));
            var serviceProvider = services.BuildServiceProvider();

            var manager = serviceProvider.GetRequiredService<DiscordManager>();
            await manager.StartAsync();

            await Task.Delay(-1);
            Console.WriteLine("adios server de jd");
        }
    }
};