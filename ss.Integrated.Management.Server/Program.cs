using System.Globalization;
using DotNetEnv;
using Microsoft.Extensions.DependencyInjection;
using ss.Internal.Management.Server.Discord;

namespace ss.Internal.Management.Server
{
	public static class Program
	{
		#if DEBUG
		public const string TournamentName = "SS26_Test";
		#else
		public const string TournamentName = "SS26";
		#endif

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

