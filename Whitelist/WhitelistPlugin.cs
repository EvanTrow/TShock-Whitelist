using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace Whitelist
{
    [ApiVersion(2, 1)]
    public class WhitelistPlugin : TerrariaPlugin
    {
        public override string Author => "EvanTrow";
        public override string Description => "Automatically joins players to a configurable team when they join the server.";
        public override string Name => "Whitelist";
        public override Version Version => new Version(1, 0, 0);

        // The in-memory store of the whitelist
        private static Whitelist whitelist;

        // File path for the whitelist JSON file
        private string WhitelistFilePath => Path.Combine(TShock.SavePath, "whitelist.json");

        public WhitelistPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            // Load the whitelist JSON when the server starts
            LoadWhitelist();

            // Register hooks
            ServerApi.Hooks.GameInitialize.Register(this, OnGameInitialize);
            //TShockAPI.Hooks.PlayerHooks.PlayerPostLogin += OnPlayerPostLogin;

            ServerApi.Hooks.NetGetData.Register(this, OnGetData);

        }


        private void OnGameInitialize(EventArgs args)
        {
            // Register commands
            Commands.ChatCommands.Add(new Command("better.whitelist.manage", WhitelistCmd, "wl")
            {
                HelpText = "Manage the UUID-based whitelist. Subcommands: add, remove, reload, list"
            });
        }

        /// <summary>
        /// This hook is called after a player successfully logs in/authenticates.
        /// Check if the player's TShock account UUID is in the whitelist.
        /// If not in the whitelist, kick them.
        /// </summary>
        private void OnGetData(GetDataEventArgs args)
        {
            // check if the packet sent is the player update packet
            if (args.MsgID == PacketTypes.PlayerUpdate)
            {
                // create a memory stream used to read information from the packet
                using (MemoryStream data = new MemoryStream(args.Msg.readBuffer, args.Index, args.Length))
                {
                    // player variable read from the first byte of the packet
                    TSPlayer player = TShock.Players[data.ReadByte()];
                    if (!IsUserWhitelisted(player.UUID))
                    {
                        if (whitelist.Attempts.Find(u => u.UUID == player.UUID)?.UUID == null)
                        {
                            whitelist.Attempts.Add(new WhitelistUser()
                            {
                                Username = player.Name,
                                UUID = player.UUID
                            });
                            SaveWhitelist();
                        }

                        player.Disconnect($"You are not whitelisted on this server.\n\n{player.Name} ({player.UUID})");

                        string msg = $"Non-Whitelist player attempted to connect: {player.Name} ({player.UUID})";
                        Console.WriteLine(msg);
                        TShock.Utils.Broadcast(msg, Color.Yellow);
                    }
                }
            }
        }

        /// <summary>
        /// Command handler for /wl
        /// </summary>
        private void WhitelistCmd(CommandArgs args)
        {
            var subCmd = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "";

            switch (subCmd)
            {
                case "add":
                    WhitelistAdd(args);
                    break;
                case "remove":
                    WhitelistRemove(args);
                    break;
                case "reload":
                    WhitelistReload(args);
                    break;
                case "list":
                    WhitelistList(args);
                    break;
                default:
                    args.Player.SendInfoMessage("Whitelist commands:");
                    args.Player.SendInfoMessage("/wl add <Username/UUID>");
                    args.Player.SendInfoMessage("/wl remove <Username/UUID>");
                    args.Player.SendInfoMessage("/wl reload");
                    args.Player.SendInfoMessage("/wl list");
                    break;
            }
        }

        private void WhitelistAdd(CommandArgs args)
        {
            string search = args.Message.Split("add ")[1];

            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Usage: /wl add <Username/UUID>");
                return;
            }

            WhitelistUser user = whitelist.Attempts.Concat(whitelist.Users).ToList().Find(x => x.UUID == search || x.Username == search);
            if (string.IsNullOrWhiteSpace(user?.UUID))
            {
                args.Player.SendErrorMessage($"{search} not found.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(whitelist.Users.Find(u => u.UUID == user.UUID)?.UUID))
            {
                args.Player.SendErrorMessage($"{user.Username} is already whitelisted.");
                return;
            }

            whitelist.Users.Add(user);
            whitelist.Attempts.RemoveAll((u) => u.UUID == user.UUID);
            SaveWhitelist();
            args.Player.SendSuccessMessage($"Added {user.Username} to the whitelist.");
        }

        private void WhitelistRemove(CommandArgs args)
        {
            string search = args.Message.Split("remove ")[1];

            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Usage: /wl remove <Username/UUID>");
                return;
            }

            WhitelistUser user = whitelist.Attempts.Concat(whitelist.Users).ToList().Find(x => x.UUID == search || x.Username == search);
            if (string.IsNullOrWhiteSpace(user?.UUID))
            {
                args.Player.SendErrorMessage($"{search} not found.");
                return;
            }

            if (whitelist.Users.Find(u => u.UUID == user.UUID)?.UUID != null)
            {
                args.Player.SendErrorMessage($"{user.Username} is not whitelisted.");
                return;
            }

            whitelist.Users.RemoveAll((u) => u.UUID == user.UUID);
            SaveWhitelist();
            args.Player.SendSuccessMessage($"Removed {user.Username} from the whitelist.");
        }

        private void WhitelistReload(CommandArgs args)
        {
            LoadWhitelist();
            args.Player.SendSuccessMessage("Whitelist reloaded from file.");
        }

        private void WhitelistList(CommandArgs args)
        {
            if (whitelist.Users.Count == 0)
            {
                args.Player.SendInfoMessage("The whitelist is currently empty. ");
                return;
            }

            args.Player.SendInfoMessage("Whitelisted:");
            foreach (var user in whitelist.Users)
            {
                args.Player.SendInfoMessage($"{user.Username} ({user.UUID})");
            }
            args.Player.SendInfoMessage("\nAttempted:");
            foreach (var user in whitelist.Attempts)
            {
                args.Player.SendInfoMessage($"{user.Username} ({user.UUID})");
            }
        }

        /// <summary>
        /// Checks if a UUID is in the whitelist.
        /// </summary>
        private bool IsUserWhitelisted(string UUID)
        {
            return whitelist.Users.Find(u => u.UUID == UUID)?.UUID != null;
        }

        /// <summary>
        /// Loads the whitelist from the JSON file.
        /// If the file doesn't exist or is invalid, it creates an empty whitelist.
        /// </summary>
        private void LoadWhitelist()
        {
            try
            {
                if (!File.Exists(WhitelistFilePath))
                {
                    whitelist = new Whitelist();
                    SaveWhitelist(); // Create a default empty file
                }
                else
                {
                    var json = File.ReadAllText(WhitelistFilePath);
                    whitelist = JsonConvert.DeserializeObject<Whitelist>(json) ?? new Whitelist();
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[Whitelist] Error loading whitelist: {ex.Message}");
                whitelist = new Whitelist();
            }
        }

        /// <summary>
        /// Saves the whitelist to the JSON file.
        /// </summary>
        private void SaveWhitelist()
        {
            try
            {
                var json = JsonConvert.SerializeObject(whitelist, Formatting.Indented);
                File.WriteAllText(WhitelistFilePath, json);
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[Whitelist] Error saving whitelist: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Holds the list of whitelisted UUIDs in memory (and in JSON).
    /// </summary>
    public class Whitelist
    {
        public List<WhitelistUser> Users { get; set; } = new List<WhitelistUser>();
        public List<WhitelistUser> Attempts { get; set; } = new List<WhitelistUser>();
    }

    public class WhitelistUser
    {
        public string Username { get; set; }
        public string UUID { get; set; }
    }
}