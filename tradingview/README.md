# ICT Market Structure (TradingView, Pine Script v6)

One overlay indicator for ICT / SMC market structure. Everything runs on the chart's own timeframe, and every feature has its own on/off toggle in the settings.

## How to add it to TradingView
1. Open a chart and go to **Pine Editor** (bottom panel).
2. Paste the contents of `ICT_Market_Structure.pine`, click **Save**, then **Add to chart**.

## Features and rules

| Feature | Rule |
|---|---|
| **Swing High / Low** | Pivot with *N* bars on each side (default 5). Labeled HH / LH / HL / LL. |
| **BOS** | A candle **closes** beyond the latest swing in the direction of the trend. Solid line. |
| **CHoCH** | The first candle **close** beyond a swing *against* the trend. Dashed line. |
| **MSS** | A CHoCH with displacement. Either the break candle's body is ≥ 1.5 × ATR(14), or the break leaves an FVG (selectable). Thick line, own colors. |
| **IDM** | The first pullback swing after a BOS (option: after CHoCH/MSS too). Dotted line, labeled **IDM** when a wick sweeps it. |
| **FVG** | 3-candle imbalance with an optional ATR size filter. Extend modes: **Until Tapped**, **Until Session End**, **Until Day End**. |
| **FVG / IFVG Show filter** | *All*, *First & Last of Swing*, *First of Swing* or *Last of Swing*, set separately for FVG and IFVG. A swing is the leg between two consecutive confirmed swing points. The filter only hides boxes: hidden FVGs can still turn into IFVGs. |
| **IFVG** | A candle **closes** fully through an FVG, so the FVG flips bias. Same three extend modes. It also ends if price closes back through it. |
| **Swing BSL / SSL** | Dashed line at each swing high / low, stopped and marked ✕ when swept. |
| **EQH / EQL** | Two swing highs / lows within the tolerance (default 0.1 × ATR). |
| **PDH / PDL, PWH / PWL** | Previous day / week high and low (in the selected timezone). Turns dotted and is marked ✕ once swept. |
| **Session High / Low** | High and low of each killzone, extended until swept. |
| **Sessions / Killzones** | Background shading. Each session has its own toggle, name, color, and start/end time entered as hour : minute + AM/PM. |

### Default killzones (New York time)
| Session | Start | End |
|---|---|---|
| Asia | 8:00 PM | 12:00 AM |
| London | 2:00 AM | 5:00 AM |
| NY AM | 9:30 AM | 11:00 AM |
| NY Lunch | 12:00 PM | 1:00 PM |
| NY PM | 1:30 PM | 4:00 PM |

You can change the timezone in the settings (New York, Chicago, London, Berlin, Dubai, Kolkata, Singapore, Tokyo, Sydney, UTC or the exchange's own).

## Notes
- **Until Session End**: an FVG ends when the current enabled killzone finishes. If it formed outside a killzone, it ends when the next one finishes. On daily and higher charts it falls back to day end.
- **Until Tapped**: after a tap the FVG box stops growing, but the indicator still watches it. A later close through it can still create an IFVG.
- Swings are confirmed *N* bars after the pivot, so swing labels appear with that delay. Structure breaks and labels never repaint once the candle closes.
