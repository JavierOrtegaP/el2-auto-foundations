# Changelog

## 1.0.2 — 2026-10-08

- Every spot you can afford is ordered at once, instead of at most ten per frame: a foundation's price doesn't depend
  on the others bought.
- No more fixed limits on the game's orders (a 10-second wait, three tries per spot). The mod waits for the game's
  answer to each: a spot the game refuses is left until next turn; an order it doesn't accept at that moment of the
  turn is sent again once the game moves on.
- Spots are scanned whenever the simulation moves on, instead of once a second, so new spots are bought sooner.
- The window lists what was bought in the last turn anything was, instead of the last 15 purchases.

## 1.0.1 — 2026-10-04

- No changes to the mod itself. The download now comes with the current README (a screenshot, how to support the
  project), and Auto Foundations is also on [Nexus Mods](https://www.nexusmods.com/endlesslegend2/mods/10), updated
  with every release.

## 1.0.0 — 2026-10-04

First release, for ENDLESS Legend 2 1.0 (Steam build 25623753).

- Buys foundations automatically wherever the game allows one, with influence above a reserve you choose (1,000 by
  default); free foundations are always taken.
- Best spots first: most tile yield per influence, weighted by each city's job strategy.
- In-game window: a Foundations page in [Mod Menu](https://github.com/JavierOrtegaP/el2-mod-menu) (F7) when
  installed, else its own window (`` ` `` key): on/off, reserve, "Buy now", spots per city with a per-city switch,
  recent purchases.
- Optional on-screen notice when foundations are bought (off by default).
