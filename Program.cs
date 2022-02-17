using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using CliWrap;
using CliWrap.Buffered;
using Dec.DiscordIPC;
using Dec.DiscordIPC.Commands;
using Dec.DiscordIPC.Events;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace VoiceChannelGrabber
{
    class Program
    {
        private static string clientID { get; set; }

        private static string clientSecret { get; set; }


        private static string tokenJsonFile = "token.json";

        private static Config Config {get; set;}

        private static string AccessToken { get; set; }

        private static bool IsDiscordavailable { get; set; }

        private static bool IsOBSavailable { get; set; }

        private static DiscordIPC client { get; set; }

        static int Main(string[] args)
        {
            // Initialize serilog logger
            Log.Logger = new LoggerConfiguration()
                 .WriteTo.Console(Serilog.Events.LogEventLevel.Debug, theme: AnsiConsoleTheme.Literate)
                 .WriteTo.File(Path.Combine(Directory.GetCurrentDirectory(), "logs", $"log-.txt"), Serilog.Events.LogEventLevel.Information, rollingInterval: RollingInterval.Day)
                 .MinimumLevel.Information()
                 .Enrich.FromLogContext()
                 .CreateLogger();

            // Name this thing
            Console.Title = "Discord Voice Channel Grabber for Streamkit-Overlay in OBS";

            // Grab the OBSCommand.exe if not present
            var libPath = Path.Combine(Directory.GetCurrentDirectory(), "lib");

            if (!File.Exists(Path.Combine(libPath, "OBSCommand.exe")))
            {
                if (!Directory.Exists(libPath)) Directory.CreateDirectory(libPath);
                using (var client = new WebClient())
                {
                    var zipPath = Path.Combine(libPath, "OBSCommand_v1.5.7.zip");
                    Log.Logger.Information($"Downloading OBSCommand to {zipPath}...");
                    client.DownloadFile(@"https://github.com/REALDRAGNET/OBSCommand/releases/download/1.5.7.0/OBSCommand_v1.5.7.zip", zipPath);
                    ZipFile.ExtractToDirectory(zipPath, libPath, true);

                    IEnumerable<FileInfo> files = Directory.GetFiles(Path.Combine(libPath,"OBSCommand")).Select(f => new FileInfo(f));
                    foreach (var file in files)
                    {
                        File.Move(file.FullName, Path.Combine(libPath, file.Name));
                    }

                    // Clean up the mess you made, now!
                    Directory.Delete(Path.Combine(libPath,"OBSCommand"));
                    File.Delete(zipPath);
                    File.Delete(Path.Combine(libPath,"obs-websocket-4.9.1-Windows-Installer.exe"));
                }
            }

            try
            {
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
            var configFile = "config.json";

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
                Console.Write("Optionally change OBS websocket address and port (default is 127.0.0.1:4444): ");
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

            // Check if OBS is available
            Log.Logger.Information("Waiting for OBS websocket connection...");
            var heartbeatTimer = new System.Timers.Timer(5000);
            heartbeatTimer.Elapsed += heartbeatTimer_Elapsed;
            heartbeatTimer_Elapsed(null, null);
            heartbeatTimer.Start();

            // Rename this thing
            Console.Title = $"Syncing Streamkit-Overlay in OBS @ /{Config.SceneName}/{Config.SourceName}";

            // Block this task until the program is closed.
            await Task.Delay(-1);

            // Unsubscribe from the event
            await client.UnsubscribeAsync(new VoiceChannelSelect.Args());
            client.OnVoiceChannelSelect -= voiceChannelHandler;

            // Dispose
            client.Dispose();
        }

        private static void voiceChannelHandler(object sender, VoiceChannelSelect.Data data)
        {
            var guild = data.guild_id;
            var channel = data.channel_id;
            Log.Logger.Information("Voice channel change detected...");
            UpdateStreamkitDiscordOverlay(guild, channel);
        }

        private static async void UpdateStreamkitDiscordOverlay(string guildId, string channelId)
        {
            if (!IsOBSavailable) return;

            if (string.IsNullOrEmpty(guildId) || string.IsNullOrEmpty(channelId))
            {
                await CreateAndExecuteOBSCommand($"/hidesource=\"{Config.SceneName}\"/\"{Config.SourceName}\"");
                Log.Logger.Information("Elvis has left the building!");
                return;
            }
            await CreateAndExecuteOBSCommand($"/showsource=\"{Config.SceneName}\"/\"{Config.SourceName}\"");

            var streamkitUrl = $"https://streamkit.discord.com/overlay/voice/{guildId}/{channelId}?icon=true&online=true&logo=white&text_color=%23ffffff&text_size=14&text_outline_color=%23000000&text_outline_size=0&text_shadow_color=%23000000&text_shadow_size=0&bg_color=%231e2124&bg_opacity=0.95&bg_shadow_color=%23000000&bg_shadow_size=0&invite_code=&limit_speaking=true&small_avatars=true&hide_names=false&fade_chat=0";
            var getSourceSettingsCommand = await CreateAndExecuteOBSCommand($"/command=GetSourceSettings,sourceName={Config.SourceName}");

            if (getSourceSettingsCommand.StandardOutput.Contains("\"status\": \"ok\"") && getSourceSettingsCommand.StandardOutput.Contains("\"sourceType\": \"browser_source\""))
            {
                //found it
                var setSourceSettingsProcess = await CreateAndExecuteOBSCommand($"/command=SetSourceSettings,sourceName={Config.SourceName},sourceSettings=url='{streamkitUrl}'");
                if (setSourceSettingsProcess.StandardOutput.Contains("\"status\": \"ok\""))
                {
                    Log.Logger.Information("Browser Source update successfull!");
                }
                else
                {
                    Log.Logger.Error("Aw snap! Something went terribly wrong... My bad!");
                }
            }
            else
            {
                var displayAddress = !string.IsNullOrWhiteSpace(Config.WebsocketAddress) ? Config.WebsocketAddress : "127.0.0.1:4444";
                Log.Logger.Error($"Browser Source '{Config.SourceName}' not found in Scene '{Config.SceneName}' calling <ws://{displayAddress}>");
            }
        }

        private static async Task UpdateStreamkitDiscordOverlayTrigger()
        {
            // Check if user is already in VoiceChannel and set browser source accordingly
            GetSelectedVoiceChannel.Data response = await client.SendCommandAsync(new GetSelectedVoiceChannel.Args() { });
            if (response != null) UpdateStreamkitDiscordOverlay(response.guild_id, response.id);
        }

        private static async void heartbeatTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            var heartbeatCommand = await CreateAndExecuteOBSCommand("/command=GetVersion");

            if(heartbeatCommand.StandardOutput.Contains("OBSWebsocketDotNet.AuthFailureException"))
            {
                Log.Logger.Warning("Unable to connect to OBS. Please double-check password!");
            }
            else if (heartbeatCommand.StandardOutput.Contains("\"status\": \"ok\""))
            {
                if (!IsOBSavailable)
                {
                    Log.Logger.Information("Connected to OBS.");
                    // Give OBS some time to initialize
                    Thread.Sleep(750);
                    await UpdateStreamkitDiscordOverlayTrigger();
                }
                IsOBSavailable = true;
                Log.Logger.Debug("OBS is alive...");
            }
            else
            {
                if (IsOBSavailable)
                {
                    Log.Logger.Warning("Websocket connection lost. Waiting for OBS...");
                }
                IsOBSavailable = false;
                Log.Logger.Debug("OBS is dead...");
            }


            try
            {
                if (IsDiscordavailable) await client.SendCommandAsync(new GetSelectedVoiceChannel.Args() { });
            }
            catch (Exception ex)
            {
                IsDiscordavailable = false;
                Log.Logger.Warning($"IPC connection lost: {ex.Message} Waiting for Discord Client...");

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
                
                await client.SubscribeAsync(new VoiceChannelSelect.Args());
                await UpdateStreamkitDiscordOverlayTrigger();
            }
        }

        private static async Task WaitForDiscordClient()
        {
            while (!IsDiscordavailable)
            {
                try
                {
                    await client.InitAsync();
                    IsDiscordavailable = true;
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

        private static async Task<BufferedCommandResult> CreateAndExecuteOBSCommand(string command)
        {
            // OBSCommand defaults to 127.0.0.1:4444 and a blank password when not given
            // I make use of it to have address and password optional in config
            if (!string.IsNullOrWhiteSpace(Config.WebsocketAddress)
                && !string.IsNullOrWhiteSpace(Config.WebsocketPassword))
            {
                return await Cli.Wrap("lib/OBSCommand.exe")
                    .WithArguments($"/server={Config.WebsocketAddress} /password={Config.WebsocketPassword} {command}")
                    .WithWorkingDirectory(Directory.GetCurrentDirectory())
                    .ExecuteBufferedAsync();
            }
            else if (!string.IsNullOrWhiteSpace(Config.WebsocketAddress)
                && string.IsNullOrWhiteSpace(Config.WebsocketPassword))
            {
                return await Cli.Wrap("lib/OBSCommand.exe")
                    .WithArguments($"/server={Config.WebsocketAddress} {command}")
                    .WithWorkingDirectory(Directory.GetCurrentDirectory())
                    .ExecuteBufferedAsync();
            }
            else if (string.IsNullOrWhiteSpace(Config.WebsocketAddress)
                && !string.IsNullOrWhiteSpace(Config.WebsocketPassword))
            {
                return await Cli.Wrap("lib/OBSCommand.exe")
                    .WithArguments($"/password={Config.WebsocketPassword} {command}")
                    .WithWorkingDirectory(Directory.GetCurrentDirectory())
                    .ExecuteBufferedAsync();
            }
            return await Cli.Wrap("lib/OBSCommand.exe").WithArguments($"{command}")
                .WithWorkingDirectory(Directory.GetCurrentDirectory())
                .ExecuteBufferedAsync();
        }
    }
}
