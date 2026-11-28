# MNQ CRT Strategy for NinjaTrader 8

This is an automated **Candle Range Theory (CRT)** strategy for **MNQ** (Micro E-mini Nasdaq-100) futures in NinjaTrader 8, on the **4H** candle.
It trades both the buy model and the sell model.

File: [`Strategies/MnqCrtStrategy.cs`](Strategies/MnqCrtStrategy.cs)

A TradingView (Pine Script) version with the same rules is in [`../MNQ-CRT-TradingView`](../MNQ-CRT-TradingView).

---

## 1. How the strategy trades

```
 Candle 1: RANGE (4H)   Candle 2: SWEEP (4H)            Candle 3: ENTRY (5 / 15 min chart)
 (your time box)        must take ONE end and           MSS  ->  limit at FVG midpoint
                        close back inside the range              or breaker block 0.5 level
 ┌──────────┐            ╷  <- sweeps high (sell)
 │  high    │ ─ ─ ─ ─ ─ ─│─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─   stop = candle 2 high
 │          │          ┌─┴─┐
 │   50%    │ ─ ─ ─ ─ ─│   │─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─   TP1 (2 lots) / breakeven trigger
 │          │          │   │ <- closes inside
 │  low     │ ─ ─ ─ ─ ─└───┘─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─   final target (opposite end)
 └──────────┘
```

| Step | Rule |
|---|---|
| Range candle | The 4H candle that starts at your **time box** (start hour, minute, **AM/PM**). Its high and low **include the wicks**. |
| Sweep candle | The next 4H candle must trade beyond **one** end of the range and **close back inside** it. **High swept → SELL**, target = range low. **Low swept → BUY**, target = range high. If the next candle does not sweep, the setup is ignored. If it sweeps both ends or closes outside the range, it is also ignored. |
| Entry candle | During the 4H candle after the sweep, the strategy works on your **5 min or 15 min** chart. The MSS level is the **first 3-candle swing to the left of the sweep point**. After the MSS, it places a **limit order** at the **breaker block 0.5 level**, or at the **first FVG** midpoint if there is no valid breaker (see below). |
| Stop loss | The **previous 4H candle** (the sweep candle) **high for sells / low for buys**, plus an optional tick buffer. |

### MSS and entry levels

```
 SELL example (lower timeframe)
                 sweep point (sweep candle high)
                 ▲
   swing  ▲  ▲  ╱ ╲  ← first FVG after the sweep (premium half of the leg)
          ╱╲╱ ╲╱   ╲
   ──────▼─────────╲───────  MSS level = FIRST 3-candle swing low to the LEFT of the sweep point
   breaker (last down-close     ╲  close below it = MSS
   candle at that swing)          ▼
```

1. **Sweep point:** the lower-timeframe candle that made the sweep candle's high (sell) or low (buy).
2. **MSS level:** the **first 3-candle swing to the left** of the sweep point. For a sell that is a swing low: a candle whose low is below the candles on both sides. For a buy it is a swing high.
3. **MSS:** a candle **closes beyond** that level (below it for a sell, above it for a buy). The MSS can print inside the sweep candle or in the entry candle. The order is only placed during the entry candle.
4. **Entry (limit order):**
   * **Breaker block 0.5 level** comes first. The breaker is the last down-close candle (sells) or up-close candle (buys) at the swing the MSS broke. The 0.5 level is halfway between its high and low, wicks included. **If a breaker and an FVG are both present, the breaker is used.**
   * **First FVG** is used only when there is no valid breaker. It is the **first** FVG of the move from the sweep point to the MSS (the one nearest the sweep). It must sit in the **premium half** of that leg for sells, or the **discount half** for buys. The limit goes at its midpoint.

The **Entry model** setting can limit this to `BreakerOnly` or `FvgOnly`. The default is `FvgOrBreaker` (breaker first, then the first FVG).

Every entry level must also pass these checks:
* It is above 50% of the range for sells (below for buys).
* Price has not already traded back to it.
* The risk fits the lot rules below.

If a level fails a check, it is skipped and the log says why.

### Lot size and exits

Risk per lot = (entry − stop) × $2 per point (MNQ).

| Condition | Lots | Exits |
|---|---|---|
| Combined risk of 2 lots ≤ **$300** (≤ $150 per lot, about 75 points) | **2** | Lot 1 books at **50% of the range**. Lot 2 targets the **opposite end** (range high for buys, range low for sells). After lot 1 is booked, lot 2's stop moves to **breakeven**. |
| Otherwise, risk per lot ≤ **$215** (about 107 points) | **1** | Target = **opposite end** of the range. When price reaches **50%** of the range, the stop moves to **breakeven**. |
| Risk per lot > $215 | — | No trade |

---

## 2. Install in NinjaTrader 8

1. Copy `Strategies/MnqCrtStrategy.cs` to
   `Documents\NinjaTrader 8\bin\Custom\Strategies\`
   If you installed an earlier version, replace the old file.
2. In NinjaTrader: **New → NinjaScript Editor**, open any strategy, and press **F5** to compile.
   Wait for the "compiled successfully" message at the bottom.
3. Open a chart: **MNQ (front month), 5 minute** (or 15 minute) bars.
   * Trading hours: **CME US Index Futures ETH**
   * Days to load: at least **10**
4. Right-click the chart → **Strategies** → add **MnqCrtStrategy**, set the parameters, select account **Sim101** first, tick **Enabled**, click **OK**.
   If the strategy was already on a chart from an earlier version, remove it and add it again, because the settings changed.
5. Open **New → NinjaScript Output** to see the strategy's log (every setup, MSS, FVG, breaker, order, and exit is printed with the reason).

The strategy adds a **1 minute** data series by itself. It uses this series to build your 4H time-box candles exactly and to manage orders minute by minute.
Do **not** set *Order Fill Resolution = High* in the Strategy Analyzer. It is not needed, and NinjaTrader does not allow it for strategies that add a data series.

---

## 3. Settings

### 1. CRT Range (Time Box)

| Setting | Default | Meaning |
|---|---|---|
| Time box time zone | NewYork | Time zone of your time box. NewYork is standard for CME futures. Other choices: Chicago, London, India, or NinjaTraderLocal (your NinjaTrader clock). |
| Range start – hour / minute / AM-PM | 2 : 00 AM | Start of the 4H range candle. The sweep candle is the next 4 hours and the entry candle is the 4 hours after that. Example: **2:00 AM** range → **6–10 AM** sweep → **10 AM–2 PM** entry. |
| Only the time-box candle | true | `true` = only the candle that starts at your time box can be a range. `false` = every 4H candle (grid anchored at your time box) is checked. |

Standard MNQ 4H candles in New York time start at **6 PM, 10 PM, 2 AM, 6 AM, 10 AM and 2 PM**.

### 2. LTF Entry (MSS + FVG / Breaker)

| Setting | Default | Meaning |
|---|---|---|
| Swing strength | 1 | Candles on each side of a swing. **1 = 3-candle swing.** The MSS level is the first swing to the left of the sweep point. |
| Entry model | FvgOrBreaker | `FvgOrBreaker` (breaker 0.5 first, else the first FVG), `FvgOnly`, or `BreakerOnly`. |
| Min FVG size (ticks) | 2 | Smaller gaps are ignored. |
| Stop buffer (ticks) | 0 | Extra ticks beyond the sweep candle high/low. |

### 3. Risk & Lots

| Setting | Default | Meaning |
|---|---|---|
| Max total risk for 2 lots ($) | 300 | Use 2 lots when both lots together risk no more than this. |
| Max risk per lot ($) | 215 | Skip the trade if one lot would risk more. |
| Breakeven offset (ticks) | 0 | 0 = stop exactly at entry. 1–2 ticks covers commission. |
| Cancel if 50% hit before fill | true | If price reaches 50% of the range before the limit fills, the order is cancelled. |
| Allowed direction | Both | Both / LongOnly / ShortOnly |

### 4. Session / 5. Display

| Setting | Default | Meaning |
|---|---|---|
| Flatten before weekend | true | Friday 4:45 PM New York: cancel entries and close any open position. |
| Show drawings | true | Draws the range box, sweep box, MSS level and MSS line, breaker block, first FVG (each with a dashed 0.5 midpoint line), and entry/SL/TP lines, plus a status line at the top-left. |
| Print log | true | Writes every decision to the NinjaScript Output window. |
| CRT range box colour | DodgerBlue | Colour of the range candle box and its high/low lines. |
| Sweep candle box colour | Orange | Colour of the sweep candle box and the CRT label. |
| Box fill opacity (%) | 10 | 0 = outline only, 100 = solid fill. |
| Breaker block colour | MediumPurple | Breaker box and its 0.5 midpoint line. |
| FVG colour | Goldenrod | First-FVG box and its 0.5 midpoint line. |
| Breaker / FVG fill opacity (%) | 25 | Fill of the breaker and FVG boxes. |
| Show 0.5 midpoint lines | true | Dashed line at the 0.5 level of the breaker block and of the FVG. |
| MSS line colour | Magenta | The dashed MSS level (while waiting) and the solid MSS line (after the break). |
| MSS line width | 2 | 1–10. |
| MSS text | MSS | Text written on the MSS line. Leave empty for no text. |
| MSS text size | 11 | Font size of the MSS text. |

---

## 4. Details you should check

Some parts of the rules could be read more than one way. This is how the strategy handles each one. If you meant something different, these are easy to change.

1. **Entry candle.** The order is only placed in the 4H candle **after** the sweep candle, because the sweep candle must first close inside the range to confirm the setup. The MSS itself can already have printed inside the sweep candle (after the sweep point). The stop is the "previous 4H candle" (the sweep candle) high or low.
2. **"Stoploss between 300 usd".** This is read as: 2 lots if the **total** risk of both lots is **≤ $300**. Otherwise 1 lot if risk is ≤ $215. Otherwise no trade.
3. **Breaker block.** The breaker is a single candle: the last down-close candle (sells) or up-close candle (buys) at the swing the MSS broke, looking back at most 5 candles. Its 0.5 level uses the candle's full high-to-low range, wicks included.
4. **Which FVG.** The **first** FVG after the sweep point that lies in the premium half (sells) / discount half (buys) of the sweep → MSS leg. An FVG made by the candle right after the MSS candle still counts as part of the same move. If the first one fails a check (for example price already returned to it), the next one in order is tried.
5. **One MSS per setup.** The MSS level comes from the sweep point, so there is one MSS per setup. If price trades beyond the sweep candle high (sells) or low (buys) before the entry fills, the setup is cancelled.
6. **Unfilled orders.** A limit order that has not filled by the end of the entry candle, or by the end of the trading session, is cancelled.
7. **2-lot breakeven.** Lot 2 moves to breakeven when lot 1's target fills. It also moves if price touches 50% without lot 1's limit filling.
8. **One trade at a time.** A new setup cannot enter while a position from an earlier setup is still open.

---

## 5. Before going live

1. Run it in the **Strategy Analyzer** on several months of MNQ data, and try each **Entry model** setting. Read the Output log for a few trades to confirm each one matches how you would have traded it by hand.
2. Run it in **Playback** (Market Replay) on a few days, with drawings on, to see the boxes, MSS, breaker and FVG on the chart.
3. Run it **live on Sim101** for at least a couple of weeks.
4. Only then consider a live account, starting with the smallest size.

> Disclaimer: This code is for education and research. It is not financial advice. Futures trading carries substantial risk of loss. Past or simulated performance does not guarantee future results.
