using BanchoSharp.Interfaces;
using Moq;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.MatchManager;

namespace ss.Integrated.Management.Server.Tests.MatchManager
{
    /// <summary>
    /// Builds a fully wired MatchManagerEliminationStage for use in tests,
    /// removing the boilerplate that was previously duplicated across every test.
    /// </summary>
    internal sealed class MatchManagerEliminationStageHarness
    {
        public MatchManagerEliminationStage Manager { get; }
        public Mock<IBanchoClient> BanchoClient { get; } = new();
        public string Channel { get; } = "#mp_1";
        public string RefName { get; }

        private static readonly string[] DefaultSlots =
            ["NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1"];

        public MatchManagerEliminationStageHarness(
            string refName = "Furina",
            string matchId = "96",
            int bestOf = 9,
            int banRounds = 1,
            string[]? slots = null)
        {
            RefName = refName;
            var mappool = (slots ?? DefaultSlots)
                .Select((slot, i) => new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slot })
                .ToList();

            Manager = new MatchManagerEliminationStage(matchId, refName, (_, _, _) => { })
            {
                client = BanchoClient.Object,
                joined = true,
                lobbyChannelName = Channel,
                currentState = IMatchManager.MatchState.Idle,
                bannedMaps = [],
                pickedMaps = [],
                currentMatch = new Models.MatchRoom
                {
                    Id = matchId,
                    TeamRedId = 1,
                    TeamBlueId = 2,
                    Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                    TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 }, Id = 1 },
                    TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 }, Id = 2 },
                    Round = new Models.Round { BestOf = bestOf, BanRounds = banRounds, MapPool = mappool }
                }
            };
        }

        /// <summary>
        /// Sends an IRC message as <paramref name="sender"/>.
        /// </summary>
        public async Task Send(string sender, string content)
        {
            var msg = new Mock<IIrcMessage>();
            msg.Setup(m => m.Prefix).Returns(sender);
            msg.Setup(m => m.Parameters).Returns(new[] { Channel, content });
            await Manager.HandleIrcMessage(msg.Object);
        }

        /// <summary>
        /// Shorthand for sending a referee command.
        /// </summary>
        public Task Ref(string command) => Send(RefName, command);

        /// <summary>
        /// Simulates a complete map: pick → ready → play → result.
        /// </summary>
        public async Task PlayMap(string picker, string map, string winner)
        {
            await Send(picker, map);
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, Manager.currentState);

            await Send("BanchoBot", "All players are ready");
            Assert.Equal(IMatchManager.MatchState.Playing, Manager.currentState);

            string loser = winner == "RedTeam" ? "BlueTeam" : "RedTeam";
            await Send("BanchoBot", $"{winner} finished playing (Score: 1000000, PASSED)");
            await Send("BanchoBot", $"{loser} finished playing (Score: 500000, PASSED)");
            await Send("BanchoBot", "The match has finished!");
        }

        /// <summary>
        /// Runs the standard opening sequence: firstpick red, firstban blue, start auto,
        /// then both teams ban one map each. Leaves state at WaitingForPickRed.
        /// </summary>
        public async Task RunStandardBanPhase(string mode = "auto")
        {
            await Ref(">firstpick red");
            await Ref(">firstban blue");
            await Ref($">start {mode}");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, Manager.currentState);
            await Send("BlueTeam", "NM1");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, Manager.currentState);
            await Send("RedTeam", "HD1");

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, Manager.currentState);
        }
    }

    public class MatchManagerEliminationStageTests
    {
        [Fact]
        public async Task PanicProtocol_ShouldPauseAndResumeAutomation()
        {
            var h = new MatchManagerEliminationStageHarness();

            await h.Send("RandomPlayer", "!panic la tengo enana");
            Assert.Equal(IMatchManager.MatchState.MatchOnHold, h.Manager.currentState);
            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp aborttimer"), Times.Once);

            await h.Ref(">panic_over");
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);
            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp timer 10"), Times.Once);
        }

        [Fact]
        public async Task PanicProtocol_NonReferee_CannotResolvePanic()
        {
            var h = new MatchManagerEliminationStageHarness();
            h.Manager.currentState = IMatchManager.MatchState.MatchOnHold;

            await h.Send("SomeRando", ">panic_over");
            
            Assert.Equal(IMatchManager.MatchState.MatchOnHold, h.Manager.currentState);
        }

        [Fact]
        public async Task AdminCommands_StartAndStop()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.Ref(">firstpick red");
            await h.Ref(">firstban blue");

            await h.Ref(">start auto");
            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);

            await h.Ref(">stop");
            Assert.Equal(IMatchManager.MatchState.Idle, h.Manager.currentState);
        }

        [Fact]
        public async Task AdminCommands_StopAndRestart_ShouldResumeFromPreviousState()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();
            
            await h.Send("RedTeam", "NM2");
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);

            await h.Ref(">stop");
            Assert.Equal(IMatchManager.MatchState.Idle, h.Manager.currentState);
            
            await h.Ref(">start auto");
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);
        }

        [Fact]
        public async Task AdminCommands_FromUnauthorizedUser_ShouldBeIgnored()
        {
            var h = new MatchManagerEliminationStageHarness();
            h.Manager.currentState = IMatchManager.MatchState.WaitingForStart;

            await h.Send("Fieera", ">stop");

            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);
        }

        [Fact]
        public async Task AdminCommands_SetMap_ShouldSetMap()
        {
            var h = new MatchManagerEliminationStageHarness();
            h.Manager.currentState = IMatchManager.MatchState.Idle;

            await h.Ref(">setmap NM1");
            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp map 1000"), Times.Once);
            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp mods NM NF"), Times.Once);
        }

        [Fact]
        public async Task AdminCommands_SetMap_WhenNotIdle_ShouldFail()
        {
            var h = new MatchManagerEliminationStageHarness();
            h.Manager.currentState = IMatchManager.MatchState.Playing;

            await h.Ref(">setmap NM1");

            h.BanchoClient.Verify(
                c => c.SendPrivateMessageAsync(h.Channel, It.Is<string>(s => s.StartsWith("!mp map"))),
                Times.Never);
        }

        [Fact]
        public async Task AdminCommands_SetMap_WithInvalidSlot_ShouldNotCrash()
        {
            var h = new MatchManagerEliminationStageHarness();
            h.Manager.currentState = IMatchManager.MatchState.Idle;

            var ex = await Record.ExceptionAsync(() => h.Ref(">setmap turradisima99"));
            Assert.Null(ex);
            h.BanchoClient.Verify(
                c => c.SendPrivateMessageAsync(h.Channel, It.Is<string>(s => s.StartsWith("!mp map"))),
                Times.Never);
        }

        [Fact]
        public async Task AdminCommands_Start_WhenAlreadyRunning_ShouldBeIgnored()
        {
            var h = new MatchManagerEliminationStageHarness();
            h.Manager.currentState = IMatchManager.MatchState.WaitingForStart;

            await h.Ref(">start auto");

            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);
        }

        [Fact]
        public async Task AdminCommands_Stop_WhenAlreadyIdle_ShouldBeIgnored()
        {
            var h = new MatchManagerEliminationStageHarness();

            await h.Ref(">stop");
            Assert.Equal(IMatchManager.MatchState.Idle, h.Manager.currentState);
            h.BanchoClient.Verify(
                c => c.SendPrivateMessageAsync(h.Channel, It.IsAny<string>()),
                Times.Once);
        }

        [Fact]
        public async Task AdminCommands_Invite_ShouldInviteBothTeams()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.Ref(">invite");

            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp invite #1"), Times.Once);
            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp invite #2"), Times.Once);
        }

        [Fact]
        public async Task AdminCommands_SetScore_ShouldUpdateScore()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();

            await h.Ref(">setscore 3 2");

            Assert.Equal(3, h.Manager.MatchScore[0]);
            Assert.Equal(2, h.Manager.MatchScore[1]);
        }

        [Fact]
        public async Task UndoCommand_ShouldRevertLastBanAndState()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.Ref(">firstpick red");
            await h.Ref(">firstban red");
            await h.Ref(">start auto");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, h.Manager.currentState);

            await h.Send("RedTeam", "NM1");
            Assert.Single(h.Manager.bannedMaps);
            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);

            await h.Ref(">undo");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, h.Manager.currentState);
            Assert.Empty(h.Manager.bannedMaps);
        }

        [Fact]
        public async Task UndoCommand_WhenHistoryIsEmpty_ShouldNotCrash()
        {
            var h = new MatchManagerEliminationStageHarness();
            var ex = await Record.ExceptionAsync(() => h.Ref(">undo"));
            Assert.Null(ex);
            Assert.Equal(IMatchManager.MatchState.Idle, h.Manager.currentState);
        }

        [Fact]
        public async Task BanPhase_DuplicateBan_ShouldBeIgnored()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.Ref(">firstpick red");
            await h.Ref(">firstban blue");
            await h.Ref(">start auto");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);
            await h.Send("BlueTeam", "NM1");
            await h.Send("RedTeam", "NM1");
            
            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, h.Manager.currentState);
            Assert.Single(h.Manager.bannedMaps);
        }

        [Fact]
        public async Task BanPhase_TiebreakerCannotBeBanned()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.Ref(">firstpick red");
            await h.Ref(">firstban blue");
            await h.Ref(">start auto");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);
            await h.Send("BlueTeam", "TB1");
            
            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);
            Assert.Empty(h.Manager.bannedMaps);
        }

        [Fact]
        public async Task BanPhase_WrongTeamBanning_ShouldBeIgnored()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.Ref(">firstpick red");
            await h.Ref(">firstban blue");
            await h.Ref(">start auto");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);
            
            await h.Send("RedTeam", "NM2");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);
            Assert.Empty(h.Manager.bannedMaps);
        }

        [Fact]
        public async Task PickPhase_PickingAlreadyPickedMap_ShouldBeIgnored()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();
            
            await h.PlayMap("RedTeam", "NM2", "RedTeam");

            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, h.Manager.currentState);
            await h.Send("BlueTeam", "NM2");
            
            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, h.Manager.currentState);
            Assert.Single(h.Manager.pickedMaps);
        }

        [Fact]
        public async Task PickPhase_PickingBannedMap_ShouldBeIgnored()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();

            await h.Send("RedTeam", "NM1");

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);
            Assert.Empty(h.Manager.pickedMaps);
        }

        [Fact]
        public async Task PickPhase_TiebreakerCannotBeManuallyPicked()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();

            await h.Send("RedTeam", "TB1");
            
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);
            Assert.Empty(h.Manager.pickedMaps);
        }

        [Fact]
        public async Task PickPhase_WrongTeamPicking_ShouldBeIgnored()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);
            
            await h.Send("BlueTeam", "NM2");

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);
            Assert.Empty(h.Manager.pickedMaps);
        }

        [Fact]
        public async Task ScoreProcessing_MapWinner_ShouldBeRecordedOnRoundChoice()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();
            await h.PlayMap("RedTeam", "NM2", "RedTeam");

            var playedMap = h.Manager.pickedMaps.Find(m => m.Slot == "NM2");
            Assert.NotNull(playedMap);
            Assert.Equal(Models.TeamColor.TeamRed, playedMap.Winner);
        }

        [Fact]
        public async Task ScoreProcessing_BlueWin_ShouldIncrementBlueScore()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();
            await h.PlayMap("RedTeam", "NM2", "BlueTeam");

            Assert.Equal(0, h.Manager.MatchScore[0]);
            Assert.Equal(1, h.Manager.MatchScore[1]);
        }

        [Fact]
        public async Task Timeout_PlayerRequested_ShouldPauseAndResume()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();

            await h.Send("RedTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.OnTimeout, h.Manager.currentState);

            await h.Send("BanchoBot", "Countdown finished");
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);
        }

        [Fact]
        public async Task Timeout_SameTeam_CannotUseTwice()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();

            await h.Send("RedTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.OnTimeout, h.Manager.currentState);

            await h.Send("BanchoBot", "Countdown finished");
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);
            
            await h.Send("RedTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);
        }

        [Fact]
        public async Task Timeout_BothTeamsCanUseTheirOwnTimeout()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();

            await h.Send("RedTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.OnTimeout, h.Manager.currentState);
            await h.Send("BanchoBot", "Countdown finished");
            
            await h.Send("RedTeam", "NM2");
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);
            
            await h.Send("BlueTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.OnTimeout, h.Manager.currentState);
        }

        [Fact]
        public async Task Timeout_ShouldBeIgnoredInAssistedMode()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.Ref(">firstpick red");
            await h.Ref(">firstban red");
            await h.Ref(">start assisted");

            Assert.Equal(MatchManagerEliminationStage.OperationMode.Assisted, h.Manager.currentMode);

            await h.Send("RedTeam", "!timeout");
            Assert.NotEqual(IMatchManager.MatchState.OnTimeout, h.Manager.currentState);
        }

        [Fact]
        public async Task Timeout_StolenPick_MaintainsCorrectPickOrder()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();
            
            await h.PlayMap("RedTeam", "NM2", "RedTeam");

            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, h.Manager.currentState);
            await h.Send("BanchoBot", "Countdown finished");
            
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);

            await h.PlayMap("RedTeam", "NM3", "RedTeam");
            
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);
        }

        [Fact]
        public async Task AssistedMode_PlayerInputsShouldBeIgnored_RefOverrideAccepted()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.Ref(">firstpick red");
            await h.Ref(">firstban blue");
            await h.Ref(">start assisted");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);
            
            await h.Send("BlueTeam", "NM1");
            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);
            Assert.Empty(h.Manager.bannedMaps);
            
            await h.Ref(">next NM1");
            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, h.Manager.currentState);
            Assert.Contains(h.Manager.bannedMaps, m => m.Slot == "NM1");

            await h.Send("RedTeam", "HD1");
            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, h.Manager.currentState);

            await h.Ref(">next HD1");
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, h.Manager.currentState);
        }

        [Fact]
        public async Task AssistedMode_WinCommand_ShouldAdvanceFromPlayingState()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.RunStandardBanPhase();

            await h.PlayMap("RedTeam", "NM2", "RedTeam");

            await h.Ref(">win red");
            
            Assert.NotEqual(IMatchManager.MatchState.Playing, h.Manager.currentState);
            Assert.Equal(1, h.Manager.MatchScore[0]);
            Assert.Equal(0, h.Manager.MatchScore[1]);
        }

        [Fact]
        public async Task AssistedMode_SwitchingToAuto_ShouldEnablePlayerTimeouts()
        {
            var h = new MatchManagerEliminationStageHarness();
            await h.Ref(">firstpick red");
            await h.Ref(">firstban red");
            await h.Ref(">start assisted");

            await h.Send("RedTeam", "!timeout");
            Assert.NotEqual(IMatchManager.MatchState.OnTimeout, h.Manager.currentState);

            await h.Ref(">operation auto");
            Assert.Equal(MatchManagerEliminationStage.OperationMode.Automatic, h.Manager.currentMode);

            await h.Send("RedTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.OnTimeout, h.Manager.currentState);
        }
        
        [Fact]
        public async Task FullMatch_SingleBanFlow_ShouldProgressToFinish()
        {
            var h = new MatchManagerEliminationStageHarness(bestOf: 9, banRounds: 1);
            await h.RunStandardBanPhase();

            await h.PlayMap("RedTeam", "NM2", "RedTeam");   // 1 - 0
            await h.PlayMap("BlueTeam", "HR1", "BlueTeam"); // 1 - 1
            await h.PlayMap("RedTeam", "HD2", "RedTeam");   // 2 - 1
            await h.PlayMap("BlueTeam", "DT1", "RedTeam");  // 3 - 1
            await h.PlayMap("RedTeam", "DT2", "BlueTeam");  // 3 - 2
            await h.PlayMap("BlueTeam", "NM4", "RedTeam");  // 4 - 2
            await h.PlayMap("RedTeam", "HD3", "RedTeam");   // 5 - 2

            Assert.Equal(IMatchManager.MatchState.MatchFinished, h.Manager.currentState);
            Assert.Equal(5, h.Manager.MatchScore[0]);
            Assert.Equal(2, h.Manager.MatchScore[1]);
        }

        [Fact]
        public async Task FullMatch_DoubleBanFlow_ShouldProgressToFinish()
        {
            var h = new MatchManagerEliminationStageHarness(bestOf: 9, banRounds: 2);
            await h.Ref(">firstpick red");
            await h.Ref(">firstban blue");
            await h.Ref(">start auto");
            
            await h.Send("BlueTeam", "NM1");
            await h.Send("RedTeam", "HD1");
            
            await h.PlayMap("RedTeam", "NM2", "RedTeam");   // 1 - 0
            await h.PlayMap("BlueTeam", "HR1", "BlueTeam"); // 1 - 1
            await h.PlayMap("RedTeam", "HD2", "RedTeam");   // 2 - 1
            await h.PlayMap("BlueTeam", "DT1", "RedTeam");  // 3 - 1
            
            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, h.Manager.currentState);
            await h.Send("RedTeam", "NM3");
            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, h.Manager.currentState);
            await h.Send("BlueTeam", "HR2");
            
            await h.PlayMap("RedTeam", "DT2", "BlueTeam");  // 3 - 2
            await h.PlayMap("BlueTeam", "NM4", "RedTeam");  // 4 - 2
            await h.PlayMap("RedTeam", "HD3", "RedTeam");   // 5 - 2

            Assert.Equal(IMatchManager.MatchState.MatchFinished, h.Manager.currentState);
        }

        [Fact]
        public async Task FullMatch_TieBreakerFlow_ShouldProgressToFinish()
        {
            var h = new MatchManagerEliminationStageHarness(bestOf: 9, banRounds: 1);
            await h.RunStandardBanPhase();
            
            await h.PlayMap("RedTeam", "NM2", "RedTeam");   // 1 - 0
            await h.PlayMap("BlueTeam", "HR1", "BlueTeam"); // 1 - 1
            await h.PlayMap("RedTeam", "HD2", "RedTeam");   // 2 - 1
            await h.PlayMap("BlueTeam", "DT1", "RedTeam");  // 3 - 1
            await h.PlayMap("RedTeam", "DT2", "BlueTeam");  // 3 - 2
            await h.PlayMap("BlueTeam", "NM4", "RedTeam");  // 4 - 2
            await h.PlayMap("RedTeam", "NM3", "BlueTeam");  // 4 - 3
            await h.PlayMap("BlueTeam", "NM5", "BlueTeam"); // 4 - 4
            
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);
            h.BanchoClient.Verify(
                c => c.SendPrivateMessageAsync(h.Channel, "!mp map 1014"),
                Times.Once);

            await h.Send("BanchoBot", "All players are ready");
            await h.Send("BanchoBot", "BlueTeam finished playing (Score: 1000000, PASSED)");
            await h.Send("BanchoBot", "RedTeam finished playing (Score: 500000, PASSED)");
            await h.Send("BanchoBot", "The match has finished!");

            Assert.Equal(IMatchManager.MatchState.MatchFinished, h.Manager.currentState);
            Assert.Equal(4, h.Manager.MatchScore[0]);
            Assert.Equal(5, h.Manager.MatchScore[1]);
        }
    }
}