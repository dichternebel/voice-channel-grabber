using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Dec.DiscordIPC;
using Dec.DiscordIPC.Commands;
using Dec.DiscordIPC.Events;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using static Dec.DiscordIPC.Entities.Sticker;

namespace VoiceChannelGrabber
{
    class Program
    {
        private static string baseDir { get; set; }

        private static string clientID { get; set; }

        private static string clientSecret { get; set; }

        private static string tokenJsonFile { get; set; }

        private static Config Config {get; set;}

        private static string AccessToken { get; set; }

        private static bool IsDiscordAvailable { get; set; }

        private static bool IsOBSconnected { get; set; }

        private static DiscordIPC client { get; set; }

        private static OBSWebsocket obs { get; set; }

        private static Timer heartbeatTimer;

        static int Main()
        {
            baseDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

            // Initialize serilog logger
            Log.Logger = new LoggerConfiguration()
                 .WriteTo.Console(Serilog.Events.LogEventLevel.Debug, theme: AnsiConsoleTheme.Literate)
                 .WriteTo.File(Path.Combine(baseDir, "logs", $"log-.txt"), Serilog.Events.LogEventLevel.Information, rollingInterval: RollingInterval.Day)
                 .MinimumLevel.Information()
                 .Enrich.FromLogContext()
                 .CreateLogger();

            // Name this thing
            Console.Title = "Discord Voice Channel Grabber for Streamkit-Voice-Overlay in OBS";

            try
            {
                Native.SetQuickEditMode(false);
                MainAsync().Wait();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Logger.Fatal(ex.Message);
                Console.ReadLine();
                return 1;
            }
        }

        static async Task MainAsync()
        {
            var configFile = Path.Combine(baseDir, "config.json");
            tokenJsonFile = Path.Combine(baseDir, "token.json");

            if (File.Exists(configFile))
            {
                var jsonString = File.ReadAllText(configFile);
                Config = JsonSerializer.Deserialize<Config>(jsonString);
            }
            else
            {
                Config = new Config{
                    WebsocketAddress = "",
                    WebsocketPassword = "",
                    SceneName = "",
                    SourceName = ""
                };

                // Get configuration settings...
                Console.WriteLine("Moin!");
                Console.WriteLine("Let's do some configuration stuff together. Great fun times... ;-)");
                Console.WriteLine();

                Console.Write("Enter the OBS scene name where the Browser Source is in: ");
                Config.SceneName = Console.ReadLine();
                Console.Write("Enter the name of the Browser Source: ");
                Config.SourceName = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(Config.SourceName))
                {
                    Log.Logger.Warning("You must specify the name, otherwise I can't do anything... :-/");
                    Log.Logger.Information("Press any key to close this thing and try again.");
                    Console.ReadKey();
                    return;

                }
                Console.Write("Optionally change OBS websocket address and port (default is 127.0.0.1:4455): ");
                Config.WebsocketAddress = Console.ReadLine();
                Console.Write("Optionally change OBS websocket password (default is <blank>): ");
                Config.WebsocketPassword = Console.ReadLine();

                // ...and write them to file
                using FileStream createStream = File.Create(configFile);
                await JsonSerializer.SerializeAsync(createStream, Config);
                await createStream.DisposeAsync();

                Console.WriteLine();
                Console.WriteLine("gg ez!");
                Thread.Sleep(500);
                Console.Clear();
            }

            clientID = ConfigurationManager.AppSettings["ClientID"];
            clientSecret = ConfigurationManager.AppSettings["ClientSecret"];

            var token = string.Empty;

            // Use DiscordIPC to listen to the VoiceChannelSelect event
            client = new DiscordIPC(clientID);
            Log.Logger.Information("Waiting for Discord Client IPC connection...");
            await WaitForDiscordClient();

            if (!File.Exists(tokenJsonFile))
            {
                //Authorize
                await AuthorizeAndAuthenticateDiscord();
            }
            else
            {
                var jsonString = File.ReadAllText(tokenJsonFile);
                var authJson = JsonSerializer.Deserialize<JsonElement>(jsonString);
                AccessToken = authJson.GetProperty("AccessToken").ToString();
                var expirationDate = Convert.ToDateTime(authJson.GetProperty("Expires").ToString());

                // Refresh that thing if rotten
                if (DateTime.Compare(DateTime.Now.AddDays(-1), expirationDate) > 0)
                {
                    var authResponse = OAuth2.AuthenticateByToken(
                        new OAuth2Provider{
                            ClientId = clientID,
                            ClientSecret = clientSecret,
                            AuthUri = "https://discord.com/api/oauth2/authorize",
                            AccessTokenUri = "https://discord.com/api/oauth2/token"
                        },
                        authJson.GetProperty("RefreshToken").ToString()
                    );

                    using FileStream createStream = File.Create(tokenJsonFile);
                    await JsonSerializer.SerializeAsync(createStream, authResponse);
                    await createStream.DisposeAsync();

                    AccessToken = authResponse.AccessToken;
                }

                //Authenticate (ignoring the response here)
                try
                {
                    await client.SendCommandAsync(new Authenticate.Args()
                    {
                        access_token = AccessToken
                    });
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex.Message);
                    await AuthorizeAndAuthenticateDiscord();
                }
            }
            
            client.OnVoiceChannelSelect += voiceChannelHandler;
            await client.SubscribeAsync(new VoiceChannelSelect.Args());

            // Initialize and start timer
            var progress = new Progress<Exception>((ex) =>
            {
                // handle exception from timercallback
                throw ex;
                //if (ex is OBSWebsocketDotNet.ErrorResponseException)
                //{
                //    Log.Logger.Warning($"Connect failed: {ex.Message}");
                //}
                //else if (ex is AuthFailureException)
                //{
                //    Log.Logger.Warning("Unable to connect to OBS. Please double-check password!");
                //}
            });
            heartbeatTimer = new Timer(x => RunHeartbeatTask(progress), null, 0, 5000);

            // Instantiate OBS Websocket
            Log.Logger.Information("Waiting for OBS websocket connection...");

            Config.WebsocketAddress = string.IsNullOrWhiteSpace(Config.WebsocketAddress) ? "ws://127.0.0.1:4455" : Config.WebsocketAddress;
            Config.WebsocketPassword = string.IsNullOrEmpty(Config.WebsocketPassword) ? "" : Config.WebsocketPassword;
            if (!Config.WebsocketAddress.StartsWith("ws://")) Config.WebsocketAddress = $"ws://{Config.WebsocketAddress}";

            obsOnDisconnect(null, null);

            // Rename this thing
            Console.Title = $"Syncing StreamKit-Voice-Overlay in OBS @ /{Config.SceneName}/{Config.SourceName}";

            // Block this task until the program is closed.
            await Task.Delay(-1);

            // Dispose
            client.Dispose();
        }

        private static void obsOnConnect(object sender, EventArgs e)
        {
            Log.Logger.Information("Connected to OBS.");
            UpdateStreamkitDiscordOverlayTrigger().Wait();
            IsOBSconnected = true;
        }

        private static void obsOnDisconnect(object sender, ObsDisconnectionInfo e)
        {
            if (e != null && e.ObsCloseCode == ObsCloseCodes.AuthenticationFailed)
            {
                Log.Logger.Error("Websocket password seems to be incorrect... Please check!");
                return;
            }

            if (IsOBSconnected) Log.Logger.Warning("Websocket connection lost. Waiting for OBS...");
            IsOBSconnected = false;

            obs = new OBSWebsocket();
            obs.Connected -= obsOnConnect;
            obs.Disconnected -= obsOnDisconnect;
            obs.Connected += obsOnConnect;
            obs.Disconnected += obsOnDisconnect;
            obs.ConnectAsync(Config.WebsocketAddress, Config.WebsocketPassword);
        }

        private static void voiceChannelHandler(object sender, VoiceChannelSelect.Data data)
        {
            var guild = data.guild_id;
            var channel = data.channel_id;
            Log.Logger.Information("Voice channel change detected...");
            UpdateStreamkitDiscordOverlay(guild, channel);
        }

        private static void UpdateStreamkitDiscordOverlay(string guildId, string channelId)
        {
            if (!obs.IsConnected) return;

            var streamkitUri = new UriBuilder($"https://streamkit.discord.com/overlay/voice/{guildId}/{channelId}?icon=true&online=true&logo=white&text_color=%23ffffff&text_size=14&text_outline_color=%23000000&text_outline_size=0&text_shadow_color=%23000000&text_shadow_size=0&bg_color=%231e2124&bg_opacity=0.95&bg_shadow_color=%23000000&bg_shadow_size=0&invite_code=&limit_speaking=true&small_avatars=true&hide_names=false&fade_chat=0");
            try
            {
                var sceneItemId = obs.GetSceneItemId(Config.SceneName, Config.SourceName, 0);

                // check for exisiting browser source with given name
                var currentBrowserSourceSettings = obs.GetInputSettings(Config.SourceName);
                if (currentBrowserSourceSettings.InputKind != "browser_source")
                {
                    throw new Exception($"{Config.SourceName} is not a browser source. Unable to get and set URL... Doh!");
                }

                if (string.IsNullOrEmpty(guildId) || string.IsNullOrEmpty(channelId))
                {
                    obs.SetSceneItemEnabled(Config.SceneName, sceneItemId, false);
                    Log.Logger.Information("Elvis has left the building!");
                }
                else
                {
                    // Be nice: Respect user's custom StreamKit parameters
                    var currentUri = new UriBuilder(currentBrowserSourceSettings.Settings["url"].ToString());
                    if (currentUri.Host == "streamkit.discord.com"
                        && currentUri.Query != null
                        && currentUri.Query.Length > 0)
                    {
                        streamkitUri.Query = currentUri.Query;
                    }

                    obs.SetSceneItemEnabled(Config.SceneName, sceneItemId, true);
                    obs.SetInputSettings(Config.SourceName, new Newtonsoft.Json.Linq.JObject { { "url", streamkitUri.ToString() } });
                    Log.Logger.Information("Browser Source update successfull!");
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error($"Ups! OBS said: '{ex.Message}'");
            }
        }

        private static async Task UpdateStreamkitDiscordOverlayTrigger()
        {
            // Check if user is already in VoiceChannel and set browser source accordingly
            GetSelectedVoiceChannel.Data response = await client.SendCommandAsync(new GetSelectedVoiceChannel.Args() { });
            if (response != null) UpdateStreamkitDiscordOverlay(response.guild_id, response.id);
        }

        private static async void RunHeartbeatTask(IProgress<Exception> progress)
        {
            try
            {
                if (IsDiscordAvailable) await client.SendCommandAsync(new GetSelectedVoiceChannel.Args() { });
            }
            catch (Exception ex)
            {
                IsDiscordAvailable = false;
                Log.Logger.Warning($"IPC connection lost: {ex.Message} Waiting for Discord Client...");

                // Unsubscribe from event handler
                client.OnVoiceChannelSelect -= voiceChannelHandler;

                await WaitForDiscordClient();
                try
                {
                    await client.SendCommandAsync(new Authenticate.Args()
                    {
                        access_token = AccessToken
                    });
                }
                catch (Dec.DiscordIPC.ErrorResponseException erex)
                {
                    Log.Logger.Fatal(erex.Message);
                    Log.Logger.Information("Aw snap! We need to restart this thing and authorize in Discord.");
                    System.Environment.Exit(-1);
                }

                client.OnVoiceChannelSelect += voiceChannelHandler;
                await client.SubscribeAsync(new VoiceChannelSelect.Args());
                await UpdateStreamkitDiscordOverlayTrigger();
            }
        }

        private static async Task WaitForDiscordClient()
        {
            while (!IsDiscordAvailable)
            {
                try
                {
                    await client.InitAsync();
                    IsDiscordAvailable = true;
                    Log.Logger.Information("Connected to local Discord client.");
                    //The pipe needs time... :-D
                    Thread.Sleep(750);
                }
                catch (System.Exception)
                {
                    Log.Logger.Debug("Waiting for Discord client...");
                    Thread.Sleep(5000);
                }
            }
        }

        private static async Task AuthorizeAndAuthenticateDiscord()
        {
            //Authorize
            string code;
            Authorize.Data codeResponse = await client.SendCommandAsync(
                new Authorize.Args()
                {
                    scopes = new List<string>() { "rpc" },
                    client_id = clientID,
                });
            code = codeResponse.code;

            var authResponse = OAuth2.AuthenticateByCode(
                new OAuth2Provider
                {
                    ClientId = clientID,
                    ClientSecret = clientSecret,
                    AuthUri = "https://discord.com/api/oauth2/authorize",
                    AccessTokenUri = "https://discord.com/api/oauth2/token"
                },
                "http://127.0.0.1",
                code
            );

            using FileStream createStream = File.Create(tokenJsonFile);
            await JsonSerializer.SerializeAsync(createStream, authResponse);
            await createStream.DisposeAsync();

            await client.SendCommandAsync(new Authenticate.Args()
            {
                access_token = authResponse.AccessToken
            });

            AccessToken = authResponse.AccessToken;
        }
    }
}
