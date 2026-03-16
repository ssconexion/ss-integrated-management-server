using Discord;
using Discord.Interactions;
using ss.Internal.Management.Server.AutoRef;

namespace ss.Internal.Management.Server.Discord.Modules;

public class BracketModule : InteractionModuleBase<SocketInteractionContext>
{
    private enum TournamentStage
    {
        RoundOf32,
        RoundOf16,
        Quarterfinals,
        Semifinals,
        Finals,
        Grandfinals,
        BracketReset,
    }
    
    [RequireFromEnvId("DISCORD_ADMIN_ROLE_ID")]
    private async Task GenerateBracketMatches(TournamentStage stage, int stageRoundId)
    {
        await DeferAsync(ephemeral: false);
        await using var db = new ModelsContext();
        var matches = new List<Models.MatchRoom>();
        
        if (stage == TournamentStage.RoundOf32)
        {
            // Winners RO32 - Rooms 1 to 16
            for (int i = 1; i <= 16; i++)
            {
                
                int winnerTo = 24 + (int)Math.Ceiling(i / 2.0);
                int loserTo = 16 + (int)Math.Ceiling(i / 2.0);

                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = winnerTo.ToString(),
                    LoserToMatchId = loserTo.ToString(),
                });
            }
        } 
        else if (stage == TournamentStage.RoundOf16)
        {
            // Losers RO16 - Rooms 17 to 24
            for (int i = 17; i <= 24; i++)
            {
                int winnerTo = 57 - i;

                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = winnerTo.ToString(),
                    LoserToMatchId = null,
                });
            }
            
            // Winners RO16 - Rooms 25 to 32
            for (int i = 25; i <= 32; i++)
            {
                
                int winnerTo = 32 + (int)Math.Ceiling(i / 2.0);
                int loserTo = i + 8;

                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = winnerTo.ToString(),
                    LoserToMatchId = loserTo.ToString(),
                });
            }
        } 
        else if (stage == TournamentStage.Quarterfinals)
        {
            // Losers QF - Rooms 33 to 40
            for (int i = 33; i <= 40; i++)
            {
                int winnerTo = 40 + (int)Math.Ceiling((i - 32) / 2.0);

                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = winnerTo.ToString(),
                    LoserToMatchId = null,
                });
            }
            
            // Losers QF Conditionals - Rooms 41 to 44
            for (int i = 41; i <= 44; i++)
            {
                // 41 -> 51 | 42 -> 52 | 43 -> 49 | 44 -> 50
                int winnerTo = (i == 41 || i == 42) ? i + 10 : i + 6;

                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = winnerTo.ToString(),
                    LoserToMatchId = null,
                });
            }
            
            // Winners QF - Rooms 45 to 48
            for (int i = 45; i <= 48; i++)
            {
                int winnerTo = 54 + (int)Math.Ceiling((i - 44) / 2.0);
                int loserTo = i + 4; 

                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = winnerTo.ToString(),
                    LoserToMatchId = loserTo.ToString(),
                });
            }
        }
        else if (stage == TournamentStage.Semifinals)
        {
            // Losers SF - Rooms 49 to 52
            for (int i = 49; i <= 52; i++)
            {
                int winnerTo = 52 + (int)Math.Ceiling((i - 48) / 2.0);

                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = winnerTo.ToString(),
                    LoserToMatchId = null,
                });
            }
    
            // Losers SF Conditionals - Rooms 53 and 54
            for (int i = 53; i <= 54; i++)
            {
                // magic number that leads to 53 -> 57 and 54 -> 58
                int winnerTo = 111 - i;

                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = winnerTo.ToString(),
                    LoserToMatchId = null,
                });
            }
    
            // Winners SF - Rooms 55 and 56
            for (int i = 55; i <= 56; i++)
            {
                int loserTo = i + 2; 

                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = "60",
                    LoserToMatchId = loserTo.ToString(),
                });
            }
        } 
        else if (stage == TournamentStage.Finals)
        {
            // Losers F - Rooms 57 and 58
            for (int i = 57; i <= 58; i++)
            {
                matches.Add(new Models.MatchRoom
                {
                    Id = i.ToString(),
                    RoundId = stageRoundId,
                    TeamRedId = null,
                    TeamBlueId = null,
                    WinnerToMatchId = "59",
                    LoserToMatchId = null,
                });
            }
            
            // Losers F Conditional - Rooms 59
            matches.Add(new Models.MatchRoom
            {
                Id = "59",
                RoundId = stageRoundId,
                TeamRedId = null,
                TeamBlueId = null,
                WinnerToMatchId = "61",
                LoserToMatchId = null,
            });
            
            // Winners F - Room 60
            matches.Add(new Models.MatchRoom
            {
                Id = "60",
                RoundId = stageRoundId,
                TeamRedId = null,
                TeamBlueId = null,
                WinnerToMatchId = "62",
                LoserToMatchId = "61",
            });
        }
        else if (stage == TournamentStage.Grandfinals)
        {
            // Losers GF
            matches.Add(new Models.MatchRoom
            {
                Id = "61",
                RoundId = stageRoundId,
                TeamRedId = null,
                TeamBlueId = null,
                WinnerToMatchId = "62",
                LoserToMatchId = null,
            });
            
            // Winners GF
            matches.Add(new Models.MatchRoom
            {
                Id = "62",
                RoundId = stageRoundId,
                TeamRedId = null,
                TeamBlueId = null,
                WinnerToMatchId = null,
                LoserToMatchId = null,
            });
        } 
        else if (stage == TournamentStage.BracketReset)
        {
            // GF Bracket Reset
            matches.Add(new Models.MatchRoom
            {
                Id = "63",
                RoundId = stageRoundId,
                TeamRedId = null,
                TeamBlueId = null,
                WinnerToMatchId = null,
                LoserToMatchId = null,
            });
        }
        
        await db.MatchRooms.AddRangeAsync(matches);
        await db.SaveChangesAsync();
        
        var embed = new EmbedBuilder()
            .WithTitle($"✅ Generada la ronda {stage}")
            .WithColor(Color.Green)
            .WithDescription($"Insertados **{matches.Count}** partidos en dicha ronda.");

        await FollowupAsync(embed: embed.Build());
    }
}