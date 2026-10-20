// MicroScalp V3 — DEMO-ONLY research robot for cTrader, October 2026.
// UNVALIDATED strategy. No claim of profitability. Do NOT run with real funds.
// Changes from V1: M5 direction filter, M1 ATR filter, estimated cost gate,
// restart-aware day trade cap / loss streak / daily loss reconstructed from History,
// absolute equity floor, timed exits, labeled diagnostics and trade reporting.
// NOTE: cTrader Cloud LocalStorage is memory-only; this robot does not rely on it.
// Use a DEDICATED demo account with NO other bots, manual trading or cash flows.

using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class MicroScalpV3 : Robot
    {
        private const string Label = "MicroScalpV3";

        [Parameter("M1 EMA fast", DefaultValue = 8, MinValue = 2, MaxValue = 50, Group = "Signals")]
        public int FastPeriod { get; set; }

        [Parameter("M1 EMA slow", DefaultValue = 21, MinValue = 3, MaxValue = 100, Group = "Signals")]
        public int SlowPeriod { get; set; }

        [Parameter("RSI period", DefaultValue = 14, MinValue = 2, MaxValue = 100, Group = "Signals")]
        public int RsiPeriod { get; set; }

        [Parameter("RSI buy min", DefaultValue = 51, MinValue = 50.1, MaxValue = 80, Group = "Signals")]
        public double BuyRsiMin { get; set; }

        [Parameter("RSI sell max", DefaultValue = 49, MinValue = 20, MaxValue = 49.9, Group = "Signals")]
        public double SellRsiMax { get; set; }

        [Parameter("Min M1 candle body (pips)", DefaultValue = 0.3, MinValue = 0, MaxValue = 20, Group = "Signals")]
        public double MinBodyPips { get; set; }

        [Parameter("Require prior-bar breakout", DefaultValue = false, Group = "Signals")]
        public bool RequireBreakout { get; set; }

        [Parameter("M5 EMA fast", DefaultValue = 8, MinValue = 2, MaxValue = 50, Group = "Market quality")]
        public int M5FastPeriod { get; set; }

        [Parameter("M5 EMA slow", DefaultValue = 21, MinValue = 3, MaxValue = 100, Group = "Market quality")]
        public int M5SlowPeriod { get; set; }

        [Parameter("Minimum M5 EMA gap (pips)", DefaultValue = 0.3, MinValue = 0, MaxValue = 20, Group = "Market quality")]
        public double MinM5GapPips { get; set; }

        [Parameter("Enable ATR filter", DefaultValue = true, Group = "Market quality")]
        public bool EnableAtrFilter { get; set; }

        [Parameter("M1 ATR period", DefaultValue = 14, MinValue = 2, MaxValue = 100, Group = "Market quality")]
        public int AtrPeriod { get; set; }

        [Parameter("Minimum ATR (pips)", DefaultValue = 0.45, MinValue = 0, MaxValue = 20, Group = "Market quality")]
        public double MinAtrPips { get; set; }

        [Parameter("Maximum ATR (pips)", DefaultValue = 4, MinValue = 0.1, MaxValue = 50, Group = "Market quality")]
        public double MaxAtrPips { get; set; }

        [Parameter("Stop loss (pips)", DefaultValue = 6, MinValue = 1, MaxValue = 100, Group = "Orders")]
        public double StopLossPips { get; set; }

        [Parameter("Take profit (pips)", DefaultValue = 8, MinValue = 1, MaxValue = 100, Group = "Orders")]
        public double TakeProfitPips { get; set; }

        [Parameter("Max spread (pips)", DefaultValue = 1.2, MinValue = 0.1, MaxValue = 20, Group = "Orders")]
        public double MaxSpreadPips { get; set; }

        [Parameter("Est. roundtrip commission (pips)", DefaultValue = 1.5, MinValue = 0, MaxValue = 10, Group = "Orders")]
        public double CommissionEstimatePips { get; set; }

        [Parameter("Min reward / est. costs", DefaultValue = 2.5, MinValue = 1, MaxValue = 20, Group = "Orders")]
        public double MinimumRewardToCosts { get; set; }

        [Parameter("Risk per trade (% equity)", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 2, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Maximum lots", DefaultValue = 0.01, MinValue = 0.01, MaxValue = 1, Group = "Risk")]
        public double MaximumLots { get; set; }

        [Parameter("Maximum trades per UTC day", DefaultValue = 12, MinValue = 1, MaxValue = 100, Group = "Risk")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Cooldown after entry (minutes)", DefaultValue = 1, MinValue = 0, MaxValue = 120, Group = "Risk")]
        public int CooldownMinutes { get; set; }

        [Parameter("Pause after loss (minutes)", DefaultValue = 10, MinValue = 0, MaxValue = 240, Group = "Risk")]
        public int LossPauseMinutes { get; set; }

        [Parameter("Stop after consecutive losses", DefaultValue = 3, MinValue = 1, MaxValue = 10, Group = "Risk")]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Max position age (minutes)", DefaultValue = 15, MinValue = 1, MaxValue = 240, Group = "Risk")]
        public int MaxPositionMinutes { get; set; }

        [Parameter("Daily equity loss stop (%)", DefaultValue = 3, MinValue = 0.5, MaxValue = 30, Group = "Risk")]
        public double DailyLossLimitPercent { get; set; }

        [Parameter("Absolute equity floor (EUR)", DefaultValue = 180, MinValue = 1, MaxValue = 10000, Group = "Risk")]
        public double EquityFloorEur { get; set; }

        [Parameter("Start hour UTC", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "Session")]
        public int StartHourUtc { get; set; }

        [Parameter("End hour UTC (exclusive)", DefaultValue = 20, MinValue = 1, MaxValue = 24, Group = "Session")]
        public int EndHourUtc { get; set; }

        private ExponentialMovingAverage _emaFast;
        private ExponentialMovingAverage _emaSlow;
        private RelativeStrengthIndex _rsi;
        private AverageTrueRange _atr;
        private Bars _m5Bars;
        private ExponentialMovingAverage _m5Fast;
        private ExponentialMovingAverage _m5Slow;
        private DateTime _lastEntryUtc = DateTime.MinValue;
        private DateTime _lastForcedCloseAttemptUtc = DateTime.MinValue;
        private int _observedBars;
        private bool _safetyHalted;
        private int _skippedHours, _skippedExisting, _skippedTrend, _skippedAtr;
        private int _skippedRsiOrMomentum, _skippedCosts, _skippedRisk;

        protected override void OnStart()
        {
            // Intentionally cannot be overridden via parameters.
            if (Account.IsLive)
            {
                Print("REFUSED | V3 is DEMO ONLY. No real-money mode exists.");
                Stop();
                return;
            }

            if (SymbolName != "EURUSD" || Bars.TimeFrame != TimeFrame.Minute)
            {
                Print("REFUSED | Attach to EURUSD M1 only, current={0}/{1}", SymbolName, Bars.TimeFrame);
                Stop();
                return;
            }

            if (Account.Equity <= EquityFloorEur || FastPeriod >= SlowPeriod || M5FastPeriod >= M5SlowPeriod ||
                StartHourUtc >= EndHourUtc || MinAtrPips >= MaxAtrPips ||
                StopLossPips <= 0 || TakeProfitPips <= 0)
            {
                Print("REFUSED | Invalid parameters or current equity already below the fixed floor.");
                Stop();
                return;
            }

            // Every account position interferes with clean evaluation: use a dedicated demo account.
            if (Positions.Count > 0)
            {
                Print("REFUSED | Other position already open. Use a SEPARATE, flat demo account for V3.");
                Stop();
                return;
            }

            _emaFast = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FastPeriod);
            _emaSlow = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SlowPeriod);
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _m5Bars = MarketData.GetBars(TimeFrame.Minute5);
            _m5Fast = Indicators.ExponentialMovingAverage(_m5Bars.ClosePrices, M5FastPeriod);
            _m5Slow = Indicators.ExponentialMovingAverage(_m5Bars.ClosePrices, M5SlowPeriod);
            Positions.Closed += OnPositionClosed;

            Print("START V3 DEMO | Label={0} | Symbol={1}, TF={2}, equity={3:F2}, minUnits={4}",
                  Label, SymbolName, Bars.TimeFrame, Account.Equity, Symbol.VolumeInUnitsMin);
            Print("RULES | M1 EMA={0}/{1}, RSI={2}, M5 EMA={3}/{4}, ATR={5} [{6:F2}..{7:F2}] pips",
                  FastPeriod, SlowPeriod, RsiPeriod, M5FastPeriod, M5SlowPeriod, AtrPeriod,
                  MinAtrPips, MaxAtrPips);
            Print("RISK | SL={0:F1} TP={1:F1} | maxLots={2:F2} | equityFloorEUR={3:F2} | " +
                  "DailyMaxLoss={4:F1}% | stopAfterLosses={5}",
                  StopLossPips, TakeProfitPips, MaximumLots, EquityFloorEur,
                  DailyLossLimitPercent, MaxConsecutiveLosses);
            Print("NOTE | Dedicated EUR 200 demo account, no deposits/withdrawals, no other bots. " +
                  "History-based limits require complete broker history; fills, SL, and commissions may differ.");
        }

        protected override void OnTick()
        {
            CheckSafety();
            if (!_safetyHalted) CloseTimedOutPosition();
        }

        protected override void OnBar()
        {
            CheckSafety();
            if (_safetyHalted) return;
            _observedBars++;
            if (Bars.Count < Math.Max(Math.Max(SlowPeriod, RsiPeriod), AtrPeriod) + 6 ||
                _m5Bars.Count < M5SlowPeriod + 6)
                return;

            var today = Server.Time.Date;
            var ownTradesToday = History.FindAll(Label, SymbolName)
                .Where(t => t.EntryTime.Date == today).ToArray();
            int alreadyTradedToday = ownTradesToday.Select(t => t.PositionId).Distinct().Count();
            foreach (var p in Positions.FindAll(Label, SymbolName))
                if (p.EntryTime.Date == today) alreadyTradedToday++;

            var lastOwn = History.FindAll(Label, SymbolName)
                .Where(t => t.ClosingTime.Date == today)
                .OrderByDescending(t => t.ClosingTime).ToArray();
            int lossStreak = 0;
            foreach (var trade in lastOwn)
            {
                if (trade.NetProfit < 0) lossStreak++;
                else break;
            }

            if (_observedBars % 10 == 0)
            {
                Print("V3 HEARTBEAT | UTC={0:HH:mm} | equity={1:F2} | tradesToday={2} " +
                      "lossStreak={3} | skip hours={4} existing={5} trend={6} atr={7} " +
                      "signal={8} costs={9} risk={10}", Server.Time, Account.Equity,
                      alreadyTradedToday, lossStreak, _skippedHours, _skippedExisting,
                      _skippedTrend, _skippedAtr, _skippedRsiOrMomentum, _skippedCosts, _skippedRisk);
                _skippedHours = _skippedExisting = _skippedTrend = _skippedAtr =
                    _skippedRsiOrMomentum = _skippedCosts = _skippedRisk = 0;
            }

            if (Server.Time.Hour < StartHourUtc || Server.Time.Hour >= EndHourUtc)
            { _skippedHours++; return; }
            if (alreadyTradedToday >= MaxTradesPerDay || lossStreak >= MaxConsecutiveLosses)
            { _skippedRisk++; return; }
            if (Positions.Count != 0) { _skippedExisting++; return; }
            if (_lastEntryUtc != DateTime.MinValue &&
                (Server.Time - _lastEntryUtc).TotalMinutes < CooldownMinutes)
            { _skippedRisk++; return; }
            if (lastOwn.Length > 0 && lastOwn[0].NetProfit < 0 &&
                (Server.Time - lastOwn[0].ClosingTime).TotalMinutes < LossPauseMinutes)
            { _skippedRisk++; return; }

            double candleOpen = Bars.OpenPrices.Last(1);
            double candleClose = Bars.ClosePrices.Last(1);
            double bodyPips = Math.Abs(candleClose - candleOpen) / Symbol.PipSize;
            double fast = _emaFast.Result.Last(1);
            double slow = _emaSlow.Result.Last(1);
            double rsi = _rsi.Result.Last(1);
            double atrPips = _atr.Result.Last(1) / Symbol.PipSize;
            double m5Fast = _m5Fast.Result.Last(1);
            double m5Slow = _m5Slow.Result.Last(1);
            double m5Gap = Math.Abs(m5Fast - m5Slow) / Symbol.PipSize;
            double spreadPips = Symbol.Spread / Symbol.PipSize;

            if (EnableAtrFilter && (double.IsNaN(atrPips) || atrPips < MinAtrPips || atrPips > MaxAtrPips))
            { _skippedAtr++; return; }
            if (m5Gap < MinM5GapPips) { _skippedTrend++; return; }

            bool m1Buy = candleClose > candleOpen && candleClose > Bars.ClosePrices.Last(2) &&
                         fast > slow && rsi >= BuyRsiMin && rsi <= 75 &&
                         (!RequireBreakout || candleClose > Bars.HighPrices.Last(2));
            bool m1Sell = candleClose < candleOpen && candleClose < Bars.ClosePrices.Last(2) &&
                          fast < slow && rsi <= SellRsiMax && rsi >= 25 &&
                          (!RequireBreakout || candleClose < Bars.LowPrices.Last(2));
            if (bodyPips < MinBodyPips || (!m1Buy && !m1Sell))
            { _skippedRsiOrMomentum++; return; }

            if ((m1Buy && m5Fast <= m5Slow) || (m1Sell && m5Fast >= m5Slow))
            { _skippedTrend++; return; }

            // This is only a conservative cost ESTIMATE, not broker fill/commission data.
            double estimatedCostPips = spreadPips + CommissionEstimatePips;
            if (spreadPips > MaxSpreadPips ||
                TakeProfitPips < MinimumRewardToCosts * estimatedCostPips)
            { _skippedCosts++; return; }

            TradeType side = m1Buy ? TradeType.Buy : TradeType.Sell;
            double riskBasedUnits = Symbol.VolumeForProportionalRisk(
                ProportionalAmountType.Equity, RiskPercent, StopLossPips, RoundingMode.Down);
            double maxUnits = Symbol.QuantityToVolumeInUnits(MaximumLots);
            double units = Symbol.NormalizeVolumeInUnits(Math.Min(riskBasedUnits, maxUnits), RoundingMode.Down);
            if (double.IsNaN(units) || units < Symbol.VolumeInUnitsMin ||
                units > Symbol.VolumeInUnitsMax)
            {
                Print("V3 SKIP SIZE | calculatedUnits={0} | brokerMin={1}", units, Symbol.VolumeInUnitsMin);
                _skippedRisk++;
                return;
            }

            double margin = Symbol.GetEstimatedMargin(side, units);
            if (double.IsNaN(margin) || margin > Account.FreeMargin * 0.5)
            { _skippedRisk++; return; }

            Print("V3 SIGNAL | utc={0:HH:mm} side={1} RSI={2:F1} ATR={3:F2}p " +
                  "M5gap={4:F2}p spread={5:F2}p estCosts={6:F2}p units={7} " +
                  "SL={8:F1} TP={9:F1}", Server.Time, side, rsi, atrPips, m5Gap,
                  spreadPips, estimatedCostPips, units, StopLossPips, TakeProfitPips);
            var result = ExecuteMarketOrder(side, SymbolName, units, Label, StopLossPips, TakeProfitPips);
            if (!result.IsSuccessful)
            {
                Print("V3 ORDER REJECTED | {0}", result.Error);
                return;
            }
            _lastEntryUtc = Server.Time;
            Print("V3 OPEN OK | id={0} | {1} units | label={2}", result.Position.Id, units, Label);
        }

        private void CheckSafety()
        {
            if (_safetyHalted) return;

            // Reconstruct UTC start-of-day balance from broker history. This survives
            // restarts on a DEDICATED account with complete history and no cash flows.
            double dayClosedNet = History.Where(h => h.ClosingTime.Date == Server.Time.Date)
                                         .Sum(h => h.NetProfit);
            double dayStartBalance = Account.Balance - dayClosedNet;
            bool dayLossHit = dayStartBalance > 0 &&
                Account.Equity <= dayStartBalance * (1.0 - DailyLossLimitPercent / 100.0);
            bool floorHit = Account.Equity <= EquityFloorEur;
            if (!dayLossHit && !floorHit) return;

            _safetyHalted = true;
            Print("V3 SAFETY HALT | UTC={0:HH:mm} | equity={1:F2} | dayStartBalance={2:F2} " +
                  "| fixedFloor={3:F2} | dailyLoss={4} floor={5}",
                  Server.Time, Account.Equity, dayStartBalance, EquityFloorEur, dayLossHit, floorHit);
            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                var result = ClosePosition(pos);
                if (!result.IsSuccessful)
                    Print("V3 SAFETY CLOSE ERROR | id={0} error={1}", pos.Id, result.Error);
            }
            Print("V3 HALTED. Check for remaining open positions in Trade view. Manual restart " +
                  "cannot bypass daily history-based limit or fixed equity floor.");
        }

        private void CloseTimedOutPosition()
        {
            if (_lastForcedCloseAttemptUtc != DateTime.MinValue &&
                (Server.Time - _lastForcedCloseAttemptUtc).TotalSeconds < 60)
                return;

            foreach (var pos in Positions.FindAll(Label, SymbolName))
            {
                double ageMins = (Server.Time - pos.EntryTime).TotalMinutes;
                if (ageMins < MaxPositionMinutes) continue;
                _lastForcedCloseAttemptUtc = Server.Time;
                Print("V3 TIME EXIT | id={0} | age={1:F1}m | floatingNet={2:F2}",
                      pos.Id, ageMins, pos.NetProfit);
                var res = ClosePosition(pos);
                if (!res.IsSuccessful) Print("V3 TIME EXIT ERROR | {0}", res.Error);
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs e)
        {
            if (e.Position.Label != Label || e.Position.SymbolName != SymbolName) return;
            Print("V3 CLOSED | id={0} netEUR={1:F2} reason={2} equityEUR={3:F2} " +
                  "(includes commissions/swaps)", e.Position.Id, e.Position.NetProfit,
                  e.Reason, Account.Equity);
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;
            Print("V3 STOPPED | Stopping the instance does NOT automatically close remaining positions. " +
                  "Check Trade > Positions/Orders before leaving the account.");
        }
    }
}
