using Npgsql;
using ss.Internal.Management.Server.AutoRef;

namespace ss.Internal.Management.Server
{
	public static class Program
	{
		public const string TournamentName = "SS26";  
		
		public static async Task Main(string[] args)
		{
			Console.WriteLine("hola server de jd");
			
			var matchType = Models.MatchType.EliminationStage;
			
			var autoRef = new AutoRef.AutoRef("32", "Furina", matchType);
			
			await Task.Run(async () => 
			{
				try 
				{
					await autoRef.StartAsync();
				}
				catch (Exception ex) 
				{
					Console.WriteLine($"Error en AutoRef: {ex.Message}");
				}
			});
			
			Console.WriteLine("adios server de jd");
		}
	}
};

