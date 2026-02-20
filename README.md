# Spanish Showdown: Integrated Management Server (SS-IMS)

## 📖 Overview

The **SS-IMS** is a high-performance, event-driven middleware designed by **SSConexión** to revolutionize the logistical flow of the _Spanish Showdown_.

This system addresses the critical scalability issues found in modern osu! tournaments: Google Sheets have been giving us problems for a long time. That is why we've made this, to get rid of that and gain complete, reliable control over data and scheduling flows. **Read below for details on how to run everything and explanations.**

Extra docs and information can be found [here](https://docs.spanishshowdown.es/) ([fallback](https://ssconexion.github.io/ss-integrated-management-server/))

## 🚀 Deployment & Installation

### Prerequisites

- **.NET 9.0 SDK** (or later)
    
- **PostgreSQL** Server (v13+)
    
- Valid `.env` configuration (Refer to `.env.example`).

- Valid database schema (empty .sql file ready for deployment (here)[https://gist.github.com/Angarn/ddd9cf693aaeeb9407cc46b750fce3e5])

### How to run

- `dotnet run` to get it running

- Apply the schema linked above to your db

- Get it filled with tools such as Adminer

## ✨ System Capabilities & Key Features

- **Zero-Sheet Dependency:** Eliminates reliance on Google Sheets/Excel. All data is persisted in a relational PostgreSQL database to have complete freedom over how we move data around.
    
- **Database-First Design:** Centralized truth for all tournament data (Players, Teams, Matches, Maps).
    
- **Redundant Score Importing:** Parsing for existing CSV scripts (e.g., [LeoFLT's qualifier script](https://gist.github.com/LeoFLT/2a7e0c3c201a327f022aa5b61b679d3f)) for private runs and a more native one that makes use of osu-api to parse a JSON file. This is enough for our usecase.

- **Qualifiers Automation:** Fully autonomous lobby management for qualifier lobbies (Room creation, map cycling, timer management).
    
- **Elimination Automation:** State-machine based logic for Head-to-Head matches. Handles **Pick/Ban phases**, **Best-Of logic**, **Tiebreaker enforcement**, and **Win Conditions** without human intervention.
    
- **Panic Protocol:** Built-in fail-safe mechanisms allowing human referees to instantly override automation in case of edge cases or disputes (`!panic` / `>panic_over`).

- **Seamless Integration with Discord:** Bi-directional communication. Control matches, receive live logs, and manage player notifications directly from a dedicated Discord thread.
    
- **Dynamic Match Management:** Since we dont have Google Sheets anymore, most of the bracket management is done through Discord and Adminer. The discord part is done here and is often enough, although we recommend to setup adminer to get full control.

# But, how does the Automated Refereeing work?

## 1. Quick Overview

The **Spanish Showdown Automated Refereeing Tool** is a modular automation tool designed to manage osu! tournament matches autonomously (although you can just not use the automation and use it like a discord to bancho IRC client, it is up to the user.). It bridges the gap between **Discord** (for coordination and logging) and **Bancho (osu! IRC)** (for game lobby management).

The system utilizes `BanchoSharp` for IRC communication and `Discord.Net` for user interaction. It ensures matches are played according to strict timing and rulesets without requiring constant human intervention, while maintaining a manual override safety net in case it is needed.

## 2. The Discord Manager

Discord serves as the entry point and orchestrator for all tournament matches. It is responsible for initializing matches and doing more tournament management oriented tasks, but we will be touching on the relevant stuff in 2.1.

### 2.1. A Match's Lifecycle 

Managing a match consists of two simple steps:

- **Initialization:** It is done with the following command: `/startref [match-id] [referee] [match-type]` It will start a multiplayer room on bancho along with its thread on a specified channel (check out the `.env.example`) if the match introduced is valid.

- **Termination:** Once the match is over and the ref specifies it with the `/endref [match-id]` command, all changes will be saved on the database and the bancho multiplayer room will be closed. The thread that held the match will be archived, securely keeping all the chat and play logs accessible in the case of a tournament staff needing to review them later. **IMPORTANT: Do not send a !mp close before an /endref command because it won't save all the changes correctly into the database!!!**

### 2.2. Other commands that may be useful

The system provides some Discord Slash Commands to manage the tournament schedule, handle referee assignments, and process match scores without ever having to touch a database or a spreadsheet. 
All commands are restricted to tournament staff holding the designated Referee role (again, check out the `.env.example`).

#### Referee & Staff Setup
- `/linkirc [nombre] [osuId] [ircPass]`: Links a referee's osu! account and IRC password to the bot. This is required so the bot can securely send messages to Bancho on the referee's behalf.
- `/assignref [matchId] [refName]`: Assigns a specific human referee to an upcoming match or qualifier room. 

#### Scheduling & Match Creation
- `/creatematchup [matchId] [teamRed] [teamBlue] [fridayDate] [roundId]`: Creates a new Head-to-Head match. **Note:** The bot will read both players submitted schedules and generate an interactive dropdown menu in Discord. This menu highlights the exact hours where both players are available, making scheduling easier for tournament staff.
- `/reschedulematchup [matchId] [date] [hour]`: Reschedules an existing match to a new time. (Uses `DD/MM HH:mm` format).
- `/removematchup [matchId]`: Cancels and deletes a Head-to-Head match from the database.
- `/createqualifierslobby [roomId] [date] [hour] [roundId]`: Schedules a new Qualifier room.
- `/removequalifiersroom [roomId]`: Cancels and deletes a Qualifier room.

#### Scoring & Match Tracking
- `/matchups`: Displays a paginated list of all scheduled matches and their starting times.
- `/importscores [osuLobbyId] [dbRoomId]`: Provide the Bancho lobby ID from the osu! website, and the bot will automatically fetch the match data via the osu! API, calculate grades, and securely save all player scores into the database. **Note:** The bot does not keep track of what lobbys have been parsed. Be careful and try to not double-parse the same room. If you do, you will have to get on Adminer to manually remove them.
- `/importscores-privaterooms [matchId] [file]`: A fallback method for private or unlisted lobbies. Allows a referee to upload a raw `.csv` file containing the match results ([check out LeoFLT's userscript for this](https://gist.github.com/LeoFLT/2a7e0c3c201a327f022aa5b61b679d3f)) to parse and save them manually.
- `/addmplinkid [matchId] [mpLinkId]`: Attaches an MP Link to a match for record-keeping. This is also a fallback in the case anything goes wrong, because currently **MP links are automatically saved once you do /endref, so there is no need to use this under normal circumstances.**

## 3. Qualifiers Mode, how does it work?

This automaton is specifically designed to handle **Qualifier Lobbies** where players play through a fixed map pool sequentially.

### 3.1. Workflow & State Machine

The core logic relies on a state machine (`MatchState`) to ensure the match progresses linearly and safely 

![image](https://ssconexion.github.io/ss-integrated-management-server/dot_inline_dotgraph_2_org.svg)

|**State**|**Description**|
|---|---|
|`Idle`|System is waiting for internal processing or initialization.|
|`WaitingForStart`|Map is selected, settings are applied, and the system is waiting for players to ready up or the timer to finish.|
|`Playing`|The map is currently being played. The system monitors for the "Match Finished" bancho message.|
|`MatchFinished`|The entire pool has been played.|
|`MatchOnHold`|**Panic Mode**. The system pauses all automation and awaits human intervention.|

### 3.2. Core Mechanics

- The referee will engage the auto mode once everyone is in the room and ready to go

- The automaton will iterate over all the maps in the mappool and finish up once it has gone over all of them.

### 3.3. Safety & Manual Overrides (Panic System)

To comply with tournament regulations regarding automation safety, the system includes a **Panic Protocol** that can be triggered by the referee or authorized staff via IRC or Discord commands. All IRC chat and gameplay logs are actively mirrored and retained in the match's Discord thread. This ensures that when a human referee takes over, they can instantly read the match history and context with minimal interference. **Whenever a match starts, the bot outputs an IRC join command in Discord that can be used by the referee to manually join the lobby if the integrated server experiences connection issues.**

- **`!panic`**: Stops everything and pings all referees. The automaton will only leave this state if a referee allows it with the `!panic_over` directive.
        
- **`>panic_over`**: Restarts automation where it left off. Can only be triggered by a referee
        
### 3.4. Administrative Commands

The system accepts commands prefixed with `>` (e.g., `>start`) from the designated referee account or Discord interface:

- `>invite`: Iterates through the database list of players assigned to this room and sends `!mp invite #id` commands to Bancho.
    
- `>start`: Engages the automatic flow, loading the first map of the pool.

- `>stop`: Stops the automaton and gives back manual control to the referee
    
- `>finish`: Forces the match to close (`!mp close`).

- `>setmap`: Sets a certain map from the qualifiers mappool with the proper mods for it. Can't be triggered while the automaton is active

#### Find all the intricacies here: https://ssconexion.github.io/ss-integrated-management-server/classAutoRefQualifiersStage.html

## 4. What about the Elimination Stage automaton?

The `AutoRefEliminationStage` handles **Head-to-Head (1v1 or Team vs Team)** matches. Unlike the linear flow of qualifiers, this module manages a dynamic, turn-based environment involving banning phases, picking phases, score validation, and win condition checks (Best Of X).

### 4.1. Workflow & State Machine

The match logic is governed by a complex state machine that dictates whose turn it is and what actions are valid.

![image](https://ssconexion.github.io/ss-integrated-management-server/dot_inline_dotgraph_1_org.svg)

|**State Category**|**States**|**Description**|
|---|---|---|
|**Initialization**|`Idle`|System is waiting for referee input or configuration|
|**Banning Phase**|`BanPhaseStart`, `WaitingForBanRed`, `WaitingForBanBlue`|The system enforces map bans based on the configured order.|
|**Picking Phase**|`PickPhaseStart`, `WaitingForPickRed`, `WaitingForPickBlue`|The system waits for the active team to select a map from the pool.|
|**Gameplay**|`WaitingForStart`, `Playing`|A map is loaded, timer is running, or players are currently playing.|
|**Resolution**|`MatchFinished`|One team has reached the win condition.|
|**Interrupts**|`OnTimeout`, `MatchOnHold`|A timeout is active or the match is paused via Panic Mode.|

### 4.2. Core Mechanics

- For each map that players pick, it will be validated in order to ensure it is a valid map (cant pick what is already picked/banned or TB)

- Tiebreaker is automatically picked if the conditions for it are met.

- Scores are processed with the following regex: `^(.*) finished playing \(Score: (\d+),`. This helps determine who wins the point and show it to the players afterwards
    
- If the round configuration specifies `BanRounds == 2` and `BanMode == SpanishShowdown`, the system interrupts the picking phase after 4 maps have been played to allow a second set of bans. This can be modified to your liking in order to have the ban rounds however you see fit.

### 4.3. Safety & Manual Overrides (Panic System)

To comply with tournament regulations regarding automation safety, this system also includes a **Panic Protocol** that can be triggered by the referee or authorized staff via IRC or Discord commands. All IRC chat and gameplay logs are actively mirrored and retained in the match's Discord thread. This ensures that when a human referee takes over, they can instantly read the match history and context with minimal interference. **Whenever a match starts, the bot outputs an IRC join command in Discord that can be used by the referee to manually join the lobby if the integrated server experiences connection issues.**

- **`!panic`**: Stops all the active timers and gets the state machine to a state where it can only be restarted with human intervention. A referee will get in touch with the players and solve anything that cause the panic to start
        
- **`>panic_over`**: Restarts automation where it left off. Can only be triggered by a referee

### 4.4. Administrative Commands

The referee controls the match flow using commands prefixed with `>`.

- `>start`: Starts auto mode

- `>stop`: Stops auto mode and gives back control

- `>firstpick [red/blue]`: Sets who will be picking first

- `>firstban [red/blue]`: Sets who will be banning first

- `>timeout`: Triggers a timeout state that doesn't use any timeouts that the players have.

- `>setmap [slot]`: Sets a given map with their mods. The automaton needs to be idle to be able to use it.

- `>maps`: Lists all available maps along picks and bans.

- `>invite`: Invites all the players to the lobby.

- `>finish`: Closes the lobby. Will be phased out sooner than later.

### 4.5. Player Interaction

Players interact with the system primarily by typing map slots in the chat when it is their turn.

- **Picking/Banning:** Typing `NM1`, `HD2`, etc., during their respective phase.
    
- **Timeouts:** A player can type `!timeout` to request a tactical timeout. The system tracks usage (`redTimeoutRequest`, `blueTimeoutRequest`) to ensure only one timeout is granted per team per match as stated in the rules of the tournament.

#### For more information on the actual code, you can check this out: https://ssconexion.github.io/ss-integrated-management-server/classAutoRefEliminationStage.html

## 5. Deployment & Environment Variables

To ensure successful deployment of the AutoRef system, the following environment configuration is required:

- `DISCORD_REFEREE_ROLE_ID`: ID of the Discord role to be pinged during `!panic` events.

- `DISCORD_BOT_TOKEN`: Token used by the Discord bot that manages everything

- `DISCORD_MATCHES_CHANNEL_ID`: ID of the Discord channel where match threads will be created

- `DISCORD_GUILD_ID`: ID of the Discord server where your tournament is taking place
    
- `POSTGRESQL_CONNECTION_STRING`: PostgreSQL connection string.

- `LANGUAGE`: States the language that will be used during the matches.

## 6. Open Source stuff

Contributions are welcome! If you are interested in improving the system, feel free to open a Pull Request or contact me on Discord (@Angarn). The project is licensed under the Apache License (see the LICENSE file for details)."


