# MNQ CRT Strategy for TradingView (Pine Script v6)

This is the TradingView version of the MNQ **4H Candle Range Theory** strategy. It follows the same rules as the NinjaTrader version in [`../MNQ-CRT-NinjaTrader`](../MNQ-CRT-NinjaTrader).

File: [`MnqCrtStrategy.pine`](MnqCrtStrategy.pine)

## Rules

1. **Range candle:** the 4H candle that starts at your time box (hour, minute, **AM/PM**, time zone). Wicks are included.
2. **Sweep candle:** the next 4H candle must trade beyond **one** end of the range and **close back inside**.
   **High swept → SELL**, target = range low. **Low swept → BUY**, target = range high. No sweep → no trade.
3. **Entry candle:** during the 4H candle after the sweep, on the 5 or 15 minute chart:
   1. wait for an **MSS**, then
   2. place a **limit order** at the **FVG midpoint** or at the **breaker block 0.5 level**.
4. **Stop loss:** the sweep candle **high (sells) / low (buys)**.
5. **Lots:**
   * **2 lots** if both lots together risk **≤ $300**. Lot 1 books at **50%** of the range and lot 2 at the **opposite end**. Lot 2 moves to **breakeven** after lot 1 books.
   * **1 lot** if risk is **≤ $215**. Target = opposite end, and the stop moves to **breakeven** when price reaches **50%**.
   * Otherwise **no trade**.

## Add it to TradingView

1. Open an **MNQ** chart (`MNQ1!` or a specific contract) on **5 minute** (or 15 minute) bars.
   Keep the default (full, not regular-hours) session, because the 4H candles need the overnight bars.
2. At the bottom of the screen open **Pine Editor**, delete everything in it, and paste the whole of `MnqCrtStrategy.pine`.
3. Click **Save** (name it e.g. "MNQ CRT"), then **Add to chart**.
4. Open **Strategy Tester** at the bottom to see trades and results.
5. Click the strategy name on the chart → **Settings** (gear icon) to change inputs.
   * In the **Properties** tab, set your **commission** per contract. The script starts at 0.
   * If you have a paid plan, turn on **Bar Magnifier** there for more accurate fills.

**Logs:** the script logs every decision: setup confirmed or ignored, MSS, breaker, FVG skipped (with reason), order placed, cancelled, breakeven, and closed. To see them, open the Pine Editor's **⋯** menu → **Pine Logs**.

**Alerts:** create an alert (clock icon) and choose this strategy as the condition. Pick **"alert() function calls only"** to get messages when:
* a CRT is confirmed,
* a limit order is placed,
* the stop moves to breakeven.

Pick **"Order fills and alert() function calls"** to also get every fill.

> TradingView itself does not send orders to your broker from a strategy. To auto-trade from TradingView you would forward these alerts by webhook to a broker bridge. Since you already have NinjaTrader, the simplest way to automate is to run the NinjaTrader version there and use TradingView for charting and backtesting.

## Settings

| Group | Setting | Default |
|---|---|---|
| CRT Range | Time box time zone | America/New_York |
| | Range start (hour : minute AM/PM) | 2:00 AM (2–6 AM range → 6–10 AM sweep → 10 AM–2 PM entry) |
| | Only the time-box candle | On (Off = check every 4H candle) |
| LTF Entry | Swing strength | 2 bars each side |
| | MSS mode | Strict shift (Any break also allowed) |
| | Entry model | FVG or Breaker (also FVG only / Breaker only) |
| | If FVG and breaker are both valid | Nearest to price (or Best price) |
| | Min FVG size | 2 ticks |
| | Stop buffer | 0 ticks |
| Risk & Lots | Max total risk for 2 lots | $300 |
| | Max risk per lot | $215 |
| | Breakeven offset | 0 ticks |
| | Cancel if 50% hit before fill | On |
| | Allowed direction | Both |
| Session / Display | Flatten before weekend (Fri 4:45 PM New York) | On |
| | Show drawings / status table | On |

**Chart colours:**
* Blue box: range candle. Orange box: sweep candle.
* Blue lines: range high and low. Grey dashed line: 50%.
* Pink line: MSS. Purple box: breaker block. Orange zone: FVG used for the entry.
* Aqua line: entry. Red line: stop. Green lines: targets.

## Differences from the NinjaTrader version

| | NinjaTrader | TradingView |
|---|---|---|
| 4H candles and trade management | Built from a 1 minute series | Built from your chart bars (5 or 15 min) |
| "Price reached 50%" breakeven check | Every 1 minute | At each chart bar close |
| Fills in backtests | NinjaTrader fill engine on 1 minute bars | TradingView's broker emulator on chart bars (Bar Magnifier improves this) |

The rules themselves are the same: setup, MSS, breaker / FVG, lot sizing, targets, breakeven, cancellations and the Friday cut-off.

> Disclaimer: This code is for education and research. It is not financial advice. Futures trading carries substantial risk of loss.
