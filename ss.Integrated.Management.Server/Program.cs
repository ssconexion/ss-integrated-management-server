using System.Globalization;
using DotNetEnv;
using ss.Internal.Management.Server.Discord;
using ss.Internal.Management.Server.Localization;

namespace ss.Internal.Management.Server
{
	public static class Program
	{
		public const string TournamentName = "SS26";
		
		public static async Task Main(string[] args)
		{
			Console.WriteLine("hola server de jd");

			Env.Load();

			I18n.CurrentCulture = new CultureInfo(Environment.GetEnvironmentVariable("LANGUAGE") ?? "en");
			
			var manager = new DiscordManager(Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? throw new InvalidOperationException());
			await manager.StartAsync();
			
			await Task.Delay(-1);
			Console.WriteLine("adios server de jd");
		}
	}
};

