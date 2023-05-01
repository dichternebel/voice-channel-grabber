# Voice Channel Grabber [![Licensed under the MIT License](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/dichternebel/voice-channel-grabber/blob/main/LICENSE)
Sync your StreamKit Browser Source in OBS with your current voice channel from your Discord client.

**tl;dr**  
If you are a streamer using OBS and Discord StreamKit Overlay you will want this thing for synchronizing your OBS Browser Source automagically.
To get it, just [join my Discord](#joining-the-public-beta) and ask for it!

## Demo
See it in action here:

![Voice Channel Grabber Showoff](assets/voice_channel_grabber_showoff.gif)

## The Story behind
Are you also using **OBS** for streaming? And do you use **Discord**? Then you probably also use the **StreamKit Discord Overlay** Browser Source in OBS. So do I!

That's a great combination to let your viewers know, who is on the server with you, who is currently talking and so on.

But for me this was also a combination that often failed. And the simple reason for that is the fact, that you have to tell StreamKit what Discord Server and what Voice Channel you are currently on.

When adding the Browser Source in OBS the StreamKit URL looks something like this:

```javascript
https://streamkit.discord.com/overlay/voice/1234567890/0987654321?icon=true&online=true&logo=white...
```

The first number is the `Guild/Server-ID` and the second is the `Voice Channel-ID`.

So, if you stick on one server and one voice channel that's perfectly working.

**But what if you play on different servers or change channels during your stream?**

Then you either have to edit the server and channel IDs in the `OBS Browser Source URL field` each time you switch, or like what I did have multiple Browser Sources with the different Server-IDs and Channel-IDs I used play on.  
But hell, sometimes I forgot to enable/show the sources, disable/hide other sources or I forgot completely about it... resulting in either displaying the wrong, or even multiple channels or nothing at all during the stream. :-/

**I do not want to take care about this overlay: It should just work!!!**

So the idea was to be able to synchronize the `Discord StreamKit URL` with the currently selected Discord Voice channel. This line of code with two variables is what I started with:

```javascript
https://streamkit.discord.com/overlay/voice/{guildId}/{channelId}?icon=true&online=true&logo=white
```

But unfortunately Discord is still limiting the usage of the underlying technology called `"RPC"` or `"IPC"`:

![RPC limitation](assets/discord-rpc-limitation.png)
> source: https://discord.com/developers/docs/topics/rpc

With the help of [DiscordIPC](https://github.com/dcdeepesh/DiscordIPC) I managed to build something that is actually working and the final result is this git repository.

## Ok, sick! What do I need to get this thing?

Just download the current version from the [release section](https://github.com/dichternebel/voice-channel-grabber/releases).

Now after downloading there are currently two possibilities:
1. Ask me to join the app tester list
2. Run this thing on your own with your private Discord app ClientID.

### Joining the app tester list

I am able to offer up to 50 seats to people wanting to test-drive this thing.

So just [join my discord](https://discord.gg/4WFudUV6sm) and ask me, I will add you to the list of app testers. Once you are on the list you may use this without any limitation whatsoever.

### Use your own private Discord app

Go to the [discord developer portal](https://discord.com/developers/applications) and add a `private application` with Redirect URI to `http://localhost:3000/callback`. This thing is not working for apps associated to a team due to the RPC limitations mentioned above resulting in OAuth2 scope errors.

### Building this
It's developed starting with Visual Studio 2019 and now v2022, but should also be compilable in VS Code.

Rename the `app.config.example` to `app.config` and paste your client id and client secret into the settings. Make sure you have set up a Redirect URI in your Discord app to `http://localhost:3000/callback`. If you prefer like me a single file application, hit the publish functionality in Visual Studio. That should be it!







