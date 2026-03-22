using BanchoSharp.Interfaces;
using Moq;
using ss.Integrated.Management.Server.Tests.MatchManager;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.MatchManager;

namespace ss.Integrated.Management.Server.Tests.MatchManager
{
    /// <summary>
    /// Builds a fully wired MatchManagerEliminationStage for use in tests,
    /// removing the boilerplate that was previously duplicated across every test.
    /// </summary>
    internal sealed class MatchManagerQualifiersStageHarness
    {
        public MatchManagerQualifiersStage Manager { get; }
        public Mock<IBanchoClient> BanchoClient { get; } = new();
        public string Channel { get; } = "#mp_1";
        public string RefName { get; }

        private static readonly string[] DefaultSlots =
            ["NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3"];

        public MatchManagerQualifiersStageHarness(
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

            Manager = new MatchManagerQualifiersStage(matchId, refName, (_, _, _) =>
            {
            })
            {
                client = BanchoClient.Object,
                joined = true,
                lobbyChannelName = Channel,
                currentState = IMatchManager.MatchState.Idle,
                currentMatch = new Models.QualifierRoom
                {
                    Id = matchId,
                    Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
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
        
        }
    }

    public class MatchManagerQualifiersStageTests
    {
        [Fact]
        public async Task PanicProtocol_ShouldPauseAndResumeAutomation()
        {
            var h = new MatchManagerQualifiersStageHarness();

            await h.Send("RandomPlayer", "!panic la tengo enana");
            Assert.Equal(IMatchManager.MatchState.MatchOnHold, h.Manager.currentState);
            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp aborttimer"), Times.Once);

            await h.Ref(">panic_over");
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);
            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp timer 10"), Times.Once);
        }

        [Fact]
        public async Task AdminCommands_StartAndStop()
        {
            var h = new MatchManagerQualifiersStageHarness();

            await h.Ref(">start");
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);

            await h.Ref(">stop");
            Assert.Equal(IMatchManager.MatchState.Idle, h.Manager.currentState);
        }

        [Fact]
        public async Task PanicProtocol_NonReferee_CannotResolvePanic()
        {
            var h = new MatchManagerQualifiersStageHarness();
            h.Manager.currentState = IMatchManager.MatchState.MatchOnHold;

            await h.Send("SomeRando", ">panic_over");
            
            Assert.Equal(IMatchManager.MatchState.MatchOnHold, h.Manager.currentState);
        }

        [Fact]
        public async Task AdminCommands_SetMap_ShouldSetMap()
        {
            var h = new MatchManagerQualifiersStageHarness();
            h.Manager.currentState = IMatchManager.MatchState.Idle;

            await h.Ref(">setmap NM1");
            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp map 1000"), Times.Once);
            h.BanchoClient.Verify(c => c.SendPrivateMessageAsync(h.Channel, "!mp mods NM NF"), Times.Once);
        }

        [Fact]
        public async Task AdminCommands_Start_WhenAlreadyRunning_ShouldBeIgnored()
        {
            var h = new MatchManagerQualifiersStageHarness();
            h.Manager.currentState = IMatchManager.MatchState.WaitingForStart;

            await h.Ref(">start");

            Assert.Equal(IMatchManager.MatchState.WaitingForStart, h.Manager.currentState);
        }

        [Fact]
        public async Task AdminCommands_Stop_WhenAlreadyIdle_ShouldBeIgnored()
        {
            var h = new MatchManagerQualifiersStageHarness();

            await h.Ref(">stop");
            Assert.Equal(IMatchManager.MatchState.Idle, h.Manager.currentState);
            h.BanchoClient.Verify(
                c => c.SendPrivateMessageAsync(h.Channel, It.IsAny<string>()),
                Times.Once);
        }

        [Fact]
        public async Task AdminCommands_SetMap_WhenNotIdle_ShouldFail()
        {
            var h = new MatchManagerQualifiersStageHarness();
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
    }