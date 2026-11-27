// =====================================================================================
//  EMA14 + ICT Silver Bullet (NY AM) strategy for MNQ - NinjaTrader 8 (NinjaScript)
// -------------------------------------------------------------------------------------
//  Idea in one line:
//    15-min EMA14 gives the direction, the 1-min ICT sequence gives the entry:
//    liquidity sweep -> market structure shift (with EMA14 reclaim) -> FVG retrace entry,
//    only inside the New York AM Silver Bullet window (10:00-11:00 New York time).
//
//  Chart / data setup:
//    * Instrument : MNQ (front month), works on NQ / MES / ES too (sizing uses point value)
//    * Primary    : 1-minute bars  (the entry timeframe)
//    * Secondary  : 15-minute bars (added automatically, used for the EMA14 bias)
//    * Trading hours template: "CME US Index Futures ETH" (needed for overnight levels)
//    * All session times are New York time; the script converts from your NinjaTrader
//      time zone automatically, so it works from any country.
//
//  Compatible with plain C# 5 syntax on purpose (no string interpolation etc.).
//  This is an educational template, not financial advice. Backtest and sim-trade it first.
// =====================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// Enums live outside the strategy namespace (same pattern as NinjaTrader's own samples)
// so they show up as drop-downs in the strategy properties window.
public enum Ema14SBBiasMode
{
    Off,                // trade both directions, no higher-timeframe filter
    HtfEma,             // 15m close vs EMA14 and EMA14 slope
    HtfEmaAndStructure  // HtfEma + 15m market structure (last BOS) must agree
}

public enum Ema14SBEntryMode
{
    FvgProximal,        // first touch of the FVG (more fills)
    FvgMidpoint         // consequent encroachment / 50% of the FVG (better R:R, fewer fills)
}

public enum Ema14SBStopMode
{
    SweepExtreme,       // beyond the low/high that swept liquidity (classic ICT)
    FvgDistal           // beyond the far edge of the FVG (tighter, more stop-outs)
}

public enum Ema14SBTargetMode
{
    FixedRR,            // runner target = entry +/- RewardRisk * risk
    Liquidity           // nearest opposing liquidity pool that pays at least MinRR (capped at MaxRR)
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Ema14SilverBulletMNQ : Strategy
    {
        private enum SetupPhase { Idle, Swept, WaitingFvg, OrderWorking, InTrade }

        private const int HtfMinutes = 15; // AddDataSeries() arguments must be hard-coded

        #region Private state
        // Indicators
        private EMA emaEntry;
        private EMA emaHtf;
        private ATR atrEntry;

        // Time
        private TimeZoneInfo nyTz;
        private TimeZoneInfo platformTz;
        private TimeSpan windowStartTod, windowEndTod, sweepStartTod, flattenTod;
        private static readonly TimeSpan RthOpen = new TimeSpan(9, 30, 0);
        private static readonly TimeSpan RthClose = new TimeSpan(16, 0, 0);
        private static readonly TimeSpan GlobexOpen = new TimeSpan(18, 0, 0);
        private HashSet<DateTime> noTradeDates;
        private SolidColorBrush windowBrush;

        // Entry-timeframe swing points (fractals)
        private double lastSwingHigh = double.NaN;
        private double lastSwingLow = double.NaN;
        private bool lastSwingHighSwept;   // wick traded through it (liquidity taken)
        private bool lastSwingLowSwept;
        private bool lastSwingHighBroken;  // a candle CLOSED through it (structure broken)
        private bool lastSwingLowBroken;

        // Higher-timeframe structure
        private double htfSwingHigh = double.NaN;
        private double htfSwingLow = double.NaN;
        private int htfTrend; // +1 bullish BOS last, -1 bearish BOS last, 0 unknown

        // Session liquidity levels (New York time)
        private double onHigh = double.NaN, onLow = double.NaN;     // overnight 18:00 -> 09:30
        private double orHigh = double.NaN, orLow = double.NaN;     // 09:30 -> window start
        private double rthHigh = double.NaN, rthLow = double.NaN;   // today's RTH high/low so far
        private double pdHigh = double.NaN, pdLow = double.NaN;     // previous RTH day high/low
        private bool inOvernight, inRth;

        // Daily bookkeeping
        private DateTime currentDay = DateTime.MinValue;
        private int tradesToday, winsToday, lossesToday;
        private double cumProfitAtDayStart;
        private bool skipToday;

        // Current setup
        private SetupPhase phase = SetupPhase.Idle;
        private int setupDir;           // +1 long, -1 short
        private double sweepExtreme;
        private int sweepBar;
        private bool keyLevelSwept;
        private int mssBar;

        // Current order / trade
        private int tradeDir;
        private double plannedEntry, plannedStop, plannedTarget, plannedTp1, plannedRisk;
        private int orderBar;
        private Order entryOrder1;      // partial (TP1) leg
        private Order entryOrder2;      // runner leg
        private string sigTp1 = string.Empty;
        private string sigRun = string.Empty;
        private bool entryFilled;
        private bool movedToBreakeven;
        private double cumProfitAtEntry;
        private int drawId;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "EMA14 bias + ICT Silver Bullet (sweep -> MSS -> FVG) for MNQ, NY AM 10:00-11:00.";
                Name = "Ema14SilverBulletMNQ";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.UniqueEntries;   // TP1 leg + runner leg
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;                    // conservative backtest fills
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 1;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Day;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 30;
                IsInstantiatedOnEachOptimizationIteration = true;

                // 1. Session (New York time)
                WindowStart = 1000;
                WindowEnd = 1100;
                SweepStart = 930;
                FlattenTime = 1200;

                // 2. Bias
                BiasMode = Ema14SBBiasMode.HtfEma;
                EmaPeriod = 14;
                EmaSlopeBars = 2;
                HtfSwingStrength = 2;

                // 3. ICT structure / entry
                SwingStrength = 2;
                RequireKeyLevelSweep = false;
                SweepExpiryBars = 20;
                RequireEmaReclaim = true;
                MinDisplacementAtr = 0.5;
                MinFvgTicks = 8;
                FvgWaitBars = 3;
                FvgMaxLookback = 15;
                EntryMode = Ema14SBEntryMode.FvgProximal;
                EntryExpiryBars = 10;

                // 4. Stops and targets
                StopMode = Ema14SBStopMode.SweepExtreme;
                StopBufferTicks = 4;
                MinStopPoints = 5;
                MaxStopPoints = 30;
                TargetMode = Ema14SBTargetMode.FixedRR;
                RewardRisk = 2.0;
                MinRR = 1.5;
                MaxRR = 4.0;
                UsePartial = true;
                Tp1RR = 1.0;
                BreakevenAtR = 1.0;
                BreakevenOffsetTicks = 1;

                // 5. Funded account guard rails
                RiskPerTradeDollars = 200;
                MaxContracts = 5;
                MaxTradesPerDay = 2;
                MaxLossesPerDay = 2;
                StopAfterFirstWin = true;
                DailyLossLimitDollars = 400;
                DailyProfitCapDollars = 800;
                NoTradeDates = string.Empty;

                // 6. Display / debug
                ShadeWindow = true;
                DebugPrint = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(Data.BarsPeriodType.Minute, HtfMinutes);
            }
            else if (State == State.DataLoaded)
            {
                emaEntry = EMA(BarsArray[0], EmaPeriod);
                emaHtf = EMA(BarsArray[1], EmaPeriod);
                atrEntry = ATR(BarsArray[0], 14);
                AddChartIndicator(emaEntry);

                windowStartTod = Hhmm(WindowStart);
                windowEndTod = Hhmm(WindowEnd);
                sweepStartTod = Hhmm(SweepStart);
                flattenTod = Hhmm(FlattenTime);

                try { nyTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch { nyTz = TimeZoneInfo.Local; }
                try { platformTz = Core.Globals.GeneralOptions.TimeZoneInfo; }
                catch { platformTz = TimeZoneInfo.Local; }
                if (platformTz == null)
                    platformTz = TimeZoneInfo.Local;

                noTradeDates = new HashSet<DateTime>();
                if (!string.IsNullOrWhiteSpace(NoTradeDates))
                {
                    foreach (string part in NoTradeDates.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        DateTime d;
                        if (DateTime.TryParseExact(part.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                            noTradeDates.Add(d.Date);
                    }
                }

                windowBrush = new SolidColorBrush(Color.FromArgb(28, 30, 144, 255));
                windowBrush.Freeze();
            }
            else if (State == State.Realtime)
            {
                // Hand historical order references over to their live counterparts.
                if (entryOrder1 != null)
                    entryOrder1 = GetRealtimeOrder(entryOrder1);
                if (entryOrder2 != null)
                    entryOrder2 = GetRealtimeOrder(entryOrder2);
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                UpdateHtfStructure();
                return;
            }
            if (BarsInProgress != 0)
                return;

            if (CurrentBars[0] < Math.Max(BarsRequiredToTrade, 2 * SwingStrength + 3)
                || CurrentBars[1] < EmaPeriod + EmaSlopeBars + 1)
                return;

            DateTime ny = ToNewYork(Time[0]);
            TimeSpan tod = ny.TimeOfDay;     // bar CLOSE time, New York

            HandleNewDay(ny);
            UpdateSessionLevels(tod);

            bool sweptLowNow, sweptHighNow;
            UpdateSwings(out sweptLowNow, out sweptHighNow);

            bool inWindow = tod > windowStartTod && tod < windowEndTod;
            bool inSweepTime = tod > sweepStartTod && tod < windowEndTod;

            if (ShadeWindow && tod > windowStartTod && tod <= windowEndTod)
                BackBrush = windowBrush;

            // ---- 1. Book-keep a finished trade ------------------------------------------
            if (entryFilled && Position.MarketPosition == MarketPosition.Flat
                && entryOrder1 == null && entryOrder2 == null)
            {
                FinishTrade();
            }

            if (tod >= flattenTod && tod < GlobexOpen)
            {
                // ---- 2. Hard time exit ---------------------------------------------------
                CancelEntryOrders();
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("Time exit", string.Empty);
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("Time exit", string.Empty);
                if (phase != SetupPhase.InTrade)
                    ResetSetup(null);
            }
            else
            {
                // ---- 3. Manage working entry orders / open trade ------------------------
                ManageOrdersAndTrade(tod);

                // ---- 4. Look for a new setup ---------------------------------------------
                bool allowLong, allowShort;
                GetBias(out allowLong, out allowShort);

                if (phase == SetupPhase.Idle && inSweepTime && CanArmNewSetup())
                {
                    if (allowLong && sweptLowNow)
                        ArmSweep(1);
                    else if (allowShort && sweptHighNow)
                        ArmSweep(-1);
                }

                if (phase == SetupPhase.Swept)
                    ProcessSwept(inWindow, allowLong, allowShort);

                if (phase == SetupPhase.WaitingFvg)
                    ProcessWaitingFvg(inWindow);
            }

            // ---- 5. Mark swing levels that a candle body closed through ----------------
            UpdateStructureBreaks();
        }

        #region Setup state machine

        private void ArmSweep(int dir)
        {
            setupDir = dir;
            sweepExtreme = dir > 0 ? Low[0] : High[0];
            sweepBar = CurrentBar;
            keyLevelSwept = TookKeyLevel(dir, sweepExtreme);
            phase = SetupPhase.Swept;
            drawId++;

            if (dir > 0)
                Draw.ArrowUp(this, "SB_sweep_" + drawId, false, 0, Low[0] - 2 * TickSize, Brushes.LimeGreen);
            else
                Draw.ArrowDown(this, "SB_sweep_" + drawId, false, 0, High[0] + 2 * TickSize, Brushes.OrangeRed);

            LogDecision(string.Format("{0} liquidity swept at {1} (key level: {2})",
                dir > 0 ? "Sell-side" : "Buy-side", sweepExtreme, keyLevelSwept));
        }

        private void ProcessSwept(bool inWindow, bool allowLong, bool allowShort)
        {
            int dir = setupDir;

            // Keep tracking the extreme of the sweep leg.
            if (dir > 0)
                sweepExtreme = Math.Min(sweepExtreme, Low[0]);
            else
                sweepExtreme = Math.Max(sweepExtreme, High[0]);
            if (!keyLevelSwept)
                keyLevelSwept = TookKeyLevel(dir, sweepExtreme);

            if (CurrentBar - sweepBar > SweepExpiryBars)
            {
                ResetSetup("sweep expired without MSS");
                return;
            }
            if ((dir > 0 && !allowLong) || (dir < 0 && !allowShort))
            {
                ResetSetup("bias no longer agrees");
                return;
            }
            if (!inWindow)
                return; // MSS + entry must happen inside the Silver Bullet window

            // MSS level = latest swing point on the other side that no candle has closed through yet.
            double structure = dir > 0 ? lastSwingHigh : lastSwingLow;
            bool structureIntact = dir > 0 ? !lastSwingHighBroken : !lastSwingLowBroken;
            if (double.IsNaN(structure) || !structureIntact)
                return;
            if (dir > 0 && structure <= sweepExtreme)
                return;
            if (dir < 0 && structure >= sweepExtreme)
                return;

            bool brokeStructure = dir > 0 ? Close[0] > structure : Close[0] < structure;
            bool emaOk = !RequireEmaReclaim || (dir > 0 ? Close[0] > emaEntry[0] : Close[0] < emaEntry[0]);
            bool displacement = dir * (Close[0] - Open[0]) >= MinDisplacementAtr * atrEntry[0];
            bool keyOk = !RequireKeyLevelSweep || keyLevelSwept;

            if (brokeStructure && emaOk && displacement && keyOk)
            {
                phase = SetupPhase.WaitingFvg;
                mssBar = CurrentBar;
                if (dir > 0)
                    Draw.Text(this, "SB_mss_" + drawId, "MSS", 0, High[0] + 4 * TickSize);
                else
                    Draw.Text(this, "SB_mss_" + drawId, "MSS", 0, Low[0] - 4 * TickSize);
                LogDecision(string.Format("{0} MSS: close {1} broke structure {2}, EMA14 {3:0.00}",
                    dir > 0 ? "Bullish" : "Bearish", Close[0], structure, emaEntry[0]));
            }
        }

        private void ProcessWaitingFvg(bool inWindow)
        {
            int dir = setupDir;

            bool invalid = dir > 0 ? Low[0] < sweepExtreme : High[0] > sweepExtreme;
            if (invalid)
            {
                ResetSetup("price ran through the sweep extreme");
                return;
            }
            if (CurrentBar - mssBar > FvgWaitBars)
            {
                ResetSetup("no valid FVG after MSS");
                return;
            }
            if (!inWindow)
            {
                ResetSetup("window closed before entry");
                return;
            }

            double proximal, distal;
            int fvgBarsAgo;
            if (!FindFvg(dir, out proximal, out distal, out fvgBarsAgo))
                return; // keep waiting a few bars, the FVG may complete on the next candle

            double entry = EntryMode == Ema14SBEntryMode.FvgProximal ? proximal : (proximal + distal) / 2.0;
            entry = Instrument.MasterInstrument.RoundToTickSize(entry);

            double stopRef = StopMode == Ema14SBStopMode.SweepExtreme ? sweepExtreme : distal;
            double stop = Instrument.MasterInstrument.RoundToTickSize(stopRef - dir * StopBufferTicks * TickSize);
            double risk = dir * (entry - stop);

            if (risk < MinStopPoints || risk > MaxStopPoints)
            {
                ResetSetup(string.Format("stop distance {0:0.00} pts outside [{1}, {2}]", risk, MinStopPoints, MaxStopPoints));
                return;
            }

            double target = ComputeTarget(dir, entry, risk);
            double tp1 = Instrument.MasterInstrument.RoundToTickSize(entry + dir * Tp1RR * risk);

            double pointValue = Instrument.MasterInstrument.PointValue;
            int qty = (int)Math.Floor(RiskPerTradeDollars / (risk * pointValue));
            qty = Math.Min(qty, MaxContracts);
            if (qty < 1)
            {
                ResetSetup(string.Format("risk {0:0.00} pts x ${1} > risk budget ${2}", risk, pointValue, RiskPerTradeDollars));
                return;
            }

            Draw.Rectangle(this, "SB_fvg_" + drawId, fvgBarsAgo + 2, distal, 0, proximal, Brushes.DodgerBlue);
            PlaceEntry(dir, qty, entry, stop, tp1, target, risk);
        }

        private void PlaceEntry(int dir, int qty, double entry, double stop, double tp1, double target, double risk)
        {
            int q1 = (UsePartial && qty >= 2 && Tp1RR > 0) ? qty / 2 : 0;
            int q2 = qty - q1;

            string side = dir > 0 ? "L" : "S";
            sigTp1 = "SB" + side + "_TP1";
            sigRun = "SB" + side + "_RUN";

            // Protective orders must be defined BEFORE the entry is submitted.
            if (q1 > 0)
            {
                SetStopLoss(sigTp1, CalculationMode.Price, stop, false);
                SetProfitTarget(sigTp1, CalculationMode.Price, tp1);
            }
            SetStopLoss(sigRun, CalculationMode.Price, stop, false);
            SetProfitTarget(sigRun, CalculationMode.Price, target);

            tradeDir = dir;
            plannedEntry = entry;
            plannedStop = stop;
            plannedTarget = target;
            plannedTp1 = tp1;
            plannedRisk = risk;
            orderBar = CurrentBar;
            entryFilled = false;
            movedToBreakeven = false;
            cumProfitAtEntry = CumProfit();

            if (dir > 0)
            {
                if (q1 > 0) EnterLongLimit(0, true, q1, entry, sigTp1);
                EnterLongLimit(0, true, q2, entry, sigRun);
            }
            else
            {
                if (q1 > 0) EnterShortLimit(0, true, q1, entry, sigTp1);
                EnterShortLimit(0, true, q2, entry, sigRun);
            }

            phase = SetupPhase.OrderWorking;
            LogDecision(string.Format("{0} LIMIT {1} @ {2} | stop {3} | TP1 {4} ({5} ct) | target {6} ({7} ct) | risk {8:0.00} pts = ${9:0}",
                dir > 0 ? "BUY" : "SELL", qty, entry, stop, q1 > 0 ? tp1.ToString() : "-", q1, target, q2,
                risk, risk * Instrument.MasterInstrument.PointValue * qty));
        }

        private void ManageOrdersAndTrade(TimeSpan tod)
        {
            bool hasWorkingEntry = entryOrder1 != null || entryOrder2 != null;

            if (phase == SetupPhase.OrderWorking && !hasWorkingEntry && !entryFilled
                && Position.MarketPosition == MarketPosition.Flat)
            {
                ResetSetup("entry order no longer working");
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat && phase == SetupPhase.OrderWorking)
                phase = SetupPhase.InTrade;

            // Cancel entries that should no longer be filled.
            if (hasWorkingEntry)
            {
                bool expired = CurrentBar - orderBar >= EntryExpiryBars;
                bool windowOver = tod >= windowEndTod;
                bool ranToTarget = tradeDir > 0 ? High[0] >= plannedTarget : Low[0] <= plannedTarget;
                bool closedBeyondStop = tradeDir > 0 ? Close[0] < plannedStop : Close[0] > plannedStop;
                if (expired || windowOver || ranToTarget || closedBeyondStop)
                {
                    CancelEntryOrders();
                    if (Position.MarketPosition == MarketPosition.Flat && !entryFilled)
                        ResetSetup(expired ? "entry expired" : windowOver ? "window closed"
                            : ranToTarget ? "price reached target without us" : "closed beyond stop");
                }
            }

            // Bar-based breakeven (covers single-contract trades and missed TP1 events).
            if (entryFilled && Position.MarketPosition != MarketPosition.Flat && !movedToBreakeven && BreakevenAtR > 0)
            {
                double trigger = plannedEntry + tradeDir * BreakevenAtR * plannedRisk;
                bool reached = tradeDir > 0 ? High[0] >= trigger : Low[0] <= trigger;
                double be = plannedEntry + tradeDir * BreakevenOffsetTicks * TickSize;
                bool safe = tradeDir > 0 ? Close[0] > be + 2 * TickSize : Close[0] < be - 2 * TickSize;
                if (reached && safe)
                    MoveStopsToBreakeven();
            }
        }

        private void FinishTrade()
        {
            double pnl = CumProfit() - cumProfitAtEntry;
            tradesToday++;
            if (pnl > 0) winsToday++;
            else if (pnl < 0) lossesToday++;
            entryFilled = false;
            LogDecision(string.Format("Trade closed: P&L ${0:0.00} | today: {1} trades, {2}W/{3}L, day P&L ${4:0.00}",
                pnl, tradesToday, winsToday, lossesToday, CumProfit() - cumProfitAtDayStart));
            ResetSetup(null);
        }

        private void ResetSetup(string reason)
        {
            if (reason != null && phase != SetupPhase.Idle)
                LogDecision("Setup cancelled: " + reason);
            phase = SetupPhase.Idle;
            setupDir = 0;
            keyLevelSwept = false;
        }

        private bool CanArmNewSetup()
        {
            if (skipToday) return false;
            if (Position.MarketPosition != MarketPosition.Flat) return false;
            if (entryOrder1 != null || entryOrder2 != null || entryFilled) return false;
            if (tradesToday >= MaxTradesPerDay) return false;
            if (lossesToday >= MaxLossesPerDay) return false;
            if (StopAfterFirstWin && winsToday > 0) return false;

            double dayPnl = CumProfit() - cumProfitAtDayStart;
            if (DailyLossLimitDollars > 0 && dayPnl <= -DailyLossLimitDollars) return false;
            if (DailyProfitCapDollars > 0 && dayPnl >= DailyProfitCapDollars) return false;
            return true;
        }

        #endregion

        #region Market structure helpers

        // Confirms a fractal swing at bar [SwingStrength] and reports whether this bar swept
        // the most recent (not yet swept) swing low / swing high.
        private void UpdateSwings(out bool sweptLowNow, out bool sweptHighNow)
        {
            sweptLowNow = false;
            sweptHighNow = false;

            // Sweep check runs against levels confirmed on earlier bars.
            if (!double.IsNaN(lastSwingLow) && !lastSwingLowSwept && Low[0] < lastSwingLow)
            {
                lastSwingLowSwept = true;
                sweptLowNow = true;
            }
            if (!double.IsNaN(lastSwingHigh) && !lastSwingHighSwept && High[0] > lastSwingHigh)
            {
                lastSwingHighSwept = true;
                sweptHighNow = true;
            }

            int s = SwingStrength;
            double hs = High[s];
            double ls = Low[s];
            bool isHigh = true, isLow = true;
            for (int k = 0; k <= 2 * s; k++)
            {
                if (k == s) continue;
                if (k < s)
                {
                    if (High[k] >= hs) isHigh = false;
                    if (Low[k] <= ls) isLow = false;
                }
                else
                {
                    if (High[k] > hs) isHigh = false;
                    if (Low[k] < ls) isLow = false;
                }
            }
            if (isHigh)
            {
                lastSwingHigh = hs;
                lastSwingHighSwept = false;
                lastSwingHighBroken = false;
            }
            if (isLow)
            {
                lastSwingLow = ls;
                lastSwingLowSwept = false;
                lastSwingLowBroken = false;
            }
        }

        // Runs after the setup logic so the MSS candle itself can use the level it breaks.
        private void UpdateStructureBreaks()
        {
            if (!double.IsNaN(lastSwingHigh) && Close[0] > lastSwingHigh)
                lastSwingHighBroken = true;
            if (!double.IsNaN(lastSwingLow) && Close[0] < lastSwingLow)
                lastSwingLowBroken = true;
        }

        private void UpdateHtfStructure()
        {
            int s = HtfSwingStrength;
            if (CurrentBars[1] < 2 * s + 1)
                return;

            // Break of structure on a 15m close.
            if (!double.IsNaN(htfSwingHigh) && Closes[1][0] > htfSwingHigh)
                htfTrend = 1;
            else if (!double.IsNaN(htfSwingLow) && Closes[1][0] < htfSwingLow)
                htfTrend = -1;

            double hs = Highs[1][s];
            double ls = Lows[1][s];
            bool isHigh = true, isLow = true;
            for (int k = 0; k <= 2 * s; k++)
            {
                if (k == s) continue;
                if (k < s)
                {
                    if (Highs[1][k] >= hs) isHigh = false;
                    if (Lows[1][k] <= ls) isLow = false;
                }
                else
                {
                    if (Highs[1][k] > hs) isHigh = false;
                    if (Lows[1][k] < ls) isLow = false;
                }
            }
            if (isHigh) htfSwingHigh = hs;
            if (isLow) htfSwingLow = ls;
        }

        private void GetBias(out bool allowLong, out bool allowShort)
        {
            if (BiasMode == Ema14SBBiasMode.Off)
            {
                allowLong = true;
                allowShort = true;
                return;
            }

            double htfClose = Closes[1][0];
            double ema = emaHtf[0];
            double emaPrev = emaHtf[EmaSlopeBars];

            allowLong = htfClose > ema && ema > emaPrev;
            allowShort = htfClose < ema && ema < emaPrev;

            if (BiasMode == Ema14SBBiasMode.HtfEmaAndStructure)
            {
                allowLong = allowLong && htfTrend == 1;
                allowShort = allowShort && htfTrend == -1;
            }
        }

        // Scans the displacement leg (newest first) for an unmitigated fair value gap.
        private bool FindFvg(int dir, out double proximal, out double distal, out int barsAgo)
        {
            proximal = 0;
            distal = 0;
            barsAgo = -1;

            int maxI = Math.Min(CurrentBar - sweepBar - 2, FvgMaxLookback);
            for (int i = 0; i <= maxI; i++)
            {
                double prox, dist;
                if (dir > 0)
                {
                    prox = Low[i];       // candle 3 low
                    dist = High[i + 2];  // candle 1 high
                    if (prox - dist < MinFvgTicks * TickSize) continue;
                    if (dist <= sweepExtreme) continue;
                }
                else
                {
                    prox = High[i];      // candle 3 high
                    dist = Low[i + 2];   // candle 1 low
                    if (dist - prox < MinFvgTicks * TickSize) continue;
                    if (dist >= sweepExtreme) continue;
                }

                double entry = EntryMode == Ema14SBEntryMode.FvgProximal ? prox : (prox + dist) / 2.0;

                // Skip gaps that price already traded back into after they formed.
                bool mitigated = false;
                for (int j = 0; j < i; j++)
                {
                    if ((dir > 0 && Low[j] <= entry) || (dir < 0 && High[j] >= entry))
                    {
                        mitigated = true;
                        break;
                    }
                }
                if (mitigated) continue;

                proximal = prox;
                distal = dist;
                barsAgo = i;
                return true;
            }
            return false;
        }

        private double ComputeTarget(int dir, double entry, double risk)
        {
            double fixedTarget = entry + dir * RewardRisk * risk;

            if (TargetMode == Ema14SBTargetMode.Liquidity)
            {
                double minDist = MinRR * risk;
                double maxDist = MaxRR * risk;
                double best = double.NaN;
                double[] pools = dir > 0
                    ? new[] { orHigh, onHigh, pdHigh, rthHigh, lastSwingHigh }
                    : new[] { orLow, onLow, pdLow, rthLow, lastSwingLow };

                foreach (double p in pools)
                {
                    if (double.IsNaN(p)) continue;
                    double dist = dir * (p - entry);
                    if (dist < minDist) continue;
                    if (double.IsNaN(best) || dist < dir * (best - entry))
                        best = p;
                }

                if (!double.IsNaN(best))
                {
                    double dist = Math.Min(dir * (best - entry) - TickSize, maxDist); // front-run the pool by 1 tick
                    return Instrument.MasterInstrument.RoundToTickSize(entry + dir * dist);
                }
            }

            return Instrument.MasterInstrument.RoundToTickSize(fixedTarget);
        }

        // Did price take a session liquidity pool (overnight, opening range, previous day)?
        private bool TookKeyLevel(int dir, double price)
        {
            bool orReady = !double.IsNaN(orHigh) && ToNewYork(Time[0]).TimeOfDay > windowStartTod;
            if (dir > 0)
            {
                if (!double.IsNaN(onLow) && price < onLow) return true;
                if (orReady && price < orLow) return true;
                if (!double.IsNaN(pdLow) && price < pdLow) return true;
            }
            else
            {
                if (!double.IsNaN(onHigh) && price > onHigh) return true;
                if (orReady && price > orHigh) return true;
                if (!double.IsNaN(pdHigh) && price > pdHigh) return true;
            }
            return false;
        }

        #endregion

        #region Session / day helpers

        private void HandleNewDay(DateTime ny)
        {
            if (ny.Date == currentDay)
                return;

            currentDay = ny.Date;
            tradesToday = 0;
            winsToday = 0;
            lossesToday = 0;
            cumProfitAtDayStart = CumProfit();
            skipToday = noTradeDates.Contains(ny.Date)
                || ny.DayOfWeek == DayOfWeek.Saturday || ny.DayOfWeek == DayOfWeek.Sunday;
            if (skipToday && noTradeDates.Contains(ny.Date))
                LogDecision("No-trade date, strategy will stay flat today.");
        }

        private void UpdateSessionLevels(TimeSpan tod)
        {
            // Time[0] is the bar close: the 09:30-09:31 bar is stamped 09:31.
            bool nowRth = tod > RthOpen && tod <= RthClose;
            bool nowOvernight = tod > GlobexOpen || tod <= RthOpen;

            if (inRth && !nowRth)
            {
                pdHigh = rthHigh;
                pdLow = rthLow;
            }

            if (nowOvernight)
            {
                if (!inOvernight)
                {
                    onHigh = High[0];
                    onLow = Low[0];
                }
                else
                {
                    onHigh = Math.Max(onHigh, High[0]);
                    onLow = Math.Min(onLow, Low[0]);
                }
            }

            if (nowRth)
            {
                if (!inRth)
                {
                    rthHigh = High[0];
                    rthLow = Low[0];
                    orHigh = High[0];
                    orLow = Low[0];
                }
                else
                {
                    rthHigh = Math.Max(rthHigh, High[0]);
                    rthLow = Math.Min(rthLow, Low[0]);
                    if (tod <= windowStartTod)
                    {
                        orHigh = Math.Max(orHigh, High[0]);
                        orLow = Math.Min(orLow, Low[0]);
                    }
                }
            }

            inRth = nowRth;
            inOvernight = nowOvernight;
        }

        private DateTime ToNewYork(DateTime platformTime)
        {
            DateTime t = DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified);
            if (platformTz == null || nyTz == null || platformTz.Id == nyTz.Id)
                return t;
            return TimeZoneInfo.ConvertTime(t, platformTz, nyTz);
        }

        private static TimeSpan Hhmm(int value)
        {
            return new TimeSpan(value / 100, value % 100, 0);
        }

        #endregion

        #region Order helpers

        private void CancelEntryOrders()
        {
            if (entryOrder1 != null)
                CancelOrder(entryOrder1);
            if (entryOrder2 != null)
                CancelOrder(entryOrder2);
        }

        private void MoveStopsToBreakeven()
        {
            if (movedToBreakeven)
                return;
            double be = Instrument.MasterInstrument.RoundToTickSize(plannedEntry + tradeDir * BreakevenOffsetTicks * TickSize);
            if (sigTp1.Length > 0)
                SetStopLoss(sigTp1, CalculationMode.Price, be, false);
            SetStopLoss(sigRun, CalculationMode.Price, be, false);
            movedToBreakeven = true;
            LogDecision("Stop moved to breakeven at " + be);
        }

        private double CumProfit()
        {
            return SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
        }

        private void LogDecision(string message)
        {
            if (DebugPrint)
                Print(string.Format("[{0}] {1:yyyy-MM-dd HH:mm} NY | {2}", Name, ToNewYork(Time[0]), message));
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
            int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            bool done = orderState == OrderState.Filled || orderState == OrderState.Cancelled || orderState == OrderState.Rejected;

            if (sigTp1.Length > 0 && order.Name == sigTp1)
                entryOrder1 = done ? null : order;
            else if (sigRun.Length > 0 && order.Name == sigRun)
                entryOrder2 = done ? null : order;

            if (orderState == OrderState.Rejected)
                LogDecision("Order rejected: " + order.Name + " " + comment);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
            MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order == null)
                return;

            string name = execution.Order.Name;
            if (name == sigTp1 || name == sigRun)
            {
                entryFilled = true;
                if (phase == SetupPhase.OrderWorking)
                    phase = SetupPhase.InTrade;
            }

            // TP1 leg hit its target -> protect the runner at breakeven.
            if (name == "Profit target" && execution.Order.FromEntrySignal == sigTp1
                && execution.Order.OrderState == OrderState.Filled
                && Position.MarketPosition != MarketPosition.Flat)
            {
                MoveStopsToBreakeven();
            }
        }

        #endregion

        #region Properties

        // ---- 1. Session (New York time) ----
        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Silver Bullet start (HHMM NY)", Order = 1, GroupName = "1. Session (New York time)")]
        public int WindowStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Silver Bullet end (HHMM NY)", Order = 2, GroupName = "1. Session (New York time)")]
        public int WindowEnd { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Start watching sweeps (HHMM NY)", Order = 3, GroupName = "1. Session (New York time)")]
        public int SweepStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Flatten all (HHMM NY)", Order = 4, GroupName = "1. Session (New York time)")]
        public int FlattenTime { get; set; }

        // ---- 2. Bias ----
        [NinjaScriptProperty]
        [Display(Name = "Bias mode (15m)", Order = 1, GroupName = "2. Bias")]
        public Ema14SBBiasMode BiasMode { get; set; }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "EMA period", Order = 2, GroupName = "2. Bias")]
        public int EmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "EMA slope lookback (15m bars)", Order = 3, GroupName = "2. Bias")]
        public int EmaSlopeBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "15m swing strength", Order = 4, GroupName = "2. Bias")]
        public int HtfSwingStrength { get; set; }

        // ---- 3. ICT structure / entry ----
        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "1m swing strength", Order = 1, GroupName = "3. ICT entry")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require session-level sweep", Description = "Sweep must also take the overnight, opening-range or previous-day high/low.", Order = 2, GroupName = "3. ICT entry")]
        public bool RequireKeyLevelSweep { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Sweep expiry (bars)", Order = 3, GroupName = "3. ICT entry")]
        public int SweepExpiryBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MSS must close beyond 1m EMA14", Order = 4, GroupName = "3. ICT entry")]
        public bool RequireEmaReclaim { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Min MSS candle body (x ATR14)", Order = 5, GroupName = "3. ICT entry")]
        public double MinDisplacementAtr { get; set; }

        [NinjaScriptProperty]
        [Range(1, 400)]
        [Display(Name = "Min FVG size (ticks)", Order = 6, GroupName = "3. ICT entry")]
        public int MinFvgTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Bars to wait for FVG after MSS", Order = 7, GroupName = "3. ICT entry")]
        public int FvgWaitBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "FVG search lookback (bars)", Order = 8, GroupName = "3. ICT entry")]
        public int FvgMaxLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Entry price", Order = 9, GroupName = "3. ICT entry")]
        public Ema14SBEntryMode EntryMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Cancel unfilled entry after (bars)", Order = 10, GroupName = "3. ICT entry")]
        public int EntryExpiryBars { get; set; }

        // ---- 4. Stops and targets ----
        [NinjaScriptProperty]
        [Display(Name = "Stop placement", Order = 1, GroupName = "4. Stops and targets")]
        public Ema14SBStopMode StopMode { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Stop buffer (ticks)", Order = 2, GroupName = "4. Stops and targets")]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Min stop (points)", Order = 3, GroupName = "4. Stops and targets")]
        public double MinStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Max stop (points)", Order = 4, GroupName = "4. Stops and targets")]
        public double MaxStopPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Target mode", Order = 5, GroupName = "4. Stops and targets")]
        public Ema14SBTargetMode TargetMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 20)]
        [Display(Name = "Reward:risk (FixedRR)", Order = 6, GroupName = "4. Stops and targets")]
        public double RewardRisk { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 20)]
        [Display(Name = "Min R:R (Liquidity mode)", Order = 7, GroupName = "4. Stops and targets")]
        public double MinRR { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 50)]
        [Display(Name = "Max R:R (Liquidity mode)", Order = 8, GroupName = "4. Stops and targets")]
        public double MaxRR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Take half off at TP1", Order = 9, GroupName = "4. Stops and targets")]
        public bool UsePartial { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "TP1 (R multiple)", Order = 10, GroupName = "4. Stops and targets")]
        public double Tp1RR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Move stop to BE at (R, 0 = off)", Order = 11, GroupName = "4. Stops and targets")]
        public double BreakevenAtR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Breakeven offset (ticks)", Order = 12, GroupName = "4. Stops and targets")]
        public int BreakevenOffsetTicks { get; set; }

        // ---- 5. Funded account guard rails ----
        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Risk per trade ($)", Order = 1, GroupName = "5. Funded account")]
        public double RiskPerTradeDollars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Max contracts", Order = 2, GroupName = "5. Funded account")]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max trades per day", Order = 3, GroupName = "5. Funded account")]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max losing trades per day", Order = 4, GroupName = "5. Funded account")]
        public int MaxLossesPerDay { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop for the day after a win", Order = 5, GroupName = "5. Funded account")]
        public bool StopAfterFirstWin { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "Daily loss limit ($, 0 = off)", Order = 6, GroupName = "5. Funded account")]
        public double DailyLossLimitDollars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "Daily profit cap ($, 0 = off)", Order = 7, GroupName = "5. Funded account")]
        public double DailyProfitCapDollars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "No-trade dates (yyyy-MM-dd, comma separated)", Order = 8, GroupName = "5. Funded account")]
        public string NoTradeDates { get; set; }

        // ---- 6. Display ----
        [NinjaScriptProperty]
        [Display(Name = "Shade Silver Bullet window", Order = 1, GroupName = "6. Display")]
        public bool ShadeWindow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Print decisions to Output window", Order = 2, GroupName = "6. Display")]
        public bool DebugPrint { get; set; }

        #endregion
    }
}
