//
// MNQ CRT Strategy - Candle Range Theory automation for NinjaTrader 8
//
// Install: copy this file to  Documents\NinjaTrader 8\bin\Custom\Strategies\
//          then open New > NinjaScript Editor and press F5 to compile.
//
// Chart:   apply the strategy to an MNQ chart whose bar type is the LOWER timeframe:
//            - 4H range  -> 5 minute or 15 minute chart
//            - 1D range  -> 60 minute chart
//          The strategy adds a 1 minute series itself (used to build the higher-timeframe
//          candles from your time box and to manage orders precisely).
//
// Rules (buy and sell model):
//   1. Range candle   = the higher-timeframe candle that starts at your time box (wicks included).
//   2. Sweep candle   = the next candle. It must trade beyond ONE end of the range and close back
//                       inside the range. High swept -> SELL setup (target range low).
//                       Low swept  -> BUY setup  (target range high). No sweep -> setup ignored.
//   3. Entry candle   = the candle after the sweep. On the lower timeframe wait for an MSS
//                       (market structure shift), then a fair value gap (FVG) in the MSS leg.
//                       A limit order is placed at the FVG midpoint.
//   4. Stop loss      = previous higher-timeframe candle (the sweep candle) high for sells / low for buys.
//   5. Lots (4H):     2 lots if total risk <= MaxRiskTwoLots ($300), else 1 lot if risk <= MaxRiskPerLot ($215),
//                     else no trade.
//                     2 lots: lot 1 target = 50% of range, lot 2 target = opposite end; after lot 1 is booked,
//                             the stop of lot 2 moves to breakeven.
//                     1 lot : target = opposite end; when price reaches 50% of range the stop moves to breakeven.
//      Lots (1D):     always 1 lot (if risk <= MaxRiskPerLot), target = 50% of the range.
//
// This file is provided as-is for educational purposes. Test it on Sim101 / Strategy Analyzer before
// using it with real money. Futures trading involves substantial risk of loss.
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public enum CrtRangeTimeframe { FourHour, Daily }
	public enum CrtAmPm { AM, PM }
	public enum CrtTimeZone { NewYork, Chicago, London, India, NinjaTraderLocal }
	public enum CrtMssMode { StrictShift, AnyBreak }
	public enum CrtDirection { Both, LongOnly, ShortOnly }

	public class MnqCrtStrategy : Strategy
	{
		#region Internal types

		private class HtfCandle
		{
			public DateTime WindowStart;   // time box start, in the selected time zone
			public DateTime WindowEnd;     // time box end, in the selected time zone
			public DateTime FirstBarTime;  // chart time of the first 1-minute bar
			public DateTime LastBarTime;   // chart time of the last 1-minute bar
			public double Open, High, Low, Close;
			public bool IsTimeBox;
			public bool IsPartial;
			public bool Finalized;
		}

		private enum SetupState { WaitingForWindow, WaitingForMss, WaitingForFvg, OrderWorking, InTrade, Done }

		private class CrtSetup
		{
			public string Id;
			public int Direction;              // +1 = BUY (range low swept), -1 = SELL (range high swept)
			public HtfCandle Range;
			public HtfCandle Sweep;
			public double RangeHigh, RangeLow, RangeMid;
			public double SweepExtreme;        // sweep candle high (sell) / low (buy)
			public double StopPrice;           // SweepExtreme +/- buffer
			public double FinalTarget;         // opposite end of the range
			public DateTime WindowStart, WindowEnd;   // entry window (selected time zone)
			public SetupState State;

			public int MssBar = -1;
			public int MssSwingBar = -1;
			public double MssLevel;
			public int LegBar = -1;
			public double LegExtreme;
			public int LastReportedFvgBar = -1;

			public int FvgFirstBar = -1;
			public double FvgTop, FvgBottom;
			public double Entry;
			public double CurrentStop;
			public int Lots;
			public double Lot1Target, Lot2Target;
			public DateTime OrderTime;
			public bool AwaitingSubmit;
			public bool BreakevenDone;
			public bool CancelLogged;
		}

		#endregion

		#region Fields

		private const string Sig1 = "CRT_1";
		private const string Sig2 = "CRT_2";
		private const string TagPrefix = "MNQCRT_";
		private static readonly TimeSpan WeekendCutoffNy = new TimeSpan(16, 45, 0);

		private TimeZoneInfo ntTz;
		private TimeZoneInfo userTz;
		private TimeZoneInfo nyTz;
		private bool sameTz;
		private int htfMinutes;
		private TimeSpan anchor;
		private int ltfMinutes;
		private string chartWarning;

		private SessionIterator sessionIterator;
		private DateTime sessionEndLocal = DateTime.MinValue;

		private HtfCandle currentCandle;
		private HtfCandle prevCandle;
		private bool firstCandle = true;
		private int setupCounter;

		private CrtSetup pending;     // confirmed CRT, looking for MSS + FVG
		private CrtSetup trade;       // setup that owns the working orders / open position
		private CrtSetup lastTrade;

		// lower-timeframe market structure
		private double swingHigh, swingLow;
		private int swingHighBar = -1, swingLowBar = -1;
		private bool swingHighBroken = true, swingLowBroken = true;
		private int structure;        // +1 bullish, -1 bearish, 0 unknown
		private int breakDir;         // break that happened on the current LTF bar
		private bool breakIsShift;
		private double breakLevel;
		private int breakSwingBar;

		private Order entry1, entry2;
		private bool lot1Filled, lot2Filled, lot1Closed, lot2Closed;
		private double lot1Fill, lot2Fill;
		private bool weekendExitSent;

		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = @"CRT (Candle Range Theory) for MNQ. Range candle from a time box (AM/PM), sweep + close back inside, LTF MSS + FVG midpoint entry, stop at previous HTF candle extreme, 1/2-lot management with breakeven.";
				Name = "MnqCrtStrategy";
				Calculate = Calculate.OnBarClose;
				EntriesPerDirection = 1;
				EntryHandling = EntryHandling.UniqueEntries;
				IsExitOnSessionCloseStrategy = false;
				ExitOnSessionCloseSeconds = 30;
				IsFillLimitOnTouch = false;
				MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution = OrderFillResolution.Standard;
				Slippage = 0;
				StartBehavior = StartBehavior.WaitUntilFlat;
				TimeInForce = TimeInForce.Gtc;
				TraceOrders = false;
				RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade = 20;
				IsInstantiatedOnEachOptimizationIteration = true;

				RangeTimeframe = CrtRangeTimeframe.FourHour;
				TimeBoxTimeZone = CrtTimeZone.NewYork;
				RangeStartHour = 2;
				RangeStartMinute = 0;
				RangeStartAmPm = CrtAmPm.AM;
				OnlyTimeBoxCandle = true;
				DailyOpenHour = 6;
				DailyOpenMinute = 0;
				DailyOpenAmPm = CrtAmPm.PM;

				SwingStrength = 2;
				MssMode = CrtMssMode.StrictShift;
				MinFvgTicks = 2;
				StopBufferTicks = 0;

				MaxRiskTwoLots = 300;
				MaxRiskPerLot = 215;
				BreakevenOffsetTicks = 0;
				CancelIfMidReachedBeforeFill = true;
				AllowedDirection = CrtDirection.Both;

				FlattenBeforeWeekend = true;
				ShowDrawings = true;
				EnableLog = true;
			}
			else if (State == State.Configure)
			{
				// 1-minute series: builds the time-box candles and manages orders bar-by-bar.
				AddDataSeries(BarsPeriodType.Minute, 1);
			}
			else if (State == State.DataLoaded)
			{
				ntTz = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
				userTz = ResolveTimeZone(TimeBoxTimeZone);
				nyTz = FindTimeZone("Eastern Standard Time") ?? ntTz;
				sameTz = userTz.Id == ntTz.Id;
				htfMinutes = RangeTimeframe == CrtRangeTimeframe.FourHour ? 240 : 1440;
				anchor = RangeTimeframe == CrtRangeTimeframe.FourHour
					? To24h(RangeStartHour, RangeStartMinute, RangeStartAmPm)
					: To24h(DailyOpenHour, DailyOpenMinute, DailyOpenAmPm);
				sessionIterator = new SessionIterator(BarsArray[1]);
				ValidateChart();
			}
			else if (State == State.Realtime)
			{
				if (entry1 != null) entry1 = GetRealtimeOrder(entry1);
				if (entry2 != null) entry2 = GetRealtimeOrder(entry2);
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress == 1)
			{
				ProcessMinuteBar();
			}
			else if (BarsInProgress == 0)
			{
				if (CurrentBars[0] < 2 * SwingStrength + 3) return;
				ProcessLtfBar();
			}
		}

		#region 1-minute series: time box candles, expiry, trade management

		private void ProcessMinuteBar()
		{
			DateTime tLocal = Times[1][0];
			DateTime tUser = ToUser(tLocal);

			// track the end of the current trading session (CME: 5:00 PM New York)
			if (sessionIterator != null && (BarsArray[1].IsFirstBarOfSession || tLocal > sessionEndLocal))
			{
				sessionIterator.GetNextSession(tLocal, true);
				sessionEndLocal = sessionIterator.ActualSessionEnd;
			}
			bool sessionEnding = sessionEndLocal > DateTime.MinValue && tLocal >= sessionEndLocal;

			if (Position.MarketPosition == MarketPosition.Flat)
				weekendExitSent = false;
			if (FlattenBeforeWeekend && IsWeekendCutoff(tLocal))
				HandleWeekendCutoff();

			ManageTrade(tUser, Highs[1][0], Lows[1][0], sessionEnding);

			if (pending != null
				&& (pending.State == SetupState.WaitingForMss || pending.State == SetupState.WaitingForFvg)
				&& (tUser >= pending.WindowEnd || sessionEnding))
				EndPending("entry window closed without a valid MSS + FVG entry");

			BuildHtfCandle(tLocal, tUser, Opens[1][0], Highs[1][0], Lows[1][0], Closes[1][0]);

			SubmitQueuedEntry();
		}

		private void BuildHtfCandle(DateTime tLocal, DateTime tUser, double o, double h, double l, double c)
		{
			DateTime barOpenUser = tUser.AddMinutes(-1);
			DateTime windowStart = WindowStartFor(barOpenUser);

			if (currentCandle == null || windowStart != currentCandle.WindowStart)
			{
				// a new time box started; close the previous one if it was not closed on time (session gap)
				if (currentCandle != null && !currentCandle.Finalized)
					FinalizeCandle(currentCandle);

				currentCandle = new HtfCandle();
				currentCandle.WindowStart = windowStart;
				currentCandle.WindowEnd = windowStart.AddMinutes(htfMinutes);
				currentCandle.FirstBarTime = tLocal;
				currentCandle.LastBarTime = tLocal;
				currentCandle.Open = o;
				currentCandle.High = h;
				currentCandle.Low = l;
				currentCandle.Close = c;
				currentCandle.IsTimeBox = windowStart.TimeOfDay == anchor;
				currentCandle.IsPartial = firstCandle && barOpenUser > windowStart;
				firstCandle = false;
				OnHtfCandleStarted(currentCandle);
			}
			else
			{
				currentCandle.High = Math.Max(currentCandle.High, h);
				currentCandle.Low = Math.Min(currentCandle.Low, l);
				currentCandle.Close = c;
				currentCandle.LastBarTime = tLocal;
			}

			if (!currentCandle.Finalized && tUser >= currentCandle.WindowEnd)
				FinalizeCandle(currentCandle);
		}

		private void OnHtfCandleStarted(HtfCandle c)
		{
			if (pending != null && pending.State == SetupState.WaitingForWindow)
			{
				pending.WindowStart = c.WindowStart;
				pending.WindowEnd = c.WindowEnd;
				pending.State = SetupState.WaitingForMss;
				Note(string.Format("{0}: entry window open {1:MM-dd HH:mm} -> {2:MM-dd HH:mm} ({3}). Waiting for LTF MSS.",
					pending.Id, c.WindowStart, c.WindowEnd, TimeBoxTimeZone));
			}
		}

		private void FinalizeCandle(HtfCandle c)
		{
			c.Finalized = true;
			HtfCandle range = prevCandle;
			prevCandle = c;
			if (range != null)
				EvaluateCrt(range, c);
		}

		private void EvaluateCrt(HtfCandle range, HtfCandle sweep)
		{
			if (range.IsPartial || sweep.IsPartial) return;
			if (RangeTimeframe == CrtRangeTimeframe.FourHour && OnlyTimeBoxCandle && !range.IsTimeBox) return;

			string label = string.Format("Range {0:MM-dd HH:mm} H {1} L {2} | Sweep candle H {3} L {4} C {5}",
				range.WindowStart, P(range.High), P(range.Low), P(sweep.High), P(sweep.Low), P(sweep.Close));

			bool sweptHigh = sweep.High > range.High;
			bool sweptLow = sweep.Low < range.Low;
			bool closedInside = sweep.Close < range.High && sweep.Close > range.Low;

			if (!sweptHigh && !sweptLow) { Note(label + " -> no sweep, setup ignored"); return; }
			if (sweptHigh && sweptLow) { Note(label + " -> both ends swept, setup ignored"); return; }
			if (!closedInside) { Note(label + " -> swept but closed outside the range, setup ignored"); return; }

			int dir = sweptHigh ? -1 : 1;
			if ((AllowedDirection == CrtDirection.LongOnly && dir < 0) || (AllowedDirection == CrtDirection.ShortOnly && dir > 0))
			{
				Note(label + " -> " + (dir > 0 ? "BUY" : "SELL") + " setup skipped (direction filter)");
				return;
			}

			if (pending != null && pending.State != SetupState.Done)
				EndPending("replaced by a newer CRT setup");

			CrtSetup s = new CrtSetup();
			s.Id = "CRT" + (++setupCounter);
			s.Direction = dir;
			s.Range = range;
			s.Sweep = sweep;
			s.RangeHigh = range.High;
			s.RangeLow = range.Low;
			s.RangeMid = RoundPrice((range.High + range.Low) / 2.0);
			s.SweepExtreme = dir < 0 ? sweep.High : sweep.Low;
			s.StopPrice = RoundPrice(dir < 0 ? sweep.High + StopBufferTicks * TickSize : sweep.Low - StopBufferTicks * TickSize);
			s.FinalTarget = dir < 0 ? range.Low : range.High;
			s.State = SetupState.WaitingForWindow;
			pending = s;

			Note(string.Format("{0}: {1} CRT confirmed. {2}. 50% = {3}, stop = {4}, final target = {5}",
				s.Id, dir > 0 ? "BUY" : "SELL", label, P(s.RangeMid), P(s.StopPrice), P(s.FinalTarget)));

			if (ShowDrawings)
			{
				Draw.Rectangle(this, TagPrefix + s.Id + "_range", false, range.FirstBarTime, range.High, range.LastBarTime, range.Low,
					Brushes.DodgerBlue, Brushes.DodgerBlue, 10);
				Draw.Rectangle(this, TagPrefix + s.Id + "_sweep", false, sweep.FirstBarTime, sweep.High, sweep.LastBarTime, sweep.Low,
					Brushes.Orange, Brushes.Orange, 10);
				Draw.Text(this, TagPrefix + s.Id + "_label", false, (dir > 0 ? "CRT BUY" : "CRT SELL") + " " + s.Id,
					sweep.FirstBarTime, dir < 0 ? sweep.High : sweep.Low, dir < 0 ? 12 : -12, Brushes.Orange,
					new SimpleFont("Arial", 11), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
			}
		}

		private void ManageTrade(DateTime tUser, double high, double low, bool sessionEnding)
		{
			if (trade == null) return;
			if (trade.AwaitingSubmit)
			{
				// signal from this same minute; drop it only if the window is already over
				if (tUser >= trade.WindowEnd || sessionEnding)
				{
					trade.AwaitingSubmit = false;
					trade.State = SetupState.Done;
					Note(trade.Id + ": entry window closed before the order could be sent");
					trade = null;
				}
				return;
			}
			ReleaseTradeIfIdle();
			if (trade == null) return;

			bool windowOver = tUser >= trade.WindowEnd || sessionEnding;
			bool midReached = trade.Direction > 0 ? high >= trade.RangeMid : low <= trade.RangeMid;

			if (trade.State == SetupState.OrderWorking)
			{
				if (windowOver)
					CancelEntries("entry window closed - limit order not filled");
				else if (CancelIfMidReachedBeforeFill && midReached)
					CancelEntries("price reached 50% of the range before the limit filled");
				return;
			}

			if (trade.State == SetupState.InTrade)
			{
				if (windowOver && (IsAlive(entry1) || IsAlive(entry2)))
					CancelEntries("entry window closed - unfilled lot cancelled");
				if (midReached)
					TriggerBreakeven("price reached 50% of the range");
			}
		}

		private bool IsWeekendCutoff(DateTime tLocal)
		{
			DateTime ny = ConvertTz(tLocal, ntTz, nyTz);
			return ny.DayOfWeek == DayOfWeek.Friday && ny.TimeOfDay >= WeekendCutoffNy;
		}

		private void HandleWeekendCutoff()
		{
			if (pending != null)
				EndPending("Friday cut-off - no new trades before the weekend");
			if (trade != null && trade.AwaitingSubmit)
			{
				trade.AwaitingSubmit = false;
				trade.State = SetupState.Done;
				trade = null;
			}
			if (trade != null)
				CancelEntries("Friday cut-off");
			if (Position.MarketPosition != MarketPosition.Flat && !weekendExitSent)
			{
				weekendExitSent = true;
				Note("Friday cut-off - flattening position before the weekend");
				if (Position.MarketPosition == MarketPosition.Long)
					ExitLong();
				else
					ExitShort();
			}
		}

		#endregion

		#region Lower timeframe: market structure, MSS, FVG, entry

		private void ProcessLtfBar()
		{
			int cb = CurrentBars[0];
			UpdateStructure(cb);

			if (ShowDrawings)
			{
				UpdateLevelDrawings();
				UpdateStatus();
			}

			CrtSetup s = pending;
			if (s == null || s.State == SetupState.WaitingForWindow || s.State == SetupState.Done)
				return;

			DateTime closeUser = ToUser(Times[0][0]);
			DateTime openUser = ltfMinutes > 0 ? closeUser.AddMinutes(-ltfMinutes) : ToUser(Times[0][1]);
			if (openUser < s.WindowStart) return;   // bar still belongs to the sweep candle
			if (openUser >= s.WindowEnd) return;    // window over (expiry is handled on the 1-minute series)
			bool canPlace = closeUser < s.WindowEnd;

			double hi = Highs[0][0];
			double lo = Lows[0][0];

			// price traded beyond the previous HTF candle extreme before we got in -> setup invalid
			if (s.Direction < 0 ? hi > s.SweepExtreme : lo < s.SweepExtreme)
			{
				EndPending("price traded beyond the sweep candle " + (s.Direction < 0 ? "high" : "low") + " before entry");
				return;
			}

			// a new extreme beyond the MSS leg cancels that MSS -> wait for a fresh one
			if (s.State == SetupState.WaitingForFvg && (s.Direction < 0 ? hi > s.LegExtreme : lo < s.LegExtreme))
			{
				s.State = SetupState.WaitingForMss;
				Note(s.Id + ": new " + (s.Direction < 0 ? "high" : "low") + " beyond the MSS leg - waiting for a new MSS");
			}

			if (s.State == SetupState.WaitingForMss && breakDir == s.Direction && (MssMode == CrtMssMode.AnyBreak || breakIsShift))
			{
				s.MssBar = cb;
				s.MssLevel = breakLevel;
				s.MssSwingBar = breakSwingBar;
				s.LegBar = breakSwingBar;
				s.LegExtreme = s.Direction < 0 ? H(breakSwingBar) : L(breakSwingBar);
				for (int i = breakSwingBar; i <= cb; i++)
				{
					if (s.Direction < 0 ? H(i) > s.LegExtreme : L(i) < s.LegExtreme)
					{
						s.LegExtreme = s.Direction < 0 ? H(i) : L(i);
						s.LegBar = i;
					}
				}
				s.State = SetupState.WaitingForFvg;
				Note(string.Format("{0}: {1} MSS confirmed - close {2} beyond swing {3}. Leg extreme {4}. Looking for FVG.",
					s.Id, s.Direction < 0 ? "bearish" : "bullish", P(Closes[0][0]), P(s.MssLevel), P(s.LegExtreme)));
				if (ShowDrawings)
				{
					Draw.Line(this, TagPrefix + s.Id + "_mss" + cb, false, BarsArray[0].GetTime(breakSwingBar), s.MssLevel, Times[0][0], s.MssLevel,
						Brushes.Magenta, DashStyleHelper.Solid, 2);
					Draw.Text(this, TagPrefix + s.Id + "_msst" + cb, false, "MSS", Times[0][0], s.MssLevel, s.Direction < 0 ? -10 : 10,
						Brushes.Magenta, new SimpleFont("Arial", 10), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
				}
			}

			if (s.State == SetupState.WaitingForFvg && canPlace)
				TryEntryFromFvg(s, cb);
		}

		// Fractal swing points + break-of-structure tracking on the lower timeframe (closed bars only).
		private void UpdateStructure(int cb)
		{
			breakDir = 0;
			breakIsShift = false;

			int n = SwingStrength;
			int p = cb - n;
			if (p - n >= 0)
			{
				double ph = H(p), pl = L(p);
				bool isHigh = true, isLow = true;
				for (int k = 1; k <= n; k++)
				{
					if (H(p - k) >= ph || H(p + k) > ph) isHigh = false;
					if (L(p - k) <= pl || L(p + k) < pl) isLow = false;
				}
				if (isHigh) { swingHigh = ph; swingHighBar = p; swingHighBroken = false; }
				if (isLow) { swingLow = pl; swingLowBar = p; swingLowBroken = false; }
			}

			double c = Closes[0][0];
			if (swingLowBar >= 0 && !swingLowBroken && c < swingLow)
			{
				swingLowBroken = true;
				breakDir = -1;
				breakIsShift = structure != -1;
				breakLevel = swingLow;
				breakSwingBar = swingLowBar;
				structure = -1;
			}
			else if (swingHighBar >= 0 && !swingHighBroken && c > swingHigh)
			{
				swingHighBroken = true;
				breakDir = 1;
				breakIsShift = structure != 1;
				breakLevel = swingHigh;
				breakSwingBar = swingHighBar;
				structure = 1;
			}
		}

		// Look for the most recent unmitigated FVG inside the MSS leg (or formed after it).
		private void TryEntryFromFvg(CrtSetup s, int cb)
		{
			int minJ = Math.Max(s.LegBar + 2, 2);
			for (int j = cb; j >= minJ; j--)
			{
				double top, bottom;
				if (s.Direction < 0)
				{
					top = L(j - 2);        // bearish FVG: candle 1 low above candle 3 high
					bottom = H(j);
				}
				else
				{
					top = L(j);            // bullish FVG: candle 3 low above candle 1 high
					bottom = H(j - 2);
				}
				if (top - bottom < MinFvgTicks * TickSize - 1e-9) continue;

				double mid = (top + bottom) / 2.0;
				bool mitigated = false;
				for (int k = j + 1; k <= cb; k++)
				{
					if (s.Direction < 0 ? H(k) >= mid : L(k) <= mid) { mitigated = true; break; }
				}
				if (mitigated) continue;

				string reason;
				if (PlaceEntry(s, j, top, bottom, out reason))
					return;

				if (j > s.LastReportedFvgBar)
				{
					s.LastReportedFvgBar = j;
					Note(string.Format("{0}: FVG {1}-{2} skipped: {3}", s.Id, P(bottom), P(top), reason));
				}
			}
		}

		private bool PlaceEntry(CrtSetup s, int fvgThirdBar, double top, double bottom, out string reason)
		{
			reason = null;
			if (trade != null || Position.MarketPosition != MarketPosition.Flat)
			{
				reason = "another CRT trade is still active";
				return false;
			}
			if (FlattenBeforeWeekend && IsWeekendCutoff(Times[0][0]))
			{
				reason = "Friday cut-off";
				return false;
			}

			double entry = RoundPrice((top + bottom) / 2.0);
			double stop = s.StopPrice;
			double riskPoints = s.Direction < 0 ? stop - entry : entry - stop;
			if (riskPoints < TickSize)
			{
				reason = "entry is beyond the stop";
				return false;
			}
			if (s.Direction < 0 ? entry <= s.RangeMid : entry >= s.RangeMid)
			{
				reason = "FVG midpoint is not " + (s.Direction < 0 ? "above" : "below") + " 50% of the range";
				return false;
			}
			double last = Closes[0][0];
			if (s.Direction < 0 ? last >= entry : last <= entry)
			{
				reason = "price is already through the FVG midpoint";
				return false;
			}

			double riskPerLot = riskPoints * Instrument.MasterInstrument.PointValue;
			int lots;
			double t1, t2 = 0;
			if (RangeTimeframe == CrtRangeTimeframe.Daily)
			{
				if (riskPerLot > MaxRiskPerLot)
				{
					reason = string.Format(CultureInfo.InvariantCulture, "risk ${0:0.00} per lot > max ${1:0.00}", riskPerLot, MaxRiskPerLot);
					return false;
				}
				lots = 1;
				t1 = s.RangeMid;
			}
			else if (2 * riskPerLot <= MaxRiskTwoLots)
			{
				lots = 2;
				t1 = s.RangeMid;
				t2 = s.FinalTarget;
			}
			else if (riskPerLot <= MaxRiskPerLot)
			{
				lots = 1;
				t1 = s.FinalTarget;
			}
			else
			{
				reason = string.Format(CultureInfo.InvariantCulture, "risk ${0:0.00} per lot > max ${1:0.00}", riskPerLot, MaxRiskPerLot);
				return false;
			}

			s.FvgFirstBar = fvgThirdBar - 2;
			s.FvgTop = top;
			s.FvgBottom = bottom;
			s.Entry = entry;
			s.CurrentStop = stop;
			s.Lots = lots;
			s.Lot1Target = t1;
			s.Lot2Target = t2;
			s.OrderTime = Times[0][0];
			s.State = SetupState.OrderWorking;
			s.AwaitingSubmit = true;    // submitted on the 1-minute series (see SubmitQueuedEntry)
			trade = s;
			pending = null;

			Note(string.Format(CultureInfo.InvariantCulture,
				"{0}: {1} LIMIT {2} lot(s) @ {3} (FVG {4}-{5})  SL {6}  TP1 {7}{8}  risk ${9:0.00}/lot",
				s.Id, s.Direction > 0 ? "BUY" : "SELL", lots, P(entry), P(bottom), P(top), P(stop), P(t1),
				lots == 2 ? "  TP2 " + P(t2) : "", riskPerLot));

			if (ShowDrawings)
				Draw.Rectangle(this, TagPrefix + s.Id + "_fvg", false, BarsArray[0].GetTime(s.FvgFirstBar), top, Times[0][0], bottom,
					Brushes.Goldenrod, Brushes.Goldenrod, 30);
			return true;
		}

		// Orders are sent from the 1-minute series so that, in backtests, they can only fill on
		// 1-minute bars that come AFTER the lower-timeframe bar that produced the signal.
		private void SubmitQueuedEntry()
		{
			if (trade == null || !trade.AwaitingSubmit) return;
			CrtSetup s = trade;
			s.AwaitingSubmit = false;

			entry1 = null;
			entry2 = null;
			lot1Filled = lot2Filled = lot1Closed = lot2Closed = false;

			// stops / targets must be set BEFORE the entry is submitted (managed approach)
			SetStopLoss(Sig1, CalculationMode.Price, s.StopPrice, false);
			SetProfitTarget(Sig1, CalculationMode.Price, s.Lot1Target);
			if (s.Lots == 2)
			{
				SetStopLoss(Sig2, CalculationMode.Price, s.StopPrice, false);
				SetProfitTarget(Sig2, CalculationMode.Price, s.Lot2Target);
			}

			Order o1, o2 = null;
			if (s.Direction > 0)
			{
				o1 = EnterLongLimit(1, true, 1, s.Entry, Sig1);
				if (s.Lots == 2) o2 = EnterLongLimit(1, true, 1, s.Entry, Sig2);
			}
			else
			{
				o1 = EnterShortLimit(1, true, 1, s.Entry, Sig1);
				if (s.Lots == 2) o2 = EnterShortLimit(1, true, 1, s.Entry, Sig2);
			}
			if (o1 != null) entry1 = o1;
			if (o2 != null) entry2 = o2;

			ReleaseTradeIfIdle();   // in case the orders were ignored / rejected
		}

		#endregion

		#region Order / execution / position events

		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
			double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
		{
			if (order == null) return;
			if (order.Name == Sig1) entry1 = order;
			else if (order.Name == Sig2) entry2 = order;

			if (orderState == OrderState.Rejected)
				Note("ORDER REJECTED: " + order.Name + " " + error + " " + comment);

			ReleaseTradeIfIdle();
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
			MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution == null || execution.Order == null) return;
			Order o = execution.Order;

			if (o.Name == Sig1 || o.Name == Sig2)
			{
				if (o.Name == Sig1) { lot1Filled = true; lot1Fill = o.AverageFillPrice; }
				else { lot2Filled = true; lot2Fill = o.AverageFillPrice; }

				if (trade == null && lastTrade != null)
					trade = lastTrade;   // fill raced a cancel request
				if (trade != null)
				{
					trade.State = SetupState.InTrade;
					Note(string.Format("{0}: entry {1} filled @ {2}", trade.Id, o.Name, P(o.AverageFillPrice)));
					if (trade.BreakevenDone)
						MoveToBreakeven(o.Name, o.AverageFillPrice);
				}
				return;
			}

			if (o.OrderState != OrderState.Filled) return;
			if (o.FromEntrySignal == Sig1) lot1Closed = true;
			else if (o.FromEntrySignal == Sig2) lot2Closed = true;

			if (trade != null)
				Note(string.Format("{0}: {1} ({2}) filled @ {3}", trade.Id, o.Name, o.FromEntrySignal, P(price)));

			if (o.Name == "Profit target" && o.FromEntrySignal == Sig1 && trade != null && trade.Lots == 2)
				TriggerBreakeven("partial profit booked at 50% of the range");
		}

		protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
		{
			if (marketPosition != MarketPosition.Flat) return;
			if (trade != null && trade.State == SetupState.InTrade)
			{
				CancelEntries("position closed");
				trade.State = SetupState.Done;
				Note(trade.Id + ": position flat - setup complete");
				lastTrade = trade;
				trade = null;
			}
			lot1Filled = lot2Filled = lot1Closed = lot2Closed = false;
		}

		#endregion

		#region Trade helpers

		private void TriggerBreakeven(string reason)
		{
			if (trade == null || trade.BreakevenDone || RangeTimeframe != CrtRangeTimeframe.FourHour) return;
			trade.BreakevenDone = true;
			CancelEntries("50% reached - unfilled lot cancelled");

			bool moved = false;
			if (lot1Filled && !lot1Closed) moved |= MoveToBreakeven(Sig1, lot1Fill);
			if (lot2Filled && !lot2Closed) moved |= MoveToBreakeven(Sig2, lot2Fill);
			if (moved)
				Note(trade.Id + ": stop moved to breakeven (" + reason + ")");
		}

		private bool MoveToBreakeven(string signal, double fillPrice)
		{
			if (trade == null) return false;
			double be = RoundPrice(trade.Direction > 0
				? fillPrice + BreakevenOffsetTicks * TickSize
				: fillPrice - BreakevenOffsetTicks * TickSize);

			double reference = State == State.Realtime
				? (trade.Direction > 0 ? GetCurrentBid() : GetCurrentAsk())
				: Closes[1][0];
			bool valid = trade.Direction > 0 ? be < reference : be > reference;

			if (valid)
			{
				SetStopLoss(signal, CalculationMode.Price, be, false);
				trade.CurrentStop = be;
			}
			else
			{
				// price is already back at/through breakeven: close this lot at market instead
				if (trade.Direction > 0) ExitLong(1, 1, "CRT BE exit", signal);
				else ExitShort(1, 1, "CRT BE exit", signal);
			}
			return true;
		}

		private void CancelEntries(string reason)
		{
			bool c1 = IsCancellable(entry1);
			bool c2 = IsCancellable(entry2);
			if (!c1 && !c2) return;
			if (trade != null && !trade.CancelLogged)
			{
				trade.CancelLogged = true;
				Note(trade.Id + ": cancelling entry order(s) - " + reason);
			}
			if (c1) CancelOrder(entry1);
			if (c2) CancelOrder(entry2);
		}

		private void ReleaseTradeIfIdle()
		{
			if (trade == null || trade.State != SetupState.OrderWorking || trade.AwaitingSubmit) return;
			if (IsAlive(entry1) || IsAlive(entry2)) return;
			if (HasFill(entry1) || HasFill(entry2) || lot1Filled || lot2Filled) return;
			if (Position.MarketPosition != MarketPosition.Flat) return;
			trade.State = SetupState.Done;
			Note(trade.Id + ": entry order(s) closed without a fill - setup finished");
			lastTrade = trade;
			trade = null;
		}

		private void EndPending(string reason)
		{
			if (pending == null) return;
			pending.State = SetupState.Done;
			Note(pending.Id + ": " + reason);
			pending = null;
		}

		private static bool IsAlive(Order o)
		{
			if (o == null) return false;
			OrderState st = o.OrderState;
			return st != OrderState.Filled && st != OrderState.Cancelled && st != OrderState.Rejected;
		}

		private static bool IsCancellable(Order o)
		{
			return IsAlive(o) && o.OrderState != OrderState.CancelPending && o.OrderState != OrderState.CancelSubmitted;
		}

		private static bool HasFill(Order o)
		{
			return o != null && o.Filled > 0;
		}

		#endregion

		#region Drawing / status

		private void UpdateLevelDrawings()
		{
			CrtSetup s = trade ?? pending;
			if (s == null) return;
			DateTime now = Times[0][0];
			string id = TagPrefix + s.Id;

			Draw.Line(this, id + "_rh", false, s.Range.FirstBarTime, s.RangeHigh, now, s.RangeHigh, Brushes.DodgerBlue, DashStyleHelper.Solid, 1);
			Draw.Line(this, id + "_rl", false, s.Range.FirstBarTime, s.RangeLow, now, s.RangeLow, Brushes.DodgerBlue, DashStyleHelper.Solid, 1);
			Draw.Line(this, id + "_rm", false, s.Range.FirstBarTime, s.RangeMid, now, s.RangeMid, Brushes.Gray, DashStyleHelper.Dash, 1);

			if (s.State == SetupState.OrderWorking || s.State == SetupState.InTrade)
			{
				Draw.Line(this, id + "_entry", false, s.OrderTime, s.Entry, now, s.Entry, Brushes.Cyan, DashStyleHelper.Solid, 2);
				Draw.Line(this, id + "_sl", false, s.OrderTime, s.CurrentStop, now, s.CurrentStop, Brushes.Red, DashStyleHelper.Solid, 2);
				Draw.Line(this, id + "_tp1", false, s.OrderTime, s.Lot1Target, now, s.Lot1Target, Brushes.LimeGreen, DashStyleHelper.Dash, 2);
				if (s.Lots == 2)
					Draw.Line(this, id + "_tp2", false, s.OrderTime, s.Lot2Target, now, s.Lot2Target, Brushes.LimeGreen, DashStyleHelper.Solid, 2);
			}
		}

		private void UpdateStatus()
		{
			string mode = RangeTimeframe == CrtRangeTimeframe.FourHour
				? string.Format("4H range starts {0} ({1}){2}", FormatClock(anchor), TimeBoxTimeZone, OnlyTimeBoxCandle ? "" : " - every 4H candle")
				: string.Format("Daily range, day opens {0} ({1})", FormatClock(anchor), TimeBoxTimeZone);

			string state;
			if (trade != null && trade.State == SetupState.OrderWorking)
				state = string.Format("{0} {1}: limit {2} lot(s) @ {3} working, SL {4}", trade.Id, trade.Direction > 0 ? "BUY" : "SELL", trade.Lots, P(trade.Entry), P(trade.CurrentStop));
			else if (trade != null)
				state = string.Format("{0} {1}: in trade, entry {2}, SL {3}, TP1 {4}{5}", trade.Id, trade.Direction > 0 ? "LONG" : "SHORT", P(trade.Entry), P(trade.CurrentStop), P(trade.Lot1Target), trade.Lots == 2 ? ", TP2 " + P(trade.Lot2Target) : "");
			else if (pending != null && pending.State == SetupState.WaitingForWindow)
				state = pending.Id + ": CRT confirmed - waiting for the next candle";
			else if (pending != null && pending.State == SetupState.WaitingForMss)
				state = pending.Id + ": entry window open - waiting for LTF MSS";
			else if (pending != null && pending.State == SetupState.WaitingForFvg)
				state = pending.Id + ": MSS confirmed - waiting for FVG entry";
			else
				state = "Waiting for range candle + sweep";

			Draw.TextFixed(this, TagPrefix + "status", "MNQ CRT | " + mode + "\n" + state + (chartWarning != null ? "\n" + chartWarning : ""), TextPosition.TopLeft);
		}

		#endregion

		#region Utilities

		private double H(int barIndex) { return BarsArray[0].GetHigh(barIndex); }
		private double L(int barIndex) { return BarsArray[0].GetLow(barIndex); }

		private double RoundPrice(double price)
		{
			return Instrument.MasterInstrument.RoundToTickSize(price);
		}

		private static string P(double price)
		{
			return price.ToString("0.00", CultureInfo.InvariantCulture);
		}

		private static string FormatClock(TimeSpan t)
		{
			return new DateTime(2000, 1, 1).Add(t).ToString("h:mm tt", CultureInfo.InvariantCulture);
		}

		private static TimeSpan To24h(int hour12, int minute, CrtAmPm ampm)
		{
			int h = hour12 % 12;
			if (ampm == CrtAmPm.PM) h += 12;
			return new TimeSpan(h, minute, 0);
		}

		// Start of the higher-timeframe window (time box grid anchored at the chosen start time).
		private DateTime WindowStartFor(DateTime userTime)
		{
			DateTime a = userTime.Date + anchor;
			if (userTime < a) a = a.AddDays(-1);
			int k = (int)Math.Floor((userTime - a).TotalMinutes / htfMinutes);
			return a.AddMinutes((double)k * htfMinutes);
		}

		private DateTime ToUser(DateTime chartTime)
		{
			return sameTz ? chartTime : ConvertTz(chartTime, ntTz, userTz);
		}

		private static DateTime ConvertTz(DateTime t, TimeZoneInfo from, TimeZoneInfo to)
		{
			if (from == null || to == null || from.Id == to.Id) return t;
			try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), from, to); }
			catch { return t; }
		}

		private static TimeZoneInfo FindTimeZone(string id)
		{
			try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
			catch { return null; }
		}

		private TimeZoneInfo ResolveTimeZone(CrtTimeZone z)
		{
			string id = null;
			switch (z)
			{
				case CrtTimeZone.NewYork: id = "Eastern Standard Time"; break;
				case CrtTimeZone.Chicago: id = "Central Standard Time"; break;
				case CrtTimeZone.London: id = "GMT Standard Time"; break;
				case CrtTimeZone.India: id = "India Standard Time"; break;
			}
			if (id == null) return ntTz;
			TimeZoneInfo tzInfo = FindTimeZone(id);
			if (tzInfo == null)
			{
				Print("[MNQ CRT] Time zone '" + id + "' not found - using the NinjaTrader time zone instead.");
				return ntTz;
			}
			return tzInfo;
		}

		private void ValidateChart()
		{
			ltfMinutes = BarsArray[0].BarsPeriod.BarsPeriodType == BarsPeriodType.Minute ? BarsArray[0].BarsPeriod.Value : 0;

			if (ltfMinutes == 0)
				chartWarning = "WARNING: use a Minute chart (5/15 min for 4H, 60 min for Daily).";
			else if (RangeTimeframe == CrtRangeTimeframe.FourHour && ltfMinutes != 5 && ltfMinutes != 15)
				chartWarning = "WARNING: 4H range expects a 5 or 15 minute chart (current " + ltfMinutes + " min).";
			else if (RangeTimeframe == CrtRangeTimeframe.Daily && ltfMinutes != 60)
				chartWarning = "WARNING: Daily range expects a 60 minute chart (current " + ltfMinutes + " min).";

			if (chartWarning != null)
				Print("[MNQ CRT] " + chartWarning);
			if (Instrument != null && Instrument.MasterInstrument.Name != "MNQ")
				Print("[MNQ CRT] Note: strategy designed for MNQ, running on " + Instrument.MasterInstrument.Name
					+ " (point value " + Instrument.MasterInstrument.PointValue + ").");
		}

		private void Note(string message)
		{
			if (!EnableLog) return;
			DateTime t = CurrentBars[BarsInProgress] >= 0 ? Times[BarsInProgress][0] : DateTime.MinValue;
			Print(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm} [MNQ CRT] {1}", t, message));
		}

		#endregion

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "Range timeframe", Description = "FourHour = 4H range candle (use a 5 or 15 min chart). Daily = 1 day range candle (use a 60 min chart).", Order = 1, GroupName = "1. CRT Range (Time Box)")]
		public CrtRangeTimeframe RangeTimeframe { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Time box time zone", Description = "Time zone of the time box below. NewYork is standard for CME futures.", Order = 2, GroupName = "1. CRT Range (Time Box)")]
		public CrtTimeZone TimeBoxTimeZone { get; set; }

		[NinjaScriptProperty]
		[Range(1, 12)]
		[Display(Name = "4H range start - hour (1-12)", Description = "Start of the 4H range candle. Sweep candle = next 4H, entry candle = the 4H after that.", Order = 3, GroupName = "1. CRT Range (Time Box)")]
		public int RangeStartHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "4H range start - minute", Order = 4, GroupName = "1. CRT Range (Time Box)")]
		public int RangeStartMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "4H range start - AM/PM", Order = 5, GroupName = "1. CRT Range (Time Box)")]
		public CrtAmPm RangeStartAmPm { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "4H: only the time-box candle", Description = "True = only the 4H candle that starts at the time box can be a range candle. False = every 4H candle (grid anchored at the time box).", Order = 6, GroupName = "1. CRT Range (Time Box)")]
		public bool OnlyTimeBoxCandle { get; set; }

		[NinjaScriptProperty]
		[Range(1, 12)]
		[Display(Name = "Daily candle open - hour (1-12)", Description = "When the daily candle opens (CME futures: 6:00 PM New York).", Order = 7, GroupName = "1. CRT Range (Time Box)")]
		public int DailyOpenHour { get; set; }

		[NinjaScriptProperty]
		[Range(0, 59)]
		[Display(Name = "Daily candle open - minute", Order = 8, GroupName = "1. CRT Range (Time Box)")]
		public int DailyOpenMinute { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Daily candle open - AM/PM", Order = 9, GroupName = "1. CRT Range (Time Box)")]
		public CrtAmPm DailyOpenAmPm { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Swing strength (bars each side)", Description = "Bars on each side that define a lower-timeframe swing high/low used for the MSS.", Order = 1, GroupName = "2. LTF Entry (MSS + FVG)")]
		public int SwingStrength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "MSS mode", Description = "StrictShift = the break must flip LTF structure (bullish -> bearish for sells). AnyBreak = any close beyond the last swing counts.", Order = 2, GroupName = "2. LTF Entry (MSS + FVG)")]
		public CrtMssMode MssMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Min FVG size (ticks)", Order = 3, GroupName = "2. LTF Entry (MSS + FVG)")]
		public int MinFvgTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Stop buffer (ticks)", Description = "Extra ticks beyond the previous HTF candle high/low for the stop loss.", Order = 4, GroupName = "2. LTF Entry (MSS + FVG)")]
		public int StopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Max total risk for 2 lots ($)", Description = "4H only: trade 2 lots when the combined stop loss of both lots is within this amount.", Order = 1, GroupName = "3. Risk & Lots")]
		public double MaxRiskTwoLots { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Max risk per lot ($)", Description = "If one lot would risk more than this, the trade is skipped.", Order = 2, GroupName = "3. Risk & Lots")]
		public double MaxRiskPerLot { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Breakeven offset (ticks)", Description = "0 = stop exactly at entry. 1-2 ticks covers commissions.", Order = 3, GroupName = "3. Risk & Lots")]
		public int BreakevenOffsetTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Cancel if 50% hit before fill", Description = "Cancel the limit entry if price reaches 50% of the range before the order fills.", Order = 4, GroupName = "3. Risk & Lots")]
		public bool CancelIfMidReachedBeforeFill { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allowed direction", Order = 5, GroupName = "3. Risk & Lots")]
		public CrtDirection AllowedDirection { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Flatten before weekend", Description = "Friday 4:45 PM New York: cancel entries and close any open position.", Order = 1, GroupName = "4. Session")]
		public bool FlattenBeforeWeekend { get; set; }

		[Display(Name = "Show drawings", Order = 1, GroupName = "5. Display")]
		public bool ShowDrawings { get; set; }

		[Display(Name = "Print log to Output window", Order = 2, GroupName = "5. Display")]
		public bool EnableLog { get; set; }

		#endregion
	}
}
