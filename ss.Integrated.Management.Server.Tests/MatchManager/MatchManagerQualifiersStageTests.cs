using BanchoSharp.Interfaces;
using Moq;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.MatchManager;

namespace ss.Integrated.Management.Server.Tests.MatchManager
{
    public class MatchManagerQualifiersStageTests
    {
        [Fact]
        public async Task PanicProtocol_ShouldPauseAndResumeAutomation()
        {
            var refereeName = "Furina";
            var matchId = "C4";
            var channelName = "#mp_12345";

            var mockBanchoClient = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerQualifiersStage(matchId, refereeName, (id, msg) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.WaitingForStart;

            matchManager.currentMatch = new Models.QualifierRoom
            {
                Id = matchId,
                Referee = new Models.RefereeInfo { DisplayName = refereeName, IRC = "pass" }
            };

            var panicMsg = new Mock<IIrcMessage>();
            panicMsg.Setup(m => m.Prefix).Returns("RandomPlayer");
            panicMsg.Setup(m => m.Parameters).Returns(new[] { channelName, "!panic la tengo enana" });

            var panicOverMsg = new Mock<IIrcMessage>();
            panicOverMsg.Setup(m => m.Prefix).Returns(refereeName);
            panicOverMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">panic_over" });


            await matchManager.HandleIrcMessage(panicMsg.Object);

            var stateAfterPanic = matchManager.currentState;

            await matchManager.HandleIrcMessage(panicOverMsg.Object);


            Assert.Equal(IMatchManager.MatchState.MatchOnHold, stateAfterPanic);

            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, "!mp aborttimer"), Times.Once);

            Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);

            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, "!mp timer 10"), Times.Once);
        }

        [Fact]
        public async Task AdminCommands_StartAndStop()
        {
            var refereeName = "Furina";
            var matchId = "C4";
            var channelName = "#mp_12345";

            var mockBanchoClient = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerQualifiersStage(matchId, refereeName, (id, msg) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.Idle;

            matchManager.currentMatch = new Models.QualifierRoom
            {
                Id = matchId,
                Referee = new Models.RefereeInfo { DisplayName = refereeName, IRC = "pass" },
                Round = new Models.Round
                {
                    MapPool = new List<Models.RoundBeatmap>
                    {
                        new Models.RoundBeatmap { BeatmapID = 1453229, Slot = "NM1" }
                    }
                }
            };

            var startMsg = new Mock<IIrcMessage>();
            startMsg.Setup(m => m.Prefix).Returns(refereeName);
            startMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">start" });

            var stopMsg = new Mock<IIrcMessage>();
            stopMsg.Setup(m => m.Prefix).Returns(refereeName);
            stopMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">stop" });


            await matchManager.HandleIrcMessage(startMsg.Object);
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);

            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, "!mp map 1453229"), Times.Once);
            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, "!mp mods NM NF"), Times.Once);
            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, "!mp timer 120"), Times.Once);


            await matchManager.HandleIrcMessage(stopMsg.Object);
            Assert.Equal(IMatchManager.MatchState.Idle, matchManager.currentState);
        }

        [Fact]
        public async Task AdminCommands_FromUnauthorizedUser_ShouldBeIgnore()
        {
            var refereeName = "Furina";
            var channelName = "#mp_12345";

            var mockBanchoClient = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerQualifiersStage("Q1", refereeName, (id, msg) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.WaitingForStart;

            matchManager.currentMatch = new Models.QualifierRoom
            {
                Referee = new Models.RefereeInfo { DisplayName = refereeName }
            };

            var maliciousMsg = new Mock<IIrcMessage>();
            maliciousMsg.Setup(m => m.Prefix).Returns("Fieera");
            maliciousMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">stop" });


            await matchManager.HandleIrcMessage(maliciousMsg.Object);
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);
        }

        [Fact]
        public async Task AdminCommands_SetMap_ShouldSetMaps()
        {
            var refereeName = "Furina";
            var matchId = "C4";
            var channelName = "#mp_12345";

            var mockBanchoClient = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerQualifiersStage(matchId, refereeName, (id, msg) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.Idle;

            matchManager.currentMatch = new Models.QualifierRoom
            {
                Id = matchId,
                Referee = new Models.RefereeInfo { DisplayName = refereeName, IRC = "pass" },
                Round = new Models.Round
                {
                    MapPool = new List<Models.RoundBeatmap>
                    {
                        new Models.RoundBeatmap { BeatmapID = 1453229, Slot = "NM1" },
                        new Models.RoundBeatmap { BeatmapID = 3392548, Slot = "HD2" }
                    }
                }
            };

            var setMap1 = new Mock<IIrcMessage>();
            setMap1.Setup(m => m.Prefix).Returns(refereeName);
            setMap1.Setup(m => m.Parameters).Returns(new[] { channelName, ">setmap nm1" });

            var setMap2 = new Mock<IIrcMessage>();
            setMap2.Setup(m => m.Prefix).Returns(refereeName);
            setMap2.Setup(m => m.Parameters).Returns(new[] { channelName, ">setmap hd2" });


            await matchManager.HandleIrcMessage(setMap1.Object);
            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, "!mp map 1453229"), Times.Once);
            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, "!mp mods nm NF"), Times.Once);


            await matchManager.HandleIrcMessage(setMap2.Object);
            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, "!mp map 3392548"), Times.Once);
            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, "!mp mods hd NF"), Times.Once);
        }

        [Fact]
        public async Task EdgeCase_Start_WhenAlreadyRunning_ShouldBeIgnored()
        {
            var refereeName = "Furina";
            var channelName = "#mp_12345";
            var mockBanchoClient = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerQualifiersStage("C4", refereeName, (id, msg) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentMatch = new Models.QualifierRoom { Referee = new Models.RefereeInfo { DisplayName = refereeName } };
            
            matchManager.currentState = IMatchManager.MatchState.WaitingForStart;

            var startMsg = new Mock<IIrcMessage>();
            startMsg.Setup(m => m.Prefix).Returns(refereeName);
            startMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">start" });
            
            await matchManager.HandleIrcMessage(startMsg.Object);
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);
            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, It.Is<string>(s => s.StartsWith("!mp map"))), Times.Never);
        }

        [Fact]
        public async Task EdgeCase_Stop_WhenAlreadyIdle_ShouldBeIgnored()
        {
            var refereeName = "Furina";
            var channelName = "#mp_12345";
            var mockBanchoClient = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerQualifiersStage("C4", refereeName, (id, msg) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentMatch = new Models.QualifierRoom { Referee = new Models.RefereeInfo { DisplayName = refereeName } };
            
            matchManager.currentState = IMatchManager.MatchState.Idle;

            var stopMsg = new Mock<IIrcMessage>();
            stopMsg.Setup(m => m.Prefix).Returns(refereeName);
            stopMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">stop" });
            
            await matchManager.HandleIrcMessage(stopMsg.Object);
            Assert.Equal(IMatchManager.MatchState.Idle, matchManager.currentState);
            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task EdgeCase_SetMap_WhenNotIdle_ShouldFailAndNotChangeMap()
        {
            var refereeName = "Furina";
            var channelName = "#mp_12345";
            var mockBanchoClient = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerQualifiersStage("C4", refereeName, (id, msg) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentMatch = new Models.QualifierRoom { Referee = new Models.RefereeInfo { DisplayName = refereeName } };
            
            matchManager.currentState = IMatchManager.MatchState.Playing;

            var setMapMsg = new Mock<IIrcMessage>();
            setMapMsg.Setup(m => m.Prefix).Returns(refereeName);
            setMapMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">setmap nm1" });
            
            await matchManager.HandleIrcMessage(setMapMsg.Object);
            mockBanchoClient.Verify(c => c.SendPrivateMessageAsync(channelName, It.Is<string>(s => s.StartsWith("!mp map"))), Times.Never);
        }
        
        [Fact]
        public async Task EdgeCase_SetMap_WithInvalidSlot_ShouldNotCrash()
        {
            var refereeName = "Furina";
            var channelName = "#mp_12345";
            var mockBanchoClient = new Mock<IBanchoClient>();
            var matchManager = new MatchManagerQualifiersStage("C4", refereeName, (id, msg) => { });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.Idle;
    
            matchManager.currentMatch = new Models.QualifierRoom
            {
                Referee = new Models.RefereeInfo { DisplayName = refereeName },
                Round = new Models.Round
                {
                    MapPool = new List<Models.RoundBeatmap>
                    {
                        new Models.RoundBeatmap { BeatmapID = 1453229, Slot = "NM1" }
                    }
                }
            };
    
            var setMapMsg = new Mock<IIrcMessage>();
            setMapMsg.Setup(m => m.Prefix).Returns(refereeName);
            setMapMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">setmap sida45" });
            
            var exception = await Record.ExceptionAsync(() => matchManager.HandleIrcMessage(setMapMsg.Object));
            Assert.Null(exception); 
        }
    }
}