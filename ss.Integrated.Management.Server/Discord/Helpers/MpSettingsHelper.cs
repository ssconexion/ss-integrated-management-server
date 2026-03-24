using System.Text.RegularExpressions;

namespace ss.Internal.Management.Server.Discord.Helpers;

public class MpSettingsHelper
{
    private static readonly Regex RoomNameRx  = new(@"Room name: (.+?), History: (https://\S+)", RegexOptions.Compiled);
    private static readonly Regex BeatmapRx   = new(@"Beatmap: (https://\S+) (.+)",              RegexOptions.Compiled);
    private static readonly Regex TeamModeRx  = new(@"Team mode: (\w+), Win condition: (\w+)",   RegexOptions.Compiled);
    private static readonly Regex ModsRx      = new(@"Active mods: (.+)",                         RegexOptions.Compiled);
    private static readonly Regex PlayersRx   = new(@"Players: (\d+)",                            RegexOptions.Compiled);
    private static readonly Regex SlotRx      = new(@"Slot (\d+)\s+(\w+)\s+(https://\S+)\s+(.+?)\s+\[(?:Host\s*/\s*)?Team (\w+)\s*/\s*([^\]]+)\]", RegexOptions.Compiled);

    private readonly TaskCompletionSource<DiscordModels.MpSettingsResult> tcs = new();
    private readonly List<string> rawLines = [];

    private int expectedSlots = -1;
    private int collectedSlots = 0;

    public Task<DiscordModels.MpSettingsResult> Task => tcs.Task;
    
    public (bool Complete, bool Consumed) Feed(string line)
    {
        bool consumed = RoomNameRx.IsMatch(line)  ||
                        BeatmapRx.IsMatch(line)   ||
                        TeamModeRx.IsMatch(line)  ||
                        ModsRx.IsMatch(line)      ||
                        PlayersRx.IsMatch(line)   ||
                        SlotRx.IsMatch(line);

        if (!consumed) return (false, false);

        rawLines.Add(line);

        var playersMatch = PlayersRx.Match(line);
        if (playersMatch.Success)
            expectedSlots = int.Parse(playersMatch.Groups[1].Value);

        if (SlotRx.IsMatch(line))
            collectedSlots++;

        bool complete = expectedSlots == 0 ||
                        (expectedSlots > 0 && collectedSlots >= expectedSlots);

        if (complete) tcs.TrySetResult(Parse());

        return (complete, true);
    }

    public void Cancel() => tcs.TrySetCanceled();

    private DiscordModels.MpSettingsResult Parse()
    {
        string roomName = "", historyUrl = "", beatmapUrl = "", beatmapName = "",
               teamMode = "", winCondition = "", mods = "";
        var slots = new List<DiscordModels.SlotInfo>();

        foreach (var line in rawLines)
        {
            Match m;

            if ((m = RoomNameRx.Match(line)).Success)
            {
                roomName   = m.Groups[1].Value.Trim();
                historyUrl = m.Groups[2].Value;
            }
            else if ((m = BeatmapRx.Match(line)).Success)
            {
                beatmapUrl  = m.Groups[1].Value;
                beatmapName = m.Groups[2].Value.Trim();
            }
            else if ((m = TeamModeRx.Match(line)).Success)
            {
                teamMode     = m.Groups[1].Value;
                winCondition = m.Groups[2].Value;
            }
            else if ((m = ModsRx.Match(line)).Success)
            {
                mods = m.Groups[1].Value;
            }
            else if ((m = SlotRx.Match(line)).Success)
            {
                slots.Add(new DiscordModels.SlotInfo(
                    SlotNumber: int.Parse(m.Groups[1].Value),
                    IsReady:    m.Groups[2].Value == "Ready",
                    ProfileUrl: m.Groups[3].Value,
                    Username:   m.Groups[4].Value,
                    Team:       m.Groups[5].Value,
                    Mods:       m.Groups[6].Value
                ));
            }
        }

        return new DiscordModels.MpSettingsResult(roomName, historyUrl, beatmapUrl, beatmapName,
                                    teamMode, winCondition, mods, slots);
    }
}