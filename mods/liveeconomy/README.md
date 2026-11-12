# Live Economy

Faustus's Currency Exchange as a live market, at the prices of the official Path of Exile leagues. While this mod is on, it gets new
prices once an hour from the LocalExile rates server. With no server in its settings, it gets them from GGG itself. Turn it off and the
exchange keeps the last prices it got, until they are too old to trade at.

If the server cannot be reached, the last prices stay and the mod tries again five minutes later.

## Settings

| Setting | Default | What it is |
|---|---|---|
| Order slots | 20 | how many orders a player can have |
| Prices in Standard | Standard | the official league the Standard league here trades at |
| Prices in other leagues | Current league | the official league every other league here trades at, Hardcore among them |
| Spread | ×1 | multiplies how far from the official price Faustus trades: 1% for the busiest items, up to 10% for the rarest |
| Share of the market | 10% | how much of an item's official hourly trade Faustus offers in an hour; 0: no limit |
| Patient orders | 100% | how much of the way from the buying price to the selling price an order fills with time; 50% leaves no profit from the spread; 0: none |
| Patient order speed | ×1 | how much faster such orders fill: by default at most 5 minutes for the busiest items, 12 hours for the rarest |
| Trading while away | 168 hours | how many hours away an order catches up on; 0: only the current hour |
| Gold fee | ×1 | multiplies the gold an order pays |
| Oldest rates | 7 days | rates older than this are not traded at |
| Faustus speaks | on | he says a line when an order is placed or filled |
| Server address | `localexile-chat.duckdns.org` | the rates server; empty: this mod gets the rates from GGG itself |
| Server port | 6114 | its port; 0: the same |
| History without a server | 168 hours | with no server, how many hours of GGG's digests to keep (advanced) |

The mod writes the current league's name into its own `settings.json`, for the label "Current league (Allflame)" in an editor.

## Without a server

With the address empty, the mod reads GGG's public digest (`https://web.poecdn.com/api/currency-exchange/<hour>`) from your machine: about 150 MB and 4 minutes the
first time, then about 1 MB an hour. It keeps them in `data/liveeconomy/`, and the Gateway uses up to about 230 MB more memory while it reads them.

## Running a rates server

The same dll is the server, on a machine with .NET and no Gateway. It reads GGG's digests once an hour and serves the prices at `/rates.json`, so its users never contact GGG:

```
dotnet liveeconomy.dll --serve
```

`Game_RATES_PORT` (default 6114), `Game_RATES_FOLDER` (default `rates-data`), `Game_RATES_HOURS` (default 168). With `--once <file>` it writes the file and exits.

## Chat commands

None. LocalExile's `/exchange` opens the window.

## LocalExile versions

1.1.3 works with LocalExile v0.4.3-beta, v0.5.0-beta and v0.5.1-beta (the same code as 1.1.2); 1.1.1 with v0.4.2-beta; 1.1.0 with v0.4.1-beta, the first with the one hook system (`GameEvents`). Its 1.0.x versions work with v0.2.1-beta to v0.4.0-beta. Needs a LocalExile with the Currency Exchange module (`ExchangeRateStore.Use`, `ExchangeRateStore.Publish`).
