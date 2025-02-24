using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using InfServer.Game;
using InfServer.Scripting;
using InfServer.Protocol;
using InfServer.Logic;

using Assets;

public class BuildManager
{
    /// <summary>
    /// Converts a list of ?buy commands into a dictionary format.
    /// </summary>
    public Dictionary<string, Tuple<List<Tuple<string, ushort>>, string, string, string, string>> ConvertBuyCommandsToDictionary(
        List<Tuple<string, string, string, string, string>> buildInputs)
    {
        var buildSets = new Dictionary<string, Tuple<List<Tuple<string, ushort>>, string, string, string, string>>();

        foreach (var buildInput in buildInputs)
        {
            string buildName = buildInput.Item1.ToLower();
            string buyCommand = buildInput.Item2;
            string description = buildInput.Item3;
            string classType = buildInput.Item4;
            string contributedBy = buildInput.Item5;

            // Convert the ?buy command into the dictionary item format
            var items = ParseBuyCommand(buyCommand);

            // Create a tuple including the items, description, the original buy command, class, and contributedBy
            Tuple<List<Tuple<string, ushort>>, string, string, string, string> buildData = 
                new Tuple<List<Tuple<string, ushort>>, string, string, string, string>(items, description, buyCommand, classType, contributedBy);

            buildSets.Add(buildName, buildData);
        }

        return buildSets;
    }

    /// <summary>
    /// Parses a single ?buy command string and converts it into a list of item tuples.
    /// </summary>
    private List<Tuple<string, ushort>> ParseBuyCommand(string buyCommand)
    {
        var items = new List<Tuple<string, ushort>>();
        var parts = buyCommand.Replace("?buy ", "").Split(',');

        foreach (var part in parts)
        {
            string itemName;
            ushort itemCount = 1;

            var itemParts = part.Split(':');
            itemName = itemParts[0].Trim().ToLower(); // Normalize item name

            if (itemParts.Length > 1)
            {
                string quantitySpecifier = itemParts[1].Trim();
                ushort count;
                if (ushort.TryParse(quantitySpecifier.TrimStart('#'), out count))

                {
                    itemCount = count;
                }
            }

            items.Add(new Tuple<string, ushort>(itemName, itemCount));
        }

        return items;
    }
}

namespace InfServer.Script.GameType_CTF
{
    using MapFlagEntry = Tuple<string, int, int>; // <Flag ID, Tile X, Tile Y>

    /// <summary>
    /// Proxies the Player object to provide CTF-oriented stats.
    /// 
    /// This list of stats maps over to the config file `Name0` through `Name6`
    /// list of stats.
    /// </summary>
    /// 
    /// <remarks>
    /// The player object is proxied because the stats are contained in variables
    /// named `ZoneStat1` through `ZoneStat7`. We want better names that match
    /// what the actual stat is, so we will hide it behind this proxy.
    /// 
    /// Note that we only create one instance for this proxy and then we reassign
    /// the player whenever we want to update; otherwise we'd be doing needless
    /// allocations for a stat update.
    /// 
    /// Ensure after you're  done updating the stat that you set player to null,
    /// that will help to guard any accidental writes.
    /// </remarks>
    class CTFPlayerStatsProxy
    {
        public Player player {get;set;}

        /// <summary>
        /// Gets or sets the number of games this player has won.
        /// </summary>
        public int GamesWon
        {
            get { return player.ZoneStat1; }
            set { player.ZoneStat1 = value; }
        }

        /// <summary>
        /// Gets or esets the number of games this player has lost.
        /// </summary>
        public int GamesLost
        {
            get { return player.ZoneStat2; }
            set { player.ZoneStat2 = value; }
        }

        /// <summary>
        /// Time in seconds that the player has carried at least one flag for.
        /// </summary>
        public int CarryTimeSeconds
        {
            get { return player.ZoneStat3; }
            set { player.ZoneStat3 = value; }
        }

        /// <summary>
        /// Cumulative time in seconds that the player has carried flags.
        /// </summary>
        public int CarryTimeSecondsPlus
        {
            get { return player.ZoneStat4; }
            set { player.ZoneStat4 = value; }
        }

        /// <summary>
        /// Number of times that a player has captured a flag - from actual pickup/killing a carrier and picking their flag up.
        /// </summary>
        public int Captures
        {
            get { return player.ZoneStat5; }
            set { player.ZoneStat5 = value; }
        }

        /// <summary>
        /// Amount of times a flag carrier gets a kill.
        /// </summary>
        public int CarryKills
        {
            get { return player.ZoneStat6; }
            set { player.ZoneStat6 = value; }
        }

        /// <summary>
        /// Amount of times a player kills a flag carrier.
        /// </summary>
        public int CarrierKills
        {
            get { return player.ZoneStat7; }
            set { player.ZoneStat7 = value; }
        }
    }

    /// <summary>
    /// Models a single CTF map (i.e. playable area) with specific teams and flag coordinates.
    /// </summary>
    /// <remarks>
    /// Consider doing this properly and loading it from a file you lazy bums.
    /// </remarks>
    class CTFMap
    {
        public string MapName { get; set; }

        /// <summary>
        /// If set to true, the coordinates of the flags are randomized and the given positions are ignored and only the Flag ID
        /// is used.
        /// </summary>
        public bool RandomizeFlagLocations = false;

        public List<string> TeamNames = new List<string>();

        /// <summary>
        /// List of flags for this map. coordinates must be multiplied by 16 as per the actual in-game coordinates (coord specified * 16).
        /// </summary>
        public List<MapFlagEntry> Flags = new List<MapFlagEntry>();
    }

    //////////////////////////////////////////////////////
    // Script class
    // Provides the interface between the script and arena
    //////////////////////////////////////////////////////
    class Script_CTF : Scripts.IScript
    {
        #region Member Variables
        //////////////////////////////////////////////////
        // Member Variables
        //////////////////////////////////////////////////
        private Arena arena;
        private CfgInfo CFG;
        private int lastGameCheck;
        private int lastStatsWriteMs;

        private int minPlayers;
        private int preGamePeriod;

        private Team winningTeam;
        private int winningTeamTick;
        private int winningTeamNotify;
        private int victoryHoldTime;
        private bool gameWon;

        private GameState gameState;
        private CTFMode flagMode;

        // Create only one so that we aren't doing needless allocations all the time.
        private CTFPlayerStatsProxy ctfPlayerProxy = new CTFPlayerStatsProxy();

        private bool isOVD = false;
        private bool is5v5 = false;
        private string winningTeamOVD = "defense";
        private string baseUsed = "Unknown";
        private bool isSD = false;
        private Team notPlaying;
        private Team playing;
        private Team spec;
        private List<Arena.FlagState> _flags;

        private Dictionary<string, Base> bases;

        private List<CTFMap> availableMaps = new List<CTFMap>();

        private CTFMap currentMap = null;

        private CommandHandler commandHandler = new CommandHandler();
        private bool allowPrivateTeams = false;
        private int overtimeStart = 0;
        private long secondOvertimeStart = 0;
        private bool isSecondOvertime = false;

        private bool isChampEnabled = true;

        private int secondOvertimeTimer;
        private Dictionary<Player, bool> disallowClassChange = new Dictionary<Player, bool>();

        private class Base
        {
            public Base(short posX, short posY, short fposX, short fposY)
            {
                x = (short)(posX * 16);
                y = (short)(posY * 16);

                flagX = (short)(fposX * 16);
                flagY = (short)(fposY * 16);

            }
            public short x;
            public short y;
            public short flagX;
            public short flagY;
        }

        /// <summary>
        /// Stores our player streak information
        /// </summary>
        private class PlayerStreak
        {
            public ItemInfo.Projectile lastUsedWeap { get; set; }
            public int lastUsedWepKillCount { get; set; }
            public long lastUsedWepTick { get; set; }
            public int lastKillerCount { get; set; }
            public int deathCount { get; set; }
            public int killCount { get; set; }
        }

        public enum EventType
        {
            None,
            Standard,
            KOTH,
            Zombie,
            Gladiator,
            CTFX,
            MiniTP,
            SUT
            // Add other event types as needed
        }

        // If arena 1, MiniTP, otherwise None   
        private EventType currentEventType = EventType.None;
        private List<Player> gladiatorPlayers = new List<Player>();
        private bool gladiatorUpgradesEnabled = true; // Toggle for upgrade system
        public Team gladiatorTeamA;
        public Team gladiatorTeamB;
        private const int gladiatorKillThreshold = 20;
        private List<int[]> teamASpawnPoints = new List<int[]>
        {
            new int[] { 500, 500 },
            // Add more spawn points for Team A
        };

        private List<int[]> teamBSpawnPoints = new List<int[]>
        {
            new int[] { 510, 510 },
            // Add more spawn points for Team B
        };   

        private void WarpPlayerToSpawn(Player player)
        {
            int[] spawnPoint;

            if (player._team == gladiatorTeamA)
            {
                // Use modulo to ensure we have a matching index
                int index = gladiatorTeamA.ActivePlayerCount % teamASpawnPoints.Count;
                spawnPoint = teamASpawnPoints[index];
            }
            else if (player._team == gladiatorTeamB)
            {
                int index = gladiatorTeamB.ActivePlayerCount % teamBSpawnPoints.Count;
                spawnPoint = teamBSpawnPoints[index];
            }
            else
            {
                // Default spawn point
                spawnPoint = new int[] { 900, 509 };
            }

            // Warp player
            player.warp(spawnPoint[0] * 16, spawnPoint[1] * 16);
        }

        private void StartGladiatorEvent()
        {
            arena.sendArenaMessage("Gladiator event has started! Type ?glad to join.", 1);
            // Initialize any necessary variables
            gladiatorPlayers.Clear();
            // Optionally, set up initial spawn points or other settings
        }

        private void JoinGladiatorEvent(Player player)
        {
            // Iterate through teams to find an empty one (teams 2 to 33)
            for (int i = 2; i <= 33; i++)
            {
                string teamName = CFG.teams[i].name;
                Team team = player._arena.getTeamByName(teamName);

                // Check if the team exists and is empty
                if (team != null && team.ActivePlayerCount == 0)
                {
                    // Assign the player to the empty team
                    AssignPlayerToTeam(player, "Infantry", teamName, false, true);
                    
                    // Warp the player to the exact coordinates (756, 533) scaled by 16
                    WarpPlayerToSpawn(player);
                    break;
                }
            }
        }

        private void CheckGladiatorVictory()
        {
            Player winningPlayer = null;
            foreach (int i in Enumerable.Range(2, 32)) // Teams 2 to 33 inclusive
            {
                string teamName = CFG.teams[i].name;
                Team team = arena.getTeamByName(teamName);

                if (team != null)
                {
                    int teamKills = team._currentGameKills;

                    if (teamKills >= gladiatorKillThreshold)
                    {
                        // Find a player from the winning team to launch fireworks
                        foreach (Player player in team.ActivePlayers)
                        {
                            winningPlayer = player; // Store the first player found as the winning player
                            break; // Exit the loop after finding the first player
                        }

                        arena.sendArenaMessage(string.Format("{0} has won the Gladiator event!", team._name));
                        LaunchFireworks(winningPlayer); // Launch fireworks for the winning player
                        EndEvent();
                        return;
                    }
                }
            }
        }

private void CheckSUTVictory()
{
    foreach (Team team in arena.Teams)
    {
        if (team._currentGameKills >= 50)
        {
            arena.sendArenaMessage(string.Format("{0} has won the SUT event with {1} kills!", team._name, team._currentGameKills));

            // Launch fireworks for all players on the winning team
            foreach (Player player in team.ActivePlayers)
            {
                LaunchFireworks(player);
            }

            EndEvent();
            return;
        }
    }
}
// Class to store player and vehicle state information
private class PlayerState
{
    public short PosX { get; set; }
    public short PosY { get; set; }
    public byte Yaw { get; set; }
    public ushort Direction { get; set; }
    public short VelocityX { get; set; }
    public short VelocityY { get; set; }
    public short Energy { get; set; }
    public short Health { get; set; }
    public Dictionary<string, int> ItemCounts { get; set; }
    public long DeathTime { get; set; }
    public bool IsOnVehicle { get; set; } // Track if the player was on a vehicle
    public int VehicleId { get; set; } // Track the vehicle ID
    public string Skill { get; set; } // Added property to store player's skill

    public PlayerState()
    {
        ItemCounts = new Dictionary<string, int>();
        Skill = "";
    }
}

// Class to store vehicle state information
private class VehicleState
{
    public short PosX { get; set; }
    public short PosY { get; set; }
    public byte Yaw { get; set; }
    public short VelocityX { get; set; }
    public short VelocityY { get; set; }
    public short Energy { get; set; }
    public short Health { get; set; }
    public ushort Direction { get; set; }
    public bool IsDestroyed { get; set; } // Track if the vehicle was destroyed
    public int VehicleTypeId { get; set; } // Store the vehicle type ID
    public Team Team { get; set; } // Store the team
}

// Dictionary to store multiple saved game states
// Key: state name, Value: Dictionary of player states
private Dictionary<string, Dictionary<string, PlayerState>> savedGameStates = new Dictionary<string, Dictionary<string, PlayerState>>();
// Dictionary to store multiple saved flag states
// Key: state name, Value: Dictionary of flag states
private Dictionary<string, Dictionary<int, Arena.FlagState>> savedFlagStates = new Dictionary<string, Dictionary<int, Arena.FlagState>>();

// Dictionary to store multiple saved vehicle states
// Key: state name, Value: Dictionary of vehicle states
private Dictionary<string, Dictionary<int, VehicleState>> savedVehicleStates = new Dictionary<string, Dictionary<int, VehicleState>>();

// Dictionary to store previously loaded vehicles to prevent recreation
private Dictionary<Tuple<string, int>, Vehicle> loadedVehicles = new Dictionary<Tuple<string, int>, Vehicle>();

// Variables for auto-save functionality
private int lastAutoSaveTime = 0;
private const int AUTO_SAVE_INTERVAL = 30000; // 30 seconds in milliseconds

private readonly string[] itemsToTrack = new string[]
{
    "energizer", "stim pack", "repulsor coil", "repulsor charge",
    "ammo rifle", "ammo shotgun", "ammo pistol", "light he",
    "heavy he", "rocket", "ammo mg", "frag grenade", "wp grenade",
    "haywire grenade", "emp grenade", "teleport beacon", "tranq",
    "bullet mine", "maklov rg 2", "kuchler rg 249", "titan rg 2mv",
    "fuel canister", "tsolvy crystals", "titanium oxide", "pandoras element",
    "ap mine", "plasma mine", "grapeshot mine", "remote mine"
};

private bool isAutoSaving = false;
private System.Threading.Timer autoSaveTimer;

private string currentLoadedState = null;

/// <summary>
/// Loads the next available state in chronological order
/// </summary>
private void LoadNextState(Player player)
{
    if (savedGameStates.Count == 0)
    {
        player.sendMessage(-1, "No saved states available.");
        return;
    }

    // Get all state names and sort them chronologically
    var states = savedGameStates.Keys.OrderBy(s =>
    {
        string[] parts = s.Split(':');
        int min = 0;
        int sec = 0;
        if (parts.Length == 2 && int.TryParse(parts[0], out min) && int.TryParse(parts[1], out sec))
        {
            return min * 60 + sec;
        }
        return 0;
    }).ToList();

    // Find index of current state
    int currentIndex = currentLoadedState == null ? -1 : states.IndexOf(currentLoadedState);
    
    // Get next state (wrap around to beginning if at end)
    int nextIndex = (currentIndex + 1) % states.Count;
    string nextState = states[nextIndex];
    
    // Load the next state
    LoadState(nextState);
    currentLoadedState = nextState;

    // Get the state that would be next after this one
    string followingState = states[(nextIndex + 1) % states.Count];
    
    // Notify player
    player.sendMessage(0, string.Format("Loaded state '{0}'. Next available state: '{1}'", nextState, followingState));
}

/// <summary>
/// Checks if it's time for an auto-save and performs it if needed
/// </summary>
private void CheckAutoSave()
{
    if (!isAutoSaving)
        return;

    // Calculate game time in milliseconds
    int gameTimeMs = Environment.TickCount - arena._tickGameStarted;
    
    // Calculate minutes and seconds based on game time
    int totalSeconds = gameTimeMs / 1000;
    int minutes = totalSeconds / 60;
    int seconds = totalSeconds % 60;
    
    // Round down to nearest 30 second mark
    seconds = (seconds / 30) * 30;
    
    // Format the save state name (e.g., "2:30" for 2 minutes 30 seconds)
    string stateName = string.Format("{0}:{1:D2}", minutes, seconds);
    
    // Perform the save
    SaveState(stateName);
}

/// <summary>
/// Starts or stops the auto-save timer based on command
/// </summary>
private void ToggleAutoSave(Player player, bool enable)
{
    if (enable && !isAutoSaving)
    {
        // Start the auto-save timer
        isAutoSaving = true;
        lastAutoSaveTime = Environment.TickCount;

        // Save initial state at 0:00
        SaveState("0:00");

        // Calculate time until next 30 second mark
        int gameTimeMs = Environment.TickCount - arena._tickGameStarted;
        int msUntilNext30 = AUTO_SAVE_INTERVAL - (gameTimeMs % AUTO_SAVE_INTERVAL);

        autoSaveTimer = new System.Threading.Timer(
            _ => CheckAutoSave(),
            null,
            msUntilNext30, // Initial delay until next 30 second mark
            AUTO_SAVE_INTERVAL
        );
        player.sendMessage(0, "Auto-save enabled. Saving every 30 seconds.");
    }
    else if (!enable && isAutoSaving)
    {
        // Stop the auto-save timer
        isAutoSaving = false;
        autoSaveTimer.Dispose();
        autoSaveTimer = null;
        player.sendMessage(0, "Auto-save disabled.");
    }
}

/// <summary>
/// Saves the current state of all players and vehicles in the arena with the given state name
/// </summary>
private void SaveState(string stateName)
{
    if (string.IsNullOrEmpty(stateName))
    {
        arena.sendArenaMessage("State name cannot be empty.");
        return;
    }

    int respawnDelay = CFG.timing.enterDelay;
    Dictionary<string, PlayerState> stateDict = new Dictionary<string, PlayerState>();
    Dictionary<int, VehicleState> vehicleStateDict = new Dictionary<int, VehicleState>();
    Dictionary<int, Arena.FlagState> flagStateDict = new Dictionary<int, Arena.FlagState>();

    // Store all flag states
    foreach (Arena.FlagState flag in arena._flags.Values)
    {
        if (flag == null)
            continue;

        // Create a deep copy of the flag state
        Arena.FlagState flagCopy = new Arena.FlagState();
        flagCopy.flag = flag.flag;
        flagCopy.team = flag.team;
        flagCopy.posX = flag.posX;
        flagCopy.posY = flag.posY;
        flagCopy.oldPosX = flag.oldPosX;
        flagCopy.oldPosY = flag.oldPosY;
        flagCopy.bActive = flag.bActive;
        flagCopy.carrier = flag.carrier;

        flagStateDict[flag.flag.GeneralData.Id] = flagCopy;
    }

    foreach (Player player in arena.Players)
    {
        if (player == null || player.IsSpectator)
            continue;

        PlayerState state = new PlayerState
        {
            PosX = player._state.positionX,
            PosY = player._state.positionY,
            Yaw = player._state.yaw,
            VelocityX = player._state.velocityX,
            VelocityY = player._state.velocityY,
            Energy = player._state.energy,
            Health = player._state.health,
            Direction = (ushort)player._state.direction,
            DeathTime = player._deathTime,
            IsOnVehicle = player._occupiedVehicle != null,
            VehicleId = player._occupiedVehicle != null ? player._occupiedVehicle._type.Id : -1,
            Skill = GetPrimarySkillName(player)
        };

        if (player.IsDead)
        {
            int deathDuration = Environment.TickCount - player._deathTime;
            state.DeathTime = respawnDelay - deathDuration;
        }
        else
        {
            state.DeathTime = 0;
        }

        // Save all items in inventory
        foreach (var item in player._inventory)
        {
            if (item.Value != null && item.Value.item != null)
            {
                string itemName = item.Value.item.name;
                int count = item.Value.quantity;
                state.ItemCounts[itemName] = count;
            }
        }

        stateDict[player._alias] = state;
    }

    // Save vehicle states
    foreach (Vehicle vehicle in arena.Vehicles)
    {
        if (vehicle == null)
            continue;

        VehicleState state = new VehicleState
        {
            PosX = vehicle._state.positionX,
            PosY = vehicle._state.positionY,
            Yaw = vehicle._state.yaw,
            VelocityX = vehicle._state.velocityX,
            VelocityY = vehicle._state.velocityY,
            Energy = vehicle._state.energy,
            Health = vehicle._state.health,
            Direction = (ushort)vehicle._state.direction,
            IsDestroyed = vehicle._state.health <= 0,
            VehicleTypeId = vehicle._type.Id,
            Team = vehicle._team
        };

        vehicleStateDict[vehicle._id] = state;
    }

    // Store the state dictionaries under the provided state name
    savedGameStates[stateName] = stateDict;
    savedVehicleStates[stateName] = vehicleStateDict;
    savedFlagStates[stateName] = flagStateDict;
    if (stateName == "0:00")
    {
        arena.sendArenaMessage("Game states will auto-save every 30 seconds (e.g. 0:00, 0:30, 1:00, 1:30, etc)");
    }
}

/// <summary>
/// Loads the previously saved state for all players and vehicles with the given state name
/// </summary>
private void LoadState(string stateName)
{
    int respawnDelay = CFG.timing.enterDelay;
    if (string.IsNullOrEmpty(stateName))
    {
        arena.sendArenaMessage("State name cannot be empty.");
        return;
    }

    if (!savedGameStates.ContainsKey(stateName) || !savedVehicleStates.ContainsKey(stateName))
    {
        arena.sendArenaMessage(string.Format("No saved state found with the name '{0}'.", stateName));
        return;
    }

    Dictionary<string, PlayerState> savedGameState = savedGameStates[stateName];
    Dictionary<int, VehicleState> savedVehicleState = savedVehicleStates[stateName];
    Dictionary<int, Arena.FlagState> savedFlagState = savedFlagStates[stateName];

    // Load all flag states
    foreach (var kvp in savedFlagState)
    {
        int flagId = kvp.Key;
        Arena.FlagState savedFlag = kvp.Value;
        Arena.FlagState currentFlag = arena._flags.Values.FirstOrDefault(f => f.flag.GeneralData.Id == flagId);
        
        if (currentFlag != null)
        {
            currentFlag.team = savedFlag.team;
            currentFlag.posX = savedFlag.posX;
            currentFlag.posY = savedFlag.posY;
            currentFlag.oldPosX = savedFlag.oldPosX;
            currentFlag.oldPosY = savedFlag.oldPosY;
            currentFlag.bActive = savedFlag.bActive;
            currentFlag.carrier = savedFlag.carrier;

            // Update flag visually for all players
            Helpers.Object_Flags(arena.Players, currentFlag);
        }
    }

    foreach (Player player in arena.Players)
    {
        if (player == null || player.IsSpectator || !savedGameState.ContainsKey(player._alias))
        {
            continue;
        }

        PlayerState state = savedGameState[player._alias];

        // Create an ObjectState with the saved position and state
        Helpers.ObjectState newState = new Helpers.ObjectState
        {
            positionX = state.PosX,
            positionY = state.PosY,
            positionZ = 0,
            yaw = state.Yaw,
            velocityX = state.VelocityX,
            velocityY = state.VelocityY,
            energy = state.Energy,
            health = state.Health,
            direction = (Helpers.ObjectState.Direction)state.Direction
        };
        ChangePlayerSkill(player, state.Skill);
        player.resetWarp();
        player.resetState(false, false, false);

        // Warp the player to restore position and state
        player.warp(Helpers.ResetFlags.ResetAll, newState, state.Health, state.Energy, (byte)state.Yaw);

        // Check if the player was on a vehicle and restore the vehicle state
        if (state.IsOnVehicle && state.VehicleId != -1)
        {
            Vehicle vehicleToOccupy = player._arena.Vehicles.FirstOrDefault(v => v._type.Id == state.VehicleId);
            if (vehicleToOccupy != null)
            {
                if (vehicleToOccupy._inhabitant == null)
                {
                    player.enterVehicle(vehicleToOccupy);
                    // Directly update the vehicle's state after entering
                    vehicleToOccupy._state.positionX = state.PosX;
                    vehicleToOccupy._state.positionY = state.PosY;
                    vehicleToOccupy._state.velocityX = state.VelocityX;
                    vehicleToOccupy._state.velocityY = state.VelocityY;
                    vehicleToOccupy._state.yaw = state.Yaw;
                    vehicleToOccupy._state.direction = (Helpers.ObjectState.Direction)state.Direction;
                    vehicleToOccupy._state.energy = state.Energy;
                    vehicleToOccupy._state.health = state.Health;
                }
            }
        }

        // After warping, directly update the player's vehicle state
        Vehicle vehicle = player._occupiedVehicle ?? player._baseVehicle;
        if (vehicle != null)
        {
            vehicle._state.positionX = state.PosX;
            vehicle._state.positionY = state.PosY;
            vehicle._state.velocityX = state.VelocityX;
            vehicle._state.velocityY = state.VelocityY;
            vehicle._state.yaw = state.Yaw;
            vehicle._state.direction = (Helpers.ObjectState.Direction)state.Direction;
            vehicle._state.energy = state.Energy;
            vehicle._state.health = state.Health;

            // Check if the player was dead when the state was saved
            if (state.Health <= 0)
            {
                // Simulate death
                vehicle._state.health = 0;
                vehicle._tickDead = Environment.TickCount - (int)(Environment.TickCount - state.DeathTime);
                player._deathTime = vehicle._tickDead;
                vehicle.kill(null);
            }
            else
            {
                // Player was alive, restore health
                vehicle._state.health = state.Health;
                vehicle._tickDead = 0;
            }
        }

        if (state.IsOnVehicle && state.VehicleId != -1)
        {
            Vehicle vehicleToOccupy = player._arena.Vehicles.FirstOrDefault(v => v._type.Id == state.VehicleId);
            if (vehicleToOccupy != null)
            {
                if (vehicleToOccupy._inhabitant == null)
                {
                    player.enterVehicle(vehicleToOccupy);
                    // Restore the velocity of the vehicle after entering it
                    vehicleToOccupy._state.velocityX = state.VelocityX;
                    vehicleToOccupy._state.velocityY = state.VelocityY;
                }
            }
        }

        // Warp the player to restore position and state again
        player.warp(Helpers.ResetFlags.ResetAll, newState, state.Health, state.Energy, (byte)state.Yaw);

        if (vehicle != null)
        {
            vehicle._state.positionX = state.PosX;
            vehicle._state.positionY = state.PosY;
            vehicle._state.velocityX = state.VelocityX;
            vehicle._state.velocityY = state.VelocityY;
            vehicle._state.yaw = state.Yaw;
            vehicle._state.direction = (Helpers.ObjectState.Direction)state.Direction;
            vehicle._state.energy = state.Energy;
            vehicle._state.health = state.Health;

            if (state.Health <= 0)
            {
                vehicle._state.health = 0;
                vehicle._tickDead = Environment.TickCount - (int)(Environment.TickCount - state.DeathTime);
                player._deathTime = vehicle._tickDead;
                vehicle.kill(null);
            }
            else
            {
                vehicle._state.health = state.Health;
                vehicle._tickDead = 0;
                player._deathTime = 0;
            }

            vehicle.update(false);

            SC_PlayerUpdate stateUpdate = new SC_PlayerUpdate
            {
                tickUpdate = Environment.TickCount,
                player = player,
                vehicle = vehicle,
                itemID = 0, // No item used
                bBot = false,
                activeEquip = null
            };

            stateUpdate.vehicle._state = vehicle._state;
            player._client.sendReliable(stateUpdate);
        }
        else
        {
            player.sendMessage(-1, "Error: Unable to update vehicle state because vehicle is null.");
        }

        // Clear current inventory
        player._inventory.Clear();

        // Restore all saved items
        foreach (var itemPair in state.ItemCounts)
        {
            ItemInfo item = AssetManager.Manager.getItemByName(itemPair.Key);
            if (item != null)
            {
                player.inventoryModify(item, (ushort)itemPair.Value);
            }
        }

        player.syncInventory();
        player.syncState();
    }

    // Remove any computer vehicles that were created after the save state
    foreach (Vehicle v in arena.Vehicles.ToList())
    {
        if (v != null && !savedVehicleState.ContainsKey(v._id))
        {
            v.kill(null);
        }
    }

    // Restore computer vehicle states
    foreach (KeyValuePair<int, VehicleState> kvp in savedVehicleState)
    {
        int savedId = kvp.Key;
        VehicleState state = kvp.Value;

        // Immediately after retrieving VehicleState state and before checking loadedVehicles
        int savedHealth = state.Health;

        // Check if we have a loaded vehicle or one in the arena
        Vehicle vehicle;
        if (!loadedVehicles.TryGetValue(Tuple.Create(stateName, savedId), out vehicle))
        {
            // Attempt to find an existing vehicle by ID
            vehicle = arena.Vehicles.FirstOrDefault(v => v != null && v._id == savedId);
        }

        // If the saved state says the vehicle should be alive (health > 0) but we either have no vehicle or a dead one, recreate it
        if (savedHealth > 0 && (vehicle == null || vehicle._state.health <= 0))
        {
            // Remove any stale reference
            loadedVehicles.Remove(Tuple.Create(stateName, savedId));

            VehInfo vehicleInfo = AssetManager.Manager.getVehicleByID(state.VehicleTypeId);
            if (vehicleInfo != null)
            {
                Helpers.ObjectState objState = new Helpers.ObjectState
                {
                    positionX = state.PosX,
                    positionY = state.PosY,
                    positionZ = 0,
                    yaw = state.Yaw,
                    velocityX = state.VelocityX,
                    velocityY = state.VelocityY,
                    energy = state.Energy,
                    health = state.Health,
                    direction = (Helpers.ObjectState.Direction)state.Direction
                };

                // Recreate the vehicle since it should exist at this saved state
                vehicle = arena.newVehicle(vehicleInfo, state.Team, null, objState);
                loadedVehicles[Tuple.Create(stateName, savedId)] = vehicle;

                // Force an update to sync the vehicle state immediately
                if (vehicle is Computer)
                {
                    Computer comp = (Computer)vehicle;
                    comp._sendUpdate = true;
                    comp.poll(); // This will trigger state updates
                }
            }
        }
        // If the saved state says the vehicle was destroyed (health <= 0) and we currently have one, kill it
        else if (savedHealth <= 0 && vehicle != null)
        {
            vehicle._state.health = 0;
            vehicle.kill(null);
        }

        // If we have a vehicle and it should be alive, update it
        if (vehicle != null && savedHealth > 0)
        {
            vehicle._state.positionX = state.PosX;
            vehicle._state.positionY = state.PosY;
            vehicle._state.velocityX = state.VelocityX;
            vehicle._state.velocityY = state.VelocityY;
            vehicle._state.yaw = state.Yaw;
            vehicle._state.direction = (Helpers.ObjectState.Direction)state.Direction;
            vehicle._state.energy = state.Energy;
            vehicle._state.health = state.Health;

            // Force an immediate update for Computer vehicles
            if (vehicle is Computer)
            {
                Computer comp = (Computer)vehicle;
                comp._sendUpdate = true;
                comp.poll(); // This will trigger state updates
            }
            else
            {
                vehicle.update(false);
            }
        }
    }

    arena.sendArenaMessage(string.Format("Game state '{0}' has been loaded.", stateName));
}

        
        private void ChangePlayerSkill(Player player, string skillName)
        {
            // Get the skill by name
            SkillInfo newSkill = AssetManager.Manager.getSkillByName(skillName);

            if (newSkill == null)
            {
                player.sendMessage(-1, string.Format("Skill '{0}' not found.", skillName));
                return;
            }

            // Set the player's default vehicle to the vehicle associated with the skill
            if (newSkill.DefaultVehicleId > 0) // Check if the skill has an associated default vehicle
            {
                player.setDefaultVehicle(AssetManager.Manager.getVehicleByID(newSkill.DefaultVehicleId));
            }

            player._skills.Clear();

            // Create a new SkillItem and set its properties
            Player.SkillItem newSkillItem = new Player.SkillItem
            {
                skill = newSkill
            };

            // Add the new skill to the player's skills
            player._skills.Add(newSkill.SkillId, newSkillItem);

            // Synchronize player state to reflect the new skill and vehicle
            player.syncState();

            // Notify the player that the skill has been changed
            //player.sendMessage(0, string.Format("Your skill has been changed to {0}.", skillName));
        }

        // Method to update death count and take action
        private void UpdateDeathCount(Player player)
        {
            if (killStreaks.ContainsKey(player._alias))
            {
                killStreaks[player._alias].deathCount++;

                int deathCount = killStreaks[player._alias].deathCount;

                // Check if the death count reaches the threshold
                if (currentEventType == EventType.KOTH && deathCount >= 3)
                {
                    // Move the player to spectator mode
                    player.spec("You have been moved to spectator mode after 3 deaths.");

                    // Optionally, send a global message
                    arena.sendArenaMessage(string.Format("{0} has been moved to spectator mode after reaching 3 deaths.", player._alias));
                }
            }
        }

        private void deprizeMinPremades(Player player, bool applySkillExceptions, bool showMessage = false)
        {
            string playerSkill = GetPrimarySkillName(player);
            
            // First remove all items from the player
            HashSet<int> itemsToDeprive = new HashSet<int> { 2005, 2007, 2009, 2, 9, 10, 11 };
            List<string> deprivedItems = new List<string>();

            foreach (int itemID in itemsToDeprive)
            {
                if (applySkillExceptions)
                {
                    if ((playerSkill == "Combat Engineer" && itemID == 2009) ||
                        (playerSkill == "Field Medic" && (itemID == 2007 || itemID == 2005)))
                    {
                        continue;
                    }
                }

                int currentCount = player.getInventoryAmount(itemID);
                if (currentCount > 0)
                {
                    ItemInfo item = player._server._assets.getItemByID(itemID);
                    if (item != null)
                    {
                        player.inventoryModify(true, item, -currentCount);
                        deprivedItems.Add(string.Format("{0} {1}", currentCount, item.name));
                    }
                }
            }

            if (showMessage)
            {
                if (deprivedItems.Count > 0)
                {
                    string itemsList = string.Join(", ", deprivedItems);
                    arena.sendArenaMessage(string.Format("*{0} deprized {1}.", player._alias, itemsList));
                }
                else
                {
                    //player.sendMessage(0, "No players had minerals or premades to deprize.");
                }
            }
        }

        private void CheckArenaItemProfile(Player sender)
        {
            HashSet<int> itemsToCheck = new HashSet<int> { 2005, 2007, 2009, 2, 9, 10, 11 };
            bool anyFound = false;

            foreach (Player p in arena.Players)
            {
                List<string> itemCounts = new List<string>();
                
                foreach (int itemID in itemsToCheck)
                {
                    int count = p.getInventoryAmount(itemID);
                    if (count > 0)
                    {
                        ItemInfo item = p._server._assets.getItemByID(itemID);
                        if (item != null)
                        {
                            itemCounts.Add(string.Format("{0} {1}", count, item.name));
                        }
                    }
                }

                if (itemCounts.Count > 0)
                {
                    anyFound = true;
                    string itemsList = string.Join(", ", itemCounts);
                    sender.sendMessage(0, string.Format("Player {0} has: {1}", p._alias, itemsList));
                }
            }
            
            if (!anyFound)
            {
                sender.sendMessage(0, "No minerals or premades were found on any players.");
            }
        }

        // Method to update kill count and reward player
        private void UpdateKillCountAndReward(Player killer)
        {
            if (killStreaks.ContainsKey(killer._alias))
            {
                killStreaks[killer._alias].killCount++;

                int killCount = killStreaks[killer._alias].killCount;

                // Grant a reward every 5 kills
                if (killCount % 5 == 0)
                {
                    ItemInfo rewardItem = AssetManager.Manager.getItemByName("Special Ammo");
                    if (rewardItem != null)
                    {
                        killer.inventoryModify(rewardItem, 10); // Grant 10 units of Special Ammo
                        killer.sendMessage(0, "You have been awarded 10 Special Ammo for 5 kills!");
                    }
                }
            }
        }

        public void StartEvent(EventType eventType)
        {
            if (currentEventType == EventType.None || currentEventType == eventType)
            {
                currentEventType = eventType;

                switch (eventType)
                {
                    case EventType.KOTH:
                        // Initialize King of the Hill
                        //RelocateFlags();
                        break;
                    case EventType.Zombie:
                        InitializeZombie();
                        break;
                    case EventType.Gladiator:
                        StartGladiatorEvent();
                        break;
                    case EventType.CTFX:
                        InitializeCTFXEvent();
                        break;
                    case EventType.MiniTP:
                        InitializeMiniTPEvent();
                        break;
                    case EventType.SUT:
                        InitializeSUTEvent();
                        //RelocateFlags();
                        break;
                    // Add other cases
                }

                arena.sendArenaMessage(string.Format("Event '{0}' has started!", eventType));
            }
        }

        public void EndEvent()
        {
            if (currentEventType == EventType.SUT){
                arena.gameEnd();
            }
            if (currentEventType == EventType.Gladiator){
                arena.gameEnd();
            }

            if (currentEventType == EventType.Zombie)
            {
                // Reset all players' skills and assign them the "Infantry" skill
                foreach (Player player in arena.PlayersIngame)
                {
                    ChangePlayerSkill(player, "Infantry");
                    //player.sendMessage(0, "The event has ended. Your skill has been changed to Infantry.");
                }
                arena.gameEnd();
            }
            currentEventType = EventType.None;
            arena.sendArenaMessage("&Event has ended.");
        }

        /// <summary>
        /// Converts specific mine types in a player's inventory to their alternate versions
        /// </summary>
        private void ConvertChampItems(Player player)
        {
            // Check if the inventory is not empty
            if (player._inventory == null || player._inventory.Count == 0)
            {
                player.sendMessage(0, "Your inventory is empty.");
                return;
            }

            // Get the conversion mapping for the player's champion group.
            Dictionary<string, int> conversions = GetChampionConversionsForPlayer(player);
            if (conversions == null)
            {
                player.sendMessage(0, "No champion item conversion available for you.");
                return;
            }

            // Create a list of items to process to avoid modifying collection during enumeration
            var itemsToProcess = player._inventory.Values
                .Where(entry => conversions.ContainsKey(entry.item.name))
                .ToList();

            // Process each item that needs conversion
            foreach (var inventoryEntry in itemsToProcess)
            {
                string itemName = inventoryEntry.item.name;
                int quantity = inventoryEntry.quantity;

                // Get the new item ID for this mine type
                int newItemId = conversions[itemName];

                if (itemName.Equals("SiG Arms m2 AS", StringComparison.OrdinalIgnoreCase) &&
                    player._alias.Equals("anjro", StringComparison.OrdinalIgnoreCase))
                {
                    newItemId = 3073;
                }

                // Remove old item first
                player.inventoryModify(false, inventoryEntry.item, -quantity);

                // Add converted item
                ItemInfo newItem = AssetManager.Manager.getItemByID(newItemId);
                if (newItem != null)
                {
                    player.inventoryModify(newItem, quantity);
                    player.sendMessage(0, string.Format("Item converted to champ item: {0} x{1} to {2}", itemName, quantity, newItem.name));
                }
            }
        }
        
        #region Champion Group Definitions

        // Season 2 Champions & their item conversion mappings
        private static readonly string[] s2Champs = new[]
        {
            "bonds", "Colossal", "gamefreek321", "Half Cut", "juetnihilia", "kal",
            "MysticGohan~", "Nos", "Pistor", "Rue", "S", "Sov", "stoink", "Tactical", "Verb", 
            "victim", "yosh", "Zmn"
        };

        private static readonly Dictionary<string, int> s2Conversions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "Grapeshot Mine", 1276 },
            { "AP Mine", 1274 },
            { "Plasma Mine", 1275 },
            { "Micro Missile Launcher", 3065 },
            { "Tranq", 1264 },
            { "Kuchler A6 CAW", 3069 },
            { "Needler", 1312 },
        };

        // OVDL1 Champions & their item conversion mappings
        private static readonly string[] ovdl1Champs = new[]
        {
            "Angelus", "Herthbul", "Designer", "albert", "Ruler", "grump", "NewJack", "anjro", "CTF[Ref] Designer", "Joe"
        };

        private static readonly Dictionary<string, int> ovdl1Conversions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "SiG Arms m2 AS", 3072 },
            { "Mini Missile Launcher", 3081 },
            { "Micro Missile Launcher", 3079 },
            { "Maklov AC mk2", 3080 },
            { "Kuchler A6 CAW", 3071 },
        };

        #endregion

        // Dictionary to store player preferences for automatic conversion
        private Dictionary<Player, bool> _autoConvertEnabled = new Dictionary<Player, bool>();

        /// <summary>
        /// Returns the item conversion dictionary for the champion group the player belongs to.
        /// Returns null if the player isn’t in any champion group.
        /// </summary>
        private Dictionary<string, int> GetChampionConversionsForPlayer(Player player)
        {
            if (s2Champs.Any(alias => alias.Equals(player._alias, StringComparison.OrdinalIgnoreCase)))
                return s2Conversions;
            if (ovdl1Champs.Any(alias => alias.Equals(player._alias, StringComparison.OrdinalIgnoreCase)))
                return ovdl1Conversions;
            return null;
        }

        /// <summary>
        /// Triggered when a player buys from either shop or ?buy
        /// </summary>
        [Scripts.Event("Shop.Buy")]
        public bool PlayerShop(Player from, ItemInfo item, int quantity)
        {
            // Check if the player belongs to any champion group.
            Dictionary<string, int> conversions = GetChampionConversionsForPlayer(from);
            // debug print conversions
            //arena.sendArenaMessage("Conversions: " + conversions);
            if (conversions != null)

            {
                // arena debug message
                //arena.sendArenaMessage("Player " + from._alias + " is in a champion group, checking auto-convert flag.");
                bool autoEnabled = false;
                // Only process conversion if the player's auto-convert flag exists and is true.
                if (_autoConvertEnabled.TryGetValue(from, out autoEnabled) && autoEnabled && isChampEnabled)
                {
                    from.sendMessage(0, "Auto-convert is enabled for you, processing champion items.");
                    ConvertChampItems(from);
                }
            }
            return true;
        }

        /// <summary>
        /// Handles the ?champ command to toggle automatic item conversion
        /// </summary>
        private void HandleChampCommand(Player player)
        {
            // Verify that the player is in a champion group.
            Dictionary<string, int> conversions = GetChampionConversionsForPlayer(player);
            if (conversions == null)
            {
                player.sendMessage(-1, "You are not authorized to use this command.");
                return;
            }

            // Toggle the auto-convert setting.
            if (_autoConvertEnabled.ContainsKey(player))
            {
                _autoConvertEnabled[player] = !_autoConvertEnabled[player];
                player.sendMessage(0, string.Format("Automatic item conversion has been {0}.",
                    _autoConvertEnabled[player] ? "enabled" : "disabled"));
            }
            else
            {
                _autoConvertEnabled[player] = true;
                player.sendMessage(0, "Automatic item conversion has been enabled.");
            }

            // Immediately convert items if auto-convert was just enabled.
            if (_autoConvertEnabled[player] && isChampEnabled)
            {
                ConvertChampItems(player);
                //player.sendMessage(0, "Items converted to champion versions.");
            }
        }

        private void LaunchFireworks(Player player)
        {
            // Duration of the fireworks display in milliseconds
            int displayDuration = 5000;
            // Interval between each firework launch in milliseconds
            int fireworkInterval = 200;

            // Firework item IDs
            short smallFireworkID = 3041;
            short largeFireworkID = 3040;

            // Random number generator
            Random random = new Random();
            DateTime endTime = DateTime.Now.AddMilliseconds(displayDuration);

            // Declare timer variable first
            System.Threading.Timer fireworkTimer = null;

            // Create timer callback
            fireworkTimer = new System.Threading.Timer((e) =>
            {
                // Check if display time is over
                if (DateTime.Now >= endTime)
                {
                    // Stop the timer
                    fireworkTimer.Dispose();
                    return;
                }

                // Determine if we should launch a small or large firework
                bool launchSmallFirework = random.NextDouble() < 0.7; // 70% chance for small firework

                // Choose the firework item ID based on the random chance
                short fireworkID = launchSmallFirework ? smallFireworkID : largeFireworkID;

                // Generate random positions within a specified radius around the player
                short radius = 100; // Radius around the player
                short angle = (short)(random.Next(360) * 256 / 360); // Random angle in degrees
                short distance = (short)(random.Next(radius + 1)); // Random distance within the radius
                short playerX = player._state.positionX;
                short playerY = player._state.positionY;
                short playerZ = player._state.positionZ;

                // Calculate new position based on angle and distance
                short newX = (short)(playerX + (short)(distance * Math.Cos(angle * Math.PI / 128)));
                short newY = (short)(playerY + (short)(distance * Math.Sin(angle * Math.PI / 128)));
                short newZ = playerZ; // Keep Z position the same

                byte yaw = 0; // Direction, set to 0 if not needed
                ushort creator = 0; // No specific creator, set to 0 or assign as appropriate

                // Send the projectile explosion effect to all players
                Helpers.Player_RouteExplosion(arena.Players, fireworkID, newX, newY, newZ, yaw, creator);

            }, null, 0, fireworkInterval); // Start immediately, repeat every fireworkInterval milliseconds
        }

        private void UpdateTurretOwnership(string flagName, Team newOwner)
        {
            var fs = arena.getFlag(flagName);
            if (fs == null || newOwner == null) return;

            // Identify turrets near the flag location and update ownership
            var turrets = arena.Vehicles.Where(v => v is Computer && 
                                                    Math.Abs(v._state.positionX - fs.posX) < 200 &&
                                                    Math.Abs(v._state.positionY - fs.posY) < 200);

            foreach (var turret in turrets)
            {
                turret._team = newOwner;
                Helpers.Vehicle_ResetState(turret._creator, false, false, false); // Assuming _creator is the player that owns the turret
            }
        }

        public bool InitializeMiniTPEvent()
        {
            BuildMiniTPTurrets();
            if (currentEventType != EventType.MiniTP) return false;
                arena.sendArenaMessage("MiniTP event has started!");

                // Get all flags
                Arena.FlagState flag1 = arena.getFlag("Bridge1");
                Arena.FlagState flag2 = arena.getFlag("Hill201"); 
                Arena.FlagState flag3 = arena.getFlag("Hill86");
                Arena.FlagState flag4 = arena.getFlag("Bridge2");
                Arena.FlagState flag5 = arena.getFlag("Bridge3");
                Arena.FlagState flag6 = arena.getFlag("Flag1");
                Arena.FlagState flag7 = arena.getFlag("Flag2");
                Arena.FlagState flag8 = arena.getFlag("Flag3");
                Arena.FlagState flag9 = arena.getFlag("Flag4");

                // Set Bridge3 flag team and position
                flag5.posX = 1241 * 16;
                flag5.posY = 887 * 16;
                flag5.oldPosX = 1241 * 16;
                flag5.oldPosY = 887 * 16;
                flag5.bActive = true;
                Helpers.Object_Flags(arena.Players, flag5);

                flag1.team = arena.Teams.FirstOrDefault(t => t._name.Equals("Collective"));
                flag1.posX = 986 * 16;  
                flag1.posY = 651 * 16;
                flag1.oldPosX = 986 * 16;
                flag1.oldPosY = 651 * 16;
                flag1.bActive = true;
                Helpers.Object_Flags(arena.Players, flag1);

                flag2.team = arena.Teams.FirstOrDefault(t => t._name.Equals("Collective"));
                flag2.posX = 965 * 16;
                flag2.posY = 651 * 16;
                flag2.oldPosX = 965 * 16;
                flag2.oldPosY = 651 * 16;
                flag2.bActive = true;
                Helpers.Object_Flags(arena.Players, flag2);

                flag3.team = arena.Teams.FirstOrDefault(t => t._name.Equals("Titan Militia"));
                flag3.posX = 1077 * 16;
                flag3.posY = 624 * 16;
                flag3.oldPosX = 1077 * 16;
                flag3.oldPosY = 624 * 16;
                flag3.bActive = true;
                Helpers.Object_Flags(arena.Players, flag3);

                flag4.team = arena.Teams.FirstOrDefault(t => t._name.Equals("Titan Militia"));
                flag4.posX = 1073 * 16;
                flag4.posY = 628 * 16;
                flag4.oldPosX = 1073 * 16;
                flag4.oldPosY = 628 * 16;
                flag4.bActive = true;
                Helpers.Object_Flags(arena.Players, flag4);

                // Set up new Flag1-4
                flag6.team = arena.Teams.FirstOrDefault(t => t._name.Equals("Collective"));
                flag6.posX = 986 * 16;
                flag6.posY = 651 * 16;
                flag6.oldPosX = 986 * 16;
                flag6.oldPosY = 651 * 16;
                flag6.bActive = true;
                Helpers.Object_Flags(arena.Players, flag6);

                flag7.team = arena.Teams.FirstOrDefault(t => t._name.Equals("Collective"));
                flag7.posX = 965 * 16;
                flag7.posY = 651 * 16;
                flag7.oldPosX = 965 * 16;
                flag7.oldPosY = 651 * 16;
                flag7.bActive = true;
                Helpers.Object_Flags(arena.Players, flag7);

                flag8.team = arena.Teams.FirstOrDefault(t => t._name.Equals("Titan Militia"));
                flag8.posX = 1077 * 16;
                flag8.posY = 624 * 16;
                flag8.oldPosX = 1077 * 16;
                flag8.oldPosY = 624 * 16;
                flag8.bActive = true;
                Helpers.Object_Flags(arena.Players, flag8);

                flag9.team = arena.Teams.FirstOrDefault(t => t._name.Equals("Titan Militia"));
                flag9.posX = 1073 * 16;
                flag9.posY = 628 * 16;
                flag9.oldPosX = 1073 * 16;
                flag9.oldPosY = 628 * 16;
                flag9.bActive = true;
                Helpers.Object_Flags(arena.Players, flag9);

                // Spawn all players
                foreach (Player player in arena.Players)
                {
                    if (player._team._name.Contains("Titan"))
                        WarpPlayerToRange(player, 654, 654, 518, 518, 0);
                    else if (player._team._name.Contains("Collective"))
                        WarpPlayerToRange(player, 648, 648, 565, 565, 0);
                }

                arena.sendArenaMessage("Game has started!", CFG.flag.resetBong);
                return true;
        }

        public void InitializeCTFXEvent()
        {
            if (currentEventType != EventType.CTFX) return;

            // Spawn CTFX flags
            if (!SpawnCTFXFlags())
            {
                Log.write(TLog.Warning, "Failed to spawn CTFX flags.");
            }

            // Adjust private teams
            AdjustPrivateTeamsBasedOnPlayerCount();
            arena.sendArenaMessage("Game has started!", CFG.flag.resetBong);

            // Initialize turrets near each flag
            // foreach (var flag in arena._flags)
            // {
            //     if (flag.bActive)
            //     {
            //         UpdateTurretOwnership(flag.GeneralData.Name, flag.team);
            //     }
            // }
        }

//SUT
public bool InitializeSUTEvent()
{
    if (currentEventType != EventType.SUT) return false;


    // Get all players who aren't spectating
    List<Player> activePlayers = arena.PlayersIngame.ToList();

    // Assign teams using our new system
    AssignTeamsForSUT(activePlayers);

    // Enable private teams and
    allowPrivateTeams = true;

    // Set victory conditions
    arena.sendArenaMessage("First team to 50 kills wins!");
    
    return true;
}

private void AssignTeamsForSUT(List<Player> players)
{
    // Ensure that players list is not empty
    if (players.Count == 0) return;

    // Get the arena from any player
    Arena arena = players[0]._arena;

    // Define team name suffixes
    string[] suffixes = { "Renegades", "Sentinels", "Warriors", "Guardians", "Raiders", 
                         "Vanguards", "Avengers", "Titans", "Commandos", "Strikers" };
    Random rand = new Random();

    // Calculate how many teams we need (max 3 players per team)
    int teamCount = (int)Math.Ceiling(players.Count / 3.0);
    List<List<Player>> teams = new List<List<Player>>();
    for (int i = 0; i < teamCount; i++)
    {
        teams.Add(new List<Player>());
    }

    // Randomly distribute players across teams
    List<Player> allPlayers = new List<Player>(players);
    int currentTeam = 0;

    while (allPlayers.Count > 0)
    {
        // Get a random player
        int index = rand.Next(allPlayers.Count);
        Player player = allPlayers[index];
        allPlayers.RemoveAt(index);

        // Add to current team
        teams[currentTeam].Add(player);

        // Move to next team (round-robin)
        currentTeam = (currentTeam + 1) % teamCount;
    }

    // Create and assign teams
    for (int i = 0; i < teams.Count; i++)
    {
        var teamPlayers = teams[i];
        if (teamPlayers.Count == 0) continue;

        // Generate team name
        string teamName = teamPlayers[0]._alias.Length <= 10 
            ? string.Format("{0} {1}", teamPlayers[0]._alias, suffixes[rand.Next(suffixes.Length)])
            : string.Format("Team {0}", suffixes[rand.Next(suffixes.Length)]);

        // Create team if it doesn't exist
        Team team = arena.getTeamByName(teamName);
        if (team == null)
        {
            team = new Team(arena, arena._server)
            {
                _name = teamName,
                _isPrivate = false,
                _id = (short)arena.Teams.Count()
            };
            arena.createTeam(team);
        }

        // Assign players to team
        foreach (Player player in teamPlayers)
        {
            AssignPlayerToTeam(player, GetPrimarySkillName(player), teamName, true, false);
        }
    }

    // // Announce team assignments
    // arena.sendArenaMessage("&Teams have been assigned!");
    
    // // Display team compositions
    // for (int i = 0; i < teams.Count; i++)
    // {
    //     var teamPlayers = teams[i];
    //     if (teamPlayers.Count == 0) continue;

    //     string teamName = teamPlayers[0]._alias.Length <= 10 
    //         ? string.Format("{0} {1}", teamPlayers[0]._alias, suffixes[rand.Next(suffixes.Length)])
    //         : string.Format("Team {0}", suffixes[rand.Next(suffixes.Length)]);

    //     arena.sendArenaMessage(string.Format("~{0}: {1}/3 players", teamName, teamPlayers.Count));
    //     foreach (var player in teamPlayers)
    //     {
    //         arena.sendArenaMessage(string.Format("   {0} ({1})", player._alias, GetPrimarySkillName(player)));
    //     }
    // }
}

private bool CanSwitchTeamsDuringSUT(Player player, string targetTeamName)
{
    if (currentEventType != EventType.SUT)
        return true;

    Team targetTeam = arena.getTeamByName(targetTeamName);
    if (targetTeam == null)
        return true;

    // Only restrict team size to 3 players maximum
    if (targetTeam.ActivePlayerCount >= 3)
    {
        player.sendMessage(-1, "That team is full (maximum 3 players per team in SUT).");
        return false;
    }

    return true;
}
        private void SpawnVehicle(int posX, int posY, Team team)
        {
            VehInfo supplyVehicle = AssetManager.Manager.getVehicleByID(412);
            Helpers.ObjectState objState = new Helpers.ObjectState
            {
                positionX = (short)(posX * 16),
                positionY = (short)(posY * 16),
                positionZ = 0
            };

            arena.newVehicle(supplyVehicle, team, null, objState);
        }

        private void AdjustPrivateTeamsBasedOnPlayerCount()
        {
            int playerCount = arena.Players.Count();
            int maxPrivateTeamPlayers = 0;

            if (playerCount <= 10)
            {
                allowPrivateTeams = false;
            }
            else if (playerCount <= 30)
            {
                allowPrivateTeams = true;
                maxPrivateTeamPlayers = 5;
            }
            else if (playerCount <= 40)
            {
                maxPrivateTeamPlayers = 8;
            }
            else if (playerCount <= 50)
            {
                maxPrivateTeamPlayers = 10;
            }
            else
            {
                allowPrivateTeams = true;
                maxPrivateTeamPlayers = int.MaxValue; // No restrictions
            }

            // Notify players of changes
            string status = allowPrivateTeams ? string.Format("enabled (max {0} players)", maxPrivateTeamPlayers) : "disabled";
            arena.sendArenaMessage(string.Format("Private teams are now {0}.", status));
        }


        private bool SpawnCTFXFlags()
        {
            // First deactivate all flags
            foreach (var flag in arena._flags.Values)
            {
                flag.bActive = false;
                Helpers.Object_Flags(arena.Players, flag);
            }

            List<MapFlagEntry> ctfxFlags = new List<MapFlagEntry>
            {
                new MapFlagEntry("Flag1", 387, 1256),
                new MapFlagEntry("Flag2", 613, 1349),
                new MapFlagEntry("Flag3", 800, 1457),
                new MapFlagEntry("Flag4", 821, 1676)
            };

            foreach (var flag in ctfxFlags)
            {
                var fs = arena.getFlag(flag.Item1);

                if (fs == null)
                {
                    return false; // Flag not found in arena
                }

                fs.posX = (short)(flag.Item2 * 16);
                fs.posY = (short)(flag.Item3 * 16);
                fs.oldPosX = (short)(flag.Item2 * 16);
                fs.oldPosY = (short)(flag.Item3 * 16);
                fs.bActive = true;
                fs.team = null;
                fs.carrier = null;

                Helpers.Object_Flags(arena.Players, fs);
            }

            return true;
        }


/// <summary>
/// Randomly warps a player within a rectangular tile region. Optionally, 
/// set their energy to a specific value. If you pass -1 for `overrideEnergy`,
/// the player keeps their current energy.
/// </summary>
private void WarpPlayerToRange(Player player, 
                               int tileXMin, int tileXMax, 
                               int tileYMin, int tileYMax,
                               short overrideEnergy = -1)
{
    // Randomize coordinates within the specified range
    Random rnd = new Random();
    int tileX = rnd.Next(tileXMin, tileXMax + 1);
    int tileY = rnd.Next(tileYMin, tileYMax + 1);

    // Convert tile coordinates to game units (pixels * 16)
    short gameX = (short)(tileX * 16);
    short gameY = (short)(tileY * 16);

    // Create an ObjectState with the new position
    Helpers.ObjectState newState = new Helpers.ObjectState
    {
        positionX = gameX,
        positionY = gameY,
        positionZ = 0,          // Typically ground level
        yaw = player._state.yaw // Keep the player's current orientation
    };

    // Decide which energy to use:
    //   - If overrideEnergy == -1, keep the player's current energy
    //   - Otherwise, override the player's energy with the provided value
    short finalEnergy = (overrideEnergy == -1)
        ? player._state.energy // "no change"
        : overrideEnergy;      // "use the specified energy"

    // Keep the player's current health
    short finalHealth = player._state.health;

    // Warp the player with their current health/energy/yaw
    player.warp(Helpers.ResetFlags.ResetNone, newState, finalHealth, finalEnergy, (byte)newState.yaw);
}

        // Define DuelEventState enum
private enum DuelEventState
{
    Inactive,
    SignupPhase,
    PoolPhase,
    KnockoutPhase,
    Completed
}

// Add the duel-related variables in the class
private DuelEventState duelState = DuelEventState.Inactive;
private List<Player> duelParticipants = new List<Player>();
private List<Tuple<Player, Player>> currentMatches = new List<Tuple<Player, Player>>();
private Dictionary<Player, int> playerScores = new Dictionary<Player, int>();

// Alias for the player that we will simulate fake players for
private string simulatedAlias = "Axidus";

// Duel staging and duel pad coordinates (multiplied by 16 for position units)
private readonly int[] duelStagingArea = new int[] { 810 * 16, 535 * 16 };
private readonly List<int[]> duelPads = new List<int[]>
{
    new int[] { 767 * 16, 525 * 16 },
    new int[] { 791 * 16, 525 * 16 },
    new int[] { 765 * 16, 544 * 16 },
    new int[] { 791 * 16, 544 * 16 }
};

// Start the duel event
private void StartDuelEvent(Player initiator)
{
    if (duelState != DuelEventState.Inactive)
    {
        EndDuelEvent();
        return;
    }

    duelState = DuelEventState.SignupPhase;
    duelParticipants.Clear();
    currentMatches.Clear();
    playerScores.Clear();

    arena.sendArenaMessage(string.Format("~Duel event initiated by {0}. Use ?duel to sign up.", initiator._alias));

    // Automatically sign up players with the Dueler skill
    SignUpDuelerPlayers();

    duelParticipants = duelParticipants.OrderBy(p => Guid.NewGuid()).ToList(); // Shuffle participants

    // Send an arena message listing out the duelParticipants foreach
    string participantList = string.Join(", ", duelParticipants.Select(p => p._alias));
    arena.sendArenaMessage(string.Format("~Duel participants: {0}", participantList));

    
    // Start pool phase if there are enough participants
    if (duelParticipants.Count >= 8)
    {
        StartPoolPhase();
        arena.sendArenaMessage("Duel event has started. Players have been paired for the first round.");
    }
    else
    {
        arena.sendArenaMessage("Not enough participants to start the duel event.");
        EndDuelEvent();
    }
    
}

// End the duel event
private void EndDuelEvent()
{
    duelState = DuelEventState.Inactive;
    duelParticipants.Clear();
    currentMatches.Clear();
    playerScores.Clear();

    arena.sendArenaMessage("~Duel event has ended.");
}

// Pair players and start pool phase matches
private void StartPoolPhase()
{
    duelState = DuelEventState.PoolPhase;
    arena.sendArenaMessage("~Starting pool phase.");

    // Shuffle players
    //duelParticipants = duelParticipants.OrderBy(p => Guid.NewGuid()).ToList();
    currentMatches.Clear();

    // Pair players and start matches if we have at least 8 participants

    for (int i = 0; i < duelParticipants.Count; i += 2)
    {
        if (i + 1 < duelParticipants.Count)
        {
            currentMatches.Add(new Tuple<Player, Player>(duelParticipants[i], duelParticipants[i + 1]));
        }
        else
        {
            // Handle odd player
            arena.sendArenaMessage(string.Format("~{0} has no opponent and advances to the next round.", duelParticipants[i]._alias));
        }
    }

    // Start the matches
    StartNextMatches();
}

// Warp players to duel pads and start matches
private void StartNextMatches()
{
    arena.sendArenaMessage("~Warping players and starting matches.");
    for (int i = 0; i < currentMatches.Count && i < duelPads.Count; i++)
    {
        var match = currentMatches[i];
        var pad = duelPads[i];

        // Warp first player if they are real
        if (match.Item1._client != null)
        {
            Helpers.ObjectState newState1 = new Helpers.ObjectState
            {
                positionX = (short)(pad[0]), // Use pad[0] directly
                positionY = (short)(pad[1]), // Use pad[1] directly
                positionZ = 0,
                yaw = match.Item1._state.yaw
            };

            match.Item1.warp(Helpers.ResetFlags.ResetAll, newState1, 0, -1, 0);
        }

        // Warp second player if they are real
        if (match.Item2._client != null)
        {
            Helpers.ObjectState newState2 = new Helpers.ObjectState
            {
                positionX = (short)(pad[0]),
                positionY = (short)(pad[1]),
                positionZ = 0,
                yaw = match.Item2._state.yaw
            };

            match.Item2.warp(Helpers.ResetFlags.ResetAll, newState2, 0, -1, 0);
        }

        // Initialize player scores
        playerScores[match.Item1] = 0;
        playerScores[match.Item2] = 0;

        // Announce the match
        arena.sendArenaMessage(string.Format("@Match: {0} vs {1}", match.Item1._alias, match.Item2._alias));
    }
}


// Set up the pool phase matches
private void SetupPoolPhaseMatches()
{
    currentMatches.Clear();

    // Pair up participants for the pool phase (2 players per match)
    for (int i = 0; i < duelParticipants.Count; i += 2)
    {
        if (i + 1 < duelParticipants.Count)
        {
            // Create a match between two players
            currentMatches.Add(Tuple.Create(duelParticipants[i], duelParticipants[i + 1]));
        }
        else
        {
            // Handle odd player
            arena.sendArenaMessage(string.Format("{0} has no opponent and advances to the next round.", duelParticipants[i]._alias));
            // Optionally add back to participants for next round
        }
    }

    // Inform players about their matches
    foreach (var match in currentMatches)
    {
        //match.Item1.sendMessage(0, string.Format("You are paired against {0} in the pool phase.", match.Item2._alias));
        //match.Item2.sendMessage(0, string.Format("You are paired against {0} in the pool phase.", match.Item1._alias));
    }

    // Announce matches in the arena
    arena.sendArenaMessage("&Duel Tournament Matches:");
    foreach (var match in currentMatches)
    {
        arena.sendArenaMessage(string.Format("*{0} vs {1}", match.Item1._alias, match.Item2._alias));
    }

    // Start the matches
    StartNextMatches();

    if (duelParticipants.Count < 8)
    {
        arena.sendArenaMessage(string.Format("Waiting for more players to sign up. Current count: {0}", duelParticipants.Count));
    }
}

// Adjust the start duel event to sign up all Dueler players
private void StartDuelEvent()
{
    if (duelState != DuelEventState.Inactive)
    {
        EndDuelEvent();
        return;
    }

    // Start the signup phase
    duelState = DuelEventState.SignupPhase;

    duelParticipants.Clear();
    currentMatches.Clear();
    playerScores.Clear();

    // Automatically sign up players with the Dueler skill
    SignUpDuelerPlayers();

    arena.sendArenaMessage("Duel event signup phase has started. Use ?duel to sign up.");

    // Check if we have enough participants to start the pool phase
    if (duelParticipants.Count >= 8)
    {
        StartPoolPhase();
    }
    else
    {
        arena.sendArenaMessage(string.Format("Waiting for more players to sign up. Current count: {0}", duelParticipants.Count));
    }
}

// Simulate fake players if the player alias matches the simulatedAlias
private void SimulateFakePlayers(Player player)
{
    if (player._alias.Equals(simulatedAlias, StringComparison.OrdinalIgnoreCase))
    {
        // Add 7 fake players with incrementing numbers
        for (int i = 1; i <= 7; i++)
        {
            string fakeAlias = simulatedAlias + i;
            Player fakePlayer = CreateFakePlayer(fakeAlias);
            duelParticipants.Add(fakePlayer);
            playerScores[fakePlayer] = 0;
        }
    }
}

// Create a fake player with a given alias
private Player CreateFakePlayer(string alias)
{
    Player fakePlayer = new Player(); // Adjust player creation logic based on your actual instantiation method
    fakePlayer._alias = alias;

    // We don't set the Dueler skill for fake players anymore
    fakePlayer._skills = new Dictionary<int, Player.SkillItem>();

    fakePlayer._team = null; // Set as neutral or assign a team

    return fakePlayer;
}

// Sign up all players and create fake players if necessary
private void SignUpDuelerPlayers()
{
    foreach (var player in arena.Players)
    {
        string skillName = GetPrimarySkillName(player);

        if (skillName.Equals("Dueler", StringComparison.OrdinalIgnoreCase))
        {
            duelParticipants.Add(player);
            playerScores[player] = 0;
            player.sendMessage(0, "You have been automatically signed up for the duel event.");

        // Simulate fake players if necessary
        SimulateFakePlayers(player);
        }
    }
}

// Advance winners and set up next matches
private void AdvanceToNextRound(Player winner)
{
    // Remove the completed match
    var match = currentMatches.FirstOrDefault(m => m.Item1 == winner || m.Item2 == winner);
    if (match != null)
    {
        currentMatches.Remove(match);
    }

    // Add winner to next round participants
    duelParticipants.Add(winner);
    var stagedArea = duelStagingArea;

    Helpers.ObjectState newState = new Helpers.ObjectState
    {
        positionX = (short)(stagedArea[0]),
        positionY = (short)(stagedArea[1]),
        positionZ = 0,
        yaw = winner._state.yaw
    };

    winner.warp(Helpers.ResetFlags.ResetAll, newState, 0, -1, 0);

    // Check if all matches are completed
    if (currentMatches.Count == 0)
    {
        if (duelParticipants.Count == 1)
        {
            // Tournament completed
            Player champion = duelParticipants.First();
            arena.sendArenaMessage(string.Format("Congratulations {0}! You are the Duel Tournament Champion!", champion._alias));
            duelState = DuelEventState.Completed;
            EndDuelEvent();
        }
        else
        {
            // Start next phase
            PrepareNextMatches();
        }
    }
}

// Prepare next matches for the knockout phase
private void PrepareNextMatches()
{
    duelState = DuelEventState.KnockoutPhase;

    // Shuffle participants
    var shuffledPlayers = duelParticipants.OrderBy(p => Guid.NewGuid()).ToList();
    duelParticipants.Clear();
    currentMatches.Clear();

    for (int i = 0; i < shuffledPlayers.Count; i += 2)
    {
        if (i + 1 < shuffledPlayers.Count)
        {
            currentMatches.Add(new Tuple<Player, Player>(shuffledPlayers[i], shuffledPlayers[i + 1]));
        }
        else
        {
            // Odd player advances automatically
            duelParticipants.Add(shuffledPlayers[i]);
            shuffledPlayers[i].sendMessage(0, "You have advanced to the next round by default.");
        }
    }

    // Inform players about their matches
    arena.sendArenaMessage("Next Round Matches:");
    foreach (var match in currentMatches)
    {
        arena.sendArenaMessage(string.Format("{0} vs {1}", match.Item1._alias, match.Item2._alias));
    }

    // Reset player scores
    ResetPlayerScores();

    // Warp players to duel pads and start matches
    StartNextMatches();
}

// Reset player scores to zero
private void ResetPlayerScores()
{
    foreach (var player in duelParticipants)
    {
        playerScores[player] = 0;
    }
}

        public void Duel(Player player)
        {
            List<Player> players = arena.PlayersIngame.ToList();
            if ((arena._name.Contains("Arena 1") || arena._name.Contains("Public1")) && players.Count > 4)
            {
                player.sendMessage(1, "Can only duel with 4 or less players in a public arena, please move to a private arena to duel with more players.");
                return;
            }

            // Get the current terrain ID
            int terrainID = player._arena.getTerrainID(player._state.positionX, player._state.positionY);
            
            // Store spectator status
            bool wasSpectator = player.IsSpectator;

            // Check if the player is a spectator
            if (wasSpectator)
            {
                /*    // Reset the player's inventory
                    player.resetInventory(true);
                    
                    // Change the player's skill to "Dueler"
                    ChangePlayerSkill(player, "Dueler");

                // Iterate through teams to find an empty one (teams 2 to 33)
                for (int i = 2; i <= 33; i++)
                {
                    string teamName = CFG.teams[i].name;
                    Team team = player._arena.getTeamByName(teamName);

                    // Check if the team exists and is empty
                    if (team != null && team.ActivePlayerCount == 0)
                    {
                        // Unspecc the player on the empty team
                        player.unspec(team);
                        
                        // Warp the player to the exact coordinates (756, 533) scaled by 16
                        WarpPlayerToExactLocation(player, 756, 533);
                        return; // Exit after unspecing and warping the player
                    }
                }
                */

                // If no empty team was found, notify the player
                player.sendMessage(-1, "Please unspec before attempting to duel.");
            }
            else
            {
                // If the player is not a spectator, check for valid terrain for dueling
                if (terrainID == 4 || terrainID == 3 || terrainID == 1 || terrainID == 2) // Only allow if on valid terrain
                {
                    // Reset the player's inventory
                    player.resetInventory(true);
                    
                    // Change the player's skill to "Dueler"
                    ChangePlayerSkill(player, "Dueler");

                    // Iterate through teams to find an empty one (teams 2 to 33)
                    for (int i = 2; i <= 33; i++)
                    {
                        string teamName = CFG.teams[i].name;
                        Team team = player._arena.getTeamByName(teamName);

                        // Check if the team exists and is empty
                        if (team != null && team.ActivePlayerCount == 0)
                        {
                            // Assign the player to the empty team
                            AssignPlayerToTeam(player, "Dueler", teamName, false, true);
                            
                            // Warp the player to the exact coordinates (756, 533) scaled by 16
                            WarpPlayerToExactLocation(player, 756, 533);
                            return; // Exit after assigning and warping the player
                        }
                    }

                    // If no empty team was found, notify the player
                    player.sendMessage(0, "No available teams for the duel.");
                }
                else
                {
                    // Notify the player they cannot duel from their current location
                    player.sendMessage(0, "You cannot duel from your current location. Please try from spec or DropShip");
                }
            }
        }

        private void WarpPlayerToExactLocation(Player player, int tileX, int tileY)
        {
            // Convert tile coordinates to game units by multiplying by 16
            short gameX = (short)(tileX * 16);
            short gameY = (short)(tileY * 16);

            // Create an ObjectState with the new position
            Helpers.ObjectState newState = new Helpers.ObjectState
            {
                positionX = gameX,
                positionY = gameY,
                positionZ = 0, // Set to 0 or adjust as needed
                yaw = player._state.yaw // Maintain the player's current orientation
            };
                // Warp the player to the new location
                player.warp(Helpers.ResetFlags.ResetAll, newState, 0, -1, 0);

        }

private void SpawnVehicle(string side, string location)
{
    // Define valid sides
    string[] validSides = { "Titan", "Collective" };

    // Dictionary to store coordinates for main vehicle locations
    Dictionary<string, int[]> mainLocations = new Dictionary<string, int[]>
    {
        { "d8", new int[] { 275, 601 } },
        { "a10", new int[] { 50, 763 } },
        { "c10", new int[] { 210, 703 } }
    };

    // Dictionary to store coordinates for exit vehicle locations
    Dictionary<string, int[]> exitLocations = new Dictionary<string, int[]>
    {
        { "Titan", new int[] { 315, 153 } },      // Titan exit coordinates
        { "Collective", new int[] { 17, 205 } }   // Collective exit coordinates
    };

    // Define vehicle IDs based on side
    int mainVehicleId = side.Equals("Titan", StringComparison.OrdinalIgnoreCase) ? 414 : 416;
    int exitVehicleId = side.Equals("Titan", StringComparison.OrdinalIgnoreCase) ? 441 : 440;

    // Validate location
    if (!mainLocations.ContainsKey(location))
    {
        arena.sendArenaMessage(string.Format("Invalid location: {0}", location), -1);
        return;
    }

    // Validate side
    if (!exitLocations.ContainsKey(side))
    {
        arena.sendArenaMessage(string.Format("Invalid side: {0}", side), -1);
        return;
    }

    // Get main vehicle coordinates
    int mainX = mainLocations[location][0];
    int mainY = mainLocations[location][1];

    // Get exit vehicle coordinates
    int exitX = exitLocations[side][0];
    int exitY = exitLocations[side][1];

    // Retrieve vehicle information
    VehInfo mainVehInfo = arena._server._assets.getVehicleByID(mainVehicleId);
    if (mainVehInfo == null)
    {
        arena.sendArenaMessage(string.Format("Main vehicle ID {0} not found.", mainVehicleId), -1);
        return;
    }

    VehInfo exitVehInfo = arena._server._assets.getVehicleByID(exitVehicleId);
    if (exitVehInfo == null)
    {
        arena.sendArenaMessage(string.Format("Exit vehicle ID {0} not found.", exitVehicleId), -1);
        return;
    }

    // Initialize vehicle states
    Helpers.ObjectState mainState = new Protocol.Helpers.ObjectState
    {
        positionX = (short)(mainX * 16),
        positionY = (short)(mainY * 16)
    };

    Helpers.ObjectState exitState = new Protocol.Helpers.ObjectState
    {
        positionX = (short)(exitX * 16),
        positionY = (short)(exitY * 16)
    };

    // Assign to team
    Team targetTeam = side.Equals("Titan", StringComparison.OrdinalIgnoreCase) ? 
                      arena.Teams.ElementAtOrDefault(0) : 
                      arena.Teams.ElementAtOrDefault(1);
    if (targetTeam == null)
    {
        arena.sendArenaMessage(string.Format("Target team not found for side: {0}", side), -1);
        return;
    }

    try
    {
        // Spawn main vehicle
        arena.newVehicle(mainVehInfo, targetTeam, null, mainState, null);
        arena.sendArenaMessage(string.Format("Spawned main vehicle at {0} for {1}.", location, side));

        // Spawn exit vehicle
        arena.newVehicle(exitVehInfo, targetTeam, null, exitState, null);
        arena.sendArenaMessage(string.Format("Spawned exit vehicle (ID {0}) for {1} at coordinates ({2}, {3}).", exitVehicleId, side, exitX, exitY));
    }
    catch (Exception ex)
    {
        // Log exception details
        Console.WriteLine(string.Format("Error spawning vehicles: {0}", ex.Message));
        arena.sendArenaMessage(string.Format("Failed to set base and exit vehicles."), -1);
    }
}



        private void changeD7()
        {
            // Retrieve vehicle information for ID 414
            VehInfo vehicleToSpawn = arena._server._assets.getVehicleByID(414);
            if (vehicleToSpawn == null)
            {
                Log.write(TLog.Error, "Vehicle with ID 414 could not be found.");
                return;
            }

            // Initialize vehicle state with scaled coordinates
            Helpers.ObjectState vehicleState = new Protocol.Helpers.ObjectState
            {
                positionX = (short)(275 * 16),
                positionY = (short)(601 * 16)
                // Initialize other necessary properties if required
            };

            // Assign the vehicle to a valid team
            Team targetTeam = arena.Teams.ElementAtOrDefault(1); // Adjust index as needed
            if (targetTeam == null)
            {
                Log.write(TLog.Error, "Team 1 does not exist.");
                return;
            }

            try
            {
                // Spawn the vehicle
                arena.newVehicle(
                    vehicleToSpawn,
                    targetTeam, // Assign to a valid team
                    null,       // Creator player (null if not applicable)
                    vehicleState,
                    null        // Setup callback if needed
                );
            }
            catch (Exception ex)
            {
            }
        }

        // Method to relocate flags
        private void RelocateFlags()
        {
            foreach (var flag in arena._flags.Values)
            {
                // Generate new random positions or set predefined ones
                short newX = (short)(arena._rand.Next(100, 1000) * 16);
                short newY = (short)(arena._rand.Next(100, 1000) * 16);

                flag.posX = newX;
                flag.posY = newY;

                // Update the flag for all players
                Helpers.Object_Flags(arena.Players, flag);
            }

            arena.sendArenaMessage("Flag spawns have been relocated.");
        }

        // Call this method when needed, for example, at the start of a new event
        private void StartNewEvent()
        {
            RelocateFlags();
            // Additional event initialization logic
        }

        private void AssignPlayerToTeam(Player player, string skillName, string teamName, bool createIfNotExist, bool noMaxSize = false)
        {
            // Check if the player's arena is valid
            if (player == null || player._arena == null)
            {
                Log.write(TLog.Error, "Failed to assign player to a team. Player or arena context is missing.");
                player.sendMessage(0, "Failed to assign you to a team. Arena context is missing.");
                return;
            }

            // Attempt to get the team by name
            Team team = player._arena.getTeamByName(teamName);

            // If the team doesn't exist and creation is allowed, create a new team
            if (team == null && createIfNotExist)
            {
                // Create a new team safely with proper initialization
                team = new Team(player._arena, player._arena._server)
                {
                    _name = teamName,
                    _isPrivate = false,
                    _id = (short)player._arena.Teams.Count()
                };

                player._arena.createTeam(team);
                Log.write(TLog.Normal, string.Format("Created new team '{0}' for {1}.", teamName, player._alias));
            }

            // Ensure the team is properly initialized before proceeding
            if (team != null)
            {
                // Check if the player's current skill matches the intended skill for the team
                string playerSkill = GetPrimarySkillName(player);
                if (playerSkill.Equals(skillName, StringComparison.OrdinalIgnoreCase))
                {
                    // Manually check team size to enforce a max of 5 players if not a no-max-size team (e.g., Marines)
                    int maxAllowedPlayers = 5; // Set the max size for Marine teams

                    // Check if the team can accept more players based on custom size logic
                    bool canJoin = noMaxSize || team.ActivePlayerCount < maxAllowedPlayers;

                    // Proceed with adding the player to the team if they can join
                    if (canJoin)
                    {
                        // If the player is a spectator, unspec them before adding to the team
                        if (player.IsSpectator)
                        {
                            player.unspec(team);
                        }
                        else
                        {
                            team.addPlayer(player, true);
                        }
                        player.sendMessage(0, string.Format("{0} has been assigned to {1}.", player._alias, teamName));
                    }
                    else
                    {
                        player.sendMessage(0, string.Format("{0} could not be assigned to {1} as it has reached the maximum number of players ({2}).", player._alias, teamName, maxAllowedPlayers));
                        Log.write(TLog.Warning, string.Format("{0} could not be assigned to {1} because it has reached the maximum number of players ({2}).", player._alias, teamName, maxAllowedPlayers));
                    }
                }
                else
                {
                    player.sendMessage(0, string.Format("{0} does not have the correct skill ({1}) to join {2}.", player._alias, skillName, teamName));
                    Log.write(TLog.Warning, string.Format("{0} attempted to join {1} but does not have the correct skill ({2}).", player._alias, teamName, skillName));
                }
            }
            else
            {
                player.sendMessage(0, string.Format("Failed to assign {0} to {1}. The team does not exist and could not be created.", player._alias, teamName));
                Log.write(TLog.Error, string.Format("Failed to assign {0} to {1}. The team does not exist and could not be created.", player._alias, teamName));
            }
        }



       private void AssignTeamsForZombieEvent(List<Player> players)
        {
            // Ensure that players list is not empty to avoid accessing null properties
            if (players.Count == 0) return;

            // Get the arena from any player, assuming all players belong to the same arena
            Arena arena = players[0]._arena;

            // Lists to hold zombies and marines
            List<Player> zombies = players.Where(p => GetPrimarySkillName(p).Equals("Zombie", StringComparison.OrdinalIgnoreCase)).ToList();
            List<Player> marines = players.Where(p => GetPrimarySkillName(p).Equals("Marine", StringComparison.OrdinalIgnoreCase)).ToList();

            // Assign all zombies to the Zombie Team
            foreach (Player zombie in zombies)
            {
                AssignPlayerToTeam(zombie, "Zombie", "Zombies", true, true); // No max size for Zombie Team
            }

            // Define suffixes for team names
            string[] suffixes = { "Renegades", "Sentinels", "Warriors", "Guardians", "Raiders", "Vanguards", "Avengers", "Titans", "Commandos", "Strikers" };
            Random rand = new Random(); // Create a random object for selecting suffixes

            // Logic to assign Marines to teams with a max of 5 players per team
            int marineTeamCount = (int)Math.Ceiling(marines.Count / 5.0);

            for (int i = 0; i < marineTeamCount; i++)
            {
                // Generate a team name based on the first player's alias if it's 10 characters or less
                string marineTeamName;
                if (marines.Count > 0)
                {
                    string playerAlias = marines[0]._alias;
                    if (playerAlias.Length <= 10)
                    {
                        // Randomly select a suffix from the array
                        string randomSuffix = suffixes[rand.Next(suffixes.Length)];
                        marineTeamName = string.Format("{0} {1}", playerAlias, randomSuffix);
                    }
                    else
                    {
                        // Fallback to default naming if alias is too long
                        marineTeamName = string.Format("Marine {0}", i + 1);
                    }
                }
                else
                {
                    marineTeamName = string.Format("Marine {0}", i + 1);
                }

                Team marineTeam = arena.getTeamByName(marineTeamName);

                // Create the Marine team if it doesn't exist
                if (marineTeam == null)
                {
                    marineTeam = new Team(arena, arena._server)
                    {
                        _name = marineTeamName,
                        _isPrivate = false,
                        _id = (short)arena.Teams.Count()
                    };
                    arena.createTeam(marineTeam);
                }

                // Assign up to 5 players to the current Marine team
                for (int j = 0; j < 5 && marines.Count > 0; j++)
                {
                    Player marine = marines[0];
                    marines.RemoveAt(0);
                    AssignPlayerToTeam(marine, "Marine", marineTeamName, true, false); // Max size of 5 per team
                }
            }
        }
        private void InitializeZombie()
        {
            // Get all players in-game
            List<Player> players = arena.PlayersIngame.ToList();

            if (players.Count == 0)
            {
                arena.sendArenaMessage("No players are available to start the Zombie event.");
                return;
            }

            // Warp all players to valid random positions within the defined zombie zone
            foreach (Player p in players)
            {
                // Use WarpPlayerToZombieZone to handle warping safely
                WarpPlayerToZombieZone(p);
            }

            // Calculate the number of initial zombies (1 zombie per 5 players, round up)
            int totalPlayers = players.Count;
            int initialZombieCount = (int)Math.Ceiling(totalPlayers / 5.0);

            // Ensure at least 1 initial zombie
            if (initialZombieCount < 1)
                initialZombieCount = 1;

            // Randomly select initial zombies
            List<Player> potentialZombies = new List<Player>(players);
            List<Player> initialZombies = new List<Player>();

            for (int i = 0; i < initialZombieCount; i++)
            {
                if (potentialZombies.Count == 0)
                    break;

                int index = rand.Next(potentialZombies.Count);
                Player selectedZombie = potentialZombies[index];
                potentialZombies.RemoveAt(index);
                initialZombies.Add(selectedZombie);
            }

            // Change initial zombies to "Zombie" skill
            foreach (Player zombie in initialZombies)
            {
                ChangePlayerSkill(zombie, "Zombie");
                zombie.sendMessage(0, String.Format("{0} has become an initial zombie! Infect other players!", zombie._alias));
            }

            // Assign the "Marine" skill to all non-zombie players
            List<Player> marinePlayers = new List<Player>(); // List to collect all Marine players for team assignment
            foreach (Player p in players)
            {
                if (!initialZombies.Contains(p))
                {
                    // Change non-zombies to "Marine" skill
                    ChangePlayerSkill(p, "Marine");
                    marinePlayers.Add(p); // Collect all Marine players
                    p.sendMessage(0, String.Format("{0} must survive the zombie apocalypse!", p._alias));
                }
            }

            // Assign teams based on their roles using the generic function
            AssignTeamsForZombieEvent(players); // Call to assign teams based on Zombie and Marine roles
        }


        private void WarpPlayerToZombieZone(Player player)
        {
            // Define the coordinates for the zombie zone
            int minX = 521, minY = 205;
            int maxX = 840, maxY = 280;
            int maxAttempts = 10; // Maximum attempts to find a valid warp location

            Random rand = new Random();
            bool validLocationFound = false;

            for (int i = 0; i < maxAttempts; i++)
            {
                // Randomly select coordinates within the defined bounds
                short x = (short)rand.Next(minX * 16, maxX * 16);
                short y = (short)rand.Next(minY * 16, maxY * 16);

                // Check if the tile at the selected coordinates is blocked or has physics objects
                if (!player._arena.getTile(x, y).Blocked)
                {
                    // Create an ObjectState for the player's new position
                    Helpers.ObjectState newState = new Helpers.ObjectState
                    {
                        positionX = x,
                        positionY = y,
                        positionZ = 0,
                        yaw = player._state.yaw
                    };

                    // Warp the player if the location is valid
                    player.warp(Helpers.ResetFlags.ResetAll, newState, 0, -1, 0);
                    validLocationFound = true;
                    break;
                }
            }

            if (!validLocationFound)
            {
                player.sendMessage(-1, "Failed to find a valid spawn location. Please try again.");
            }
        }

        private void HandleZombieDeath(Player victim, Player killer)
        {
            // Check if the killer is a zombie
            if (killer != null && killer._skills.Any(s => s.Value.skill.Name.Equals("Zombie", StringComparison.OrdinalIgnoreCase)))
            {
                // Convert the victim into a zombie
                ChangePlayerSkill(victim, "Zombie");

                // Assign them to the Zombies team
                AssignPlayerToTeam(victim, "Zombie", "Zombies", true, true);
                
                // Inform the victim they were turned into a zombie
                victim.sendMessage(0, string.Format("You were killed by a zombie ({0}). You are now a zombie!", killer != null ? killer._alias : "Unknown"));

                // Warp the victim to a valid location in the zombie zone
                WarpPlayerToZombieZone(victim);
            }
            else
            {
                // Handle non-zombie deaths if needed
                victim.sendMessage(0, string.Format("You were killed by {0}.", killer != null ? killer._alias : "Unknown"));
            }

            // Check for victory condition when only one non-zombie player remains
            CheckZombieWinCondition();
        }


        private bool IsPlayerZombie(Player player)
        {
            if (player == null)
                return false;

            // Get the player's current skill name
            string currentSkillName = GetPrimarySkillName(player);

            // Check if the skill is "Zombie"
            return currentSkillName.Equals("Zombie", StringComparison.OrdinalIgnoreCase);
        }

        private void CheckZombieWinCondition()
        {
            // Get all players who are not zombies
            List<Player> nonZombiePlayers = arena.PlayersIngame.Where(p => !IsPlayerZombie(p)).ToList();

            int totalPlayers = arena.PlayersIngame.Count();

            if (totalPlayers >= 4)
            {
                if (nonZombiePlayers.Count == 1)
                {
                    // When 3 or more players, and only 1 non-zombie left, they win
                    Player winner = nonZombiePlayers.First();

                    // Announce the winner
                    arena.sendArenaMessage(string.Format("{0} is the last survivor and wins the Zombie event!", winner._alias));

                    // Prize the winner with the "staff" skill
                    ChangePlayerSkill(winner, "staff");

                    // Optionally end the event
                    EndEvent();
                }
                else if (nonZombiePlayers.Count == 0)
                {
                    // All players have become zombies
                    arena.sendArenaMessage("&All players have become zombies! Zombies win!");

                    // Optionally end the event
                    EndEvent();
                }
            }
            else if (totalPlayers <= 3)
            {
                if (nonZombiePlayers.Count == 0)
                {
                    // All players have become zombies
                    arena.sendArenaMessage("&All players have become zombies!");

                    // Optionally end the event
                    EndEvent();
                }
                // When only 2 players, and one is still not a zombie, continue the game
                // Victory condition is not triggered until both become zombies
            }
        }


        // Private Teams
        public void privateTeams(Player player, Player recipient, string command, string payload)
        {
            // Check if private teams are enabled
            if (!allowPrivateTeams)
            {
                player.sendMessage(-1, "Private teams are currently disabled in this arena.");
                return;
            }

            // Check if the payload is valid
            if (string.IsNullOrEmpty(payload))
            {
                player.sendMessage(-1, "Usage: ?t teamName:password");
                return;
            }

            // Get the current terrain ID at the player's position
            int terrainID = player._arena.getTerrainID(player._state.positionX, player._state.positionY);

            // Check if the terrain allows for team changes only on terrain #4
            if ((terrainID != 1 && terrainID != 2 && terrainID != 3 && terrainID != 4) && !player.IsSpectator)
            {
                player.sendMessage(-1, "Can't change team from this terrain.");
                return;
            }

            // Check if the player has enough energy to switch teams
            int minEnergy = player._server._zoneConfig.arena.teamSwitchMinEnergy / 1000;
            if (player._state.energy < minEnergy)
            {
                player.sendMessage(-1, string.Format("Cannot switch teams unless you have at least {0} energy (you have {1}).", minEnergy, player._state.energy));
                return;
            }

            // Split the payload into team name and password
            string[] parts = payload.Split(':');
            if (parts.Length != 2)
            {
                player.sendMessage(-1, "Invalid format. Correct usage: ?t teamName:password");
                return;
            }

            string teamName = parts[0].Trim();
            string teamPassword = parts[1].Trim();

            // Validate the team name and password
            if (string.IsNullOrEmpty(teamName))
            {
                player.sendMessage(-1, "Team name cannot be empty.");
                return;
            }

            try
            {
                // Check if the team already exists
                Team team = player._arena.getTeamByName(teamName);

                if (team != null)
                {
                    // The team exists, check if it's private and validate the password
                    if (!team.IsSpec && team._password == teamPassword)
                    {
                        // Attempt to add the player to the team
                        if (!team.IsFull)
                        {
                            team.addPlayer(player, true);
                            player.sendMessage(0, string.Format("You have joined the private team '{0}'.", teamName));
                            Log.write(TLog.Normal, string.Format("{0} joined the private team '{1}'.", player._alias, teamName));
                        }
                        else
                        {
                            player.sendMessage(-1, "The team is full.");
                        }
                    }
                    else
                    {
                        player.sendMessage(-1, "Incorrect password or the team is not private.");
                    }
                }
                else
                {

                    // Validate the team name against restricted names
                    string temp = teamName.ToLower();
                    if (temp.Equals("spec") || temp.Equals("spectator") || temp.Contains("bot team"))
                    {
                        player.sendMessage(-1, "You can't use this team name.");
                        return;
                    }

                    if (temp.Length > 32)
                    {
                        player.sendMessage(-1, "The team name cannot be longer than 32 characters.");
                        return;
                    }

                    // Create a new private team
                    Team newTeam = new Team(player._arena, player._arena._server)
                    {
                        _name = teamName,
                        _isPrivate = true,
                        _password = teamPassword,
                        _id = (short)player._arena.Teams.Count(),
                        _owner = player
                    };

                    // Add the new team to the arena
                    player._arena.createTeam(newTeam);
                    newTeam.addPlayer(player, true);
                    player.sendMessage(0, string.Format("You have created and joined the private team '{0}'.", teamName));

                }
            }
            catch (Exception e)
            {
                player.sendMessage(-1, "An error occurred while processing your team request.");
            }
        }

/// <summary>
/// Mix event section
/// </summary>

private Dictionary<Player, bool> playedInPreviousMix = new Dictionary<Player, bool>();

// Dictionaries to track players' role preferences and simulated players for testing
private Dictionary<string, List<string>> playerRolePreferences = new Dictionary<string, List<string>>();
// List of required roles
private readonly List<string> requiredRoles = new List<string> { "med", "eng", "sl" };
// List of preferred roles
private readonly List<string> preferredRolesDefense = new List<string> { "dinf", "dhvy" };
private readonly List<string> preferredRolesOffense = new List<string> { "oinf", "ohvy", "jt", "infil" };
// Variables to keep track of the mix process state
private bool isMixActive = false;
private DateTime lastTeamUpdateMessageTime;

// Function to start the mix process
private void StartMixProcess()
{
    isMixActive = true;
    playerRolePreferences.Clear();
    signUpOrder.Clear();
    lastTeamUpdateMessageTime = DateTime.Now;
    arena.sendArenaMessage("*Mix initiated: pick your preferred roles using ?mix roleName (sl, med, eng, jt, infil, oinf, dinf, ohvy, dhvy)!", 1);
}

// List to hold all role preferences submitted by players
private List<KeyValuePair<string, List<string>>> playerPool = new List<KeyValuePair<string, List<string>>>();
private HashSet<string> assignedPlayersGlobal = new HashSet<string>();
private int aliasCounter = 0;
private bool mixGameActive = true; // True when roles can be selected, false when teams are built

// Declare teams to hold the player assignments
private List<KeyValuePair<string, string>> team1 = new List<KeyValuePair<string, string>>();
private List<KeyValuePair<string, string>> team2 = new List<KeyValuePair<string, string>>();

// Define roles categorized by offense and defense
private static readonly HashSet<string> defenseRoles = new HashSet<string> { "med", "eng", "dinf", "dhvy" };
private static readonly HashSet<string> offenseRoles = new HashSet<string> { "sl", "oinf", "jt", "infil", "ohvy" };
private List<string> signUpOrder = new List<string>();

// Used to give random role based on Infantry/Heavy Weapons class when using *mix auto
private Random rand = new Random();

// Max counts per role per team
private Dictionary<string, int> maxRoleCountsPerTeam = new Dictionary<string, int>
{
    { "med", 1 },
    { "eng", 1 },
    { "sl", 1 },
    { "dhvy", 1 },
    { "ohvy", 1 },
    { "infil", 1 },
    { "jt", 1 },
    { "dinf", 3 },
    { "oinf", 4 }
};

// Handle player role selection
private void HandleRoleSelection(Player player, string roleSelection)
{
    if (!mixGameActive)
    {
        player.sendMessage(0, "Mix teams have already been built. Please wait for the next game.");
        return;
    }

    var alias = player._alias;
    // Handle multiple entries from the same player
    if (alias == "Axidus")
    {
        int testAliasCounter = playerRolePreferences.Count(p => p.Key.StartsWith("Axidus")) + 1;
        alias = "Axidus" + testAliasCounter.ToString();
    }

    var roles = roleSelection.Split(',').Select(r => r.Trim().ToLower()).ToList();

    // Validate roles
    var invalidRoles = roles.Where(r => !defenseRoles.Contains(r) && !offenseRoles.Contains(r)).ToList();
    if (invalidRoles.Any())
    {
        var invalidRolesList = string.Join(", ", invalidRoles);
        player.sendMessage(0, "Invalid role, use ?mix med, eng, sl, jt, infil, dinf, dhvy, oinf, ohvy");
        return; // Stop processing if there are invalid roles
    }

    if (!playerRolePreferences.ContainsKey(alias))
    {
        playerRolePreferences[alias] = new List<string>();
        signUpOrder.Add(alias); // Track sign-up order
    }

    foreach (var role in roles)
    {
        if (!playerRolePreferences[alias].Contains(role))
        {
            playerRolePreferences[alias].Add(role);
        }
    }

    playerPool = playerRolePreferences.ToList();
    arena.sendArenaMessage(string.Format("{0} players in pool.", playerPool.Count));

    if (playerPool.Count >= 20 && AreRequiredRolesFilled())
    {
        BuildTeamsFromPool();
        mixGameActive = false;
    }
    else
    {
        DisplayRequiredRolesMissingMessage();
    }
}

// Used with *mix auto parameter
private void AssignPlayersBasedOnSkills()
{
    // Get all players currently in team spec
    var teamSpec = arena.Players.Where(p => p.IsSpectator && p._team.IsSpec).ToList();
    foreach (var player in teamSpec)
    {
        string skillName = GetPrimarySkillName(player);
        // Map the skill to a role
        string role = MapSkillToRole(skillName);
        if (role != null)
        {
            // Call HandleRoleSelection
            HandleRoleSelection(player, role);
        }
        else
        {
            // Skill not recognized, perhaps send a message
            player.sendMessage(0, "Unable to assign role based on your primary skill.");
        }
    }
}

private string MapSkillToRole(string skillName)
{
    if (skillName == null)
        return null;

    switch (skillName)
    {
        case "Infantry":
            // Randomly pick oinf or dinf
            return RandomPick("oinf", "dinf");
        case "Heavy Weapons":
            // Randomly pick ohvy or dhvy
            return RandomPick("ohvy", "dhvy");
        case "Squad Leader":
            return "sl";
        case "Infiltrator":
            return "infil";
        case "Combat Engineer":
            return "eng";
        case "Field Medic":
            return "med";
        case "Jump Trooper":
            return "jt";
        default:
            return null; // Unknown skill
    }
}

private string RandomPick(string option1, string option2)
{
    return rand.Next(2) == 0 ? option1 : option2;
}

// Display the current player pool
public void DisplayCurrentPlayerPool(Player player)
{
    if (!mixGameActive) return;

    try
    {
        player.sendMessage(0, "*Current Player Pool:");
        foreach (var entry in playerPool) // Renamed player to entry
        {
            var alias = entry.Key;
            var roles = string.Join(", ", entry.Value);
            var priority = DidPlayerPlayInLastMix(alias) ? "Played Last Mix" : "Not Played Last Mix";
            var signUpPosition = signUpOrder.IndexOf(alias) + 1;

            // Sending message to the player passed into the method
            player.sendMessage(0, string.Format("*{0} ({1}) - {2}, Sign-Up Position: {3}", alias, roles, priority, signUpPosition));
        }
        player.sendMessage(0, string.Format("#{0} players in pool.", playerPool.Count));
    }
    catch (Exception ex)
    {
        Console.WriteLine("Error displaying player pool: {0}", ex.Message);
    }
}

// Check if required roles are filled
private bool AreRequiredRolesFilled()
{
    var totalRoles = playerPool.SelectMany(p => p.Value).ToList();
    int medCount = totalRoles.Count(role => role == "med");
    int engCount = totalRoles.Count(role => role == "eng");
    int slCount = totalRoles.Count(role => role == "sl");

    return medCount >= 2 && engCount >= 2 && slCount >= 2;
}

// Display message for missing required roles
private void DisplayRequiredRolesMissingMessage()
{
    var totalRoles = playerPool.SelectMany(p => p.Value).ToList();
    int medCount = totalRoles.Count(role => role == "med");
    int engCount = totalRoles.Count(role => role == "eng");
    int slCount = totalRoles.Count(role => role == "sl");

    int medNeeded = 2 - medCount;
    int engNeeded = 2 - engCount;
    int slNeeded = 2 - slCount;

    List<string> missingRoles = new List<string>();
    if (slNeeded > 0) missingRoles.Add(string.Format("SL: {0}", slNeeded));
    if (medNeeded > 0) missingRoles.Add(string.Format("Med: {0}", medNeeded));
    if (engNeeded > 0) missingRoles.Add(string.Format("Eng: {0}", engNeeded));

    if (missingRoles.Count > 0)
    {
        string missingRolesMessage = string.Join(", ", missingRoles);
        arena.sendArenaMessage(string.Format("#Missing required roles: {0}", missingRolesMessage));
    }

    int defenseSignups = playerPool.Count(p => p.Value.Any(role => defenseRoles.Contains(role)));
    int offenseSignups = playerPool.Count(p => p.Value.Any(role => offenseRoles.Contains(role)));

    //arena.sendArenaMessage(string.Format("{0} of 10 defense signups", defenseSignups));
    //arena.sendArenaMessage(string.Format("{0} of 10 offense signups", offenseSignups));
}

// Build teams from the player pool
private void BuildTeamsFromPool()
{
    // Clear the global tracker before starting new assignments
    assignedPlayersGlobal.Clear();

    arena.sendArenaMessage("&Building teams from the player pool...");

    team1.Clear();
    team2.Clear();

    // Assign required roles first
    if (!AssignRequiredRoles())
    {
        arena.sendArenaMessage("*Failed to assign required roles. Cannot build teams.");
        return;
    }

    // Assign remaining players
    AssignRemainingPlayers();

    // Ensure teams have 5 offense and 5 defense players
    EnsureTeamsAreBalanced();

    // Display team compositions
    DisplayTeamComposition("&Team 1 Composition:", team1);
    DisplayTeamComposition("&Team 2 Composition:", team2);

    // Mark players as having played in the previous mix
    foreach (var player in team1.Concat(team2))
    {
        var playerObject = FindPlayerByAlias(player.Key);
        if (playerObject != null)
        {
            playedInPreviousMix[playerObject] = true; // Mark as played in the previous mix
        }
    }

    // Unspec players to their respective teams
    UnspecPlayersToTeams(team1, team2);

    arena.sendArenaMessage("*Finished building teams.");
}

// Assign required roles to teams, prioritizing players who selected only one role
private bool AssignRequiredRoles()
{
    var assignedPlayers = new HashSet<string>();

    foreach (var role in requiredRoles)
    {
        var playersWithRole = GetPlayersByRoleAndPriority(role)
            .Where(alias => !assignedPlayersGlobal.Contains(alias))
            .ToList();

        if (playersWithRole.Count < 2)
        {
            arena.sendArenaMessage(string.Format("Not enough players for role {0}.", role));
            return false;
        }

        // Assign one player to each team
        var player1 = playersWithRole[0];
        var player2 = playersWithRole[1];

        team1.Add(new KeyValuePair<string, string>(player1, role));
        team2.Add(new KeyValuePair<string, string>(player2, role));

        assignedPlayers.Add(player1);
        assignedPlayers.Add(player2);
        assignedPlayersGlobal.Add(player1);
        assignedPlayersGlobal.Add(player2);
    }

    return true;
}

// Get players by role, prioritizing those who only selected one role and based on sign-up order
private List<string> GetPlayersByRoleAndPriority(string role)
{
    var playersWithRole = playerPool
        .Where(p => p.Value.Contains(role))
        .ToList();

    // First, get players who only selected this role
    var playersOnlyThisRole = playersWithRole
        .Where(p => p.Value.Count == 1)
        .Select(p => p.Key)
        .ToList();

    // Then, get players who selected multiple roles including this role
    var playersMultipleRoles = playersWithRole
        .Where(p => p.Value.Count > 1)
        .Select(p => p.Key)
        .ToList();

    // Prioritize players who only selected this role
    var prioritizedPlayers = playersOnlyThisRole
        .Concat(playersMultipleRoles)
        .ToList();

    // Now, order by sign-up order
    var orderedPlayers = prioritizedPlayers
        .OrderBy(alias => signUpOrder.IndexOf(alias))
        .ToList();

    return orderedPlayers;
}

// Check if a player played in the last mix
private bool DidPlayerPlayInLastMix(string alias)
{
    var playerObject = FindPlayerByAlias(alias);
    if (playerObject != null && playedInPreviousMix.ContainsKey(playerObject))
    {
        return playedInPreviousMix[playerObject];
    }
    return false;
}

// Assign remaining players to teams
private void AssignRemainingPlayers()
{
    var allRoles = maxRoleCountsPerTeam.Keys.ToList();

    var assignedPlayers = new HashSet<string>(assignedPlayersGlobal);

    var team1RoleCounts = maxRoleCountsPerTeam.ToDictionary(kvp => kvp.Key, kvp => 0);
    var team2RoleCounts = maxRoleCountsPerTeam.ToDictionary(kvp => kvp.Key, kvp => 0);

    int team1OffenseCount = team1.Count(p => offenseRoles.Contains(p.Value));
    int team1DefenseCount = team1.Count(p => defenseRoles.Contains(p.Value));
    int team2OffenseCount = team2.Count(p => offenseRoles.Contains(p.Value));
    int team2DefenseCount = team2.Count(p => defenseRoles.Contains(p.Value));

    // Update role counts based on assigned required roles
    foreach (var p in team1)
    {
        if (team1RoleCounts.ContainsKey(p.Value))
            team1RoleCounts[p.Value]++;
    }
    foreach (var p in team2)
    {
        if (team2RoleCounts.ContainsKey(p.Value))
            team2RoleCounts[p.Value]++;
    }

    // Assign remaining players
    var remainingPlayers = playerPool
        .Where(p => !assignedPlayersGlobal.Contains(p.Key))
        .OrderBy(p => p.Value.Count == 1 ? 0 : 1) // Prioritize single-role players
        .ThenBy(p => signUpOrder.IndexOf(p.Key))
        .ToList();

    foreach (var player in remainingPlayers)
    {
        var alias = player.Key;
        var preferences = player.Value;

        bool assigned = false;

        foreach (var role in preferences)
        {
            if (AssignPlayerToTeam(alias, role, team1, team1RoleCounts, ref team1OffenseCount, ref team1DefenseCount))
            {
                assigned = true;
                break;
            }
            else if (AssignPlayerToTeam(alias, role, team2, team2RoleCounts, ref team2OffenseCount, ref team2DefenseCount))
            {
                assigned = true;
                break;
            }
        }

        if (!assigned)
        {
            // Try to assign the player to a default role (oinf or dinf) depending on team needs
            if (AssignPlayerToAnyAvailableRole(alias, team1, team1RoleCounts, ref team1OffenseCount, ref team1DefenseCount))
            {
                assigned = true;
            }
            else if (AssignPlayerToAnyAvailableRole(alias, team2, team2RoleCounts, ref team2OffenseCount, ref team2DefenseCount))
            {
                assigned = true;
            }

            if (!assigned)
            {
                // If still unable to assign, inform the player
                var message = string.Format("*{0}, unable to assign you to any available role.", alias);
                arena.sendArenaMessage(message);
            }
        }
    }
}

// New method to assign player to oinf or dinf based on team needs
private bool AssignPlayerToAnyAvailableRole(string alias, List<KeyValuePair<string, string>> team, Dictionary<string, int> teamRoleCounts, ref int teamOffenseCount, ref int teamDefenseCount)
{
    if (team.Count >= 10)
        return false; // Team is full

    // Decide whether to assign to offense or defense based on team needs
    if (teamOffenseCount < 5)
    {
        // Try to assign as oinf
        if (teamRoleCounts["oinf"] < maxRoleCountsPerTeam["oinf"])
        {
            team.Add(new KeyValuePair<string, string>(alias, "oinf"));
            assignedPlayersGlobal.Add(alias);
            teamRoleCounts["oinf"]++;
            teamOffenseCount++;
            return true;
        }
    }
    if (teamDefenseCount < 5)
    {
        // Try to assign as dinf
        if (teamRoleCounts["dinf"] < maxRoleCountsPerTeam["dinf"])
        {
            team.Add(new KeyValuePair<string, string>(alias, "dinf"));
            assignedPlayersGlobal.Add(alias);
            teamRoleCounts["dinf"]++;
            teamDefenseCount++;
            return true;
        }
    }
    return false; // Unable to assign
}

// Assign player to a team if possible
private bool AssignPlayerToTeam(string alias, string role, List<KeyValuePair<string, string>> team, Dictionary<string, int> teamRoleCounts, ref int teamOffenseCount, ref int teamDefenseCount)
{
    if (teamRoleCounts[role] >= maxRoleCountsPerTeam[role])
        return false; // Role is full on this team

    if (team.Count >= 10)
        return false; // Team is full

    if (offenseRoles.Contains(role))
    {
        if (teamOffenseCount >= 5)
            return false; // Offense is full on this team
    }
    else if (defenseRoles.Contains(role))
    {
        if (teamDefenseCount >= 5)
            return false; // Defense is full on this team
    }

    team.Add(new KeyValuePair<string, string>(alias, role));
    assignedPlayersGlobal.Add(alias);
    teamRoleCounts[role]++;

    if (offenseRoles.Contains(role))
        teamOffenseCount++;
    else if (defenseRoles.Contains(role))
        teamDefenseCount++;

    return true;
}

// Ensure each team has 5 offense and 5 defense players
private void EnsureTeamsAreBalanced()
{
    // Teams are already balanced based on counts during assignment
    // Additional balancing logic can be added here if necessary
}

// Display team compositions
private void DisplayTeamComposition(string header, List<KeyValuePair<string, string>> team)
{
    var defense = team.Where(p => defenseRoles.Contains(p.Value)).ToList();
    var offense = team.Where(p => offenseRoles.Contains(p.Value)).ToList();

    arena.sendArenaMessage(header);

    arena.sendArenaMessage("Defense:");
    foreach (var player in defense)
    {
        arena.sendArenaMessage(string.Format("~{0} ({1})", player.Key, player.Value));
    }

    arena.sendArenaMessage("Offense:");
    foreach (var player in offense)
    {
        arena.sendArenaMessage(string.Format("@{0} ({1})", player.Key, player.Value));
    }
}

// Unspec players to their teams
private void UnspecPlayersToTeams(List<KeyValuePair<string, string>> team1, List<KeyValuePair<string, string>> team2)
{
    var teamCKY = arena.getTeamByName("CKY C");
    var teamPT = arena.getTeamByName("PT T");

    foreach (var player in team1)
    {
        var playerObject = FindPlayerByAlias(player.Key);
        if (playerObject != null)
        {
            playerObject.unspec(teamCKY);
        }
    }

    foreach (var player in team2)
    {
        var playerObject = FindPlayerByAlias(player.Key);
        if (playerObject != null)
        {
            playerObject.unspec(teamPT);
        }
    }
}

// Find player by alias
private Player FindPlayerByAlias(string alias)
{
    if (alias.StartsWith("Axidus"))
    {
        alias = "Axidus";
    }

    foreach (Player player in arena.Players)
    {
        if (player._alias.Equals(alias, StringComparison.OrdinalIgnoreCase))
        {
            return player;
        }
    }
    return null;
}

        public class CommandHandler
        {
            // Dictionary to map aliases to their respective class IDs
            private static readonly Dictionary<string, string> skillAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "inf", "Infantry" },
                { "infil", "Infiltrator" },
                { "hvy", "Heavy Weapons" },
                { "eng", "Combat Engineer" },
                { "med", "Field Medic" },
                { "jt", "Jump Trooper" },
                { "sl", "Squad Leader" }
            };

            /// <summary>
            /// Handles the ?swap command to change the player's class/skill.
            /// </summary>
            /// <param name="player">The player issuing the command.</param>
            /// <param name="skillName">The input string after the ?swap command.</param>
            public void HandleSwapCommand(Player player, string skillName, CfgInfo CFG, Script_CTF script)
            {
                // Check if the player is on an occupied vehicle, if so don't allow them to swap
                Vehicle occupiedVehicle = player._arena.Vehicles.FirstOrDefault(v => v._inhabitant == player);
                if (occupiedVehicle != null && occupiedVehicle._type != null && occupiedVehicle._type.Name != null && 
                    (occupiedVehicle._type.Name == "Hoverboard" || occupiedVehicle._type.Name == "Deathboard" || occupiedVehicle._type.Name == "Flight Pack" || 
                     occupiedVehicle._type.Name == "ChUcKdaPaCk" || occupiedVehicle._type.Name == "Jump Pack" || occupiedVehicle._type.Name == "Drop Pack"))
                {
                    player.sendMessage(-1, string.Format("*You cannot swap skills while in a vehicle. Vehicle Name: {0}", occupiedVehicle._type.Name));
                    return;
                } else if (occupiedVehicle != null) {
                    //player.sendMessage(0, string.Format("*Occupied Vehicle: {0} (ID: {1})", occupiedVehicle._type.Name, occupiedVehicle._type.ClassId));
                }

                // Check if the provided skill name is allowed by checking against the skillAliases keys
                if (!skillAliases.ContainsKey(skillName.Trim().ToLower()))
                {
                    player.sendMessage(-1, string.Format("*Skill '{0}' is not allowed. Allowed skills are: inf, infil, jt, sl, med, eng, hvy.", skillName));
                    return;
                }

                // Declare fullSkillName to be used for alias mapping
                string fullSkillName;

                // Normalize the skill name using the alias dictionary
                if (skillAliases.TryGetValue(skillName.Trim().ToLower(), out fullSkillName))
                {
                    skillName = fullSkillName;
                }

                // Attempt to retrieve the skill using the provided or full name
                SkillInfo skill = AssetManager.Manager.getSkillByName(skillName);

                // Check if the skill was found
                if (skill == null)
                {
                    // Notify the player if the skill was not found
                    player.sendMessage(-1, string.Format("*Skill '{0}' not found.", skillName));
                    return;
                }

                // Check if the current terrain allows skill changes or if player in spec
                int terrainID = player._arena.getTerrainID(player._state.positionX, player._state.positionY);
                if (!CFG.terrains[terrainID].skillEnabled && !player._team.IsSpec)
                {
                    player.sendMessage(-1, "*You cannot change your skill on this terrain.");
                    return;
                }

                int energyNeeded = 500;

                // Check if the player has enough energy to change the skill
                if (player._state.energy < energyNeeded)
                {
                    player.sendMessage(-1, string.Format("*You need {0} energy to change your skill. Current energy: {1}.", energyNeeded, player._state.energy));
                    return;
                }

                // Check if the player already has the skill
                if (player._skills.ContainsKey(skill.SkillId))
                {
                    player.sendMessage(0, string.Format("*You already have the skill: {0}", skill.Name));
                    return;
                }
                // Check if the skill is Infiltrator and limit per team (except in Arena 1)
                if (skill.Name == "Infiltrator" && !player._arena._name.Contains("Arena 1") && !player._arena._name.Contains("Public1"))
                {
                    // Count infiltrators only on the player's team
                    int teamInfiltratorCount = player._arena.Players
                        .Where(p => p._team == player._team && !p._team.IsSpec && p._skills.Any(s => s.Value.skill.Name == "Infiltrator"))
                        .Count();

                    if (teamInfiltratorCount >= 2)
                    {
                        player.sendMessage(-1, "Infiltrator skill purchase not allowed due to 2 infils on the team already.");
                        return;
                    }
                }

                // Update play time for current skill before changing to new skill using inline logic to retrieve the primary skill info
                SkillInfo currentSkill = null;
                if (player._skills != null && player._skills.Count > 0)
                {
                    foreach (var skillItem in player._skills.Values)
                    {
                        currentSkill = skillItem.skill;
                        break;
                    }
                }
                // Update play time for current skill before changing to new skill
                script.UpdateSkillPlayTime(player, currentSkill);
                
                // Update class swap count for player
                if (!script.playerClassSwaps.ContainsKey(player))
                    script.playerClassSwaps[player] = 0;
                script.playerClassSwaps[player]++;

                // Reset the player's inventory if the skill requires it
                if (skill.ResetInventory)
                {
                    player.resetInventory(true);
                }

                // Reset the player's skills if the skill requires it
                if (skill.ResetSkills > 0)
                {
                    player._skills.Clear();
                }

                // Add the new skill to the player's skills
                Player.SkillItem newSkill = new Player.SkillItem
                {
                    skill = skill
                };

                // Add the new skill to the player's skill dictionary
                player._skills.Add(skill.SkillId, newSkill);

                // Change the player's vehicle to match the skill's DefaultVehicleId
                if (skill.DefaultVehicleId > 0) // Check if the skill has an associated default vehicle
                {
                    player.setDefaultVehicle(AssetManager.Manager.getVehicleByID(skill.DefaultVehicleId));
                }

                // Deduct the energy required for the skill change
                player._state.energy -= (short)energyNeeded; // Deduct and cast to short to match expected type

                // Synchronize player state to reflect the new skill and vehicle
                // Reset the players warp, but keep their position
                Vehicle vehicle = player._occupiedVehicle ?? player._baseVehicle;
                if (vehicle != null)
                {
                    vehicle._state.positionX = player._state.positionX;
                    vehicle._state.positionY = player._state.positionY;
                    vehicle._state.velocityX = 0;
                    vehicle._state.velocityY = 0;
                    vehicle._state.yaw = player._state.yaw;
                    vehicle._state.direction = Helpers.ObjectState.Direction.None;
                    vehicle._state.energy = 1000;
                    vehicle._state.health = 1000;
                    vehicle._tickDead = 0;
                    vehicle.update(false);
                }
                player.syncState();

                // Notify the player that the skill has been added and vehicle changed
                //player.sendMessage(0, string.Format("*Your skill has been changed to: {0}", skill.Name));
            }

            /// <summary>
            /// Dictionary to store the key-value pairs for the short command
            /// </summary>
            private Dictionary<string, string> shortCommands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "PF Generator", "pf" },
                { "IDF Generator", "idf" },
                { "EMP Generator", "emp" },
                { "carapace", "cara" },
                { "CMP6  Assault Armor", "p6" },
                { "Kevlite Combat Armor", "kev" },
                { "Ceramax Combat Armor", "cera" },
                { "CMP4 Combat Armor", "cmp4" },
                { "CMP6 Jump Armor", "cmp6j" },
                { "Drop Armor", "drop" },
                { "Shock Armor", "shock" },
                { "Repulsor Coil", "basic" },
                { "Repulsor Charge", "basic" },
                { "Energizer", "basic" },
                { "Stim Pack", "basic" },
                { "Teleport Beacon", "basic" },
                { "Sentry", "basic" },
                { "Frag Grenade", "nades" },
                { "WP Grenade", "nades" },
                { "EMP Grenade", "nades" },
                { "Haywire Grenade", "nades" },
                { "Maklov AR mk 606", "ar" },
                { "Kuchler AR249", "ar2" },
                { "Titan Arms AR 2mv", "ar3" },
                { "Maklov RG 2", "rgs1" },
                { "Kuchler RG 249", "rgs2" },
                { "Titan Arms RG 2mv", "rgs3" },
                { "SiG Arms m2 AS", "sg" },
                { "Maklov G2 ACW", "sg2" },
                { "Titan Arms mk II CCS", "sg3" },
                { "Titan Arms mk III SG", "sg4" },
                { "Flechette Rifle", "fr" },
                { "Unittech BR2000", "br" },
                { "Titan Arms BR01", "br2" },
                { "Kamenev AKS BR", "br3" },
                { "Kuchler GR790", "gr" },
                { "Maklov GR201g", "gr2" },
                { "Titan Arms 9mm Carbine", "carb" },
                { "Unittech Tech 09 SMG", "smg1" },
                { "Unittech Tech 09 SMG (2)", "smg2" },
                { "Steiner SMG 10a", "smg3" },
                { "Unittech Tech 07m PSMG (2)", "psmg" },
                { "Needler", "n" },
                { "Deathboard", "db" },
                { "Drop Pack", "dp" },
                { "FPack", "fp" },
                { "AP Mine", "aM" },
                { "Plasma Mine", "pM" },
                { "Grapeshot Mine", "gM" },
                { "Gas Mine", "gaM" },
                { "Repulsor Mine", "repM" },
                { "Kamenev SC mk2", "sc" },
                { "Unittech SC 99r", "sc2" },
                { "Gauss Cannon", "gc" },
                { "Kuchler A6 CAW", "caw" },
                { "Maklov LMG mk6", "lmg" },
                { "Kuchler LMG249a", "lmg2" },
                { "Kuchler PC v2", "pc" },
                { "Maklov XVI PC2000", "pc2" },
                { "Maklov AC mk2", "ac" },
                { "Steiner AC 2000k", "ac2" },
                { "Steiner MG94k", "mg" },
                { "Titan Arms MG 101a", "mg2" },
                { "Micro Missile Launcher", "mml" },
                { "Mini Missile Launcher", "mml2" },
                { "Recoiless Rifle", "rr" },
                { "RR Canister", "rrc" },
                { "Maklov GL 8a", "gl" },
                { "Kuchler GL MK II", "gl2" },
                { "Reprogramming Kit", "reprog" },
                { "Engineer Repair Kit", "repair" },
                { "Medikit", "m" },
                { "Deluxe Medikit", "m2" },
                { "Steron Kit", "stk" },
                { "Stim Pack Kit", "spk" },
                { "Teleport Summoner", "ts" },
                { "Teleport Disruptor", "td" },
                { "Suit SuperCharger", "ssc" },
                { "Stealth Sensors", "ss" },
                { "Stealth Coating", "stc" },
                { "Particle Accelerator", "pa" },
                { "Heavy PowerCell", "CellH" },
                { "Medium PowerCell", "CellM" },
                { "Light PowerCell", "CellL" },
                { "Enhanced Sensors", "es" },
                { "Blink Generator", "bg" },
                { "Improved Cloak", "ic" },
                { "Cloaking Unit", "cu" },
                { "SL Beamer", "eb" },
                { "Infil Beamer", "eb" },
                { "Medic Beamer", "eb" },
                { "Engy Beamer", "eb" },
                { "Gravitron", "grav" },
                { "Plasma Bomb", "pb" },
                { "Plasma Projector", "ppr" },
                { "Disruptor", "dis" },
                { "Maklov mk IV PR", "pr" },
                { "Kuchler PR209g", "pr2" },
                { "Pulsar", "pu" },
                { "Stunner", "stun" },
                { "Incinerator", "inc" },
                { "Flamethrower", "ft" },
                { "Kuchler CR 102", "cr" },
                { "Maklov CR mk IV", "cr2" },
                { "Maklov g7 PSMG", "PSMG2" },
                { "Kuchler 201a PSMG", "PSMG3" },
                { "Kamenev AP mk2", "ap" },
                { "Kamenev AP mk2 (2)", "ap2" },
                { "Titan Arms AP101", "ap3" },
                { "Holo-Taunt EZ", "htEZ" },
                { "Holo-Taunt GG", "htGG" },
                { "Turret Box", "tbox" }
                // Add more key-value pairs here as needed
            };

            /// <summary>
            /// Handles the ?short command to output the short versions of given input(s) in a ?buy macro format.
            /// </summary>
            /// <param name="player">The player issuing the command.</param>
            /// <param name="input">The input string after the ?short command.</param>
            public void HandleShortCommand(Player player, string input)
            {
                var validShortForms = new HashSet<string>(); // Use a set to prevent duplicates
                var invalidItems = new List<string>();
                var groupedItems = new Dictionary<string, int>(); // Dictionary to handle occurrences of grouped items

                // Check if input is empty; if so, generate buy macro from current inventory
                if (string.IsNullOrWhiteSpace(input))
                {
                    // Iterate through the player's inventory
                    foreach (var inventoryEntry in player._inventory.Values)
                    {
                        string itemName = inventoryEntry.item.name.ToLower().Trim(); // Normalize item name
                        int quantity = inventoryEntry.quantity;

                        // Check if the inventory item has a shorthand in the shortCommands dictionary
                        string shortForm;

                        // Check for grouped items based on the shortCommands dictionary
                        if (shortCommands.TryGetValue(itemName, out shortForm))
                        {
                            string form = shortForm;

                            // Check if the form is part of any grouped item
                            if (form == "basic" || form == "nades")
                            {
                                if (!groupedItems.ContainsKey(form))
                                    groupedItems[form] = 0;
                                groupedItems[form] += 1; // Increment count for the specific group
                            }
                            else
                            {
                                // Add the shorthand without :1 if quantity is 1
                                validShortForms.Add(quantity == 1 ? form : string.Format("{0}:{1}", form, quantity));
                            }
                        }
                        else if (shortCommands.ContainsValue(itemName)) // Handle direct short form entries
                        {
                            validShortForms.Add(quantity == 1 ? itemName : string.Format("{0}:{1}", itemName, quantity));
                        }
                        else
                        {
                            // Include the full item name with its quantity if no shorthand exists
                            validShortForms.Add(quantity == 1 ? itemName : string.Format("{0}:{1}", itemName, quantity));
                            invalidItems.Add(itemName);
                        }
                    }

                    // Add grouped items to validShortForms if they exist
                    foreach (var group in groupedItems.Keys)
                    {
                        validShortForms.Add(group); // Add each group found
                    }

                    // Notify the player about unmatched items, prefixed with @ to color the message red
                    if (invalidItems.Count > 0)
                    {
                        player.sendMessage(-1, string.Format("@No short form found for: {0}", string.Join(", ", invalidItems)));
                    }

                    // Send the formatted valid short forms as a single output to the player with a space after ?b and no spaces between items
                    if (validShortForms.Count > 0)
                    {
                        player.sendMessage(0, string.Format("?bw {0}", string.Join(",", validShortForms)));
                    }
                    else
                    {
                        player.sendMessage(0, "No items found in inventory with matching short forms.");
                    }

                    return; // Exit the method after processing inventory
                }

                // Split the input string by commas to handle multiple items
                var items = input.Split(',');

                foreach (var item in items)
                {
                    // Trim the item and handle possible quantity or prefix notation
                    var trimmedItem = item.Trim();
                    string quantityPrefix = string.Empty;
                    string cleanedItem = trimmedItem;

                    // Extract quantity or special prefixes like '#', '-'
                    var match = Regex.Match(trimmedItem, @"(:[#-]?\d+)$");
                    if (match.Success)
                    {
                        quantityPrefix = match.Groups[1].Value;
                        cleanedItem = trimmedItem.Substring(0, match.Index).Trim();
                    }

                    string shortForm;

                    // Normalize the cleaned item (trim spaces and convert to lowercase) before lookup
                    string normalizedItem = cleanedItem.Trim().ToLower();

                    // Check if the cleaned item exists in the dictionary as a key or if it is already a short form
                    if (shortCommands.TryGetValue(normalizedItem, out shortForm) || shortCommands.ContainsValue(normalizedItem))
                    {
                        // If it's already a short form (value in the dictionary), use it as is, otherwise, use the mapped short form
                        string form = shortCommands.ContainsValue(normalizedItem) ? normalizedItem : shortForm;

                        // Add without the quantity if it is 1
                        validShortForms.Add(quantityPrefix == ":1" ? form : string.Format("{0}{1}", form, quantityPrefix));
                    }
                    else
                    {
                        // Add the full item if not found or already short
                        validShortForms.Add(string.Format("{0}{1}", cleanedItem, quantityPrefix));
                        invalidItems.Add(cleanedItem);
                    }
                }

                // Notify about invalid items that couldn't be matched to a short form
                if (invalidItems.Count > 0)
                {
                    player.sendMessage(-1, string.Format("@No short form found for: {0}", string.Join(", ", invalidItems)));
                }

                // Send the formatted valid short forms as a single output to the player with a space after ?b
                if (validShortForms.Count > 0)
                {
                    player.sendMessage(0, string.Format("?b {0}", string.Join(",", validShortForms)));
                }
            }
        }

        private Dictionary<string, PlayerStreak> killStreaks;
        private Player lastKiller;

        private Dictionary<string, int> explosives;
        private string[] explosiveList = { "Frag Grenade", "WP Grenade", "EMP Grenade", "Kuchler RG 249", "Maklov RG 2", "Titan Arms RG 2mv", "AP Mine",
                                        "Plasma Mine", "Grapeshot Mine", "RPG", "Micro Missle Launcher", "Recoilless Rifle", "Kuchler PC v2",
                                        "Maklov XVI PC2000" };
        //Note: these corrispond with the weapons above in order
        private int[] explosiveAliveTimes = { 250, 250, 250, 500, 500, 500, 500, 100, 250, 500, 500, 500, 450, 450 };

        #endregion

        private void InitializeMaps()
        {
            // Initialize our hardcoded maps. Note that we should really move these into a json file eventually.
            // NOTE: This is to be called _after_ `_flags` is initialized because it depends on whether the
            // arena is OVD or not.

            availableMaps.Clear();

            CTFMap def = new CTFMap();
            def.MapName = "default";
            def.TeamNames.Add(CFG.teams[0].name);
            def.TeamNames.Add(CFG.teams[1].name);

            // For default, we are interested in the original flag placement; so we will extract
            // those. Note that if this code does not work, we will probably have to investigate
            // at what point in time we need to query the flags to get the position.

            // Add dummy flags based on however many flags the arena actually has, because we will be
            // randomizing their positions anyway.
            def.RandomizeFlagLocations = true;

            foreach (var fs in _flags)
            {
                var flagName = fs.flag.GeneralData.Name.ToLower().Trim('\"');

                def.Flags.Add(new MapFlagEntry(flagName, 202, 118));
            }

            availableMaps.Add(def);

            CTFMap full = new CTFMap();
            full.MapName = "full";
            full.TeamNames.Add("Titan Militia");
            full.TeamNames.Add("Collective");
			full.Flags.Add(new MapFlagEntry("Hill201", 54, 32));
            full.Flags.Add(new MapFlagEntry("Bridge1", 202, 120));
            full.Flags.Add(new MapFlagEntry("Bridge2", 202, 202));
            full.Flags.Add(new MapFlagEntry("Bridge3", 202, 286));
            full.Flags.Add(new MapFlagEntry("Hill86", 316, 338));
            full.Flags.Add(new MapFlagEntry("sdFlag", 202, 202));

            availableMaps.Add(full);

            CTFMap bravo = new CTFMap();
            bravo.MapName = "bravo";
            bravo.TeamNames.Add(CFG.teams[0].name); // these _should_ be the default two teams.
            bravo.TeamNames.Add(CFG.teams[1].name);
            bravo.Flags.Add(new MapFlagEntry("flag1", 500, 1333));
            bravo.Flags.Add(new MapFlagEntry("flag2", 500, 1213));
            bravo.Flags.Add(new MapFlagEntry("flag3", 744, 1564));
            bravo.Flags.Add(new MapFlagEntry("flag4", 864, 1564));

            availableMaps.Add(bravo);

            currentMap = full; // Set full map as the ... default ... event.
        }

        #region Game Functions
        //////////////////////////////////////////////////
        // Game Functions
        //////////////////////////////////////////////////
        /// <summary>
        /// Performs script initialization
        /// </summary>
        public bool init(IEventObject invoker)
        {
            arena = invoker as Arena;
            CFG = arena._server._zoneConfig;

            _flags = new List<Arena.FlagState>();
            minPlayers = 2;
            victoryHoldTime = CFG.flag.victoryHoldTime;
            preGamePeriod = CFG.flag.startDelay;

            killStreaks = new Dictionary<string, PlayerStreak>();
            explosives = new Dictionary<string, int>();

            bases = new Dictionary<string, Base>();

            for (int i = 0; i < explosiveList.Length; i++)
            {
                explosives.Add(explosiveList[i], explosiveAliveTimes[i]);
            }

            foreach (Arena.FlagState fs in arena._flags.Values)
            {	//Determine the minimum number of players
                if (fs.flag.FlagData.MinPlayerCount < minPlayers)
                { minPlayers = fs.flag.FlagData.MinPlayerCount; }

                //Register our flag change events
                fs.TeamChange += OnFlagChange;
            }

            gameState = GameState.Init;

            bases["A7"] = new Base(19, 463, 32, 473);
            bases["D7"] = new Base(277, 495, 262, 484);
            bases["F8"] = new Base(450, 610, 466, 626);
            bases["F5"] = new Base(422, 370, 413, 357);
            bases["A5"] = new Base(57, 359, 72, 354);
            bases["B6"] = new Base(153, 445, 133, 456);
            bases["A8"] = new Base(22, 574, 36, 562);
            bases["A10"] = new Base(100, 733, 107, 749);
            bases["F6"] = new Base(440, 456, 429, 446);
            bases["H4"] = new Base(600, 280, 621, 295);

            if (!arena._name.Contains("Arena 1") && !arena._name.Contains("Public1"))
            {
                foreach (Arena.FlagState fs in arena._flags.Values)
                {
                    if (fs.flag.FlagData.MinPlayerCount == 200)
                        _flags.Add(fs);     
                }

                playing = new Team(arena, arena._server);
                playing._name = "Playing";
                playing._id = (short)arena.Teams.Count();
                playing._password = "";
                playing._owner = null;
                playing._isPrivate = true;
                arena.createTeam(playing);

                isOVD = true;
            }
            else
            {
                _playerStatsEnabled = false;
                foreach (Arena.FlagState fs in arena._flags.Values)
                {
                    if (fs.flag.FlagData.MinPlayerCount == 0)
                        _flags.Add(fs);
                }
            }

            InitializeMaps();

            // Instantiate the BuildManager
            BuildManager buildManager = new BuildManager();

            // Define build inputs dynamically
            List<Tuple<string, string, string, string, string>> buildInputs = new List<Tuple<string, string, string, string, string>>
            {
                new Tuple<string, string, string, string, string>("ohvyAxi", "?buy kev,pf,ssc,RPG,AC,LMG,ammo mg:40,rocket:20,ammo rifle:40,Basic", 
                                                                "*Base attacking; dueling power, low ammo, designed for pushing defense back then doing turret damage.", 
                                                                "Heavy Weapons", "Axidus"),
                new Tuple<string, string, string, string, string>("ohvyAxi2", "?buy kev,pf,CellH,rpg,smg1,ac,ammo mg:#50,ammo pistol:#70,rocket:#20,frag grenade:#2,emp mine:#3,basic", 
                                                                "Base attacking; SMG instead of LMG allows to carry more AC and a Heavy PowerCell. Weaker dueling power but a full clip of SMG + Demo pack still packs a punch to get them away long enough to finish with RPG", 
                                                                "Heavy Weapons", "Axidus"),                                                                
                new Tuple<string, string, string, string, string>("dhvyAxi", "?buy kev,pf,ssc,MML,AC,ammo mg:125,rocket:26,bullet mine:#5,plasma mine:#1,ap mine:#1,Basic", 
                                                                "2 Weapon Defensive build focused on AOE weapons (high ammo count).", 
                                                                "Heavy Weapons", "Axidus"),
                new Tuple<string, string, string, string, string>("oinf", "?buy p6,pf,ssc,ar,ar3,sg,Ammo Rifle:#60,Ammo Shotgun:#60,rgs1:#4,rgs3:#4,nades:4,basic,bullet mine:#5", 
                                                                "Double RG oinf w/ p6+pf+ssc, 8 rgs, 8 grenades, and 50ar/30sg ammo", 
                                                                "Infantry", "Roman Reigns"),
                new Tuple<string, string, string, string, string>("dinf", "?buy cmp6,pf,ssc,es,ar,sg2,rg:4,nades,basic,plasma mine:2,bullet mine:#10,ammo rifle:#140,ammo shotgun:#155", 
                                                                "Shotgun dinf w/ p6+pf+ssc, 100ar/50sg ammo, 4 rgs, 8 grenades, 10 bullet mines, 2 plasma. ", 
                                                                "Infantry", "Roman Reigns"), 
                new Tuple<string, string, string, string, string>("dinfcaw", "?buy cmp6,pf,ssc,caw,ar,es,basic,nades,rg:4,ammo pistol:#300,plasma mine:2,bullet mine:#10,ammo rifle:#100", 
                                                                "CAW dinf w/ p6+pf+ssc, 300pistol/100ar ammo, 4 rgs, 8 grenades, 10 bullet mines, 2 plasma. ", 
                                                                "Infantry", "Axidus"),
                new Tuple<string, string, string, string, string>("dinfcara", "?buy cara,pf,ssc,ar,sg2,nades,basic,rg:4,Ammo Rifle:#90,Ammo Shotgun:#105,Bullet Mine:#10,plasma mine:2", 
                                                                "Cara/PF dinf w/ 120ar/120sg ammo, 4 rgs, 8 grenades", 
                                                                "Infantry", "Roman Reigns"), 
                new Tuple<string, string, string, string, string>("oinfcara", "?buy cara,idf,ssc,ar,sg2,nades,basic,rg:4,Ammo Rifle:#120,Ammo Shotgun:#120,Bullet Mine:#10", 
                                                                "Cara/IDF oinf w/ 120ar/120sg ammo, 4 rgs, 8 grenades", 
                                                                "Infantry", "metal"), 
                new Tuple<string, string, string, string, string>("slBR", "?buy P6,PF,SSC,EB,TD,BR,CR,nades,basic,Ammo Pistol:#50,Ammo Rifle:#50,Ammo MG:#5,grapeshot mine:5,ap mine:#3", 
                                                                "Weapons: BR/CR/EB. 50ar/50pi ammo, 5 grapeshot mine, 3 ap mine, handful of Ammo MG for dropping to hide mines.", 
                                                                "Squad Leader", "metal"),                                                                                                       
                new Tuple<string, string, string, string, string>("slbonds", "?buy cmp6,emp,ssc,sl beamer,td,kuchler cr 102,unittech br2000,ammo rifle:#35,ammo pistol:#40,basic,nades,emp mine:#4,grapeshot mine:#1,ap mine:#4,ammo mg:#4", 
                                                                "Weapons: BR/CR/EB. Light on ammo, plenty of mines, handful of Ammo MG for dropping to hide mines.", 
                                                                "Squad Leader", "bonds"),
                new Tuple<string, string, string, string, string>("medbonds", "?buy cmp4,emp,medic beamer,ssc,td,teleport summoner,needler,medikit,deluxe medikit,smg1,es,stunner,ammo pistol:#120,basic,frag grenade:4,tranq:10", 
                                                                "*Standard medic build. Weapons: SMG/EB. Utility items (ES, TD).", 
                                                                "Field Medic", "bonds"),
                new Tuple<string, string, string, string, string>("eng", "?buy basic,nades,fr,inc,eb,kev,ssc,pf,hoverboard,tbox,nades,basic,grapeshot mine:5,repair,ammo pistol:500,fuel canister:#50,ammo shotgun:#100", 
                                                                "Standard engineer build. Weapons: Flechette/EB/Incin/Grenades/grapeshot Mines. Utility items (Kevlite, SSC, PF, Hoverboard, Turret box, repair kit).", 
                                                                "Combat Engineer", "default"),
                new Tuple<string, string, string, string, string>("jt", "?buy drop,pf,CellH,ar2,ar,ar3,gl,dp,rgs1,rgs2,rgs3,nades,aM,pM,Ammo Rifle:#200,Light HE:#50,Ammo MG:#1,basic", 
                                                                "DropPack JT. Weapons: AR, Maklov GL, 12 RG, nades, mines", 
                                                                "Jump Trooper", "default"),
                new Tuple<string, string, string, string, string>("footjt", "?buy drop,pf,CellH,ar2,ar,ar3,gl,dp,rgs1,rgs2,rgs3,nades,aM,pM,Ammo Rifle:#200,Light HE:#50,Ammo MG:#1,basic", 
                                                                "Foot JT. Weapons: AR, Maklov GL, 4 RG, nades, mines", 
                                                                "Jump Trooper", "default"),
                new Tuple<string, string, string, string, string>("infil", "?buy pr,dis,blink,eb,basic", 
                                                                "Infiltrator Weapons: PR, EB, Disruptor, Blink Gen.  Utility: Cloak/SSC", 
                                                                "Infiltrator", "default"),
                new Tuple<string, string, string, string, string>("slherth", "?buy p6,emp,CellM,eb,cr,td,inc,ammo pistol:100,gM:5,aM:2,ammo mg:3,fuel canister:40,basic,nades", 
                                                                "P6/EMP/CellM/EB/CR/TD/Inc/100pistol/5GMines/2AMines/3AmmoMG/40fuel",
                                                                "Squad Leader", "Herthbul")                                                             
            };

            // Convert ?buy commands to a dictionary format
            buildSets = buildManager.ConvertBuyCommandsToDictionary(buildInputs);
            currentEventType = (arena._name.Contains("Arena 1") || arena._name.Contains("Public1")) ? EventType.MiniTP : EventType.None;

            return true;
        }

        /// <summary>
        /// Retrieves all items from the player's inventory and sends the details to the player.
        /// </summary>
        /// <param name="player">The player whose inventory is to be retrieved and displayed.</param>
        public void DisplayInventoryItems(Player player)
        {
            // Check if the inventory is not empty
            if (player._inventory == null || player._inventory.Count == 0)
            {
                player.sendMessage(0, "Your inventory is empty.");
                return;
            }

            // Iterate through each item in the inventory
            foreach (var inventoryEntry in player._inventory.Values)
            {
                string itemName = inventoryEntry.item.name; // Get the item name
                int quantity = inventoryEntry.quantity; // Get the quantity

                // Send a message to the player with the item details
                player.sendMessage(0, string.Format("Item: {0}, Quantity: {1}", itemName, quantity));
            }
        }

        /*//////////////////////////////////////////////////
        // List of ?builds to simplify buy macro setups per class
        *///////////////////////////////////////////////////
        // Dictionary to store build sets with items and descriptions

        /* To do: 
            -Implement tags for builds (IE: Sustain, Aggressive, Versatile, Default), 
            -Alias of build creator
            -class
            -randomize function (IE: ?build hvy random)
            -database of builds so players can add/modify/delete and manage ingame    

            done: 
            -mirror ?build to ?buy customBuildName logic (see if its possible)    
        */

        private Dictionary<string, Tuple<List<Tuple<string, ushort>>, string, string, string, string>> buildSets;


        private static Dictionary<string, int> maxAmmoQuantities = new Dictionary<string, int>
        {
            { "heavy he", 20 },
            { "light he", 50 },
            { "fuel canister", 120 },
            { "ammo rifle", 400 },
            { "ammo pistol", 500 },
            { "ammo shotgun", 200 },
            { "ammo mg", 250 },
            { "rocket", 40 },
            { "ap mine", 5 },
            { "plasma mine", 5 },
            { "tranq", 10 },
            { "sentry", 1 }
            // Add other ammo types here
        };

        private bool BuildTurret(Player player, string turretName)
            {
                // Attempt to find the nearest computer
                Computer nearestComputer = null;
                int shortestDistance = int.MaxValue;

                // Iterate over computers in the arena to find the closest one
                foreach (Computer computer in player._arena.Vehicles.OfType<Computer>())
                {
                    int distance = (int)((player._state.position() - computer._state.position()).Length * 100);
                    if (distance < shortestDistance)
                    {
                        shortestDistance = distance;
                        nearestComputer = computer;
                    }
                }

                // Check if a nearest computer was found
                if (nearestComputer == null)
                {
                    player.sendMessage(-1, string.Format("No nearby computers detected."));
                    return false;
                }

                // Access the computer's produce options
                var produceOptions = nearestComputer._type.Products.ToList();

                // Ensure produce options are available
                if (produceOptions == null || produceOptions.Count == 0)
                {
                    player.sendMessage(-1, string.Format("No production options available."));
                    return false;
                }

                // Find the corresponding turret based on the turret name parameter
                var selectedProduct = produceOptions.FirstOrDefault(p => p.Title.Equals(turretName, StringComparison.OrdinalIgnoreCase));

                // Check if the product exists
                if (selectedProduct == null)
                {
                    player.sendMessage(-1, string.Format("Unable to find {0} in the production list.", turretName));
                    return false;
                }

                // Trigger the production logic
                player.sendMessage(0, string.Format("Producing: {0}", selectedProduct.Title));
                player._arena.produceRequest(player, nearestComputer, selectedProduct);

                return true;
            }

        private bool BuildTurretAtLocation(Player player, string turretName, short x, short y)
        {
            // Get the vehicle info for the turret type
            VehInfo turretInfo;
            switch (turretName.ToLower())
            {
                case "mg":
                    turretInfo = AssetManager.Manager.getVehicleByID(400);
                    break;
                case "rocket": 
                    turretInfo = AssetManager.Manager.getVehicleByID(401);
                    break;
                case "plasma":
                    turretInfo = AssetManager.Manager.getVehicleByID(700);
                    break;
                case "sentry":
                    turretInfo = AssetManager.Manager.getVehicleByID(402);
                    break;
                default:
                    player.sendMessage(-1, "Invalid turret type specified.");
                    return false;
            }

            // Create the object state at specified x,y coordinates
            Helpers.ObjectState objState = new Helpers.ObjectState
            {
                positionX = x,
                positionY = y,
                positionZ = 0 // Set to ground level
            };

            // Create the turret directly
            Vehicle turret = player._arena.newVehicle(turretInfo, player._team, player, objState);
            return true;
        }

        private bool BuildServerTurretAtLocation(string turretName, short x, short y, Team team)
        {
            // Get the vehicle info for the turret type
            VehInfo turretInfo = null;
            switch (turretName.ToLower())
            {
                case "mg":
                    turretInfo = AssetManager.Manager.getVehicleByID(429); // 429 or 400
                    break;
                case "rocket": 
                    turretInfo = AssetManager.Manager.getVehicleByID(428); // 428 or 401
                    break;
                case "plasma":
                    turretInfo = AssetManager.Manager.getVehicleByID(427); // 427 or 700
                    break;
                case "sentry":
                    turretInfo = AssetManager.Manager.getVehicleByID(402);
                    break;
                case "homeportal":
                    turretInfo = AssetManager.Manager.getVehicleByID(438);
                    break;
                default:
                    return false;
            }

            if (turretInfo == null)
                return false;

            // Create the object state at specified x,y coordinates
            Helpers.ObjectState objState = new Helpers.ObjectState
            {
                positionX = x,
                positionY = y,
                positionZ = 0
            };
            Vehicle turret = arena.newVehicle(turretInfo, team, null, objState);
            return true;
        }

        private bool BuildMiniTPTurrets()
        {
            // Get the first two teams
            Team team1 = arena.Teams.FirstOrDefault(t => t._name.Equals("Titan Militia"));
            Team team2 = arena.Teams.FirstOrDefault(t => t._name.Equals("Collective"));

            if (team1 == null || team2 == null)
                return false;

            BuildServerTurretAtLocation("MG", (short)(1094 * 16), (short)(627 * 16), team1);
            BuildServerTurretAtLocation("MG", (short)(1077 * 16), (short)(636 * 16), team1);
            BuildServerTurretAtLocation("Rocket", (short)(1089 * 16), (short)(635 * 16), team1);
            BuildServerTurretAtLocation("Plasma", (short)(1076 * 16), (short)(639 * 16), team1);
            BuildServerTurretAtLocation("Sentry", (short)(1081 * 16), (short)(630 * 16), team1);
            BuildServerTurretAtLocation("Sentry", (short)(1101 * 16), (short)(634 * 16), team1);
            BuildServerTurretAtLocation("homeportal", (short)(1077 * 16), (short)(628 * 16), team1);

            BuildServerTurretAtLocation("MG", (short)(977 * 16), (short)(632 * 16), team2);
            BuildServerTurretAtLocation("MG", (short)(977 * 16), (short)(622 * 16), team2);
            BuildServerTurretAtLocation("Rocket", (short)(991 * 16), (short)(628 * 16), team2);
            BuildServerTurretAtLocation("Plasma", (short)(976 * 16), (short)(639 * 16), team2);
            BuildServerTurretAtLocation("Sentry", (short)(1002 * 16), (short)(629 * 16), team2);
            BuildServerTurretAtLocation("Sentry", (short)(985 * 16), (short)(638 * 16), team2);
            BuildServerTurretAtLocation("homeportal", (short)(976 * 16), (short)(649 * 16), team2);

            return true;
        }

        /// <summary>
        /// Example usage of BuildTurretAtLocation
        /// </summary>
        private void BuildTurretExample(Player player)
        {
            // Get player's current position
            short playerX = player._state.positionX;
            short playerY = player._state.positionY;

            // Build different types of turrets around the player
            BuildTurretAtLocation(player, "MG", playerX, playerY); // Build MG turret at player location
            BuildTurretAtLocation(player, "Rocket", (short)(playerX + 100), playerY); // Build rocket turret 100 units to the right
            BuildTurretAtLocation(player, "Plasma", playerX, (short)(playerY + 100)); // Build plasma turret 100 units up
            BuildTurretAtLocation(player, "Sentry", (short)(playerX - 100), playerY); // Build sentry turret 100 units to the left

            player.sendMessage(0, "Built example turrets around your position");
        }

        private void HandleBuildCommand(Player player, string buildName)
        {
            // Can you buy from this location?
            if ((player._arena.getTerrain(player._state.positionX, player._state.positionY).storeEnabled) || (player._team.IsSpec && player._server._zoneConfig.arena.spectatorStore))
            {
                // Get the player's current class
                string playerClass = GetPrimarySkillName(player);

                if (string.IsNullOrWhiteSpace(buildName))
                {
                    // List the description of the different payloads available
                    player.sendMessage(0, "#------------------------");
                    player.sendMessage(0, "~?build <buildName> ---- Applies the specified build.");
                    player.sendMessage(0, "~?build list ---- Lists all builds available for your class.");
                    player.sendMessage(0, "~?build <buildName> info ---- Displays information about a specific build.");
                    player.sendMessage(0, "~?build random ---- Selects a random build for your class.");
                    player.sendMessage(0, "#------------------------");
                    player.sendMessage(0, "~?b <buildName> ---- short for ?build");
                    player.sendMessage(0, "~?bw <buildName> ---- wipes inventory, then purchases build");
                    player.sendMessage(0, "~?bwd <buildName> ---- wipes inventory, purchases build + excess ammo for drops (then use command ?d to drop excess ammo)");
                    player.sendMessage(0, "~?d ---- drops excess ammo after using ?bwd <buildName> command");
                    player.sendMessage(0, "#------------------------");
                    return;
                }

                // Check for "list" payload
                if (buildName.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    // Handle listing all builds for player's class
                    var buildsForClass = buildSets
                        .Where(b => b.Value.Item4.Equals(playerClass, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (buildsForClass.Count > 0)
                    {
                        player.sendMessage(0, "&Available builds for your class '" + playerClass + "':");
                        foreach (var build in buildsForClass)
                        {
                            string buildNameToDisplay = build.Key;
                            string description = build.Value.Item2;
                            string contributedBy = build.Value.Item5;

                            // Use different starting characters in player.sendMessage to format nicely
                            player.sendMessage(0, "*" + buildNameToDisplay + ": " + description + " (Contributed By: " + contributedBy + ")");
                        }
                    }
                    else
                    {
                        player.sendMessage(-1, "No builds available for your class '" + playerClass + "'.");
                    }
                    return;
                }

                // Check for "info" payload
                if (buildName.EndsWith(" info", StringComparison.OrdinalIgnoreCase))
                {
                    // Get the build name without the " info" suffix
                    string buildNameWithoutInfo = buildName.Substring(0, buildName.Length - 5).Trim();

                    if (buildSets.ContainsKey(buildNameWithoutInfo.ToLower()))
                    {
                        var buildData = buildSets[buildNameWithoutInfo.ToLower()];

                        string description = buildData.Item2;
                        string buyCommand = buildData.Item3;
                        string classType = buildData.Item4;
                        string contributedBy = buildData.Item5;

                        // Display the build information using player.sendMessage and string formatting
                        player.sendMessage(0, "#------------------------");
                        player.sendMessage(0, "Build Name: " + buildNameWithoutInfo + "       Contributed By: " + contributedBy);
                        player.sendMessage(0, "&" + description);
                        player.sendMessage(0, "*" + buyCommand);
                        player.sendMessage(0, "#------------------------");
                    }
                    else
                    {
                        player.sendMessage(-1, "Build '" + buildNameWithoutInfo + "' not found.");
                    }
                    return;
                }

                // Check if the buildName is "random"
                if (buildName.Equals("random", StringComparison.OrdinalIgnoreCase))
                {
                    var availableBuilds = buildSets
                        .Where(b => b.Value.Item4.Equals(playerClass, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (availableBuilds.Count > 0)
                    {
                        // Select a random build
                        var randomBuild = availableBuilds[new Random().Next(availableBuilds.Count)];
                        buildName = randomBuild.Key;
                        player.sendMessage(0, "Selected random build: " + buildName);
                    }
                    else
                    {
                        player.sendMessage(-1, "No available builds for your class.");
                        return;
                    }
                }

                // Now proceed to handle the buildName, which may include multiple items/builds, split by commas
                // Split the buildName by commas to handle multiple items
                string[] itemRequests = buildName.Split(',');

                foreach (string request in itemRequests)
                {
                    // Split each request by colon to separate item name and quantity
                    string[] parts = request.Split(':');
                    string itemName = parts[0].Trim().ToLower(); // Normalize item name
                    string quantitySpecifier = parts.Length > 1 ? parts[1].Trim() : "1"; // Default quantity is additive 1

                    // Check if it's a build
                    if (buildSets.ContainsKey(itemName))
                    {
                        // Handle build logic
                        player.sendMessage(0, "*Found build: " + itemName + ". Processing items...");
                        var buildData = buildSets[itemName];
                        List<Tuple<string, ushort>> buildItems = buildData.Item1;

                        bool containsPowercell = buildItems.Any(item => 
                            item.Item1.Equals("cellh", StringComparison.OrdinalIgnoreCase) ||
                            item.Item1.Equals("cellm", StringComparison.OrdinalIgnoreCase) ||
                            item.Item1.Equals("celll", StringComparison.OrdinalIgnoreCase) ||
                            item.Item1.Equals("heavy powercell", StringComparison.OrdinalIgnoreCase) ||
                            item.Item1.Equals("medium powercell", StringComparison.OrdinalIgnoreCase) ||
                            item.Item1.Equals("light powercell", StringComparison.OrdinalIgnoreCase));

                        // If build contains a powercell and player has 500+ energy, give them NRG Restore
                        // if (containsPowercell && player._state.energy >= 500)
                        // {
                        //     ItemInfo nrgRestore = AssetManager.Manager.getItemByName("NRG Restore");
                        //     if (nrgRestore != null)
                        //     {
                        //         player.inventoryModify(nrgRestore, 1);
                        //     }
                        // }

                        foreach (var item in buildItems)
                        {
                            ItemInfo prize = AssetManager.Manager.getItemByName(item.Item1);
                            if (prize != null)
                            {
                                int desiredQuantity = item.Item2;
                                int currentQuantity = player.getInventoryAmount(prize.id);

                                // By default, apply fixed quantity logic
                                if (currentQuantity < desiredQuantity)
                                {
                                    // Award the difference to reach the desired quantity
                                    int amountToAdd = desiredQuantity - currentQuantity;
                                    player.inventoryModify(prize, amountToAdd);
                                }
                                else if (currentQuantity > desiredQuantity)
                                {
                                    // Remove the excess to match the desired quantity
                                    int amountToRemove = currentQuantity - desiredQuantity;
                                    player.inventoryModify(prize, -amountToRemove);
                                    player.sendMessage(0, "~" + amountToRemove + "x" + prize.name + " has been removed to match the desired quantity of " + desiredQuantity + ".");
                                }
                            }
                            else
                            {
                                player.sendMessage(-1, "@Failed to find item " + item.Item1 + ".");
                            }
                        }
                    }
                    else
                    {
                        // Handle store item logic
                        ItemInfo storeItem = AssetManager.Manager.getItemByName(itemName);
                        if (storeItem != null)
                        {
                            // Check if this is a powercell purchase
                            bool isPowercell = itemName.Equals("cellh", StringComparison.OrdinalIgnoreCase) ||
                                             itemName.Equals("cellm", StringComparison.OrdinalIgnoreCase) ||
                                             itemName.Equals("celll", StringComparison.OrdinalIgnoreCase) ||
                                             itemName.Equals("heavy powercell", StringComparison.OrdinalIgnoreCase) ||
                                             itemName.Equals("medium powercell", StringComparison.OrdinalIgnoreCase) ||
                                             itemName.Equals("light powercell", StringComparison.OrdinalIgnoreCase);

                            // If it's a powercell and player has 500+ energy, give them NRG Restore
                            // if (isPowercell && player._state.energy >= 500)
                            // {
                            //     ItemInfo nrgRestore = AssetManager.Manager.getItemByName("NRG Restore");
                            //     if (nrgRestore != null)
                            //     {
                            //         player.inventoryModify(nrgRestore, 1);
                            //     }
                            // }

                            // Determine if the command specifies fixed quantity or additive quantity
                            bool isFixed = quantitySpecifier.StartsWith("#");
                            int quantity = int.Parse(quantitySpecifier.TrimStart('#'));

                            if (isFixed)
                            {
                                // Fixed quantity logic: Match the exact quantity, removing excess if necessary
                                int currentQuantity = player.getInventoryAmount(storeItem.id);

                                if (currentQuantity < quantity)
                                {
                                    // Award the difference to reach the desired quantity
                                    int amountToAdd = quantity - currentQuantity;
                                    player.inventoryModify(storeItem, amountToAdd);
                                }
                                else if (currentQuantity > quantity)
                                {
                                    // Remove the excess to match the desired quantity
                                    int amountToRemove = currentQuantity - quantity;
                                    player.inventoryModify(storeItem, -amountToRemove);
                                    player.sendMessage(0, "~" + amountToRemove + "x" + storeItem.name + " has been removed to match the desired quantity of " + quantity + ".");
                                }
                            }
                            else
                            {
                                // Additive quantity logic: Just add the amount to inventory
                                player.inventoryModify(storeItem, quantity);
                            }
                        }
                        else
                        {
                            player.sendMessage(-1, "Item '" + itemName + "' not found.");
                        }
                    }
                }
                Dictionary<string, int> conversions = GetChampionConversionsForPlayer(player);
                if (conversions != null)
                {
                    bool autoEnabled = false;
                    if (_autoConvertEnabled.TryGetValue(player, out autoEnabled) && autoEnabled && isChampEnabled)
                    {
                        // Convert any eligible items in the player's inventory.
                        ConvertChampItems(player);
                    }
                }
            }
            else
            {
                player.sendMessage(-1, "You cannot purchase builds from this location");
            }
        }

        private void WipeInventory(Player player, string payload)
        {
            int terrainID = player._arena.getTerrainID(player._state.positionX, player._state.positionY);
            if (terrainID == 4 || terrainID == 3 || terrainID == 1 || terrainID == 2 || player.IsSpectator){
                player.resetInventory(true);
                player.sendMessage(0, "Your inventory has been wiped.");
            } else {
                player.sendMessage(0, "Cannot wipe inventory from this location.");
            }
        }

        // Dictionary to remember the last used build for each player
        private Dictionary<Player, string> lastUsedBuild = new Dictionary<Player, string>();

        // Wipes inventory, then buys the build
        private void WipeAndBuy(Player player, string buildName)
        {
            int terrainID = player._arena.getTerrainID(player._state.positionX, player._state.positionY);
            if (terrainID == 4 || terrainID == 3 || terrainID == 1 || terrainID == 2 || player.IsSpectator){
                // Take note of the items and their quantities before resetting the inventory
                Dictionary<int, int> itemsToGiveBack = new Dictionary<int, int>();
                HashSet<int> itemsToDeprive = new HashSet<int> { 2005, 2007, 2009, 9, 10, 11 };
                foreach (int itemID in itemsToDeprive)
                {
                    int currentCount = player.getInventoryAmount(itemID);
                    itemsToGiveBack.Add(itemID, currentCount);
                }

                player.resetInventory(true);
                player.sendMessage(0, "Your inventory has been wiped.");

                // Give back the items and their quantities after resetting the inventory
                foreach (var item in itemsToGiveBack)
                {
                    ItemInfo itemInfo = player._server._assets.getItemByID(item.Key);
                    if (itemInfo != null)
                    {
                        player.inventoryModify(itemInfo, item.Value);
                    }
                }
            } else {
                player.sendMessage(0, "Cannot wipe inventory from this location.");
                return;
            }

            // Handle the case where buildName is "random"
            if (string.IsNullOrWhiteSpace(buildName) || buildName.ToLower() == "random")
            {
                HandleBuildCommand(player, "random");
                return;
            }

            // Buy the build
            HandleBuildCommand(player, buildName);
        }

        // Wipes inventory, buys the build, and fills up all possible ammo types to their maximum
        private void WipeBuyAndMaxAmmo(Player player, string buildName)
        {
            int terrainID = player._arena.getTerrainID(player._state.positionX, player._state.positionY);
            if (terrainID == 4 || terrainID == 3 || terrainID == 1 || terrainID == 2 || player.IsSpectator){
                // Take note of the items and their quantities before resetting the inventory
                Dictionary<int, int> itemsToGiveBack = new Dictionary<int, int>();
                HashSet<int> itemsToDeprive = new HashSet<int> { 2005, 2007, 2009, 2, 9, 10, 11 };
                foreach (int itemID in itemsToDeprive)
                {
                    int currentCount = player.getInventoryAmount(itemID);
                    itemsToGiveBack.Add(itemID, currentCount);
                }

                player.resetInventory(true);
                player.sendMessage(0, "Your inventory has been wiped.");

                // Give back the items and their quantities after resetting the inventory
                foreach (var item in itemsToGiveBack)
                {
                    ItemInfo itemInfo = player._server._assets.getItemByID(item.Key);
                    if (itemInfo != null)
                    {
                        player.inventoryModify(itemInfo, item.Value);
                    }
                }
            } else {
                player.sendMessage(0, "Cannot wipe inventory from this location.");
                return;
            }
            
            // Store build name
            lastUsedBuild[player] = buildName.ToLower();
            //player.sendMessage(0, "~Build name stored: " + buildName.ToLower());

            // Handle the case where buildName is "random"
            if (string.IsNullOrWhiteSpace(buildName) || buildName.ToLower() == "random")
            {

                HandleBuildCommand(player, "random");
                MaxOutAmmo(player);
                return;
            }

            // Buy the build
            HandleBuildCommand(player, buildName);

            // Fill up all possible ammo types to their maximum
            MaxOutAmmo(player);
        }

        // Fill the player's inventory with the maximum allowed ammo for each type in maxAmmoQuantities
        private void MaxOutAmmo(Player player)
        {
            foreach (var ammoType in maxAmmoQuantities)
            {
                string ammoName = ammoType.Key;
                int maxQuantity = ammoType.Value;

                // Get the current quantity the player has for this ammo type
                ItemInfo ammoItem = AssetManager.Manager.getItemByName(ammoName);
                if (ammoItem != null)
                {
                    int currentQuantity = player.getInventoryAmount(ammoItem.id);

                    // Calculate how much more ammo to give to reach the max
                    int amountToAdd = maxQuantity - currentQuantity;

                    if (amountToAdd > 0)
                    {
                        player.inventoryModify(ammoItem, amountToAdd);
                        //player.sendMessage(0, "~Maxed out ammo: Awarded " + amountToAdd + "x" + ammoName + ".");
                    }
                    else
                    {
                        //player.sendMessage(0, "~Ammo already maxed: " + currentQuantity + "x" + ammoName + ".");
                    }
                }
                else
                {
                    player.sendMessage(-1, "@Ammo type '" + ammoName + "' not found.");
                }
            }
        }

        private void DropUnusedItems(Player player, string buildName)
        {
            // Check if the player is in spectator mode, dead, and not null
            if (player.IsDead || player.IsSpectator)
            {
                player.sendMessage(-1, "Cannot be in spec or dead while attempting to drop items.");
                return;
            }

            // If no build name is provided, use the last used build
            if (string.IsNullOrWhiteSpace(buildName))
            {
                if (lastUsedBuild.ContainsKey(player))
                {
                    // Retrieve the last used build
                    buildName = lastUsedBuild[player];
                }
                else
                {
                    player.sendMessage(-1, "Please specify a build name or use a build first.");
                    return;
                }
            }

            // Convert buildName to lowercase for case-insensitive matching
            buildName = buildName.ToLower();

            // Check if the build exists
            if (buildSets.ContainsKey(buildName))
            {

                var buildData = buildSets[buildName];
                List<Tuple<string, ushort>> buildItems = buildData.Item1;

                // Track the ammo used by the build
                Dictionary<string, int> usedAmmo = new Dictionary<string, int>();

                // Iterate through the build items and record their quantities
                foreach (var item in buildItems)
                {
                    // Normalize the ammo name for case-insensitive matching
                    string ammoName = item.Item1.ToLower();

                    // Only consider items listed in the maxAmmoQuantities dictionary
                    if (maxAmmoQuantities.ContainsKey(ammoName))
                    {
                        usedAmmo[ammoName] = item.Item2;
                    }
                }

                // Collect excess ammo to drop
                List<Tuple<string, int>> itemsToDrop = new List<Tuple<string, int>>();

                // Iterate through all items in maxAmmoQuantities
                foreach (var ammoType in maxAmmoQuantities.Keys)
                {
                    // Normalize the ammo type for case-insensitive matching
                    string normalizedAmmoType = ammoType.ToLower();

                    // Find the item in the player's inventory, if it exists
                    var inventoryItem = player._inventory.Values.FirstOrDefault(i => i.item.name.ToLower() == normalizedAmmoType);

                    // If the item is in the player's inventory
                    if (inventoryItem != null)
                    {
                        int currentQuantity = inventoryItem.quantity;

                        // Get the quantity used by the build, if any
                        int usedQuantity = usedAmmo.ContainsKey(normalizedAmmoType) ? usedAmmo[normalizedAmmoType] : 0;

                        // Calculate excess
                        int excess = currentQuantity - usedQuantity;

                        // If the item is not in the build, drop all of it
                        if (usedQuantity == 0 && currentQuantity > 0)
                        {
                            itemsToDrop.Add(new Tuple<string, int>(normalizedAmmoType, currentQuantity));
                        }
                        // If the item is in the build but there's excess, drop the excess
                        else if (excess > 0)
                        {
                            itemsToDrop.Add(new Tuple<string, int>(normalizedAmmoType, excess));
                        }
                    }
                }

                // Now drop all the collected items
                foreach (var item in itemsToDrop)
                {
                    string ammoType = item.Item1;
                    int excess = item.Item2;

                    // Find the item in the player's inventory
                    var inventoryItem = player._inventory.Values.FirstOrDefault(i => i.item.name.ToLower() == ammoType);

                    if (inventoryItem != null)
                    {
                        // Spawn the item on the map at the player's location
                        if (!player._arena.exists("Player.ItemDrop") || (bool)player._arena.callsync("Player.ItemDrop", false, player, inventoryItem.item, excess))
                        {
                            // Drop the item onto the map based on terrain settings and nearby item stacking
                            //if (player._arena.getTerrain(player._state.positionX, player._state.positionY).prizeExpire > 1)
                            //{
                                if (player._arena.getItemCountInRange(inventoryItem.item, player._state.positionX, player._state.positionY, 50) > 0)
                                {
                                    // Stack with nearby items if present
                                    player._arena.itemStackSpawn(inventoryItem.item, (ushort)excess, player._state.positionX, player._state.positionY, 50, player);
                                }
                                else
                                {
                                    // Spawn a new item drop
                                    player._arena.itemSpawn(inventoryItem.item, (ushort)excess, player._state.positionX, player._state.positionY, 0, (int)player._team._id, player);
                                }

                                // Remove the excess from the player's inventory
                                player.inventoryModify(inventoryItem.item, -excess);
                                // player.sendMessage(0, "~Dropped " + excess + "x" + inventoryItem.item.name + " (excess).");
                            //}
                        }
                    }
                }
            }
            else
            {
                player.sendMessage(-1, "Build not found. Available builds are: " + string.Join(", ", buildSets.Keys));
            }
        }

        public void CountItemsOnSpecificTerrain(Arena arena)
        {
            // Define item IDs of interest
            HashSet<int> targetItemIDs = new HashSet<int> { 2005, 2009 }; // Tox, Tso, Pandora's

            // Define counters for each dropship area
            Dictionary<string, Dictionary<int, int>> dropshipItemCounts = new Dictionary<string, Dictionary<int, int>>()
            {
                { "Titan Dropship", new Dictionary<int, int> { { 2005, 0 }, { 2009, 0 } } },
                { "Collective Dropship", new Dictionary<int, int> { { 2005, 0 }, { 2009, 0 } } }
            };

            // Iterate through all items currently in the arena
            foreach (var item in arena._items.Values)
            {
                // Fetch the item's current position and multiply by 16
                int itemX = item.positionX;
                int itemY = item.positionY;

                // Check the terrain at the item's position
                int terrainID = arena.getTerrainID(itemX, itemY);

                // Only consider items on terrain ID 4
                if (terrainID == 4 && targetItemIDs.Contains(item.item.id))
                {
                    int itemQuantity = item.quantity; // Assuming quantity holds the correct value

                    // Check if the item is within Titan Dropship boundaries
                    if (itemX >= 608 * 16 && itemX <= 699 * 16 && itemY >= 440 * 16 && itemY <= 478 * 16)
                    {
                        dropshipItemCounts["Titan Dropship"][item.item.id] += itemQuantity;
                    }
                    // Check if the item is within Collective Dropship boundaries
                    else if (itemX >= 607 * 16 && itemX <= 699 * 16 && itemY >= 591 * 16 && itemY <= 633 * 16)
                    {
                        dropshipItemCounts["Collective Dropship"][item.item.id] += itemQuantity;
                    }
                }
            }

            // Send the counts as arena messages
            string titanMessage = string.Format(
                "~Titan Dropship Mineral Counts: TOX: {0}, TSO: {1}",
                dropshipItemCounts["Titan Dropship"][2009],
                dropshipItemCounts["Titan Dropship"][2005]
            );

            string collectiveMessage = string.Format(
                "@Collective Dropship Mineral Counts: TOX: {0}, TSO: {1}",
                dropshipItemCounts["Collective Dropship"][2009],
                dropshipItemCounts["Collective Dropship"][2005]
            );

            // Send the formatted messages to the arena
            arena.sendArenaMessage(titanMessage);
            arena.sendArenaMessage(collectiveMessage);
        }

        /*//////////////////////////////////////////////////
        // Class change announcement logic
        *///////////////////////////////////////////////////

        // Dictionaries to track the last skill name, last announcement time, and to track if a player has ever been on a non-SPEC team for each player
        private Dictionary<Player, string> playerLastSkillNames = new Dictionary<Player, string>();
        
        private Dictionary<Player, DateTime> lastAnnouncementTimes = new Dictionary<Player, DateTime>();
        Dictionary<Player, bool> playerHasPlayed = new Dictionary<Player, bool>();

        // Grace period for announcements (in seconds)
        private const int AnnouncementGracePeriod = 30;

        // Helper method to retrieve the primary skill name from the player's skills dictionary
        private string GetPrimarySkillName(Player player)
        {
            // Assuming the primary skill is the first entry or has a specific key
            if (player._skills != null && player._skills.Count > 0)
            {
                // Get the first skill from the dictionary
                foreach (var skillItem in player._skills.Values)
                {
                    return skillItem.skill.Name; // Access the Name property from SkillInfo
                }
            }
            return "Unknown"; // Default to "Unknown" if no skill is found
        }

        public void pollItemExpiration()
        {
            // Iterate through all items currently in the arena
            foreach (var item in arena._items.Values)
            {
                // Fetch the item's current position
                short itemX = item.positionX;
                short itemY = item.positionY;

                // Check the terrain at the item's position
                int terrainID = arena.getTerrainID(itemX, itemY);

                // Fetch the prizeExpire value from the terrain configuration in cfg.terrains
                int prizeExpireTime = CFG.terrains[terrainID].prizeExpire;

                // Define the non-expiring item IDs
                HashSet<int> nonExpiringItemIDs = new HashSet<int> { 2005, 2007, 2009, 2, 9, 10, 11 }; // Tox, Tso, Steron Injection, AutoGuns (premades)

                // Check if item is "Ammo MG" with stack < 50
                // if (item.item.name == "Ammo MG" && item.quantity < 50)
                // {
                //     item.tickExpire = Environment.TickCount + 1000; // Set to expire in 1 second
                //     continue;
                // }

                // Apply custom expiration logic
                if (nonExpiringItemIDs.Contains(item.item.id))
                {
                    // Set the item's expiration to infinite if in the non-expiring ID list
                    item.tickExpire = 0;
                }
            }
        }

        public void pollSkillCheck()
        {
            // Loop through each player in the arena
            if (!arena._name.Contains("Arena 1") && !arena._name.Contains("Public1"))
            {
                foreach (Player player in arena.Players)
                {
                    // Initialize tracking for new players if needed
                    if (!player.IsSpectator && !playerClassPlayTimes.ContainsKey(player))
                    {
                        playerClassPlayTimes[player] = new Dictionary<string, int>();
                        playerLastClassSwitch[player] = Environment.TickCount;
                        playerClassSwaps[player] = 0;
                    }

                    // We need to get the current skill name from the player's skills dictionary
                    string currentSkillName = GetPrimarySkillName(player);

                    // Get the player's current team name
                    string currentTeamName = player._team != null ? player._team._name : "SPEC";

                    // Check if the player's skill name has changed
                    if (!playerLastSkillNames.ContainsKey(player) || playerLastSkillNames[player] != currentSkillName)
                    {
                        // Check if the player has ever been on a non-SPEC team
                        if (!playerHasPlayed.ContainsKey(player))
                        {
                            playerHasPlayed[player] = false; // Default to false if not tracked yet
                        }

                        // If the player is currently on a non-SPEC team, mark them as having played
                        if (currentTeamName != "spec")
                        {
                            playerHasPlayed[player] = true; // Mark the player as having played
                        }

                        // Only announce class changes for players who have ever been on a non-SPEC team
                        if (playerHasPlayed[player])
                        {
                            // Update the dictionary with the new skill name
                            playerLastSkillNames[player] = currentSkillName;

                            // Check if the player is within the grace period for announcements
                            bool withinGracePeriod = lastAnnouncementTimes.ContainsKey(player) &&
                                                       (DateTime.Now - lastAnnouncementTimes[player]).TotalSeconds < AnnouncementGracePeriod;

                            // Announce the player's skill change if it is Infiltrator, not within the grace period, 
                            // and the player is not on team "np"
                            if (currentSkillName == "Infiltrator" && !withinGracePeriod && currentTeamName.ToLower() != "np")
                            {
                                // Make the actual announcement
                                if (player._alias == "YAH" || player._alias == "JACKIE"){
                                    arena.sendArenaMessage("#SWITCHING CLASS IN UPPERCASE ------ TEAM " + currentTeamName.ToUpper() + " ------ " + player._alias + ".", 14);
                                } else {
                                    arena.sendArenaMessage("#CLOAKERS ------ TEAM " + currentTeamName + " ------ " + player._alias + ".", 14);
                                }

                                // Send another arena message 5 times every 1 second
                                // for (int i = 0; i < 5; i++)
                                // {
                                //     arena.sendArenaMessage("#HEY YOU AUTISTIC FUCK, TURN YOUR ES ON", 3);
                                //     System.Threading.Thread.Sleep(1000);
                                // }

                                // Update the last announcement time for the player
                                lastAnnouncementTimes[player] = DateTime.Now;
                            }
                        }
                    }
                }
            }
        }

        public void pollFlagBug()
        {
            // Loop through each flag in the current map
            foreach (var flag in currentMap.Flags)
            {
                // Retrieve the flag object from the arena using the flag's nameinf
                var fs = arena.getFlag(flag.Item1);  // flag.Item1 is the flag name

                if (flag.Item1.Equals("sdFlag", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (fs != null)
                {
                    // Check if the flag's current position is (0, 0)
                    // If 0,0, its bugged, lets respawn it to its default position!
                    if (fs.posX == 0 && fs.posY == 0 && fs.carrier == null)
                    {
                        // The player is carrying this flag and its coordinates are (0, 0)
                        // Reset the flag's position to its original coordinates
                        fs.posX = (short)(flag.Item2 * 16);  // flag.Item2 is X coordinate
                        fs.posY = (short)(flag.Item3 * 16);  // flag.Item3 is Y coordinate

                        fs.bActive = true;      // Ensure the flag is active
                        fs.carrier = null;      // Clear the carrier
                        fs.team = null;         // Reset the team if necessary

                        // Update the flag's status for all players
                        Helpers.Object_Flags(arena.Players, fs);

                        // Since a player can carry only one flag, we can exit the loop
                        break;
                    }
                }
            }
        }

        private DateTime lastPollCheckTime = DateTime.MinValue;
        private const double pollCheckInterval = 2.0; // Interval in seconds

        /// <summary>
        /// CTF Script poll called by our arena
        /// </summary>
            public bool poll()
            {
            //Log.write(TLog.Error, "Polling arena.");

            int now = Environment.TickCount;
            //int seconds = ((Environment.TickCount - arena._tickGameStarted) / 1000);

            // Check if it's time to run less frequent polling functions
            if ((DateTime.Now - lastPollCheckTime).TotalSeconds >= pollCheckInterval)
            {
                pollItemExpiration();
                pollSkillCheck();
                pollFlagBug();
                lastPollCheckTime = DateTime.Now;
                // Gladiator event polling
                if (currentEventType == EventType.Gladiator)
                {
                    CheckGladiatorVictory();
                }
                if (currentEventType == EventType.SUT)
                {
                    CheckSUTVictory();
                }
                //arena.sendArenaMessage(string.Format("Elapsed game time: {0} seconds", seconds));
            }

            /*//////////////////////////////////////////////////
            // Game state management
            /*//////////////////////////////////////////////////

            // Check if it's time to start the first overtime and if it is not already active
            if (overtimeStart != 0 && Environment.TickCount >= overtimeStart && !isSD)
            {
                isSD = true;
                arena.sendArenaMessage("&Overtime triggered. 50% Reduced healing on both Medikits. Engineer Repairs reduced to 50 hp (unless all 5 flags held)", 30);

                overtimeStart = Environment.TickCount;

                // Schedule second overtime
                //secondOvertimeStart = overtimeStart + 10000; // 10 seconds for testing
                secondOvertimeStart = overtimeStart + (15 * 60000); // 15 minutes

                overtimeStart = 0; // Reset to avoid multiple triggers
            }

            // Check if it's time to start the second overtime
            if (isSD && !isSecondOvertime && secondOvertimeStart != 0 && Environment.TickCount >= secondOvertimeStart)
            {
                isSecondOvertime = true;
                arena.sendArenaMessage("&Second Overtime triggered. 75% Reduced healing on both Medikits. Engineer Repairs reduced to 40 hp (unless all 5 flags held). ", 30);

                secondOvertimeStart = 0; // Reset to avoid multiple triggers
            }

            if (now - lastGameCheck < Arena.gameCheckInterval)
                return true;
            lastGameCheck = now;

            if (gameState == GameState.Init)
            {
                if (arena.PlayersIngame.Count() < minPlayers)
                {
                    gameState = GameState.NotEnoughPlayers;
                }
            }

            switch (gameState)
            {
                case GameState.NotEnoughPlayers:
                    arena.setTicker(1, 3, 0, "Not Enough Players");
                    gameState = GameState.Init;
                    break;
                case GameState.Transitioning:
                    //Do nothing while we wait
                    break;
                case GameState.ActiveGame:
                    PollCTF(now);
                    break;
                case GameState.Init:
                    Initialize();
                    break;
                case GameState.PreGame:
                    PreGame();
                    break;
                case GameState.PostGame:
                    gameState = GameState.Init;
                    break;
            }
            return true;
        }

        private void OnFlagChange(Arena.FlagState flag)
        {
            Team victory = flag.team;

            //Does this team now have all the flags?
            foreach (Arena.FlagState fs in arena._flags.Values)
            {
                if (fs.bActive && fs.team != flag.team)
                {   //Not all flags are captured yet
                    victory = null;
                    break;
                }
            }

            if (!gameWon)
            {   //All flags captured?
                if (victory != null)
                {   //Yep
                    winningTeamTick = (Environment.TickCount + (victoryHoldTime * 10));
                    winningTeamNotify = 0;
                    winningTeam = victory;
                    flagMode = CTFMode.XSeconds;
                }
                else
                {   //Aborted?
                    if (winningTeam != null)
                    {   //Yep
                        winningTeam = null;
                        winningTeamTick = 0;
                        flagMode = CTFMode.Aborted;
                    }
                }
            }
        }



        #endregion

        #region Script Functions

       public static void scrambleSpecificTeams(Arena arena, List<Team> teamsToScramble, bool alertArena, Script_CTF script)
        {
            // Ensure the arena and teams are valid
            if (arena == null || teamsToScramble == null || teamsToScramble.Count != 2)
                throw new ArgumentException("Invalid arena or teams passed.");

            List<Player> playersToScramble = new List<Player>();

            // Manually collect players on the teamsToScramble
            foreach (var player in arena.PlayersIngame) // Assuming PlayersIngame gives all players in the arena
            {
                // Ensure player is on one of the teamsToScramble and is not a Dueler
                if (teamsToScramble.Contains(player._team) && 
                    !script.GetPrimarySkillName(player).Equals("Dueler", StringComparison.OrdinalIgnoreCase))
                {
                    playersToScramble.Add(player); // Add player to the list
                }
            }

            // Shuffle players randomly using arena's random generator
            var shuffledPlayers = playersToScramble.OrderBy(plyr => arena._rand.Next(0, 500)).ToList();

            // Distribute players back into the two teams evenly
            for (int i = 0; i < shuffledPlayers.Count; i++)
            {
                Team team = teamsToScramble[i % teamsToScramble.Count]; // Rotate between teamA and teamB
                if (shuffledPlayers[i]._team != team) // Only move if the player isn't already on the team
                {
                    team.addPlayer(shuffledPlayers[i]);
                }
            }

            foreach (var player in arena.PlayersIngame)
            {
                if (script.currentEventType == EventType.MiniTP){
                    Player playerCopy = player; // Create a copy to use in timer callback
                    System.Threading.Timer timer = null;
                    timer = new System.Threading.Timer((state) =>
                    {
                        if (playerCopy._team._name.Contains("Titan"))
                            script.WarpPlayerToRange(playerCopy, 654, 654, 518, 518);
                        else if (playerCopy._team._name.Contains("Collective"))
                            script.WarpPlayerToRange(playerCopy, 648, 648, 565, 565);
                        timer.Dispose();
                    }, null, 3000, System.Threading.Timeout.Infinite);
                }
            }

            // Notify players of the scramble
            if (alertArena)
            {
                arena.sendArenaMessage("Teams have been scrambled between " + teamsToScramble[0]._name + " and " + teamsToScramble[1]._name + "!");
            }
        }

        private bool SpawnMapPlayers()
        {
            if (currentMap != null)
            {
                // Retrieve teams from the current map
                var teamA = arena.getTeamByName(currentMap.TeamNames[0]);
                var teamB = arena.getTeamByName(currentMap.TeamNames[1]);

                foreach (var player in arena.PlayersIngame)
                {
                    string playerSkill = this.GetPrimarySkillName(player);
                    if (playerSkill.Equals("Dueler", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // Skip Dueler players
                    } 
                    else 
                    {
                        // Assign players evenly between teamA and teamB
                        if (teamA.ActivePlayerCount < teamB.ActivePlayerCount)
                        {
                            teamA.addPlayer(player);
                        }
                        else
                        {
                            teamB.addPlayer(player);
                        }
                    }
                    // if (currentEventType == EventType.MiniTP){
                    //     if (player._team._name.Contains("Titan"))
                    //         WarpPlayerToRange(player, 654, 654, 518, 518);
                    //     else if (player._team._name.Contains("Collective")) 
                    //         WarpPlayerToRange(player, 648, 648, 565, 565);
                    //     return false;
                    // }
                }

                // Scramble only teamA and teamB, leaving other teams unaffected
                scrambleSpecificTeams(arena, new List<Team> { teamA, teamB }, true, this);

                return true;
            }

            return false; // In case currentMap is null
        }

        private bool SpawnMapFlags()
        {
            foreach(var flag in currentMap.Flags)
            {
                var fs = arena.getFlag(flag.Item1);

                if (fs == null)
                {
                    return false;
                }

                // Check if the flag is "sdFlag" and set it inactive
                if (flag.Item1.Equals("sdFlag", StringComparison.OrdinalIgnoreCase))
                {
                    fs.bActive = false;  // Make sure sdFlag is inactive by default
                    fs.posX = 0;         // You can position it off the map or at a default position
                    fs.posY = 0;
                    continue;            // Skip the rest of the logic for sdFlag
                }

                bool bActive = true;

                if (currentMap.RandomizeFlagLocations)
                {
                    bActive = RandomizeFlagLocation(fs);
                }
                else
                {
                    fs.posX = (short)(flag.Item2 * 16);
                    fs.posY = (short)(flag.Item3 * 16);
                }
                
                fs.bActive = bActive;
                fs.team = null;
                fs.carrier = null;

                Helpers.Object_Flags(arena.Players, fs);
            }

            return true;
        }

        //Spawn first flag
        private bool SpawnMapFlag()
        {
            var flag = currentMap.Flags[0];
            var fs = arena.getFlag(flag.Item1);
                
            if (fs == null)
            {
                return false;
            }

            bool bActive = true;

            if (currentMap.RandomizeFlagLocations)
            {
                bActive = RandomizeFlagLocation(fs);
            }
            else
            {
                fs.posX = (short)(flag.Item2 * 16);
                fs.posY = (short)(flag.Item3 * 16);
            }
               
            fs.bActive = bActive;
            fs.team = null;
            fs.carrier = null;

            Helpers.Object_Flags(arena.Players, fs);
            
            return true;
        }

        ///////////////////////////////////////////////////
        // Script Functions
        ///////////////////////////////////////////////////
        /// <summary>
        /// Resets all variables and initializes a new game state
        /// </summary>
        private void Initialize()
        {
            winningTeamNotify = 0;
            winningTeamTick = 0;
            winningTeam = null;
            gameWon = false;

            //We are officially initialized, pregame it.
            gameState = GameState.PreGame;
        }

        /// <summary>
        /// Our waiting period between games
        /// </summary>
        private void PreGame()
        {
            gameState = GameState.Transitioning;

            //Sit here until timer runs out
            arena.setTicker(1, 3, preGamePeriod * 100, "Next game: ",
                    delegate ()
                    {	//Trigger the game start
                        arena.gameStart();
                    }
            );
        }

        /// <summary>
        /// Resets our tickers and gamestate
        /// </summary>
        private void Reset()
        {
            //Clear any tickers that might be still active
            if (gameState == GameState.Transitioning)
            {
                arena.setTicker(4, 3, 0, ""); //Next game
            }
            arena.setTicker(4, 1, 0, ""); //Victory in x:x

            //Reset
            gameState = GameState.Init;
        }

        /// <summary>
        /// Did someone win yet? If so, set the announcement
        /// </summary>
        private void CheckWinner(int now)
        {
            //See if someone is winning
            if (winningTeam != null)
            {
                //Has XSeconds been called yet?
                if (flagMode == CTFMode.XSeconds)
                { return; }

                int tick = (int)Math.Ceiling((winningTeamTick - now) / 1000.0f);
                switch (tick)
                {
                    case 10:
                        flagMode = CTFMode.TenSeconds;
                        break;
                    case 30:
                        flagMode = CTFMode.ThirtySeconds;
                        break;
                    case 60:
                        flagMode = CTFMode.SixtySeconds;
                        break;
                    default:
                        if (tick <= 0)
                        {
                            flagMode = CTFMode.GameDone;
                        }
                        break;
                }
            }
        }

        private void SetNotifyBypass(int countdown)
        {   //If XSeconds matches one of these, it will bypass that call
            //so there is no duplicate Victory message
            switch (countdown)
            {
                case 10:
                    winningTeamNotify = 1;
                    break;
                case 30:
                    winningTeamNotify = 2;
                    break;
                case 60:
                    winningTeamNotify = 3;
                    break;
            }
        }

        /// <summary>
        /// Poll the flag state while checking for a winner
        /// </summary>
        private void PollCTF(int now)
        {
            //See if we have enough players to keep playing
            if (arena.PlayersIngame.Count() < minPlayers)
            {
                Reset();
            }
            else
            {
                CheckWinner(now);
            }

            int countdown = winningTeamTick > 0 ? (int)Math.Ceiling((winningTeamTick - now) / 1000.0f) : 0;
            switch (flagMode)
            {
                case CTFMode.Aborted:
                    arena.setTicker(4, 1, 0, "");
                    arena.sendArenaMessage("Victory has been aborted.", CFG.flag.victoryAbortedBong);
                    flagMode = CTFMode.None;
                    break;
                case CTFMode.TenSeconds:
                    //10 second win timer
                    if (winningTeamNotify == 1) //Been notified already?
                    { break; }
                    winningTeamNotify = 1;
                    arena.sendArenaMessage(string.Format("Victory for {0} in {1} seconds!", winningTeam._name, countdown), CFG.flag.victoryWarningBong);
                    flagMode = CTFMode.None;
                    break;
                case CTFMode.ThirtySeconds:
                    //30 second win timer
                    if (winningTeamNotify == 2) //Been notified already?
                    { break; }
                    winningTeamNotify = 2;
                    arena.sendArenaMessage(string.Format("Victory for {0} in {1} seconds!", winningTeam._name, countdown), CFG.flag.victoryWarningBong);
                    flagMode = CTFMode.None;
                    break;
                case CTFMode.SixtySeconds:
                    //60 second win timer
                    if (winningTeamNotify == 3) //Been notified already?
                    { break; }
                    winningTeamNotify = 3;
                    arena.sendArenaMessage(string.Format("Victory for {0} in {1} seconds!", winningTeam._name, countdown), CFG.flag.victoryWarningBong);
                    flagMode = CTFMode.None;
                    break;
                case CTFMode.XSeconds:
                    //Initial win timer upon capturing
                    SetNotifyBypass(countdown); //Checks to see if xSeconds matches any other timers
                    arena.setTicker(4, 1, CFG.flag.victoryHoldTime, "Victory in ");
                    arena.sendArenaMessage(string.Format("Victory for {0} in {1} seconds!", winningTeam._name, countdown), CFG.flag.victoryWarningBong);
                    flagMode = CTFMode.None;
                    break;
                case CTFMode.GameDone:
                    //Game is done
                    gameWon = true;
                    arena.gameEnd();
                    break;
            }

            UpdateCTFTickers();
            UpdateKillStreaks();
            UpdateFlagCarryStats(now);
        }
        /// <summary>
        /// Handles a player's produce request
        /// </summary>
        [Scripts.Event("Player.Produce")]
        public bool playerProduce(Player player, Computer computer, VehInfo.Computer.ComputerProduct product)
        {
            string[] titlesToCheck = { "SciOps", "Sergeant", "Captain", "BioChem Trooper" };
            if (currentEventType == EventType.CTFX && titlesToCheck.Contains(product.Title)){
                ChangePlayerSkill(player, product.Title);
                player.resetInventory(true);
                WarpPlayerToRange(player, 985, 997, 1117, 1129);
                //return false;
            }
            return true;
        }

        // mined stat dictionary that holds list of teams and the count of TSO and TOX mined
        private Dictionary<string, int> minedStats = new Dictionary<string, int>();

        // Add stat method for tracking per team and per item type ("Tso" for 2005, "Tox" for 2009)
        private void AddStat(int itemId, int quantity, string teamName)
        {
            // Build the key based on item id: 2005 -> "Tso mined", 2009 -> "Tox mined"
            string key = (itemId == 2005) ? teamName + " Tso mined" : teamName + " Tox mined";
            if (!minedStats.ContainsKey(key))
                minedStats[key] = 0;
            minedStats[key] += quantity;
        }

        /// <summary>
        /// Triggered when a player requests to pick up an item
        /// </summary>
        [Scripts.Event("Player.ItemPickup")]
        public bool playerItemPickup(Player player, Arena.ItemDrop drop, ushort quantity)
        {
            var itemId = drop.item.id;
            var itemName = drop.item.name;

            // For item IDs 2005 (Tso) or 2009 (Tox), update the team's stat accordingly.
            if ((itemId == 2005 || itemId == 2009) && quantity == 5)
            {
                int terrainID = player._arena.getTerrainID(player._state.positionX, player._state.positionY);
                if (terrainID == 0 && (player._team._name.Contains("Titan") || player._team._name.Contains("Collective")))
                {
                    AddStat(itemId, quantity, player._team._name);
                    //arena.sendArenaMessage(string.Format("{0} has mined 5 {1}!", player._alias, itemName));
                }
            }

            // If tranq is picked up, and player has Subzero (Tranq) item, give them the Subzero (tranq) item instead (ID 1264)
            if (drop.item.id == 1113 && player._inventory.ContainsKey(1312))
            {
                player.inventoryModify(1264, quantity);
                player.sendMessage(0, "You have been given the Subzero (Tranq) item!");
                // destroy drop from arena
                player._arena._items.Remove(drop.id);
                Helpers.Object_ItemDropUpdate(player._arena.Players, (ushort)drop.item.id, (ushort)0);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Triggered when a player attempts to use a warp item
        /// </summary>
        private Dictionary<ushort, int> summonedCounts = new Dictionary<ushort, int>();

        [Scripts.Event("Player.WarpItem")]
        public bool playerWarpItem(Player player, ItemInfo.WarpItem item, ushort targetPlayerID, short posX, short posY)
        {
            // if (item.warpMode == Assets.ItemInfo.WarpItem.WarpMode.Portal)
            // {

            // }

            // GetPrimarySkillName and if Combat Engineer, update computers send update
            if (GetPrimarySkillName(player) == "Combat Engineer" || 
                (arena.getPlayerById(targetPlayerID) != null && GetPrimarySkillName(arena.getPlayerById(targetPlayerID)) == "Combat Engineer"))
            {
                foreach (Vehicle v in arena.Vehicles)
                {
                    if (v is Computer)
                    {
                        Computer comp = (Computer)v;
                        comp._sendUpdate = true;
                        comp.poll();
                    }
                }
                // debug message to the player that they're receiving an update on turret hp
                player.sendMessage(0, "Debug: Updating turret hp for player client.");
            }

            // Redirect DropShip Recall for CTFX Event
            if (currentEventType == EventType.CTFX && item.id == 46){
                WarpPlayerToRange(player, 985, 997, 1117, 1129, 0);
                return false;
            }

            if (currentEventType == EventType.MiniTP && item.id == 46){
                // Set players energy to 0
                player._state.energy = 0;
                player.syncState();
                if (player._team._name.Contains("Titan"))
                    WarpPlayerToRange(player, 654, 654, 518, 518, 0);
                else if (player._team._name.Contains("Collective"))
                    WarpPlayerToRange(player, 648, 648, 565, 565, 0);
                return false;
            }

            var flag = arena.getFlag("Bridge3");
            // Send a message to the arena that they have been summoned if they're carrying a flag
            if (item.id == 35){
                // Track summoned count for target player
                if (!summonedCounts.ContainsKey(targetPlayerID))
                    summonedCounts[targetPlayerID] = 0;
                summonedCounts[targetPlayerID]++;

                // If the target player is not within 100 units on X axis after summoning, refund the energy cost
                Player targetPlayer = arena.Players.FirstOrDefault(p => p._id == targetPlayerID);
                if (targetPlayer != null)
                {
                    int terrainID = targetPlayer._arena.getTerrainID(targetPlayer._state.positionX, targetPlayer._state.positionY);
                    //player.sendMessage(0, string.Format("Attempting to summon player from terrain {0}", terrainID));
                }

                System.Threading.Timer timer = null;
                timer = new System.Threading.Timer((state) =>
                {
                    targetPlayer = arena.Players.FirstOrDefault(p => p._id == targetPlayerID);
                    if (targetPlayer != null)
                    {
                        //player.sendMessage(0, "Checking if target player is still in DropShip.");
                        int terrainID = targetPlayer._arena.getTerrainID(targetPlayer._state.positionX, targetPlayer._state.positionY);
                        if (terrainID == 4)
                        {
                            int currentEnergy = player._state.energy;
                            player.setEnergy((short)Math.Min(1000, currentEnergy + 100)); // Add 100 energy, capped at 1000
                            player.sendMessage(0, "Summon failed: energy was refunded, target player still in dropship.");
                        }
                    }
                }, null, 1000, System.Threading.Timeout.Infinite);
                if (is5v5){
                    if (targetPlayer != null) {
                        if (flag != null && flag.carrier == targetPlayer && baseUsed != "Unknown" && winningTeamOVD != "offense"){
                            arena.sendArenaMessage(string.Format("Offense wins! {0} has been summoned with a flag!", targetPlayer._alias));
                            winningTeamOVD = "offense";
                        }
                    }
                }
            }

            // Send a message to the arena if a player is carrying a flag and warps to a teammate or blinks
            if (player != null && is5v5) {
                // Check if player's team has a Squad Leader
                bool hasSquadLeader = player._team.ActivePlayers.Any(p => 
                    p._skills.Values.Any(s => s.skill.Name == "Squad Leader"));

                if (hasSquadLeader && flag != null && flag.carrier == player && baseUsed != "Unknown" && winningTeamOVD != "offense"){
                    arena.sendArenaMessage(string.Format("Offense wins! {0} has warped with a flag!", player._alias));
                    winningTeamOVD = "offense";
                }
            }

            // Test Teleport Disruptor logic working via Player.WarpItem script event
            // if (item.id == 35 || item.id == 17){ // "Teleport Summoner" and "Squad Leader Summon"
            //     Player targetPlayer = arena.Players.FirstOrDefault(p => p._id == targetPlayerID);
            //     if (targetPlayer != null){
            //         ItemInfo.UtilityItem teleportDisruptor = AssetManager.Manager.getItemByName("Teleport Disruptor") as ItemInfo.UtilityItem;
            //         if (teleportDisruptor != null){
            //             int disruptorRange = teleportDisruptor.antiWarpDistance;
            //             bool isDisrupted = arena.Players.Any(p => 
            //                 p._id != targetPlayer._id && 
            //                 p.activeUtilities.Any(u => u.activateSound != null) && 
            //                 p._state.energy > 0 && 
            //                 Math.Sqrt(
            //                     Math.Pow((p._state.positionX - targetPlayer._state.positionX) / 16, 2) + 
            //                     Math.Pow((p._state.positionY - targetPlayer._state.positionY) / 16, 2)
            //                 ) <= disruptorRange);
            //             if (isDisrupted){
            //                 player.sendMessage(-1, "The player you are trying to summon is currently being teleport disrupted.");
            //                 return false;
            //             }
            //         }
            //     }
            // }
            return true;
        }

        /// <summary>
        /// Handles the spawn of a player
        /// </summary>
        [Scripts.Event("Player.Spawn")]
        public bool playerSpawn(Player player, bool bDeath)
        {
            //  arena.sendArenaMessage(string.Format("Player {0} spawned. Death: {1}, Event Type: {2}", 
            //      player._alias, bDeath, currentEventType));

            //If player skill is Dueler, reset their warp only.
            if (player._skills.Values.Any(s => s.skill.Name == "Dueler")){
                WarpPlayerToRange(player, 756, 756, 536, 536, 1000);
                return true;
            }

            if (currentEventType == EventType.CTFX || currentEventType == EventType.SUT){
                player.resetWarp();
                if (currentEventType == EventType.CTFX){
                    WarpPlayerToRange(player, 985, 997, 1117, 1129, 1000);
                } else {
                    WarpPlayerToRange(player, 679, 686, 608, 618, 1000);
                }
                //arena.sendArenaMessage(string.Format("CTFX: Warping {0} to range (985-997, 1117-1129)", player._alias));
                player.Bounty = CFG.bounty.start;
                player.syncState();
                return false;
            }

            if (currentEventType == EventType.MiniTP){
                player.resetWarp();
                if (player._team._name.Contains("Titan")) {
                    WarpPlayerToRange(player, 654, 654, 518, 518, 1000);
                    //arena.sendArenaMessage(string.Format("MiniTP: Warping Titan player {0} to (654, 518)", player._alias));
                }
                else if (player._team._name.Contains("Collective")) {
                    WarpPlayerToRange(player, 648, 648, 565, 565, 1000);
                    //arena.sendArenaMessage(string.Format("MiniTP: Warping Collective player {0} to (648, 565)", player._alias));
                }
                if (bDeath)
                {	//Trigger the appropriate event
                    if (player._bEnemyDeath) {
                        //Logic_Assets.RunEvent(player, CFG.EventInfo.killedByEnemy);
                        //arena.sendArenaMessage(string.Format("{0} was killed by enemy", player._alias));
                    }
                    else {
                        //Logic_Assets.RunEvent(player, CFG.EventInfo.killedByTeam);
                        //arena.sendArenaMessage(string.Format("{0} was killed by team", player._alias));
                    }

                    //Reset flags to unowned state?
                    if (player._arena.getTerrain(player._state.positionX, player._state.positionY).safety
                        && !CFG.flag.allowSafety) {
                        arena.flagResetPlayer(player, true);
                        //arena.sendArenaMessage(string.Format("Resetting flags for {0} in safety zone", player._alias));
                    }

                    //Reset his bounty
                    player.Bounty = CFG.bounty.start;
                    //Bring their energy to full
                    player._state.energy = 1000;
                    //arena.sendArenaMessage(string.Format("Reset bounty for {0} to {1}", player._alias, CFG.bounty.start));
                    //Update his client to reflect bty change
                    player.syncState();
                }
                return false;
            }

            return true;
        }
        // Dictionary to track class play times for each player
        private Dictionary<Player, Dictionary<string, int>> playerClassPlayTimes = new Dictionary<Player, Dictionary<string, int>>();
        
        // Dictionary to track when each player last switched classes
        private Dictionary<Player, int> playerLastClassSwitch = new Dictionary<Player, int>();

        /// <summary>
        /// Sends ordered class play time messages to the player
        /// </summary>
        private void SendOrderedPlayTimes(Player player)
        {
            if (!playerClassPlayTimes.ContainsKey(player))
                return;

            // Get current time to calculate current class time
            int currentTick = Environment.TickCount;
            var playTimes = new Dictionary<string, int>(playerClassPlayTimes[player]);

            // Add time for current class if player has one
            string currentSkill = player._skills.Count > 0 ? player._skills.First().Value.skill.Name : null;
            if (currentSkill != null)
            {
                int sessionTime = Math.Max(0, currentTick - playerLastClassSwitch[player]);
                if (!playTimes.ContainsKey(currentSkill))
                    playTimes[currentSkill] = 0;
                playTimes[currentSkill] += sessionTime;
            }

            // Order play times from highest to lowest
            var orderedTimes = playTimes
                .OrderByDescending(x => x.Value)
                .ToList();

            // Calculate total time
            double totalSeconds = orderedTimes.Sum(x => x.Value) / 1000.0;

            // Send header message
            player.sendMessage(-1, string.Format("&Total Time: {0:F1} seconds | Class Swaps: {1}", totalSeconds, playerClassSwaps[player]));

            // Send individual class times
            foreach (var entry in orderedTimes)
            {
                double seconds = entry.Value / 1000.0;
                player.sendMessage(0, string.Format("*{0}: {1:F1} seconds", entry.Key, seconds));
            }
        }

        // Dictionary to track class swaps for each player
        private Dictionary<Player, int> playerClassSwaps = new Dictionary<Player, int>();

        /// <summary>
        /// Triggered when a player's purchase is successful
        /// </summary>
        [Scripts.Event("Shop.SkillPurchase")]
        public bool PlayerShopSkillPurchase(Player from, SkillInfo skill)
        {
            Vehicle vehicle = from._occupiedVehicle ?? from._baseVehicle;
            if (vehicle != null)
            {
                vehicle._state.positionX = from._state.positionX;
                vehicle._state.positionY = from._state.positionY;
                vehicle._state.velocityX = 0;
                vehicle._state.velocityY = 0;
                vehicle._state.yaw = from._state.yaw;
                vehicle._state.direction = Helpers.ObjectState.Direction.None;
                vehicle._state.energy = 1000;
                vehicle._state.health = 1000;
                vehicle._tickDead = 0;
                vehicle.update(false);
            }
            
            from.warp(Helpers.ResetFlags.ResetAll, from._state, from._state.health, from._state.energy, (byte)from._state.yaw);
            
            // Update class swap count for player
            if (!playerClassSwaps.ContainsKey(from))
                playerClassSwaps[from] = 0;
            playerClassSwaps[from]++;

            if (currentEventType == EventType.CTFX){
                WarpPlayerToRange(from, 985, 997, 1117, 1129);
                return false;
            }
            if (currentEventType == EventType.MiniTP){
                if (from._team._name.Contains("Titan"))
                    WarpPlayerToRange(from, 654, 654, 518, 518);
                else if (from._team._name.Contains("Collective")) 
                    WarpPlayerToRange(from, 648, 648, 565, 565);
                return false;
            }
            return true;
        }
        /// <summary>
        /// Updates the play time tracking for a player's skill
        /// </summary>
        private void UpdateSkillPlayTime(Player player, SkillInfo newSkill = null)
        {
            // Don't update play time for spectators (either on spec team or in spec vehicle)
            if (player._team.IsSpec || (player._baseVehicle != null && player._baseVehicle._type.Name.Contains("Spectator")))
                return;

            // Initialize tracking for new players
            if (!playerClassPlayTimes.ContainsKey(player))
            {
                playerClassPlayTimes[player] = new Dictionary<string, int>();
                playerLastClassSwitch[player] = Environment.TickCount;
            }

            // Get current time
            int currentTick = Environment.TickCount;

            // Get current skill name if it exists
            string currentSkill = player._skills.Count > 0 ? player._skills.First().Value.skill.Name : null;

            if (currentSkill != null)
            {
                // Calculate time played in current session
                int startTime = playerLastClassSwitch[player];
                int sessionPlayTime = Math.Max(0, currentTick - startTime); // Ensure non-negative

                // Add session time to accumulated time for current skill
                if (!playerClassPlayTimes[player].ContainsKey(currentSkill))
                {
                    playerClassPlayTimes[player][currentSkill] = 0;
                }
                playerClassPlayTimes[player][currentSkill] += sessionPlayTime;

                // Send message showing total accumulated play time in seconds
                double totalSeconds = Math.Min(playerClassPlayTimes[player][currentSkill] / 1000.0,
                    (currentTick - arena._tickGameStarted) / 1000.0); // Cap at game duration
                // player.sendMessage(-1, string.Format("You played {0} for {1:F1} seconds", currentSkill, totalSeconds));
                // player.sendMessage(-1, string.Format("You have swapped {0} times", playerClassSwaps[player]));
            }

            // Reset the last switch time for the new skill
            playerLastClassSwitch[player] = currentTick;

            // Initialize play time for new skill if provided and not exists
            if (newSkill != null && !playerClassPlayTimes[player].ContainsKey(newSkill.Name))
            {
                playerClassPlayTimes[player][newSkill.Name] = 0;
            }
        }

        /// <summary>
        /// Triggered when a player requests to buy a skill
        /// </summary>
        [Scripts.Event("Shop.SkillRequest")]
        public bool PlayerShopSkillRequest(Player from, SkillInfo skill)
        {            
            UpdateSkillPlayTime(from, skill);

            //arena.sendArenaMessage(string.Format("Player {0} requested to buy skill {1}.", from._alias, skillName));
            if (skill.Name == "Infiltrator" && !from._arena._name.Contains("Arena 1") && !from._arena._name.Contains("Public1"))
            {
                // Count infiltrators only on the player's team
                int teamInfiltratorCount = arena.Players
                    .Where(p => p._team == from._team && !p._team.IsSpec && p._skills.Any(s => s.Value.skill.Name == "Infiltrator"))
                    .Count();

                if (teamInfiltratorCount >= 2)
                {
                    from.sendMessage(-1, "Infiltrator skill purchase not allowed due to 2 infils on the team already.");
                    return false;
                }
            }
            return true;
        }
        // Properties to store portal redirect settings
        private string redirectSide = null;
        private string redirectBase = null;

        /// <summary>
        /// Handles a player's portal request
        /// </summary>
        [Scripts.Event("Player.Portal")]
        public bool playerPortal(Player player, LioInfo.Portal portal)
        {
            // Check if the player's arena and event type are valid
            if (currentEventType == EventType.MiniTP)
            {
                // Replace 'desiredPortalID' with the specific portal ID you want to check
                ushort[] desiredPortalIDs = { 66, 67 }; // Titan DropShip portals

                // Check if the portal used is one of the ones we want to redirect
                if (desiredPortalIDs.Any(id => id == portal.PortalData.DestinationWarpGroup))
                {
                        if (player._team._name.Contains("Titan")) {
                            WarpPlayerToRange(player, 654, 654, 518, 518);
                        }
                        else if (player._team._name.Contains("Collective")) {
                        WarpPlayerToRange(player, 648, 648, 565, 565);
                    }
                    return false;
                }
            }

            // Check if the player's arena and event type are valid
            if (currentEventType == EventType.CTFX)
            {
                // Replace 'desiredPortalID' with the specific portal ID you want to check
                ushort[] desiredPortalIDs = { 66, 67 }; // Titan DropShip portals

                // Check if the portal used is one of the ones we want to redirect
                if (desiredPortalIDs.Any(id => id == portal.PortalData.DestinationWarpGroup))
                {
                    // Define the target position within the same arena
                    Helpers.ObjectState targetPosition = new Helpers.ObjectState();
                    targetPosition.positionX = 502; // Replace with desired X coordinate
                    targetPosition.positionY = 1422; // Replace with desired Y coordinate
                    targetPosition.positionZ = 0;    // Z coordinate, if applicable
                    targetPosition.yaw = 0;          // Facing direction, if applicable

                    // Warp the player to the new position
                    player.warp(targetPosition.positionX * 16, targetPosition.positionY * 16);
                    //WarpPlayerToExactLocation(player, targetPosition.positionX, targetPosition.positionY);

                    // Send a message to the player indicating they used a portal
                    player.sendMessage(0, "You have been redirected through a special portal!");

                    // Return false to prevent the default portal handling
                    return false;
                }
            }
            // Check if the player's arena and event type are valid
            if (currentEventType == EventType.SUT)
            {
                // Replace 'desiredPortalID' with the specific portal ID you want to check
                ushort[] desiredPortalIDs = { 66, 67 }; // Titan DropShip portals

                // Check if the portal used is one of the ones we want to redirect
                if (desiredPortalIDs.Any(id => id == portal.PortalData.DestinationWarpGroup))
                {
                    int totalPlayers = arena.PlayersIngame.Count();

                    if (totalPlayers < 10)
                        WarpPlayerToRange(player, 1180, 1270, 145, 240);

                    if (totalPlayers >= 10 && totalPlayers <= 29)
                        WarpPlayerToRange(player, 1180, 1400, 145, 270);

                    if (totalPlayers >= 30)
                        WarpPlayerToRange(player, 1070, 1540, 60, 310);

                    // Send a message to the player indicating they used a portal
                    //player.sendMessage(0, "You have been redirected to Small Unit Tactics!");

                    // Return false to prevent the default portal handling
                    return false;
                }
            }
            //arena debug message using string.format stating the portal.id
            //arena.sendArenaMessage(string.Format("Portal ID: {0}", portal.PortalData.DestinationWarpGroup));

            // Handle portal redirects based on base locations using value tuples with named elements
            bool isTitanPortal = new[] { 18, 201 }.Contains(portal.PortalData.DestinationWarpGroup);
            bool isCollectivePortal = new[] { 6, 7, 202 }.Contains(portal.PortalData.DestinationWarpGroup);

            bool shouldRedirect = (redirectSide == "titan" && isTitanPortal) || 
                                  (redirectSide == "collective" && isCollectivePortal);

            if (shouldRedirect)
            {
                if (redirectBase.Equals("d8", StringComparison.OrdinalIgnoreCase))
                {
                    WarpPlayerToRange(player, 275, 275, 601, 601);
                    return false;
                }
                else if (redirectBase.Equals("a10", StringComparison.OrdinalIgnoreCase))
                {
                    WarpPlayerToRange(player, 32, 32, 696, 696);
                    return false;
                }
                else if (redirectBase.Equals("a8", StringComparison.OrdinalIgnoreCase))
                {
                    WarpPlayerToRange(player, 28, 28, 611, 611);
                    return false;
                }
                else if (redirectBase.Equals("f8", StringComparison.OrdinalIgnoreCase))
                {
                    WarpPlayerToRange(player, 421, 421, 569, 569);
                    return false;
                }
                else if (redirectBase.Equals("b8", StringComparison.OrdinalIgnoreCase))
                {
                    WarpPlayerToRange(player, 164, 177, 570, 572);
                    return false;
                }
            }

            // Check if it's a victory portal
            bool isVictoryPortal = new[] { 4, 5, 10, 28, 30, 35 }.Contains(portal.PortalData.DestinationWarpGroup);

            // Check if the player is carrying a flag and is5v5, if true winningTeamOVD is set to offense
            bool isCarryingFlag = false;
            foreach (Arena.FlagState fs in arena._flags.Values)
            {
                if (fs.carrier == player)
                {
                    isCarryingFlag = true;
                    break;
                }
            }

            if (is5v5 && isCarryingFlag && isVictoryPortal)
            {
                // Check if there's a squad leader on the player's team
                bool hasSquadLeader = arena.Players.Any(p => 
                    p._team == player._team && 
                    !p._team.IsSpec && 
                    p._skills.Any(s => s.Value.skill.Name == "Squad Leader"));

                if (hasSquadLeader)
                {
                    winningTeamOVD = "offense";
                    arena.sendArenaMessage("Offense wins! Flag carrier has used portal.");
                }
            }

            //IF destination warp group is 200 (enemy portal), check if vehicle is on the map
            if (portal.PortalData.DestinationWarpGroup == 200)
            {
                var enemyPortal = arena.Vehicles.FirstOrDefault(v => 
                    v._type.Type == VehInfo.Types.Computer && 
                    v.relativeID == 292);

                if (enemyPortal != null)
                {
                    // Check team and coordinates for Collective
                    if ((player._team._name == "Collective" || player._team._name == "Collective'") &&
                        enemyPortal._state.positionX >= 1090 * 16 && enemyPortal._state.positionX <= 1140 * 16 &&
                        enemyPortal._state.positionY >= 570 * 16 && enemyPortal._state.positionY <= 620 * 16)
                    {
                        // arena.sendArenaMessage(string.Format("Collective player {0} using portal at X:{1}-{2}, Y:{3}-{4}",
                        //     player._alias, 1090, 1140, 570, 620));
                        player.warp(enemyPortal._state.positionX, enemyPortal._state.positionY);
                        return false;
                    }

                    // Check team and coordinates for Titan Militia
                    if ((player._team._name == "Titan Militia" || player._team._name == "Titan Militia'") &&
                        enemyPortal._state.positionX >= 991 * 16 && enemyPortal._state.positionX <= 1015 * 16 &&
                        enemyPortal._state.positionY >= 630 * 16 && enemyPortal._state.positionY <= 669 * 16)
                    {
                        // arena.sendArenaMessage(string.Format("Titan player {0} using portal at X:{1}-{2}, Y:{3}-{4}",
                        //     player._alias, 991, 1015, 630, 669));
                        player.warp(enemyPortal._state.positionX, enemyPortal._state.positionY);
                        return false;
                    }
                }
            }

            // Proceed with the default portal handling
            return true;
        }


        // Define the non-expiring item IDs
         HashSet<int> nonExpiringItemIDs = new HashSet<int> { 2005, 2007, 2009, 2, 9, 10, 11 };

        /// <summary>
        /// Triggered when a player requests to drop an item
        /// </summary>
        // [Scripts.Event("Player.ItemDrop")]
        // public bool playerItemDrop(Player player, ItemInfo item, object quantity)
        // {
        //     // Convert quantity to an int regardless of whether it's a ushort or int.
        //     int qty = Convert.ToInt32(quantity);

        //     // If on terrain 0, and it's 5 tso or tox (item ids 2005 and 2009), subtract 5 from minedStats.
        //     if (player._arena.getTerrainID(player._state.positionX, player._state.positionY) == 0 &&
        //     (item.id == 2005 || item.id == 2009))
        //     {
        //         string key = (item.id == 2005)
        //             ? player._team._name + " Tso mined"
        //             : player._team._name + " Tox mined";
        //         if (minedStats.ContainsKey(key))
        //         {
        //             minedStats[key] -= 5;
        //         }
        //         //player._arena.sendArenaMessage(string.Format("{0} has dropped 5 {1}!", player._alias, item.name));
        //     }
        //     return true;
        // }
        
        [Scripts.Event("Player.Leave")]
        public void playerLeave(Player player)
        {
            // Check if the player is still in the arena's player list
            if (!arena.Players.Contains(player))
            {
                int toxCount = player.getInventoryAmount(2009);
                int tsoCount = player.getInventoryAmount(2005);
                int panCount = player.getInventoryAmount(2007);

                if (toxCount > 0 || tsoCount > 0 || panCount > 0)
                {
                    string toxMessage = toxCount > 0 ? string.Format(" {0} Tox", toxCount) : "";
                    string tsoMessage = tsoCount > 0 ? string.Format(" {0} Tso", tsoCount) : "";
                    string panMessage = panCount > 0 ? string.Format(" {0} Pan", panCount) : "";

                    string message = string.Format("&{0} had{1}{2}{3}.", player._alias, toxMessage, tsoMessage, panMessage).Trim();
                    if (!string.IsNullOrEmpty(message))
                    {
                        arena.sendArenaMessage(message);
                    }
                }
            }
            return;
        }

        /// <summary>
        /// Triggered when a player decides to leave the game or spectate.
        /// This ensures that any flags the player was carrying are respawned at their original locations.
        /// </summary>
        [Scripts.Event("Player.LeaveGame")]
        public bool playerLeaveGame(Player player)
        {
            if (!arena._name.Contains("Arena 1"))
                deprizeMinPremades(player, false, false); // Remove items regardless of skill

            // Loop through each flag in the current map
            foreach (var flag in currentMap.Flags)
            {
                // Retrieve the flag object from the arena using the flag's name
                var fs = arena.getFlag(flag.Item1);  // flag.Item1 is the flag name

                if (fs != null && fs.carrier == player)
                {
                    // The player is carrying this flag
                    // Reset the flag's position to its original coordinates
                    fs.posX = (short)(flag.Item2 * 16);  // flag.Item2 is X coordinate
                    fs.posY = (short)(flag.Item3 * 16);  // flag.Item3 is Y coordinate

                    fs.bActive = true;      // Ensure the flag is active
                    fs.carrier = null;      // Clear the carrier
                    fs.team = null;         // Reset the team if necessary

                    // Update the flag's status for all players
                    Helpers.Object_Flags(arena.Players, fs);

                    // Since a player can carry only one flag, we can exit the loop
                    break;
                }
            }

            return true;
        }

        /// <summary>
        /// Called when the game begins
        /// </summary>
        [Scripts.Event("Game.Start")]
        public bool StartGame()
        {

            if (arena._name.Contains("CTFDL") || arena._name.Contains("OVDL")){ // or OVDL1
                // Disable champ item conversion for CTFDL or OVDL arenas
                isChampEnabled = false;
            }

            //Reset Flags
            if (currentEventType == EventType.MiniTP)
            {
                InitializeMiniTPEvent();
            } else {
                arena.flagReset();
                SpawnMapFlags();
            }

            if (!isOVD && (currentEventType != EventType.MiniTP))
            {
                // Let's not disturb the teams that OvD uses.
                SpawnMapPlayers();
            }
            
            HealAll();

            gameState = GameState.ActiveGame;
            flagMode = CTFMode.None;

            ResetKiller(null);
            killStreaks.Clear();

            //Team team1 = arena.Teams.ElementAtOrDefault(1);
            //Team team2 = arena.Teams.ElementAtOrDefault(2);
            List<Team> teamsWithPlayers = arena.Teams.Where(t => t.ActivePlayers.Count() > 0).ToList();
            Team team1 = teamsWithPlayers.FirstOrDefault(t => t._name.Contains(" T") || t._name.Contains("Titan"));
            Team team2 = teamsWithPlayers.FirstOrDefault(t => t._name.Contains(" C") || t._name.Contains("Collective"));

            // Extra check to make sure everyone is in dropship at game start
            // get all players in arena, if they are not on DropShip terrain 4, warp them back to the dropship by warping them warp group 2
            if (isOVD){
                foreach (Player p in arena.PlayersIngame)
                {
                    // Skip warping if player is a Dueler
                    if (GetPrimarySkillName(p) == "Dueler")
                        continue;

                    // Get player's current terrain ID
                    int terrainID = arena.getTerrainID(p._state.positionX, p._state.positionY);
                    // If player is not on dropship terrain (ID 4), warp them to warp group 2
                    if (terrainID != 4)
                    {
                        p.sendMessage(0, "You have been warped back to the DropShip for the game start.");
                        // Get warp group 2 coordinates from LIO
                        // Warp players to fixed coordinates based on their team

                        if (p._team == team1)
                        {
                            p.warp(682 * 16, 459 * 16); // Team 1 coordinates
                        }
                        else if (p._team == team2) 
                        {
                            p.warp(682 * 16, 614 * 16); // Team 2 coordinates
                        }
                    }
                }
            }

            //string teamNames = string.Join(", ", teamsWithPlayers.Select(t => t._name));
            //arena.sendArenaMessage(string.Format("Current teams with players: {0}", teamNames), 0);
            
            int team1FieldMedicCount = team1 != null ? arena.Players.Count(p => p._team._name == team1._name && p._skills.Values.Any(s => s.skill.Name == "Field Medic")) : 0;
            int team1CombatEngineerCount = team1 != null ? arena.Players.Count(p => p._team._name == team1._name && p._skills.Values.Any(s => s.skill.Name == "Combat Engineer")) : 0;
            int team2FieldMedicCount = team2 != null ? arena.Players.Count(p => p._team._name == team2._name && p._skills.Values.Any(s => s.skill.Name == "Field Medic")) : 0;
            int team2CombatEngineerCount = team2 != null ? arena.Players.Count(p => p._team._name == team2._name && p._skills.Values.Any(s => s.skill.Name == "Combat Engineer")) : 0;

            // Debug message for team counts
            // arena.sendArenaMessage(string.Format("Team 1: Field Medics - {0}, Combat Engineers - {1}. Team 2: Field Medics - {2}, Combat Engineers - {3}.", team1FieldMedicCount, team1CombatEngineerCount, team2FieldMedicCount, team2CombatEngineerCount));

            foreach (Player p in arena.Players)
            {
                string skillName = GetPrimarySkillName(p);
                PlayerStreak temp = new PlayerStreak();
                temp.lastKillerCount = 0;
                temp.lastUsedWeap = null;
                temp.lastUsedWepKillCount = 0;
                temp.lastUsedWepTick = -1;
                killStreaks.Add(p._alias, temp);
                if (currentEventType == EventType.Gladiator){
                    WarpPlayerToSpawn(p);
                }
                if (currentEventType == EventType.CTFX){
                    WarpPlayerToRange(p, 985, 997, 1117, 1129);
                    return false;
                }
                if (currentEventType == EventType.MiniTP){
                    if (p._team._name.Contains("Titan"))
                        WarpPlayerToRange(p, 654, 654, 518, 518);
                    else if (p._team._name.Contains("Collective")) 
                        WarpPlayerToRange(p, 648, 648, 565, 565);
                    return false;
                }

                Team playerTeam = p._team;
                if (!arena._name.Contains("Arena 1"))
                {
                    if (playerTeam == team1)
                    {
                        // Handling for Team 1 Field Medics
                        if (team1FieldMedicCount > 1 && skillName == "Field Medic")
                        {
                            deprizeMinPremades(p, false, false);
                            ItemInfo item2005 = arena._server._assets.getItemByID(2005);
                            ItemInfo item2007 = arena._server._assets.getItemByID(2007);
                            //arena.itemSpawn(item2005, 150, 689 * 16, 615 * 16, null);
                            arena.itemSpawn(item2005, 150, 689 * 16, 462 * 16, null);
                            arena.itemSpawn(item2007, 150, 689 * 16, 465 * 16, null);
                        }
                        else //if (skillName == "Field Medic")
                        {
                            deprizeMinPremades(p, true, false);
                        }

                        // Handling for Team 1 Combat Engineers
                        if (team1CombatEngineerCount > 1 && skillName == "Combat Engineer")
                        {
                            deprizeMinPremades(p, false, false);
                            ItemInfo item2009 = arena._server._assets.getItemByID(2009);
                            //arena.itemSpawn(item2009, 150, 689 * 16, 610 * 16, null);
                            arena.itemSpawn(item2009, 150, 689 * 16, 457 * 16, null);
                        }
                        else //if (skillName == "Combat Engineer")
                        {
                            deprizeMinPremades(p, true, false);
                        }
                    }
                    else if (playerTeam == team2)
                    {
                        // Handling for Team 2 Field Medics
                        if (team2FieldMedicCount > 1 && skillName == "Field Medic")
                        {
                            deprizeMinPremades(p, false, false);
                            ItemInfo item2005 = arena._server._assets.getItemByID(2005);
                            ItemInfo item2007 = arena._server._assets.getItemByID(2007);
                            //arena.itemSpawn(item2005, 150, 689 * 16, 615 * 16, null);
                            arena.itemSpawn(item2007, 150, 689 * 16, 618 * 16, null);
                            arena.itemSpawn(item2005, 150, 689 * 16, 615 * 16, null);
                            //arena.itemSpawn(item2005, 150, 689 * 16, 462 * 16, null);
                        }
                        else //if (skillName == "Field Medic")
                        {
                            deprizeMinPremades(p, true, false);
                        }

                        // Handling for Team 2 Combat Engineers
                        if (team2CombatEngineerCount > 1 && skillName == "Combat Engineer")
                        {
                            deprizeMinPremades(p, false, false);
                            ItemInfo item2009 = arena._server._assets.getItemByID(2009);
                            arena.itemSpawn(item2009, 150, 689 * 16, 610 * 16, null);
                            //arena.itemSpawn(item2009, 150, 689 * 16, 457 * 16, null);
                        }
                        else //if (skillName == "Combat Engineer")
                        {
                            deprizeMinPremades(p, true, false);
                        }
                    }
                }
            }
            //Let everyone know
            arena.sendArenaMessage("Game has started!", CFG.flag.resetBong);
            return true;
        }

        private bool AreAllFlagsCapturedByOneTeam()
        {
            Team potentialWinningTeam = null;

            // Check each flag's status
            foreach (Arena.FlagState fs in arena._flags.Values)
            {
                if (fs.bActive)
                {
                    // If no team has been set yet, use the current flag's team
                    if (potentialWinningTeam == null)
                        potentialWinningTeam = fs.team;
                    else if (fs.team != potentialWinningTeam)
                    {
                        // If a flag is held by a different team, not all flags are captured
                        return false;
                    }
                }
            }

            // All flags are captured by the same team, or no active flags left
            return potentialWinningTeam != null;
        }

        /// <summary>
        /// Attempts to spawn a given flag; returns true if successful, false if no suitable location found.
        /// </summary>
        public bool RandomizeFlagLocation(Arena.FlagState fs)
        {   //Set offsets
            int levelX = arena._server._assets.Level.OffsetX * 16;
            int levelY = arena._server._assets.Level.OffsetY * 16;

            //Give it some valid coordinates
            int attempts = 0;
            do
            {   //Make sure we're not doing this infinitely
                if (attempts++ > 200)
                {
                    Log.write(TLog.Error, "Unable to satisfy flag spawn for '{0}'.", fs.flag);
                    return false;
                }

                fs.posX = (short)(fs.flag.GeneralData.OffsetX - levelX);
                fs.posY = (short)(fs.flag.GeneralData.OffsetY - levelY);
                fs.oldPosX = fs.posX;
                fs.oldPosY = fs.posY;

                //Taken from Math.cs
                //For random flag spawn if applicable
                int lowerX = fs.posX - ((short)fs.flag.GeneralData.Width / 2);
                int higherX = fs.posX + ((short)fs.flag.GeneralData.Width / 2);
                int lowerY = fs.posY - ((short)fs.flag.GeneralData.Height / 2);
                int higherY = fs.posY + ((short)fs.flag.GeneralData.Height / 2);

                //Clamp within the map coordinates
                int mapWidth = (arena._server._assets.Level.Width - 1) * 16;
                int mapHeight = (arena._server._assets.Level.Height - 1) * 16;

                lowerX = Math.Min(Math.Max(0, lowerX), mapWidth);
                higherX = Math.Min(Math.Max(0, higherX), mapWidth);
                lowerY = Math.Min(Math.Max(0, lowerY), mapHeight);
                higherY = Math.Min(Math.Max(0, higherY), mapHeight);

                //Randomly generate some coordinates!
                int tmpPosX = ((short)arena._rand.Next(lowerX, higherX));
                int tmpPosY = ((short)arena._rand.Next(lowerY, higherY));

                //Check for allowable terrain drops
                int terrainID = arena.getTerrainID(tmpPosX, tmpPosY);
                for (int terrain = 0; terrain < 15; terrain++)
                {
                    if (terrainID == terrain && fs.flag.FlagData.FlagDroppableTerrains[terrain] == 1)
                    {
                        fs.posX = (short)tmpPosX;
                        fs.posY = (short)tmpPosY;
                        fs.oldPosX = fs.posX;
                        fs.oldPosY = fs.posY;
                        break;
                    }
                }

                //Check the terrain settings
                if (arena.getTerrain(fs.posX, fs.posY).flagTimerSpeed == 0)
                    continue;

            }
            while (arena.getTile(fs.posX, fs.posY).Blocked);

            return true;
        }

        /// <summary>
        /// Called when the game ends
        /// </summary>
        [Scripts.Event("Game.End")]
        public bool EndGame()
        {
            // Stop auto-save if it's running
            if (isAutoSaving)
            {
                isAutoSaving = false;
                autoSaveTimer.Dispose();
                autoSaveTimer = null;
            }
            if (!isOVD)
            {
                if (winningTeam == null)
                {
                    arena.sendArenaMessage("There was no winner.");
                }
                else
                {
                    UpdateGameEndFlagStats();

                    arena.sendArenaMessage(winningTeam._name + " has won the game!");
                    winningTeam = null;
                }
            }
            else
            {
                arena.sendArenaMessage("&Game has ended, Host may either *reset to spec all, or *restart for a rematch", 3);
            }

            // Only proceed if we have turret stats to write
            if (_turretStats != null && _turretStats.Any())
            {
                // Create base stats directory if it doesn't exist
                string baseStatsDir = "playerStats";
                if (!System.IO.Directory.Exists(baseStatsDir))
                {
                    System.IO.Directory.CreateDirectory(baseStatsDir);
                }

                // Export regular game stats as CSV if we have any players with stats
                if (arena.Players.Any(p => p.StatsLastGame != null))
                {
                    // Determine game mode based on player count
                    string gameMode = baseUsed != "Unknown" ? "OvD" : "Mix";
                    arena.sendArenaMessage(string.Format("Exporting stats for game mode {0}", gameMode), 0);

                    string gameStatsPath = System.IO.Path.Combine(baseStatsDir, string.Format("game_stats_{0}_{1}.csv", DateTime.Now.ToString("MM_dd_yyyy_HH_mm_ss"), arena._name.Replace(" ", "_")));
                    using (System.IO.StreamWriter writer = new System.IO.StreamWriter(gameStatsPath))
                    {
                        writer.WriteLine("PlayerName,Team,Kills,Deaths,Captures,CarrierKills,CarryTimeSeconds,GameLengthMinutes,Result,MainClass,ClassSwaps,TurretDamage,GameMode,Side,BaseUsed");

                        // Get teams with more than 9 players
                        var largeTeams = arena.Teams.Where(t => t.ActivePlayerCount > 9).ToList();

                        // For each large team, check their flag position against base coordinates
                        foreach (var team in largeTeams)
                        {
                            var flags = _flags.Where(f => f.team == team).ToList();
                            var flag = flags.FirstOrDefault();
                            if (flag != null)
                            {
                                int startX = 0, endX = 0, startY = 0, endY = 0;
                                string detectedBase = "Unknown";

                                // Use existing case logic to check coordinates
                                switch (baseUsed)
                                {
                                    case "D7":
                                        startX = 255 * 16; endX = 328 * 16;
                                        startY = 435 * 16; endY = 505 * 16;
                                        if (flag.posX >= startX && flag.posX <= endX && 
                                            flag.posY >= startY && flag.posY <= endY)
                                            detectedBase = "D7";
                                        break;
                                    case "F6":
                                        startX = 375 * 16; endX = 481 * 16;
                                        startY = 435 * 16; endY = 509 * 16;
                                        if (flag.posX >= startX && flag.posX <= endX && 
                                            flag.posY >= startY && flag.posY <= endY)
                                            detectedBase = "F6";
                                        break;
                                    case "F4":
                                        startX = 367 * 16; endX = 435 * 16;
                                        startY = 224 * 16; endY = 306 * 16;
                                        if (flag.posX >= startX && flag.posX <= endX && 
                                            flag.posY >= startY && flag.posY <= endY)
                                            detectedBase = "F4";
                                        break;
                                    case "A7":
                                        startX = 255 * 16; endX = 328 * 16;
                                        startY = 435 * 16; endY = 505 * 16;
                                        if (flag.posX >= startX && flag.posX <= endX && 
                                            flag.posY >= startY && flag.posY <= endY)
                                            detectedBase = "A7";
                                        break;
                                    case "A5":
                                        startX = 4 * 16; endX = 79 * 16;
                                        startY = 305 * 16; endY = 377 * 16;
                                        if (flag.posX >= startX && flag.posX <= endX && 
                                            flag.posY >= startY && flag.posY <= endY)
                                            detectedBase = "A5";
                                        break;
                                    case "B6":
                                        startX = 128 * 16; endX = 203 * 16;
                                        startY = 432 * 16; endY = 515 * 16;
                                        if (flag.posX >= startX && flag.posX <= endX && 
                                            flag.posY >= startY && flag.posY <= endY)
                                            detectedBase = "B6";
                                        break;
                                    default:
                                        startX = 0; endX = 0;
                                        startY = 0; endY = 0;
                                        break;
                                }
                                // Arena message for base used for that team
                                arena.sendArenaMessage(string.Format("Team {0} used base {1}", team._name, detectedBase));

                                if (detectedBase != "Unknown")
                                {
                                    arena.sendArenaMessage(string.Format("Team {0} is using base {1}", team._name, detectedBase));
                                }
                            }
                        }

                        // Cache the players list to avoid potential modification during iteration
                        var players = arena.Players.ToList();
                        foreach (Player p in players)
                        {
                            // Guard clauses to skip invalid players
                            if (p == null || p.StatsLastGame == null || p._team == null) continue;
                            if (!p._team._name.Contains(" T") && !p._team._name.Contains(" C")) continue;

                            ctfPlayerProxy.player = p;
                            double gameLengthMinutes = (arena._tickGameEnded - arena._tickGameStarted) / (1000.0 * 60.0);
                            string result = "Loss";
                            
                            if (gameMode == "OvD")
                            {
                                bool isOffense = p._team.ActivePlayers.Any(player => 
                                    playerLastSkillNames.ContainsKey(player) && 
                                    playerLastSkillNames[player] == "Squad Leader");

                                if (isOffense)
                                {
                                    if (winningTeamOVD == "offense")
                                    {
                                        result = "Win";
                                    }
                                }
                                else
                                {
                                    if (winningTeamOVD != "offense")
                                    {
                                        result = "Win";
                                    }
                                }
                            }
                            else
                            {
                                if (winningTeam != null && p._team == winningTeam)
                                {
                                    result = "Win";
                                }
                            }

                            string side = "N/A";
                            if (gameMode == "Mix")
                            {
                                var teamSummonedCounts = summonedCounts.Where(x => x.Key.ToString() == p._team._name).ToList();
                                if (teamSummonedCounts.Count > 0)
                                {
                                    var top4 = teamSummonedCounts.OrderByDescending(x => x.Value).Take(4);
                                    side = top4.Any(x => x.Key == p._id) ? "offense" : "defense";
                                }
                                else
                                {
                                    string skill = GetPrimarySkillName(p);
                                    if (skill == "Squad Leader")
                                    {
                                        side = "offense";
                                    }
                                    else if (skill == "Field Medic" || skill == "Combat Engineer")
                                    {
                                        side = "defense";
                                    }
                                }
                            }

                            string mainClass = "Unknown";
                            if (playerClassPlayTimes.ContainsKey(p))
                            {
                                var playTimes = playerClassPlayTimes[p];
                                if (playTimes.Any())
                                {
                                    mainClass = playTimes.OrderByDescending(x => x.Value).First().Key;
                                }
                            }
                            if (mainClass == "Unknown")
                            {
                                mainClass = GetPrimarySkillName(p);
                            }

                            int turretDamage = playerDamageStats.ContainsKey(p._id) ? playerDamageStats[p._id] : 0;

                            if (gameMode == "OvD" && p._team != null)
                            {
                                bool hasSquadLeader = p._team.ActivePlayers.Any(player => 
                                    playerLastSkillNames.ContainsKey(player) && 
                                    playerLastSkillNames[player] == "Squad Leader");

                                bool hasFieldMedic = p._team.ActivePlayers.Any(player => 
                                    playerLastSkillNames.ContainsKey(player) && 
                                    playerLastSkillNames[player] == "Field Medic");

                                bool hasCombatEngineer = p._team.ActivePlayers.Any(player => 
                                    playerLastSkillNames.ContainsKey(player) && 
                                    playerLastSkillNames[player] == "Combat Engineer");

                                if (hasSquadLeader)
                                {
                                    side = "offense";
                                }
                                else if (hasFieldMedic || hasCombatEngineer)
                                {
                                    side = "defense";
                                }
                            }

                            int classSwaps = playerClassSwaps.ContainsKey(p) ? playerClassSwaps[p] : 0;

                            writer.WriteLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7:F2},{8},{9},{10},{11},{12},{13},{14}",
                                p._alias.Replace(",", ""),
                                p._team._name ?? "None",
                                p.StatsLastGame.kills,
                                p.StatsLastGame.deaths,
                                p.StatsLastGame.zonestat5,
                                p.StatsLastGame.zonestat7,
                                p.StatsLastGame.zonestat3,
                                gameLengthMinutes,
                                result,
                                mainClass,
                                classSwaps,
                                turretDamage,
                                gameMode,
                                side,
                                baseUsed));

                            ctfPlayerProxy.player = null;
                        }
                    }
                }
            }

            gameState = GameState.PostGame;
            arena.flagReset();
            isSD = false;
            isSecondOvertime = false;
            overtimeStart = 0;
            secondOvertimeStart = 0;
            redirectSide = null;
            redirectBase = null;
            _turretStats.Clear();
            _playerWeaponStats.Clear();
            _ebHitStats.Clear();
            minedStats.Clear();
            playerClassPlayTimes.Clear();
            playerLastClassSwitch.Clear();
            playerClassSwaps.Clear();
            winningTeamOVD = "defense";
            return true;
        }

        /// <summary>
        /// Called to reset the game state
        /// </summary>
        [Scripts.Event("Game.Reset")]
        public bool gameReset()
        {
            gameState = GameState.PostGame;
            arena.flagReset();
            ResetKiller(null);
            killStreaks.Clear();

            // Reset mix state and player pool
            mixGameActive = true;
            isMixActive = false;
            isSD = false;
            isSecondOvertime = false;
            overtimeStart = 0;
            secondOvertimeStart = 0;
            playerPool.Clear();
            playerRolePreferences.Clear();
            assignedPlayersGlobal.Clear();
            team1.Clear();
            team2.Clear();
            aliasCounter = 0;
            signUpOrder.Clear();
            redirectSide = null;
            redirectBase = null;
            is5v5 = false;
            minedStats.Clear();
            _turretStats.Clear();
            _playerWeaponStats.Clear();
            _ebHitStats.Clear();
            playerClassPlayTimes.Clear();
            playerLastClassSwitch.Clear();
            playerClassSwaps.Clear();
            winningTeamOVD = "defense";

            // Show breakdown for current game stats
            //arena.breakdown(true);

            if (isOVD)
            {
                arena.sendArenaMessage("& ----=[ Please type ?team spec or ?playing or ?p to ready up for the next game! ]=----", 30);

                // Spec all players and show their individual breakdowns
                foreach (Player p in arena.Players)
                {
                    p.spec("np");
                    //arena.individualBreakdown(p, true); // Show current game stats
                }
            }

            return true;
        }


        #endregion

        #region Player Events
        private Dictionary<string, TurretData> _turretStats = new Dictionary<string, TurretData>();
        public class TurretData
        {
            public string TeamName { get; set; }
            public string TurretType { get; set; }
            public int Kills { get; set; }
            public List<int> PlayersKilled { get; set; }
            public Dictionary<int, int> DamageReceived { get; set; } // Player ID to damage amount

            public TurretData(string teamName, string turretType)
            {
                TeamName = teamName;
                TurretType = turretType;
                Kills = 0;
                PlayersKilled = new List<int>();
                DamageReceived = new Dictionary<int, int>();
            }
        }

        /// <summary>
        /// Triggered when a player dies to a computer(E.G. turret)
        /// </summary>
        [Scripts.Event("Player.ComputerKill")]
        public bool ComputerKill(Player victim, Computer vehicle)
        {
            // Get the turret's team name
            string teamName = vehicle._team != null ? vehicle._team._name : "Neutral";

            // Get the turret's type/name
            string turretType = vehicle._type.Name;

            // Create a composite key
            string key = teamName + ":" + turretType;

            // Declare the variable before using it in TryGetValue
            TurretData data;

            // Get or create the turret's data
            if (!_turretStats.TryGetValue(key, out data))
            {
                data = new TurretData(teamName, turretType);
                _turretStats.Add(key, data);
            }

            // Update the turret's stats
            data.Kills++;
            data.PlayersKilled.Add(victim._id);

            return true;
        }

        /// <summary>
        /// Called when a player requests to pick up/drop the flag.
        /// </summary>
        /// <param name="from"></param>
        /// <param name="bPickup"></param>
        /// <param name="bSuccess"></param>
        /// <param name="flag"></param>
        /// <returns></returns>
        // Dictionary to track players and the flags they are carrying
        Dictionary<Player, LioInfo.Flag> playerFlagMap = new Dictionary<Player, LioInfo.Flag>();

        [Scripts.Event("Player.FlagAction")]
        public bool playerFlagAction(Player from, bool bPickup, bool bSuccess, LioInfo.Flag flag)
        {
            if (bPickup && bSuccess)
            {
                // Track the flag the player picked up
                if (!playerFlagMap.ContainsKey(from))
                {
                    playerFlagMap[from] = flag;  // Store the flag the player is carrying
                }

                ctfPlayerProxy.player = from;
                ctfPlayerProxy.Captures++;
                ctfPlayerProxy.player = null;
            }
            else if (!bPickup && bSuccess)
            {
                // If the player successfully drops the flag, remove it from the tracking map
                if (playerFlagMap.ContainsKey(from))
                {
                    playerFlagMap.Remove(from);
                }
            }

            return true;
        }

        public void EmulateSwitch(Player player, bool? isOpen = null)
        {
            // Find the associated switch
            LioInfo.Switch sw = player._server._assets.getLioByID(708) as LioInfo.Switch;

            if (sw == null)
            {
                player.sendMessage(0, "Switch not found.");
                return;
            }

            // Get the current switch state
            Arena.SwitchState ss;
            if (!player._arena._switches.TryGetValue(sw.GeneralData.Id, out ss))
            {
                player.sendMessage(0, "Switch state not found.");
                return;
            }

            // Determine the desired state
            bool currentState = ss.bOpen;
            bool desiredState = isOpen ?? !currentState; // If isOpen is null, toggle the state

            // Call the arena's handlePlayerSwitch method via an event
            player._arena.handleEvent(delegate (Arena arena)
            {
                player._arena.handlePlayerSwitch(player, desiredState, sw);
                // Send a message indicating the switch operation was successful
                player.sendMessage(0, string.Format("Switch operation successful. Switch is now {0}.", desiredState ? "open" : "closed"));
            });
        }

        /// <summary>
        /// Called when a player sends a chat command
        /// </summary>
        [Scripts.Event("Player.ChatCommand")]
        public bool playerChatCommand(Player player, Player recipient, string command, string payload)
        {
            switch (command.ToLower())
            {
                // case "lm":
                //     // server-side alias for loadmacro
                //     string macroName = payload; // whatever the user typed after "?lm"
                //     CS_Chat pkt = new CS_Chat
                //     {
                //         chatType = Helpers.Chat_Type.Macro,
                //         recipient = "",
                //         message = "?loadmacro " + macroName
                //     };
                //     player._client.sendReliable(pkt);
                //     break;
                case "playtime":
                    SendOrderedPlayTimes(player);
                    break;
                case "stats":
                    ListPlayerWeaponStats(player, payload);
                    break;
                case "playing":
                case "p": // Alias for playing
                    player.spec("spec");
                    break;
                case "np":
                    {
                        player.spec();
                    }
                    break;
                case "pool":
                    DisplayCurrentPlayerPool(player);
                    break;
                case "teamJoin":
                case "t":
                    privateTeams(player, recipient, command, payload);
                    break;
                case "build":
                case "b": // Alias for build
                    HandleBuildCommand(player, payload);
                    break;
                case "wipe":
                case "w":  // Alias for wipe
                    WipeInventory(player, payload);
                    break;
                case "bw":
                    WipeAndBuy(player, payload);
                    break;
                case "bwd":
                    WipeBuyAndMaxAmmo(player, payload);
                    break;
                case "d":
                    DropUnusedItems(player, payload);
                    break;
                case "short":
                    commandHandler.HandleShortCommand(player, payload);
                    break;
                case "swap":
                    commandHandler.HandleSwapCommand(player, payload, CFG, this);
                    // If MiniTP event, warp player to dropship based on team on.
                    if (currentEventType == EventType.MiniTP){
                        if (player._team._name.Contains("Titan")) {
                            WarpPlayerToRange(player, 654, 654, 518, 518);
                        }
                        else if (player._team._name.Contains("Collective")) {
                            WarpPlayerToRange(player, 648, 648, 565, 565);
                        }
                    }
                    break;
                case "champ":
                    if (isChampEnabled)
                        HandleChampCommand(player);
                    else
                        player.sendMessage(-1, "The champ command is currently disabled.");
                    break;
                case "inv":
                    DisplayInventoryItems(player);
                    break;
                 case "mg":
                    BuildTurret(player, "MG Turret");
                    break;
                case "rocket":
                    BuildTurret(player, "Rocket Turret");
                    break;
                case "plasma":
                    BuildTurret(player, "Plasma Turret");
                    break;
                case "duel":
                    Duel(player);
                    break;
                case "mix":
                    if (isMixActive)
                    {
                        HandleRoleSelection(player, payload);
                    }
                    else
                    {
                        player.sendMessage(-1, "*Mix is not currently active.");
                    }
                    break;
                case "glad":
                    if (currentEventType == EventType.Gladiator)
                    {
                        JoinGladiatorEvent(player);
                    }
                    else
                    {
                        player.sendMessage(-1, "Gladiator event is not currently active.");
                    }
                    break;

                return false;
            }
            return true;
        }

        private int CalculateExplosionDamage(ItemInfo.Projectile wep, double distance, double damageMultiplier = 1.0)
        {
            double radius = wep.explosiveDamageRadius; // Explosion radius in pixels

            // If the target is outside the explosion radius, no damage is dealt
            if (distance > radius)
                return 0;

            // Damage at the center of the explosion
            int damageInner = wep.explosiveDamageInner;

            // Damage at the edge of the explosion
            int damageOuter = wep.explosiveDamageOuter;

            // Bypass damage at the center
            int bypassDamageInner = wep.bypassDamageInner;

            // Bypass damage at the edge
            int bypassDamageOuter = wep.bypassDamageOuter;

            double damage = 0;
            double bypassDamage = 0;

            // Determine how damage falls off based on the explosiveDamageMode
            switch (wep.explosiveDamageMode)
            {
                case 0:
                    {
                        // Linear falloff for damage and bypass damage
                        double distanceRatio = distance / radius;
                        damage = (int)Math.Ceiling((double)(damageInner - ((damageInner - damageOuter) * distanceRatio)));
                        bypassDamage = (int)Math.Ceiling((double)(bypassDamageInner - ((bypassDamageInner - bypassDamageOuter) * distanceRatio)));
                        break;
                    }
                case 1:
                    {
                        // Constant damage and bypass damage within the radius (no falloff)
                        damage = (int)Math.Ceiling((double)damageInner);
                        bypassDamage = (int)Math.Ceiling((double)bypassDamageInner);
                        break;
                    }
                default:
                    {
                        // Default to linear falloff if mode is unrecognized
                        double distanceRatio = distance / radius;
                        damage = (int)Math.Ceiling(damageInner - ((damageInner - damageOuter) * distanceRatio));
                        bypassDamage = (int)Math.Ceiling(bypassDamageInner - ((bypassDamageInner - bypassDamageOuter) * distanceRatio));
                        break;
                    }
            }

            // Apply damage multiplier only to explosive damage, not bypass damage
            damage *= damageMultiplier;

            // Ensure damage and bypass damage are not negative
            if (damage < 0)
                damage = 0;
            if (bypassDamage < 0)
                bypassDamage = 0;

            // Add bypass damage to the overall damage
            damage += bypassDamage;

            // Always round up to the nearest whole number
            return (int)Math.Ceiling(damage);
        }


        private double CalculateDistance(short x1, short y1, short x2, short y2)
        {
            // Positions are in 1/1000th of a pixel, so divide by 1000.0
            double posX1 = x1 / 1000.0;
            double posY1 = y1 / 1000.0;
            double posX2 = x2 / 1000.0;
            double posY2 = y2 / 1000.0;

            double deltaX = posX1 - posX2;
            double deltaY = posY1 - posY2;
            return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }

        // Dictionary to store player damage stats that can be accessed from other methods
        private Dictionary<int, int> playerDamageStats = new Dictionary<int, int>();
        private List<KeyValuePair<int, int>> orderedPlayerStats = new List<KeyValuePair<int, int>>();

        /// <summary>
        /// Updates the player damage stats based on turret data
        /// </summary>
        private void UpdatePlayerDamageStats()
        {
            playerDamageStats.Clear();
            // Calculate total damage dealt by each player across all turrets
            foreach (var turretData in _turretStats.Values)
            {
                foreach (var damageEntry in turretData.DamageReceived)
                {
                    if (!playerDamageStats.ContainsKey(damageEntry.Key))
                        playerDamageStats[damageEntry.Key] = 0;
                    playerDamageStats[damageEntry.Key] += damageEntry.Value;
                }
            }

            // Order players by total damage dealt
            orderedPlayerStats = playerDamageStats
                .OrderByDescending(x => x.Value)
                .Take(3)
                .ToList();
        }

        // Method to list weapon stats for all players or a specific player
        private void ListPlayerWeaponStats(Player requestingPlayer, string alias = null)
        {
            if (string.IsNullOrEmpty(alias))
            {
                // List all players' weapon stats
                requestingPlayer.sendMessage(0, "#All Player Weapon Stats");
                foreach (var playerStats in _playerWeaponStats)
                {
                    Player player = playerStats.Key;
                    requestingPlayer.sendMessage(0, string.Format("@{0}'s Weapon Stats:", player._alias));
                    
                    // Show turret damage if any
                    if (playerDamageStats.ContainsKey(player._id))
                    {
                        requestingPlayer.sendMessage(0, string.Format("*Turret Damage Dealt: {0}", playerDamageStats[player._id]));
                    }

                    foreach (var weaponStat in playerStats.Value)
                    {
                        if (weaponStat.Value.ShotsFired > 0)
                        {
                            string weaponName = player._server._assets.getItemByID(weaponStat.Key).name;
                            requestingPlayer.sendMessage(0, string.Format("*Weapon: {0} | Shots: {1}/{2} | Acc: {3:P2}",
                                weaponName,
                                weaponStat.Value.ShotsLanded,
                                weaponStat.Value.ShotsFired,
                                (double)weaponStat.Value.ShotsLanded / weaponStat.Value.ShotsFired));
                        }
                    }
                }
            }
            else
            {
                // List specific player's weapon stats
                var player = arena.Players.FirstOrDefault(p => p._alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
                if (player != null && _playerWeaponStats.ContainsKey(player))
                {
                    requestingPlayer.sendMessage(0, string.Format("#{0}'s Weapon Stats", player._alias));
                    
                    // Show turret damage if any
                    if (playerDamageStats.ContainsKey(player._id))
                    {
                        requestingPlayer.sendMessage(0, string.Format("&TURRET DMG: {0}", playerDamageStats[player._id]));
                    }

                    foreach (var weaponStat in _playerWeaponStats[player])
                    {
                        if (weaponStat.Value.ShotsFired > 0)
                        {
                            string weaponName = player._server._assets.getItemByID(weaponStat.Key).name;
                            requestingPlayer.sendMessage(0, string.Format("*Weapon: {0} | Shots: {1}/{2} | Acc: {3:P2}",
                                weaponName,
                                weaponStat.Value.ShotsLanded,
                                weaponStat.Value.ShotsFired,
                                (double)weaponStat.Value.ShotsLanded / weaponStat.Value.ShotsFired));
                        }
                    }
                }
                else
                {
                    requestingPlayer.sendMessage(0, string.Format("No stats found for player '{0}'", alias));
                }
            }
        }

        // Dictionary to track stats for EB's hit per player (top 3)
        private Dictionary<Player, int> _ebHitStats = new Dictionary<Player, int>();

        /// <summary>
        /// Called when the player uses ?breakdown or end game is called
        /// </summary>
        [Scripts.Event("Player.Breakdown")]
        public bool breakdown(Player from, bool bCurrent)
        {
            // Display players weapon stats if they exist
            // Display players weapon stats if they exist
            if (_playerWeaponStats != null && _playerWeaponStats.Any())
            {
                from.sendMessage(0, "#Top 3 Player Accuracy");
                
                try
                {
                    // Calculate best accuracy per player
                    var playerAccuracies = _playerWeaponStats
                        .Select(playerStats => new {
                            Player = playerStats.Key._alias,
                            WeaponStats = playerStats.Value
                                .Where(w => w.Value != null && w.Value.ShotsFired > 0) // Only consider weapons that were fired
                                .OrderByDescending(w => (double)w.Value.ShotsLanded / w.Value.ShotsFired)
                                .DefaultIfEmpty() // Handle case where no weapons were fired
                                .First(),
                            WeaponId = playerStats.Value
                                .Where(w => w.Value != null && w.Value.ShotsFired > 0)
                                .OrderByDescending(w => (double)w.Value.ShotsLanded / w.Value.ShotsFired)
                                .Select(w => w.Key)
                                .DefaultIfEmpty()
                                .First()
                        })
                        .Where(x => x.WeaponStats.Value != null) // Filter out players with no valid weapon stats
                        .OrderByDescending(x => (double)x.WeaponStats.Value.ShotsLanded / x.WeaponStats.Value.ShotsFired)
                        .Take(3);

                    int rank = 1;
                    foreach (var entry in playerAccuracies)
                    {
                        double accuracy = (double)entry.WeaponStats.Value.ShotsLanded / entry.WeaponStats.Value.ShotsFired;
                        string weaponName = from._server._assets.getItemByID(entry.WeaponId).name;
                        from.sendMessage(0, string.Format("*{0}. {1} - {2:P2} accuracy with {3} ({4}/{5})", 
                            rank++, entry.Player, accuracy,
                            weaponName,
                            entry.WeaponStats.Value.ShotsLanded, 
                            entry.WeaponStats.Value.ShotsFired));
                    }
                }
                catch (Exception ex)
                {
                    from.sendMessage(0, "Error displaying weapon stats.");
                }
            }

            // Show EB's hit stats if any
            if (_ebHitStats != null && _ebHitStats.Any())
            {
                try
                {
                    from.sendMessage(0, "#EB's Hit Stats");
                    var orderedEBStats = _ebHitStats.OrderByDescending(s => s.Value).Take(3).ToList();
                    if (orderedEBStats.Count >= 1)
                    {
                        from.sendMessage(0, string.Format("*1st (HIT={0}): {1}", orderedEBStats[0].Value, orderedEBStats[0].Key));
                    }
                    if (orderedEBStats.Count >= 2)
                    {
                        from.sendMessage(0, string.Format("*2nd (HIT={0}): {1}", orderedEBStats[1].Value, orderedEBStats[1].Key));
                    }
                    if (orderedEBStats.Count >= 3)
                    {
                        from.sendMessage(0, string.Format("*3rd (HIT={0}): {1}", orderedEBStats[2].Value, orderedEBStats[2].Key));
                    }
                }
                catch (Exception ex)
                {
                    from.sendMessage(0, "Error displaying EB hit stats.");
                }
            }
            
            // Show mined stats if any
            if (minedStats != null && minedStats.Any())
            {
                try
                {
                    from.sendMessage(0, "#Mined Stats");
                    
                    // Group stats by team name and calculate totals
                    var teamTotals = minedStats
                        .GroupBy(x => x.Key.Split(' ')[0]) // Group by team name
                        .Select(g => new {
                            Team = g.Key,
                            ToxCount = g.Where(x => x.Key.Contains("Tox")).Sum(x => x.Value),
                            TsoCount = g.Where(x => x.Key.Contains("Tso")).Sum(x => x.Value),
                            Total = g.Sum(x => x.Value)
                        })
                        .OrderByDescending(x => x.Total)
                        .Take(3)
                        .ToList();

                    if (teamTotals.Count >= 1)
                    {
                        from.sendMessage(0, string.Format("*1st {0}: (TOX={1}, TSO={2})", 
                            teamTotals[0].Team, teamTotals[0].ToxCount, teamTotals[0].TsoCount));
                    }
                    if (teamTotals.Count >= 2) 
                    {
                        from.sendMessage(0, string.Format("*2nd {0}: (TOX={1}, TSO={2})",
                            teamTotals[1].Team, teamTotals[1].ToxCount, teamTotals[1].TsoCount));
                    }
                    if (teamTotals.Count >= 3)
                    {
                        from.sendMessage(0, string.Format("*3rd {0}: (TOX={1}, TSO={2})",
                            teamTotals[2].Team, teamTotals[2].ToxCount, teamTotals[2].TsoCount));
                    }
                }
                catch (Exception ex)
                {
                    from.sendMessage(0, "Error displaying mined stats.");
                }
            }
            
            // Only show turret stats if we're doing a breakdown for the current game
            // and there are actually turret stats to show
            if (bCurrent && _turretStats != null && _turretStats.Any())
            {
                from.sendMessage(0, "#Turret Stats");
                
                var orderedStats = _turretStats
                    .OrderByDescending(x => x.Value.Kills)
                    .ToList();

                // Find the top team's stats
                var topTeamStats = orderedStats
                    .GroupBy(x => x.Key.Split(':')[0])
                    .OrderByDescending(g => g.Sum(x => x.Value.Kills))
                    .Take(2)
                    .ToList();

                if (topTeamStats.Count >= 1)
                {
                    from.sendMessage(0, string.Format("*1st (K={0}): {1}", 
                        topTeamStats[0].Sum(x => x.Value.Kills),
                        topTeamStats[0].Key));
                }

                if (topTeamStats.Count >= 2) 
                {
                    from.sendMessage(0, string.Format("*2nd (K={0}): {1}",
                        topTeamStats[1].Sum(x => x.Value.Kills), 
                        topTeamStats[1].Key));
                }

                // Update player damage stats
                UpdatePlayerDamageStats();

                from.sendMessage(0, "#Top Turret Damage Dealers");
                if (orderedPlayerStats.Count >= 1)
                {
                    Player player = arena.getPlayerById((uint)orderedPlayerStats[0].Key);
                    string playerName = player != null ? player._alias : "Unknown";
                    from.sendMessage(0, string.Format("*1st (DMG={0}): {1}", 
                        orderedPlayerStats[0].Value,
                        playerName));
                }

                if (orderedPlayerStats.Count >= 2)
                {
                    Player player = arena.getPlayerById((uint)orderedPlayerStats[1].Key);
                    string playerName = player != null ? player._alias : "Unknown";
                    from.sendMessage(0, string.Format("*2nd (DMG={0}): {1}",
                        orderedPlayerStats[1].Value,
                        playerName));
                }

                if (orderedPlayerStats.Count >= 3)
                {
                    Player player = arena.getPlayerById((uint)orderedPlayerStats[2].Key);
                    string playerName = player != null ? player._alias : "Unknown";
                    from.sendMessage(0, string.Format("*3rd (DMG={0}): {1}",
                        orderedPlayerStats[2].Value,
                        playerName));
                }
            }
            else
            {
                from.sendMessage(0, "No turret stats available for this game.");
            }

            return false;
        }
        // Dictionary to track Freedom AR shot counts per player
        private Dictionary<Player, int> _freedomARShotCount = new Dictionary<Player, int>();
        // New method to handle projectile explosions properly using SC_Projectile
        private void HandleExplosionProjectile(short posX, short posY, short posZ, int projectileId, int shooterId, byte yaw)
        {
            SC_Projectile explosion = new SC_Projectile
            {
                projectileID = (short)projectileId,
                playerID = (ushort)shooterId,
                posX = posX,
                posY = posY,
                posZ = posZ,
                yaw = yaw
            };

            foreach (Player p in arena.Players)
            {
                p._client.sendReliable(explosion);
            }
        }

        private Dictionary<string, Player> _projectileOwners = new Dictionary<string, Player>();
        private HashSet<int> _ignoredExplosionWeaponIds = new HashSet<int>() { 1117, 1199, 1200 };
        private int _lastExplosionTick;
        private Player _lastExplosionShooter;
        private int _lastExplosionWeaponId;
        private short _lastExplosionX, _lastExplosionY, _lastExplosionZ;

        /// <summary>
        /// Triggered when an explosion happens from a projectile a player fired
        /// </summary>
        [Scripts.Event("Player.Explosion")]
        public bool playerExplosion(Player from, ItemInfo.Projectile usedWep, short posX, short posY, short posZ)
        {
            // Explosion occurred at coordinates arena message:
            //arena.sendArenaMessage(string.Format("{0} exploded at {1}, {2}, {3}", usedWep.name, posX, posY, posZ));

            // EB's hit stats for projectiles (IDs 1163 through 1166)
            if (usedWep.id >= 1163 && usedWep.id <= 1166)
            {
                // Get players within 20 pixels of explosion
                List<Player> playersInRange = arena.getPlayersInRange(posX, posY, 20);
                
                if (playersInRange.Count > 0)
                {
                    if (!_ebHitStats.ContainsKey(from))
                    {
                        _ebHitStats[from] = 0;
                    }
                    _ebHitStats[from]++;
                }
                // Map this explosion to its shooter using a composite key
                string key = string.Format("{0}_{1}_{2}_{3}", usedWep.id, posX, posY, posZ);
                _projectileOwners[key] = from;
            }

            // Stats for weapon damage
            if (_playerStatsEnabled)
            {
                // Only update the global explosion record if this explosion is NOT one of the ignorable ones
                if (!_ignoredExplosionWeaponIds.Contains(usedWep.id))
                {
                    _lastExplosionTick = Environment.TickCount;
                    _lastExplosionShooter = from;
                    _lastExplosionWeaponId = usedWep.id;
                    _lastExplosionX = posX;
                    _lastExplosionY = posY;
                    _lastExplosionZ = posZ;
                }

                // Initialize stats for new players
                if (!_playerWeaponStats.ContainsKey(from))
                {
                    _playerWeaponStats[from] = new Dictionary<int, WeaponStats>();
                }

                // Initialize stats for new weapons
                if (!_playerWeaponStats[from].ContainsKey(usedWep.id))
                {
                    _playerWeaponStats[from][usedWep.id] = new WeaponStats();
                }

                var stats = _playerWeaponStats[from][usedWep.id];

                // Track shots fired for specific weapon types
                switch (usedWep.id)
                {
                    case 1115: // SC
                    case 3032: // AC
                    case 1083: // RPG
                    case 1105: // Maklov LMG
                    case 3027: // Kuchler LMG
                    case 1052: // MML Middle Micro
                    case 1556: // MMl Middle Mini
                    case 5050: // CAW
                    case 1318: // Rifle Grenade
                    case 1113: // Tranq
                    case 1163: // EB
                    case 1164: // EB
                    case 1165: // EB
                    case 1166: // EB
                    case 3017: // Maklov AR
                    case 3013: // Kuchler AR
                    case 3018: // Titan AR
                    case 1179: // Haywire (128 radius)
                    case 1178: // Haywire (102.4 radius)
                    case 1173: // Haywire (76.8 radius)
                    case 1174: // Haywire (51.2 radius)
                    case 1175: // Haywire (25.6 radius)

                        stats.ShotsFired++;
                        // Map this explosion to its shooter using a composite key
                        string key = string.Format("{0}_{1}_{2}_{3}", usedWep.id, posX, posY, posZ);
                        _projectileOwners[key] = from;
                        // arena.sendArenaMessage(string.Format("{0} fired weapon {1} (Total shots: {2})", 
                        //     from._alias, usedWep.name, stats.ShotsFired));
                        break;
                }
            }

            // albert's EMP Grenade
            if ((usedWep.name == "EMP Grenade" || usedWep.name == "Haywire Grenade") && (from._alias == "albert") && isChampEnabled)
            {
                // Get players within 20 pixels of explosion
                List<Player> playersInRange = arena.getPlayersInRange(posX, posY, 128);
                
                if (playersInRange.Count > 0)
                {
                    string word = "BLOOP!";
                    int xOffset = 10;
                    for (int i = 0; i < word.Length; i++)
                    {
                        char letter = word[i];
                        ItemInfo.Projectile letterWep = AssetManager.Manager.getItemByName(letter.ToString()) as ItemInfo.Projectile;
                        if (letterWep != null)
                        {
                            short newPosX = (short)(posX + (i * xOffset));
                            HandleExplosionProjectile(newPosX, posY, posZ, letterWep.id, from._id, from._state.yaw);
                        }
                    }
                }
            }
            
            // Herthbul's Tranq
            if (usedWep.name == "Tranq" && (from._alias == "Herthbul") && isChampEnabled)
            {
                // Get players within 20 pixels of explosion
                List<Player> playersInRange = arena.getPlayersInRange(posX, posY, 20);
                
                if (playersInRange.Count > 0)
                {
                    int[] explosionIds = new int[] { 1301, 1306, 1307 };
                    int randomIndex = new Random().Next(explosionIds.Length);
                    int selectedExplosionId = explosionIds[randomIndex];

                    // Instead of using CS_Explosion (which causes an unowned explosion error),
                    // create and send a projectile explosion message so that the explosion is properly owned.
                    byte yaw = from._state.yaw;
                    SC_Projectile tranqExplosion = new SC_Projectile
                    {
                        projectileID = (short)selectedExplosionId,
                        playerID = (ushort)from._id,
                        posX = posX,
                        posY = posY,
                        posZ = posZ,
                        yaw = yaw
                    };

                    foreach (Player p in arena.Players)
                    {
                        p._client.sendReliable(tranqExplosion);
                    }
                }
            }

            // Check if the weapon is Freedom AR
            // if (usedWep.name == "Freedom AR")
            // {
            //     // Get the current shot count for this player
            //     if (!_freedomARShotCount.ContainsKey(from))
            //     {
            //         _freedomARShotCount[from] = 0;
            //     }

            //     int shotCount = _freedomARShotCount[from];
            //     int[] weaponIds = new int[] { 3017, 3013, 1162 };

            //     // Update shot count
            //     _freedomARShotCount[from] = (shotCount + 1) % 3;

            //     // Get the new weapon projectile
            //     ItemInfo.Projectile newWeapon = (ItemInfo.Projectile)AssetManager.Manager.getItemByID(weaponIds[shotCount]);
            //     if (newWeapon != null)
            //     {
            //         // Keep original weapon and fire both
            //         byte yaw = from._state.yaw;
                    
            //         // Route both explosions to all players
            //         foreach (Player p in arena.Players)
            //         {
            //             // Fire original weapon
            //             SC_Projectile originalExplosion = new SC_Projectile
            //             {
            //                 projectileID = (short)usedWep.id,
            //                 playerID = (ushort)from._id,
            //                 posX = posX,
            //                 posY = posY,
            //                 posZ = posZ,
            //                 yaw = yaw
            //             };
            //             p._client.sendReliable(originalExplosion);

            //             // Fire additional weapon
            //             SC_Projectile additionalExplosion = new SC_Projectile
            //             {
            //                 projectileID = (short)newWeapon.id,
            //                 playerID = (ushort)from._id,
            //                 posX = posX,
            //                 posY = posY,
            //                 posZ = posZ,
            //                 yaw = yaw
            //             };
            //             p._client.sendReliable(additionalExplosion);
            //         }
            //     }
            // }
            if (gameState != GameState.ActiveGame)
            { return true; }

                // Explosion radius in pixels
                double radius = usedWep.explosiveDamageRadius;

                // Adjust positions for scaling (from 1/1000th of a pixel to pixels)
                double explosionPosX = posX;
                double explosionPosY = posY;

                // Get all vehicles (including turrets) within the explosion radius
                List<Vehicle> vehiclesInRange = arena.getVehiclesInRange(posX, posY, (int)(radius));

                foreach (Vehicle v in vehiclesInRange)
                {
                    // Skip sentries
                    if (v._type.Id == 402)
                        continue;

                    // Check if the vehicle is a turret (Computer)
                    Computer turret = v as Computer;
                    if (turret != null)
                    {
                        // Adjust turret positions for scaling
                        double turretPosX = turret._state.positionX;
                        double turretPosY = turret._state.positionY;

                        // Calculate distance from the explosion center to the edge of the turret
                        // Assuming turret's physical radius is 8 (as per the context)
                        double turretRadius = 8; // Turret's physical radius in pixels
                        double distance = Math.Sqrt(Math.Pow(explosionPosX - turretPosX, 2) + Math.Pow(explosionPosY - turretPosY, 2)) - turretRadius;

                        // Skip if out of radius
                        if (distance > radius)
                            continue;
                        // Adjust damage based on turret type before calculating damage
                        double damageMultiplier = 1.0;
                        if (turret._type.Name == "Auto Turret-Rocket")
                            damageMultiplier = 0.6; // 40% reduction
                        else if (turret._type.Name == "Auto Turret-MG")
                            damageMultiplier = 0.8; // 20% reduction
                        else if (turret._type.Name == "Auto Turret-Plasma")
                            damageMultiplier = 0.9; // 10% reduction

                        // Calculate the damage based on weapon properties and distance, applying the damage multiplier
                        int damage = CalculateExplosionDamage(usedWep, distance, damageMultiplier);

                        // Ensure damage is not negative
                        if (damage < 0)
                            damage = 0;
                        // Always round up to the nearest whole number
                        damage = (int)Math.Ceiling((double)damage / 1000.0);

                        if (damage <= 0)
                            continue; // No damage dealt

                        // Update turret stats
                        string teamName = turret._team != null ? turret._team._name : "Neutral";
                        string turretType = turret._type.Name;
                        string key = teamName + ":" + turretType;

                        TurretData data;
                        if (!_turretStats.TryGetValue(key, out data))
                        {
                            data = new TurretData(teamName, turretType);
                            _turretStats.Add(key, data);
                        }

                        // Update DamageReceived
                        if (data.DamageReceived.ContainsKey(from._id))
                            data.DamageReceived[from._id] += damage;
                        else
                            data.DamageReceived[from._id] = damage;

                        // Optionally, send a message or log the damage
                        //arena.sendArenaMessage(string.Format("Player {0} dealt {1} damage to turret {2}.", from._alias, damage, turretType), -1);
                    }
                }

            if (killStreaks.ContainsKey(from._alias))
            {
                if (explosives.ContainsKey(usedWep.name))
                    UpdateWeapon(from, usedWep, explosives[usedWep.name]);
            }
            return true;
        }

        /// <summary>
        /// Triggered when one player has killed another
        /// </summary>
        [Scripts.Event("Player.PlayerKill")]
        public bool playerPlayerKill(Player victim, Player killer)
        {
            if (gameState != GameState.ActiveGame)
            {
                return true;
            }

            UpdateKiller(killer);

            if (killStreaks.ContainsKey(victim._alias))
            {
                long wepTick = killStreaks[victim._alias].lastUsedWepTick;

                if (wepTick != -1)
                {
                    UpdateWeaponKill(killer);
                }
            }

            // TODO: Remove these unnecessary null checks - killer/victim must be defined objects here.
            if (killer != null && victim != null && victim._bounty >= 300)
            {
                arena.sendArenaMessage(String.Format("{0} has ended {1}'s bounty.", killer._alias, victim._alias), 5);
            }

            bool bVictimCarrier = arena._flags.Values.Any(fs => fs.carrier == victim);
            bool bKillerCarrier = arena._flags.Values.Any(fs => fs.carrier == killer);

            if (bVictimCarrier)
            {
                ctfPlayerProxy.player = killer;
                ctfPlayerProxy.CarrierKills++;
                ctfPlayerProxy.player = null;
            }

            if (bKillerCarrier)
            {
                ctfPlayerProxy.player = killer;
                ctfPlayerProxy.CarryKills++;
                ctfPlayerProxy.player = null;
            }

            return true;
        }

        /// <summary>
        /// Triggered when a player attempts to repair a vehicle or player
        /// </summary>
        [Scripts.Event("Player.Repair")]
        public bool playerRepair(Player player, ItemInfo.RepairItem item, UInt16 targetVehicle, short posX, short posY)
        {
            if (isSD) // Are we in sudden death - reduced healing if so
            {
                // Only apply heal reduction if not all flags are captured by one team
                if (!AreAllFlagsCapturedByOneTeam())
                {
                    // Determine which reduced item to use
                    string reducedItemName = null;

                    // Determine if the item is the Engineer Repair Kit
                    if (item.name == "Engineer Repair Kit")
                    {
                        // Find the computer vehicle the player is attempting to repair
                        Vehicle vehicleTarget = arena.Vehicles.FirstOrDefault(v => v._id == targetVehicle);

                        // Ensure the target vehicle exists and is a computer-controlled vehicle
                        if (vehicleTarget != null && vehicleTarget is Computer)
                        {
                            // Check if the vehicle's health is already full and if the player has enough energy
                            if (vehicleTarget._state.health >= vehicleTarget._type.Hitpoints)
                            {
                                // Notify the player that the vehicle is already fully repaired
                                //player.sendMessage(0, "The target vehicle is already at full health. Repair not needed.");
                                return false; // Prevent resource consumption and further default repair actions
                            }
                            //player.sendMessage(0, string.Format("The target vehicle health is {1}, with a max of {0}", vehicleTarget._type.Hitpoints, vehicleTarget._state.health));

                            // Apply reduced repair during sudden death
                            float percentage = (float)item.repairPercentage / 100;
                            if (item.repairPercentage > 100)
                                percentage = (float)item.repairPercentage / 1000;

                            // Calculate the repair amount
                            double repairAmount = item.repairAmount;

                            // Apply reduced repair amount based on the overtime state
                            if (isSD && !isSecondOvertime)
                            {
                                repairAmount = 50; // Existing 50% reduction
                            }
                            else if (isSecondOvertime)
                            {
                                repairAmount = 40; // Further reduce by 75%
                            }

                            // Adjust the vehicle's health with the calculated repair amount
                            vehicleTarget._state.health = (short)Math.Min(
                                vehicleTarget._type.Hitpoints,
                                (int)(vehicleTarget._state.health + (percentage * vehicleTarget._state.health) + repairAmount));

                            // Mark the vehicle for a state update if it's a computer-controlled vehicle
                            (vehicleTarget as Computer)._sendUpdate = true;

                            // Play the repair sound using Player_RouteItemUsed
                            Helpers.Player_RouteItemUsed(vehicleTarget._inhabitant ?? player, player, targetVehicle, 
                                (short)item.id, player._state.positionX, player._state.positionY, (byte)player._state.yaw);

                            //Reload the players weapon
                            // Send a reload packet to trigger the reload effect
                            SC_ItemReload reloadPacket = new SC_ItemReload
                            {
                                itemID = (short)item.id
                            };
                            player._client.sendReliable(reloadPacket);  // Sending reload packet to trigger reload on the client

                            // Notify the player
                            player.sendMessage(0, string.Format("You repaired {0} for {1} health (reduced in Overtime).",
                                vehicleTarget._type.Name, repairAmount));

                            // remove energy - commenting out as no longer needed
                            // player.inventoryModify(false, AssetManager.Manager.getItemByName("EnergySubtract"), 100);
                            player.syncState();

                            // Prevent further default repair actions
                            return false;
                        }
                        else
                        {
                            // Target vehicle is not valid
                            player.sendMessage(-1, "You can only repair computer-controlled vehicles with this item.");
                            return false;
                        }
                    }

                    if (item.name == "Medikit")
                    {
                        reducedItemName = isSecondOvertime ? "Medikit-R2" : "Medikit-R"; // Use the reduced version of Medikit
                    }
                    else if (item.name == "Deluxe Medikit")
                    {
                        reducedItemName = isSecondOvertime ? "Deluxe Medikit-R2" : "Deluxe Medikit-R"; // Use the reduced version of Deluxe Medikit
                    }

                    if (reducedItemName != null)
                    {
                        // Load the reduced healing item
                        ItemInfo.RepairItem reducedItem = AssetManager.Manager.getItemByName(reducedItemName) as ItemInfo.RepairItem;

                        if (reducedItem != null)
                        {
                            // Get the repair range of the item, using the absolute value
                            int repairRange = Math.Abs((int)reducedItem.repairDistance);
                            // double originalRepairAmount = reducedItem.repairAmount; // Save original amount

                            // // Adjust healing percentage based on the overtime state
                            // if (isSD && !isSecondOvertime)
                            // {
                            //     reducedItem.repairAmount = (int)(reducedItem.repairAmount * 0.5); // 50% reduction
                            // }
                            // else if (isSecondOvertime)
                            // {
                            //     reducedItem.repairAmount = (int)(reducedItem.repairAmount * 0.25); // 75% reduction
                            // }

                            // Heal the player using the medikit
                            //player.heal(reducedItem, player, posX, posY);

                            // remove energy - commenting out no longer needed
                            //player.inventoryModify(false, AssetManager.Manager.getItemByName("EnergySubtract"), 100);

                            //Reload the players weapon
                            // Send a reload packet to trigger the reload effect
                            SC_ItemReload reloadPacket = new SC_ItemReload
                            {
                                itemID = (short)item.id
                            };
                            player._client.sendReliable(reloadPacket);  // Sending reload packet to trigger reload on the client

                            Helpers.Player_RouteItemUsed(player, player, player._id, (short)reducedItem.id, posX, posY, (byte)player._state.yaw);
                            player.syncState();

                            int teammatesHealed = 0;

                            // Heal nearby teammates
                            foreach (Player p in arena.Players)
                            {
                                if (p != player && p._team == player._team)
                                {
                                    double distance = Helpers.distanceTo(p._state.positionX, p._state.positionY, posX, posY);
                                    
                                    // Log distance for debugging
                                    // Log.write(TLog.Normal, "Distance between {0} and {1}: {2}", player._alias, p._alias, distance);

                                    if (distance <= repairRange)
                                    {
                                        p.heal(reducedItem, player, posX, posY);
                                        teammatesHealed++;
                                        //Log.write(TLog.Normal, "Healed player {0}", p._alias);
                                    }
                                }
                            }

                            // Send a message confirming the reduced healing
                            player.sendMessage(0, string.Format("Applied reduced healing in Overtime using {0}. Healing amount: {1}. Teammates healed: {2}", 
                                reducedItem.name, reducedItem.repairAmount, teammatesHealed));

                            player.syncState();
                            return false; // Prevent further default repair actions
                            //return true;
                        }
                        else
                        {
                            player.sendMessage(0, string.Format("Error: Could not find {0} item for healing.", reducedItemName));
                        }
                    }
                }
            }
            return true;
        }

        private int _storedPortalX = 0;
        private int _storedPortalY = 0;
        System.Threading.Timer portalTimer = null;  

        // Class to store weapon stats
        private class WeaponStats
        {
            public int ShotsFired { get; set; }
            public int ShotsLanded { get; set; }
            public double AverageDistance { get; set; }
            public int TotalHits { get; set; }

            public WeaponStats()
            {
                ShotsFired = 0;
                ShotsLanded = 0;
                AverageDistance = 0;
                TotalHits = 0;
            }
        }

        // Dictionary to track detailed weapon stats per player
        private Dictionary<Player, Dictionary<int, WeaponStats>> _playerWeaponStats = new Dictionary<Player, Dictionary<int, WeaponStats>>();
        private bool _playerStatsEnabled = false;

        /// <summary>
        /// Triggered when a player notifies the server of an explosion
        /// </summary>
        [Scripts.Event("Player.DamageEvent")] 
        public bool playerDamageEvent(Player player, ItemInfo.Projectile weapon, short posX, short posY, short posZ)
        {
            if (weapon == null || player == null)
                return false;

            // arena.sendArenaMessage(string.Format("Last explosion stats: Tick={0}, Shooter={1}, Weapon={2}, X={3}, Y={4}, Z={5}", 
            //     _lastExplosionTick, _lastExplosionShooter._alias, _lastExplosionWeaponId, _lastExplosionX, _lastExplosionY, _lastExplosionZ));

            if (_playerStatsEnabled)
            {
                // Use a tick-based threshold to match the last explosion event.
                int currentTick = Environment.TickCount;
                int tickThreshold = 200; // Acceptable difference in milliseconds

                // Check if the last explosion event matches this weapon and is recent
                if (weapon.id == _lastExplosionWeaponId && (currentTick - _lastExplosionTick) <= tickThreshold)
                {
                    // Use the stored shooter while still keeping 'player' as the victim.
                    Player shooter = _lastExplosionShooter;

                    // Update shooter stats for accuracy tracking
                    if (!_playerWeaponStats.ContainsKey(shooter))
                    {
                        _playerWeaponStats[shooter] = new Dictionary<int, WeaponStats>();
                    }
                    if (!_playerWeaponStats[shooter].ContainsKey(weapon.id))
                    {
                        _playerWeaponStats[shooter][weapon.id] = new WeaponStats();
                    }
                    var shooterStats = _playerWeaponStats[shooter][weapon.id];

                    shooterStats.ShotsLanded++;
                    shooterStats.TotalHits++;

                    // Calculate distance from explosion to victim's vehicle
                    if (player._baseVehicle != null)
                    {
                        double distance = CalculateDistance(_lastExplosionX, _lastExplosionY, posX, posY);
                        shooterStats.AverageDistance =
                            ((shooterStats.AverageDistance * (shooterStats.TotalHits - 1)) + distance) / shooterStats.TotalHits;
                    }

                    // Debug message showing all weapon stats for the shooter
                    string statsMessage = string.Format("Weapon Stats for {0}:", shooter._alias);
                    foreach (var weaponStats in _playerWeaponStats[shooter])
                    {
                        ItemInfo weaponInfo = AssetManager.Manager.getItemByID(weaponStats.Key);
                        string weaponName = weaponInfo != null ? weaponInfo.name : string.Format("Weapon {0}", weaponStats.Key);

                        statsMessage += string.Format("\n{0}: Shots={1}, Hits={2}, Avg Distance={3:F2}",
                            weaponName,
                            weaponStats.Value.ShotsFired,
                            weaponStats.Value.ShotsLanded,
                            weaponStats.Value.AverageDistance);
                    }
                    // arena.sendArenaMessage(statsMessage);
                }
            }
                
            if (weapon.id == 1317){
                // Player has entered a portal, use stored coordinates or generate new ones
                if (_storedPortalX == 0 && _storedPortalY == 0) {
                    // First player - generate and store random coordinates
                    Random random = new Random();
                    _storedPortalX = random.Next(0, 1000); 
                    _storedPortalY = random.Next(0, 1000);
                    
                    // Only create a timer if portal coordinates were just generated
                    if (_storedPortalX != 0 && _storedPortalY != 0) {
                        portalTimer = new System.Threading.Timer((state) => {
                            lock(this) {
                                _storedPortalX = 0;
                                _storedPortalY = 0;
                                arena.sendArenaMessage("The portal has closed!");
                            }
                            ((System.Threading.Timer)state).Dispose();
                        }, null, 30000, System.Threading.Timeout.Infinite); // 30000 ms = 30 seconds
                    }
                }

                // Warp player to stored coordinates with small random offset
                Random offsetRandom = new Random();
                int offsetX = offsetRandom.Next(-50, 50);
                int offsetY = offsetRandom.Next(-50, 50);
                player.warp((_storedPortalX * 16) + offsetX, (_storedPortalY * 16) + offsetY);
            }

            // Check if the damage event string is valid
            if (!string.IsNullOrEmpty(weapon.damageEventString))
            {
                // Execute the event, parsing the damage event string
                Logic_Assets.RunEvent(player, weapon.damageEventString);
                return false; // Return false to continue the server-side handling if necessary
            }

            // Default behavior if no event string is specified
            return true; 
        }

        /// <summary>
        /// Triggered when a player has died, by any means
        /// </summary>
        /// <remarks>killer may be null if it wasn't a player kill</remarks>
        [Scripts.Event("Player.Death")]
        public bool playerDeath(Player victim, Player killer, Helpers.KillType killType, CS_VehicleDeath update)
        {
            // Handle Zombie event deaths
            switch (currentEventType)
            {
                case EventType.Zombie:
                    HandleZombieDeath(victim, killer);
                    return false; // Prevent default respawn
                case EventType.SUT:
                    return true;
            }
            
            // if (is5v5){ // Check if medic dies, if true, check if any players left on defense in base coordinates
            //     string victimSkillName = GetPrimarySkillName(victim);
            //     if (victimSkillName.Equals("Field Medic", StringComparison.OrdinalIgnoreCase))
            //     {
            //         // Set base coordinates based on baseUsed
            //         int startX = 0, endX = 0, startY = 0, endY = 0;
            //         switch (baseUsed)                
            //         {
            //             case "D7":
            //                 startX = 255 * 16; endX = 328 * 16;
            //                 startY = 435 * 16; endY = 505 * 16;
            //                 break;
            //             case "F6":
            //                 startX = 375 * 16; endX = 481 * 16;
            //                 startY = 435 * 16; endY = 509 * 16;
            //                 break;
            //             case "F4":
            //                 startX = 367 * 16; endX = 435 * 16;
            //                 startY = 224 * 16; endY = 306 * 16;
            //                 break;
            //             case "A7":
            //                 startX = 255 * 16; endX = 328 * 16;
            //                 startY = 435 * 16; endY = 505 * 16;
            //                 break;
            //             case "A5":
            //                 startX = 4 * 16; endX = 79 * 16;
            //                 startY = 305 * 16; endY = 377 * 16;
            //                 break;
            //             case "B6":
            //                 startX = 128 * 16; endX = 203 * 16;
            //                 startY = 432 * 16; endY = 515 * 16;
            //                 break;
            //             default:
            //                 startX = 0; endX = 0;
            //                 startY = 0; endY = 0;
            //                 break;
            //         }

            //         // Check if any players from victim's team are still in base coordinates
            //         bool playersInBase = false;
            //         foreach (Player p in arena.Players)
            //         {
            //             if (p._team == victim._team && p != victim)
            //             {
            //                 if (p._state.positionX >= startX && p._state.positionX <= endX &&
            //                     p._state.positionY >= startY && p._state.positionY <= endY)
            //                 {
            //                     playersInBase = true;
            //                     break;
            //                 }
            //             }
            //         }

            //         // If no players left in base, set offense as winner
            //         if (!playersInBase)
            //         {
            //             winningTeamOVD = "offense";
            //             arena.sendArenaMessage("Offense wins! No defenders remain in base!");
            //         }
            //     }
            // }

            string victimSkill = GetPrimarySkillName(victim);

            if (currentEventType == EventType.Gladiator)
            {
                // Player is already in the event, just warp them
                victim.resetWarp();
                WarpPlayerToSpawn(victim);

                //Update normally
                killer.Kills++;
                victim.Deaths++;
                return false; // Prevent default team assignment
            }

            // Only proceed if the game is active
            if (gameState != GameState.ActiveGame)
            {
                return true;
            }

            if (killer != null){
                string killerSkill = GetPrimarySkillName(killer);
                if (killerSkill.Equals("Dueler", StringComparison.OrdinalIgnoreCase))
                {
                    killer.inventoryModify(104, 1);
                }
            }

            // Update death statistics
            UpdateDeath(victim, killer);
            int killerHP = killer != null ? killer._state.health : 0;

            // Check if the victim's primary skill is "Dueler"
            if (victimSkill.Equals("Dueler", StringComparison.OrdinalIgnoreCase))
            {
                WarpPlayerToExactLocation(victim, 759, 535);
                // Ensure the duel is in the correct state
                if (duelState == DuelEventState.PoolPhase || duelState == DuelEventState.KnockoutPhase)
                {
                    if (killer != null)
                    {
                        // Ensure the killer is part of the duel participants
                        if (!duelParticipants.Contains(killer))
                        {
                            // Optional: Add the killer to participants or ignore the kill
                            return true;
                        }

                        // Initialize scores if not already done
                        if (!playerScores.ContainsKey(killer))
                            playerScores[killer] = 0;
                        if (!playerScores.ContainsKey(victim))
                            playerScores[victim] = 0;

                        // Update killer's score
                        playerScores[killer]++;

                        string victimMessage = string.Format("You were killed by {0}, who has {1} HP remaining. Score: {2} - {3}", killer._alias, killerHP, playerScores[killer], playerScores[victim]);
                        victim.sendMessage(0, victimMessage);

                        string killerMessage = string.Format("You killed {0}. Score: {1} - {2}", victim._alias, playerScores[killer], playerScores[victim]);
                        killer.sendMessage(0, killerMessage);

                        // Check if the killer has reached the required kills to win
                        if (playerScores[killer] >= 2) // Adjust to 3 if needed
                        {
                            // Killer wins the match
                            victim.sendMessage(0, string.Format("You have lost the duel against {0}.", killer._alias));
                            killer.sendMessage(0, string.Format("You have won the duel against {0}!", victim._alias));

                            // Move loser to spectator
                            victim.spec();

                            // Advance winner to next round
                            AdvanceToNextRound(killer);
                        }
                        else
                        {
                            // Respawn victim at the duel pad
                            var match = currentMatches.FirstOrDefault(m => m.Item1 == victim || m.Item2 == victim);
                            if (match != null)
                            {
                                int padIndex = currentMatches.IndexOf(match);
                                if (padIndex >= 0 && padIndex < duelPads.Count)
                                {
                                    int[] pad = duelPads[padIndex];
                                    victim.warp(pad[0], pad[1]);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Handle cases where the killer is null
                        string message = "You were killed, but the killer's information is unavailable.";
                        victim.sendMessage(0, message);
                    }

                    return false; // Prevent default respawn
                } else {
                    string victimMessage = string.Format("You were killed by {0}, who has {1} HP remaining.", killer._alias, killerHP);
                    victim.sendMessage(0, victimMessage);
                }
            }
            
            return true;
        }

        /// <summary>
        /// Called when the player successfully joins the game
        /// </summary>
        [Scripts.Event("Player.Enter")]
        public void playerEnter(Player player)
        {
            if (!arena._name.Contains("Arena 1"))
                deprizeMinPremades(player, false, false); // Remove items regardless of skill
            //Add them to the list if its not in it
            if (!killStreaks.ContainsKey(player._alias))
            {
                PlayerStreak temp = new PlayerStreak();
                temp.lastKillerCount = 0;
                temp.lastUsedWeap = null;
                temp.lastUsedWepKillCount = 0;
                temp.lastUsedWepTick = -1;
                temp.deathCount = 0;
                temp.killCount = 0;
                killStreaks.Add(player._alias, temp);
            }

            string skillName = GetPrimarySkillName(player);
            if (skillName == "Dueler"){
                ChangePlayerSkill(player, "Infantry");
            }

            // If the Zombie event is active, make the player a zombie
            if (currentEventType == EventType.Zombie)
            {
                // Change the player's skill to the zombie skill
                ChangePlayerSkill(player, "Zombie");
                AssignPlayerToTeam(player, "Zombie", "Zombies", true, true);
                player.sendMessage(0, "You have joined during the Zombie event and are now a zombie!");
            }

            if (currentEventType == EventType.MiniTP){
                if (player._team._name.Contains("Titan"))
                    WarpPlayerToRange(player, 654, 654, 518, 518);
                else if (player._team._name.Contains("Collective")) 
                    WarpPlayerToRange(player, 648, 648, 565, 565);
                return;
            }
        }

        /// <summary>
        /// Called when a player enters the arena
        /// </summary>
        [Scripts.Event("Player.EnterArena")]
        public void playerEnterArena(Player player)
        {
            // Check if the player belongs to any champion group
            Dictionary<string, int> conversions = GetChampionConversionsForPlayer(player);
            if (conversions != null)
            {
                // Enable champ item conversion by default for champion players.
                _autoConvertEnabled[player] = true;

                // Greet the champion and provide instructions
                player.sendMessage(0, "&Welcome, Champion " + player._alias + "!");
                player.sendMessage(0, "*You are eligible for champion item conversions. Champion items can be disabled by a mod just as an FYI.");
                player.sendMessage(0, "*Type ?champ to toggle automatic champion item conversion on/off. They are enabled by default.");
                player.sendMessage(0, "*Use ?b, ?bw, or ?bwd itemName for this to take effect. (?buy will not work!)");
                player.sendMessage(0, "*The following champion items are available to you:");

                // List the available champion conversions based on which group they belong to
                if (s2Champs.Any(alias => alias.Equals(player._alias, StringComparison.OrdinalIgnoreCase)))
                {
                    player.sendMessage(0, "$Season 2 Champion Items:");
                    foreach (var kvp in s2Conversions)
                    {
                        // Get the champion item name from the asset manager if available
                        ItemInfo champItem = AssetManager.Manager.getItemByID(kvp.Value);
                        string champItemName = champItem != null ? champItem.name : "Unknown";
                        player.sendMessage(0, string.Format("~{0}", champItemName));
                    }
                }
                else if (ovdl1Champs.Any(alias => alias.Equals(player._alias, StringComparison.OrdinalIgnoreCase)))
                {
                    player.sendMessage(0, "$OVDL1 Champion Items:");
                    foreach (var kvp in ovdl1Conversions)
                    {
                        ItemInfo champItem = AssetManager.Manager.getItemByID(kvp.Value);
                        string champItemName = champItem != null ? champItem.name : "Unknown";
                        player.sendMessage(0, string.Format("~{0}", champItemName));
                    }
                }
            }

            if (!arena._name.Contains("Arena 1"))
                deprizeMinPremades(player, false, false); // Remove items regardless of skill
            if (isOVD)
            {
                player.sendMessage(3, "&Welcome to Offense vs Defense. Please type ?playing or ?p if you wish to play!");

                if (player.PermissionLevel > 0 || player._permissionTemp > 0)
                {
                    player.sendMessage(0, "#If you are hosting OvDs, please use *endgame to spec all. This will automatically trigger the playing/not playing scripting");
                }
            }
            //Add them to the list if its not in it
            if (!killStreaks.ContainsKey(player._alias))
            {
                PlayerStreak temp = new PlayerStreak();
                temp.lastKillerCount = 0;
                temp.lastUsedWeap = null;
                temp.lastUsedWepKillCount = 0;
                temp.lastUsedWepTick = -1;
                killStreaks.Add(player._alias, temp);
            }
        }

        /// <summary>
        /// Triggered when a player wants to unspec and join the game
        /// </summary>
        [Scripts.Event("Player.JoinGame")]
        public bool playerJoinGame(Player player)
        {
            if (!arena._name.Contains("Arena 1"))
                deprizeMinPremades(player, false, false); // Remove items regardless of skill
            
            //Add them to the list if its not in it
            if (!killStreaks.ContainsKey(player._alias))
            {
                PlayerStreak temp = new PlayerStreak();
                temp.lastKillerCount = 0;
                temp.lastUsedWeap = null;
                temp.lastUsedWepKillCount = 0;
                temp.lastUsedWepTick = -1;
                killStreaks.Add(player._alias, temp);
            }

            if (currentEventType == EventType.SUT)
            {
                for (int i = 2; i <= 33; i++)
                {
                    string teamName = CFG.teams[i].name;
                    Team team = player._arena.getTeamByName(teamName);

                    // Check if the team exists and is empty
                    if (team != null && team.ActivePlayerCount == 0)
                    {
                        AssignPlayerToTeam(player, GetPrimarySkillName(player), team._name, true, false);
                        WarpPlayerToRange(player, 679, 686, 608, 618, 1000);
                        break;
                    }                
                    return true;
                }
            }


            if (currentEventType == EventType.Gladiator && gladiatorPlayers != null)
            {
                JoinGladiatorEvent(player);
            }

            if (currentEventType == EventType.CTFX)
            {
                var teamA = arena.getTeamByName(currentMap.TeamNames[0]);
                var teamB = arena.getTeamByName(currentMap.TeamNames[1]);

                if (player.IsSpectator)
                {
                    if (teamA.ActivePlayerCount < teamB.ActivePlayerCount)
                    {
                        player.unspec(teamA);
                    }
                    else
                    {
                        player.unspec(teamB);
                    }
                }

                player._lastMovement = Environment.TickCount;
                player._maxTimeCalled = false;
                WarpPlayerToRange(player, 985, 997, 1117, 1129);

                return false;
            }

            if (currentEventType == EventType.MiniTP)
            {
                var teamA = arena.getTeamByName(currentMap.TeamNames[0]);
                var teamB = arena.getTeamByName(currentMap.TeamNames[1]);

                if (player.IsSpectator)
                {
                    if (teamA.ActivePlayerCount < teamB.ActivePlayerCount)
                    {
                        player.unspec(teamA);
                    }
                    else
                    {
                        player.unspec(teamB);
                    }
                }

                player._lastMovement = Environment.TickCount;
                player._maxTimeCalled = false;
                if (player._team._name.Contains("Titan"))
                    WarpPlayerToRange(player, 654, 654, 518, 518);
                else if (player._team._name.Contains("Collective")) 
                    WarpPlayerToRange(player, 648, 648, 565, 565);

                return false;
            }

            // This is what allows Arena 1 to default to Collective/Titan Militia when initially unspeccing
            if (!isOVD)
            {
                // Get the player's primary skill name
                string playerSkill = GetPrimarySkillName(player);

                // Determine the team for the player if they are a spectator
                Team targetTeam = null;

                // Check if the player is a spectator and has the skill "Dueler"
                if (player.IsSpectator && (playerSkill == "Dueler" || currentEventType == EventType.Gladiator))
                {
                    // Iterate through teams to find an appropriate one (teams 2 to 33)
                    for (int i = 2; i <= 33; i++)
                    {
                        string teamName = CFG.teams[i].name;
                        Team team = player._arena.getTeamByName(teamName);

                        // Check if the team exists and is empty
                        if (team != null  && team.ActivePlayerCount == 0)
                        {
                            targetTeam = team; // Store the team for unspec later
                            break;
                        }
                    }

                    // If a suitable team is found, proceed with duel or gladiator logic
                    if (targetTeam != null)
                    {
                        if (currentEventType == EventType.Gladiator)
                        {
                            // Gladiator event specific logic
                            WarpPlayerToSpawn(player);
                        }
                        else
                        {
                            // Call the Duel logic directly
                            Duel(player);
                        }
                    }
                    else
                    {
                        player.sendMessage(0, "No available teams for the duel or gladiator event.");
                    }
                }

                // Obtain spawn coordinates from the current map.
                var teamA = arena.getTeamByName(currentMap.TeamNames[0]);
                var teamB = arena.getTeamByName(currentMap.TeamNames[1]);

                // If they are not a spectator or they didn't need to duel, unspec them
                if (!player.IsSpectator || targetTeam == null)
                {
                    if (teamA.ActivePlayerCount < teamB.ActivePlayerCount)
                    {
                        player.unspec(teamA);
                    }
                    else
                    {
                        player.unspec(teamB);
                    }
                }

                player._lastMovement = Environment.TickCount;
                player._maxTimeCalled = false;

                return false; // Allow other processes to run after joining the game
            }

            return true;
        }

        /// <summary>
        /// Called when a player sends a mod command
        /// </summary>
        [Scripts.Event("Player.ModCommand")]
        public bool playerModCommand(Player player, Player recipient, string command, string payload)
        {
            command = (command.ToLower());

            // if (command.Equals("spec2", StringComparison.OrdinalIgnoreCase))
            // {
            //     // Force recipient to spectator mode first
            //     recipient.spec();

            //     // Send initial message
            //     arena.sendArenaMessage(string.Format("{0} has been spec'd by {1}", recipient._alias, player._alias));

            //     // Start a timer for 2 minutes
            //     System.Timers.Timer specTimer = new System.Timers.Timer(120000);
            //     specTimer.AutoReset = false;
                
            //     string recipientName = recipient._alias;
                
            //     specTimer.Elapsed += (sender, e) =>
            //     {
            //         arena.sendArenaMessage(string.Format("{0}'s spec time has expired", recipientName));
            //         specTimer.Dispose();
            //     };
                
            //     specTimer.Start();
                
            //     return false; // Return false to prevent other handlers from processing
            // }

            // Toggle playerWeaponStats enable/disable
            if (command.Equals("weaponstats", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(payload))
                {
                    _playerStatsEnabled = !_playerStatsEnabled;
                }
                player.sendMessage(0, string.Format("Weapon stats are now {0}", _playerStatsEnabled ? "enabled" : "disabled"));
                return true;
            }

            if (command.Equals("redirect", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(payload))
                {
                    player.sendMessage(-1, "Usage: setredirect <side> <base> (e.g., setredirect titan d8)");
                    return false;
                }
                string[] parts = payload.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    player.sendMessage(-1, "Invalid parameters. Usage: setredirect <side> <base>");
                    return false;
                }
                string side = parts[0].ToLower();
                string baseStr = parts[1].ToLower();
                if (side != "titan" && side != "collective")
                {
                    player.sendMessage(-1, "Invalid side. Use 'titan' or 'collective'.");
                    return false;
                }
                if (baseStr != "d8" && baseStr != "a10" && baseStr != "a8" && baseStr != "f8" && baseStr != "b8")
                {
                    player.sendMessage(-1, "Invalid base. Use one of: d8, a10, a8, f8, b8.");
                    return false;
                }

                redirectSide = side;
                redirectBase = baseStr;
                player.sendMessage(0, string.Format("Redirect settings updated: side = {0}, base = {1}.", redirectSide, redirectBase));
                return false;
            }

            if (command.Equals("autosave", StringComparison.OrdinalIgnoreCase))
            {
                bool enable;
                if (string.IsNullOrEmpty(payload) || !bool.TryParse(payload, out enable))
                {
                    // Toggle current state if no payload or invalid boolean
                    enable = !isAutoSaving;
                }
                ToggleAutoSave(player, enable);
                return true;
            }

            if (command.Equals("savestate", StringComparison.OrdinalIgnoreCase)){
                if (!string.IsNullOrEmpty(payload))
                {
                    SaveState(payload);
                }
                return true;
            }
            
            if (command.Equals("loadstate", StringComparison.OrdinalIgnoreCase)){
                if (!string.IsNullOrEmpty(payload))
                {
                    if (payload.Equals("next", StringComparison.OrdinalIgnoreCase))
                    {
                        LoadNextState(player);
                    }
                    else
                    {
                        LoadState(payload);
                    }
                }
                return true;
            }

            if (command.Equals("turrets", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(payload))
                    {
                        BuildTurretExample(player);
                    }
                    return true;
                }
            
            if (command.Equals("turret", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(payload))
                        {
                            short playerX = player._state.positionX;
                            short playerY = player._state.positionY;
                            BuildTurretAtLocation(player, payload, playerX, playerY);
                        }
                    return true;
                }

            if (command.Equals("champItems", StringComparison.OrdinalIgnoreCase))
                {
                    isChampEnabled = !isChampEnabled;
                    arena.sendArenaMessage(string.Format("Champ items are now {0}", isChampEnabled ? "enabled" : "disabled"));
                    return true;
                }

            if (command.Equals("deprize", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Player p in arena.Players)
                    {
                        deprizeMinPremades(p, false, true);
                    }
                    return true;
                }

            if (command.Equals("arenaprofile", StringComparison.OrdinalIgnoreCase))
            {
                CheckArenaItemProfile(player);
                return true;
            }

            if (command.Equals("switchTest", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if payload is provided
                    if (string.IsNullOrEmpty(payload))
                    {
                        player.sendMessage(0, "Usage: *switchtest true|false");
                        return true; // Command handled
                    }

                    // Try to parse the payload into a boolean
                    bool isOpen;
                    if (bool.TryParse(payload, out isOpen))
                    {
                        EmulateSwitch(player, isOpen);
                    }
                    else
                    {
                        player.sendMessage(0, "Invalid parameter. Use true or false.");
                    }
                    return true; // Command handled
                }

            if (command.Equals("duel", StringComparison.OrdinalIgnoreCase))
            {
                switch (payload.ToLower())
                {
                    case "start":
                        if (duelState == DuelEventState.Inactive)
                        {
                            StartDuelEvent(player);
                            //player.sendMessage(0, "Duel tournament has started! Players can now sign up using ?duel.");
                        }
                        else
                        {
                            EndDuelEvent();
                            player.sendMessage(0, "Duel tournament has been stopped.");
                        }
                        break;
                    case "off":
                    case "end":
                        if (duelState != DuelEventState.Inactive)
                        {
                            EndDuelEvent();
                            player.sendMessage(0, "Duel tournament has been stopped.");
                        }
                        else
                        {
                            player.sendMessage(0, "No duel tournament is active.");
                        }
                        break;
                    case "info":
                        player.sendMessage(0, "Duel tournament pairs players in a best-of-three format. Sign up with ?duel.");
                        break;
                    default:
                        player.sendMessage(0, "Invalid command. Use *duel start, *duel end, or *duel info.");
                        break;
                }
                return true;
            }

            if (command.Equals("event", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(payload))
                {
                    // Display the list of possible events if no payload is provided
                    player.sendMessage(0, string.Format("Available events: {0}", string.Join(", ", Enum.GetNames(typeof(EventType)))));
                    return true;
                }

                // Check if the input payload matches an event type
                EventType eventType;
                if (Enum.TryParse<EventType>(payload, true, out eventType))
                {
                    // If an event is already running, stop it
                    if (currentEventType != default(EventType))
                    {
                        EndEvent();
                        player.sendMessage(0, string.Format("Event '{0}' has been stopped.", currentEventType));
                    }
                    else
                    {
                        // Start the new event
                        StartEvent(eventType);
                        player.sendMessage(0, string.Format("Event '{0}' has been started.", eventType));
                    }
                    return true;
                }
                else
                {
                    // Inform the player if the provided event type is invalid
                    player.sendMessage(-1, string.Format("Invalid event type. Available events: {0}", string.Join(", ", Enum.GetNames(typeof(EventType)))));
                }
            }

           if (command.Equals("base"))
            {
                string[] args = payload.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                // Canonical valid sides
                string[] validSides = { "Titan", "Collective" };
                Dictionary<string, int[]> locations = new Dictionary<string, int[]>()
                {
                    { "d8", new int[] { 275, 601 } },
                    { "a10", new int[] { 32, 696 } },
                    { "a8", new int[] { 28, 611 } },
                    { "f8", new int[] { 421, 569 } }
                };

                if (args.Length == 2)
                {
                    string inputSide = args[0].Trim();
                    string inputLocation = args[1].Trim();

                    // Use canonical side casing for SpawnVehicle
                    string side = validSides.FirstOrDefault(s => s.Equals(inputSide));
                    if (side == null)
                    {
                        player.sendMessage(-1, string.Format("Invalid side: {0}", inputSide));
                        player.sendMessage(-1, string.Format("Valid sides: {0}", string.Join(", ", validSides)));
                        return false;
                    }

                    if (!locations.ContainsKey(inputLocation))
                    {
                        player.sendMessage(-1, string.Format("Invalid location: {0}", inputLocation));
                        player.sendMessage(-1, string.Format("Valid locations: {0}", string.Join(", ", locations.Keys)));
                        return false;
                    }

                    SpawnVehicle(side, inputLocation);
                }
                else
                {
                    player.sendMessage(-1, "Usage: *base <side> <location>");
                    player.sendMessage(-1, string.Format("Valid sides: {0}", string.Join(", ", validSides)));
                    player.sendMessage(-1, string.Format("Valid locations: {0}", string.Join(", ", locations.Keys)));
                }
            }

            if (command.Equals("min", StringComparison.OrdinalIgnoreCase))
            {
                CountItemsOnSpecificTerrain(arena);
            }

            if (command.Equals("privateteams", StringComparison.OrdinalIgnoreCase) || command.Equals("pt", StringComparison.OrdinalIgnoreCase))
            {
                // Toggle the allowPrivateTeams flag
                allowPrivateTeams = !allowPrivateTeams;

                // Notify the moderator and players of the change
                string status = allowPrivateTeams ? "enabled" : "disabled";
                player.sendMessage(0, string.Format("Private teams have been {0} for this arena.", status));
                arena.sendArenaMessage(string.Format("Private teams are now {0}. Use ?t teamName:password to join a private team.", status));
                return true;
            }

            // Modify the command handler for '*mix'
            if (command.Equals("mix", StringComparison.OrdinalIgnoreCase))
            {
                if (player.PermissionLevelLocal < Data.PlayerPermission.ArenaMod)
                    return false;

                if (!string.IsNullOrEmpty(payload))
                {
                    if (payload.Equals("help", StringComparison.OrdinalIgnoreCase))
                    {
                        // Display help message
                        player.sendMessage(0, "Usage: *mix [auto|end|off|help]");
                        player.sendMessage(0, "*mix: Starts or toggles the mix process.");
                        player.sendMessage(0, "*mix auto: Automatically assigns roles based on pre-selected skills.");
                        player.sendMessage(0, "*mix end/off: Ends the mix process.");
                        return true;
                    }
                    else if (payload.Equals("end", StringComparison.OrdinalIgnoreCase) || payload.Equals("off", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isMixActive)
                        {
                            isMixActive = false;
                            mixGameActive = true;
                            player.sendMessage(0, "Mix process has been ended.");
                            // Additional cleanup if necessary
                        }
                        else
                        {
                            player.sendMessage(0, "Mix process is not active.");
                        }
                        return true;
                    }
                    else if (payload.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        player.sendMessage(0, "Mix auto initiated");
                        StartMixProcess(); // Start the mix process
                        AssignPlayersBasedOnSkills(); // Automatically assign roles based on pre-selected skill
                        return true;
                    }
                    else
                    {
                        player.sendMessage(0, "Invalid option. Type '*mix help' for usage information.");
                        return true;
                    }
                }
                else
                {
                    // No payload provided; toggle the mix process
                    if (isMixActive)
                    {
                        isMixActive = false;
                        player.sendMessage(0, "Mix process has been ended.");
                        // Additional cleanup if necessary
                    }
                    else
                    {
                        player.sendMessage(0, "Mix initiated");
                        StartMixProcess();
                    }
                    return true;
                }
            }

            if (command.Equals("fireworks"))
            {
                LaunchFireworks(player);
            }

            if (command.Equals("setup"))
            {
                if (player.PermissionLevelLocal < Data.PlayerPermission.ArenaMod)
                    return false;

                if (!bases.ContainsKey(payload.ToUpper()))
                {
                    player.sendMessage(-1, "That base is not recognized, Options are: ");
                    foreach (string key in bases.Keys)
                        player.sendMessage(0, key);

                    return true;
                }

                is5v5 = true;
                Base defense = bases[payload.ToUpper()];

                arena.itemSpawn(arena._server._assets.getItemByName("Fuel Canister"), 600, defense.x, defense.y, 0, null);
                arena.itemSpawn(arena._server._assets.getItemByName("Ammo MG"), 1250, defense.x, defense.y, 0, null);
                arena.itemSpawn(arena._server._assets.getItemByName("Light HE"), 250, defense.x, defense.y, 0, null);
                arena.itemSpawn(arena._server._assets.getItemByName("Heavy HE"), 100, defense.x, defense.y, 0, null);
                arena.itemSpawn(arena._server._assets.getItemByName("Ammo Pistol"), 2500, defense.x, defense.y, 0, null);

                arena.itemSpawn(arena._server._assets.getItemByName("AP Mine"), 25, defense.x, defense.y, 0, null);
                arena.itemSpawn(arena._server._assets.getItemByName("Plasma Mine"), 25, defense.x, defense.y, 0, null);
                arena.itemSpawn(arena._server._assets.getItemByName("Bullet Mine"), 100, defense.x, defense.y, 0, null);

                arena.itemSpawn(arena._server._assets.getItemByName("rocket"), 200, defense.x, defense.y, 0, null);
                arena.itemSpawn(arena._server._assets.getItemByName("tranq"), 50, defense.x, defense.y, 0, null);

                arena.itemSpawn(arena._server._assets.getItemByName("sentry"), 5, defense.x, defense.y, 0, null);

                arena.itemSpawn(arena._server._assets.getItemByID(2005), 150, defense.x, defense.y, 100, null);
                arena.itemSpawn(arena._server._assets.getItemByID(2009), 150, defense.x, defense.y, 100, null);


                

                //arena.itemSpawn(arena._server._assets.getItemByID(23), 2, defense.x, defense.y, 100, null);

                //arena.itemSpawn(arena._server._assets.getItemByID(10), 2, defense.x, defense.y, 100, null);
                //arena.itemSpawn(arena._server._assets.getItemByID(11), 1, defense.x, defense.y, 100, null);
                //arena.itemSpawn(arena._server._assets.getItemByID(9), 1, defense.x, defense.y, 100, null);
                Arena.FlagState flag = arena.getFlag("Bridge3");

                flag.posX = defense.flagX;
                flag.posY = defense.flagY;
                flag.oldPosX = defense.flagX;
                flag.oldPosY = defense.flagY;

                // Set flag ownership based on base location
                string baseKey = payload.ToUpper();
                // Titan Bases
                if (baseKey == "D7" || baseKey == "F6" || baseKey == "F5")
                {
                    // Find active team containing " T" and assign flag
                    flag.team = arena.Teams.FirstOrDefault(t => t._name.Contains(" T") && t.ActivePlayerCount > 0);
                }
                // Collective Bases
                else if (baseKey == "A7" || baseKey == "A5" || baseKey == "B6" || baseKey == "H4")
                {
                    // Find active team containing " C" and assign flag 
                    flag.team = arena.Teams.FirstOrDefault(t => t._name.Contains(" C") && t.ActivePlayerCount > 0);
                }
                
                // Move all non-Bridge3 flags to coordinates (25,25)
                Arena.FlagState flag1 = arena.getFlag("Bridge1");
                Arena.FlagState flag2 = arena.getFlag("Hill201"); 
                Arena.FlagState flag3 = arena.getFlag("Hill86");
                Arena.FlagState flag4 = arena.getFlag("Bridge2");

                flag1.posX = 50 * 16;
                flag1.posY = 30 * 16;
                Helpers.Object_Flags(arena.Players, flag1);

                flag2.posX = 50 * 16;
                flag2.posY = 30 * 16;
                Helpers.Object_Flags(arena.Players, flag2);

                flag3.posX = 50 * 16;
                flag3.posY = 30 * 16;
                Helpers.Object_Flags(arena.Players, flag3);

                flag4.posX = 50 * 16;
                flag4.posY = 30 * 16;
                Helpers.Object_Flags(arena.Players, flag4);

                Helpers.Object_Flags(arena.Players, flag);
                //Store the base used in a global variable
                baseUsed = payload.ToUpper();
                arena.sendArenaMessage(String.Format("&Minerals, flag, and auto-kits dropped at {0}", payload.ToUpper()));
                return true;
            }

            // Trigger command overtime or ot with optional minutes parameter
            if (command.Equals("overtime") || command.Equals("ot"))
            {
                if (player.PermissionLevelLocal < Data.PlayerPermission.ArenaMod){
                    player.sendMessage(-1, "permissions lacking, cannot trigger overtime.");
                    return false;
                }

                if (!string.IsNullOrEmpty(payload))
                {
                    int minutes;
                    if (int.TryParse(payload, out minutes))
                    {
                        // Calculate the current game time in milliseconds
                        int currentTimeInMilliseconds = Environment.TickCount - arena._tickGameStarted;
                        
                        // Calculate the desired overtime start time in milliseconds
                        int overtimeInMilliseconds = minutes * 60000;
                        
                        // Schedule overtime to start at the specified minute mark from the game start
                        overtimeStart = arena._tickGameStarted + overtimeInMilliseconds;
                        //overtimeStart = _tickGameStarted + overtimeInMilliseconds;

                        player.sendMessage(0, "Overtime scheduled to start at " + minutes + " minutes.");
                    }
                    else
                    {
                        player.sendMessage(-1, "Invalid minutes parameter. Please enter a valid number.");
                        return false;
                    }
                }

                else
                {
                // Toggle overtime mode
                        if (!isSD)
                        {
                            // Start first overtime immediately
                            isSD = true;
                            arena.sendArenaMessage("&Overtime triggered. 50% Reduced healing on both Medikits. Engineer Repairs reduced to 50 hp.", 30);

                            // Record the start time of the first overtime
                            overtimeStart = Environment.TickCount;

                            // Schedule the second overtime
                            //secondOvertimeStart = overtimeStart + 10000; // 10 seconds for testing
                            secondOvertimeStart = overtimeStart + (15 * 60000); // 15 minutes

                            player.sendMessage(0, "Overtime started immediately.");
                        }
                        else
                        {
                            // Turn off overtime modes
                            isSD = false;
                            isSecondOvertime = false;
                            overtimeStart = 0;
                            secondOvertimeStart = 0;
                            arena.sendArenaMessage("&Overtime mode disabled, healing/repair items restored to normal.");
                            player.sendMessage(0, "Overtime mode disabled.");
                        }
                }
                return true;
            }

            if (command.Equals("sd"))
            {
                if (player.PermissionLevelLocal < Data.PlayerPermission.ArenaMod)
                    return false;
                
                arena.flagReset();
                //SpawnMapFlag();
                //Arena.FlagState flag = arena.getFlag("Hill201");
                // Get the "sdFlag" and activate it
                Arena.FlagState flag = arena.getFlag("sdFlag");

                if (flag == null)
                {
                    player.sendMessage(-1, "Sudden death flag not found.");
                    return false;
                }

                // Set the flag to active
                flag.bActive = true;

                Random random = new Random();
                int sdrr = random.Next(5); // Generates a random number between 0 and 4 (inclusive)
                switch (sdrr)
                {
                    case 0:
                        flag.posX = (short)(202 * 16);
                        flag.posY = (short)(120 * 16);
                        break;
                    case 1:
                        flag.posX = (short)(202 * 16);
                        flag.posY = (short)(202 * 16);
                        break;
                    case 2:
                        flag.posX = (short)(202 * 16);
                        flag.posY = (short)(286 * 16);
                        break;
                    case 3:
                        flag.posX = (short)(607 * 16);
                        flag.posY = (short)(166 * 16);
                        break;
                    case 4:
                        flag.posX = (short)(747 * 16);
                        flag.posY = (short)(211 * 16);
                        break;
                }

                Helpers.Object_Flags(arena.Players, flag);

				arena.sendArenaMessage("!+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+", 30);
                arena.sendArenaMessage("&|  S U D D E N   D E A T H  |");
				arena.sendArenaMessage("!+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+");
                arena.sendArenaMessage("&|  S U D D E N   D E A T H  |");
				arena.sendArenaMessage("!+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+");
                arena.sendArenaMessage("&|  S U D D E N   D E A T H  |");
                arena.sendArenaMessage("!+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+", 17);

                return true;
            }


            if (command.Equals("healall"))
            {
                if (player.PermissionLevelLocal < Data.PlayerPermission.ArenaMod)
                {
                    return false;
                }

                HealAll();
                return true;
            }

            if (command == "map")
            {
                var mapNames = string.Join(", ", availableMaps.Select(x => x.MapName));

                if (string.IsNullOrWhiteSpace(payload))
                {
                    player.sendMessage(-1, "Available map options are: " + mapNames);
                }
                else
                {
                    var requestedEvent = availableMaps.FirstOrDefault(x => x.MapName == payload);

                    if (requestedEvent != null)
                    {
                        player.sendMessage(-1, "Switching to map " + requestedEvent.MapName);
                        currentMap = requestedEvent;

                        SpawnMapPlayers();
                        SpawnMapFlags();
                    }
                    else
                    {
                        player.sendMessage(-1, "Map with that name not found. Available options are: " + mapNames);
                    }
                }
                
                return true;
            }

            return false;
        }

        #endregion

        #region Updaters

        private void UpdateGameEndFlagStats()
        {
            foreach(var player in arena.PlayersIngame)
            {
                if (player._team == winningTeam)
                {
                    ctfPlayerProxy.player = player;
                    ctfPlayerProxy.GamesWon++;
                    ctfPlayerProxy.player = null;
                }
                else
                {
                    ctfPlayerProxy.player = player;
                    ctfPlayerProxy.GamesLost++;
                    ctfPlayerProxy.player = null;
                }
            }
        }

        /// <summary>
        /// Updates the stats for flag carriers. Not that this is supposed to be
        /// executed once per second.
        /// </summary>
        private void UpdateFlagCarryStats(int nowMs)
        {
            if (nowMs - lastStatsWriteMs < 1000) {
                return;
            }

            lastStatsWriteMs = nowMs;

            var dict = new Dictionary<Player, int>();

            foreach (Arena.FlagState fs in arena._flags.Values)
            {
                if (fs.carrier != null)
                {
                    if (!dict.ContainsKey(fs.carrier))
                    {
                        dict.Add(fs.carrier, 0);
                    }

                    dict[fs.carrier]++;
                }
            }

            foreach(var d in dict)
            {
                ctfPlayerProxy.player = d.Key;
                ctfPlayerProxy.CarryTimeSeconds++; // 1 second.
                ctfPlayerProxy.CarryTimeSecondsPlus += d.Value; // 1 second * number of flags.
                ctfPlayerProxy.player = null;
            }
        }

        private void UpdateCTFTickers()
        {
            List<Player> rankedPlayers = arena.Players.ToList().OrderBy(player => (player.StatsCurrentGame == null ? 0 : player.StatsCurrentGame.deaths)).OrderByDescending(
                player => (player.StatsCurrentGame == null ? 0 : player.StatsCurrentGame.kills)).ToList();
            int idx = 3;
            string format = "";
            foreach (Player p in rankedPlayers)
            {
                if (p.StatsCurrentGame == null)
                { continue; }
                if (idx-- == 0)
                {
                    break;
                }

                switch (idx)
                {
                    case 2:
                        format = string.Format("!1st: {0}(K={1} D={2}) ", p._alias, p.StatsCurrentGame.kills, p.StatsCurrentGame.deaths);
                        break;
                    case 1:
                        format = (format + string.Format("!2nd: {0}(K={1} D={2})", p._alias, p.StatsCurrentGame.kills, p.StatsCurrentGame.deaths));
                        break;
                }
            }
            if (!string.IsNullOrWhiteSpace(format))
            { arena.setTicker(1, 2, 0, format); }

            arena.setTicker(2, 3, 0, delegate (Player p)
            {
                if (p.StatsCurrentGame == null)
                {
                    return "Personal Score: Kills=0 - Deaths=0";
                }
                return string.Format("Personal Score: Kills={0} - Deaths={1}", p.StatsCurrentGame.kills, p.StatsCurrentGame.deaths);
            });
        }

        /// <summary>
        /// Updates our players kill streak timer
        /// </summary>
        private void UpdateKillStreaks()
        {
            foreach (KeyValuePair<string, PlayerStreak> p in killStreaks)
            {
                if (p.Value.lastUsedWepTick == -1)
                    continue;

                if (Environment.TickCount - p.Value.lastUsedWepTick <= 0)
                    ResetWeaponTicker(p.Key);
            }
        }

        /// <summary>
        /// Updates the last killer
        /// </summary>
        private void ResetKiller(Player killer)
        {
            lastKiller = killer;
        }

        /// <summary>
        /// Resets the weapon ticker to default (Time Expired)
        /// </summary>
        private void ResetWeaponTicker(string targetAlias)
        {
            if (killStreaks.ContainsKey(targetAlias))
            {
                killStreaks[targetAlias].lastUsedWeap = null;
                killStreaks[targetAlias].lastUsedWepKillCount = 0;
                killStreaks[targetAlias].lastUsedWepTick = -1;
            }
        }

        /// <summary>
        /// Updates the killer and their kill counter
        /// </summary>
        private void UpdateKiller(Player killer)
        {
            if (killStreaks.ContainsKey(killer._alias))
            {
                killStreaks[killer._alias].lastKillerCount++;
                switch (killStreaks[killer._alias].lastKillerCount)
                {
                    case 6:
                        arena.sendArenaMessage(string.Format("{0} is on fire!", killer._alias), 8);
                        break;
                    case 8:
                        arena.sendArenaMessage(string.Format("Someone kill {0}!", killer._alias), 9);
                        break;
                }
            }
            //Is this first blood?
            if (lastKiller == null)
            {
                //It is, lets make the sound
                arena.sendArenaMessage(string.Format("{0} has drawn first blood.", killer._alias), 7);
            }
            lastKiller = killer;
        }

        /// <summary>
        /// Updates the victim's kill streak and notifies the public
        /// </summary>
        private void UpdateDeath(Player victim, Player killer)
        {
            if (killStreaks.ContainsKey(victim._alias))
            {
                if (killStreaks[victim._alias].lastKillerCount >= 6)
                {
                    arena.sendArenaMessage(string.Format("{0}", killer != null ? killer._alias + " has ended " + victim._alias + "'s kill streak." :
                        victim._alias + "'s kill streak has ended."), 6);
                }
                killStreaks[victim._alias].lastKillerCount = 0;
            }
        }

        /// <summary>
        /// Updates the last fired weapon and its ticker
        /// </summary>
        private void UpdateWeapon(Player from, ItemInfo.Projectile usedWep, int aliveTime)
        {
            if (killStreaks.ContainsKey(from._alias))
            {
                killStreaks[from._alias].lastUsedWeap = usedWep;
                killStreaks[from._alias].lastUsedWepTick = DateTime.Now.AddTicks(aliveTime).Ticks;
            }
        }

        /// <summary>
        /// Updates the last weapon used and kill count then announcing it to the public
        /// </summary>
        private void UpdateWeaponKill(Player from)
        {
            if (killStreaks.ContainsKey(from._alias))
            {
                if (killStreaks[from._alias].lastUsedWeap == null)
                    return;

                killStreaks[from._alias].lastUsedWepKillCount++;
                ItemInfo.Projectile lastUsedWep = killStreaks[from._alias].lastUsedWeap;
                switch (killStreaks[from._alias].lastUsedWepKillCount)
                {
                    case 2:
                        arena.sendArenaMessage(string.Format("{0} just got a double {1} kill.", from._alias, lastUsedWep.name), 17);
                        break;
                    case 3:
                        arena.sendArenaMessage(string.Format("{0} just got a triple {1} kill!", from._alias, lastUsedWep.name), 18);
                        break;
                    case 4:
                        arena.sendArenaMessage(string.Format("A 4 {0} kill by {0}?!?", lastUsedWep.name, from._alias), 19);
                        break;
                    case 5:
                        arena.sendArenaMessage(string.Format("Unbelievable! {0} with the 5 {1} kill?", from._alias, lastUsedWep.name), 20);
                        break;
                }
            }
        }
        #endregion

        private void HealAll()
        {
            foreach (Player p in arena.PlayersIngame)
            {
                p.inventoryModify(104, 1);
            }
            
            arena.sendArenaMessage("&All players have been healed");
        }

        private enum GameState
        {
            Init,
            PreGame,
            ActiveGame,
            PostGame,
            NotEnoughPlayers,
            Transitioning,
        }

        private enum CTFMode
        {
            None,
            Aborted,
            TenSeconds,
            ThirtySeconds,
            SixtySeconds,
            XSeconds,
            GameDone,
        }        
    }
}

public static class ArenaExtensions
{
    /// <summary>
    /// Spawns the given item randomly in the specified area
    /// </summary>
    public static void spawnItemInArea(this Arena arena, ItemInfo item, ushort quantity, short x, short y, short radius)
    {       //Sanity
        if (quantity <= 0)
            return;

        int blockedAttempts = 30;

        short pX;
        short pY;
        while (true)
        {
            pX = x;
            pY = y;
            Helpers.randomPositionInArea(arena, radius, ref pX, ref pY);
            if (arena.getTile(pX, pY).Blocked)
            {
                blockedAttempts--;
                if (blockedAttempts <= 0)
                    //Consider the spawn to be blocked
                    return;
                continue;
            }
            arena.itemSpawn(item, quantity, pX, pY, null);
            break;
        }
    }
}
