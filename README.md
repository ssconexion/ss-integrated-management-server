# Spanish Showdown: Integrated Management Server (SS-IMS)

> **Automated Refereeing & Tournament Orchestration System**
## 📖 Overview

The **SS-IMS** is a high-performance, event-driven middleware designed by **SSConexión** to revolutionize the logistical flow of the _Spanish Showdown_.

This system addresses the critical scalability issues found in modern osu! tournaments: human error, volunteer unavailability, and the cognitive load of managing multiple concurrent lobbies. By replacing manual spreadsheet tracking with a **database-first approach** and bridging **Discord** with **Bancho (IRC)**, SS-IMS provides a seamless, autonomous refereeing experience for both Qualifier and Elimination stages.

Extra docs and information can be found [here](https://docs.spanishshowdown.es/) ([fallback](https://ssconexion.github.io/ss-integrated-management-server/))

## 🚀 Deployment & Installation

### Prerequisites

- **.NET 9.0 SDK** (or later)
    
- **PostgreSQL** Server (v13+)
    
- Valid `.env` configuration (Refer to `.env.example`).
    

### Quick Start

To deploy the database schema and apply migrations, execute the following EF Core commands in your terminal:

Bash

```sh
# Initialize migration snapshot
dotnet ef migrations add InitialCreate

# Apply schema to the database
dotnet ef database update
```

> **⚠️ Standalone Mode Configuration:**
> 
> This manager is architected to run alongside the official tournament web infrastructure. If you intend to run this tool in **Standalone Mode** (isolated from the web web-server), you **must comment out** the `IgnoreMigrations` directives in `ModelsContext.cs`. Failure to do so will prevent Entity Framework from generating the necessary tables.


## ✨ System Capabilities & Key Features

### Core Architecture

- **Zero-Sheet Dependency:** Eliminates reliance on Google Sheets/Excel. All data is persisted in a relational PostgreSQL database to ensure data integrity.
    
- **Database-First Design:** Centralized truth for all tournament data (Players, Teams, Matches, Maps).
    
- **Legacy Import Support:** Native parsing for existing CSV scripts (e.g., [LeoFLT's qualifier script](https://gist.github.com/LeoFLT/2a7e0c3c201a327f022aa5b61b679d3f)) to ease migration.
    

### Automation Modules (AutoRef)

- **Qualifier Automation:** Fully autonomous lobby management for qualifier lobbies (Room creation, map cycling, timer management, score logging).
    
- **Elimination Automation:** State-machine based logic for Head-to-Head matches. Handles **Pick/Ban phases**, **Best-Of logic**, **Tiebreaker enforcement**, and **Win Conditions** without human intervention.
    
- **Panic Protocol:** Built-in fail-safe mechanisms allowing human referees to instantly override automation in case of edge cases or disputes (`!panic` / `!panic_over`).
    

### Discord & Lifecycle Management

- **Seamless Integration:** Bi-directional communication. Control matches, receive live logs, and manage player notifications directly from a dedicated Discord thread.
    
- **Automated Provisioning:** Instant setup of match environments, including Discord threads, database entries, and Bancho lobbies upon scheduled start time.
    
- **Dynamic Rescheduling:** Fully implemented system for handling time changes and slot management, updating the schedule in real-time without manual database queries


# AutoRef System Documentation

## 1. System Overview

The **AutoRef System** is a modular, event-driven automation tool designed to manage osu! tournament matches autonomously. It bridges the gap between **Discord** (for coordination and logging) and **Bancho (osu! IRC)** (for game lobby management).

The system is built on **.NET (C#)** and utilizes `BanchoSharp` for IRC communication and `Discord.Net` for user interaction. It ensures matches are played according to strict timing and rulesets without requiring constant human intervention, while maintaining a manual override safety net.

## 2. Architecture: The Discord Manager

The `DiscordManager` serves as the entry point and orchestrator for all tournament matches. It is responsible for initializing the match environment, allocating resources, and spawning the appropriate worker process based on the match stage.

### 2.1. Lifecycle Management

The lifecycle of a match is handled via two primary asynchronous tasks:

#### **Initialization (`CreateMatchEnvironmentAsync`)**

1. **Validation:** Checks if the match ID is already active in memory to prevent duplication.
    
2. **Environment Setup:**
    
    - Locates the designated tournament parent channel.
    - Creates a **Public Thread** specific to the match (e.g., `TournamentName: Match A1 vs B2`). All logs and interactions are confined to this thread to reduce clutter.
        
3. **Worker Factory:** Instantiates the specific `IAutoRef` implementation based on the `MatchType` enum:
    
    - **Qualifiers Mode:** Uses `AutoRefQualifiersStage`.
    - **Elimination Mode:** Uses `AutoRefEliminationStage`.
        
4. **Execution:** Starts the worker task asynchronously in a non-blocking context.

#### **Termination (`EndMatchEnvironmentAsync`)**

1. **Graceful Shutdown:** Signals the worker to stop processing and disconnect from Bancho.
    
2. **Cleanup:** Removes the match from the active memory dictionary.
    
3. **Archival:** Notifies the thread, locks it to prevent further messages, and archives it for record-keeping.

## 3. Module: AutoRef Qualifiers Stage

The `AutoRefQualifiersStage` class implements the `IAutoRef` interface, specifically designed to handle **Qualifier Lobbies** where players play through a fixed map pool sequentially.

### 3.1. Workflow & State Machine

The core logic relies on a finite state machine (`MatchState`) to ensure the match progresses linearly and safely.

|**State**|**Description**|
|---|---|
|`Idle`|System is waiting for internal processing or initialization.|
|`WaitingForStart`|Map is selected, settings are applied, and the system is waiting for players to ready up or the timer to finish.|
|`Playing`|The map is currently being played. The system monitors for the "Match Finished" event.|
|`MatchFinished`|The entire pool has been played.|
|`MatchOnHold`|**Panic Mode**. The system pauses all automation and awaits human intervention.|

### 3.2. Initialization Phase (`StartAsync`)

1. **Database Hydration:** Fetches the `QualifierRoom`, `Referee` credentials, `Round` info, and `Player` list from the database (`ModelsContext`).
    
2. **Bancho Connection:** Establishes an authenticated IRC connection using the assigned referee's credentials.
    
3. **Lobby Creation:** Automatically creates a tournament lobby (`!mp make`) and sets the default room settings (Team Mode, Win Condition, Slots).
    

### 3.3. Automation Logic

The system listens to IRC messages via `HandleIrcMessage` and reacts to specific events:

- **Ready Check:** When "All players are ready" or "Countdown finished" is detected in `WaitingForStart` state, the system triggers `!mp start`.
    
- **Map Completion:** When "The match has finished" is detected, the system:
    
    - Increments the map index.
        
    - Waits a 10-second buffer period.
        
    - Calls `PrepareNextQualifierMap` to load the next beatmap and mods from the database.
        
- **Qualifier Flow:** The system iterates through the `Round.MapPool`. Once the index exceeds the pool size, the match is declared finished.
    

### 3.4. Safety & Manual Overrides (Panic System)

To comply with tournament regulations regarding automation safety, the system includes a **Panic Protocol** that can be triggered by the referee or authorized staff via IRC or Discord commands.

- **`!panic`**:
    
    - **Action:** Sets state to `MatchOnHold`.
        
    - **Effect:** Aborts the current timer (`!mp aborttimer`) and stops all automatic progression.
        
    - **Notification:** Pings the Referee role in Discord.
        
- **`!panic_over`**:
    
    - **Action:** Resumes automation.
        
    - **Effect:** Sets state back to `WaitingForStart` and resumes the start timer.
        

### 3.5. Administrative Commands

The system accepts commands prefixed with `>` (e.g., `>start`) from the designated referee account or Discord interface:

- `>invite`: Iterates through the database list of players assigned to this room and sends `!mp invite #id` commands to Bancho.
    
- `>start`: Engages the automatic flow, loading the first map of the pool.
    
- `>finish`: Forces the match to close (`!mp close`) and disconnects the bot.

## 4. Module: AutoRef Elimination Stage

The `AutoRefEliminationStage` handles **Head-to-Head (1v1 or Team vs Team)** matches. Unlike the linear flow of qualifiers, this module manages a dynamic, turn-based environment involving banning phases, picking phases, score validation, and win condition checks (Best Of X).

### 4.1. Workflow & State Machine

The match logic is governed by a complex state machine that dictates whose turn it is and what actions are valid.

|**State Category**|**States**|**Description**|
|---|---|---|
|**Initialization**|`Idle`|System is waiting for referee input or configuration.|
|**Banning Phase**|`BanPhaseStart`, `WaitingForBanRed`, `WaitingForBanBlue`|The system enforces map bans based on the configured order.|
|**Picking Phase**|`PickPhaseStart`, `WaitingForPickRed`, `WaitingForPickBlue`|The system waits for the active team to select a map from the pool.|
|**Gameplay**|`WaitingForStart`, `Playing`|A map is loaded, timer is running, or players are currently playing.|
|**Resolution**|`MatchFinished`|One team has reached the win condition.|
|**Interrupts**|`OnTimeout`, `MatchOnHold`|A timeout is active or the match is paused via Panic Mode.|

### 4.2. Core Mechanics

#### **Banning & Picking Logic**

The system validates every user input against the `IsMapAvailable` check:

1. **Existence:** The map slot (e.g., "NM1") must exist in the database for the current round.
    
2. **Uniqueness:** The map must not have been banned or picked previously.
    
3. **Tiebreaker Protocol:** The Tiebreaker map cannot be picked manually; it is only enforced by the system if the score reaches `(BestOf - 1) - 1`.
    

#### **Score Processing**

Scores are not read from the API (to avoid latency) but are scraped directly from BanchoBot's chat messages via Regex:

> `^(.*) finished playing \(Score: (\d+),`

1. **Aggregation:** The system aggregates scores for all players in the Red and Blue teams.
    
2. **Comparison:** The team with the higher total score wins the point (`matchScore`).
    
3. **Win Condition:** After every map, the system checks if a team has reached the required wins ( `(BestOf / 2) + 1`).
    

#### **Tiebreaker & Second Ban Phase**

- **Tiebreaker:** If the match score reaches a draw at match point (e.g., 3-3 in a BO7), the system automatically loads the map labeled `TB1`.
    
- **Double Ban Phase:** If the round configuration specifies `BanRounds == 2`, the system interrupts the picking phase after 4 maps have been played to allow a second set of bans.
    

### 4.3. Administrative Interface (IRC & Discord)

The referee controls the match flow using commands prefixed with `>`.

|**Command**|**Arguments**|**Description**|
|---|---|---|
|`>start`|None|Engages the automation. If the match was stopped previously, it resumes the state; otherwise, it starts the Ban Phase.|
|`>stop`|None|Pauses the automation and saves the state (`stoppedPreviously`). Moves system to `Idle`.|
|`>firstpick`|`red` / `blue`|**Required before start.** Sets which team picks first.|
|`>firstban`|`red` / `blue`|**Required before start.** Sets which team bans first.|
|`>timeout`|None|Manually triggers a timeout state.|
|`>setmap`|`[slot]`|Forces a map pick (e.g., `>setmap NM1`). Only works in `Idle`.|
|`>maps`|None|Prints the current Bans, Picks, and Available maps to the lobby.|
|`>invite`|None|Sends invites to all players in the database for this match.|
|`>finish`|None|Forces the match to close and disconnects the bot.|

### 4.4. Player Interaction

Players interact with the system primarily by typing map slots in the chat when it is their turn.

- **Picking/Banning:** Typing `NM1`, `HD2`, etc., during their respective phase.
    
- **Timeouts:** A player can type `!timeout` to request a tactical timeout. The system tracks usage (`redTimeoutRequest`, `blueTimeoutRequest`) to ensure only one timeout is granted per team per match.
    

### 4.5. Error Recovery & Persistence

- **State Recovery:** If the bot crashes or is stopped via `>stop`, the `previousState` is stored. When `>start` is issued again, the match resumes exactly where it left off.
    
- **Database Sync:** When `StopAsync` is called (end of match context), the system serializes the `BannedMaps` and `PickedMaps` lists and saves them to PostgreSQL to ensure tournament records are accurate.

## 5. Deployment & Environment Variables

To ensure successful deployment of the AutoRef system, the following environment configuration is required:

- `DISCORD_REFEREE_ROLE_ID`: ID of the Discord role to be pinged during `!panic` events.

- `DISCORD_BOT_TOKEN`: Token used by the Discord bot that manages everything

- `DISCORD_MATCHES_CHANNEL_ID`: ID of the Discord channel where match threads will be created

- `DISCORD_GUILD_ID`: ID of the Discord server where your tournament is taking place
    
- `POSTGRESQL_CONNECTION_STRING`: PostgreSQL connection string.

## 6. Known Limitations

- **Bancho Lag:** Extreme server lag may delay score parsing. The system includes a `Task.Delay` buffer to mitigate this.
    
- **Usernames:** The system relies on database usernames matching Bancho usernames. Name changes during a tournament must be synchronized in the DB.
