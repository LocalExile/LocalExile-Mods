# Global Chat

Talk to other LocalExile players, whichever server they run. While this mod is on, global, trade and whisper chat go
through the LocalExile global chat server. Turn it off and chat is back on your own local chat server, without a restart.
Local chat stays between the players in your area.

If the server cannot be reached, chat says so and stays local, and tries again at an area change a minute later.
Lines and character names pass through that server unencrypted; it stores nothing.

## Settings

| Setting | Default | What it is |
|---|---|---|
| Address | `localexile-chat.duckdns.org` | the IP or host name of the chat server |
| Port | `6113` | its port |

Anyone can run a chat server for their own group (`src/GameServer.Chat` in LocalExile) and put its address here.

## Chat commands

None.

## LocalExile versions

LocalExile v0.2.0-beta, v0.2.1-beta, v0.2.2-beta, v0.3.0-beta and v0.4.0-beta. Needs a LocalExile with the Chat module (`ChatRoutes.Use`). From 1.1.0 it needs the one hook system (LocalExile pull request 818).
