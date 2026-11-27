# ICT 10 AM Silver Bullet: NQ strategy for TradingView

A Pine Script v6 **strategy** for Nasdaq-100 futures (`NQ1!` / `MNQ1!`). It combines:

- **ICT market structure**: a higher-timeframe bias, plus a market structure shift (MSS) on the entry timeframe
- **A liquidity sweep**: a raid on resting buy-side or sell-side liquidity
- **The 10 AM Silver Bullet**: entry from a fair value gap (FVG) formed between 10:00 and 11:00 New York time

File: [`ict_10am_silver_bullet_nq.pine`](./ict_10am_silver_bullet_nq.pine)

---

## 1. The trading model

All times are **New York time**. The script uses `America/New_York` internally, so it works whatever time zone your chart is set to.

### Long setup (short is the mirror image)

| Step | Rule | What the script checks |
|---|---|---|
| **1. Bias** | Only buy when higher-timeframe structure is bullish | On the HTF (default 1H), the last close-through of a pivot was *up* (a bullish BOS). Only closed HTF bars are used, so it doesn't repaint. |
| **2. Liquidity** | Know where the sell-side liquidity rests | Lows of the previous day (PDL), the Asia session (20:00–00:00), London (02:00–05:00) and NY pre-open (08:30–10:00), plus the last few un-swept swing lows |
| **3. Sweep** | Price raids a sell-side pool between **09:30 and 11:00** | `low < pool`. With *strict sweep* on, the candle also has to close back above the pool. |
| **4. MSS** | Price displaces back up and **closes above the last internal swing high** | The close breaks the most recent internal pivot high (length 2), and the candle body is ≥ 1.0 × ATR(14) (the displacement check) |
| **5. FVG** | A bullish FVG forms in that up-leg **inside 10:00–11:00** | `low[0] − high[2] ≥ 4 ticks`, created after the sweep and not yet fully filled |
| **6. Entry** | Limit order inside the FVG | Default is the FVG midpoint (consequent encroachment, "CE"). You can also pick the near edge or the far edge. |
| **7. Stop** | Below the sweep low | The lowest point of the sweep minus 4 ticks, or below FVG candle 1. Minimum stop 5 pts; setups needing more than 40 pts are skipped. |
| **8. Target** | 2R, or the nearest buy-side liquidity | Fixed R:R, or the nearest un-swept high that is at least 1.5R away |
| **9. Management** | Protect and close out | Stop moves to breakeven at +1R. An unfilled order is cancelled at 11:00. Anything still open is closed at 12:00. Max 1 trade per day. |

### Example sequence

```
 09:58  NY pre-open low (08:30–10:00 range) is set
 10:07  price wicks below it          → "SSL sweep" label        (sell-side liquidity taken)
 10:12  big bullish candle closes above the last minor high → "MSS" dashed line
 10:12  that leg left a bullish FVG   → blue FVG box
 10:12  BUY LIMIT at FVG 50%, SL below the 10:07 low, TP = 2R    (green/red boxes)
 10:16  price retraces into the FVG → filled
 10:31  +1R → stop to breakeven
 10:44  TP hit
```

---

## 2. Install in TradingView

1. Open a chart of **`NQ1!`** (or **`MNQ1!`** for micros) on the **1-minute** timeframe.
2. Open **Pine Editor** at the bottom of the screen and choose **Open → New strategy**.
3. Delete the template code. Paste the whole contents of `ict_10am_silver_bullet_nq.pine`.
4. Click **Save** (give it a name), then **Add to chart**.
5. Open the **Strategy Tester** tab to see trades, the equity curve and stats.
6. Click the ⚙️ on the strategy name to change inputs. They're described below.

### Alerts (optional)

Right-click the chart → **Add alert** → *Condition*: `ICT SB 10AM` → **"alert() function calls only"**. You'll get a message whenever a limit order is placed, with entry, SL, TP, size and which pool was swept. The strategy's own fill alerts ("order fills") also work.

---

## 3. Inputs

### Sessions
| Input | Default | Notes |
|---|---|---|
| Silver Bullet entry window | `1000-1100` | Orders are only placed inside this window. An unfilled order is cancelled when it ends. |
| Liquidity sweep window | `0930-1100` | Lets the 09:30 open raid count as the sweep, as long as the FVG still forms after 10:00 |
| Force-flat window | `1200-1700` | Closes any open trade at 12:00. Widen it if you want to hold for bigger targets. |
| Max trades per day | 1 | Classic Silver Bullet is one shot per day |

### Liquidity pools
PDH/PDL, Asia, London, NY pre-SB range and swing pivots can each be switched on or off. The session times can be edited. Each pool is drawn as a dotted line. Once it's swept, the line becomes solid and its label reads "swept".

### Market structure shift
- **Internal swing length** (2): the pivot size used for the MSS. Smaller means faster, more frequent shifts.
- **Max bars from sweep to MSS** (30): if no shift happens within this many bars, the setup is dropped.
- **Displacement** (1.0 × ATR): how strong the breaking candle's body must be. Lower it to 0.6–0.8 for more trades. Raise it to 1.3+ for only the most violent shifts.

### FVG entry
- **Entry price**: near edge (first touch, fills most often), CE/50% (default), or far edge (best price, fills least often).
- **Min FVG size** (4 ticks = 1 NQ point).
- **FVG must form inside the SB window**: the pure Silver Bullet rule. Turn it off to also take FVGs that formed between 09:30 and 10:00.

### Risk & exits
- **Stop**: beyond the sweep extreme (safer) or beyond FVG candle 1 (tighter, higher R).
- **Target**: fixed R:R, or opposing liquidity (the next PDH / Asia high / swing high, and so on). The liquidity target falls back to the fixed R:R if no pool is at least `Min R:R` away.
- **Sizing**: fixed contracts, or **Risk $ per trade**, which calculates contracts from the stop distance and the contract's point value (NQ = $20/pt, MNQ = $2/pt).

### Bias filters
- **HTF market-structure bias** (on, 60 min): longs only in bullish HTF structure, shorts only in bearish. Try 15 min for more trades, or 240 min for stricter bias.
- **Premium/discount vs midnight open** (off): buy only below the 00:00 open, sell only above it. This is a classic ICT filter that you can switch on.

---

## 4. Suggested starting configurations

| Style | Changes from default |
|---|---|
| **Conservative** | Strict sweep ON, Displacement 1.2, Premium/discount ON, Target = Opposing liquidity, Min R:R 2 |
| **Default** | As shipped |
| **More trades** | Displacement 0.7, HTF = 15, FVG must form in window OFF, Entry = near edge |
| **Micro account** | Symbol `MNQ1!`, Sizing = Risk $ per trade, Risk $ = 100 |

On a **3- or 5-minute chart**, reduce the bar-count settings. For example: Max bars to MSS 10, Bars to wait for FVG 3, swing pivot length 3. Also raise Min FVG size to about 8 ticks, because gaps on higher timeframes are larger.

---

## 5. Backtesting notes and limitations

- **Data depth**: TradingView keeps a limited history of 1-minute bars (it depends on your plan). For multi-month 1-minute tests, use **Deep Backtesting** (Premium), or test on 3–5 minute bars.
- **Realistic costs**: the defaults assume $2.50 commission per contract per side and 2 ticks of slippage. Adjust these in *Properties*.
- **Limit fills**: TradingView fills a limit when price *touches* it. For a stricter test, set *Properties → Verify price for limit orders* to 1–2 ticks.
- **Bar Magnifier** (Premium): turn it on in *Properties* so the order of fills inside a bar (stop vs target) is simulated more accurately.
- **Rolls**: `NQ1!` is a continuous contract. Back-adjusted data (`NQ1!` with "back-adjust" enabled in chart settings) gives cleaner PDH/PDL around roll dates.
- **No repainting**: HTF bias and PDH/PDL use only completed bars. Pivots confirm with a lag of `length` bars, as they would in real time. Orders are decided on bar close.
- The rules are a mechanical reading of a discretionary method. Expect it to differ from how any one trader reads the chart. Use the **"Label skipped setups (debug)"** option to see *why* a sweep didn't become a trade.

> ⚠️ **Disclaimer**: this is for education and research only, not financial advice. Futures trading carries substantial risk. Forward-test on a paper account before risking real money.
