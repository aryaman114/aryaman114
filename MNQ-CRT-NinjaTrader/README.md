# MNQ CRT Strategy for NinjaTrader 8

This is an automated **Candle Range Theory (CRT)** strategy for **MNQ** (Micro E-mini Nasdaq-100) futures in NinjaTrader 8.
It trades both the buy model and the sell model.

File: [`Strategies/MnqCrtStrategy.cs`](Strategies/MnqCrtStrategy.cs)

---

## 1. How the strategy trades

```
 Candle 1: RANGE        Candle 2: SWEEP                 Candle 3: ENTRY (lower timeframe)
 (your time box)        must take ONE end and           MSS  ->  FVG  ->  limit order at FVG midpoint
                        close back inside the range
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
| Range candle | The higher-timeframe candle that starts at your **time box** (start hour, minute, **AM/PM**). Its high and low **include the wicks**. |
| Sweep candle | The next candle must trade beyond **one** end of the range and **close back inside** it. **High swept → SELL**, target = range low. **Low swept → BUY**, target = range high. If the next candle does not sweep, the setup is ignored. If it sweeps both ends or closes outside the range, it is also ignored. |
| Entry candle | During the candle after the sweep, the strategy works on the **lower timeframe** (your chart): it waits for an **MSS** (market structure shift), then finds an **FVG** (fair value gap) in the MSS leg, then places a **limit order at the FVG midpoint**. |
| Stop loss | The **previous higher-timeframe candle** (the sweep candle) **high for sells / low for buys**, plus an optional tick buffer. |
| Timeframes | 4H range → chart on **5 min or 15 min**. 1 Day range → chart on **60 min**. |

### Lot size and exits

Risk per lot = (entry − stop) × $2 per point (MNQ).

| Range | Condition | Lots | Exits |
|---|---|---|---|
| 4H | Combined risk of 2 lots ≤ **$300** (≤ $150 per lot, about 75 points) | **2** | Lot 1 books at **50% of the range**. Lot 2 targets the **opposite end** (range high for buys, range low for sells). After lot 1 is booked, lot 2's stop moves to **breakeven**. |
| 4H | Otherwise, risk per lot ≤ **$215** (about 107 points) | **1** | Target = **opposite end** of the range. When price reaches **50%** of the range, the stop moves to **breakeven**. |
| 4H | Risk per lot > $215 | — | No trade |
| 1 Day | Risk per lot ≤ $215 | **1** | Books the whole lot at **50%** of the range. |

---

## 2. Install in NinjaTrader 8

1. Copy `Strategies/MnqCrtStrategy.cs` to
   `Documents\NinjaTrader 8\bin\Custom\Strategies\`
2. In NinjaTrader: **New → NinjaScript Editor**, open any strategy, and press **F5** to compile.
   Wait for the "compiled successfully" sound or message at the bottom.
3. Open a chart: **MNQ (front month), Minute bars**:
   * 4H mode: **5 minute** (or 15 minute)
   * Daily mode: **60 minute**
   * Trading hours: **CME US Index Futures ETH**
   * Days to load: at least **10** (daily mode: **20+**)
4. Right-click the chart → **Strategies** → add **MnqCrtStrategy**, set the parameters, select account **Sim101** first, tick **Enabled**, click **OK**.
5. Open **New → NinjaScript Output** to see the strategy's log (every setup, MSS, FVG, order, and exit is printed with the reason).

The strategy adds a **1 minute** data series by itself. It uses this series to build your time-box candles exactly and to manage orders minute by minute.
Do **not** set *Order Fill Resolution = High* in the Strategy Analyzer. It is not needed, and NinjaTrader does not allow it for strategies that add a data series.

---

## 3. Settings

### 1. CRT Range (Time Box)

| Setting | Default | Meaning |
|---|---|---|
| Range timeframe | FourHour | `FourHour` or `Daily` |
| Time box time zone | NewYork | Time zone of your time box. NewYork is standard for CME futures. Other choices: Chicago, London, India, or NinjaTraderLocal (your NinjaTrader clock). |
| 4H range start – hour / minute / AM-PM | 2 : 00 AM | Start of the 4H range candle. The sweep candle is the next 4 hours and the entry candle is the 4 hours after that. Example: **2:00 AM** range → **6–10 AM** sweep → **10 AM–2 PM** entry. |
| 4H: only the time-box candle | true | `true` = only the candle that starts at your time box can be a range. `false` = every 4H candle (grid anchored at your time box) is checked. |
| Daily candle open – hour / minute / AM-PM | 6 : 00 PM | When the daily candle opens. CME futures: 6:00 PM New York. |

Standard MNQ 4H candles in New York time start at **6 PM, 10 PM, 2 AM, 6 AM, 10 AM and 2 PM**.

### 2. LTF Entry (MSS + FVG)

| Setting | Default | Meaning |
|---|---|---|
| Swing strength | 2 | Bars on each side that define a lower-timeframe swing high/low. |
| MSS mode | StrictShift | `StrictShift`: the break must **flip** structure. For a sell, the lower timeframe must have been bullish, then a candle **closes below** the last swing low. `AnyBreak`: any close beyond the last swing counts, including a trend continuation (BOS). |
| Min FVG size (ticks) | 2 | Smaller gaps are ignored. |
| Stop buffer (ticks) | 0 | Extra ticks beyond the sweep candle high/low. |

### 3. Risk & Lots

| Setting | Default | Meaning |
|---|---|---|
| Max total risk for 2 lots ($) | 300 | 4H: use 2 lots when both lots together risk no more than this. |
| Max risk per lot ($) | 215 | Skip the trade if one lot would risk more. |
| Breakeven offset (ticks) | 0 | 0 = stop exactly at entry. 1–2 ticks covers commission. |
| Cancel if 50% hit before fill | true | If price reaches 50% of the range before the limit fills, the order is cancelled. |
| Allowed direction | Both | Both / LongOnly / ShortOnly |

### 4. Session / 5. Display

| Setting | Default | Meaning |
|---|---|---|
| Flatten before weekend | true | Friday 4:45 PM New York: cancel entries and close any open position. |
| Show drawings | true | Draws the range box, sweep box, MSS line, FVG zone, and entry/SL/TP lines, plus a status line at the top-left. |
| Print log | true | Writes every decision to the NinjaScript Output window. |

---

## 4. Details you should check

Some parts of the rules could be read more than one way. This is how the strategy handles each one. If you meant something different, these are easy to change.

1. **Entry candle.** The MSS and FVG are searched in the candle **after** the sweep candle, because the sweep candle must first close inside the range to confirm the setup. That is why the stop is the "previous higher-timeframe candle" (the sweep candle) high or low.
2. **"Stoploss between 300 usd".** This is read as: 2 lots if the **total** risk of both lots is **≤ $300**. Otherwise 1 lot if risk is ≤ $215. Otherwise no trade.
3. **Which FVG.** The strategy uses the most recent FVG in the MSS leg (from the leg's extreme to now) that price has not yet returned to the midpoint of. If no FVG exists at the moment of the MSS, it keeps looking at new FVGs until the entry candle ends. The FVG midpoint must be above 50% of the range for sells (below for buys), otherwise TP1 would be on the wrong side.
4. **Invalidation.** If price trades beyond the sweep candle high (sells) or low (buys) before the entry fills, the setup is cancelled. A new extreme beyond the MSS leg resets the search and waits for a new MSS.
5. **Unfilled orders.** A limit order that has not filled by the end of the entry candle, or by the end of the trading session, is cancelled.
6. **2-lot breakeven.** Lot 2 moves to breakeven when lot 1's target fills. It also moves if price touches 50% without lot 1's limit filling.
7. **Daily mode.** Daily mode takes 1 lot and books at 50% with no breakeven step. On MNQ, the previous day's high or low is often more than 107 points (more than $215) from the entry, so **daily mode will skip many setups** with the default $215 cap.
8. **One trade at a time.** A new setup cannot enter while a position from an earlier setup is still open.

---

## 5. Before going live

1. Run it in the **Strategy Analyzer** on several months of MNQ data, in both modes, and read the Output log for a few trades to confirm each one matches how you would have traded it by hand.
2. Run it in **Playback** (Market Replay) on a few days, with drawings on, to see the boxes, MSS, and FVG on the chart.
3. Run it **live on Sim101** for at least a couple of weeks.
4. Only then consider a live account, starting with the smallest size.

> Disclaimer: This code is for education and research. It is not financial advice. Futures trading carries substantial risk of loss. Past or simulated performance does not guarantee future results.
