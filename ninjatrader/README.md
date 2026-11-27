# EMA14 + ICT Silver Bullet: MNQ intraday bot for NinjaTrader 8

The **15-minute EMA14 gives the direction.** The **1-minute ICT sequence gives the entry**: liquidity sweep, then market structure shift, then a fair value gap retrace. It only trades inside the **New York AM Silver Bullet window (10:00–11:00 New York time)** and is sized for **funded / prop-firm accounts**.

File: [`Ema14SilverBulletMNQ.cs`](Ema14SilverBulletMNQ.cs) (NinjaScript strategy, plain C# 5, no add-ons needed)

> Educational template, not financial advice. No strategy wins every week. Backtest it, run it on Sim for a few weeks, and check that your prop firm allows automated trading before using it on a funded account (see [Going live](#7-going-live-on-a-funded-account)).

---

## 1. Market and timing

| Item | Value |
|---|---|
| Instrument | MNQ (Micro E-mini Nasdaq-100). Tick = 0.25 pt = $0.50; 1 point = $2 per contract |
| Contract | Front month. In late Nov 2026 that is **MNQ 12-26**; roll to **MNQ 03-27** around the second week of December |
| Entry chart | 1-minute (primary series) |
| Bias chart | 15-minute (added by the script automatically) |
| Trading hours template | `CME US Index Futures ETH` (the bot needs overnight data for its liquidity levels) |
| Watch for sweeps from | 09:30 New York |
| Silver Bullet window | **10:00–11:00 New York**. The MSS, FVG and order must all happen inside it |
| Hard flat | 12:00 New York (cancel everything and close any open position) |

All times are New York time and DST is handled. The script converts from whatever time zone NinjaTrader is set to, so it works the same in India, Europe or the US.

---

## 2. The rules (long side; shorts are the exact mirror)

### Step 1: Bias (15-minute EMA14)
Longs are allowed only when the last closed **15m candle is above EMA14 and EMA14 is rising** (EMA14 now > EMA14 two bars ago).
Optional (`BiasMode = HtfEmaAndStructure`): the last 15m break of structure must also be bullish (a 15m close above the last 15m swing high).

### Step 2: Liquidity sweep (sell-side taken)
On the 1m chart, price trades **below the most recent confirmed 1m swing low** (a 5-bar fractal, `SwingStrength = 2`).
This is the stop-run / "Judas" move against the bias. The bot remembers the lowest price of this leg (the **sweep low**).
Optional (`RequireKeyLevelSweep`): the sweep must also take a session pool: the overnight low, the 09:30–10:00 low, or the previous day's low.

### Step 3: Market structure shift + EMA14 reclaim (inside 10:00–11:00)
A 1m candle **closes above the latest swing high that hasn't been closed through yet** and:
* closes **above the 1m EMA14** (momentum flips back with the bias), and
* is a displacement candle: bullish body ≥ `0.5 × ATR(14)`.

The setup is cancelled if no MSS happens within 20 bars of the sweep.

### Step 4: Fair value gap entry
Within 3 bars of the MSS, the bot looks inside the displacement leg (sweep low → now) for a **bullish FVG**: candle 3's low is above candle 1's high, and the gap is at least 8 ticks (2 points).
* Entry: **buy limit at the top of the FVG** (`FvgProximal`), or at the 50% "consequent encroachment" (`FvgMidpoint`).
* The FVG must not already have been traded back into.
* The order is cancelled if it's not filled within 10 bars, if price reaches the target first, if a candle closes below the stop, or at 11:00.

### Step 5: Stop, targets, management
| | Default |
|---|---|
| Stop | 1 point (4 ticks) below the **sweep low** |
| Allowed stop size | 5–30 MNQ points; anything outside this range is skipped |
| TP1 | **1R**, takes off half the contracts |
| Runner | **2R** (`FixedRR`), or the nearest opposing liquidity pool ≥ 1.5R, capped at 4R (`Liquidity` mode) |
| Breakeven | Once TP1 fills (or price reaches 1R), the stop moves to entry + 1 tick |
| Time stop | Flat by 12:00 New York |

### Picture of a long setup
```
 15m: close > EMA14, EMA14 rising  ->  only look for longs

 1m:            swing high (MSS level)
                   ___                         target 2R --------
          _/\_    /   \        |==FVG==|<- buy limit   TP1 1R ---
         /    \  /     \       |       |
   swing low ->\/       \     /  <- displacement candle closes above swing high AND EMA14
   ------------------    \   /
                          \_/  <- sweep: trades below swing low (sell-side liquidity taken)
                          stop = sweep low - 1 pt
```

---

## 3. Funded account risk plan

Built-in guard rails (all adjustable in the strategy properties):

| Guard rail | Default | Why |
|---|---|---|
| Risk per trade | $200 | Contracts = floor($200 / (stop pts × $2)), capped at max contracts |
| Max contracts | 5 MNQ | Keeps you well inside scaling-plan limits |
| Max trades / day | 2 | Silver Bullet is one window; overtrading kills funded accounts |
| Max losses / day | 2 | |
| Stop after first win | On | One good trade and done. Protects the trailing drawdown and helps with consistency rules |
| Daily loss limit | $400 | Set this **below** your firm's daily loss limit |
| Daily profit cap | $800 | Helps with "best day must be < X% of profit" consistency rules |
| No-trade dates | empty | e.g. `2026-12-09` (FOMC day). Add any high-impact news days you want to skip |

Suggested sizing (rule of thumb: **risk per trade ≤ ~10% of the max drawdown**, daily stop ≤ ~20%):

| Account | Typical max drawdown* | Risk / trade | Daily loss limit | Max contracts |
|---|---|---|---|---|
| 25K | ~$1,000–1,500 | $75–100 | $200 | 3 |
| 50K | ~$2,000–2,500 | $150–200 | $400 | 5 |
| 100K | ~$3,000 | $250–300 | $600 | 8 |

*Drawdown types, daily limits and consistency rules differ by firm and change often. Use your firm's current numbers.

At the defaults, a typical 15–25 point stop means 4–6 MNQ before the cap, so 5 contracts at most ($150–$250 risk per trade).

---

## 4. Will it give 3+ trades a week?

It should be close, but I couldn't measure it here: you need to backtest it in NinjaTrader on real MNQ data. On 1-minute MNQ, a swing sweep followed by an MSS shows up in the 10:00–11:00 window on most days. The EMA14 bias, the FVG requirement and the "must fill" rule then filter that down. A reasonable expectation is **roughly 2–5 trades a week**. Check the real number in the Strategy Analyzer ("Trades" tab ÷ weeks tested).

**If you get fewer than 3 a week**, loosen these one at a time:
1. `MinFvgTicks` 8 → 4–6
2. `MinDisplacementAtr` 0.5 → 0.3
3. `EntryExpiryBars` 10 → 15, and keep `EntryMode = FvgProximal`
4. `StopAfterFirstWin` → off (allows a second trade after a win)
5. `MaxStopPoints` 30 → 40 (and lower `RiskPerTradeDollars` if needed)

**If you get plenty of trades but low quality**, tighten instead: `RequireKeyLevelSweep = true`, `BiasMode = HtfEmaAndStructure`, `EntryMode = FvgMidpoint`, `TargetMode = Liquidity`.

---

## 5. Install in NinjaTrader 8

1. Copy `Ema14SilverBulletMNQ.cs` to `Documents\NinjaTrader 8\bin\Custom\Strategies\`.
2. In NinjaTrader: **New → NinjaScript Editor**, open the file, and press **F5** to compile. The bottom panel should show no errors.
3. Open a chart: **MNQ 12-26, 1 Minute**, trading hours **CME US Index Futures ETH**, with at least 10 days loaded.
4. Right-click the chart → **Strategies** → add **Ema14SilverBulletMNQ** → choose account **Sim101** → tick **Enabled** → OK.
5. Open **New → NinjaScript Output** to watch the bot explain every decision (sweep, MSS, order, cancel reason, P&L).

The chart shows a shaded 10:00–11:00 window, arrows at sweeps, "MSS" labels, and a box on the FVG used for the entry.

> If F5 reports an error on `Core.Globals.GeneralOptions.TimeZoneInfo`, replace that expression with `TimeZoneInfo.Local` (NinjaTrader uses your PC's time zone by default).

---

## 6. Backtest checklist (Strategy Analyzer)

* Instrument MNQ, **1 Minute**, trading hours **CME US Index Futures ETH**, at least 12 months of data
* **Order fill resolution: High → Tick 1** (realistic limit fills; needs tick data)
* Commission: your broker's MNQ round-turn (roughly $1–$2 per contract with fees). Slippage: 1 tick (already the default)
* Optimize on one period (e.g. 2024–2025), then test **unchanged** settings on a later period (2026). Prefer parameter regions where results stay stable, not the single best value
* What to look for before going live:
  * ≥ 3 trades/week on average
  * Profit factor > 1.3 over 150+ trades
  * Max drawdown (at your chosen risk) **under half** of your firm's drawdown
  * Longest losing streak × risk per trade < firm drawdown
  * Worst day loss < firm daily loss limit

---

## 7. Going live on a funded account

1. **Confirm in writing that your firm allows a fully automated NinjaTrader strategy on your account type.** Rules differ between firms and change often. Some firms allow bots, some only allow semi-automation (ATM strategies), and some ban them on funded accounts.
2. Run it on Sim101 in real time for 2–4 weeks and compare with the backtest.
3. Use a stable PC or VPS. With `StartBehavior = WaitUntilFlat` and `RealtimeErrorHandling = StopCancelClose`, the bot won't take over positions you entered by hand and will flatten if an order error happens.
4. Each morning, check for news: 10:00 New York releases (ISM, JOLTS, consumer confidence) land right at the window open. Add FOMC/CPI/NFP days to `NoTradeDates` if they hurt your results.

---

## 8. Parameter reference

| Group | Parameter | Default | Meaning |
|---|---|---|---|
| Session | WindowStart / WindowEnd | 1000 / 1100 | Silver Bullet window (HHMM New York) |
| | SweepStart | 930 | Sweeps from this time count as liquidity grabs |
| | FlattenTime | 1200 | Cancel and close everything |
| Bias | BiasMode | HtfEma | Off / HtfEma / HtfEmaAndStructure |
| | EmaPeriod | 14 | Used on both the 15m and 1m charts |
| | EmaSlopeBars | 2 | 15m EMA slope lookback |
| | HtfSwingStrength | 2 | 15m fractal size for the structure filter |
| ICT entry | SwingStrength | 2 | 1m fractal size (2 = 5-bar fractal) |
| | RequireKeyLevelSweep | false | Sweep must also take the overnight, opening-range or previous-day high/low |
| | SweepExpiryBars | 20 | Bars allowed between sweep and MSS |
| | RequireEmaReclaim | true | MSS candle must close beyond the 1m EMA14 |
| | MinDisplacementAtr | 0.5 | MSS candle body ≥ x × ATR14 |
| | MinFvgTicks | 8 | Minimum FVG size |
| | FvgWaitBars | 3 | Bars allowed after MSS for the FVG to print |
| | FvgMaxLookback | 15 | Maximum bars scanned for the FVG |
| | EntryMode | FvgProximal | FvgProximal / FvgMidpoint |
| | EntryExpiryBars | 10 | Cancel the unfilled limit order after this many bars |
| Stops/targets | StopMode | SweepExtreme | SweepExtreme / FvgDistal |
| | StopBufferTicks | 4 | Extra room beyond the stop level |
| | Min/MaxStopPoints | 5 / 30 | Skip trades with a stop outside this range |
| | TargetMode | FixedRR | FixedRR / Liquidity |
| | RewardRisk | 2.0 | Runner target in FixedRR mode |
| | MinRR / MaxRR | 1.5 / 4.0 | Bounds for Liquidity mode |
| | UsePartial / Tp1RR | true / 1.0 | Half off at 1R (needs ≥ 2 contracts) |
| | BreakevenAtR / BreakevenOffsetTicks | 1.0 / 1 | Stop to entry + 1 tick after 1R |
| Funded | RiskPerTradeDollars | 200 | Dollar risk used for position sizing |
| | MaxContracts | 5 | |
| | MaxTradesPerDay / MaxLossesPerDay | 2 / 2 | |
| | StopAfterFirstWin | true | |
| | DailyLossLimitDollars / DailyProfitCapDollars | 400 / 800 | 0 = off |
| | NoTradeDates | (empty) | `yyyy-MM-dd`, comma separated |
| Display | ShadeWindow / DebugPrint | true / true | |

---

## 9. Known limitations

* Runs `OnBarClose`, so decisions happen when a 1m bar closes and orders reach the market a few seconds after the candle closes. This keeps the backtest and live behaviour the same.
* The 15m bias uses the **last closed** 15m candle (no look-ahead), so it lags by up to 15 minutes.
* Only one setup is tracked at a time, and only one direction per setup.
* Logic was checked by compiling against a stub of the NinjaScript API and replaying scripted price paths: sweep → MSS → FVG fill → TP1 → breakeven → target or stop-out, long and short. It has **not** been run inside NinjaTrader yet, so compile it there and test on Sim first.
