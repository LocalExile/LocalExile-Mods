# Live Economy

Faustus's Currency Exchange as a live market, at the prices of the official Path of Exile leagues. While this mod is on, the Gateway
gets new prices once an hour from the LocalExile rates service, so neither the mod nor your game server contacts Path of
Exile's servers. Turn it off and the exchange keeps the last prices it got, until they are too old to trade at.

If the service cannot be reached, the last prices stay and the Gateway tries again five minutes later.

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
| Rates service | `http://localexile-chat.duckdns.org:6114/rates.json` | the address of the rates service (advanced) |

The mod writes the current league's name into its own `settings.json`, for the label "Current league (Allflame)" in an editor.

Anyone can run a rates service (`src/GameServer.Rates` in LocalExile) and put its address here.

## Chat commands

None. LocalExile's `/exchange` opens the window.

## LocalExile versions

Needs a LocalExile with the Currency Exchange module (`UseExchangeRates`).
