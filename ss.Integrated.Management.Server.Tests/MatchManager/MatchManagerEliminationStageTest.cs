using BanchoSharp.Interfaces;
using Moq;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.MatchManager;

namespace ss.Integrated.Management.Server.Tests.MatchManager
{
    public class MatchManagerEliminationStageTests
    {
        [Fact]
        public async Task PanicProtocol_ShouldPauseAndResumeAutomation()
        {
            var refereeName = "Furina";
            var matchId = "C4";
            var channelName = "#mp_12345";

            var mockBanchoClient = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage(matchId, refereeName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.WaitingForStart;

            matchManager.currentMatch = new Models.MatchRoom
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

            var matchManager = new MatchManagerEliminationStage(matchId, refereeName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.Idle;

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = matchId,
                Referee = new Models.RefereeInfo { DisplayName = refereeName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round
                {
                    BestOf = 9, BanRounds = 1, MapPool = new List<Models.RoundBeatmap>
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

            var firstPickRedMsg = new Mock<IIrcMessage>();
            firstPickRedMsg.Setup(m => m.Prefix).Returns(refereeName);
            firstPickRedMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">firstpick red" });

            var firstBanBlueMsg = new Mock<IIrcMessage>();
            firstBanBlueMsg.Setup(m => m.Prefix).Returns(refereeName);
            firstBanBlueMsg.Setup(m => m.Parameters).Returns(new[] { channelName, ">firstban blue" });

            await matchManager.HandleIrcMessage(firstPickRedMsg.Object);
            await matchManager.HandleIrcMessage(firstBanBlueMsg.Object);

            await matchManager.HandleIrcMessage(startMsg.Object);
            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);

            await matchManager.HandleIrcMessage(stopMsg.Object);
            Assert.Equal(IMatchManager.MatchState.Idle, matchManager.currentState);
        }

        [Fact]
        public async Task AdminCommands_FromUnauthorizedUser_ShouldBeIgnore()
        {
            var refereeName = "Furina";
            var channelName = "#mp_12345";

            var mockBanchoClient = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("Q1", refereeName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.WaitingForStart;

            matchManager.currentMatch = new Models.MatchRoom
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

            var matchManager = new MatchManagerEliminationStage(matchId, refereeName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.Idle;

            matchManager.currentMatch = new Models.MatchRoom
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

            var matchManager = new MatchManagerEliminationStage("C4", refereeName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentMatch = new Models.MatchRoom { Referee = new Models.RefereeInfo { DisplayName = refereeName } };

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

            var matchManager = new MatchManagerEliminationStage("C4", refereeName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentMatch = new Models.MatchRoom { Referee = new Models.RefereeInfo { DisplayName = refereeName } };

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

            var matchManager = new MatchManagerEliminationStage("C4", refereeName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentMatch = new Models.MatchRoom { Referee = new Models.RefereeInfo { DisplayName = refereeName } };

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

            var matchManager = new MatchManagerEliminationStage("C4", refereeName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBanchoClient.Object;
            matchManager.lobbyChannelName = channelName;
            matchManager.currentState = IMatchManager.MatchState.Idle;

            matchManager.currentMatch = new Models.MatchRoom
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

        [Fact]
        public async Task FullMatchSimulation_DoubleBanFlow_ShouldProgressToFinish()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBancho.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channel;
            matchManager.currentState = IMatchManager.MatchState.Idle;
            matchManager.bannedMaps = new List<Models.RoundChoice>();
            matchManager.pickedMaps = new List<Models.RoundChoice>();

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 2, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };

            Func<string, string, string, Task> PlayMap = async (picker, map, winner) =>
            {
                await SendMsg(picker, map);
                Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);

                await SendMsg("BanchoBot", "All players are ready");
                Assert.Equal(IMatchManager.MatchState.Playing, matchManager.currentState);

                string loser = winner == "RedTeam" ? "BlueTeam" : "RedTeam";
                await SendMsg("BanchoBot", $"{winner} finished playing (Score: 1000000, PASSED)");
                await SendMsg("BanchoBot", $"{loser} finished playing (Score: 500000, PASSED)");

                await SendMsg("BanchoBot", "The match has finished!");
            };


            await SendMsg(refName, ">firstpick red");
            await SendMsg(refName, ">firstban blue");
            await SendMsg(refName, ">start");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);
            await SendMsg("BlueTeam", "NM1"); // Blue ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            await SendMsg("RedTeam", "HD1"); // Red ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);

            await PlayMap("RedTeam", "NM2", "RedTeam"); // Pick 1: (Score: 1 - 0)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);

            await PlayMap("BlueTeam", "HR1", "BlueTeam"); // Pick 2: (Score: 1 - 1)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);

            await PlayMap("RedTeam", "HD2", "RedTeam"); // Pick 3: (Score: 2 - 1)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);

            await PlayMap("BlueTeam", "DT1", "RedTeam"); // Pick 4: (Score: 3 - 1)

            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            await SendMsg("RedTeam", "NM3"); // Red ban 2

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);
            await SendMsg("BlueTeam", "HR2"); // Blue ban 2

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            await PlayMap("RedTeam", "DT2", "BlueTeam"); // Pick 5: (Score: 3 - 2)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);
            await PlayMap("BlueTeam", "NM4", "RedTeam"); // Pick 6: (Score: 4 - 2)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            await PlayMap("RedTeam", "HD3", "RedTeam"); // Pick 7: (Score: 5 - 2)

            Assert.Equal(IMatchManager.MatchState.MatchFinished, matchManager.currentState);
        }

        [Fact]
        public async Task FullMatchSimulation_SingleBanFlow_ShouldProgressToFinish()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBancho.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channel;
            matchManager.currentState = IMatchManager.MatchState.Idle;
            matchManager.bannedMaps = new List<Models.RoundChoice>();
            matchManager.pickedMaps = new List<Models.RoundChoice>();

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };

            Func<string, string, string, Task> PlayMap = async (picker, map, winner) =>
            {
                await SendMsg(picker, map);
                Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);

                await SendMsg("BanchoBot", "All players are ready");
                Assert.Equal(IMatchManager.MatchState.Playing, matchManager.currentState);

                string loser = winner == "RedTeam" ? "BlueTeam" : "RedTeam";
                await SendMsg("BanchoBot", $"{winner} finished playing (Score: 1000000, PASSED)");
                await SendMsg("BanchoBot", $"{loser} finished playing (Score: 500000, PASSED)");

                await SendMsg("BanchoBot", "The match has finished!");
            };


            await SendMsg(refName, ">firstpick red");
            await SendMsg(refName, ">firstban blue");
            await SendMsg(refName, ">start");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);
            await SendMsg("BlueTeam", "NM1"); // Blue ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            await SendMsg("RedTeam", "HD1"); // Red ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);

            await PlayMap("RedTeam", "NM2", "RedTeam"); // Pick 1: (Score: 1 - 0)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);

            await PlayMap("BlueTeam", "HR1", "BlueTeam"); // Pick 2: (Score: 1 - 1)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);

            await PlayMap("RedTeam", "HD2", "RedTeam"); // Pick 3: (Score: 2 - 1)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);

            await PlayMap("BlueTeam", "DT1", "RedTeam"); // Pick 4: (Score: 3 - 1)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            await PlayMap("RedTeam", "DT2", "BlueTeam"); // Pick 5: (Score: 3 - 2)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);
            await PlayMap("BlueTeam", "NM4", "RedTeam"); // Pick 6: (Score: 4 - 2)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            await PlayMap("RedTeam", "HD3", "RedTeam"); // Pick 7: (Score: 5 - 2)

            Assert.Equal(IMatchManager.MatchState.MatchFinished, matchManager.currentState);
        }

        [Fact]
        public async Task FullMatchSimulation_Bo9TieBreakerFlow_ShouldProgressToFinish()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBancho.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channel;
            matchManager.currentState = IMatchManager.MatchState.Idle;
            matchManager.bannedMaps = new List<Models.RoundChoice>();
            matchManager.pickedMaps = new List<Models.RoundChoice>();

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };

            Func<string, string, string, Task> PlayMap = async (picker, map, winner) =>
            {
                await SendMsg(picker, map);
                Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);

                await SendMsg("BanchoBot", "All players are ready");
                Assert.Equal(IMatchManager.MatchState.Playing, matchManager.currentState);

                string loser = winner == "RedTeam" ? "BlueTeam" : "RedTeam";
                await SendMsg("BanchoBot", $"{winner} finished playing (Score: 1000000, PASSED)");
                await SendMsg("BanchoBot", $"{loser} finished playing (Score: 500000, PASSED)");

                await SendMsg("BanchoBot", "The match has finished!");
            };

            Func<string, Task> PlayTieBreaker = async (winner) =>
            {
                mockBancho.Verify(c => c.SendPrivateMessageAsync(channel, "!mp map 1014"), Times.Once);

                await SendMsg("BanchoBot", "All players are ready");
                Assert.Equal(IMatchManager.MatchState.Playing, matchManager.currentState);

                string loser = winner == "RedTeam" ? "BlueTeam" : "RedTeam";
                await SendMsg("BanchoBot", $"{winner} finished playing (Score: 1000000, PASSED)");
                await SendMsg("BanchoBot", $"{loser} finished playing (Score: 500000, PASSED)");

                await SendMsg("BanchoBot", "The match has finished!");
            };


            await SendMsg(refName, ">firstpick red");
            await SendMsg(refName, ">firstban blue");
            await SendMsg(refName, ">start");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);
            await SendMsg("BlueTeam", "NM1"); // Blue ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            await SendMsg("RedTeam", "HD1"); // Red ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);

            await PlayMap("RedTeam", "NM2", "RedTeam"); // Pick 1: (Score: 1 - 0)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);

            await PlayMap("BlueTeam", "HR1", "BlueTeam"); // Pick 2: (Score: 1 - 1)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);

            await PlayMap("RedTeam", "HD2", "RedTeam"); // Pick 3: (Score: 2 - 1)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);

            await PlayMap("BlueTeam", "DT1", "RedTeam"); // Pick 4: (Score: 3 - 1)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            await PlayMap("RedTeam", "DT2", "BlueTeam"); // Pick 5: (Score: 3 - 2)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);
            await PlayMap("BlueTeam", "NM4", "RedTeam"); // Pick 6: (Score: 4 - 2)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            await PlayMap("RedTeam", "NM3", "BlueTeam"); // Pick 7: (Score: 4 - 3)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);
            await PlayMap("BlueTeam", "NM5", "BlueTeam"); // Pick 8: (Score: 4 - 4)

            Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);
            await PlayTieBreaker("BlueTeam"); // Pick 9: (Score: 4 - 5)

            Assert.Equal(IMatchManager.MatchState.MatchFinished, matchManager.currentState);
        }

        [Fact]
        public async Task Timeout_StolenPick_MaintainsCorrectOrder()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBancho.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channel;
            matchManager.currentState = IMatchManager.MatchState.Idle;
            matchManager.bannedMaps = new List<Models.RoundChoice>();
            matchManager.pickedMaps = new List<Models.RoundChoice>();

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };

            Func<string, string, string, Task> PlayMap = async (picker, map, winner) =>
            {
                await SendMsg(picker, map);
                Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);

                await SendMsg("BanchoBot", "All players are ready");
                Assert.Equal(IMatchManager.MatchState.Playing, matchManager.currentState);

                string loser = winner == "RedTeam" ? "BlueTeam" : "RedTeam";
                await SendMsg("BanchoBot", $"{winner} finished playing (Score: 1000000, PASSED)");
                await SendMsg("BanchoBot", $"{loser} finished playing (Score: 500000, PASSED)");

                await SendMsg("BanchoBot", "The match has finished!");
            };

            await SendMsg(refName, ">firstpick red");
            await SendMsg(refName, ">firstban blue");
            await SendMsg(refName, ">start");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);
            await SendMsg("BlueTeam", "NM1"); // Blue ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            await SendMsg("RedTeam", "HD1"); // Red ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            await PlayMap("RedTeam", "NM2", "RedTeam");

            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);
            await SendMsg("BanchoBot", "Countdown finished");

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            await PlayMap("RedTeam", "NM3", "RedTeam");

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
        }

        [Fact]
        public async Task TimeoutCommand_PlayerRequested_ShouldPauseAndResumeCorrectly()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            })
            {
                client = mockBancho.Object,
                joined = true,
                lobbyChannelName = channel,
                currentState = IMatchManager.MatchState.Idle,
                bannedMaps = [],
                pickedMaps = [],
            };

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };
            
            await SendMsg(refName, ">firstpick red");
            await SendMsg(refName, ">firstban blue");
            await SendMsg(refName, ">start");

            await SendMsg("BlueTeam", "NM1");
            await SendMsg("RedTeam", "HD1");
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            
            await SendMsg("RedTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.OnTimeout, matchManager.currentState);
           
            await SendMsg("BanchoBot", "Countdown finished");
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            
            await SendMsg("RedTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            
            await SendMsg("RedTeam", "NM2");
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);
            
            await SendMsg("BlueTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.OnTimeout, matchManager.currentState);
            
            await SendMsg("BanchoBot", "Countdown finished");
            Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);
        }
        
        [Fact]
        public async Task AssistedMode_ShouldIgnorePlayerPicksAndBans_AndAcceptRefOverride()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            })
            {
                client = mockBancho.Object,
                joined = true,
                lobbyChannelName = channel,
                currentState = IMatchManager.MatchState.Idle,
                bannedMaps = [],
                pickedMaps = [],
            };

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };
            
            await SendMsg(refName, ">firstpick red");
            await SendMsg(refName, ">firstban blue");
            await SendMsg(refName, ">start assisted");
            
            Assert.Equal(MatchManagerEliminationStage.OperationMode.Assisted, matchManager.currentMode);
            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);
            
            await SendMsg("BlueTeam", "NM1");
            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);
            Assert.Empty(matchManager.bannedMaps);
            
            await SendMsg(refName, ">next NM1");
            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            Assert.Contains(matchManager.bannedMaps, m => m.Slot == "NM1");
            
            await SendMsg("RedTeam", "HD1");
            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            
            await SendMsg(refName, ">next HD1");
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
        }

        [Fact]
        public async Task Timeouts_ShouldBeIgnoredInAssistedMode_AndWorkInAutoMode()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            })
            {
                client = mockBancho.Object,
                joined = true,
                lobbyChannelName = channel,
                currentState = IMatchManager.MatchState.Idle,
                bannedMaps = [],
                pickedMaps = [],
            };

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };
            
            await SendMsg(refName, ">firstpick red");
            await SendMsg(refName, ">firstban red");
            await SendMsg(refName, ">start assisted");
            Assert.Equal(MatchManagerEliminationStage.OperationMode.Assisted, matchManager.currentMode);
            
            await SendMsg("RedTeam", "!timeout");
            Assert.NotEqual(IMatchManager.MatchState.OnTimeout, matchManager.currentState);
            
            await SendMsg(refName, ">operation auto");
            Assert.Equal(MatchManagerEliminationStage.OperationMode.Automatic, matchManager.currentMode);
            
            await SendMsg("RedTeam", "!timeout");
            Assert.Equal(IMatchManager.MatchState.OnTimeout, matchManager.currentState);
        }
        
        [Fact]
        public async Task UndoCommand_ShouldRevertLastPickAndState()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            })
            {
                client = mockBancho.Object,
                joined = true,
                lobbyChannelName = channel,
                currentState = IMatchManager.MatchState.Idle,
                bannedMaps = [],
                pickedMaps = [],
            };

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };
            
            await SendMsg(refName, ">firstpick red");
            await SendMsg(refName, ">firstban red");
            await SendMsg(refName, ">start auto");
            
            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            Assert.Empty(matchManager.bannedMaps);
            
            await SendMsg("RedTeam", "NM1");
            
            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);
            Assert.Single(matchManager.bannedMaps);
            Assert.Equal("NM1", matchManager.bannedMaps[0].Slot);
            
            await SendMsg(refName, ">undo");
            
            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            Assert.Empty(matchManager.bannedMaps);
        }

        [Fact]
        public async Task UndoCommand_ShouldNotCrashWhenHistoryIsEmpty()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            })
            {
                client = mockBancho.Object,
                joined = true,
                lobbyChannelName = channel,
                currentState = IMatchManager.MatchState.Idle,
                bannedMaps = [],
                pickedMaps = [],
            };

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };
            
            await SendMsg(refName, ">undo");
            Assert.Equal(IMatchManager.MatchState.Idle, matchManager.currentState);
        }
        
        [Fact]
        public async Task InviteCommand_ShouldNotCrash()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            })
            {
                client = mockBancho.Object,
                joined = true,
                lobbyChannelName = channel,
                currentState = IMatchManager.MatchState.Idle,
                bannedMaps = [],
                pickedMaps = [],
            };

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };
            
            await SendMsg(refName, ">invite");
            mockBancho.Verify(c => c.SendPrivateMessageAsync(channel, "!mp invite #1"), Times.Once, "error");
            mockBancho.Verify(c => c.SendPrivateMessageAsync(channel, "!mp invite #2"), Times.Once, "error");
        }
        
        [Fact]
        public async Task MatchSimulation_SetScores_ShouldProgressToFinish()
        {
            var channel = "#mp_1";
            var refName = "Furina";
            var mockBancho = new Mock<IBanchoClient>();

            var matchManager = new MatchManagerEliminationStage("96", refName, (id, msg, messageKind) =>
            {
            });

            matchManager.client = mockBancho.Object;
            matchManager.joined = true;
            matchManager.lobbyChannelName = channel;
            matchManager.currentState = IMatchManager.MatchState.Idle;
            matchManager.bannedMaps = new List<Models.RoundChoice>();
            matchManager.pickedMaps = new List<Models.RoundChoice>();

            var mappool = new List<Models.RoundBeatmap>();
            string[] slots = { "NM1", "NM2", "NM3", "NM4", "NM5", "HD1", "HD2", "HD3", "HR1", "HR2", "HR3", "DT1", "DT2", "DT3", "TB1" };
            for (int i = 0; i < slots.Length; i++) mappool.Add(new Models.RoundBeatmap { BeatmapID = 1000 + i, Slot = slots[i] });

            matchManager.currentMatch = new Models.MatchRoom
            {
                Id = "96",
                Referee = new Models.RefereeInfo { DisplayName = refName, IRC = "pass" },
                TeamRed = new Models.User { OsuData = new() { Username = "RedTeam", Id = 1 } },
                TeamBlue = new Models.User { OsuData = new() { Username = "BlueTeam", Id = 2 } },
                Round = new Models.Round { BestOf = 9, BanRounds = 1, MapPool = mappool }
            };

            Func<string, string, Task> SendMsg = async (sender, content) =>
            {
                var msg = new Mock<IIrcMessage>();
                msg.Setup(m => m.Prefix).Returns(sender);
                msg.Setup(m => m.Parameters).Returns(new[] { channel, content });
                await matchManager.HandleIrcMessage(msg.Object);
            };

            Func<string, string, string, Task> PlayMap = async (picker, map, winner) =>
            {
                await SendMsg(picker, map);
                Assert.Equal(IMatchManager.MatchState.WaitingForStart, matchManager.currentState);

                await SendMsg("BanchoBot", "All players are ready");
                Assert.Equal(IMatchManager.MatchState.Playing, matchManager.currentState);

                string loser = winner == "RedTeam" ? "BlueTeam" : "RedTeam";
                await SendMsg("BanchoBot", $"{winner} finished playing (Score: 1000000, PASSED)");
                await SendMsg("BanchoBot", $"{loser} finished playing (Score: 500000, PASSED)");

                await SendMsg("BanchoBot", "The match has finished!");
            };


            await SendMsg(refName, ">firstpick red");
            await SendMsg(refName, ">firstban blue");
            await SendMsg(refName, ">start");

            Assert.Equal(IMatchManager.MatchState.WaitingForBanBlue, matchManager.currentState);
            await SendMsg("BlueTeam", "NM1"); // Blue ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForBanRed, matchManager.currentState);
            await SendMsg("RedTeam", "HD1"); // Red ban 1

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);

            await PlayMap("RedTeam", "NM2", "RedTeam"); // Pick 1: (Score: 1 - 0)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);

            await PlayMap("BlueTeam", "HR1", "BlueTeam"); // Pick 2: (Score: 1 - 1)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);

            await PlayMap("RedTeam", "HD2", "RedTeam"); // Pick 3: (Score: 2 - 1)
            Assert.Equal(IMatchManager.MatchState.WaitingForPickBlue, matchManager.currentState);

            await PlayMap("BlueTeam", "DT1", "RedTeam"); // Pick 4: (Score: 3 - 1)

            Assert.Equal(IMatchManager.MatchState.WaitingForPickRed, matchManager.currentState);
            await SendMsg(refName, ">setscore 2 2"); // Pick 4: (Score: 2 - 2) yo que se, se le fue la conexión
            Assert.Equal(2, matchManager.MatchScore[0]);
            Assert.Equal(2, matchManager.MatchScore[1]);
        }
    }
}