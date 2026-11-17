#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.BarsTypes;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

// Enums live at global scope so NinjaScript's generated wrappers (Strategies / MarketAnalyzerColumns namespaces) can resolve them.
    /// <summary>Controls how value area boundaries are rendered on the volume profile.</summary>
    public enum STVPDisplayStyle
    {
        /// <summary>Draw outline rectangles around each value area (original behaviour).</summary>
        Box,
        /// <summary>Draw a high-line and low-line for each value area, spanning the full session width.</summary>
        Lines
    }

    /// <summary>Stroke style for individual value area level lines.</summary>
    public enum STVPLineStyle { Solid, Dash, Dot, DashDot }

    /// <summary>Controls what metric the volume profile bars display.</summary>
    public enum STVPBarDisplayMode
    {
        /// <summary>Bars are sized and colored by total traded volume at each price (default).</summary>
        Volume,
        /// <summary>Bars are sized by absolute tick delta (buy vol − sell vol) and colored green/red by sign.</summary>
        Delta,
        /// <summary>Footprint shows Buy volume on the LEFT and Sell volume on the RIGHT (same order as SuperDOM), with volume profile on the far right.</summary>
        BidAsk
    }

    /// <summary>Which buy/sell totals the BidAsk footprint numbers use.</summary>
    public enum STVPBuySellSource
    {
        /// <summary>Full session cumulative buy/sell at each price (default).</summary>
        Session,
        /// <summary>Only trades inside the sliding time window (matches GemsTraderLadder Buy/Sell Sliding Window columns).</summary>
        SlidingWindow
    }

    /// <summary>How aggressive buy vs sell is assigned on each trade.</summary>
    public enum STVPTradeClassification
    {
        /// <summary>Quote rule: price ≥ ask → buy, price ≤ bid → sell, else Lee-Ready tick rule (default, NT-style).</summary>
        QuoteRule,
        /// <summary>Midpoint rule used by GemsTraderLadder: close &gt; (bid+ask)/2 → buy, else sell. Use this when matching ladder Session Buys/Sells numbers.</summary>
        Midpoint
    }

    /// <summary>Legacy single-mode selector. Diagonal and horizontal imbalances can now run together; zone confirmation uses diagonal initiative.</summary>
    public enum STVPImbalanceMode
    {
        /// <summary>Ask[level] vs Bid[level−1] / Bid[level] vs Ask[level+1] (initiative walk).</summary>
        Diagonal,
        /// <summary>Ask vs Bid at the same price (who owned the level). Better on thin products like ZS.</summary>
        Horizontal
    }

    /// <summary>Anchor corner for the analytics panel overlay.</summary>
    public enum STVPPanelPosition { TopLeft, TopRight }
    public enum STVPTakeProfitMode { AtrMultiple, StructureLevels, Ticks } // legacy enum kept for compile; only Ticks used
    public enum STVPStopMode { AtrMultiple, Structure, Ticks } // legacy enum kept for compile; only Ticks used

    /// <summary>Auto-trade entry order type.</summary>
    public enum STVPEntryOrderType { Market, LimitAtSignal }
    public enum STVPICArmLevel { Clear, Slight }

    /// <summary>Lifecycle of a detected Level-2 resting wall (live only).</summary>
    public enum STVPWallState
    {
        /// <summary>Qualified wall present in the book.</summary>
        Seen,
        /// <summary>Price is within WallTestingTicks of the wall.</summary>
        Testing,
        /// <summary>Aggressive volume into the wall has reached the absorb ratio while size still holds or refills.</summary>
        Absorbing,
        /// <summary>Wall size consumed or price traded through the level.</summary>
        Broken,
        /// <summary>Most of the size cancelled rather than traded through.</summary>
        Pulled
    }

    /// <summary>Advisory in-trade management state combining control + walls (live only).</summary>
    public enum STVPTradeMonitorState
    {
        Flat,
        Hold,
        HoldPlus,
        Caution,
        Exit
    }

    /// <summary>Current auction regime derived purely from order-flow and structure.</summary>
    public enum STVPRegimeState
    {
        /// <summary>Insufficient data or transitional.</summary>
        Transition,
        /// <summary>One-sided price discovery / imbalance in progress.</summary>
        Discovery,
        /// <summary>Two-sided auction behaviour (value forming, opposing participation present).</summary>
        Auction
    }

    /// <summary>Controls whether the volume/TPO profile resets each trading session or each week.</summary>
    public enum STVPProfilePeriod
    {
        /// <summary>One profile per trading session (default).</summary>
        Session,
        /// <summary>One cumulative profile for the full trading week.</summary>
        Weekly
    }

    /// <summary>How "RTH" is defined for IB timing, RTH tracking, auto-trade start and the open blackout.</summary>
    public enum STVPRthMode
    {
        /// <summary>Default. Full Session when the chart's template session opens inside the RTH window (no overnight / pre-RTH part, e.g. HE, LE); otherwise Manual (e.g. ES / NQ with a Globex overnight session).</summary>
        Auto,
        /// <summary>RTH = the RTH Start / End times (index futures with a Globex overnight session). Original behaviour.</summary>
        Manual,
        /// <summary>The whole trading-hours-template session is RTH (day-session-only markets such as HE / LE). RTH Start / End are ignored.</summary>
        FullSession
    }

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// SessionTPOVolumeProfile
    /// Tick-accurate, session-based Volume by Price (VbP) indicator.
    ///
    /// Features:
    ///   - Per-session VbP histogram with HVN / LVN / MVN bar coloring
    ///   - VPOC line (dashed, across session)
    ///   - Volume Value Area (VVAH / VVAL) – 70% of session volume
    ///   - Volume VA fill and outline box on the profile
    ///   - Initial Balance (IBH / IBL) horizontal lines
    ///   - Naked VPOC / VVAH / VVAL extension lines into future sessions
    ///   - Prior session High / Low extension lines
    ///   - Session stats mini-panel (date, range, IB range, VPOC, VA)
    ///   - Configurable VbP width for current vs prior sessions
    ///   - Right-edge offset so it doesn't overlap other indicators
    ///
    /// No HTTP export · No TPO letters · No exchange coloring · Self-contained.
    /// </summary>


    public class SessionTPOVolumeProfile : Indicator
    {
        #region Inner Classes

        private class TPOPeriod
        {
            public char              Letter;
            public DateTime          StartTime;
            public DateTime          EndTime;
            public bool              IsRTH;
            public HashSet<int>      PriceLevelsTouched;

            public TPOPeriod()
            {
                PriceLevelsTouched = new HashSet<int>();
            }
        }

        private enum DayType
        {
            Unknown, Normal, NormalVariation, Trend,
            DoubleDistribution, Neutral, PShape, BShape
        }

        private class SinglePrintZone
        {
            public int           LowLevel;
            public int           HighLevel;
            public HashSet<int>  UntestedLevels;
            public bool          IsFullyTested => UntestedLevels.Count == 0;
            public DateTime      BreakTime = DateTime.MinValue;

            public SinglePrintZone(int low, int high)
            {
                LowLevel       = low;
                HighLevel      = high;
                UntestedLevels = new HashSet<int>();
                for (int lvl = low; lvl <= high; lvl++)
                    UntestedLevels.Add(lvl);
            }
        }

        #endregion

        #region Data Class

        private class SessionProfile
        {
            public DateTime SessionDate;
            public DateTime StartTime;
            public DateTime EndTime;

            // Core volume data
            public Dictionary<int, long> VolumeAtPrice;

            // Session bounds
            public double SessionHigh;
            public double SessionLow;
            public double OpenPrice;
            public double ClosePrice;

            // RTH specifics
            public double RTHHigh;
            public double RTHLow;
            public double RTHOpen;
            public double RTHClose;
            public int    RTHStartBarIndex;
            public int    RTHEndBarIndex;

            // Computed profile levels  (stored as price-level indices, multiply by rowSize to get price)
            public int    VPOCLevel;    // Level index of highest-volume row
            public int    VVAH;         // 1σ Volume Value Area High  (68 % of session volume)
            public int    VVAL;         // 1σ Volume Value Area Low   (68 % of session volume)
            public int    SD2H;         // 2σ Volume Value Area High  (95 % of session volume)
            public int    SD2L;         // 2σ Volume Value Area Low   (95 % of session volume)
            public int    SD3H;         // 3σ Volume Value Area High  (99.7 % of session volume)
            public int    SD3L;         // 3σ Volume Value Area Low   (99.7 % of session volume)

            // Initial Balance (stored as level indices)
            public int    IBHigh;
            public int    IBLow;
            public double IBHighPrice;   // exact IB high price (tick-rounded), 0 = not set
            public double IBLowPrice;    // exact IB low price  (tick-rounded), 0 = not set
            public bool   IBComplete;
            public DateTime IBFirstTick = DateTime.MinValue;  // diagnostics: first / last tick folded into the IB
            public DateTime IBLastTick  = DateTime.MinValue;
            // Formation-order timestamps: when the current IB high / low extreme was last established.
            // The earlier of the two is the "first-formed" side (IB-by-rejection bias).
            public DateTime IBHighFormTime = DateTime.MinValue;
            public DateTime IBLowFormTime  = DateTime.MinValue;
            // Close price captured when IB window ends (for ending-zone / quartile bias).
            public double   IBClosePrice  = 0;

            // Derived helpers
            public int    HighLevel;    // Highest touched level index
            public int    LowLevel;     // Lowest  touched level index
            public double Halfback;     // Mid-price of VbP range (price units)

            // Chart bar positions
            public int    StartBarIndex;
            public int    EndBarIndex;

            // Naked-level testing flags
            public bool     VPOCTested;
            public bool     VVAHTested;
            public bool     VVALTested;
            public bool     SD2HTested;
            public bool     SD2LTested;
            public bool     SD3HTested;
            public bool     SD3LTested;
            public bool     HighTested;
            public bool     LowTested;
            public DateTime VPOCBreakTime = DateTime.MinValue;
            public DateTime VVAHBreakTime = DateTime.MinValue;
            public DateTime VVALBreakTime = DateTime.MinValue;
            public DateTime SD2HBreakTime = DateTime.MinValue;
            public DateTime SD2LBreakTime = DateTime.MinValue;
            public DateTime SD3HBreakTime = DateTime.MinValue;
            public DateTime SD3LBreakTime = DateTime.MinValue;
            public DateTime HighBreakTime = DateTime.MinValue;
            public DateTime LowBreakTime  = DateTime.MinValue;

            // ================================================================
            // TPO data — populated from tick-level letter construction
            // ================================================================

            // Raw TPO data
            public Dictionary<int, HashSet<char>> TPOsAtPrice; // price level → set of TPO letters (combined or display)
            public List<TPOPeriod>                Periods;      // ordered list of TPO time periods

            // Split Globex / RTH TPO maps (used when SplitGlobexRthTPO is enabled)
            public Dictionary<int, HashSet<char>> GlobexTPOsAtPrice;
            public Dictionary<int, HashSet<char>> RthTPOsAtPrice;

            // TPO-based levels (prefixed TPO to distinguish from volume levels)
            // When SplitGlobexRthTPO is ON these hold the RTH levels (naked extensions use RTH only).
            // When OFF they hold the full-session cumulative TPO levels.
            public int  POCLevel;   // TPO Point of Control (highest TPO count level)
            public int  TPOVAH;     // TPO 1σ Value Area High (68% of TPO letters)
            public int  TPOVAL;     // TPO 1σ Value Area Low
            public int  TPOSD2H;   // TPO 2σ Value Area High (95% of TPO letters)
            public int  TPOSD2L;   // TPO 2σ Value Area Low
            public int  TPOSD3H;   // TPO 3σ Value Area High (99.7% of TPO letters)
            public int  TPOSD3L;   // TPO 3σ Value Area Low
            public int  VAM;        // midpoint of TPO VA

            // Globex-only TPO levels (populated only when SplitGlobexRthTPO is ON)
            public int  GlobexPOCLevel;
            public int  GlobexTPOVAH;
            public int  GlobexTPOVAL;
            public int  GlobexTPOSD2H;
            public int  GlobexTPOSD2L;
            public int  GlobexTPOSD3H;
            public int  GlobexTPOSD3L;

            // Single prints & structure analysis (TPO-based)
            public List<SinglePrintZone> SinglePrintZones;
            public DayType Classification;
            public bool   HasPoorHigh;
            public bool   HasPoorLow;
            public bool   HasExcessHigh;
            public bool   HasExcessLow;
            public double PoorHighPrice;
            public double PoorLowPrice;
            public double ExcessHighPrice;
            public double ExcessLowPrice;

            // TPO naked-level flags
            public bool     TPOPOCTested;
            public bool     TPOVAHTested;
            public bool     TPOVALTested;
            public bool     TPOSD2HTested;
            public bool     TPOSD2LTested;
            public bool     TPOSD3HTested;
            public bool     TPOSD3LTested;
            public DateTime TPOPOCBreakTime  = DateTime.MinValue;
            public DateTime TPOVAHBreakTime  = DateTime.MinValue;
            public DateTime TPOVALBreakTime  = DateTime.MinValue;
            public DateTime TPOSD2HBreakTime = DateTime.MinValue;
            public DateTime TPOSD2LBreakTime = DateTime.MinValue;
            public DateTime TPOSD3HBreakTime = DateTime.MinValue;
            public DateTime TPOSD3LBreakTime = DateTime.MinValue;

            // Delta tracking — cumulative buy-minus-sell volume for the session
            public long FinalDelta;

            // Per-price tick delta — buy vol and sell vol tracked separately at each level
            public Dictionary<int, long> BuyVolumeAtPrice;
            public Dictionary<int, long> SellVolumeAtPrice;

            // Naked single print testing
            public int IBEndPeriodIndex;

            public SessionProfile()
            {
                VolumeAtPrice     = new Dictionary<int, long>();
                BuyVolumeAtPrice  = new Dictionary<int, long>();
                SellVolumeAtPrice = new Dictionary<int, long>();
                TPOsAtPrice       = new Dictionary<int, HashSet<char>>();
                GlobexTPOsAtPrice = new Dictionary<int, HashSet<char>>();
                RthTPOsAtPrice    = new Dictionary<int, HashSet<char>>();
                Periods           = new List<TPOPeriod>();
                SinglePrintZones  = new List<SinglePrintZone>();
                Classification    = DayType.Unknown;
                SessionHigh       = double.MinValue;
                SessionLow        = double.MaxValue;
                RTHHigh           = double.MinValue;
                RTHLow            = double.MaxValue;
                StartBarIndex     = -1;
                EndBarIndex       = -1;
                RTHStartBarIndex  = -1;
                RTHEndBarIndex    = -1;
                IBEndPeriodIndex  = -1;
            }
        }

        #endregion

        #region Private Fields

        private List<SessionProfile> sessions;
        private SessionProfile       currentSession;
        private SessionProfile       mostRecentCompletedSession;
        private DateTime             lastBarTime;
        private double               rowSize;

        // Session boundary detection — reads directly from the chart's trading hours template
        private SessionIterator      sessionIterator;

        // Per-bar alert fired flags — reset each time the primary bar index advances
        private int  lastAlertBarIndex = -1;
        private bool alertSD2HFired    = false;
        private bool alertSD2LFired    = false;
        private bool alertSD3HFired    = false;
        private bool alertSD3LFired    = false;
        private bool alertVVAHFired    = false;
        private bool alertVVALFired    = false;

        // SharpDX rendering resources
        private SharpDX.Direct2D1.Brush vpocBrush;
        private SharpDX.Direct2D1.Brush hvnBrush;
        private SharpDX.Direct2D1.Brush lvnBrush;
        private SharpDX.Direct2D1.Brush lvnExtAboveBrush;  // LVN extension fill for rows above VPOC
        private SharpDX.Direct2D1.Brush lvnExtBelowBrush;  // LVN extension fill for rows below VPOC
        private SharpDX.Direct2D1.Brush mvnBrush;
        private SharpDX.Direct2D1.Brush deltaPositiveBrush;   // delta mode: positive (buy > sell)
        private SharpDX.Direct2D1.Brush deltaNegativeBrush;   // delta mode: negative (sell > buy)
        private SharpDX.Direct2D1.Brush deltaPositiveFillBrush;  // semi-transparent fill for delta overlay on volume background
        private SharpDX.Direct2D1.Brush deltaNegativeFillBrush;  // semi-transparent fill for delta overlay on volume background
        private SharpDX.Direct2D1.Brush bidAskAskBrush;          // BidAsk mode: ask-lifted volume outline (right sub-bar)
        private SharpDX.Direct2D1.Brush bidAskBidBrush;          // BidAsk mode: bid-hit volume outline (left sub-bar)
        private SharpDX.Direct2D1.Brush bidAskAskFillBrush;      // BidAsk mode: ask-lifted fill (semi-transparent)
        private SharpDX.Direct2D1.Brush bidAskBidFillBrush;      // BidAsk mode: bid-hit fill (semi-transparent)
        private SharpDX.Direct2D1.Brush buyingImbalanceBrush;     // BidAsk diagonal imbalance: buying (ask side)
        private SharpDX.Direct2D1.Brush sellingImbalanceBrush;    // BidAsk diagonal imbalance: selling (bid side)
        private SharpDX.Direct2D1.Brush buyZoneFillBrush;         // stacked buying-imbalance zone fill (base)
        private SharpDX.Direct2D1.Brush sellZoneFillBrush;        // stacked selling-imbalance zone fill (base)
        private SharpDX.Direct2D1.Brush buyZoneBorderBrush;       // stacked buying-imbalance zone border (base)
        private SharpDX.Direct2D1.Brush sellZoneBorderBrush;      // stacked selling-imbalance zone border (base)
        private SharpDX.Direct2D1.Brush buyZoneFillStrongBrush;   // HVN-overlapping buy zone fill
        private SharpDX.Direct2D1.Brush sellZoneFillStrongBrush;  // HVN-overlapping sell zone fill
        private SharpDX.Direct2D1.Brush buyZoneBorderStrongBrush; // HVN-overlapping buy zone border
        private SharpDX.Direct2D1.Brush sellZoneBorderStrongBrush;// HVN-overlapping sell zone border
        private SharpDX.Direct2D1.Brush sameLevelBuyDomBrush;     // same-level: ask > bid label color
        private SharpDX.Direct2D1.Brush sameLevelSellDomBrush;    // same-level: bid > ask label color
        private SharpDX.Direct2D1.Brush volumeBarOutlineBrush;
        private SharpDX.Direct2D1.Brush volumeVABoxBrush;
        private SharpDX.Direct2D1.Brush valueAreaFillBrush;
        private SharpDX.Direct2D1.Brush levelZoneFillBrush;      // level-zone fill (VP + TPO levels, all-or-nothing)
        private SharpDX.Direct2D1.Brush levelZoneBorderBrush;    // level-zone outline
        private SharpDX.Direct2D1.Brush ibBrush;
        private SharpDX.Direct2D1.Brush ibFirstBrush;       // first-formed IB extreme
        private SharpDX.Direct2D1.Brush ibSecondHighBrush;  // IB High when it formed second
        private SharpDX.Direct2D1.Brush ibSecondLowBrush;   // IB Low when it formed second
        private SharpDX.Direct2D1.Brush ibQuartileBrush;    // 25/50/75% lines inside IB
        private SharpDX.Direct2D1.Brush nakedVpocBrush;
        private SharpDX.Direct2D1.Brush nakedVABrush;      // prior/naked VVAL
        private SharpDX.Direct2D1.Brush nakedVAHBrush;     // prior/naked VVAH
        private SharpDX.Direct2D1.Brush sd2VABoxBrush;     // 2σ VA box outline
        private SharpDX.Direct2D1.Brush sd2VAFillBrush;    // 2σ VA fill
        private SharpDX.Direct2D1.Brush nakedSD2Brush;     // naked 2σ extension lines
        private SharpDX.Direct2D1.Brush sd3VABoxBrush;     // 3σ VA box outline
        private SharpDX.Direct2D1.Brush sd3VAFillBrush;    // 3σ VA fill
        private SharpDX.Direct2D1.Brush nakedSD3Brush;     // naked 3σ extension lines
        private SharpDX.Direct2D1.Brush priorHighLowBrush;
        private SharpDX.Direct2D1.Brush textBrush;
        private SharpDX.DirectWrite.TextFormat volumeFormat;
        private SharpDX.DirectWrite.TextFormat statsFormat;
        private SharpDX.DirectWrite.TextFormat vaLabelFormat;  // for VVAH/VVAL/SD level labels
        private SharpDX.DirectWrite.TextFormat bidAskAskFormat; // left-aligned; ask column in BidAsk mode
        private StrokeStyle              dashStrokeStyle;
        private StrokeStyle              dotStrokeStyle;
        private StrokeStyle              dashDotStrokeStyle;

        // Per-level VA line brushes — independent color for each of the 6 VA boundaries
        private SharpDX.Direct2D1.Brush vvahLineBrush;
        private SharpDX.Direct2D1.Brush vvalLineBrush;
        private SharpDX.Direct2D1.Brush sd2HLineBrush;
        private SharpDX.Direct2D1.Brush sd2LLineBrush;
        private SharpDX.Direct2D1.Brush sd3HLineBrush;
        private SharpDX.Direct2D1.Brush sd3LLineBrush;

        private bool resourcesCreated  = false;
        private bool zOrderInitialized = false;

        // ================================================================
        // Cluster Zone fields
        // ================================================================
        private struct NakedLevelEntry
        {
            public double Price;
            public bool   IsVolume;
            public bool   IsTPO;
            public int    SessionIdx;  // index into sessions list
        }

        private struct ClusterZone
        {
            public double BottomPrice;
            public double TopPrice;
            public int    LevelCount;
            public int    SessionCount;
            public bool   HasVolume;
            public bool   HasTPO;
            public int    Score;
            public int    Tier;        // 1, 2, or 3
        }

        // Cluster zone brushes — fill (semi-transparent) and solid border per tier
        private SharpDX.Direct2D1.Brush clusterTier1Brush;
        private SharpDX.Direct2D1.Brush clusterTier2Brush;
        private SharpDX.Direct2D1.Brush clusterTier3Brush;
        private SharpDX.Direct2D1.Brush clusterTier1BorderBrush;
        private SharpDX.Direct2D1.Brush clusterTier2BorderBrush;
        private SharpDX.Direct2D1.Brush clusterTier3BorderBrush;

        // Analytics panel brush and format
        private SharpDX.Direct2D1.Brush   analyticsPanelBrush;
        private SharpDX.Direct2D1.Brush   analyticsPanelBgBrush;
        private SharpDX.DirectWrite.TextFormat analyticsPanelFormat;
        private SharpDX.DirectWrite.TextFormat clusterLabelFormat;

        // ================================================================
        // Delta tracking fields
        // ================================================================
        // By-reference OrderFlow+ cumulative delta — BidAsk, Session period
        private OrderFlowCumulativeDelta cumulativeDeltaIndicator;
        private Queue<long>              sessionFinalDeltaHistory = new Queue<long>();
        private int                      lastTickDirectionSign    = 0;  // 1 = uptick, -1 = downtick; carry-forward for equal-price ticks

        // Per primary-bar bid/ask totals (reset each new primary bar)
        private Dictionary<int, long>    primaryBarBidVol = new Dictionary<int, long>(); // sell aggressor
        private Dictionary<int, long>    primaryBarAskVol = new Dictionary<int, long>(); // buy aggressor

        // Session-cumulative bid/ask snapshot at each primary bar index
        // (running total from session open through that bar; used by the bottom 4-row table)
        private Dictionary<int, long>    sessionCumBidAtBar = new Dictionary<int, long>(); // sell aggressor cumulative
        private Dictionary<int, long>    sessionCumAskAtBar = new Dictionary<int, long>(); // buy  aggressor cumulative
        private long                     sessionRunningBid  = 0;
        private long                     sessionRunningAsk  = 0;

        // ================================================================
        // Sliding-window buy/sell (GemsTraderLadder-style)
        // ================================================================
        // Per-level last trade time + cumulative size inside the window.
        // Same simplification as GemsOrderFlow: one entry per price level;
        // the whole level is dropped when its last trade ages out of the window.
        private Dictionary<int, long>     windowBuyAtPrice  = new Dictionary<int, long>();
        private Dictionary<int, long>     windowSellAtPrice = new Dictionary<int, long>();
        private Dictionary<int, DateTime> windowBuyTime     = new Dictionary<int, DateTime>();
        private Dictionary<int, DateTime> windowSellTime    = new Dictionary<int, DateTime>();
        private DateTime                  lastWindowCleanup = DateTime.MinValue;

        // ================================================================
        // TPO private fields
        // ================================================================
        private TPOPeriod   currentTPOPeriod; // the period currently being filled with letters

        // TPO brushes
        private SharpDX.Direct2D1.Brush tpoLetterBrush;    // TPO letter color
        private SharpDX.Direct2D1.Brush tpoPocBrush;       // TPO POC line
        private SharpDX.Direct2D1.Brush tpoVAHBrush;       // TPO VAH line
        private SharpDX.Direct2D1.Brush tpoVALBrush;       // TPO VAL line
        private SharpDX.Direct2D1.Brush nakedTPOPocBrush;  // naked TPO POC extension
        private SharpDX.Direct2D1.Brush nakedTPOVABrush;   // naked TPO VAH/VAL extension
        private SharpDX.Direct2D1.Brush tpoSD2Brush;       // TPO SD2H/SD2L lines
        private SharpDX.Direct2D1.Brush nakedTPOSD2Brush;  // naked TPO SD2H/SD2L extension
        private SharpDX.Direct2D1.Brush tpoSD3Brush;       // TPO SD3H/SD3L lines
        private SharpDX.Direct2D1.Brush nakedTPOSD3Brush;  // naked TPO SD3H/SD3L extension
        private SharpDX.Direct2D1.Brush singlePrintBrush;  // single print zones
        private SharpDX.Direct2D1.Brush poorHighLowBrush;  // poor highs/lows
        private SharpDX.Direct2D1.Brush excessBrush;       // excess highs/lows
        private SharpDX.Direct2D1.Brush tpoVABoxBrush;     // TPO value area box outline
        private SharpDX.DirectWrite.TextFormat letterFormat;  // Consolas font for TPO letters

        // ================================================================
        // Regime & Dominance fields (non-lagging order-flow + structure)
        // ================================================================
        private double          regimeScore      = 0.0;   // −1 (Auction) … +1 (Discovery)
        private double          dominance        = 0.0;   // −1 (Sellers) … +1 (Buyers)
        private STVPRegimeState regimeState      = STVPRegimeState.Transition;
        private int             lastRegimeBar    = -1;
        private bool            regimeHistoryRebuilt = false;
        // In-charge Clear/Strong transition tracking (panel: clear >= 0.25, strong >= 0.50)
        // Forming primary-bar OHLC built from ticks (live Control price direction needs the
        // primary bar's range; Open[0]/High[0]/Low[0]/Close[0] on the 1-tick series are one price).
        private int             fbBar = -1;
        private double          fbOpen, fbHigh, fbLow, fbLast;
        private int             ctrlLogBar = -1;      // Control log: bar being logged
        private long            ctrlLogVol = -1;      // Control log: last bar volume printed
        // Control log per-bar stats (for the BAR SUMMARY line)
        private DateTime        ctrlStatStart;
        private long            ctrlStatLastVol = -1, ctrlStatMaxVol, ctrlStatMinVol, ctrlStatLastFlipVol, ctrlStatFirstArmVol, ctrlLogNextStep;
        private double          ctrlStatLast, ctrlStatMax, ctrlStatMin;
        private int             ctrlStatFlips, ctrlStatFirstArmSide, ctrlStatZone;
        private int             ctrlLogFiredBar = -1;  // Control log: bar on which IC fired
        private bool            ctrlLogReached = false; // Control log: contract count reached on this bar
        private int             ctrlLogZone = 99;       // Control log: last arm zone printed after the count was reached
        private bool            ctrlLogDone = false;  // Control log: bar already reached the contract count
        private int             lastClearStrongSide = 0;  // 0 = not CS, +1 = buyers CS, -1 = sellers CS
        private DateTime        lastTickTimeSeen = DateTime.MinValue;  // chart-time of the latest tick (for RTH-open blackout)

        // Trading-hours-template session bounds for the latest tick (set in ProcessTick from GetSessionKey).
        private DateTime        templateDayBegin = DateTime.MinValue;
        private DateTime        templateDayEnd   = DateTime.MinValue;
        private DateTime        lastKeyBegin     = DateTime.MinValue;
        private DateTime        lastKeyEnd       = DateTime.MinValue;
        private bool            forceFlatClampLogged = false;
        private Dictionary<DateTime, DateTime> tradingDayFirstBegin = new Dictionary<DateTime, DateTime>();
        // IC noise control: hysteresis release gate, per-side cooldown clock (live only)
        private DateTime        lastICFireBuy  = DateTime.MinValue;
        private DateTime        lastICFireSell = DateTime.MinValue;
        // Trade-signal engine state (see #region Trade Signals)
        private List<TradeSignal> signals       = new List<TradeSignal>();
        private readonly object   signalLock    = new object();
        private HashSet<string>   signalKeys    = new HashSet<string>();
        private List<ZoneSnap>    prevZones     = new List<ZoneSnap>();
        // Auto-trade (sim/live): market entry + ATR stop/target as working orders (draggable)
        private Account           autoTradeAccount;
        private readonly object   autoTradeLock = new object();
        private string            pendingEntrySignal = null;
        private int               pendingDir = 0;
        private double            pendingStop = 0;
        private double            pendingTarget = 0;
        private int               pendingQty = 0;
        private Order             workingEntryOrder;
        private DateTime          limitEntrySubmitTime = DateTime.MinValue;
        private int               limitEntryBarIdx     = -1;
        private bool              limitCancelRequested = false;
        private Order             workingStopOrder;
        private Order             workingTargetOrder;
        // Chart UI: lower-left auto-trade toggle button
        private bool              autoTradeBtnHooked = false;
        private SharpDX.RectangleF autoTradeBtnRect = new SharpDX.RectangleF();
        private SharpDX.Direct2D1.SolidColorBrush autoTradeBtnOnBrush;
        private SharpDX.Direct2D1.SolidColorBrush autoTradeBtnOffBrush;
        private SharpDX.Direct2D1.SolidColorBrush autoTradeBtnTextBrush;
        private SharpDX.DirectWrite.TextFormat    autoTradeBtnFormat;
        private ChartControl      autoTradeBtnChartControl;
        private int               lastSignalEvalBar = -1;
        // Vacuum (VU/VD) failure-confirmed fade: pending setup waiting for the next closed bar
        private int               vacPendingBar      = -1;
        private int               vacPendingDir      = 0;      // trade direction: +1 = VD (fade long), -1 = VU (fade short)
        private double            vacPendingPriceDir = 0;
        private double            vacPendingBarDelta = 0;
        private double            vacPendingDom      = 0;
        // IC persistence (live only): a pending IN CHARGE signal waiting out its hold timer
        private int               icPendingSide      = 0;      // +1 buyers, -1 sellers
        private int               icPendingBar       = -1;
        private DateTime          icPendingStart     = DateTime.MinValue;
        private double            icPendingArmPrice  = 0;
        // Throttle IC BLOCK structure prints (once per bar+side, or every 30s max)
        private int               icStructBlockBar   = -1;
        private int               icStructBlockSide  = 0;
        private DateTime          icStructBlockLast  = DateTime.MinValue;
        private ATR               atrIndicator;
        private List<RejectedSetup> rejected = new List<RejectedSetup>();
        private HashSet<string>     rejectedKeys = new HashSet<string>();
        // Per primary bar, per price row: aggressive ask / bid volume (footprint of each bar)
        private Dictionary<int, Dictionary<int, long>> barAskAtPrice = new Dictionary<int, Dictionary<int, long>>();
        private Dictionary<int, Dictionary<int, long>> barBidAtPrice = new Dictionary<int, Dictionary<int, long>>();
        // Diagnostics: why signals did or didn't appear
        private int               diagEval = 0, diagNoRegime = 0, diagRaw = 0, diagCtrl = 0, diagDay = 0, diagRisk = 0, diagNoTP = 0, diagConf = 0;
        private DateTime          sigSessionKey = DateTime.MinValue;
        private bool              eightyInit    = false;
        private bool              eightyDone    = false;
        private int               eightySide    = 0;
        private DateTime          eightyInsideSince = DateTime.MinValue;

        // Historical storage — one entry per primary bar index so past sessions
        // can be scrubbed and the Discovery / Auction timeline is visible.
        private Dictionary<int, double>          regimeScoreAtBar  = new Dictionary<int, double>();
        private Dictionary<int, double>          dominanceAtBar    = new Dictionary<int, double>();
        private Dictionary<int, STVPRegimeState> regimeStateAtBar  = new Dictionary<int, STVPRegimeState>();
        // Depth contribution stored per bar so historical plots include it.
        // Live L2 values are stored when available; otherwise a tick-derived
        // depth proxy is stored so every bar still has a depth signal.
        private Dictionary<int, double>          bookImbalanceAtBar = new Dictionary<int, double>();
        private Dictionary<int, double>          absorptionAtBar    = new Dictionary<int, double>();
        private Dictionary<int, bool>            bookFromLiveL2     = new Dictionary<int, bool>();

        // ── Level-2 book state (realtime only on most feeds) ───────────────────
        private long     l2BestBidSize       = 0;
        private long     l2BestAskSize       = 0;
        private double   l2BestBidPrice      = 0;
        private double   l2BestAskPrice      = 0;
        private double   l2BookImbalance     = 0;   // −1 (ask heavy) … +1 (bid heavy)
        private double   l2AbsorptionScore  = 0;   // 0…1  higher = aggressive flow being absorbed
        private long     l2LastAggressiveVol = 0;
        private int      l2LastAggressiveDir = 0;  // +1 buy aggressor, −1 sell aggressor
        private DateTime l2LastUpdateTime    = DateTime.MinValue;
        // Multi-level depth maps: position → size (position 0 = best)
        private Dictionary<int, long> l2BidDepth = new Dictionary<int, long>();
        private Dictionary<int, long> l2AskDepth = new Dictionary<int, long>();

        // ================================================================
        // L2 Wall monitor + in-trade advisory (live only, NQ-oriented)
        // ================================================================
        private class L2Level
        {
            public double   Price;
            public long     Size;
            public DateTime FirstSeen;
            public DateTime LastSeen;
            public long     PeakSize;
            public long     TradedInto;     // aggressive volume printed at this price while wall active
            public long     CancelledEst;  // size reductions not explained by trades
        }

        private class BookWall
        {
            public double        Price;
            public int           Side;          // +1 bid support, -1 ask resistance
            public long          Size;
            public long          PeakSize;
            public long          OriginalSize;  // size when first qualified as wall
            public DateTime      FirstSeen;
            public DateTime      QualifiedAt;
            public DateTime      LastSeen;
            public STVPWallState State;
            public STVPWallState PrevState;
            public long          TradedInto;
            public long          CancelledEst;
            public bool          TaggedForIC;   // IC signal saw this wall ahead
            public int           ICDir;         // +1 IC buy / -1 IC sell that tagged it
            public DateTime      LastStateLog = DateTime.MinValue;
        }

        // Price-keyed live book (rounded to tick)
        private Dictionary<double, L2Level> l2BidByPrice = new Dictionary<double, L2Level>();
        private Dictionary<double, L2Level> l2AskByPrice = new Dictionary<double, L2Level>();
        private readonly object             wallLock = new object();
        private List<BookWall>              activeWalls = new List<BookWall>();
        private DateTime                    lastWallScanTime = DateTime.MinValue;       // wall-clock throttle
        private DateTime                    lastWallScanMarketTime = DateTime.MinValue; // market/replay time of last scan
        private DateTime                    lastDepthMarketTime = DateTime.MinValue;    // last OnMarketDepth market time
        private bool                        depthDiagPrinted = false;                   // one-shot Market Replay / live depth log
        private DateTime                    lastMrBlockPrint = DateTime.MinValue;
        private DateTime                    lastMrEvalTime = DateTime.MinValue;
        private string                      lastMrBlockKey = "";
        private DateTime                    lastDepthCleanup = DateTime.MinValue;
        private bool                        depthEverSeen = false;

        // In-trade monitor
        private STVPTradeMonitorState tradeMonState = STVPTradeMonitorState.Flat;
        private bool                  tradeMonBeMoved = false;
        private STVPTradeMonitorState tradeMonPending = STVPTradeMonitorState.Flat;
        private DateTime              tradeMonPendingSince = DateTime.MinValue;
        private DateTime              tradeMonLastAlert = DateTime.MinValue;
        private int                   tradeMonDir = 0;       // +1 long, -1 short
        private double                tradeMonEntry = 0;
        private int                   tradeMonQty = 0;         // contracts for P&L
        private double                tradeMonExitFill = 0;    // actual exit execution price (stop/target/flatten)
        private string                tradeMonExitFillName = ""; // which order filled the exit
        private string                tradeMonSetup = "";
        private DateTime              tradeMonStart = DateTime.MinValue;
        private bool                  tradeMonWallCleared = false;  // opposing wall broken during trade
        private string                tradeMonDetail = "";
        private double                tradeMonStopPrice = 0;    // live managed stop
        private double                tradeMonTargetPrice = 0;  // live managed target
        private bool                  tradeMonCautionApplied = false;
        private bool                  tradeMonExitApplied = false;
        private DateTime              tradeMonLastManage = DateTime.MinValue;
        private double                sessionTradePnL = 0;      // cumulative $ P&L for session log
        private int                   sessionTradeCount = 0;
        private int                   sessionTradeWins = 0;
        private int                   sessionTradeLosses = 0;
        private DateTime              autoTradeForceFlatDoneDate = DateTime.MinValue; // chart date we already force-flattened

        // Wall rendering brushes
        private SharpDX.Direct2D1.Brush wallSeenBrush;
        private SharpDX.Direct2D1.Brush wallBidBrush;
        private SharpDX.Direct2D1.Brush wallAskBrush;
        private SharpDX.Direct2D1.Brush wallTestingBrush;
        private SharpDX.Direct2D1.Brush wallAbsorbBrush;
        private SharpDX.Direct2D1.Brush wallDimBrush;
        private SharpDX.Direct2D1.Brush tradeMonBgBrush;
        private SharpDX.Direct2D1.Brush tradeMonHoldBrush;
        private SharpDX.Direct2D1.Brush tradeMonCautionBrush;
        private SharpDX.Direct2D1.Brush tradeMonExitBrush;
        private SharpDX.DirectWrite.TextFormat wallLabelFormat;
        private SharpDX.DirectWrite.TextFormat tradeMonFormat;

        // Regime rendering brushes
        private SharpDX.Direct2D1.Brush regimeDiscoveryBgBrush;
        private SharpDX.Direct2D1.Brush regimeAuctionBgBrush;
        private SharpDX.Direct2D1.Brush regimeTransitionBgBrush;
        private SharpDX.Direct2D1.Brush regimeBuyerBrush;
        private SharpDX.Direct2D1.Brush regimeSellerBrush;
        private SharpDX.Direct2D1.Brush regimePanelBgBrush;
        private SharpDX.Direct2D1.Brush regimePanelTextBrush;
        private SharpDX.DirectWrite.TextFormat regimePanelFormat;

        #endregion

        // =========================================================
        //  PROPERTIES
        // =========================================================

        #region Properties — RTH Settings

        [NinjaScriptProperty]
        [Display(Name = "RTH Start Time",
                 Description = "Start of Regular Trading Hours (e.g. 8:30 AM). Ignored when RTH Mode = Full Session. Used for Initial Balance timing and RTH-specific bar tracking. Session boundaries are always read from the chart's trading hours template.",
                 Order = 1, GroupName = "1. RTH Settings")]
        public TimeSpan RTHStartTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTH End Time",
                 Description = "End of Regular Trading Hours (e.g. 4:00 PM). Used for IB and RTH bar index tracking.",
                 Order = 2, GroupName = "1. RTH Settings")]
        public TimeSpan RTHEndTime { get; set; }

        [Display(Name = "RTH Mode",
                 Description = "Auto (default) = detects it per contract from the chart's trading hours template: if the session opens inside the RTH window below (no overnight part, e.g. HE / LE) the whole session is RTH, otherwise the RTH Start / End times are used (index futures with a Globex overnight session). Manual = always use RTH Start / End. Full Session = always treat the whole template session as RTH. When the whole session is RTH, Initial Balance, Auto-Trade Start After RTH and the RTH Open Blackout anchor on the template session open and nothing is classed as Globex.",
                 Order = 0, GroupName = "1. RTH Settings")]
        public STVPRthMode RthMode { get; set; }

        [Display(Name = "Merge Template Sessions Into One Trading Day",
                 Description = "OFF (default): one profile per session in the trading hours template. ON: if the template splits a trading day into separate sessions (e.g. a break between the overnight and day segments), keep them in ONE profile. Leave OFF for index futures and for single-session markets like HE / LE.",
                 Order = 3, GroupName = "1. RTH Settings")]
        public bool MergeTemplateSessions { get; set; }

        #endregion

        #region Properties — Volume Profile Settings

        [NinjaScriptProperty]
        [Range(1, 30)]
        [Display(Name = "Ticks Per Row", Description = "Number of ticks that make up one price row in the profile", Order = 1, GroupName = "2. Volume Profile Settings")]
        public int TicksPerRow { get; set; }

        [NinjaScriptProperty]
        [Range(15, 240)]
        [Display(Name = "IB Duration (Minutes)", Description = "Initial Balance duration in minutes (typically 60)", Order = 2, GroupName = "2. Volume Profile Settings")]
        public int IBDurationMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Sessions", Description = "Maximum number of session (or weekly) profiles to keep and display", Order = 3, GroupName = "2. Volume Profile Settings")]
        public int MaxSessionsToShow { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Profile Period",
                 Description = "Session = one volume/TPO profile per trading day. Weekly = one cumulative profile for the whole trading week (better structural reference on thin markets like HE/LE).",
                 Order = 0, GroupName = "2. Volume Profile Settings")]
        public STVPProfilePeriod ProfilePeriod { get; set; }

        #endregion

        #region Properties — Display Options

        [NinjaScriptProperty]
        [Display(Name = "Show VPOC Line", Description = "Draw a dashed VPOC line across the session", Order = 1, GroupName = "3. Display Options")]
        public bool ShowVPOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Volume Value Area", Description = "Draw the 70% volume value area fill and box outline", Order = 2, GroupName = "3. Display Options")]
        public bool ShowVolumeValueArea { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Initial Balance", Description = "Draw IBH and IBL horizontal lines (colored by formation order). Lines appear as soon as each extreme exists — do not wait for the full IB window.", Order = 3, GroupName = "3. Display Options")]
        public bool ShowInitialBalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show IB Quartile Levels", Description = "Draw 25% / 50% / 75% horizontal lines inside the IB range (used for the ending-zone bias filter).", Order = 31, GroupName = "3. Display Options")]
        public bool ShowIBQuartiles { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show IB Bias Label", Description = "After the IB completes, show a short bias label (LONG/SHORT/lean/FADE) based on formation order + ending zone.", Order = 32, GroupName = "3. Display Options")]
        public bool ShowIBBiasLabel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked VPOC", Description = "Extend untested prior VPOC levels as dashed lines into future sessions", Order = 4, GroupName = "3. Display Options")]
        public bool ShowNakedVPOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Prior Session VPOC", Description = "Extend the previous session's volume POC as a dashed line. Independent of Show Naked VPOC, which controls the older untested VPOCs.", Order = 4, GroupName = "3. Display Options")]
        public bool ShowPriorSessionVPOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked VVAH/VVAL", Description = "Extend untested prior volume value area boundaries as dashed lines", Order = 5, GroupName = "3. Display Options")]
        public bool ShowNakedVAHVAL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Prior Session Levels", Description = "Extend prior session high and low as dashed lines", Order = 6, GroupName = "3. Display Options")]
        public bool ShowPriorSessionLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Volume Labels", Description = "Print volume numbers on each bar (only when bar is tall enough)", Order = 7, GroupName = "3. Display Options")]
        public bool ShowVolumeLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Per-Bar Bid/Ask Totals",
                 Description = "Show a 4-row table pinned to the bottom of the price panel. Rows 1–2: per-bar Buy / Sell (reset each bar; SuperDOM order). Rows 3–4: session-cumulative Buy / Sell. Winning cell uses same-level dominance colors.",
                 Order = 7, GroupName = "3. Display Options")]
        public bool ShowPerBarBidAskTotals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Session Stats", Description = "Display a mini stats panel at the session low (date, range, IB, VPOC, VA)", Order = 8, GroupName = "3. Display Options")]
        public bool ShowSessionStats { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 2σ Value Area (95%)", Description = "Draw the 2-sigma (95%) volume value area fill and SD2H/SD2L box outline", Order = 9, GroupName = "3. Display Options")]
        public bool ShowSD2VA { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 3σ Value Area (99.7%)", Description = "Draw the 3-sigma (99.7%) volume value area SD3H/SD3L box outline", Order = 10, GroupName = "3. Display Options")]
        public bool ShowSD3VA { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 1σ Fill", Description = "Shade the 1σ (68%) value area region", Order = 11, GroupName = "3. Display Options")]
        public bool ShowVA1Fill { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 2σ Fill", Description = "Shade the 2σ (95%) value area region", Order = 12, GroupName = "3. Display Options")]
        public bool ShowSD2Fill { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show 3σ Fill", Description = "Shade the 3σ (99.7%) value area region", Order = 13, GroupName = "3. Display Options")]
        public bool ShowSD3Fill { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked 2σ VVAH/VVAL", Description = "Extend untested prior-session 2σ value area boundaries as dashed lines", Order = 14, GroupName = "3. Display Options")]
        public bool ShowNakedSD2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked 3σ VVAH/VVAL", Description = "Extend untested prior-session 3σ value area boundaries as dashed lines", Order = 15, GroupName = "3. Display Options")]
        public bool ShowNakedSD3 { get; set; }

        [NinjaScriptProperty]
        [Range(6, 16)]
        [Display(Name = "Volume Label Font Size", Description = "Font size for volume numbers printed inside profile bars", Order = 16, GroupName = "3. Display Options")]
        public int VolumeFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(6, 16)]
        [Display(Name = "VA Level Label Font Size", Description = "Font size for VVAH/VVAL/SD2H/SD2L/SD3H/SD3L level labels on the profile", Order = 17, GroupName = "3. Display Options")]
        public int VALabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(-300, 300)]
        [Display(Name = "VA Label Horizontal Offset",
                 Description = "Pixels to shift VA level labels left or right. Positive = right (closer to the profile bars). Negative = left (further away).",
                 Order = 18, GroupName = "3. Display Options")]
        public int VALabelXOffset { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "VA Box Line Width", Description = "Stroke width of the VVAH/VVAL, SD2H/L, and SD3H/L value area box outlines", Order = 19, GroupName = "3. Display Options")]
        public int VABoxLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VA Display Style",
                 Description = "Box: draw outline rectangles around each value area (original). Lines: draw a high and low horizontal line for each value area, spanning the full session width.",
                 Order = 20, GroupName = "3. Display Options")]
        public STVPDisplayStyle VADisplayStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show LVN Extension Fill",
                 Description = "Shade every LVN row across the full session width, from the session start bar to the profile right edge, using a semi-transparent tint of the LVN bar color.",
                 Order = 21, GroupName = "3. Display Options")]
        public bool ShowLVNExtension { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Current Session Volume Levels",
                 Description = "Show or hide the current session VPOC, VVAH, VVAL, SD2H, SD2L, SD3H, SD3L lines. Prior session within-session lines and all extension lines are not affected.",
                 Order = 22, GroupName = "3. Display Options")]
        public bool ShowCurrentVolumeLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HVN / LVN Only Mode",
                 Description = "When checked, only High Volume Node (HVN), Low Volume Node (LVN), and VPOC bars are drawn. All Medium Volume Node (MVN) bars are hidden, leaving gaps that make structural nodes immediately visible.",
                 Order = 23, GroupName = "3. Display Options")]
        public bool HvnLvnOnlyMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Volume Bar Display Mode",
                 Description = "Volume: bars sized and colored by total traded volume (default). Delta: bars sized by |buy−sell| tick delta and colored green/red by sign. BidAsk: footprint shows Buy on the LEFT and Sell on the RIGHT (same order as SuperDOM) with volume profile on the far right.",
                 Order = 24, GroupName = "3. Display Options")]
        public STVPBarDisplayMode VolumeBarDisplayMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Buys / Sells",
                 Description = "BidAsk mode: show full-session cumulative Buy (right outer) and Sell (left outer) columns — same as GemsTraderLadder Session Buys/Sells.",
                 Order = 241, GroupName = "3. Display Options")]
        public bool ShowSessionBuysSells { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Buy/Sell Sliding Window",
                 Description = "BidAsk mode: show sliding-window Buy/Sell columns (inner pair when Session is also on) — same as GemsTraderLadder Buy/Sell Sliding Window.",
                 Order = 242, GroupName = "3. Display Options")]
        public bool ShowSlidingWindowBuysSells { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "Buy/Sell Sliding Window (Seconds)",
                 Description = "Time window in seconds for sliding-window buy/sell totals (same idea as GemsTraderLadder). Typical 60–240.",
                 Order = 243, GroupName = "3. Display Options")]
        public int TradeSlidingWindowSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Sliding Window Buy/Sell Totals",
                 Description = "BidAsk mode: show the sliding-window aggregate Buy and Sell totals at the top and bottom of the sliding-window columns (winner highlighted), like the session totals above the session columns.",
                 Order = 245, GroupName = "3. Display Options")]
        public bool ShowSlidingWindowTotals { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Sliding Window Totals Offset (Multiple)",
                 Description = "Distance of the Buy/Sell totals from the active sliding-window range = Ticks Per Row x this multiple, above the top and below the bottom. Example: 4 ticks per row x 3 = 12 ticks. The totals follow the window as it moves.",
                 Order = 246, GroupName = "3. Display Options")]
        public int SlidingWindowTotalsOffsetMultiple { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade Classification",
                 Description = "Midpoint (default): close > (bid+ask)/2 → buy, else sell — same rule as GemsTraderLadder. QuoteRule: price ≥ ask → buy, price ≤ bid → sell, else tick rule. Use Ticks Per Row = 1 for price-level parity with the ladder.",
                 Order = 244, GroupName = "3. Display Options")]
        public STVPTradeClassification TradeClassification { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Imbalance Highlights",
                 Description = "BidAsk mode: highlight diagonal imbalance cells on the footprint (cyan/magenta fills). Diagonal uses Diagonal Imbalance Multiple. Horizontal imbalance colors the buy/sell numbers separately via Horizontal Imbalance Multiple.",
                 Order = 25, GroupName = "3. Display Options")]
        public bool ShowDiagonalImbalance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance Mode",
                 Description = "Legacy setting kept for compatibility. Diagonal and horizontal imbalances now run together: cell fills always use diagonal initiative; buy/sell number colors use horizontal same-level imbalance.",
                 Order = 255, GroupName = "3. Display Options")]
        public STVPImbalanceMode ImbalanceMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100.0)]
        [Display(Name = "Diagonal Imbalance Multiple",
                 Description = "Diagonal initiative: Ask[level] ≥ Multiple × Bid[level−1] (buy) / Bid[level] ≥ Multiple × Ask[level+1] (sell). Used for imbalance cell highlights. Typical 3.",
                 Order = 26, GroupName = "3. Display Options")]
        public double DiagonalImbalanceRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100.0)]
        [Display(Name = "Horizontal Imbalance Multiple",
                 Description = "Same-level imbalance: dominant side ≥ Multiple × opposite side at the same price. When met, the winning buy/sell number uses the same-level dominance color; otherwise both sides stay neutral (white/text color). Typical 3.",
                 Order = 261, GroupName = "3. Display Options")]
        public double HorizontalImbalanceRatio { get; set; }

        [NinjaScriptProperty]
        [Range(0, int.MaxValue)]
        [Display(Name = "Min Imbalance Volume",
                 Description = "BidAsk mode: the dominant cell must have at least this many contracts to count as a diagonal or horizontal imbalance. Filters out noise such as 3 vs 1 at thin price levels (the multiple alone would flag those).",
                 Order = 26, GroupName = "3. Display Options")]
        public int MinImbalanceVolume { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Zone Horizontal Control Floor",
                 Description = "Imbalance Zones: minimum same-level (Ask − Bid) contract difference for a price row to count as buy- or sell-owned when building zones. Independent of the diagonal imbalance multiple.",
                 Order = 26, GroupName = "3. Display Options")]
        public int ZoneHorizontalControlFloor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Imbalance Zones",
                 Description = "BidAsk mode: auction zones built structure-first. (1) HVN/VPOC acceptance body + horizontal bid/ask ownership → strong buy/sell zone. (2) One-sided horizontal control outside acceptance → transition zone. (3) Diagonal imbalance only confirms (does not define) zones.",
                 Order = 27, GroupName = "3. Display Options")]
        public bool ShowImbalanceZones { get; set; }

        [NinjaScriptProperty]
        [Range(2, 20)]
        [Display(Name = "Min Levels For Imbalance Zone",
                 Description = "Minimum consecutive price rows for an acceptance band (HVN/VPOC) or a horizontal-control transition band to form a zone.",
                 Order = 28, GroupName = "3. Display Options")]
        public int MinLevelsForImbalanceZone { get; set; }

        [NinjaScriptProperty]
        [Range(6, 16)]
        [Display(Name = "Label Font Size", Description = "Font size for level labels (VPOC, VVAH, VVAL, SD2H/L, SD3H/L, stats panel)", Order = 14, GroupName = "3. Display Options")]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Extension Line Width", Description = "Stroke width for naked-level and prior-level extension lines", Order = 15, GroupName = "3. Display Options")]
        public int ExtensionLineWidth { get; set; }

        [NinjaScriptProperty]
        [Range(20, 400)]
        [Display(Name = "Current Session VbP Width",
                 Description = "Pixel width of the live session VbP profile (right-aligned to chart edge)", Order = 16, GroupName = "3. Display Options")]
        public int CurrentSessionVbPWidth { get; set; }

        [NinjaScriptProperty]
        [Range(20, 400)]
        [Display(Name = "Prior Session VbP Width",
                 Description = "Pixel width of prior-session VbP profiles (right-aligned to session end bar)", Order = 17, GroupName = "3. Display Options")]
        public int PriorSessionVbPWidth { get; set; }

        [NinjaScriptProperty]
        [Range(0, 400)]
        [Display(Name = "Current Session Right Offset",
                 Description = "Pixels to pull the live VbP profile in from the chart right edge (avoids overlap with other right-anchored indicators)", Order = 18, GroupName = "3. Display Options")]
        public int CurrentSessionRightOffset { get; set; }

        [NinjaScriptProperty]
        [Range(0, 500)]
        [Display(Name = "Level Label Right Offset",
                 Description = "Pixels to shift level labels left from the chart right edge. 0 = flush against the right edge; larger values move labels further left.",
                 Order = 19, GroupName = "3. Display Options")]
        public int LevelLabelRightOffset { get; set; }

        #endregion

        #region Properties — Level Zones

        [NinjaScriptProperty]
        [Display(Name = "Show Level Zones",
                 Description = "All-or-nothing: draw a highlighted zone around every displayed Volume Profile and TPO level (in-session levels and naked extension levels). Zones extend Zone Ticks Up above and Zone Ticks Down below each level.",
                 Order = 1, GroupName = "3A. Level Zones")]
        public bool ShowLevelZones { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Zone Ticks Up", Description = "Ticks above each level to extend the top of the zone (default 8)", Order = 2, GroupName = "3A. Level Zones")]
        public int LevelZoneTicksUp { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Zone Ticks Down", Description = "Ticks below each level to extend the bottom of the zone (default 8)", Order = 3, GroupName = "3A. Level Zones")]
        public int LevelZoneTicksDown { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Level Zone Color", Description = "Fill/outline color for the level zones", Order = 4, GroupName = "3A. Level Zones")]
        public System.Windows.Media.Brush LevelZoneColor { get; set; }
        [Browsable(false)]
        public string LevelZoneColorSerialize
        { get { return Serialize.BrushToString(LevelZoneColor); } set { LevelZoneColor = Serialize.StringToBrush(value); } }

        #endregion

        #region Properties — Colors

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "VPOC Color", Description = "Color for the VPOC bar and dashed VPOC line", Order = 1, GroupName = "4. Colors")]
        public System.Windows.Media.Brush VPOCColor { get; set; }
        [Browsable(false)]
        public string VPOCColorSerialize
        { get { return Serialize.BrushToString(VPOCColor); } set { VPOCColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "HVN Color (High Volume Node)", Description = "Bar color for price rows with volume >= 130% of session average", Order = 2, GroupName = "4. Colors")]
        public System.Windows.Media.Brush HVNColor { get; set; }
        [Browsable(false)]
        public string HVNColorSerialize
        { get { return Serialize.BrushToString(HVNColor); } set { HVNColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "LVN Color (Low Volume Node)", Description = "Bar color for price rows with volume <= 70% of session average", Order = 3, GroupName = "4. Colors")]
        public System.Windows.Media.Brush LVNColor { get; set; }
        [Browsable(false)]
        public string LVNColorSerialize
        { get { return Serialize.BrushToString(LVNColor); } set { LVNColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "MVN Color (Medium Volume)", Description = "Bar color for price rows between LVN and HVN thresholds", Order = 4, GroupName = "4. Colors")]
        public System.Windows.Media.Brush VolumeBarColor { get; set; }
        [Browsable(false)]
        public string VolumeBarColorSerialize
        { get { return Serialize.BrushToString(VolumeBarColor); } set { VolumeBarColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Volume Bar Outline Color", Description = "Outline color drawn around every VbP bar", Order = 5, GroupName = "4. Colors")]
        public System.Windows.Media.Brush VolumeBarOutlineColor { get; set; }
        [Browsable(false)]
        public string VolumeBarOutlineColorSerialize
        { get { return Serialize.BrushToString(VolumeBarOutlineColor); } set { VolumeBarOutlineColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Volume VA Box Color", Description = "Outline color for the VVAH/VVAL box drawn on the profile", Order = 6, GroupName = "4. Colors")]
        public System.Windows.Media.Brush VolumeVABoxColor { get; set; }
        [Browsable(false)]
        public string VolumeVABoxColorSerialize
        { get { return Serialize.BrushToString(VolumeVABoxColor); } set { VolumeVABoxColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Value Area Fill Color", Description = "Fill color shaded over the VVAH–VVAL range", Order = 7, GroupName = "4. Colors")]
        public System.Windows.Media.Brush ValueAreaFillColor { get; set; }
        [Browsable(false)]
        public string ValueAreaFillColorSerialize
        { get { return Serialize.BrushToString(ValueAreaFillColor); } set { ValueAreaFillColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "IB Color (fallback)", Description = "Fallback color for IB lines when formation order is unknown. Also used for IB range text in the stats panel.", Order = 8, GroupName = "4. Colors")]
        public System.Windows.Media.Brush IBColor { get; set; }
        [Browsable(false)]
        public string IBColorSerialize
        { get { return Serialize.BrushToString(IBColor); } set { IBColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "IB First-Formed Side Color", Description = "Color for the IB extreme that printed first (default Orange). This is the side more likely to be rejected.", Order = 81, GroupName = "4. Colors")]
        public System.Windows.Media.Brush IBFirstSideColor { get; set; }
        [Browsable(false)]
        public string IBFirstSideColorSerialize
        { get { return Serialize.BrushToString(IBFirstSideColor); } set { IBFirstSideColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "IB Second Side = High Color", Description = "Color for IB High when it is the second extreme to form (default Green). High formed second → bullish bias.", Order = 82, GroupName = "4. Colors")]
        public System.Windows.Media.Brush IBSecondHighColor { get; set; }
        [Browsable(false)]
        public string IBSecondHighColorSerialize
        { get { return Serialize.BrushToString(IBSecondHighColor); } set { IBSecondHighColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "IB Second Side = Low Color", Description = "Color for IB Low when it is the second extreme to form (default Red). Low formed second → bearish bias.", Order = 83, GroupName = "4. Colors")]
        public System.Windows.Media.Brush IBSecondLowColor { get; set; }
        [Browsable(false)]
        public string IBSecondLowColorSerialize
        { get { return Serialize.BrushToString(IBSecondLowColor); } set { IBSecondLowColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "IB Quartile Line Color", Description = "Color for the 25% / 50% / 75% lines drawn inside the IB range.", Order = 84, GroupName = "4. Colors")]
        public System.Windows.Media.Brush IBQuartileColor { get; set; }
        [Browsable(false)]
        public string IBQuartileColorSerialize
        { get { return Serialize.BrushToString(IBQuartileColor); } set { IBQuartileColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Naked VPOC Color", Description = "Extension line color for untested prior-session VPOC levels", Order = 9, GroupName = "4. Colors")]
        public System.Windows.Media.Brush NakedVPOCColor { get; set; }
        [Browsable(false)]
        public string NakedVPOCColorSerialize
        { get { return Serialize.BrushToString(NakedVPOCColor); } set { NakedVPOCColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Prior/Naked VVAL Color", Description = "Extension line color for prior-session and untested (naked) volume value area LOW (VVAL)", Order = 10, GroupName = "4. Colors")]
        public System.Windows.Media.Brush NakedVAColor { get; set; }
        [Browsable(false)]
        public string NakedVAColorSerialize
        { get { return Serialize.BrushToString(NakedVAColor); } set { NakedVAColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Prior/Naked VVAH Color", Description = "Extension line color for prior-session and untested (naked) volume value area HIGH (VVAH)", Order = 27, GroupName = "4. Colors")]
        public System.Windows.Media.Brush NakedVAHColor { get; set; }
        [Browsable(false)]
        public string NakedVAHColorSerialize
        { get { return Serialize.BrushToString(NakedVAHColor); } set { NakedVAHColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Prior High/Low Color", Description = "Extension line color for prior session high and low", Order = 11, GroupName = "4. Colors")]
        public System.Windows.Media.Brush PriorHighLowColor { get; set; }
        [Browsable(false)]
        public string PriorHighLowColorSerialize
        { get { return Serialize.BrushToString(PriorHighLowColor); } set { PriorHighLowColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Text / Stats Color", Description = "Color for general text: stats panel, session labels", Order = 12, GroupName = "4. Colors")]
        public System.Windows.Media.Brush TextColor { get; set; }
        [Browsable(false)]
        public string TextColorSerialize
        { get { return Serialize.BrushToString(TextColor); } set { TextColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "2σ VA Color (95%)", Description = "Box outline and fill color for the 2-sigma (95%) volume value area", Order = 13, GroupName = "4. Colors")]
        public System.Windows.Media.Brush SD2Color { get; set; }
        [Browsable(false)]
        public string SD2ColorSerialize
        { get { return Serialize.BrushToString(SD2Color); } set { SD2Color = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "3σ VA Color (99.7%)", Description = "Box outline and fill color for the 3-sigma (99.7%) volume value area", Order = 14, GroupName = "4. Colors")]
        public System.Windows.Media.Brush SD3Color { get; set; }
        [Browsable(false)]
        public string SD3ColorSerialize
        { get { return Serialize.BrushToString(SD3Color); } set { SD3Color = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "LVN Extension Fill — Above VPOC", Description = "Fill color for the LVN row-wide extension bands that sit above the session VPOC", Order = 15, GroupName = "4. Colors")]
        public System.Windows.Media.Brush LVNExtAboveColor { get; set; }
        [Browsable(false)]
        public string LVNExtAboveColorSerialize
        { get { return Serialize.BrushToString(LVNExtAboveColor); } set { LVNExtAboveColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "LVN Extension Fill — Below VPOC", Description = "Fill color for the LVN row-wide extension bands that sit below the session VPOC", Order = 16, GroupName = "4. Colors")]
        public System.Windows.Media.Brush LVNExtBelowColor { get; set; }
        [Browsable(false)]
        public string LVNExtBelowColorSerialize
        { get { return Serialize.BrushToString(LVNExtBelowColor); } set { LVNExtBelowColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Delta Positive Color", Description = "Bar color for price rows with positive tick delta (buy vol > sell vol) when Volume Bar Display Mode is set to Delta", Order = 17, GroupName = "4. Colors")]
        public System.Windows.Media.Brush DeltaPositiveColor { get; set; }
        [Browsable(false)]
        public string DeltaPositiveColorSerialize
        { get { return Serialize.BrushToString(DeltaPositiveColor); } set { DeltaPositiveColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Delta Negative Color", Description = "Bar color for price rows with negative tick delta (sell vol > buy vol) when Volume Bar Display Mode is set to Delta", Order = 18, GroupName = "4. Colors")]
        public System.Windows.Media.Brush DeltaNegativeColor { get; set; }
        [Browsable(false)]
        public string DeltaNegativeColorSerialize
        { get { return Serialize.BrushToString(DeltaNegativeColor); } set { DeltaNegativeColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid/Ask — Ask Color", Description = "Color for the ask-lifted (buy aggressor) portion of each bar when Volume Bar Display Mode is set to BidAsk. Fills the right side of the bar proportional to ask volume.", Order = 19, GroupName = "4. Colors")]
        public System.Windows.Media.Brush BidAskAskColor { get; set; }
        [Browsable(false)]
        public string BidAskAskColorSerialize
        { get { return Serialize.BrushToString(BidAskAskColor); } set { BidAskAskColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid/Ask — Bid Color", Description = "Color for the bid-hit (sell aggressor) portion of each bar when Volume Bar Display Mode is set to BidAsk. Fills the left side of the bar proportional to bid volume.", Order = 20, GroupName = "4. Colors")]
        public System.Windows.Media.Brush BidAskBidColor { get; set; }
        [Browsable(false)]
        public string BidAskBidColorSerialize
        { get { return Serialize.BrushToString(BidAskBidColor); } set { BidAskBidColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid/Ask — Buying Imbalance Color", Description = "Highlight color for ask-side cells where Ask[level] ≥ Diagonal Imbalance Multiple × Bid[level−1] (buying pressure imbalance).", Order = 21, GroupName = "4. Colors")]
        public System.Windows.Media.Brush BuyingImbalanceColor { get; set; }
        [Browsable(false)]
        public string BuyingImbalanceColorSerialize
        { get { return Serialize.BrushToString(BuyingImbalanceColor); } set { BuyingImbalanceColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid/Ask — Selling Imbalance Color", Description = "Highlight color for bid-side cells where Bid[level] ≥ Diagonal Imbalance Multiple × Ask[level+1] (selling pressure imbalance).", Order = 22, GroupName = "4. Colors")]
        public System.Windows.Media.Brush SellingImbalanceColor { get; set; }
        [Browsable(false)]
        public string SellingImbalanceColorSerialize
        { get { return Serialize.BrushToString(SellingImbalanceColor); } set { SellingImbalanceColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Buy Zone Color",
                 Description = "Fill and border color for Buying auction zones on the BidAsk profile. Alpha is applied automatically (lighter for transition zones, stronger for HVN/VPOC acceptance zones).",
                 Order = 23, GroupName = "4. Colors")]
        public System.Windows.Media.Brush BuyZoneColor { get; set; }
        [Browsable(false)]
        public string BuyZoneColorSerialize
        { get { return Serialize.BrushToString(BuyZoneColor); } set { BuyZoneColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Sell Zone Color",
                 Description = "Fill and border color for Selling auction zones on the BidAsk profile. Alpha is applied automatically (lighter for transition zones, stronger for HVN/VPOC acceptance zones).",
                 Order = 24, GroupName = "4. Colors")]
        public System.Windows.Media.Brush SellZoneColor { get; set; }
        [Browsable(false)]
        public string SellZoneColorSerialize
        { get { return Serialize.BrushToString(SellZoneColor); } set { SellZoneColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid/Ask — Same-Level Buy Dominance Color",
                 Description = "Text color for the ask (buy) volume label when horizontal imbalance is met (Ask ≥ Horizontal Imbalance Multiple × Bid at the same price). Otherwise the label stays neutral.",
                 Order = 25, GroupName = "4. Colors")]
        public System.Windows.Media.Brush SameLevelBuyDominanceColor { get; set; }
        [Browsable(false)]
        public string SameLevelBuyDominanceColorSerialize
        { get { return Serialize.BrushToString(SameLevelBuyDominanceColor); } set { SameLevelBuyDominanceColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Bid/Ask — Same-Level Sell Dominance Color",
                 Description = "Text color for the bid (sell) volume label when horizontal imbalance is met (Bid ≥ Horizontal Imbalance Multiple × Ask at the same price). Otherwise the label stays neutral.",
                 Order = 26, GroupName = "4. Colors")]
        public System.Windows.Media.Brush SameLevelSellDominanceColor { get; set; }
        [Browsable(false)]
        public string SameLevelSellDominanceColorSerialize
        { get { return Serialize.BrushToString(SameLevelSellDominanceColor); } set { SameLevelSellDominanceColor = Serialize.StringToBrush(value); } }

        #endregion

        #region Properties — VA Level Styles

        // ── VVAH ───────────────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "VVAH Color", Description = "Line color for the 1σ Value Area High boundary", Order = 1, GroupName = "5. VA Level Styles")]
        public System.Windows.Media.Brush VVAHLineColor { get; set; }
        [Browsable(false)]
        public string VVAHLineColorSerialize
        { get { return Serialize.BrushToString(VVAHLineColor); } set { VVAHLineColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "VVAH Line Width", Description = "Stroke width for the VVAH line", Order = 2, GroupName = "5. VA Level Styles")]
        public int VVAHLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VVAH Line Style", Description = "Stroke style for the VVAH line (Solid / Dash / Dot / DashDot)", Order = 3, GroupName = "5. VA Level Styles")]
        public STVPLineStyle VVAHLineStyle { get; set; }

        // ── VVAL ───────────────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "VVAL Color", Description = "Line color for the 1σ Value Area Low boundary", Order = 4, GroupName = "5. VA Level Styles")]
        public System.Windows.Media.Brush VVALLineColor { get; set; }
        [Browsable(false)]
        public string VVALLineColorSerialize
        { get { return Serialize.BrushToString(VVALLineColor); } set { VVALLineColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "VVAL Line Width", Description = "Stroke width for the VVAL line", Order = 5, GroupName = "5. VA Level Styles")]
        public int VVALLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VVAL Line Style", Description = "Stroke style for the VVAL line (Solid / Dash / Dot / DashDot)", Order = 6, GroupName = "5. VA Level Styles")]
        public STVPLineStyle VVALLineStyle { get; set; }

        // ── SD2H ───────────────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "SD2H Color", Description = "Line color for the 2σ Value Area High boundary", Order = 7, GroupName = "5. VA Level Styles")]
        public System.Windows.Media.Brush SD2HLineColor { get; set; }
        [Browsable(false)]
        public string SD2HLineColorSerialize
        { get { return Serialize.BrushToString(SD2HLineColor); } set { SD2HLineColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "SD2H Line Width", Description = "Stroke width for the SD2H line", Order = 8, GroupName = "5. VA Level Styles")]
        public int SD2HLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SD2H Line Style", Description = "Stroke style for the SD2H line (Solid / Dash / Dot / DashDot)", Order = 9, GroupName = "5. VA Level Styles")]
        public STVPLineStyle SD2HLineStyle { get; set; }

        // ── SD2L ───────────────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "SD2L Color", Description = "Line color for the 2σ Value Area Low boundary", Order = 10, GroupName = "5. VA Level Styles")]
        public System.Windows.Media.Brush SD2LLineColor { get; set; }
        [Browsable(false)]
        public string SD2LLineColorSerialize
        { get { return Serialize.BrushToString(SD2LLineColor); } set { SD2LLineColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "SD2L Line Width", Description = "Stroke width for the SD2L line", Order = 11, GroupName = "5. VA Level Styles")]
        public int SD2LLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SD2L Line Style", Description = "Stroke style for the SD2L line (Solid / Dash / Dot / DashDot)", Order = 12, GroupName = "5. VA Level Styles")]
        public STVPLineStyle SD2LLineStyle { get; set; }

        // ── SD3H ───────────────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "SD3H Color", Description = "Line color for the 3σ Value Area High boundary", Order = 13, GroupName = "5. VA Level Styles")]
        public System.Windows.Media.Brush SD3HLineColor { get; set; }
        [Browsable(false)]
        public string SD3HLineColorSerialize
        { get { return Serialize.BrushToString(SD3HLineColor); } set { SD3HLineColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "SD3H Line Width", Description = "Stroke width for the SD3H line", Order = 14, GroupName = "5. VA Level Styles")]
        public int SD3HLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SD3H Line Style", Description = "Stroke style for the SD3H line (Solid / Dash / Dot / DashDot)", Order = 15, GroupName = "5. VA Level Styles")]
        public STVPLineStyle SD3HLineStyle { get; set; }

        // ── SD3L ───────────────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "SD3L Color", Description = "Line color for the 3σ Value Area Low boundary", Order = 16, GroupName = "5. VA Level Styles")]
        public System.Windows.Media.Brush SD3LLineColor { get; set; }
        [Browsable(false)]
        public string SD3LLineColorSerialize
        { get { return Serialize.BrushToString(SD3LLineColor); } set { SD3LLineColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "SD3L Line Width", Description = "Stroke width for the SD3L line", Order = 17, GroupName = "5. VA Level Styles")]
        public int SD3LLineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "SD3L Line Style", Description = "Stroke style for the SD3L line (Solid / Dash / Dot / DashDot)", Order = 18, GroupName = "5. VA Level Styles")]
        public STVPLineStyle SD3LLineStyle { get; set; }

        #endregion


        #region Properties — TPO Settings

        [NinjaScriptProperty]
        [Range(5, 120)]
        [Display(Name = "TPO Period (Minutes)", Description = "Duration of each TPO time period. Use 30 for standard Market Profile.", Order = 1, GroupName = "7. TPO Settings")]
        public int TPOPeriodMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Split Globex / RTH TPO",
                 Description = "ON: build separate TPO profiles and levels for Globex (overnight) and RTH. Both sets of levels are drawn (g / r prefixes). Naked and prior TPO extensions use RTH levels only. OFF: one cumulative TPO for the full session (original behaviour).",
                 Order = 2, GroupName = "7. TPO Settings")]
        public bool SplitGlobexRthTPO { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show TPO Letters", Description = "Render TPO letter columns inside each session", Order = 3, GroupName = "7. TPO Settings")]
        public bool ShowTPOLetters { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show TPO POC", Description = "Draw TPO Point of Control line (highest-letter-count row)", Order = 3, GroupName = "7. TPO Settings")]
        public bool ShowTPOPOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show TPO Value Area", Description = "Draw TPO 1\u03c3 VAH/VAL lines (68% of TPO letters) and shaded fill", Order = 4, GroupName = "7. TPO Settings")]
        public bool ShowTPOVAHVAL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show TPO SD2 Value Area", Description = "Draw TPO 2\u03c3 value area boundaries (95% of TPO letters)", Order = 5, GroupName = "7. TPO Settings")]
        public bool ShowTPOSD2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show TPO SD3 Value Area", Description = "Draw TPO 3\u03c3 value area boundaries (99.7% of TPO letters)", Order = 6, GroupName = "7. TPO Settings")]
        public bool ShowTPOSD3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked TPO POC", Description = "Extend untested prior TPO POC levels as dashed lines", Order = 6, GroupName = "7. TPO Settings")]
        public bool ShowNakedTPOPOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked TPO VAH/VAL", Description = "Extend untested prior TPO VAH/VAL levels as dashed lines", Order = 7, GroupName = "7. TPO Settings")]
        public bool ShowNakedTPOVAHVAL { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked TPO SD2", Description = "Extend untested prior TPO SD2H/SD2L levels as dashed lines", Order = 8, GroupName = "7. TPO Settings")]
        public bool ShowNakedTPOSD2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked TPO SD3", Description = "Extend untested prior TPO SD3H/SD3L levels as dashed lines", Order = 9, GroupName = "7. TPO Settings")]
        public bool ShowNakedTPOSD3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Single Prints", Description = "Highlight single-print zones in the current session (areas with only one TPO letter)", Order = 9, GroupName = "7. TPO Settings")]
        public bool ShowSinglePrints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Naked Single Prints", Description = "Extend untested prior single-print zones as dashed extension zones", Order = 10, GroupName = "7. TPO Settings")]
        public bool ShowNakedSinglePrints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Poor Highs/Lows", Description = "Mark poor (weak) session highs and lows from prior sessions", Order = 11, GroupName = "7. TPO Settings")]
        public bool ShowPoorHighsLows { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Excess", Description = "Mark excess at prior session extremes (1-tick tail = strong rejection)", Order = 12, GroupName = "7. TPO Settings")]
        public bool ShowExcess { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Day Type", Description = "Display day type classification (Normal, Trend, Neutral, etc.) in session stats", Order = 13, GroupName = "7. TPO Settings")]
        public bool ShowDayType { get; set; }

        [NinjaScriptProperty]
        [Range(6, 14)]
        [Display(Name = "TPO Letter Font Size", Description = "Font size for TPO letters rendered in the session", Order = 14, GroupName = "7. TPO Settings")]
        public int TPOFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Current Session TPO Levels",
                 Description = "Show or hide the current session TPO POC, VAH, VAL, SD2H, SD2L lines. Prior session within-session lines and all naked extension lines are not affected.",
                 Order = 15, GroupName = "7. TPO Settings")]
        public bool ShowCurrentTPOLevels { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "TPO Letter Color", Order = 1, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush TPOLetterColor { get; set; }
        [Browsable(false)]
        public string TPOLetterColorSerialize
        { get { return Serialize.BrushToString(TPOLetterColor); } set { TPOLetterColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "TPO POC Color", Order = 2, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush TPOPOCColor { get; set; }
        [Browsable(false)]
        public string TPOPOCColorSerialize
        { get { return Serialize.BrushToString(TPOPOCColor); } set { TPOPOCColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "TPO VAH Color", Order = 3, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush TPOVAHColor { get; set; }
        [Browsable(false)]
        public string TPOVAHColorSerialize
        { get { return Serialize.BrushToString(TPOVAHColor); } set { TPOVAHColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "TPO VAL Color", Order = 4, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush TPOVALColor { get; set; }
        [Browsable(false)]
        public string TPOVALColorSerialize
        { get { return Serialize.BrushToString(TPOVALColor); } set { TPOVALColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Naked TPO POC Color", Order = 5, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush NakedTPOPOCColor { get; set; }
        [Browsable(false)]
        public string NakedTPOPOCColorSerialize
        { get { return Serialize.BrushToString(NakedTPOPOCColor); } set { NakedTPOPOCColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Naked TPO VAH/VAL Color", Order = 6, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush NakedTPOVAColor { get; set; }
        [Browsable(false)]
        public string NakedTPOVAColorSerialize
        { get { return Serialize.BrushToString(NakedTPOVAColor); } set { NakedTPOVAColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "TPO SD2 Color", Order = 7, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush TPOSDTwoColor { get; set; }
        [Browsable(false)]
        public string TPOSDTwoColorSerialize
        { get { return Serialize.BrushToString(TPOSDTwoColor); } set { TPOSDTwoColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Naked TPO SD2 Color", Order = 8, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush NakedTPOSDTwoColor { get; set; }
        [Browsable(false)]
        public string NakedTPOSDTwoColorSerialize
        { get { return Serialize.BrushToString(NakedTPOSDTwoColor); } set { NakedTPOSDTwoColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "TPO SD3 Color", Order = 9, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush TPOSDThreeColor { get; set; }
        [Browsable(false)]
        public string TPOSDThreeColorSerialize
        { get { return Serialize.BrushToString(TPOSDThreeColor); } set { TPOSDThreeColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Naked TPO SD3 Color", Order = 10, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush NakedTPOSDThreeColor { get; set; }
        [Browsable(false)]
        public string NakedTPOSDThreeColorSerialize
        { get { return Serialize.BrushToString(NakedTPOSDThreeColor); } set { NakedTPOSDThreeColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Single Print Color", Order = 9, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush SinglePrintColor { get; set; }
        [Browsable(false)]
        public string SinglePrintColorSerialize
        { get { return Serialize.BrushToString(SinglePrintColor); } set { SinglePrintColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Poor High/Low Color", Order = 10, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush PoorHighLowColor { get; set; }
        [Browsable(false)]
        public string PoorHighLowColorSerialize
        { get { return Serialize.BrushToString(PoorHighLowColor); } set { PoorHighLowColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Excess Color", Order = 11, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush ExcessColor { get; set; }
        [Browsable(false)]
        public string ExcessColorSerialize
        { get { return Serialize.BrushToString(ExcessColor); } set { ExcessColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "TPO VA Box Color", Order = 12, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush TPOVABoxColor { get; set; }
        [Browsable(false)]
        public string TPOVABoxColorSerialize
        { get { return Serialize.BrushToString(TPOVABoxColor); } set { TPOVABoxColor = Serialize.StringToBrush(value); } }

        #endregion


        #region Properties — Analytics Panel

        [NinjaScriptProperty]
        [Display(Name = "Show Analytics Panel",
                 Description = "Show the pre-trade analytics panel (Delta Z-Score, Volume Skewness, VA Migration, TPO Kurtosis) in the top-left of the chart.",
                 Order = 1, GroupName = "10. Analytics Panel")]
        public bool ShowAnalyticsPanel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Analytics Panel Position",
                 Description = "TopLeft: upper-left corner of the chart. TopRight: upper-right corner, inside the price bars area.",
                 Order = 2, GroupName = "10. Analytics Panel")]
        public STVPPanelPosition AnalyticsPanelPosition { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Delta Z-Score",
                 Description = "Show cumulative session delta normalised against a rolling window of prior session final deltas. Use as a tie-breaker at cluster zone touches.",
                 Order = 2, GroupName = "10. Analytics Panel")]
        public bool ShowDeltaZScore { get; set; }

        [NinjaScriptProperty]
        [Range(5, 100)]
        [Display(Name = "Delta Z-Score Lookback Sessions",
                 Description = "Number of completed prior sessions used to compute the delta Z-Score mean and standard deviation.",
                 Order = 3, GroupName = "10. Analytics Panel")]
        public int DeltaZScoreLookbackSessions { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Delta Z-Score Threshold",
                 Description = "Z-Score magnitude at which the reading is considered extreme. Values beyond ±Threshold suggest the tested level will break rather than hold.",
                 Order = 4, GroupName = "10. Analytics Panel")]
        public double DeltaZScoreThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Volume Skewness",
                 Description = "Show skewness of the most recent completed session's volume distribution around VPOC. Right-skewed = bullish lean, left-skewed = bearish lean.",
                 Order = 5, GroupName = "10. Analytics Panel")]
        public bool ShowVolumeSkewness { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VA Migration Regime",
                 Description = "Show a pre-trade regime label based on how value areas have migrated across recent sessions (Trending Up/Down, Balanced, or Converging).",
                 Order = 6, GroupName = "10. Analytics Panel")]
        public bool ShowVAMigration { get; set; }

        [NinjaScriptProperty]
        [Range(2, 10)]
        [Display(Name = "VA Migration Lookback (sessions)",
                 Description = "Number of completed sessions used to determine the VA migration regime.",
                 Order = 7, GroupName = "10. Analytics Panel")]
        public int VAMigrationLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show TPO Kurtosis",
                 Description = "Show kurtosis of the developing TPO distribution. High kurtosis early in session predicts a balanced day; low kurtosis predicts a trend day.",
                 Order = 8, GroupName = "10. Analytics Panel")]
        public bool ShowTPOKurtosis { get; set; }

        #endregion

        #region Properties — Level Export

        [NinjaScriptProperty]
        [Display(Name        = "Print Levels At Session Start",
                 Description = "Prints prior session levels to the Output window automatically when a new session begins.",
                 Order = 1, GroupName = "11. Level Export")]
        public bool PrintLevelsAtSessionStart { get; set; }

        [NinjaScriptProperty]
        [Display(Name        = "Print Levels On Demand (Ctrl+P)",
                 Description = "Press Ctrl+P while chart has focus to print prior session levels and all active naked levels on demand.",
                 Order = 2, GroupName = "11. Level Export")]
        public bool PrintLevelsOnDemand { get; set; }

        #endregion

        #region Properties — Regime & Dominance

        [NinjaScriptProperty]
        [Display(Name = "Enable Regime Detection",
                 Description = "Master switch for the non-lagging Discovery / Auction regime + dominance engine.",
                 Order = 1, GroupName = "12. Regime & Dominance")]
        public bool EnableRegimeDetection { get; set; }

        [NinjaScriptProperty]
        [Range(3, 30)]
        [Display(Name = "Dominance Window (Bars)",
                 Description = "Number of recent primary bars used to measure aggressive bid/ask dominance. Pure order-flow window — no moving averages.",
                 Order = 2, GroupName = "12. Regime & Dominance")]
        public int DominanceWindowBars { get; set; }

        [Range(-1, 30)]
        [Display(Name = "Control Window (Bars, 0 = off, -1 = same as Dominance Window)",
                 Description = "Bars of aggressor flow used for the persistence part of the Control score (30% weight). 0 = drop persistence entirely: Control becomes 0.57 x bar delta + 0.43 x price direction (still spans -1..+1). -1 = use the Dominance Window (original behaviour). Affects Control only; the Discovery/Auction regime keeps using the Dominance Window.",
                 Order = 21, GroupName = "12. Regime & Dominance")]
        public int ControlWindowBars { get; set; }

        [Display(Name = "Log Control To Output (new bars)",
                 Description = "Realtime only. From the start of each new primary bar, prints a line to the NinjaScript Output window on every tick: time, price, bar contracts, Control, and its parts (bar delta, price direction, persistence), until the bar reaches the contract count below. Tags Clear BUY / Clear SELL when |Control| >= the Clear/Strong Gate.",
                 Order = 22, GroupName = "12. Regime & Dominance")]
        public bool LogControlOnNewBar { get; set; }

        [Display(Name = "Log Volumetric Bar To Control",
                 Description = "When the primary chart series is NT8 Volumetric bars, print each bar's footprint-style bid/ask, delta, POC-in-bar, and strongest diagonal imbalance rows into the Control log. No effect on time/tick/range bars that are not Volumetric. Safe to leave ON.",
                 Order = 25, GroupName = "12. Regime & Dominance")]
        public bool LogVolumetricToControl { get; set; }

        [Range(1, 1000000)]
        [Display(Name = "Control Log: Stop At Bar Contracts",
                 Description = "The Control log for a bar stops once the forming bar has traded this many contracts (the line that crosses it is printed and marked).",
                 Order = 23, GroupName = "12. Regime & Dominance")]
        public int ControlLogContracts { get; set; }

        [Display(Name = "Control Log: Continue Until IC Fires",
                 Description = "After the bar reaches the contract count, keep logging (only when Control changes arm zone: IC-arm BUY / SELL / neutral) until IN CHARGE actually fires on that bar or the bar ends. An IC FIRE line (time, price, contracts, Control) is printed when it fires.",
                 Order = 24, GroupName = "12. Regime & Dominance")]
        public bool ControlLogUntilFire { get; set; }

        [Range(0, 100000)]
        [Display(Name = "Control Log: Print Every N Contracts (0 = every tick)",
                 Description = "Sampling for the Control log. A line prints each time the bar passes another multiple of this many contracts, and ALWAYS prints when Control changes arm zone (IC-arm BUY / SELL / neutral). Default 0 prints a line on every volume change (full detail, a lot of lines at 10,000 contracts).",
                 Order = 25, GroupName = "12. Regime & Dominance")]
        public int ControlLogStepContracts { get; set; }

        [Display(Name = "Control Log: Bar Summary Line",
                 Description = "Off by default. When on, one BAR SUMMARY line prints as each bar ends (final Control, peak/trough with contract counts, first arm, zone changes).",
                 Order = 26, GroupName = "12. Regime & Dominance")]
        public bool ControlLogBarSummary { get; set; }

        [NinjaScriptProperty]
        [Range(0.15, 0.90)]
        [Display(Name = "Discovery Threshold",
                 Description = "RegimeScore above this value is classified as Discovery (one-sided).",
                 Order = 3, GroupName = "12. Regime & Dominance")]
        public double DiscoveryThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(-0.90, -0.15)]
        [Display(Name = "Auction Threshold",
                 Description = "RegimeScore below this value is classified as Auction (two-sided).",
                 Order = 4, GroupName = "12. Regime & Dominance")]
        public double AuctionThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.30, 0.95)]
        [Display(Name = "Imbalance Intensity Floor",
                 Description = "Minimum abs(delta)/volume over the dominance window required to contribute to one-sidedness. Lower = more sensitive.",
                 Order = 5, GroupName = "12. Regime & Dominance")]
        public double ImbalanceIntensityFloor { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Level-2 Book",
                 Description = "Include market-depth contribution in regime/dominance for ALL bars. Live: real Level-2 book + absorption. Historical (or when L2 not flowing): tick-derived depth proxy so historical bands always include a depth signal.",
                 Order = 6, GroupName = "12. Regime & Dominance")]
        public bool UseLevel2Book { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "L2 Depth Levels",
                 Description = "How many book levels (from the touch) to include when computing book imbalance. 1 = best bid/ask only.",
                 Order = 7, GroupName = "12. Regime & Dominance")]
        public int L2DepthLevels { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "L2 Weight on Dominance",
                 Description = "How strongly Level-2 book imbalance modulates the dominance reading (0 = ignore L2, 1 = full weight).",
                 Order = 8, GroupName = "12. Regime & Dominance")]
        public double L2DominanceWeight { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "L2 Absorption Weight",
                 Description = "How strongly detected absorption (aggressive volume failing against resting size) pulls RegimeScore toward Auction.",
                 Order = 9, GroupName = "12. Regime & Dominance")]
        public double L2AbsorptionWeight { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Regime Background",
                 Description = "Tint the price-panel background according to the current regime (Discovery / Auction / Transition).",
                 Order = 10, GroupName = "12. Regime & Dominance")]
        public bool ShowRegimeBackground { get; set; }

        [Display(Name = "Show Detail Line (score / control numbers)",
                 Description = "Adds one small line with the raw numbers behind the plain-English panel. Off by default.",
                 Order = 19, GroupName = "12. Regime & Dominance")]
        public bool ShowRegimeDetailLine { get; set; }

        [Display(Name = "Show Depth History Strip (advanced)",
                 Description = "Optional third strip showing book/depth imbalance per bar. Historical bars use a tick-derived proxy; real Level 2 is only available live. Off by default.",
                 Order = 20, GroupName = "12. Regime & Dominance")]
        public bool ShowDepthHistoryStrip { get; set; }

        [Display(Name = "Show Market + Who's-In-Charge History Strips",
                 Description = "Draw per-bar strips across the chart: top strip = who is in charge (buyers/sellers), second strip = book/depth imbalance. Bright = real Level-2, dim = tick-derived proxy (historical).",
                 Order = 18, GroupName = "12. Regime & Dominance")]
        public bool ShowRegimeHistoryStrips { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Regime Panel",
                 Description = "Display a compact status panel showing RegimeState, RegimeScore and Dominance.",
                 Order = 11, GroupName = "12. Regime & Dominance")]
        public bool ShowRegimePanel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Regime Panel Position",
                 Description = "Where to place the regime status panel.",
                 Order = 12, GroupName = "12. Regime & Dominance")]
        public STVPPanelPosition RegimePanelPosition { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Discovery Background Color",
                 Description = "Semi-transparent fill used when regime is Discovery.",
                 Order = 13, GroupName = "12. Regime & Dominance")]
        public System.Windows.Media.Brush DiscoveryBgColor { get; set; }
        [Browsable(false)]
        public string DiscoveryBgColorSerialize
        { get { return Serialize.BrushToString(DiscoveryBgColor); } set { DiscoveryBgColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Auction Background Color",
                 Description = "Semi-transparent fill used when regime is Auction.",
                 Order = 14, GroupName = "12. Regime & Dominance")]
        public System.Windows.Media.Brush AuctionBgColor { get; set; }
        [Browsable(false)]
        public string AuctionBgColorSerialize
        { get { return Serialize.BrushToString(AuctionBgColor); } set { AuctionBgColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Transition Background Color",
                 Description = "Semi-transparent fill used when regime is Transition.",
                 Order = 15, GroupName = "12. Regime & Dominance")]
        public System.Windows.Media.Brush TransitionBgColor { get; set; }
        [Browsable(false)]
        public string TransitionBgColorSerialize
        { get { return Serialize.BrushToString(TransitionBgColor); } set { TransitionBgColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Buyer Dominance Color",
                 Description = "Color used for buyer-side dominance text / accent.",
                 Order = 16, GroupName = "12. Regime & Dominance")]
        public System.Windows.Media.Brush BuyerDominanceColor { get; set; }
        [Browsable(false)]
        public string BuyerDominanceColorSerialize
        { get { return Serialize.BrushToString(BuyerDominanceColor); } set { BuyerDominanceColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Seller Dominance Color",
                 Description = "Color used for seller-side dominance text / accent.",
                 Order = 17, GroupName = "12. Regime & Dominance")]
        public System.Windows.Media.Brush SellerDominanceColor { get; set; }
        [Browsable(false)]
        public string SellerDominanceColorSerialize
        { get { return Serialize.BrushToString(SellerDominanceColor); } set { SellerDominanceColor = Serialize.StringToBrush(value); } }

        #endregion

        #region Properties — Trade Signals












        [Range(0, 2359)]
        [Display(Name = "Last Signal Time (HHMM, chart time)", Description = "No setup/IC signals or auto-trade entries at/after this chart time. Open setup signals are closed out. Auto-trader also blocks new entries. 1400 = 14:00. 0 = off.", Order = 11, GroupName = "13. Trade Signals")]
        public int SignalEndHHMM { get; set; }








        [Display(Name = "Show Entry / Stop / Target Lines", Description = "Draw horizontal entry, stop and TP lines from each signal until it resolves.", Order = 19, GroupName = "13. Trade Signals")]
        public bool ShowSignalLines { get; set; }









        [Range(1, 10000)]
        [Display(Name = "Stop-Loss Ticks", Description = "Stop distance in ticks from entry/fill. This is the only stop mode — no ATR or structure.", Order = 35, GroupName = "13. Trade Signals")]
        public int StopLossTicks { get; set; }

        [Range(1, 10000)]
        [Display(Name = "Take-Profit Ticks", Description = "Take-profit distance in ticks from entry/fill. This is the only target mode — no ATR or structure.", Order = 36, GroupName = "13. Trade Signals")]
        public int TakeProfitTicks { get; set; }






        [Display(Name = "Alert On Live Signals", Description = "Popup / sound when a new signal closes on a live bar.", Order = 20, GroupName = "13. Trade Signals")]
        public bool SignalAlerts { get; set; }






















        [NinjaScriptProperty]
        [Display(Name = "Enable Value Area Rejection",
                 Description = "Rejection entries at prior-session and naked TPO/volume VAH/VAL and session highs/lows only (not POC). Long at support with aligned control; short at resistance with aligned control.",
                 Order = 440, GroupName = "13. Trade Signals")]
        public bool EnableValueAreaRejection { get; set; }

        [Range(2, 40)]
        [Display(Name = "VA Test Proximity (ticks)",
                 Description = "How close price must be to a candidate level (prior or naked) to count as a test. Symmetric band. YM often 4–8; NQ 6–12.",
                 Order = 441, GroupName = "13. Trade Signals")]
        public int VATestProximityTicks { get; set; }


        [NinjaScriptProperty]
        [Display(Name = "VA Prefer TPO Levels",
                 Description = "When ON, prefer TPO VAH/VAL/POC over volume levels for the same session. When OFF, prefer volume levels.",
                 Order = 443, GroupName = "13. Trade Signals")]
        public bool VAPreferTpoLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VA Include Naked Levels",
                 Description = "Also test untested (naked) TPO/volume VAH and VAL from older sessions (not POC).",
                 Order = 444, GroupName = "13. Trade Signals")]
        public bool VAIncludeNakedLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VA Include High / Low",
                 Description = "Include prior-session high and low, and naked (untested) highs/lows from older sessions, as rejection levels.",
                 Order = 445, GroupName = "13. Trade Signals")]
        public bool VAIncludeHighLow { get; set; }

        [Range(1, 20)]
        [Display(Name = "VA Naked Lookback (sessions)",
                 Description = "How many completed prior sessions to scan for naked levels and naked highs/lows (most recent prior is always included for classic VAH/VAL).",
                 Order = 446, GroupName = "13. Trade Signals")]
        public int VANakedLookbackSessions { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VA Require Cum Delta Confirm",
                 Description = "Prior/naked rejection: bar aggressor delta (ask−bid) must agree with the trade. Long needs buy delta on the bar; short needs sell delta. Same idea as MR cum-delta confirm.",
                 Order = 447, GroupName = "13. Trade Signals")]
        public bool VARequireCumDeltaConfirm { get; set; }

        [Range(0, 500)]
        [Display(Name = "VA Cum Delta Min Contracts",
                 Description = "Minimum |ask−bid| on the forming bar for prior/naked VA rejection. 0 = any same-sign delta.",
                 Order = 448, GroupName = "13. Trade Signals")]
        public int VACumDeltaMinContracts { get; set; }


        [NinjaScriptProperty]
        [Display(Name = "Enable Mean-Revert VA Edge",
                 Description = "Current-session developing TPO VAL/VAH rejection after IB. Long when price tests developing TPO VAL; short when price tests developing TPO VAH. Optional cum-delta and control filters. L2 walls are optional annotation only, not required.",
                 Order = 450, GroupName = "13. Trade Signals")]
        public bool EnableMeanRevertVAEdge { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MR Require TPO VAL/VAH",
                 Description = "Deprecated — MR always uses current-session developing TPO VAL (long) and VAH (short) rejection. Kept for workspace compatibility; ignored.",
                 Order = 4505, GroupName = "13. Trade Signals")]
        public bool MRRequireTpoVaEdge { get; set; }

        [Range(2, 40)]
        [Display(Name = "MR VA Edge Proximity (ticks)",
                 Description = "How close price must be to the developing TPO VAL (long) or TPO VAH (short) to count as a rejection test.",
                 Order = 451, GroupName = "13. Trade Signals")]
        public int MRVAEdgeProximityTicks { get; set; }

        [Range(1, 20)]
        [Display(Name = "MR Wall Proximity (ticks)",
                 Description = "Optional: if an L2 wall of the correct side is within this many ticks, it is noted on the MR FIRE log. Walls are NOT required to fire the trade.",
                 Order = 4515, GroupName = "13. Trade Signals")]
        public int MRWallProximityTicks { get; set; }


        [NinjaScriptProperty]
        [Display(Name = "MR Require Imbalance or L2",
                 Description = "DEPRECATED — MR entries now require an L2 wall only (imbalance levels are not used). Property kept for workspace compatibility; ignored.",
                 Order = 453, GroupName = "13. Trade Signals")]
        public bool MRRequireImbalanceOrL2 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MR Require Cum Delta Confirm",
                 Description = "Bar aggressor delta (ask−bid) must agree with the trade: long needs buy delta on the bar, short needs sell delta. Matches the Cum Delta / bar delta confirmation on the chart (gray arrow). Recommended ON.",
                 Order = 455, GroupName = "13. Trade Signals")]
        public bool MRRequireCumDeltaConfirm { get; set; }

        [Range(0, 500)]
        [Display(Name = "MR Cum Delta Min Contracts",
                 Description = "Minimum |ask−bid| on the forming bar to count as Cum Delta confirmation. 0 = any same-sign delta.",
                 Order = 456, GroupName = "13. Trade Signals")]
        public int MRCumDeltaMinContracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "MR One Signal Per Edge",
                 Description = "When ON, each VAL (long) / VAH (short) price level fires only once per session. When OFF (default), the same edge can fire again on a later revisit — e.g. after an early weak touch and a later real move.",
                 Order = 457, GroupName = "13. Trade Signals")]
        public bool MROneSignalPerEdge { get; set; }








        [Range(8, 500)]
        [Display(Name = "Gap Trend Min (ticks)",
                 Description = "If |gap vs prior close| exceeds this, day bias forces TrendUp/TrendDown until price re-enters a large portion of the gap. NQ: 80+ ticks (~20 pts). ES: 40+.",
                 Order = 49, GroupName = "13. Trade Signals")]
        public int GapTrendMinTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trend Runner TP",
                 Description = "When day bias is TrendUp/TrendDown, extend take-profit beyond 1R toward the next imbalance level (short red/green profile lines) or opposing zone edge. Caps at Runner Max R. Stops still use min auction risk. On 1R favorable, stop can move to break-even if Auto-Manage is ON.",
                 Order = 50, GroupName = "13. Trade Signals")]
        public bool EnableTrendRunner { get; set; }

        [Range(1.5, 6.0)]
        [Display(Name = "Runner Min R",
                 Description = "Minimum reward multiple of risk on trend-bias entries (e.g. 2.0 = at least 2R target).",
                 Order = 51, GroupName = "13. Trade Signals")]
        public double RunnerMinR { get; set; }

        [Range(2.0, 10.0)]
        [Display(Name = "Runner Max R",
                 Description = "Cap on trend runner target as multiple of risk (e.g. 4.0).",
                 Order = 52, GroupName = "13. Trade Signals")]
        public double RunnerMaxR { get; set; }








        #endregion

        #region Properties — Auto Trade (Sim / Live)

        [NinjaScriptProperty]
        [Display(Name = "Enable Trade Entries (master on/off)",
                 Description = "Master switch. OFF = alerts and chart marks only, no orders. ON = when a live alert fires, enter a market order with ATR stop-loss and take-profit (draggable on chart). Use a Sim account first.",
                 Order = 1, GroupName = "14. Auto Trade (Sim / Live)")]
        public bool EnableAutoTrade { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Account Name",
                 Description = "Exact account name as shown in NinjaTrader (e.g. Sim101). Must match a connected account.",
                 Order = 2, GroupName = "14. Auto Trade (Sim / Live)")]
        public string AutoTradeAccountName { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Quantity",
                 Description = "Contracts / shares per auto-trade entry.",
                 Order = 3, GroupName = "14. Auto Trade (Sim / Live)")]
        public int AutoTradeQuantity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Only Trade When Flat",
                 Description = "If on, skip new entries while this instrument already has an open position on the selected account.",
                 Order = 4, GroupName = "14. Auto Trade (Sim / Live)")]
        public bool AutoTradeOnlyWhenFlat { get; set; }

        [Display(Name = "Entry Order Type",
                 Description = "Market: enter at market the moment the alert fires (current behaviour). LimitAtSignal: place a limit order at the signal's entry price (buy limit for longs, sell limit for shorts) \u2014 it fills at that price or better, and may never fill if price runs away. Stop and target are attached when it fills.",
                 Order = 5, GroupName = "14. Auto Trade (Sim / Live)")]
        public STVPEntryOrderType AutoTradeEntryOrderType { get; set; }

        [Range(0, 3600)]
        [Display(Name = "Limit Cancel After (seconds)",
                 Description = "Limit At Signal only: cancel the unfilled limit entry after this many seconds. 0 = cancel when the signal bar closes. While a limit entry is working, new auto entries are blocked, so an expiry is always applied.",
                 Order = 6, GroupName = "14. Auto Trade (Sim / Live)")]
        public int AutoTradeLimitCancelSeconds { get; set; }

        [Range(0, 3600)]
        [Display(Name = "RTH Open Blackout (seconds)",
                 Description = "Auto-trader places NO entries from the RTH Start Time until this many seconds after it (e.g. 60). Signals and alerts still fire and manual trades are unaffected. Uses chart time, same as RTH Start Time. 0 = off.",
                 Order = 20, GroupName = "14. Auto Trade (Sim / Live)")]
        public int AutoTradeRthBlackoutSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 240)]
        [Display(Name = "Auto-Trade Start After RTH (minutes)",
                 Description = "Minutes after Group 1 RTH Start Time before MR / prior-naked signals and auto-trade entries are allowed. Chart time (Chicago if your chart is set that way). Independent of IB Duration. Example: RTH Start 08:30 and 30 → trading allowed from 09:00. 0 = allow from RTH open (subject to optional RTH Open Blackout seconds).",
                 Order = 19, GroupName = "14. Auto Trade (Sim / Live)")]
        public int AutoTradeStartMinutesAfterRth { get; set; }

        [Range(0, 2359)]
        [Display(Name = "Force Flat Time (HHMM, chart time)",
                 Description = "Auto-trader flattens any open STVP position at/after this chart time and blocks new auto entries from then on. 1500 = 15:00 (3:00 PM). Uses the same clock as Last Signal Time / RTH times. 0 = off (no forced flatten).",
                 Order = 21, GroupName = "14. Auto Trade (Sim / Live)")]
        public int AutoTradeForceFlatHHMM { get; set; }

        [Range(0, 600)]
        [Display(Name = "Force Flat Minutes Before Close",
                 Description = "Auto-trader flattens and blocks new entries this many minutes before the end of the current trading-hours-template session. Follows the template, so it works for day-only markets (HE / LE) as well as overnight products. When > 0 it replaces Force Flat Time (HHMM). 0 = off (Force Flat Time is used).",
                 Order = 22, GroupName = "14. Auto Trade (Sim / Live)")]
        public int AutoTradeForceFlatMinutesBeforeClose { get; set; }



        #endregion

        #region Properties — L2 Walls

        [NinjaScriptProperty]
        [Display(Name = "Enable Wall Monitor",
                 Description = "Live only: detect large resting Level-2 walls by price, track Seen/Testing/Absorbing/Broken/Pulled, draw them at the chart right edge, and tag IC signals that have a wall ahead (observe-only).",
                 Order = 1, GroupName = "15. L2 Walls")]
        public bool EnableWallMonitor { get; set; }

        [NinjaScriptProperty]
        [Range(5, 5000)]
        [Display(Name = "Wall Min Size (contracts)",
                 Description = "Minimum resting size to DETECT and track a wall (chart + in-trade monitor). NQ default 15 so stacks like 15–25 that can stall a move are visible. IC entry block uses Block IC Min Wall Size separately.",
                 Order = 2, GroupName = "15. L2 Walls")]
        public int WallMinSize { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 20.0)]
        [Display(Name = "Wall Median Multiple",
                 Description = "Level size must be at least this multiple of the near-touch median. NQ default 2× so modest but real passive stacks still qualify on a thin book.",
                 Order = 3, GroupName = "15. L2 Walls")]
        public double WallMedianMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Near Levels For Median",
                 Description = "How many price levels nearest the touch (each side) feed the median used for the relative wall filter.",
                 Order = 4, GroupName = "15. L2 Walls")]
        public int WallNearLevels { get; set; }

        [NinjaScriptProperty]
        [Range(5, 2000)]
        [Display(Name = "Wall Scan Range (ticks)",
                 Description = "How far above the ask / below the bid to scan for walls. NQ default 200 ticks (= 50 points). Raise further if you want deeper book context for IC block and markers.",
                 Order = 5, GroupName = "15. L2 Walls")]
        public int WallScanRangeTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 30.0)]
        [Display(Name = "Wall Persist (seconds)",
                 Description = "Minimum time size must remain at a price before it qualifies as a wall. NQ default 1s — fast enough for in-trade exits, still filters pure flicker.",
                 Order = 6, GroupName = "15. L2 Walls")]
        public double WallPersistSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Wall Testing Distance (ticks)",
                 Description = "Price within this many ticks of the wall → Testing state.",
                 Order = 7, GroupName = "15. L2 Walls")]
        public int WallTestingTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.25, 3.0)]
        [Display(Name = "Absorb Ratio",
                 Description = "Aggressive volume traded into the wall price band ≥ this fraction of the wall's original size (while size still holds or refills) → Absorbing.",
                 Order = 8, GroupName = "15. L2 Walls")]
        public double WallAbsorbRatio { get; set; }

        [NinjaScriptProperty]
        [Range(0.3, 1.0)]
        [Display(Name = "Pull Fraction",
                 Description = "If estimated cancelled size reaches this fraction of peak size without price trading through → Pulled.",
                 Order = 9, GroupName = "15. L2 Walls")]
        public double WallPullFraction { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Walls On Chart",
                 Description = "Draw active wall markers at the right edge of the price panel (grey/side-colored Seen, yellow Testing, orange Absorbing, dim Broken/Pulled).",
                 Order = 10, GroupName = "15. L2 Walls")]
        public bool ShowWallsOnChart { get; set; }












        [NinjaScriptProperty]
        [Range(50, 400)]
        [Display(Name = "Wall Marker Width (pixels)",
                 Description = "Horizontal width of wall bars drawn at the right edge.",
                 Order = 21, GroupName = "15. L2 Walls")]
        public int WallMarkerWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Block Entry On Large Opposing Level (Phase A)",
                 Description = "Skip IC emit and auto-trade when the LARGEST single opposing level within the lookahead is at least Entry Max Largest Level. Ignores thin full-book sums (normal NQ ladder). In-trade CAUTION/EXIT still use total oppSum.",
                 Order = 22, GroupName = "15. L2 Walls")]
        public bool BlockEntryOnOpposingSum { get; set; }

        [NinjaScriptProperty]
        [Range(5, 5000)]
        [Display(Name = "Entry Max Largest Level",
                 Description = "Phase A: block new entry if any single opposing price has at least this many contracts (bids under a sell, asks over a buy). NQ default 30 — matches walls that actually stall price, not 100+ lots spread across 40 thin levels.",
                 Order = 23, GroupName = "15. L2 Walls")]
        public int EntryMaxOpposingSize { get; set; }




        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Tick-accurate session-based Volume by Price with TPO Market Profile, plus non-lagging Discovery/Auction regime detection and aggressor dominance.";
                Name                     = "SessionTPOVolumeProfile";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                IsSuspendedWhileInactive = false;

                // RTH sub-period times (ES / NQ defaults — set to match your chart template)
                RTHStartTime = new TimeSpan(8, 30, 0);
                RTHEndTime   = new TimeSpan(16, 0, 0);
                RthMode      = STVPRthMode.Auto;
                MergeTemplateSessions = false;

                // Profile settings
                ProfilePeriod         = STVPProfilePeriod.Session;
                TicksPerRow           = 1;   // 1 tick per row so buy/sell numbers match SuperDOM ladder price levels
                IBDurationMinutes     = 60;
                MaxSessionsToShow     = 20;

                // Display defaults
                ShowVPOC               = true;
                ShowVolumeValueArea    = true;
                ShowInitialBalance     = true;
                ShowIBQuartiles        = true;
                ShowIBBiasLabel        = true;
                ShowNakedVPOC          = true;
                ShowPriorSessionVPOC   = true;
                ShowNakedVAHVAL        = true;
                ShowSD2VA              = true;
                ShowSD3VA              = true;
                ShowVA1Fill            = true;   // 1σ fill on by default (matches original)
                ShowSD2Fill            = false;  // off by default — enable when needed
                ShowSD3Fill            = false;  // off by default — enable when needed
                ShowNakedSD2           = true;
                ShowNakedSD3           = true;
                ShowPriorSessionLevels = true;
                ShowVolumeLabels       = true;
                ShowPerBarBidAskTotals = true;
                ShowSessionStats       = true;
                VolumeFontSize         = 8;
                VALabelFontSize        = 9;
                VALabelXOffset         = 0;
                VABoxLineWidth         = 2;
                VADisplayStyle         = STVPDisplayStyle.Box;
                ShowLVNExtension       = false;  // off by default — enable when needed
                ShowCurrentVolumeLevels = true;   // hide current session volume lines when using prior levels only
                ShowLevelZones         = true;   // level zones (VP + TPO) — all-or-nothing toggle
                LevelZoneTicksUp       = 8;
                LevelZoneTicksDown     = 8;
                LevelZoneColor         = System.Windows.Media.Brushes.DodgerBlue;
                HvnLvnOnlyMode         = false;  // off by default — shows all node types (HVN + MVN + LVN)
                VolumeBarDisplayMode   = STVPBarDisplayMode.Volume;  // default to standard volume bars
                ShowSessionBuysSells   = true;
                ShowSlidingWindowBuysSells = true;
                TradeSlidingWindowSeconds = 60;
                ShowSlidingWindowTotals = true;
                SlidingWindowTotalsOffsetMultiple = 3;
                TradeClassification    = STVPTradeClassification.Midpoint;  // same rule as GemsTraderLadder
                LabelFontSize          = 9;
                ExtensionLineWidth     = 2;
                CurrentSessionVbPWidth    = 80;
                PriorSessionVbPWidth      = 50;
                CurrentSessionRightOffset = 90;
                LevelLabelRightOffset     = 120;

                // Colors
                VPOCColor          = System.Windows.Media.Brushes.Cyan;
                HVNColor           = System.Windows.Media.Brushes.Magenta;
                LVNColor           = System.Windows.Media.Brushes.Gray;
                VolumeBarColor     = System.Windows.Media.Brushes.DodgerBlue;
                VolumeBarOutlineColor = System.Windows.Media.Brushes.Black;
                VolumeVABoxColor   = System.Windows.Media.Brushes.MediumOrchid;

                var vaFill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(30, 65, 105, 225));
                vaFill.Freeze();
                ValueAreaFillColor = vaFill;

                IBColor           = System.Windows.Media.Brushes.Lime;
                IBFirstSideColor  = System.Windows.Media.Brushes.Orange;
                IBSecondHighColor = System.Windows.Media.Brushes.LimeGreen;
                IBSecondLowColor  = System.Windows.Media.Brushes.Red;
                IBQuartileColor   = System.Windows.Media.Brushes.DimGray;
                NakedVPOCColor    = System.Windows.Media.Brushes.Yellow;
                NakedVAColor      = System.Windows.Media.Brushes.DarkRed;     // prior/naked VVAL
                NakedVAHColor     = System.Windows.Media.Brushes.Green;       // prior/naked VVAH
                PriorHighLowColor = System.Windows.Media.Brushes.Gray;
                TextColor         = System.Windows.Media.Brushes.White;

                // 2σ (95 %) — warm blue-green to distinguish from 1σ
                SD2Color = System.Windows.Media.Brushes.DeepSkyBlue;
                // 3σ (99.7 %) — gold to mark extreme extension
                SD3Color = System.Windows.Media.Brushes.Gold;

                // Per-level VA line styles — defaults mirror the existing band colors so
                // existing charts look identical until the user changes individual levels.
                VVAHLineColor = System.Windows.Media.Brushes.MediumOrchid;
                VVAHLineWidth = 2;
                VVAHLineStyle = STVPLineStyle.Solid;

                VVALLineColor = System.Windows.Media.Brushes.MediumOrchid;
                VVALLineWidth = 2;
                VVALLineStyle = STVPLineStyle.Solid;

                SD2HLineColor = System.Windows.Media.Brushes.DeepSkyBlue;
                SD2HLineWidth = 2;
                SD2HLineStyle = STVPLineStyle.Solid;

                SD2LLineColor = System.Windows.Media.Brushes.DeepSkyBlue;
                SD2LLineWidth = 2;
                SD2LLineStyle = STVPLineStyle.Solid;

                SD3HLineColor = System.Windows.Media.Brushes.Gold;
                SD3HLineWidth = 2;
                SD3HLineStyle = STVPLineStyle.Solid;

                SD3LLineColor = System.Windows.Media.Brushes.Gold;
                SD3LLineWidth = 2;
                SD3LLineStyle = STVPLineStyle.Solid;

                // LVN extension fill — defaults to the same gray-25% that was previously hardcoded,
                // split so above-VPOC and below-VPOC rows can be styled independently.
                var lvnAbove = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(64, 128, 128, 128));
                lvnAbove.Freeze();
                LVNExtAboveColor = lvnAbove;

                var lvnBelow = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(64, 128, 128, 128));
                lvnBelow.Freeze();
                LVNExtBelowColor = lvnBelow;

                // Delta display defaults
                DeltaPositiveColor = System.Windows.Media.Brushes.LimeGreen;
                DeltaNegativeColor = System.Windows.Media.Brushes.Red;

                // Bid/Ask display mode colors
                BidAskAskColor = System.Windows.Media.Brushes.LimeGreen;  // ask-lifted (buy aggressor)
                BidAskBidColor = System.Windows.Media.Brushes.Red;         // bid-hit   (sell aggressor)

                // Imbalance (diagonal cell highlights + horizontal number colors run together)
                ShowDiagonalImbalance      = true;
                ImbalanceMode              = STVPImbalanceMode.Diagonal;
                DiagonalImbalanceRatio     = 3.0;
                HorizontalImbalanceRatio   = 3.0;
                MinImbalanceVolume         = 15;
                ZoneHorizontalControlFloor = 50;
                ShowImbalanceZones         = true;
                MinLevelsForImbalanceZone  = 2;
                BuyingImbalanceColor  = System.Windows.Media.Brushes.Cyan;
                SellingImbalanceColor = System.Windows.Media.Brushes.Magenta;
                BuyZoneColor          = System.Windows.Media.Brushes.LimeGreen;
                SellZoneColor         = System.Windows.Media.Brushes.Red;
                SameLevelBuyDominanceColor  = System.Windows.Media.Brushes.DodgerBlue;   // ask > bid
                SameLevelSellDominanceColor = System.Windows.Media.Brushes.Magenta;      // bid > ask

                // Alert defaults

                // TPO Settings defaults
                TPOPeriodMinutes     = 30;
                SplitGlobexRthTPO    = false;  // OFF = cumulative full-session TPO (original)
                ShowTPOLetters       = true;
                ShowTPOPOC           = true;
                ShowTPOVAHVAL        = true;
                ShowTPOSD2           = true;
                ShowTPOSD3           = true;
                ShowNakedTPOPOC      = true;
                ShowNakedTPOVAHVAL   = true;
                ShowNakedTPOSD2      = true;
                ShowNakedTPOSD3      = true;
                ShowSinglePrints     = true;
                ShowNakedSinglePrints = true;
                ShowPoorHighsLows    = true;
                ShowExcess           = true;
                ShowDayType          = true;
                TPOFontSize          = 8;
                ShowCurrentTPOLevels = true;    // hide current session TPO lines when using prior levels only

                // TPO Colors defaults
                TPOLetterColor       = System.Windows.Media.Brushes.White;
                TPOPOCColor          = System.Windows.Media.Brushes.Orange;
                TPOVAHColor          = System.Windows.Media.Brushes.Red;
                TPOVALColor          = System.Windows.Media.Brushes.Red;
                NakedTPOPOCColor     = System.Windows.Media.Brushes.Magenta;
                NakedTPOVAColor      = System.Windows.Media.Brushes.White;
                TPOSDTwoColor        = System.Windows.Media.Brushes.LimeGreen;
                NakedTPOSDTwoColor   = System.Windows.Media.Brushes.Lime;
                TPOSDThreeColor      = System.Windows.Media.Brushes.DodgerBlue;
                NakedTPOSDThreeColor = System.Windows.Media.Brushes.CornflowerBlue;
                SinglePrintColor     = System.Windows.Media.Brushes.Yellow;
                PoorHighLowColor     = System.Windows.Media.Brushes.OrangeRed;
                ExcessColor          = System.Windows.Media.Brushes.LimeGreen;
                TPOVABoxColor        = System.Windows.Media.Brushes.RoyalBlue;

                // Cluster Zone defaults

                // Analytics Panel defaults
                ShowAnalyticsPanel         = true;
                AnalyticsPanelPosition     = STVPPanelPosition.TopRight;
                ShowDeltaZScore            = true;
                DeltaZScoreLookbackSessions = 20;
                DeltaZScoreThreshold       = 2.0;
                ShowVolumeSkewness         = true;
                ShowVAMigration            = true;
                VAMigrationLookback        = 3;
                ShowTPOKurtosis            = true;

                PrintLevelsAtSessionStart  = true;
                PrintLevelsOnDemand        = true;

                // Regime & Dominance defaults (non-lagging)
                EnableRegimeDetection      = true;
                DominanceWindowBars        = 8;
                ControlWindowBars          = -1;
                LogControlOnNewBar         = true;
                LogVolumetricToControl    = true;
                ControlLogContracts        = 10000;
                ControlLogUntilFire        = true;
                ControlLogStepContracts    = 0;
                ControlLogBarSummary       = false;
                DiscoveryThreshold         = 0.40;
                AuctionThreshold           = -0.30;
                ImbalanceIntensityFloor    = 0.55;
                UseLevel2Book              = true;
                L2DepthLevels              = 3;
                L2DominanceWeight          = 0.35;
                L2AbsorptionWeight         = 0.40;
                ShowRegimeBackground       = true;
                ShowRegimePanel            = true;
                ShowRegimeHistoryStrips    = true;

                // Trade signals // removed signal path   // ONLY filter: buy/sell must agree with who's in charge  // do not suppress by day type
                SignalEndHHMM          = 1400;
                ShowSignalLines        = true;   // draw entry / stop / ATR take-profit lines
                SignalAlerts           = true;
                StopLossTicks          = 20;
                TakeProfitTicks        = 40;
                EnableValueAreaRejection = true;
                VARequireCumDeltaConfirm = true;
                VACumDeltaMinContracts   = 0;
                EnableMeanRevertVAEdge   = true; // current-session developing TPO VAL/VAH rejection
                MRRequireTpoVaEdge       = true; // OFF = experiment: cum delta + L2 only
                MRVAEdgeProximityTicks   = 12;
                MRWallProximityTicks     = 4;
                MRRequireImbalanceOrL2   = true;
                MRRequireCumDeltaConfirm = true;
                MRCumDeltaMinContracts   = 0;
                MROneSignalPerEdge       = false; // allow edge revisits (NQ)
                VATestProximityTicks   = 8;
                VAPreferTpoLevels      = true;
                VAIncludeNakedLevels   = true;
                VAIncludeHighLow       = true;
                VANakedLookbackSessions = 5;  // ticks: NQ~8pts, YM~32pts (~$160 both); override per instrument
                GapTrendMinTicks       = 80;
                EnableTrendRunner      = true;
                RunnerMinR             = 2.0;
                RunnerMaxR             = 4.0;  // NQ ~20 pts gap forces trend bias

                // Auto trade defaults — OFF until you enable; use Sim account first
                EnableAutoTrade          = false;
                AutoTradeAccountName     = "Sim101";
                AutoTradeQuantity        = 1;
                AutoTradeEntryOrderType  = STVPEntryOrderType.Market;
                AutoTradeLimitCancelSeconds = 60;
                AutoTradeOnlyWhenFlat    = true;
                AutoTradeRthBlackoutSeconds = 0;
                AutoTradeStartMinutesAfterRth = 30; // e.g. 08:30 RTH → trade from 09:00
                AutoTradeForceFlatMinutesBeforeClose = 0;
                AutoTradeForceFlatHHMM     = 1500;  // 3:00 PM chart time — flatten + no new entries

                // L2 walls & in-trade monitor — NQ RTH calibrated (not ES).
                // Live depth observation: ~60 contracts is already "large" on NQ; book is thin vs ES.
                EnableWallMonitor          = true;
                WallMinSize                = 15;
                WallMedianMultiple         = 2.0;
                WallNearLevels             = 5;
                WallScanRangeTicks         = 200;
                WallPersistSeconds         = 1.0;
                WallTestingTicks           = 2;
                WallAbsorbRatio            = 1.0;
                WallPullFraction           = 0.60;
                ShowWallsOnChart           = true;
                WallMarkerWidth            = 72;
                BlockEntryOnOpposingSum    = true;
                EntryMaxOpposingSize       = 30;  // off — not used as a print filter
                StopLossTicks          = 40;
                TakeProfitTicks        = 40;  // never draw invalid/rejected markers
                ShowRegimeDetailLine       = false;
                ShowDepthHistoryStrip      = false;
                RegimePanelPosition        = STVPPanelPosition.TopRight;

                var discBg = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(45, 40, 120, 255));
                discBg.Freeze();
                DiscoveryBgColor = discBg;

                var auctBg = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(45, 170, 100, 230));
                auctBg.Freeze();
                AuctionBgColor = auctBg;

                var transBg = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(25, 150, 150, 150));
                transBg.Freeze();
                TransitionBgColor = transBg;

                BuyerDominanceColor  = System.Windows.Media.Brushes.LimeGreen;
                SellerDominanceColor = System.Windows.Media.Brushes.OrangeRed;
            }
            else if (State == State.Configure)
            {
                // Tick series at BarsInProgress == 1 — provides tick-accurate volume
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                double ts = (Instrument != null && Instrument.MasterInstrument != null)
                    ? Instrument.MasterInstrument.TickSize
                    : TickSize;
                if (ts <= 0) ts = TickSize;
                rowSize                  = ts * Math.Max(1, TicksPerRow);
                sessions                 = new List<SessionProfile>();
                lock (levelArchiveLock) { levelArchive.Clear(); }   // LevelAccessor
                lastBarTime              = DateTime.MinValue;
                atrIndicator             = ATR(14);
                rejected                 = new List<RejectedSetup>();
                rejectedKeys             = new HashSet<string>();
                currentTPOPeriod         = null;
                primaryBarBidVol         = new Dictionary<int, long>();
                primaryBarAskVol         = new Dictionary<int, long>();
                sessionCumBidAtBar       = new Dictionary<int, long>();
                sessionCumAskAtBar       = new Dictionary<int, long>();
                sessionRunningBid        = 0;
                sessionRunningAsk        = 0;
                sessionFinalDeltaHistory = new Queue<long>();
                regimeHistoryRebuilt     = false;
                signals                  = new List<TradeSignal>();
                signalKeys               = new HashSet<string>();
                vaFiredBarIndex          = -1;
                vaFiredThisBar           = new HashSet<string>();
                prevZones                = new List<ZoneSnap>();
                lastSignalEvalBar        = -1;
                vacPendingBar = -1; vacPendingDir = 0;
                lastClearStrongSide      = 0;
                icPendingSide = 0; icPendingBar = -1; icPendingStart = DateTime.MinValue; icPendingArmPrice = 0;
                icStructBlockBar = -1; icStructBlockSide = 0; icStructBlockLast = DateTime.MinValue;
                lastICFireBuy            = DateTime.MinValue;
                lastICFireSell           = DateTime.MinValue;
                diagEval = 0; diagNoRegime = 0; diagRaw = 0; diagCtrl = 0; diagDay = 0; diagRisk = 0; diagNoTP = 0; diagConf = 0;
                barAskAtPrice = new Dictionary<int, Dictionary<int, long>>();
                barBidAtPrice = new Dictionary<int, Dictionary<int, long>>();
                sigSessionKey            = DateTime.MinValue;
                eightyInit = false; eightyDone = false; eightySide = 0; eightyInsideSince = DateTime.MinValue;
                lastRegimeBar            = -1;
                regimeScoreAtBar         = new Dictionary<int, double>();
                dominanceAtBar           = new Dictionary<int, double>();
                regimeStateAtBar         = new Dictionary<int, STVPRegimeState>();
                bookImbalanceAtBar       = new Dictionary<int, double>();
                absorptionAtBar          = new Dictionary<int, double>();
                bookFromLiveL2           = new Dictionary<int, bool>();
                l2BidDepth               = new Dictionary<int, long>();
                l2AskDepth               = new Dictionary<int, long>();
                l2BidByPrice             = new Dictionary<double, L2Level>();
                l2AskByPrice             = new Dictionary<double, L2Level>();
                activeWalls              = new List<BookWall>();
                depthEverSeen            = false;
                lastDepthMarketTime      = DateTime.MinValue;
                lastWallScanTime         = DateTime.MinValue;
                lastWallScanMarketTime   = DateTime.MinValue;
                depthDiagPrinted         = false;
                lastDepthCleanup         = DateTime.MinValue;
                tradeMonState            = STVPTradeMonitorState.Flat;
                tradeMonPending          = STVPTradeMonitorState.Flat;
                tradeMonDir              = 0;
                tradeMonEntry            = 0;
                tradeMonQty              = 0;
                tradeMonExitFill         = 0;
                tradeMonExitFillName     = "";
                sessionTradePnL          = 0;
                sessionTradeCount        = 0;
                sessionTradeWins         = 0;
                sessionTradeLosses       = 0;
                tradeMonSetup            = "";
                tradeMonDetail           = "";
                tradeMonWallCleared      = false;

                // SessionIterator reads session boundaries directly from the chart's
                // trading hours template — no hardcoded ETH start time needed.
                sessionIterator = new SessionIterator(Bars);
                tradingDayFirstBegin.Clear();
                templateDayBegin = templateDayEnd = lastKeyBegin = lastKeyEnd = DateTime.MinValue;

                // Instantiate cumulative delta — must be by-reference so the .Update()
                // call in the tick handler keeps it in sync with the primary bars.
                cumulativeDeltaIndicator = OrderFlowCumulativeDelta(
                    CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Session, 0);

                Print("SessionTPOVolumeProfile loaded — period: " + ProfilePeriod + "  row size: " + rowSize.ToString("F4")
                    + "  (RTH " + RTHStartTime.ToString(@"hh\:mm")
                    + "–" + RTHEndTime.ToString(@"hh\:mm") + ")"
                    + "  RthMode: " + RthMode + (MergeTemplateSessions ? " (merge template sessions)" : "")
                    + "  template: " + Bars.TradingHours.Name);
            }
            else if (State == State.Realtime)
            {
                // Historical load is finished (works with the market closed too).
                if (!regimeHistoryRebuilt)
                    RebuildRegimeHistory();
                PrintSignalSummary();
                AttachAutoTradeAccount();
            }
            else if (State == State.Terminated)
            {
                UnhookAutoTradeButton();
                DetachAutoTradeAccount();
                DisposeResources();
            }
        }

        #endregion

        // =========================================================
        //  OnBarUpdate
        // =========================================================

        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            // BarsInProgress == 1  →  tick series  →  volume accumulation
            if (BarsInProgress == 1)
            {
                ProcessTick();
                return;
            }

            // BarsInProgress == 0  →  primary series  →  bar-index tracking + VPOC recalc
            if (BarsInProgress == 0)
                ProcessPrimaryBar();
        }

        /// <summary>
        /// Realtime Level-2 (market depth) handler.
        /// Updates best bid/ask size and a short multi-level book imbalance.
        /// Also feeds a simple absorption detector used by the regime engine.
        /// Note: market depth is available live and in Market Replay when the recorded
        /// data includes Level II. Historical chart load (no replay) does not fire this.
        /// </summary>
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            // Depth is required for regime L2 terms and/or the wall monitor.
            // No State filter: Market Replay runs as State.Realtime and must pass through.
            if (!UseLevel2Book && !EnableWallMonitor) return;
            if (e == null) return;

            // Only track the top N levels the user requested (position-index path — regime)
            if (e.Position >= 0 && e.Position < L2DepthLevels)
            {
                if (e.MarketDataType == MarketDataType.Bid)
                {
                    if (e.Operation == Operation.Remove || e.Volume <= 0)
                        l2BidDepth.Remove(e.Position);
                    else
                        l2BidDepth[e.Position] = e.Volume;

                    if (e.Position == 0)
                    {
                        l2BestBidSize  = e.Volume > 0 && e.Operation != Operation.Remove ? e.Volume : 0;
                        l2BestBidPrice = e.Price;
                    }
                }
                else if (e.MarketDataType == MarketDataType.Ask)
                {
                    if (e.Operation == Operation.Remove || e.Volume <= 0)
                        l2AskDepth.Remove(e.Position);
                    else
                        l2AskDepth[e.Position] = e.Volume;

                    if (e.Position == 0)
                    {
                        l2BestAskSize  = e.Volume > 0 && e.Operation != Operation.Remove ? e.Volume : 0;
                        l2BestAskPrice = e.Price;
                    }
                }

                // Recompute book imbalance across the configured depth
                long bidSum = 0, askSum = 0;
                for (int i = 0; i < L2DepthLevels; i++)
                {
                    long bs = 0, asz = 0;
                    l2BidDepth.TryGetValue(i, out bs);
                    l2AskDepth.TryGetValue(i, out asz);
                    double w = 1.0 / (1.0 + i);
                    bidSum += (long)(bs * w);
                    askSum += (long)(asz * w);
                }
                long total = bidSum + askSum;
                if (total > 0)
                    l2BookImbalance = (double)(bidSum - askSum) / total;
                else
                    l2BookImbalance = 0;

                // Simple absorption vs best (regime engine)
                if (l2LastAggressiveVol > 0 && l2LastAggressiveDir != 0)
                {
                    long resting = l2LastAggressiveDir > 0 ? l2BestAskSize : l2BestBidSize;
                    if (resting > 0 && l2LastAggressiveVol >= resting * 0.5)
                    {
                        double ratio = Math.Min(1.0, (double)l2LastAggressiveVol / Math.Max(1, resting));
                        l2AbsorptionScore = Math.Min(1.0, 0.6 * ratio + 0.4 * l2AbsorptionScore);
                    }
                    else
                        l2AbsorptionScore *= 0.85;
                }
            }

            DateTime depthT = GetMarketDepthTime(e);
            l2LastUpdateTime = depthT;
            if (depthT.Year > 2000)
                lastDepthMarketTime = depthT;
            depthEverSeen = true;

            // One-shot confirmation that L2 is flowing (live or Market Replay)
            if (!depthDiagPrinted && EnableWallMonitor)
            {
                depthDiagPrinted = true;
                Print("SessionTPOVolumeProfile L2 depth active — walls enabled"
                    + "  time=" + depthT.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + "  " + e.MarketDataType
                    + "  px=" + e.Price.ToString("F2")
                    + "  sz=" + e.Volume
                    + "  pos=" + e.Position
                    + "  state=" + State);
            }

            // Price-keyed book for wall monitor (needs a price on the event)
            // Works in live AND Market Replay (State.Realtime with recorded depth).
            if (EnableWallMonitor && e.Price > 0 && TickSize > 0)
                UpdatePriceKeyedBook(e);
        }

        /// <summary>
        /// Maintain bid/ask maps keyed by rounded price. Handles missing prices on Remove
        /// by opportunistic stale cleanup.
        /// </summary>
        private void UpdatePriceKeyedBook(MarketDepthEventArgs e)
        {
            double px = Instrument.MasterInstrument.RoundToTickSize(e.Price);
            bool isBid = e.MarketDataType == MarketDataType.Bid;
            var map = isBid ? l2BidByPrice : l2AskByPrice;
            // Market/replay time for age & persist — NOT wall clock (fast Market Replay breaks walls otherwise)
            DateTime mkt = GetMarketDepthTime(e);
            DateTime wall = DateTime.Now;

            if (e.Operation == Operation.Remove || e.Volume <= 0)
            {
                L2Level prev;
                if (map.TryGetValue(px, out prev))
                {
                    long drop = prev.Size;
                    if (drop > 0)
                        prev.CancelledEst += drop;
                    map.Remove(px);
                }
            }
            else
            {
                L2Level lvl;
                if (!map.TryGetValue(px, out lvl))
                {
                    lvl = new L2Level
                    {
                        Price = px,
                        Size = e.Volume,
                        FirstSeen = mkt,
                        LastSeen = mkt,
                        PeakSize = e.Volume
                    };
                    map[px] = lvl;
                }
                else
                {
                    if (e.Volume < lvl.Size)
                    {
                        long drop = lvl.Size - e.Volume;
                        long already = Math.Min(drop, Math.Max(0, lvl.TradedInto - (lvl.PeakSize - lvl.Size)));
                        long cancelPart = drop - already;
                        if (cancelPart > 0) lvl.CancelledEst += cancelPart;
                    }
                    lvl.Size = e.Volume;
                    lvl.LastSeen = mkt;
                    if (e.Volume > lvl.PeakSize) lvl.PeakSize = e.Volume;
                }
            }

            // Stale cleanup in MARKET time (8s of session time without update)
            // Critical for Market Replay: never use wall-clock here or levels vanish under fast playback.
            if (mkt.Year > 2000 && (lastDepthCleanup == DateTime.MinValue
                || (mkt - lastDepthCleanup).TotalSeconds >= 2.0
                || (mkt - lastDepthCleanup).TotalSeconds < 0)) // clock jump / seek in replay
            {
                lastDepthCleanup = mkt;
                CleanupStaleBook(l2BidByPrice, mkt, 8.0);
                CleanupStaleBook(l2AskByPrice, mkt, 8.0);
            }

            // Throttle: wall-clock (~4 Hz CPU) OR market-time advance (~100ms session time).
            // Market Replay at high speed advances session time faster than wall-clock —
            // market-time trigger keeps walls updating. Slow replay / live uses either gate.
            bool dueWall = lastWallScanTime == DateTime.MinValue
                || (wall - lastWallScanTime).TotalMilliseconds >= 250;
            bool dueMkt = mkt.Year > 2000 && (lastWallScanMarketTime == DateTime.MinValue
                || (mkt - lastWallScanMarketTime).TotalMilliseconds >= 100
                || (mkt - lastWallScanMarketTime).TotalSeconds < 0); // seek backward
            if (dueWall || dueMkt)
            {
                lastWallScanTime = wall;
                if (mkt.Year > 2000) lastWallScanMarketTime = mkt;
                ScanAndUpdateWalls();
                CheckAutoTradeForceFlat(); // force-flat only (in-trade monitor removed)
            }
        }

        private static void CleanupStaleBook(Dictionary<double, L2Level> map, DateTime now, double maxAgeSec)
        {
            if (map.Count == 0) return;
            var dead = new List<double>();
            foreach (var kv in map)
                if ((now - kv.Value.LastSeen).TotalSeconds > maxAgeSec)
                    dead.Add(kv.Key);
            for (int i = 0; i < dead.Count; i++)
                map.Remove(dead[i]);
        }

        private double WallRoundPrice(double p)
        {
            return Instrument.MasterInstrument.RoundToTickSize(p);
        }

        /// <summary>
        /// Median of the N nearest levels to the touch on one side (relative wall filter).
        /// </summary>
        private static double MedianNearSize(Dictionary<double, L2Level> map, double touch, bool above, int nLevels, double tick)
        {
            if (map.Count == 0 || nLevels <= 0) return 0;
            var sizes = new List<long>();
            // sort prices toward the scan direction
            var prices = new List<double>(map.Keys);
            prices.Sort();
            if (above)
            {
                for (int i = 0; i < prices.Count; i++)
                {
                    if (prices[i] < touch - tick * 0.1) continue;
                    sizes.Add(map[prices[i]].Size);
                    if (sizes.Count >= nLevels) break;
                }
            }
            else
            {
                for (int i = prices.Count - 1; i >= 0; i--)
                {
                    if (prices[i] > touch + tick * 0.1) continue;
                    sizes.Add(map[prices[i]].Size);
                    if (sizes.Count >= nLevels) break;
                }
            }
            if (sizes.Count == 0) return 0;
            sizes.Sort();
            return sizes[sizes.Count / 2];
        }

        private void ScanAndUpdateWalls()
        {
            if (TickSize <= 0) return;
            double bid = l2BestBidPrice > 0 ? l2BestBidPrice : (Close.Count > 0 ? Close[0] : 0);
            double ask = l2BestAskPrice > 0 ? l2BestAskPrice : bid;
            if (bid <= 0 && ask <= 0) return;
            if (bid <= 0) bid = ask;
            if (ask <= 0) ask = bid;

            double range = WallScanRangeTicks * TickSize;
            double medBid = MedianNearSize(l2BidByPrice, bid, false, WallNearLevels, TickSize);
            double medAsk = MedianNearSize(l2AskByPrice, ask, true, WallNearLevels, TickSize);
            long thrBid = Math.Max(WallMinSize, (long)Math.Ceiling(WallMedianMultiple * Math.Max(1.0, medBid)));
            long thrAsk = Math.Max(WallMinSize, (long)Math.Ceiling(WallMedianMultiple * Math.Max(1.0, medAsk)));

            DateTime now = GetMarketNow(); // market/replay time — Wall Persist works in Market Replay
            var found = new Dictionary<string, BookWall>(); // key side|price

            // Ask walls (resistance) above best bid / around ask
            foreach (var kv in l2AskByPrice)
            {
                double px = kv.Key;
                if (px < ask - TickSize) continue;
                if (px > ask + range) continue;
                var lvl = kv.Value;
                if (lvl.Size < thrAsk && lvl.PeakSize < thrAsk) continue;
                // Guard Market Replay clock skew: if FirstSeen is ahead of now, snap it
                if (lvl.FirstSeen > now) lvl.FirstSeen = now;
                string key = "-1|" + px.ToString("F2");
                BookWall existing = FindWall(-1, px);
                if (existing == null)
                {
                    if (lvl.Size < thrAsk) continue;
                    if ((now - lvl.FirstSeen).TotalSeconds < WallPersistSeconds) continue;
                    existing = new BookWall
                    {
                        Price = px,
                        Side = -1,
                        Size = lvl.Size,
                        PeakSize = lvl.PeakSize,
                        OriginalSize = lvl.Size,
                        FirstSeen = lvl.FirstSeen,
                        QualifiedAt = now,
                        LastSeen = now,
                        State = STVPWallState.Seen,
                        PrevState = STVPWallState.Seen,
                        TradedInto = lvl.TradedInto,
                        CancelledEst = lvl.CancelledEst
                    };
                }
                else
                {
                    existing.Size = lvl.Size;
                    existing.PeakSize = Math.Max(existing.PeakSize, lvl.PeakSize);
                    existing.LastSeen = now;
                    existing.TradedInto = Math.Max(existing.TradedInto, lvl.TradedInto);
                    existing.CancelledEst = Math.Max(existing.CancelledEst, lvl.CancelledEst);
                }
                found[key] = existing;
            }

            // Bid walls (support) below best ask / around bid
            foreach (var kv in l2BidByPrice)
            {
                double px = kv.Key;
                if (px > bid + TickSize) continue;
                if (px < bid - range) continue;
                var lvl = kv.Value;
                if (lvl.Size < thrBid && lvl.PeakSize < thrBid) continue;
                if (lvl.FirstSeen > now) lvl.FirstSeen = now;
                string key = "1|" + px.ToString("F2");
                BookWall existing = FindWall(1, px);
                if (existing == null)
                {
                    if (lvl.Size < thrBid) continue;
                    if ((now - lvl.FirstSeen).TotalSeconds < WallPersistSeconds) continue;
                    existing = new BookWall
                    {
                        Price = px,
                        Side = 1,
                        Size = lvl.Size,
                        PeakSize = lvl.PeakSize,
                        OriginalSize = lvl.Size,
                        FirstSeen = lvl.FirstSeen,
                        QualifiedAt = now,
                        LastSeen = now,
                        State = STVPWallState.Seen,
                        PrevState = STVPWallState.Seen,
                        TradedInto = lvl.TradedInto,
                        CancelledEst = lvl.CancelledEst
                    };
                }
                else
                {
                    existing.Size = lvl.Size;
                    existing.PeakSize = Math.Max(existing.PeakSize, lvl.PeakSize);
                    existing.LastSeen = now;
                    existing.TradedInto = Math.Max(existing.TradedInto, lvl.TradedInto);
                    existing.CancelledEst = Math.Max(existing.CancelledEst, lvl.CancelledEst);
                }
                found[key] = existing;
            }

            // Update states for found + mark missing as broken/pulled
            double last = Close.Count > 0 ? Close[0] : (bid + ask) * 0.5;
            var nextList = new List<BookWall>();
            foreach (var w in found.Values)
            {
                UpdateWallState(w, last, now);
                nextList.Add(w);
            }

            // Walls that disappeared from the book
            lock (wallLock)
            {
                foreach (var old in activeWalls)
                {
                    string key = old.Side + "|" + old.Price.ToString("F2");
                    if (found.ContainsKey(key)) continue;
                    if (old.State == STVPWallState.Broken || old.State == STVPWallState.Pulled)
                    {
                        // keep briefly for chart dim display
                        if ((now - old.LastSeen).TotalSeconds < 8.0)
                            nextList.Add(old);
                        continue;
                    }
                    // classify exit
                    bool through = (old.Side < 0 && last > old.Price + TickSize * 0.5)
                                || (old.Side > 0 && last < old.Price - TickSize * 0.5);
                    if (through || old.TradedInto >= old.OriginalSize * WallAbsorbRatio)
                        TransitionWall(old, STVPWallState.Broken, now);
                    else if (old.CancelledEst >= old.PeakSize * WallPullFraction)
                        TransitionWall(old, STVPWallState.Pulled, now);
                    else
                        TransitionWall(old, STVPWallState.Broken, now); // gone without clear pull stats
                    old.LastSeen = now;
                    nextList.Add(old);
                }
                activeWalls = nextList;
            }
        }

        private BookWall FindWall(int side, double price)
        {
            lock (wallLock)
            {
                for (int i = 0; i < activeWalls.Count; i++)
                {
                    var w = activeWalls[i];
                    if (w.Side == side && Math.Abs(w.Price - price) < TickSize * 0.25)
                        return w;
                }
            }
            return null;
        }

        private void TransitionWall(BookWall w, STVPWallState st, DateTime now)
        {
            if (w.State == st) return;
            w.PrevState = w.State;
            w.State = st;
            if (w.TaggedForIC || false)
            {
                if ((now - w.LastStateLog).TotalSeconds >= 0.5)
                {
                    w.LastStateLog = now;
                    Print("SessionTPOVolumeProfile WALL " + (w.Side > 0 ? "BID" : "ASK")
                        + " " + w.Price.ToString("F2")
                        + "  " + w.PrevState + " → " + w.State
                        + "  size=" + w.Size + " peak=" + w.PeakSize
                        + " traded=" + w.TradedInto + " cancel~" + w.CancelledEst
                        + (w.TaggedForIC ? "  [IC-tagged " + (w.ICDir > 0 ? "BUY" : "SELL") + "]" : ""));
                }
            }
        }

        private void UpdateWallState(BookWall w, double lastPrice, DateTime now)
        {
            double distTicks = Math.Abs(lastPrice - w.Price) / TickSize;
            bool testing = distTicks <= WallTestingTicks;

            // Broken: price through the wall
            bool through = (w.Side < 0 && lastPrice > w.Price + TickSize * 0.5)
                        || (w.Side > 0 && lastPrice < w.Price - TickSize * 0.5);
            if (through)
            {
                TransitionWall(w, STVPWallState.Broken, now);
                return;
            }

            // Pulled: mostly cancelled, little traded
            if (w.Size < Math.Max(WallMinSize * 0.25, 1)
                && w.CancelledEst >= w.PeakSize * WallPullFraction
                && w.TradedInto < w.OriginalSize * 0.35)
            {
                TransitionWall(w, STVPWallState.Pulled, now);
                return;
            }

            // Absorbing: aggressors have hit ≥ ratio of original while size holds or refills
            bool sizeHolds = w.Size >= w.OriginalSize * 0.40 || w.Size >= WallMinSize * 0.5;
            if (w.TradedInto >= w.OriginalSize * WallAbsorbRatio && sizeHolds)
            {
                TransitionWall(w, STVPWallState.Absorbing, now);
                return;
            }

            if (testing)
                TransitionWall(w, STVPWallState.Testing, now);
            else if (w.State != STVPWallState.Absorbing)
                TransitionWall(w, STVPWallState.Seen, now);
        }

        /// <summary>
        /// Attribute aggressive volume at a trade price to any matching wall / book level.
        /// Called from the tick path.
        /// </summary>
        private void AttributeTradeToWalls(double price, long vol, int aggressorDir)
        {
            if (!EnableWallMonitor || vol <= 0 || TickSize <= 0) return;
            double px = WallRoundPrice(price);
            // buy aggressor hits asks; sell aggressor hits bids
            if (aggressorDir > 0)
            {
                L2Level lvl;
                if (l2AskByPrice.TryGetValue(px, out lvl))
                    lvl.TradedInto += vol;
            }
            else if (aggressorDir < 0)
            {
                L2Level lvl;
                if (l2BidByPrice.TryGetValue(px, out lvl))
                    lvl.TradedInto += vol;
            }

            lock (wallLock)
            {
                for (int i = 0; i < activeWalls.Count; i++)
                {
                    var w = activeWalls[i];
                    if (Math.Abs(w.Price - px) > TickSize * 0.6) continue;
                    // only count aggression into the wall (buyers into ask wall, sellers into bid wall)
                    if (w.Side < 0 && aggressorDir > 0) w.TradedInto += vol;
                    if (w.Side > 0 && aggressorDir < 0) w.TradedInto += vol;
                }
            }
        }

        /// <summary>
        /// Opposing wall ahead of a directional trade (ask wall for long, bid wall for short).
        /// </summary>
        private BookWall FindOpposingWallAhead(int dir, double entry)
        {
            BookWall best = null;
            double bestDist = double.MaxValue;
            lock (wallLock)
            {
                for (int i = 0; i < activeWalls.Count; i++)
                {
                    var w = activeWalls[i];
                    if (w.State == STVPWallState.Broken || w.State == STVPWallState.Pulled) continue;
                    // long → ask resistance above; short → bid support below
                    if (dir > 0 && w.Side < 0 && w.Price >= entry - TickSize)
                    {
                        double d = w.Price - entry;
                        if (d < bestDist) { bestDist = d; best = w; }
                    }
                    else if (dir < 0 && w.Side > 0 && w.Price <= entry + TickSize)
                    {
                        double d = entry - w.Price;
                        if (d < bestDist) { bestDist = d; best = w; }
                    }
                }
            }
            return best;
        }

        private BookWall FindSupportingWall(int dir, double entry)
        {
            BookWall best = null;
            double bestDist = double.MaxValue;
            lock (wallLock)
            {
                for (int i = 0; i < activeWalls.Count; i++)
                {
                    var w = activeWalls[i];
                    if (w.State == STVPWallState.Broken || w.State == STVPWallState.Pulled) continue;
                    // long → bid support below; short → ask resistance above
                    if (dir > 0 && w.Side > 0 && w.Price <= entry + TickSize)
                    {
                        double d = entry - w.Price;
                        if (d < bestDist) { bestDist = d; best = w; }
                    }
                    else if (dir < 0 && w.Side < 0 && w.Price >= entry - TickSize)
                    {
                        double d = w.Price - entry;
                        if (d < bestDist) { bestDist = d; best = w; }
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Tag IC signal if an opposing wall sits ahead. Observe-only.
        /// Returns wall label suffix (" W" or "") and logs details.
        /// </summary>
        private string TagIcWallIfAny(int dir, double entry)
        {
            // IC wall tagging removed with IC trade signals
            return "";
            if (!EnableWallMonitor) return "";
            if (false) return "";
            if (State != State.Realtime)
            {
                return "";
            }
            if (!depthEverSeen || l2LastUpdateTime == DateTime.MinValue
                || (GetMarketNow() - l2LastUpdateTime).TotalSeconds > 5.0)
            {
                Print("SessionTPOVolumeProfile IC wall check: NO DEPTH DATA (enable market depth on this instrument)");
                return "";
            }

            var wall = FindOpposingWallAhead(dir, entry);
            if (wall == null) return "";

            wall.TaggedForIC = true;
            wall.ICDir = dir;
            double distTicks = Math.Abs(wall.Price - entry) / Math.Max(TickSize, 1e-9);
            Print("SessionTPOVolumeProfile IC " + (dir > 0 ? "BUY" : "SELL")
                + " wall ahead: " + (wall.Side > 0 ? "BID" : "ASK")
                + " " + wall.Price.ToString("F2")
                + "  size=" + wall.Size + " peak=" + wall.PeakSize
                + "  dist=" + distTicks.ToString("0.0") + " ticks"
                + "  state=" + wall.State
                + "  traded=" + wall.TradedInto
                + "  cancel~" + wall.CancelledEst);
            return " W";
        }

        // ------------------------------------------------------------------
        //  In-trade monitor (advisory only)
        // ------------------------------------------------------------------

        /// <summary>
        /// Phase A: true if a single large opposing level ahead of px blocks a new entry.
        /// Uses largest level size — not full-book sum (thin NQ ladder often sums to 100+ with no real wall).
        /// </summary>
        private bool IsEntryPathBlocked(int dir, double px, out string reason)
        {
            reason = "";
            if (!BlockEntryOnOpposingSum || !EnableWallMonitor) return false;
            if (State != State.Realtime) return false;
            if (!depthEverSeen || l2LastUpdateTime == DateTime.MinValue
                || (GetMarketNow() - l2LastUpdateTime).TotalSeconds > 5.0)
            {
                // No depth: do not block (can't see path)
                return false;
            }
            int n; long largest; double nearest;
            long sum = SumOpposingBookAhead(dir, px, out n, out largest, out nearest);
            // Block only when one price is a real wall-sized passive stack
            if (largest >= EntryMaxOpposingSize)
            {
                reason = "largest=" + largest + " at/near "
                    + (nearest > 0 ? nearest.ToString("F2") : "?")
                    + " (max level " + EntryMaxOpposingSize
                    + "; book sum=" + sum + " / " + n + " lvls — sum not used for entry block)";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Sum opposing resting size within lookahead ticks of the current price.
        /// Long → asks at/above price; short → bids at/below price.
        /// Also returns count of distinct levels with any size and the largest single level.
        /// </summary>
        private long SumOpposingBookAhead(int dir, double px, out int levelCount, out long largest, out double nearestPrice)
        {
            levelCount = 0;
            largest = 0;
            nearestPrice = 0;
            if (TickSize <= 0 || px <= 0) return 0;
            double reach = 40 * TickSize;
            long sum = 0;
            double bestDist = double.MaxValue;
            var map = dir > 0 ? l2AskByPrice : l2BidByPrice;
            foreach (var kv in map)
            {
                double p = kv.Key;
                long sz = kv.Value.Size;
                if (sz <= 0) continue;
                if (dir > 0)
                {
                    if (p < px - TickSize * 0.1) continue;
                    if (p > px + reach) continue;
                }
                else
                {
                    if (p > px + TickSize * 0.1) continue;
                    if (p < px - reach) continue;
                }
                sum += sz;
                levelCount++;
                if (sz > largest) largest = sz;
                double d = Math.Abs(p - px);
                if (d < bestDist) { bestDist = d; nearestPrice = p; }
            }
            return sum;
        }

        private void ArmInTradeMonitor(int dir, double entry, string setup)
        {
            // In-trade monitor removed.
            return;
        }


        private void ClearInTradeMonitor(string reason)
        {
            // In-trade monitor removed.
            return;
        }


        /// <summary>
        /// Resolve open position direction from auto-trade account, else keep signal-armed direction.
        /// </summary>
        private int ResolveLivePositionDir()
        {
            try
            {
                if (autoTradeAccount != null && Instrument != null)
                {
                    int q = 0;
                    foreach (Position p in autoTradeAccount.Positions)
                    {
                        if (p == null || p.Instrument == null) continue;
                        if (p.Instrument.FullName != Instrument.FullName) continue;
                        if (p.MarketPosition == MarketPosition.Long) q = p.Quantity;
                        else if (p.MarketPosition == MarketPosition.Short) q = -p.Quantity;
                        else q = 0;
                        break;
                    }
                    if (q > 0) return 1;
                    if (q < 0) return -1;
                    // flat on account — if we were managing a trade, clear
                    if (EnableAutoTrade && tradeMonDir != 0)
                        return 0;
                }
            }
            catch { }
            return tradeMonDir; // signal-only mode keeps last armed dir until manual clear / session
        }

        private void UpdateInTradeMonitor()
        {
            // In-trade monitor removed.
            return;
        }


        /// <summary>
        /// Phase B — drive STVP working stop/target (and flatten on EXIT) from monitor state.
        /// Only runs when false is on and auto-trade account is attached.
        /// </summary>
        private void ApplyManageFromMonitor(STVPTradeMonitorState publish, int dir, double entry,
            double nowPx, long oppSum, double oppNearest, string detail)
        {
            // In-trade monitor / manage-from-monitor removed.
            return;
        }


        private bool OrderIsLive(Order o)
        {
            if (o == null) return false;
            OrderState s = o.OrderState;
            return s == OrderState.Working || s == OrderState.Accepted || s == OrderState.Submitted
                || s == OrderState.ChangeSubmitted || s == OrderState.CancelSubmitted;
        }

        private void ApplyCautionProtectiveOrders(int dir, double entry, double nowPx, double oppNearest, string detail)
        {
            try
            {
                // Break-even stop (only tighten)
                double be = entry + dir * 0 * TickSize;
                be = Instrument.MasterInstrument.RoundToTickSize(be);

                double newStop = be;
                if (tradeMonStopPrice > 0)
                {
                    // long: stop can only rise; short: stop can only fall
                    if (dir > 0) newStop = Math.Max(tradeMonStopPrice, be);
                    else         newStop = Math.Min(tradeMonStopPrice, be);
                }

                // Pull target in front of opposing book, or halfway to old TP
                double newTgt = tradeMonTargetPrice;
                if (oppNearest > 0 && TickSize > 0)
                {
                    newTgt = Instrument.MasterInstrument.RoundToTickSize(
                        oppNearest - dir * 2 * TickSize);
                }
                else if (tradeMonTargetPrice > 0 && entry > 0)
                {
                    newTgt = Instrument.MasterInstrument.RoundToTickSize(entry + 0.5 * (tradeMonTargetPrice - entry));
                }

                // Target must remain on profit side of entry and of current price if possible
                if (dir > 0)
                {
                    if (newTgt <= entry) newTgt = Instrument.MasterInstrument.RoundToTickSize(entry + 2 * TickSize);
                    if (tradeMonTargetPrice > 0) newTgt = Math.Min(newTgt, tradeMonTargetPrice); // only pull in
                }
                else
                {
                    if (newTgt >= entry) newTgt = Instrument.MasterInstrument.RoundToTickSize(entry - 2 * TickSize);
                    if (tradeMonTargetPrice > 0) newTgt = Math.Max(newTgt, tradeMonTargetPrice);
                }

                // Stop must not cross through market incorrectly
                if (dir > 0 && nowPx > 0 && newStop >= nowPx)
                    newStop = Instrument.MasterInstrument.RoundToTickSize(Math.Min(entry, nowPx - TickSize));
                if (dir < 0 && nowPx > 0 && newStop <= nowPx)
                    newStop = Instrument.MasterInstrument.RoundToTickSize(Math.Max(entry, nowPx + TickSize));

                bool changed = false;
                lock (autoTradeLock)
                {
                    if (OrderIsLive(workingStopOrder) && newStop > 0
                        && Math.Abs(newStop - workingStopOrder.StopPrice) >= TickSize * 0.5)
                    {
                        workingStopOrder.StopPrice = newStop;
                        autoTradeAccount.Change(new[] { workingStopOrder });
                        tradeMonStopPrice = newStop;
                        changed = true;
                    }
                    else if (newStop > 0)
                        tradeMonStopPrice = newStop;

                    if (OrderIsLive(workingTargetOrder) && newTgt > 0
                        && Math.Abs(newTgt - workingTargetOrder.LimitPrice) >= TickSize * 0.5)
                    {
                        workingTargetOrder.LimitPrice = newTgt;
                        autoTradeAccount.Change(new[] { workingTargetOrder });
                        tradeMonTargetPrice = newTgt;
                        changed = true;
                    }
                    else if (newTgt > 0)
                        tradeMonTargetPrice = newTgt;
                }

                Print("SessionTPOVolumeProfile MANAGE CAUTION: BE stop=" + newStop.ToString("F2")
                    + "  pull TP=" + newTgt.ToString("F2")
                    + (changed ? " (orders changed)" : " (prices noted; orders not live?)")
                    + "  " + detail);
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile MANAGE CAUTION error: " + ex.Message);
            }
        }

        private void FlattenManagedPosition(int dir, string detail)
        {
            try
            {
                // Cancel protective OCO legs first so flatten is clean
                lock (autoTradeLock)
                {
                    var toCancel = new System.Collections.Generic.List<Order>();
                    if (OrderIsLive(workingStopOrder)) toCancel.Add(workingStopOrder);
                    if (OrderIsLive(workingTargetOrder)) toCancel.Add(workingTargetOrder);
                    if (toCancel.Count > 0)
                    {
                        try { autoTradeAccount.Cancel(toCancel.ToArray()); }
                        catch (Exception cex) { Print("SessionTPOVolumeProfile MANAGE EXIT cancel: " + cex.Message); }
                    }
                    workingStopOrder = null;
                    workingTargetOrder = null;
                }

                int qty = 0;
                try
                {
                    foreach (Position p in autoTradeAccount.Positions)
                    {
                        if (p == null || p.Instrument == null) continue;
                        if (p.Instrument.FullName != Instrument.FullName) continue;
                        if (p.MarketPosition == MarketPosition.Long) qty = p.Quantity;
                        else if (p.MarketPosition == MarketPosition.Short) qty = p.Quantity;
                        break;
                    }
                }
                catch { }

                if (qty <= 0)
                {
                    Print("SessionTPOVolumeProfile MANAGE EXIT: no open qty to flatten  " + detail);
                    ClearInTradeMonitor("exit-flat");
                    return;
                }

                OrderAction action = dir > 0 ? OrderAction.Sell : OrderAction.Buy;
                string name = "STVP EXIT " + DateTime.Now.Ticks.ToString().Substring(8);

                Action submitFlat = () =>
                {
                    try
                    {
                        Order flat = autoTradeAccount.CreateOrder(
                            Instrument,
                            action,
                            OrderType.Market,
                            OrderEntry.Manual,
                            TimeInForce.Day,
                            qty,
                            0, 0,
                            string.Empty,
                            name,
                            Core.Globals.MinDate,
                            null);
                        autoTradeAccount.Submit(new[] { flat });
                        Print("SessionTPOVolumeProfile MANAGE EXIT: market flatten " + (dir > 0 ? "SELL" : "BUY")
                            + " qty=" + qty + "  " + detail);
                    }
                    catch (Exception ex)
                    {
                        Print("SessionTPOVolumeProfile MANAGE EXIT error: " + ex.Message);
                    }
                };

                try
                {
                    if (ChartControl != null && ChartControl.Dispatcher != null
                        && !ChartControl.Dispatcher.CheckAccess())
                        ChartControl.Dispatcher.InvokeAsync(submitFlat);
                    else
                        submitFlat();
                }
                catch { submitFlat(); }

                ClearInTradeMonitor("exit-managed");
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile MANAGE EXIT unexpected: " + ex.Message);
            }
        }

        // ------ Tick processing ------

        private void ProcessTick()
        {
            if (CurrentBars[1] < 1) return;

            DateTime tickTime   = Times[1][0];
            lastTickTimeSeen    = tickTime;
            DateTime sessionKey = GetSessionKey(tickTime);
            templateDayBegin    = lastKeyBegin;
            templateDayEnd      = lastKeyEnd;

            // Detect session boundary — identical logic to VWAP "Session" mode
            if (currentSession == null || sessionKey != currentSession.SessionDate)
                StartNewSession(tickTime, sessionKey);

            if (currentSession == null) return;

            // Keep StartTime as the earliest tick in the session. On time-based primary
            // charts (e.g. 5‑minute) OnBarUpdate may open the session with the first
            // primary bar's timestamp (often the bar close), which is later than the
            // ticks that already printed in that bar. Naked-level break times from those
            // early ticks would then sort before StartTime and suppress extension lines.
            if (tickTime < currentSession.StartTime)
                currentSession.StartTime = tickTime;

            double tickPrice  = Closes[1][0];
            long   tickVolume = (long)Volumes[1][0];
            if (tickVolume <= 0 || tickPrice <= 0) return;

            // Integer tick-index conversion — avoids FP Floor gaps on 0.025-tick markets (HE/LE)
            int priceLevel = LevelOf(tickPrice);

            // --- Volume accumulation ---
            if (!currentSession.VolumeAtPrice.ContainsKey(priceLevel))
                currentSession.VolumeAtPrice[priceLevel] = 0;
            currentSession.VolumeAtPrice[priceLevel] += tickVolume;

            // --- Per-price bid/ask volume accumulation ---
            //
            // Primary (quote-aware Bid/Ask classification):
            //   Compare the trade price to the best Bid/Ask stored on this tick bar
            //   via Bars.GetBid / Bars.GetAsk (available on the 1-tick series both
            //   historically — when the data provider supplies bid/ask at trade time —
            //   and in real time). Same logic NT uses for BidAsk delta mode:
            //     price >= ask  → buy aggressor  (ask-lifted)
            //     price <= bid  → sell aggressor (bid-hit)
            //     otherwise     → trade between quotes; fall through to tick rule
            //
            // Fallback (no valid bid/ask on this tick): Lee-Ready tick rule.
            //   price > prev tick  → uptick  (buy aggressor)
            //   price < prev tick  → downtick (sell aggressor)
            //   price == prev tick → carry forward last known direction

            long upVol   = 0;  // buy aggressor volume
            long downVol = 0;  // sell aggressor volume

            double tickBid = BarsArray[1].GetBid(CurrentBars[1]);
            double tickAsk = BarsArray[1].GetAsk(CurrentBars[1]);
            bool   hasQuotes = tickBid > 0 && tickAsk > 0 && tickAsk >= tickBid;

            if (TradeClassification == STVPTradeClassification.Midpoint && hasQuotes)
            {
                // GemsTraderLadder SimpleTradeClassifier: close > midpoint → buyer, else seller
                double midpoint = (tickAsk + tickBid) / 2.0;
                if (tickAsk == tickBid)
                {
                    if (tickPrice > tickAsk) upVol = tickVolume;
                    else                     downVol = tickVolume;
                }
                else
                {
                    if (tickPrice > midpoint) upVol = tickVolume;
                    else                      downVol = tickVolume;
                }
            }
            else
            {
                // Quote rule (default): price ≥ ask → buy, price ≤ bid → sell, else tick rule
                if (hasQuotes)
                {
                    if (tickPrice >= tickAsk)
                        upVol = tickVolume;
                    else if (tickPrice <= tickBid)
                        downVol = tickVolume;
                    // else: traded between bid and ask — use tick-rule below
                }

                if (upVol == 0 && downVol == 0)
                {
                    // No quotes, or trade printed between bid/ask: Lee-Ready tick rule
                    if (CurrentBars[1] >= 1)
                    {
                        double prevClose = Closes[1][1];
                        if (tickPrice > prevClose)      lastTickDirectionSign =  1;
                        else if (tickPrice < prevClose) lastTickDirectionSign = -1;
                        // equal price: carry forward unchanged
                    }
                    int dir = lastTickDirectionSign == 0 ? 1 : lastTickDirectionSign;
                    if (dir > 0) upVol   = tickVolume;
                    else         downVol = tickVolume;
                }
            }

            if (upVol > 0)
            {
                if (!currentSession.BuyVolumeAtPrice.ContainsKey(priceLevel))
                    currentSession.BuyVolumeAtPrice[priceLevel] = 0;
                currentSession.BuyVolumeAtPrice[priceLevel] += upVol;

                // Sliding window buys
                long wb;
                windowBuyAtPrice.TryGetValue(priceLevel, out wb);
                windowBuyAtPrice[priceLevel] = wb + upVol;
                windowBuyTime[priceLevel] = tickTime;
            }
            if (downVol > 0)
            {
                if (!currentSession.SellVolumeAtPrice.ContainsKey(priceLevel))
                    currentSession.SellVolumeAtPrice[priceLevel] = 0;
                currentSession.SellVolumeAtPrice[priceLevel] += downVol;

                // Sliding window sells
                long ws;
                windowSellAtPrice.TryGetValue(priceLevel, out ws);
                windowSellAtPrice[priceLevel] = ws + downVol;
                windowSellTime[priceLevel] = tickTime;
            }

            // Periodically prune levels whose last trade fell outside the sliding window
            if ((tickTime - lastWindowCleanup).TotalSeconds >= 1.0)
            {
                ClearTradesOutsideSlidingWindow(tickTime);
                lastWindowCleanup = tickTime;
            }

            // --- Per primary-bar bid/ask totals (reset automatically on new bar index) ---
            // --- and session-cumulative snapshot at this primary bar ---
            int primaryBarIdx = GetPrimaryBarIndexForTick(tickTime);
            if (primaryBarIdx != fbBar)
            {
                fbBar  = primaryBarIdx;
                fbOpen = fbHigh = fbLow = tickPrice;
            }
            else
            {
                if (tickPrice > fbHigh) fbHigh = tickPrice;
                if (tickPrice < fbLow)  fbLow  = tickPrice;
            }
            fbLast = tickPrice;
            if (primaryBarIdx >= 0)
            {
                if (upVol   > 0) AddBarLevelVol(barAskAtPrice, primaryBarIdx, priceLevel, upVol);
                if (downVol > 0) AddBarLevelVol(barBidAtPrice, primaryBarIdx, priceLevel, downVol);
                if (upVol > 0)
                {
                    if (!primaryBarAskVol.ContainsKey(primaryBarIdx))
                        primaryBarAskVol[primaryBarIdx] = 0;
                    primaryBarAskVol[primaryBarIdx] += upVol;
                    sessionRunningAsk += upVol;
                }
                if (downVol > 0)
                {
                    if (!primaryBarBidVol.ContainsKey(primaryBarIdx))
                        primaryBarBidVol[primaryBarIdx] = 0;
                    primaryBarBidVol[primaryBarIdx] += downVol;
                    sessionRunningBid += downVol;
                }
                // Snapshot running session totals so the bottom table can show
                // "session Bid/Ask as it was during this bar"
                sessionCumBidAtBar[primaryBarIdx] = sessionRunningBid;
                sessionCumAskAtBar[primaryBarIdx] = sessionRunningAsk;

                // REALTIME in-charge Clear/Strong: evaluate on every tick (mid-bar),
                // not only when the primary bar closes.
                if (EnableRegimeDetection)
                    UpdateRegimeAndDominanceRealtime(primaryBarIdx);

                // Live entry paths: MR + prior/naked level rejection only
                if (State == State.Realtime)
                {
                    EvaluateMeanRevertVAEdge();
                    EvaluateValueAreaRejection();
                }
            }

            // Feed Level-2 absorption detector with this tick's aggressive volume
            if (UseLevel2Book)
            {
                if (upVol > 0)
                {
                    l2LastAggressiveVol = upVol;
                    l2LastAggressiveDir = 1;   // buy aggressor hitting the ask
                    AttributeTradeToWalls(tickPrice, upVol, 1);
                }
                else if (downVol > 0)
                {
                    l2LastAggressiveVol = downVol;
                    l2LastAggressiveDir = -1;  // sell aggressor hitting the bid
                    AttributeTradeToWalls(tickPrice, downVol, -1);
                }
            }

            // --- Session high / low ---
            if (tickPrice > currentSession.SessionHigh || currentSession.SessionHigh == double.MinValue)
            {
                currentSession.SessionHigh = tickPrice;
                currentSession.HighLevel   = priceLevel;
            }
            if (tickPrice < currentSession.SessionLow || currentSession.SessionLow == double.MaxValue)
            {
                currentSession.SessionLow = tickPrice;
                currentSession.LowLevel   = priceLevel;
            }

            if (currentSession.OpenPrice == 0)
                currentSession.OpenPrice = tickPrice;
            currentSession.ClosePrice = tickPrice;

            // --- RTH tracking ---
            TimeSpan timeOfDay = tickTime.TimeOfDay;
            bool isRTH = IsRthAt(tickTime);
            if (isRTH)
            {
                if (currentSession.RTHOpen == 0)
                    currentSession.RTHOpen = tickPrice;
                currentSession.RTHClose = tickPrice;

                if (tickPrice > currentSession.RTHHigh || currentSession.RTHHigh == double.MinValue)
                    currentSession.RTHHigh = tickPrice;
                if (tickPrice < currentSession.RTHLow || currentSession.RTHLow == double.MaxValue)
                    currentSession.RTHLow = tickPrice;
            }

            // --- Initial Balance accumulation ---
            // Window = RTHStartTime → RTHStartTime + IBDurationMinutes (both are indicator properties).
            // No hardcoded clock times; works in whatever timezone your chart/session is set to.
            if (!currentSession.IBComplete && isRTH)
            {
                DateTime  rthStartDt   = GetRthStartDt(tickTime);
                TimeSpan  rthStartTod  = rthStartDt.TimeOfDay;
                TimeSpan  ibEndTime    = rthStartTod.Add(TimeSpan.FromMinutes(IBDurationMinutes));
                // Full Session mode compares absolute times so a template session that
                // crosses midnight still closes the IB window correctly.
                bool ibWindowClosed = UseFullSessionRth()
                    ? tickTime >= rthStartDt.AddMinutes(IBDurationMinutes)
                    : timeOfDay >= ibEndTime;
                if (ibWindowClosed)
                {
                    // IB window has closed — capture close for ending-zone bias
                    currentSession.IBComplete = true;
                    double closePx = Instrument != null
                        ? Instrument.MasterInstrument.RoundToTickSize(tickPrice) : tickPrice;
                    currentSession.IBClosePrice = closePx;

                    string formOrder = "unknown";
                    if (currentSession.IBHighFormTime != DateTime.MinValue && currentSession.IBLowFormTime != DateTime.MinValue)
                    {
                        if (currentSession.IBHighFormTime < currentSession.IBLowFormTime)
                            formOrder = "High 1st / Low 2nd";
                        else if (currentSession.IBLowFormTime < currentSession.IBHighFormTime)
                            formOrder = "Low 1st / High 2nd";
                        else
                            formOrder = "same tick";
                    }
                    string zone = IBEndingZoneLabel(currentSession);
                    Print("IB closed " + tickTime.ToString("yyyy-MM-dd HH:mm:ss")
                        + " | RTHStart " + rthStartTod.ToString(@"hh\:mm") + " + " + IBDurationMinutes + "m = " + ibEndTime.ToString(@"hh\:mm")
                        + " | ticks used " + currentSession.IBFirstTick.ToString("HH:mm:ss") + " -> " + currentSession.IBLastTick.ToString("HH:mm:ss")
                        + " | IBH " + IBHighPx(currentSession).ToString("F2") + " IBL " + IBLowPx(currentSession).ToString("F2")
                        + " | form " + formOrder + " | zone " + zone
                        + " | bias " + IBBiasLabel(currentSession));
                }
                else
                {
                    if (currentSession.IBFirstTick == DateTime.MinValue) currentSession.IBFirstTick = tickTime;
                    currentSession.IBLastTick = tickTime;
                    if (currentSession.IBHigh == 0 || priceLevel > currentSession.IBHigh)
                        currentSession.IBHigh = priceLevel;
                    if (currentSession.IBLow  == 0 || priceLevel < currentSession.IBLow)
                        currentSession.IBLow  = priceLevel;

                    // Exact prices: the level index is a row bucket (TicksPerRow wide), so its
                    // edges can sit 1+ ticks away from the real IB extreme.
                    double ibTickPx = Instrument != null
                        ? Instrument.MasterInstrument.RoundToTickSize(tickPrice) : tickPrice;
                    if (currentSession.IBHighPrice == 0 || ibTickPx > currentSession.IBHighPrice)
                    {
                        currentSession.IBHighPrice = ibTickPx;
                        currentSession.IBHighFormTime = tickTime;
                    }
                    if (currentSession.IBLowPrice  == 0 || ibTickPx < currentSession.IBLowPrice)
                    {
                        currentSession.IBLowPrice  = ibTickPx;
                        currentSession.IBLowFormTime  = tickTime;
                    }
                    // Keep rolling close so IBClosePrice is ready the moment the window ends
                    currentSession.IBClosePrice = ibTickPx;
                }
            }

            // --- Sync cumulative delta indicator with this tick so primary-bar reads are current ---
            if (cumulativeDeltaIndicator != null)
                cumulativeDeltaIndicator.Update(cumulativeDeltaIndicator.BarsArray[1].Count - 1, 1);

            // --- Naked level testing against prior sessions ---
            if (ShowNakedVPOC || ShowPriorSessionVPOC || ShowNakedVAHVAL || ShowPriorSessionLevels
                || ShowNakedTPOPOC || ShowNakedTPOVAHVAL || ShowNakedTPOSD2 || ShowNakedTPOSD3 || ShowNakedSinglePrints)
                CheckNakedLevelTests(tickPrice, tickTime);

            // --- TPO letter construction ---
            if (ShowTPOLetters || ShowTPOPOC || ShowTPOVAHVAL || ShowTPOSD2 || ShowTPOSD3
                || ShowNakedTPOPOC || ShowNakedTPOVAHVAL || ShowNakedTPOSD2 || ShowNakedTPOSD3
                || ShowSinglePrints || ShowNakedSinglePrints || ShowPoorHighsLows || ShowExcess
                || SplitGlobexRthTPO)
            {
                // When split mode is on, start a new period if the RTH/Globex side changed
                // even if the time window has not elapsed (letter sequence restarts at RTH open).
                bool sideChanged = SplitGlobexRthTPO && currentTPOPeriod != null
                    && currentTPOPeriod.IsRTH != isRTH;

                if (currentTPOPeriod == null || tickTime >= currentTPOPeriod.EndTime || sideChanged)
                    StartNewTPOPeriod(tickTime, isRTH);

                if (currentTPOPeriod != null)
                {
                    currentTPOPeriod.PriceLevelsTouched.Add(priceLevel);

                    // Always maintain combined map for letter display / fallback
                    if (!currentSession.TPOsAtPrice.ContainsKey(priceLevel))
                        currentSession.TPOsAtPrice[priceLevel] = new HashSet<char>();
                    currentSession.TPOsAtPrice[priceLevel].Add(currentTPOPeriod.Letter);

                    // Split maps when enabled
                    if (SplitGlobexRthTPO)
                    {
                        var map = isRTH ? currentSession.RthTPOsAtPrice : currentSession.GlobexTPOsAtPrice;
                        if (!map.ContainsKey(priceLevel))
                            map[priceLevel] = new HashSet<char>();
                        map[priceLevel].Add(currentTPOPeriod.Letter);
                    }
                }
            }

            // --- Pierce-and-return alerts and trade arrows ---
            // Arrows draw on all bars (historical + realtime).
            // Alert() popup/sound is gated inside CheckAlerts() to realtime only.
            if (false)
                CheckAlerts();
        }

        /// <summary>
        /// Maps a tick to the primary bar it belongs to.
        /// Historically the tick series is processed BEFORE the primary bar that contains
        /// those ticks closes, so CurrentBars[0] still points at the previous bar. Without
        /// this correction every historical bar's bid/ask volume (and therefore its
        /// dominance and depth proxy) was filed one bar early and read as empty.
        /// In realtime CurrentBars[0] is already the forming bar.
        /// </summary>
        private int GetPrimaryBarIndexForTick(DateTime tickTime)
        {
            int idx = CurrentBars[0];
            if (State != State.Historical)
                return idx;
            if (idx < 0)
                return 0;
            if (tickTime >= Times[0][0])
                return idx + 1;
            return idx;
        }

        // ------ Primary-bar processing ------

        private void ProcessPrimaryBar()
        {
            if (CurrentBars[0] < 1) return;

            DateTime barTime    = Times[0][0];
            DateTime sessionKey = GetSessionKey(barTime);

            // Detect session boundary — mirrors ProcessTick so both paths stay in sync
            if (currentSession == null || sessionKey != currentSession.SessionDate)
                StartNewSession(barTime, sessionKey);

            if (currentSession != null)
            {
                // Track bar indices for rendering
                if (currentSession.StartBarIndex < 0)
                    currentSession.StartBarIndex = CurrentBars[0];
                currentSession.EndBarIndex = CurrentBars[0];

                bool isRTH = IsRthAt(barTime);
                if (isRTH)
                {
                    if (currentSession.RTHStartBarIndex < 0)
                        currentSession.RTHStartBarIndex = CurrentBars[0];
                    currentSession.RTHEndBarIndex = CurrentBars[0];
                }

                // Recalculate VPOC and volume value area on every bar
                CalculateVPOCandVA(currentSession);

                // Recalculate TPO POC and value areas
                CalculateTPOPOCandVA(currentSession);

                // Keep FinalDelta current — DeltaClose[0] is the session-cumulative
                // delta at the close of this primary bar (auto-resets each session).
                if (cumulativeDeltaIndicator != null)
                    currentSession.FinalDelta = (long)cumulativeDeltaIndicator.DeltaClose[0];

                // Non-lagging regime + dominance update (order-flow + structure only)
                if (EnableRegimeDetection)
                    UpdateRegimeAndDominance();
            }

            lastBarTime = barTime;

            // Last historical bar reached: fill in the whole regime / dominance / depth history
            if (EnableRegimeDetection && !regimeHistoryRebuilt
                && State == State.Historical && CurrentBars[0] >= BarsArray[0].Count - 1)
                RebuildRegimeHistory();

            // Trade signals: evaluate every newly CLOSED primary bar (history and live alike)
            EvaluateSignalsPending();
            EvaluateIntrabarFailedBreakouts();
            CheckLimitEntryExpiry();

            // On-demand print — flag was set by OnRender (UI thread), executed here
            // on the data thread where sessions list access is safe
            if (printRequested && sessions != null && sessions.Count >= 2)
            {
                printRequested = false;
                PrintPriorSession(sessions[sessions.Count - 2]);
                PrintNakedLevels();
            }
        }

        #endregion

        // =========================================================
        //  SESSION MANAGEMENT
        // =========================================================

        #region Session Management

        /// <summary>
        /// Returns the actual begin time of the trading session that contains <paramref name="t"/>,
        /// read directly from the chart's trading hours template via SessionIterator.
        ///
        /// This correctly handles combined Globex+RTH sessions — a Sunday 17:00 tick
        /// returns Monday's session begin time, not Sunday's date — without any
        /// hardcoded ETH start-time assumption.
        /// </summary>
        private DateTime GetSessionKey(DateTime t)
        {
            sessionIterator.GetNextSession(t, true);
            DateTime sessionBegin = sessionIterator.ActualSessionBegin;
            lastKeyBegin = sessionBegin;
            lastKeyEnd   = sessionIterator.ActualSessionEnd;

            if (ProfilePeriod == STVPProfilePeriod.Weekly)
            {
                // Weekly key = Monday of the exchange trading week, NOT calendar date of session begin.
                // Overnight products (ZS, ES, etc.): Sunday evening belongs to Monday's trading day, so the
                // new week starts at the Sunday open and ends with Friday's close — not on Monday RTH.
                // Day-session-only products (HE/LE): trading day == session calendar date; same Monday key.
                // Prefer ActualTradingDayExchange (set by GetNextSession); fall back to GetTradingDay.
                DateTime tradingDay = sessionIterator.ActualTradingDayExchange;
                if (tradingDay == DateTime.MinValue || tradingDay.Year < 1990)
                    tradingDay = sessionIterator.GetTradingDay(t);
                DateTime d = tradingDay.Date;
                int offset = ((int)d.DayOfWeek + 6) % 7; // Monday=0 ... Sunday=6
                return d.AddDays(-offset);
            }

            if (MergeTemplateSessions)
            {
                // Key on the exchange trading day (first segment's begin) so a template that splits
                // one trading day into several sessions still yields one profile.
                DateTime td = sessionIterator.ActualTradingDayExchange;
                if (td == DateTime.MinValue || td.Year < 1990)
                    td = sessionIterator.GetTradingDay(t);
                td = td.Date;
                DateTime firstBegin;
                if (!tradingDayFirstBegin.TryGetValue(td, out firstBegin))
                {
                    firstBegin = sessionBegin;
                    tradingDayFirstBegin[td] = firstBegin;
                }
                lastKeyBegin = firstBegin;
                return firstBegin;
            }

            return sessionBegin;
        }

        /// <summary>
        /// True when <paramref name="t"/> counts as RTH. Manual: inside RTH Start / End. Full Session: always
        /// (every tick of a template session is RTH — day-only markets such as HE / LE).
        /// </summary>
        private bool UseFullSessionRth()
        {
            if (RthMode == STVPRthMode.FullSession) return true;
            if (RthMode == STVPRthMode.Manual) return false;
            // Auto: the template session opens inside the RTH window => there is no overnight / pre-RTH part.
            if (templateDayBegin == DateTime.MinValue) return false;
            TimeSpan b = templateDayBegin.TimeOfDay;
            return b >= RTHStartTime && b < RTHEndTime;
        }

        private bool IsRthAt(DateTime t)
        {
            if (UseFullSessionRth()) return true;
            TimeSpan tod = t.TimeOfDay;
            return tod >= RTHStartTime && tod < RTHEndTime;
        }

        /// <summary>
        /// Chart-time instant RTH opens for the day containing <paramref name="now"/>. Manual: that date at
        /// RTH Start Time. Full Session: the open of the current trading-hours-template session.
        /// </summary>
        private DateTime GetRthStartDt(DateTime now)
        {
            if (UseFullSessionRth() && templateDayBegin != DateTime.MinValue && templateDayBegin <= now)
                return templateDayBegin;
            return now.Date + RTHStartTime;
        }

        private void StartNewSession(DateTime barTime, DateTime sessionDate)
        {
            // Capture the closing session before resetting currentSession
            SessionProfile priorSession = null;

            // Finalize the closing session
            if (currentSession != null)
            {
                // EndTime must be strictly before the new session's first event time
                // (barTime). On 5‑minute charts lastBarTime is the last primary bar of
                // the prior session and can equal or lag the boundary; using it alone
                // made break times in the new session compare as "still in prior".
                DateTime end = lastBarTime;
                if (end < currentSession.StartTime)
                    end = currentSession.StartTime;
                if (barTime > DateTime.MinValue && end >= barTime)
                    end = barTime.AddTicks(-1);
                if (end < currentSession.StartTime)
                    end = currentSession.StartTime;
                currentSession.EndTime = end;
                // FinalDelta was kept current each primary bar by ProcessPrimaryBar
                CalculateVPOCandVA(currentSession);
                CalculateTPOPOCandVA(currentSession);

                // Push final delta to rolling history for Z-Score computation
                sessionFinalDeltaHistory.Enqueue(currentSession.FinalDelta);
                while (sessionFinalDeltaHistory.Count > DeltaZScoreLookbackSessions)
                    sessionFinalDeltaHistory.Dequeue();

                priorSession = currentSession;
            }

            currentSession   = new SessionProfile
            {
                SessionDate = sessionDate,
                StartTime   = barTime
            };
            currentTPOPeriod = null;

            // Reset session-running bid/ask so cumulative starts from zero for the new session
            sessionRunningBid = 0;
            sessionRunningAsk = 0;

            // Reset sliding-window buy/sell so a new session starts clean
            windowBuyAtPrice.Clear();
            windowSellAtPrice.Clear();
            windowBuyTime.Clear();
            windowSellTime.Clear();
            lastWindowCleanup = DateTime.MinValue;

            sessions.Add(currentSession);

            // Trim to MaxSessionsToShow
            while (sessions.Count > MaxSessionsToShow)
                sessions.RemoveAt(0);

            ArchiveLevelSession(currentSession);   // LevelAccessor

            // Auto-print prior session levels at session start
            if (PrintLevelsAtSessionStart && State == State.Realtime && priorSession != null)
                PrintPriorSession(priorSession);
        }

        /// <summary>
        /// Drops price levels whose last trade is older than TradeSlidingWindowSeconds.
        /// Mirrors GemsOrderFlow.ClearTradesOutsideSlidingWindow behaviour (one entry per level;
        /// the whole level is removed when its most recent trade ages out).
        /// </summary>
        private void ClearTradesOutsideSlidingWindow(DateTime time)
        {
            int secs = TradeSlidingWindowSeconds > 0 ? TradeSlidingWindowSeconds : 60;
            if (windowBuyTime.Count > 0)
            {
                var expired = new List<int>();
                foreach (var kv in windowBuyTime)
                {
                    if ((time - kv.Value).TotalSeconds > secs)
                        expired.Add(kv.Key);
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    int lvl = expired[i];
                    windowBuyTime.Remove(lvl);
                    windowBuyAtPrice.Remove(lvl);
                }
            }
            if (windowSellTime.Count > 0)
            {
                var expired = new List<int>();
                foreach (var kv in windowSellTime)
                {
                    if ((time - kv.Value).TotalSeconds > secs)
                        expired.Add(kv.Key);
                }
                for (int i = 0; i < expired.Count; i++)
                {
                    int lvl = expired[i];
                    windowSellTime.Remove(lvl);
                    windowSellAtPrice.Remove(lvl);
                }
            }
        }

        // Ctrl+P sets this flag in OnRender (UI thread).
        // Actual printing happens in ProcessPrimaryBar (data thread) — same thread
        // as tick/session processing, so sessions list access is safe.
        private bool printKeyActive  = false;
        private bool printRequested  = false;

        /// <summary>
        /// Prints prior session levels (p prefix) to the Output window.
        /// Called automatically at session start and on demand via Ctrl+P.
        /// </summary>
        private void PrintPriorSession(SessionProfile s)
        {
            string LP(int lv)   { return (lv * rowSize).ToString("F2"); }
            string HP(int lv)   { return (lv * rowSize + rowSize).ToString("F2"); }
            string FP(double p) { return p.ToString("F2"); }

            string vpoc   = s.VPOCLevel > 0                ? LP(s.VPOCLevel)   : "N/A";
            string vvah   = s.VVAH      > 0                ? HP(s.VVAH)        : "N/A";
            string vval   = s.VVAL      > 0                ? LP(s.VVAL)        : "N/A";
            string sd2h   = s.SD2H      > 0                ? HP(s.SD2H)        : "N/A";
            string sd2l   = s.SD2L      > 0                ? LP(s.SD2L)        : "N/A";
            string sd3h   = s.SD3H      > 0                ? HP(s.SD3H)        : "N/A";
            string sd3l   = s.SD3L      > 0                ? LP(s.SD3L)        : "N/A";
            string tpopoc = s.POCLevel  > 0                ? LP(s.POCLevel)    : "N/A";
            string tpovah = s.TPOVAH    > 0                ? HP(s.TPOVAH)      : "N/A";
            string tpoval = s.TPOVAL    > 0                ? LP(s.TPOVAL)      : "N/A";
            string tpo2h  = s.TPOSD2H   > 0               ? HP(s.TPOSD2H)     : "N/A";
            string tpo2l  = s.TPOSD2L   > 0               ? LP(s.TPOSD2L)     : "N/A";
            string tpo3h  = s.TPOSD3H   > 0               ? HP(s.TPOSD3H)     : "N/A";
            string tpo3l  = s.TPOSD3L   > 0               ? LP(s.TPOSD3L)     : "N/A";
            string phi    = s.SessionHigh > double.MinValue ? FP(s.SessionHigh) : "N/A";
            string plo    = s.SessionLow  < double.MaxValue ? FP(s.SessionLow)  : "N/A";
            string rthO   = s.RTHOpen     > 0              ? FP(s.RTHOpen)      : "N/A";
            string rthH   = s.RTHHigh    > double.MinValue ? FP(s.RTHHigh)      : "N/A";
            string rthL   = s.RTHLow     < double.MaxValue ? FP(s.RTHLow)       : "N/A";

            var    sb  = new System.Text.StringBuilder();
            string sep = new string('═', 58);
            string dsh = new string('─', 58);

            sb.AppendLine();
            sb.AppendLine(sep);
            sb.AppendLine("  PRIOR SESSION  " + s.SessionDate.ToString("ddd MM/dd/yyyy"));
            sb.AppendLine(sep);
            sb.AppendLine("  Volume Levels");
            sb.AppendLine("    pVPOC    : " + vpoc);
            sb.AppendLine("    pVVAH    : " + vvah);
            sb.AppendLine("    pVVAL    : " + vval);
            sb.AppendLine("    pSD2H    : " + sd2h);
            sb.AppendLine("    pSD2L    : " + sd2l);
            sb.AppendLine("    pSD3H    : " + sd3h);
            sb.AppendLine("    pSD3L    : " + sd3l);
            sb.AppendLine("  TPO Levels");
            sb.AppendLine("    pTPOPOC  : " + tpopoc);
            sb.AppendLine("    pTPOVAH  : " + tpovah);
            sb.AppendLine("    pTPOVAL  : " + tpoval);
            sb.AppendLine("    pTPOSD2H : " + tpo2h);
            sb.AppendLine("    pTPOSD2L : " + tpo2l);
            sb.AppendLine("    pTPOSD3H : " + tpo3h);
            sb.AppendLine("    pTPOSD3L : " + tpo3l);
            sb.AppendLine("  Session Bounds");
            sb.AppendLine("    pHI : " + phi + "    pRTHHigh : " + rthH);
            sb.AppendLine("    pLO : " + plo + "    pRTHLow  : " + rthL);
            sb.AppendLine("    RTHOpen  : " + rthO + "    DayType : " + s.Classification.ToString());
            sb.AppendLine();
            sb.AppendLine(dsh);
            sb.AppendLine("  PASTE INTO AI CONTEXT BLOCK");
            sb.AppendLine(dsh);
            sb.AppendLine("  Date          : " + s.SessionDate.ToString("yyyy-MM-dd"));
            sb.AppendLine("  pVPOC         : " + vpoc);
            sb.AppendLine("  pVVAH         : " + vvah);
            sb.AppendLine("  pVVAL         : " + vval);
            sb.AppendLine("  pSD2H         : " + sd2h);
            sb.AppendLine("  pSD2L         : " + sd2l);
            sb.AppendLine("  pSD3H         : " + sd3h);
            sb.AppendLine("  pSD3L         : " + sd3l);
            sb.AppendLine("  pTPOPOC       : " + tpopoc);
            sb.AppendLine("  pTPOVAH       : " + tpovah);
            sb.AppendLine("  pTPOVAL       : " + tpoval);
            sb.AppendLine("  pTPOSD2H      : " + tpo2h);
            sb.AppendLine("  pTPOSD2L      : " + tpo2l);
            sb.AppendLine("  pHI           : " + phi);
            sb.AppendLine("  pLO           : " + plo);
            sb.AppendLine("  RTH Open      : (fill in at 08:30 CT)");
            sb.AppendLine("  Overnight Low : (fill in pre-market)");
            sb.AppendLine("  Macro Context : ");
            sb.AppendLine("  FOMC Status   : ");
            sb.AppendLine(sep);

            Print(sb.ToString());
        }

        /// <summary>
        /// Prints all active naked levels — levels from sessions OLDER than the
        /// prior session whose extension lines are still active on the chart
        /// (i.e. price has not yet reached them, tested flag = false).
        /// Labeled with "n" prefix and session date. Sorted price descending,
        /// split above and below current price.
        /// </summary>
        private void PrintNakedLevels()
        {
            if (sessions == null || sessions.Count < 2) return;

            // Sorted descending by price
            var levels = new SortedDictionary<double, string>(
                Comparer<double>.Create((a, b) => b.CompareTo(a)));

            // Walk every completed prior session — same set the chart walks in
            // CheckNakedLevelTests(). If the tested flag is false the extension
            // line is still active on the chart, so it belongs in this list.
            // Skip sessions[Count-1] which is the current developing session.
            for (int i = 0; i < sessions.Count - 1; i++)
            {
                SessionProfile s = sessions[i];
                if (s.VolumeAtPrice.Count == 0) continue;

                string date = s.SessionDate.ToString("MM/dd");

                System.Action<double, string> add = (price, label) =>
                {
                    string entry = "  n" + label + "  " + price.ToString("F2") + "  (" + date + ")";
                    if (!levels.ContainsKey(price))
                        levels[price] = entry;
                    else
                        levels[price] = levels[price] + " + n" + label.TrimStart();
                };

                // Volume naked levels
                if (!s.VPOCTested  && s.VPOCLevel > 0) add(s.VPOCLevel * rowSize,          "VPOC    ");
                if (!s.VVAHTested  && s.VVAH      > 0) add(s.VVAH * rowSize + rowSize,      "VVAH    ");
                if (!s.VVALTested  && s.VVAL      > 0) add(s.VVAL * rowSize,                "VVAL    ");
                if (!s.SD2HTested  && s.SD2H      > 0) add(s.SD2H * rowSize + rowSize,      "SD2H    ");
                if (!s.SD2LTested  && s.SD2L      > 0) add(s.SD2L * rowSize,                "SD2L    ");
                if (!s.SD3HTested  && s.SD3H      > 0) add(s.SD3H * rowSize + rowSize,      "SD3H    ");
                if (!s.SD3LTested  && s.SD3L      > 0) add(s.SD3L * rowSize,                "SD3L    ");

                // TPO naked levels
                if (!s.TPOPOCTested  && s.POCLevel  > 0) add(s.POCLevel * rowSize,          "TPOPOC  ");
                if (!s.TPOVAHTested  && s.TPOVAH    > 0) add(s.TPOVAH * rowSize + rowSize,  "TPOVAH  ");
                if (!s.TPOVALTested  && s.TPOVAL    > 0) add(s.TPOVAL * rowSize,            "TPOVAL  ");
                if (!s.TPOSD2HTested && s.TPOSD2H   > 0) add(s.TPOSD2H * rowSize + rowSize, "TPOSD2H ");
                if (!s.TPOSD2LTested && s.TPOSD2L   > 0) add(s.TPOSD2L * rowSize,           "TPOSD2L ");
                if (!s.TPOSD3HTested && s.TPOSD3H   > 0) add(s.TPOSD3H * rowSize + rowSize, "TPOSD3H ");
                if (!s.TPOSD3LTested && s.TPOSD3L   > 0) add(s.TPOSD3L * rowSize,           "TPOSD3L ");

                // Session high/low naked levels
                if (!s.HighTested && s.SessionHigh > double.MinValue) add(s.SessionHigh, "HI      ");
                if (!s.LowTested  && s.SessionLow  < double.MaxValue) add(s.SessionLow,  "LO      ");
            }

            // Use currentSession.ClosePrice — updated every tick, reliable in OnRender context
            // Close[0] is not safe to use from OnRender as it may reference a stale bar
            double cp  = currentSession != null && currentSession.ClosePrice > 0
                       ? currentSession.ClosePrice
                       : (sessions.Count > 0 ? sessions[sessions.Count - 1].ClosePrice : 0);
            var    sb  = new System.Text.StringBuilder();
            string dsh = new string('─', 58);

            sb.AppendLine();
            sb.AppendLine(dsh);
            sb.AppendLine("  NAKED LEVELS — all sessions, extension lines still active");
            sb.AppendLine("  Current Price: " + cp.ToString("F2"));
            sb.AppendLine(dsh);

            bool anyAbove = false;
            foreach (KeyValuePair<double, string> kvp in levels)
                if (kvp.Key > cp) { sb.AppendLine(kvp.Value); anyAbove = true; }
            if (!anyAbove) sb.AppendLine("  (none above)");

            sb.AppendLine("  ─── " + cp.ToString("F2") + " ───");

            bool anyBelow = false;
            foreach (KeyValuePair<double, string> kvp in levels)
                if (kvp.Key <= cp) { sb.AppendLine(kvp.Value); anyBelow = true; }
            if (!anyBelow) sb.AppendLine("  (none below)");

            sb.AppendLine(dsh);
            Print(sb.ToString());
        }

        private void StartNewTPOPeriod(DateTime tickTime, bool isRTH)
        {
            if (currentSession == null) return;

            // When split: letter index is local to Globex or RTH so RTH restarts at A.
            // When cumulative: sequential across the full session.
            int periodCount = SplitGlobexRthTPO
                ? currentSession.Periods.FindAll(p => p.IsRTH == isRTH).Count
                : currentSession.Periods.Count;
            int alphaIndex = periodCount % 52;
            char letter    = alphaIndex < 26
                ? (char)('A' + alphaIndex)
                : (char)('a' + (alphaIndex - 26));

            DateTime periodEnd = tickTime.AddMinutes(TPOPeriodMinutes);

            currentTPOPeriod = new TPOPeriod
            {
                Letter    = letter,
                StartTime = tickTime,
                EndTime   = periodEnd,
                IsRTH     = isRTH
            };
            currentSession.Periods.Add(currentTPOPeriod);
        }

        #endregion

        // =========================================================
        //  CALCULATIONS
        // =========================================================

        #region Calculations

        /// <summary>
        /// Recalculates VPOC, volume value area (VVAH/VVAL), and halfback for the session.
        /// Called on every primary bar close and on session finalization.
        /// </summary>
        private void CalculateVPOCandVA(SessionProfile session)
        {
            if (session.VolumeAtPrice.Count == 0) return;

            // --- VPOC: level with the highest volume ---
            long maxVolume = 0;
            foreach (var kvp in session.VolumeAtPrice)
            {
                if (kvp.Value > maxVolume)
                {
                    maxVolume         = kvp.Value;
                    session.VPOCLevel = kvp.Key;
                }
            }

            // --- Three-sigma volume value areas ---
            CalculateVolumeValueArea(session);

            // --- Halfback from VbP profile extremes ---
            if (session.VolumeAtPrice.Count > 0)
            {
                int hi = session.VolumeAtPrice.Keys.Max();
                int lo = session.VolumeAtPrice.Keys.Min();
                session.Halfback = ((hi + lo) / 2.0) * rowSize;
            }
        }

        /// <summary>
        /// Calculates all three volume value areas using the empirical 68–95–99.7 rule:
        ///   1σ — 68 %  → VVAH / VVAL  (replaces the legacy 70 % single-band)
        ///   2σ — 95 %  → SD2H / SD2L  (matches VWAP 2nd standard deviation band)
        ///   3σ — 99.7% → SD3H / SD3L  (matches VWAP 3rd standard deviation band)
        ///
        /// All three bands expand outward from the VPOC using the same algorithm:
        /// at each step the higher-volume neighbour is absorbed until the target
        /// percentage of total session volume is enclosed.
        /// </summary>
        private void CalculateVolumeValueArea(SessionProfile session)
        {
            if (session.VolumeAtPrice.Count == 0 || session.VPOCLevel == 0) return;

            long totalVolume = session.VolumeAtPrice.Values.Sum();

            // 1σ — 68.27% (one standard deviation)
            int vvah, vval;
            ComputeValueAreaBand(session, totalVolume, 0.68, out vvah, out vval);
            session.VVAH = vvah;
            session.VVAL = vval;

            // 2σ — 95.45% (two standard deviations)
            int sd2h, sd2l;
            ComputeValueAreaBand(session, totalVolume, 0.95, out sd2h, out sd2l);
            session.SD2H = sd2h;
            session.SD2L = sd2l;

            // 3σ — 99.73% (three standard deviations)
            int sd3h, sd3l;
            ComputeValueAreaBand(session, totalVolume, 0.997, out sd3h, out sd3l);
            session.SD3H = sd3h;
            session.SD3L = sd3l;
        }

        /// <summary>
        /// Expands a value area band outward from the VPOC until <paramref name="pct"/>
        /// of <paramref name="totalVolume"/> is enclosed.  Returns the resulting
        /// high and low level indices via out parameters.
        /// </summary>
        private void ComputeValueAreaBand(SessionProfile session, long totalVolume, double pct,
            out int bandHigh, out int bandLow)
        {
            long targetVolume  = (long)(totalVolume * pct);
            bandHigh           = session.VPOCLevel;
            bandLow            = session.VPOCLevel;
            long currentVolume = session.VolumeAtPrice.ContainsKey(session.VPOCLevel)
                ? session.VolumeAtPrice[session.VPOCLevel] : 0;

            while (currentVolume < targetVolume)
            {
                long volAbove = session.VolumeAtPrice.ContainsKey(bandHigh + 1)
                    ? session.VolumeAtPrice[bandHigh + 1] : 0;
                long volBelow = session.VolumeAtPrice.ContainsKey(bandLow - 1)
                    ? session.VolumeAtPrice[bandLow - 1] : 0;

                if (volAbove == 0 && volBelow == 0) break;

                if (volAbove >= volBelow)
                {
                    bandHigh++;
                    currentVolume += volAbove;
                }
                else
                {
                    bandLow--;
                    currentVolume += volBelow;
                }
            }
        }

        // ================================================================
        // TPO Calculations
        // ================================================================

        /// <summary>
        /// Computes POC + 1σ/2σ/3σ TPO value-area levels from a TPO letter map.
        /// Returns false if the map is empty.
        /// </summary>
        private bool ComputeTPOLevelsFromMap(Dictionary<int, HashSet<char>> map,
            out int poc, out int vah, out int val, out int sd2h, out int sd2l, out int sd3h, out int sd3l)
        {
            poc = vah = val = sd2h = sd2l = sd3h = sd3l = 0;
            if (map == null || map.Count == 0) return false;

            int maxCount = 0;
            foreach (var kvp in map)
            {
                if (kvp.Value.Count > maxCount)
                {
                    maxCount = kvp.Value.Count;
                    poc      = kvp.Key;
                }
            }

            int totalTPOs = 0;
            foreach (var kvp in map)
                totalTPOs += kvp.Value.Count;

            // 1σ VA — 68%
            vah = val = poc;
            int current = map.ContainsKey(poc) ? map[poc].Count : 0;
            int target  = (int)(totalTPOs * 0.68);
            while (current < target)
            {
                int above = map.ContainsKey(vah + 1) ? map[vah + 1].Count : 0;
                int below = map.ContainsKey(val - 1) ? map[val - 1].Count : 0;
                if (above == 0 && below == 0) break;
                if (above >= below) { vah++; current += above; }
                else                { val--; current += below; }
            }

            // 2σ VA — 95%
            sd2h = sd2l = poc;
            int curSD2 = map.ContainsKey(poc) ? map[poc].Count : 0;
            int tgtSD2 = (int)(totalTPOs * 0.95);
            while (curSD2 < tgtSD2)
            {
                int above = map.ContainsKey(sd2h + 1) ? map[sd2h + 1].Count : 0;
                int below = map.ContainsKey(sd2l - 1) ? map[sd2l - 1].Count : 0;
                if (above == 0 && below == 0) break;
                if (above >= below) { sd2h++; curSD2 += above; }
                else                { sd2l--; curSD2 += below; }
            }

            // 3σ VA — 99.7%
            sd3h = sd3l = poc;
            int curSD3 = map.ContainsKey(poc) ? map[poc].Count : 0;
            int tgtSD3 = (int)(totalTPOs * 0.997);
            while (curSD3 < tgtSD3)
            {
                int above = map.ContainsKey(sd3h + 1) ? map[sd3h + 1].Count : 0;
                int below = map.ContainsKey(sd3l - 1) ? map[sd3l - 1].Count : 0;
                if (above == 0 && below == 0) break;
                if (above >= below) { sd3h++; curSD3 += above; }
                else                { sd3l--; curSD3 += below; }
            }
            return true;
        }

        private void CalculateTPOPOCandVA(SessionProfile session)
        {
            if (SplitGlobexRthTPO)
            {
                // RTH levels → primary fields (naked extensions / prior levels use these only)
                int poc, vah, val, sd2h, sd2l, sd3h, sd3l;
                if (ComputeTPOLevelsFromMap(session.RthTPOsAtPrice,
                        out poc, out vah, out val, out sd2h, out sd2l, out sd3h, out sd3l))
                {
                    session.POCLevel = poc;
                    session.TPOVAH   = vah;
                    session.TPOVAL   = val;
                    session.TPOSD2H  = sd2h;
                    session.TPOSD2L  = sd2l;
                    session.TPOSD3H  = sd3h;
                    session.TPOSD3L  = sd3l;
                    session.VAM      = (vah + val) / 2;
                }

                // Globex levels → separate fields (display only; never used for naked extensions)
                if (ComputeTPOLevelsFromMap(session.GlobexTPOsAtPrice,
                        out poc, out vah, out val, out sd2h, out sd2l, out sd3h, out sd3l))
                {
                    session.GlobexPOCLevel  = poc;
                    session.GlobexTPOVAH    = vah;
                    session.GlobexTPOVAL    = val;
                    session.GlobexTPOSD2H   = sd2h;
                    session.GlobexTPOSD2L   = sd2l;
                    session.GlobexTPOSD3H   = sd3h;
                    session.GlobexTPOSD3L   = sd3l;
                }

                // Structure analysis on RTH profile only
                var savedMap = session.TPOsAtPrice;
                session.TPOsAtPrice = session.RthTPOsAtPrice;
                AnalyzePoorHighsLows(session);
                AnalyzeExcess(session);
                ClassifyDayType(session);
                ComputeSinglePrintZones(session);
                session.TPOsAtPrice = savedMap;
            }
            else
            {
                // Cumulative full-session TPO (original behaviour)
                int poc, vah, val, sd2h, sd2l, sd3h, sd3l;
                if (!ComputeTPOLevelsFromMap(session.TPOsAtPrice,
                        out poc, out vah, out val, out sd2h, out sd2l, out sd3h, out sd3l))
                    return;

                session.POCLevel = poc;
                session.TPOVAH   = vah;
                session.TPOVAL   = val;
                session.TPOSD2H  = sd2h;
                session.TPOSD2L  = sd2l;
                session.TPOSD3H  = sd3h;
                session.TPOSD3L  = sd3l;
                session.VAM      = (vah + val) / 2;

                session.GlobexPOCLevel = session.GlobexTPOVAH = session.GlobexTPOVAL = 0;
                session.GlobexTPOSD2H  = session.GlobexTPOSD2L = 0;
                session.GlobexTPOSD3H  = session.GlobexTPOSD3L = 0;

                AnalyzePoorHighsLows(session);
                AnalyzeExcess(session);
                ClassifyDayType(session);
                ComputeSinglePrintZones(session);
            }
        }

        private void AnalyzePoorHighsLows(SessionProfile session)
        {
            if (session.TPOsAtPrice.Count == 0) return;
            int highestLevel = session.TPOsAtPrice.Keys.Max();
            int lowestLevel  = session.TPOsAtPrice.Keys.Min();
            session.HasPoorHigh    = session.TPOsAtPrice.ContainsKey(highestLevel) && session.TPOsAtPrice[highestLevel].Count >= 2;
            session.PoorHighPrice  = session.HasPoorHigh ? highestLevel * rowSize : 0;
            session.HasPoorLow     = session.TPOsAtPrice.ContainsKey(lowestLevel)  && session.TPOsAtPrice[lowestLevel].Count  >= 2;
            session.PoorLowPrice   = session.HasPoorLow  ? lowestLevel  * rowSize : 0;
        }

        private void AnalyzeExcess(SessionProfile session)
        {
            if (session.TPOsAtPrice.Count == 0) return;
            int highestLevel = session.TPOsAtPrice.Keys.Max();
            int lowestLevel  = session.TPOsAtPrice.Keys.Min();
            session.HasExcessHigh  = session.TPOsAtPrice.ContainsKey(highestLevel)
                && session.TPOsAtPrice[highestLevel].Count == 1
                && session.TPOsAtPrice.ContainsKey(highestLevel - 1)
                && session.TPOsAtPrice[highestLevel - 1].Count == 1;
            session.ExcessHighPrice = session.HasExcessHigh ? highestLevel * rowSize : 0;
            session.HasExcessLow   = session.TPOsAtPrice.ContainsKey(lowestLevel)
                && session.TPOsAtPrice[lowestLevel].Count  == 1
                && session.TPOsAtPrice.ContainsKey(lowestLevel  + 1)
                && session.TPOsAtPrice[lowestLevel  + 1].Count  == 1;
            session.ExcessLowPrice  = session.HasExcessLow  ? lowestLevel  * rowSize : 0;
        }

        private void ClassifyDayType(SessionProfile session)
        {
            if (!session.IBComplete || session.IBHigh == 0 || session.IBLow == 0) { session.Classification = DayType.Unknown; return; }
            double ibRange      = IBHighPx(session) - IBLowPx(session);
            double sessionRange = session.SessionHigh - session.SessionLow;
            if (ibRange <= 0 || sessionRange <= 0) { session.Classification = DayType.Unknown; return; }

            double extAbove   = session.SessionHigh - IBHighPx(session);
            double extBelow   = IBLowPx(session) - session.SessionLow;
            double extRatio   = (Math.Max(0, extAbove) + Math.Max(0, extBelow)) / ibRange;
            bool   upperExt   = extAbove > ibRange * 0.5;
            bool   lowerExt   = extBelow > ibRange * 0.5;

            if (session.TPOsAtPrice.Count > 0)
            {
                int totalTPOs = 0;
                foreach (var kvp in session.TPOsAtPrice) totalTPOs += kvp.Value.Count;
                var sorted = session.TPOsAtPrice.Keys.OrderBy(k => k).ToList();
                var dists  = new List<System.Tuple<int, int, int>>();
                int? dStart = null; int dCount = 0;
                for (int li = 0; li < sorted.Count; li++)
                {
                    int lvl = sorted[li]; int tc = session.TPOsAtPrice[lvl].Count;
                    if (tc >= 3) { if (dStart == null) { dStart = lvl; dCount = 0; } dCount += tc; }
                    else { if (dStart != null) { dists.Add(System.Tuple.Create(dStart.Value, sorted[li - 1], dCount)); dStart = null; dCount = 0; } }
                }
                if (dStart != null) dists.Add(System.Tuple.Create(dStart.Value, sorted[sorted.Count - 1], dCount));
                if (dists.Count == 2)
                {
                    bool each20 = dists[0].Item3 >= totalTPOs * 0.20 && dists[1].Item3 >= totalTPOs * 0.20;
                    int gs = dists[0].Item2 + 1, ge = dists[1].Item1 - 1, gl = ge - gs + 1;
                    bool gapThin = true;
                    if (gl >= 3) { for (int gl2 = gs; gl2 <= ge; gl2++) { int gc = session.TPOsAtPrice.ContainsKey(gl2) ? session.TPOsAtPrice[gl2].Count : 0; if (gc > 1) { gapThin = false; break; } } }
                    else gapThin = false;
                    if (each20 && gapThin) { session.Classification = DayType.DoubleDistribution; return; }
                }
            }

            if      (extRatio < 0.25)                                      session.Classification = DayType.Neutral;
            else if (extRatio > 2.0)                                       session.Classification = DayType.Trend;
            else if (upperExt && !lowerExt)                                session.Classification = DayType.PShape;
            else if (lowerExt && !upperExt)                                session.Classification = DayType.BShape;
            else if (extRatio > 1.0)                                       session.Classification = DayType.NormalVariation;
            else                                                            session.Classification = DayType.Normal;
        }

        private void ComputeSinglePrintZones(SessionProfile session)
        {
            session.SinglePrintZones.Clear();
            if (session.TPOsAtPrice.Count == 0) return;

            var allLevels = session.TPOsAtPrice.Keys.OrderBy(k => k).ToList();
            if (allLevels.Count < 3) return; // need at least 3 rows for interior single prints to exist

            // ----------------------------------------------------------------
            // Find the HIGH TAIL — all contiguous single-letter rows from the
            // session high level downward. These are poor-high / tail levels,
            // NOT interior single prints. Stop the moment we hit a row with
            // more than one letter (the body of the profile starts there).
            // ----------------------------------------------------------------
            int highTailLow = allLevels[allLevels.Count - 1] + 1; // sentinel: nothing excluded by default
            for (int i = allLevels.Count - 1; i >= 0; i--)
            {
                if (session.TPOsAtPrice[allLevels[i]].Count == 1)
                    highTailLow = allLevels[i]; // extend tail downward
                else
                    break; // body starts here
            }

            // ----------------------------------------------------------------
            // Find the LOW TAIL — all contiguous single-letter rows from the
            // session low level upward.
            // ----------------------------------------------------------------
            int lowTailHigh = allLevels[0] - 1; // sentinel: nothing excluded by default
            for (int i = 0; i < allLevels.Count; i++)
            {
                if (session.TPOsAtPrice[allLevels[i]].Count == 1)
                    lowTailHigh = allLevels[i]; // extend tail upward
                else
                    break; // body starts here
            }

            // ----------------------------------------------------------------
            // Interior single prints only — rows with exactly one letter that
            // are NOT part of either tail.
            // ----------------------------------------------------------------
            var singles = session.TPOsAtPrice
                .Where(kvp => kvp.Value.Count == 1
                    && kvp.Key < highTailLow   // below the high tail
                    && kvp.Key > lowTailHigh)  // above the low tail
                .Select(kvp => kvp.Key)
                .OrderBy(k => k)
                .ToList();

            if (singles.Count == 0) return;

            // Cluster consecutive levels into contiguous zones
            int zStart = singles[0], zPrev = singles[0];
            for (int i = 1; i < singles.Count; i++)
            {
                if (singles[i] == zPrev + 1) { zPrev = singles[i]; }
                else
                {
                    session.SinglePrintZones.Add(new SinglePrintZone(zStart, zPrev));
                    zStart = singles[i];
                    zPrev  = singles[i];
                }
            }
            session.SinglePrintZones.Add(new SinglePrintZone(zStart, zPrev));
        }

        #endregion
        // =========================================================

        #region Naked Level Detection

        /// <summary>
        /// Checks whether the current tick price touches any prior-session naked level.
        /// Once touched the level is flagged as tested and the break time is recorded
        /// so the extension line terminates at that bar.
        /// </summary>
        private void CheckNakedLevelTests(double price, DateTime breakTime)
        {
            if (sessions.Count < 2) return;

            // Only iterate prior (completed) sessions
            for (int i = 0; i < sessions.Count - 1; i++)
            {
                var s = sessions[i];
                if (s.VolumeAtPrice.Count == 0) continue;

                // VPOC test
                if (!s.VPOCTested && s.VPOCLevel > 0)
                {
                    double lo = s.VPOCLevel * rowSize;
                    double hi = lo + rowSize;
                    if (price >= lo && price <= hi)
                        { s.VPOCTested = true; s.VPOCBreakTime = breakTime; }
                }

                // VVAH test
                if (!s.VVAHTested && s.VVAH > 0)
                {
                    double lo = s.VVAH * rowSize;
                    double hi = lo + rowSize;
                    if (price >= lo && price <= hi)
                        { s.VVAHTested = true; s.VVAHBreakTime = breakTime; }
                }

                // VVAL test
                if (!s.VVALTested && s.VVAL > 0)
                {
                    double lo = s.VVAL * rowSize;
                    double hi = lo + rowSize;
                    if (price >= lo && price <= hi)
                        { s.VVALTested = true; s.VVALBreakTime = breakTime; }
                }

                // SD2H test (2σ upper boundary)
                if (!s.SD2HTested && s.SD2H > 0)
                {
                    double lo = s.SD2H * rowSize;
                    double hi = lo + rowSize;
                    if (price >= lo && price <= hi)
                        { s.SD2HTested = true; s.SD2HBreakTime = breakTime; }
                }

                // SD2L test (2σ lower boundary)
                if (!s.SD2LTested && s.SD2L > 0)
                {
                    double lo = s.SD2L * rowSize;
                    double hi = lo + rowSize;
                    if (price >= lo && price <= hi)
                        { s.SD2LTested = true; s.SD2LBreakTime = breakTime; }
                }

                // SD3H test (3σ upper boundary)
                if (!s.SD3HTested && s.SD3H > 0)
                {
                    double lo = s.SD3H * rowSize;
                    double hi = lo + rowSize;
                    if (price >= lo && price <= hi)
                        { s.SD3HTested = true; s.SD3HBreakTime = breakTime; }
                }

                // SD3L test (3σ lower boundary)
                if (!s.SD3LTested && s.SD3L > 0)
                {
                    double lo = s.SD3L * rowSize;
                    double hi = lo + rowSize;
                    if (price >= lo && price <= hi)
                        { s.SD3LTested = true; s.SD3LBreakTime = breakTime; }
                }

                // Session high test
                if (!s.HighTested && s.HighLevel > 0)
                {
                    double lo = s.HighLevel * rowSize;
                    double hi = lo + rowSize;
                    if (price >= lo && price <= hi)
                        { s.HighTested = true; s.HighBreakTime = breakTime; }
                }

                // Session low test
                if (!s.LowTested && s.LowLevel > 0)
                {
                    double lo = s.LowLevel * rowSize;
                    double hi = lo + rowSize;
                    if (price >= lo && price <= hi)
                        { s.LowTested = true; s.LowBreakTime = breakTime; }
                }

                // === TPO naked level tests ===

                if (!s.TPOPOCTested && s.POCLevel > 0)
                {
                    double lo = s.POCLevel * rowSize;
                    if (price >= lo && price <= lo + rowSize)
                        { s.TPOPOCTested = true; s.TPOPOCBreakTime = breakTime; }
                }
                if (!s.TPOVAHTested && s.TPOVAH > 0)
                {
                    double lo = s.TPOVAH * rowSize;
                    if (price >= lo && price <= lo + rowSize)
                        { s.TPOVAHTested = true; s.TPOVAHBreakTime = breakTime; }
                }
                if (!s.TPOVALTested && s.TPOVAL > 0)
                {
                    double lo = s.TPOVAL * rowSize;
                    if (price >= lo && price <= lo + rowSize)
                        { s.TPOVALTested = true; s.TPOVALBreakTime = breakTime; }
                }
                if (!s.TPOSD2HTested && s.TPOSD2H > 0)
                {
                    double lo = s.TPOSD2H * rowSize;
                    if (price >= lo && price <= lo + rowSize)
                        { s.TPOSD2HTested = true; s.TPOSD2HBreakTime = breakTime; }
                }
                if (!s.TPOSD2LTested && s.TPOSD2L > 0)
                {
                    double lo = s.TPOSD2L * rowSize;
                    if (price >= lo && price <= lo + rowSize)
                        { s.TPOSD2LTested = true; s.TPOSD2LBreakTime = breakTime; }
                }
                if (!s.TPOSD3HTested && s.TPOSD3H > 0)
                {
                    double lo = s.TPOSD3H * rowSize;
                    if (price >= lo && price <= lo + rowSize)
                        { s.TPOSD3HTested = true; s.TPOSD3HBreakTime = breakTime; }
                }
                if (!s.TPOSD3LTested && s.TPOSD3L > 0)
                {
                    double lo = s.TPOSD3L * rowSize;
                    if (price >= lo && price <= lo + rowSize)
                        { s.TPOSD3LTested = true; s.TPOSD3LBreakTime = breakTime; }
                }

                // Single print zone testing
                int priceLevel = LevelOf(price);
                foreach (var zone in s.SinglePrintZones)
                {
                    if (zone.IsFullyTested) continue;
                    zone.UntestedLevels.Remove(priceLevel);
                    if (zone.IsFullyTested) zone.BreakTime = breakTime;
                }
            }
        }

        #endregion

        // =========================================================
        //  PIERCE-AND-RETURN ALERTS
        // =========================================================

        #region Alerts

        /// <summary>
        /// Checks pierce-and-return conditions on the current session's VA levels every tick.
        /// Condition for HIGH levels (SD2H, SD3H, VVAH):
        ///   Prior primary bar CLOSED ABOVE the level  AND  current bar LOW has crossed BACK BELOW it.
        /// Condition for LOW levels (SD2L, SD3L, VVAL):
        ///   Prior primary bar CLOSED BELOW the level  AND  current bar HIGH has crossed BACK ABOVE it.
        /// Each alert fires at most once per primary bar (flag resets on bar index change).
        /// </summary>
        private void CheckAlerts()
        {
            // Pierce/return VA alerts removed.
            return;
        }


        #endregion

        #region OnRender

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            // Ctrl+P detected — set flag only. Printing happens in ProcessPrimaryBar
            // on the data thread where sessions list access is safe.
            if (PrintLevelsOnDemand && sessions != null && sessions.Count >= 2)
            {
                bool ctrl = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl)
                         || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl);
                bool p    = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.P);

                if (ctrl && p && !printKeyActive)
                {
                    printKeyActive = true;
                    printRequested = true;
                }
                else if (!ctrl || !p)
                {
                    printKeyActive = false;
                }
            }

            if (sessions == null || sessions.Count == 0) return;
            if (ChartBars == null) return;

            // Pin behind price bars (runs once)
            if (!zOrderInitialized)
            {
                SetZOrder(ChartBars.ZOrder + 20);
                zOrderInitialized = true;
            }

            if (!resourcesCreated)
                CreateResources();
            if (!resourcesCreated) return;

            // Regime background + panel (drawn first so it sits under profiles)
            if (EnableRegimeDetection)
                RenderRegimeOverlay(chartControl, chartScale);

            // Identify most-recently-completed session for "p" vs "n" prefixing
            mostRecentCompletedSession = null;
            for (int i = sessions.Count - 1; i >= 0; i--)
            {
                if (sessions[i] != currentSession)
                {
                    mostRecentCompletedSession = sessions[i];
                    break;
                }
            }

            // Pass 1: per-session profile + IB + fill
            foreach (var session in sessions)
            {
                if (session.StartBarIndex < 0) continue;
                RenderSession(chartControl, chartScale, session);
            }

            // Pass 2: prior-session extension lines (drawn on top of profiles)
            foreach (var session in sessions)
            {
                if (session.StartBarIndex < 0) continue;
                RenderSessionExtensions(chartControl, chartScale, session);
            }

            // Pass 3: current-session level labels at the right margin
            RenderCurrentSessionLevelLabels(chartControl, chartScale);

            // Cluster zones removed

            // Pass 5: analytics panel — pre-trade context in top-left corner
            if (ShowAnalyticsPanel)
                RenderAnalyticsPanel(chartControl, chartScale);

            // Pass 6: per-bar Bid vs Ask totals at top/bottom of each primary bar
            if (ShowPerBarBidAskTotals)
                RenderPerBarBidAskTotals(chartControl, chartScale);

            // Pass 7: trade signals — arrows, entry / stop / take-profit lines (drawn on top)
            if (false)
                RenderSignals(chartControl, chartScale);

            // Pass 8: L2 walls (right-edge markers) + in-trade monitor panel
            if (EnableWallMonitor && ShowWallsOnChart && State == State.Realtime)
                RenderWallMarkers(chartControl, chartScale);
            // In-trade monitor panel removed

            // Lower-left auto-trade on/off button (always visible for quick toggle)
            EnsureAutoTradeButtonHook(chartControl);
            RenderAutoTradeButton(chartControl, chartScale);
        }

        private void RenderWallMarkers(ChartControl chartControl, ChartScale chartScale)
        {
            if (ChartPanel == null || RenderTarget == null || wallLabelFormat == null) return;
            BookWall[] snap;
            lock (wallLock) { snap = activeWalls.ToArray(); }
            if (snap.Length == 0) return;

            float right = ChartPanel.X + ChartPanel.W - 4f;
            float width = Math.Max(40f, WallMarkerWidth);
            float left = right - width;

            for (int i = 0; i < snap.Length; i++)
            {
                var w = snap[i];
                float y = chartScale.GetYByValue(w.Price);
                if (y < ChartPanel.Y - 4 || y > ChartPanel.Y + ChartPanel.H + 4) continue;

                float tickH = 4f;
                try
                {
                    float y2 = chartScale.GetYByValue(w.Price + TickSize);
                    tickH = Math.Max(3f, Math.Abs(y2 - y));
                }
                catch { }

                SharpDX.Direct2D1.Brush br = wallSeenBrush;
                float alphaScale = 1f;
                switch (w.State)
                {
                    case STVPWallState.Testing:
                        br = wallTestingBrush; break;
                    case STVPWallState.Absorbing:
                        br = wallAbsorbBrush; break;
                    case STVPWallState.Broken:
                    case STVPWallState.Pulled:
                        br = wallDimBrush; alphaScale = 0.45f; break;
                    default:
                        br = w.Side > 0 ? (wallBidBrush ?? wallSeenBrush) : (wallAskBrush ?? wallSeenBrush);
                        break;
                }
                if (br == null) continue;

                var rect = new SharpDX.RectangleF(left, y - tickH * 0.5f, width, tickH);
                RenderTarget.FillRectangle(rect, br);
                if (w.TaggedForIC && wallAbsorbBrush != null)
                    RenderTarget.DrawRectangle(rect, wallAbsorbBrush, 1.5f);

                string label = (w.Side > 0 ? "B " : "A ")
                    + w.Size.ToString("N0")
                    + " " + w.State.ToString().Substring(0, Math.Min(3, w.State.ToString().Length));
                if (w.TaggedForIC) label += " W";
                RenderTarget.DrawText(label, wallLabelFormat,
                    new SharpDX.RectangleF(left + 2f, y - tickH * 0.5f - 12f, width + 40f, 14f),
                    br);
            }
        }

        private void RenderInTradeMonitorPanel(ChartControl chartControl, ChartScale chartScale)
        {
            // In-trade monitor panel removed.
            return;
        }


        // ---- Per-bar Bid vs Ask totals (bottom table) ----

        /// <summary>
        /// Fixed 4-row table pinned to the bottom of the price panel.
        /// Rows 1–2: per-bar Buy / Sell (buy = ask aggressor, sell = bid aggressor).
        /// Rows 3–4: session-cumulative Buy / Sell as of that bar.
        /// One column per visible primary bar. Winning cell is filled with the
        /// same-level dominance color.
        /// </summary>
        private void RenderPerBarBidAskTotals(ChartControl chartControl, ChartScale chartScale)
        {
            if (primaryBarBidVol == null || primaryBarAskVol == null) return;
            if (ChartBars == null || ChartPanel == null || textBrush == null) return;

            int fromIdx = ChartBars.FromIndex;
            int toIdx   = ChartBars.ToIndex;
            if (toIdx < fromIdx) return;

            const float rowH    = 16f;
            const float labelW  = 40f;   // left sticky labels
            const int   numRows = 4;
            float tableH        = rowH * numRows;
            float tableBottom   = ChartPanel.Y + ChartPanel.H - 2f;
            float tableTop      = tableBottom - tableH;

            // Row Y positions (SuperDOM order): 0=Bar Buy, 1=Bar Sell, 2=Session Buy, 3=Session Sell
            float barBuyRowY  = tableTop;                 // ask aggressor (buy)
            float barSellRowY = tableTop + rowH;          // bid aggressor (sell)
            float sesBuyRowY  = tableTop + rowH * 2f;
            float sesSellRowY = tableTop + rowH * 3f;
            // Aliases kept for any residual references during edit
            float barAskRowY  = barBuyRowY;
            float barBidRowY  = barSellRowY;
            float sesAskRowY  = sesBuyRowY;
            float sesBidRowY  = sesSellRowY;

            // Background for the whole table strip
            if (analyticsPanelBgBrush != null)
                RenderTarget.FillRectangle(
                    new SharpDX.RectangleF(ChartPanel.X, tableTop, ChartPanel.W, tableH),
                    analyticsPanelBgBrush);

            // Horizontal dividers between rows
            for (int r = 1; r < numRows; r++)
            {
                float y = tableTop + rowH * r;
                // Slightly stronger divider between bar totals and session totals
                float thickness = (r == 2) ? 1.0f : 0.6f;
                RenderTarget.DrawLine(
                    new SharpDX.Vector2(ChartPanel.X, y),
                    new SharpDX.Vector2(ChartPanel.X + ChartPanel.W, y),
                    textBrush, thickness);
            }

            // Outer top/bottom borders
            RenderTarget.DrawLine(
                new SharpDX.Vector2(ChartPanel.X, tableTop),
                new SharpDX.Vector2(ChartPanel.X + ChartPanel.W, tableTop),
                textBrush, 0.8f);
            RenderTarget.DrawLine(
                new SharpDX.Vector2(ChartPanel.X, tableBottom),
                new SharpDX.Vector2(ChartPanel.X + ChartPanel.W, tableBottom),
                textBrush, 0.8f);

            // Left row labels
            if (statsFormat != null)
            {
                RenderTarget.DrawText("Buy", statsFormat,
                    new SharpDX.RectangleF(ChartPanel.X + 2f, barBuyRowY + 1f, labelW - 4f, rowH - 1f), textBrush);
                RenderTarget.DrawText("Sell", statsFormat,
                    new SharpDX.RectangleF(ChartPanel.X + 2f, barSellRowY + 1f, labelW - 4f, rowH - 1f), textBrush);
                RenderTarget.DrawText("sBuy", statsFormat,
                    new SharpDX.RectangleF(ChartPanel.X + 2f, sesBuyRowY + 1f, labelW - 4f, rowH - 1f), textBrush);
                RenderTarget.DrawText("sSell", statsFormat,
                    new SharpDX.RectangleF(ChartPanel.X + 2f, sesSellRowY + 1f, labelW - 4f, rowH - 1f), textBrush);
            }
            // Vertical line after label column
            float gridLeft = ChartPanel.X + labelW;
            RenderTarget.DrawLine(
                new SharpDX.Vector2(gridLeft, tableTop),
                new SharpDX.Vector2(gridLeft, tableBottom),
                textBrush, 0.6f);

            // Centered text formats for cell values (create once, dispose at end)
            SharpDX.DirectWrite.TextFormat cellFmt = null;
            try
            {
                cellFmt = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 9);
                cellFmt.TextAlignment = TextAlignment.Center;
                cellFmt.WordWrapping  = WordWrapping.NoWrap;
            }
            catch { cellFmt = statsFormat; }

            for (int barIdx = fromIdx; barIdx <= toIdx; barIdx++)
            {
                long bid = 0, ask = 0;
                primaryBarBidVol.TryGetValue(barIdx, out bid);
                primaryBarAskVol.TryGetValue(barIdx, out ask);

                long sBid = 0, sAsk = 0;
                if (sessionCumBidAtBar != null)
                    sessionCumBidAtBar.TryGetValue(barIdx, out sBid);
                if (sessionCumAskAtBar != null)
                    sessionCumAskAtBar.TryGetValue(barIdx, out sAsk);

                float xCenter = chartControl.GetXByBarIndex(ChartBars, barIdx);
                float barW = 30f;
                if (barIdx < toIdx)
                    barW = Math.Max(8f, chartControl.GetXByBarIndex(ChartBars, barIdx + 1) - xCenter);
                else if (barIdx > fromIdx)
                    barW = Math.Max(8f, xCenter - chartControl.GetXByBarIndex(ChartBars, barIdx - 1));

                float cellLeft  = xCenter - barW * 0.5f;
                float cellRight = cellLeft + barW;

                // Clip to panel
                if (cellRight < gridLeft || cellLeft > ChartPanel.X + ChartPanel.W)
                    continue;

                // Vertical grid line at right edge of this column
                RenderTarget.DrawLine(
                    new SharpDX.Vector2(cellRight, tableTop),
                    new SharpDX.Vector2(cellRight, tableBottom),
                    textBrush, 0.4f);

                // --- Per-bar Buy / Sell (Buy = ask aggressor, Sell = bid aggressor) ---
                bool buyWins  = ask > bid;
                bool sellWins = bid > ask;

                var buyRect = new SharpDX.RectangleF(cellLeft, barBuyRowY, barW, rowH);
                if (buyWins && sameLevelBuyDomBrush != null)
                    RenderTarget.FillRectangle(buyRect, sameLevelBuyDomBrush);
                if (cellFmt != null && textBrush != null)
                    RenderTarget.DrawText(ask.ToString(), cellFmt, buyRect, textBrush);

                var sellRect = new SharpDX.RectangleF(cellLeft, barSellRowY, barW, rowH);
                if (sellWins && sameLevelSellDomBrush != null)
                    RenderTarget.FillRectangle(sellRect, sameLevelSellDomBrush);
                if (cellFmt != null && textBrush != null)
                    RenderTarget.DrawText(bid.ToString(), cellFmt, sellRect, textBrush);

                // --- Session-cumulative Buy / Sell (as of this bar) ---
                bool sBuyWins  = sAsk > sBid;
                bool sSellWins = sBid > sAsk;

                var sBuyRect = new SharpDX.RectangleF(cellLeft, sesBuyRowY, barW, rowH);
                if (sBuyWins && sameLevelBuyDomBrush != null)
                    RenderTarget.FillRectangle(sBuyRect, sameLevelBuyDomBrush);
                if (cellFmt != null && textBrush != null)
                    RenderTarget.DrawText(sAsk.ToString(), cellFmt, sBuyRect, textBrush);

                var sSellRect = new SharpDX.RectangleF(cellLeft, sesSellRowY, barW, rowH);
                if (sSellWins && sameLevelSellDomBrush != null)
                    RenderTarget.FillRectangle(sSellRect, sameLevelSellDomBrush);
                if (cellFmt != null && textBrush != null)
                    RenderTarget.DrawText(sBid.ToString(), cellFmt, sSellRect, textBrush);
            }

            if (cellFmt != null && cellFmt != statsFormat)
                cellFmt.Dispose();
        }

        // ---- Per-session pass ----

        private void RenderSession(ChartControl chartControl, ChartScale chartScale, SessionProfile session)
        {
            int startX       = ChartControl.GetXByBarIndex(ChartBars, Math.Max(0, session.StartBarIndex));
            int endX         = ChartControl.GetXByBarIndex(ChartBars, Math.Min(ChartBars.Count - 1, session.EndBarIndex));
            if (endX <= startX) endX = startX + 100;
            int sessionWidth = endX - startX;

            // Current-session level lines (VPOC, VA, TPO, etc.) extend to the far right
            // edge of the chart so they remain visible beyond the developing bar.
            // Prior sessions still terminate at their own end bar.
            bool isCurrent   = (session == currentSession);
            int  levelEndX   = isCurrent && ChartPanel != null
                             ? (int)(ChartPanel.X + ChartPanel.W)
                             : endX;

            // (1) Value area fills — drawn back-to-front so 1σ appears on top
            // Each fill is independently controlled by ShowVA1Fill / ShowSD2Fill / ShowSD3Fill
            if (ShowSD3Fill && session.SD3H > 0 && session.SD3L > 0)
                RenderSDFill(chartScale, session.SD3H, session.SD3L, startX, sessionWidth, sd3VAFillBrush);
            if (ShowSD2Fill && session.SD2H > 0 && session.SD2L > 0)
                RenderSDFill(chartScale, session.SD2H, session.SD2L, startX, sessionWidth, sd2VAFillBrush);
            if (ShowVA1Fill && session.VVAH > 0 && session.VVAL > 0)
                RenderVolumeValueAreaFill(chartScale, session, startX, sessionWidth);

            // (1a) Level zones — all-or-nothing zones around every displayed VP / TPO level.
            //      Drawn behind lines, profile bars and letters so nothing is obscured.
            if (ShowLevelZones)
                RenderLevelZones(chartScale, session, startX, levelEndX);

            // (2) Initial Balance lines — draw as soon as either extreme exists (do not wait for IBComplete)
            if (ShowInitialBalance && (session.IBHighPrice > 0 || session.IBLowPrice > 0 || session.IBHigh > 0 || session.IBLow > 0))
                RenderInitialBalance(chartControl, chartScale, session, startX, sessionWidth);

            // (3) VbP histogram bars + VA box outline
            if (session.VolumeAtPrice.Count > 0)
                RenderVolumeProfile(chartControl, chartScale, session, startX, sessionWidth);

            // (4) VPOC line — for prior sessions always draw; for current session respect ShowCurrentVolumeLevels
            //     Current session: extend to chart far-right edge
            if (ShowVPOC && session.VPOCLevel > 0 && (session != currentSession || ShowCurrentVolumeLevels))
                RenderVPOCLine(chartScale, session, startX, levelEndX);

            // (5) Session stats mini-panel
            if (ShowSessionStats)
                RenderSessionStats(chartScale, session, startX);

            // (6) TPO rendering — letters, POC, VA lines, single prints, poor/excess
            //     Level lines use levelEndX (far right for current session)
            RenderTPOLayer(chartControl, chartScale, session, startX, levelEndX, sessionWidth);

            // (7) Within-session level labels at line ends — prior sessions only
            if (session != currentSession)
                RenderSessionLevelLabels(chartScale, session, endX);
        }

        // ---- Extension lines for prior sessions ----

        private void RenderSessionExtensions(ChartControl chartControl, ChartScale chartScale, SessionProfile session)
        {
            if (session == currentSession) return;
            if (session.StartBarIndex < 0) return;

            int endX = ChartControl.GetXByBarIndex(ChartBars, Math.Min(ChartBars.Count - 1, session.EndBarIndex));
            // Extend from the session end bar position
            string pfx = (session == mostRecentCompletedSession) ? "p" : "n";

            // Naked VPOC
            // Prior (most recent completed) session's VPOC has its own toggle; older sessions use Naked VPOC.
            bool vpocOn = (session == mostRecentCompletedSession) ? ShowPriorSessionVPOC : ShowNakedVPOC;
            if (vpocOn && session.VPOCLevel > 0)
            {
                double vpocPrice = session.VPOCLevel * rowSize + (rowSize * 0.5);
                RenderExtensionLine(chartScale, endX, vpocPrice,
                    session.VPOCBreakTime, nakedVpocBrush, pfx + "VPOC " + vpocPrice.ToString("F2"), ShowLevelZones);
            }

            // Naked VVAH / VVAL
            if (ShowNakedVAHVAL)
            {
                if (session.VVAH > 0)
                {
                    double vvahPrice = session.VVAH * rowSize + rowSize;
                    RenderExtensionLine(chartScale, endX, vvahPrice,
                        session.VVAHBreakTime, nakedVAHBrush, pfx + "VVAH " + vvahPrice.ToString("F2"), ShowLevelZones);
                }
                if (session.VVAL > 0)
                {
                    double vvalPrice = session.VVAL * rowSize;
                    RenderExtensionLine(chartScale, endX, vvalPrice,
                        session.VVALBreakTime, nakedVABrush, pfx + "VVAL " + vvalPrice.ToString("F2"), ShowLevelZones);
                }
            }

            // Naked 2σ boundaries
            if (ShowNakedSD2)
            {
                if (session.SD2H > 0)
                {
                    double p = session.SD2H * rowSize + rowSize;
                    RenderExtensionLine(chartScale, endX, p,
                        session.SD2HBreakTime, nakedSD2Brush, pfx + "SD2H " + p.ToString("F2"), ShowLevelZones);
                }
                if (session.SD2L > 0)
                {
                    double p = session.SD2L * rowSize;
                    RenderExtensionLine(chartScale, endX, p,
                        session.SD2LBreakTime, nakedSD2Brush, pfx + "SD2L " + p.ToString("F2"), ShowLevelZones);
                }
            }

            // Naked 3σ boundaries
            if (ShowNakedSD3)
            {
                if (session.SD3H > 0)
                {
                    double p = session.SD3H * rowSize + rowSize;
                    RenderExtensionLine(chartScale, endX, p,
                        session.SD3HBreakTime, nakedSD3Brush, pfx + "SD3H " + p.ToString("F2"), ShowLevelZones);
                }
                if (session.SD3L > 0)
                {
                    double p = session.SD3L * rowSize;
                    RenderExtensionLine(chartScale, endX, p,
                        session.SD3LBreakTime, nakedSD3Brush, pfx + "SD3L " + p.ToString("F2"), ShowLevelZones);
                }
            }

            // Prior session high / low
            if (ShowPriorSessionLevels)
            {
                if (session.HighLevel > 0)
                    RenderExtensionLine(chartScale, endX, session.SessionHigh,
                        session.HighBreakTime, priorHighLowBrush, pfx + "HI " + session.SessionHigh.ToString("F2"), ShowLevelZones);
                if (session.LowLevel > 0)
                    RenderExtensionLine(chartScale, endX, session.SessionLow,
                        session.LowBreakTime, priorHighLowBrush, pfx + "LO " + session.SessionLow.ToString("F2"), ShowLevelZones);
            }

            // === TPO naked extensions ===
            // Always RTH levels only (when split, primary TPO fields are RTH; Globex is never extended).
            string rPfx = SplitGlobexRthTPO ? "r" : "";
            if (ShowNakedTPOPOC && session.POCLevel > 0)
            {
                double p = session.POCLevel * rowSize + (rowSize * 0.5);
                RenderExtensionLine(chartScale, endX, p,
                    session.TPOPOCBreakTime, nakedTPOPocBrush, pfx + rPfx + "POC " + p.ToString("F2"), ShowLevelZones);
            }
            if (ShowNakedTPOVAHVAL)
            {
                if (session.TPOVAH > 0)
                    RenderExtensionLine(chartScale, endX, session.TPOVAH * rowSize + rowSize,
                        session.TPOVAHBreakTime, nakedTPOVABrush, pfx + rPfx + "TPOVAH " + (session.TPOVAH * rowSize + rowSize).ToString("F2"), ShowLevelZones);
                if (session.TPOVAL > 0)
                    RenderExtensionLine(chartScale, endX, session.TPOVAL * rowSize,
                        session.TPOVALBreakTime, nakedTPOVABrush, pfx + rPfx + "TPOVAL " + (session.TPOVAL * rowSize).ToString("F2"), ShowLevelZones);
            }
            if (ShowNakedTPOSD2)
            {
                if (session.TPOSD2H > 0)
                    RenderExtensionLine(chartScale, endX, session.TPOSD2H * rowSize + rowSize,
                        session.TPOSD2HBreakTime, nakedTPOSD2Brush, pfx + rPfx + "TPOSD2H " + (session.TPOSD2H * rowSize + rowSize).ToString("F2"), ShowLevelZones);
                if (session.TPOSD2L > 0)
                    RenderExtensionLine(chartScale, endX, session.TPOSD2L * rowSize,
                        session.TPOSD2LBreakTime, nakedTPOSD2Brush, pfx + rPfx + "TPOSD2L " + (session.TPOSD2L * rowSize).ToString("F2"), ShowLevelZones);
            }
            if (ShowNakedTPOSD3)
            {
                if (session.TPOSD3H > 0)
                    RenderExtensionLine(chartScale, endX, session.TPOSD3H * rowSize + rowSize,
                        session.TPOSD3HBreakTime, nakedTPOSD3Brush, pfx + rPfx + "TPOSD3H " + (session.TPOSD3H * rowSize + rowSize).ToString("F2"), ShowLevelZones);
                if (session.TPOSD3L > 0)
                    RenderExtensionLine(chartScale, endX, session.TPOSD3L * rowSize,
                        session.TPOSD3LBreakTime, nakedTPOSD3Brush, pfx + rPfx + "TPOSD3L " + (session.TPOSD3L * rowSize).ToString("F2"), ShowLevelZones);
            }
            if (ShowPoorHighsLows)
            {
                if (session.HasPoorHigh && session.PoorHighPrice > 0)
                    RenderExtensionLine(chartScale, endX, session.PoorHighPrice,
                        session.HighBreakTime, poorHighLowBrush, pfx + "PrHi " + session.PoorHighPrice.ToString("F2"));
                if (session.HasPoorLow && session.PoorLowPrice > 0)
                    RenderExtensionLine(chartScale, endX, session.PoorLowPrice,
                        session.LowBreakTime, poorHighLowBrush, pfx + "PrLo " + session.PoorLowPrice.ToString("F2"));
            }
            if (ShowNakedSinglePrints)
            {
                foreach (var zone in session.SinglePrintZones)
                    RenderExtensionZone(chartScale, zone, endX);
            }
        }

        // ---- Current-session level labels at right margin ----

        private void RenderCurrentSessionLevelLabels(ChartControl chartControl, ChartScale chartScale)
        {
            if (currentSession == null) return;

            // Anchor labels at the far right so they sit at the end of the
            // extended current-session level lines.
            int   lw     = 110;
            int   lh     = 14;
            float labelX = GetCanonicalLabelAnchorX();

            // Volume VPOC
            if (ShowCurrentVolumeLevels && ShowVPOC && currentSession.VPOCLevel > 0 && vpocBrush != null)
            {
                double p = currentSession.VPOCLevel * rowSize + (rowSize * 0.5);
                float  y = chartScale.GetYByValue(p);
                RenderTarget.DrawText("VPOC " + p.ToString("F2"), statsFormat,
                    new SharpDX.RectangleF(labelX, y - 7, lw, lh), vpocBrush);
            }
            // 1σ VA
            if (ShowCurrentVolumeLevels && ShowVolumeValueArea)
            {
                if (currentSession.VVAH > 0 && vvahLineBrush != null)
                { double p = currentSession.VVAH * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("VVAH " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), vvahLineBrush); }
                if (currentSession.VVAL > 0 && vvalLineBrush != null)
                { double p = currentSession.VVAL * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("VVAL " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), vvalLineBrush); }
            }
            // 2σ VA
            if (ShowCurrentVolumeLevels && ShowSD2VA)
            {
                if (currentSession.SD2H > 0 && sd2HLineBrush != null)
                { double p = currentSession.SD2H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("SD2H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), sd2HLineBrush); }
                if (currentSession.SD2L > 0 && sd2LLineBrush != null)
                { double p = currentSession.SD2L * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("SD2L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), sd2LLineBrush); }
            }
            // 3σ VA
            if (ShowCurrentVolumeLevels && ShowSD3VA)
            {
                if (currentSession.SD3H > 0 && sd3HLineBrush != null)
                { double p = currentSession.SD3H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("SD3H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), sd3HLineBrush); }
                if (currentSession.SD3L > 0 && sd3LLineBrush != null)
                { double p = currentSession.SD3L * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("SD3L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), sd3LLineBrush); }
            }
            // TPO levels — when split, primary fields are RTH (r prefix); Globex uses g prefix
            string tpoPfx = SplitGlobexRthTPO ? "r" : "";
            if (ShowCurrentTPOLevels && ShowTPOPOC && currentSession.POCLevel > 0 && tpoPocBrush != null)
            {
                double p = currentSession.POCLevel * rowSize + (rowSize * 0.5);
                float  y = chartScale.GetYByValue(p);
                RenderTarget.DrawText(tpoPfx + "POC " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoPocBrush);
            }
            if (ShowCurrentTPOLevels && ShowTPOVAHVAL)
            {
                if (currentSession.TPOVAH > 0 && tpoVAHBrush != null)
                { double p = currentSession.TPOVAH * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText(tpoPfx + "TPOVAH " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoVAHBrush); }
                if (currentSession.TPOVAL > 0 && tpoVALBrush != null)
                { double p = currentSession.TPOVAL * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText(tpoPfx + "TPOVAL " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoVALBrush); }
            }
            if (ShowCurrentTPOLevels && ShowTPOSD2)
            {
                if (currentSession.TPOSD2H > 0 && tpoSD2Brush != null)
                { double p = currentSession.TPOSD2H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText(tpoPfx + "TPOSD2H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoSD2Brush); }
                if (currentSession.TPOSD2L > 0 && tpoSD2Brush != null)
                { double p = currentSession.TPOSD2L * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText(tpoPfx + "TPOSD2L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoSD2Brush); }
            }
            if (ShowCurrentTPOLevels && ShowTPOSD3)
            {
                if (currentSession.TPOSD3H > 0 && tpoSD3Brush != null)
                { double p = currentSession.TPOSD3H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText(tpoPfx + "TPOSD3H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoSD3Brush); }
                if (currentSession.TPOSD3L > 0 && tpoSD3Brush != null)
                { double p = currentSession.TPOSD3L * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText(tpoPfx + "TPOSD3L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoSD3Brush); }
            }
            // Globex TPO labels (split mode only)
            if (SplitGlobexRthTPO && ShowCurrentTPOLevels)
            {
                if (ShowTPOPOC && currentSession.GlobexPOCLevel > 0 && tpoPocBrush != null)
                { double p = currentSession.GlobexPOCLevel * rowSize + (rowSize * 0.5); float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("gPOC " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoPocBrush); }
                if (ShowTPOVAHVAL)
                {
                    if (currentSession.GlobexTPOVAH > 0 && tpoVAHBrush != null)
                    { double p = currentSession.GlobexTPOVAH * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                      RenderTarget.DrawText("gTPOVAH " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoVAHBrush); }
                    if (currentSession.GlobexTPOVAL > 0 && tpoVALBrush != null)
                    { double p = currentSession.GlobexTPOVAL * rowSize; float y = chartScale.GetYByValue(p);
                      RenderTarget.DrawText("gTPOVAL " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoVALBrush); }
                }
                if (ShowTPOSD2 && tpoSD2Brush != null)
                {
                    if (currentSession.GlobexTPOSD2H > 0)
                    { double p = currentSession.GlobexTPOSD2H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                      RenderTarget.DrawText("gTPOSD2H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoSD2Brush); }
                    if (currentSession.GlobexTPOSD2L > 0)
                    { double p = currentSession.GlobexTPOSD2L * rowSize; float y = chartScale.GetYByValue(p);
                      RenderTarget.DrawText("gTPOSD2L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoSD2Brush); }
                }
                if (ShowTPOSD3 && tpoSD3Brush != null)
                {
                    if (currentSession.GlobexTPOSD3H > 0)
                    { double p = currentSession.GlobexTPOSD3H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                      RenderTarget.DrawText("gTPOSD3H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoSD3Brush); }
                    if (currentSession.GlobexTPOSD3L > 0)
                    { double p = currentSession.GlobexTPOSD3L * rowSize; float y = chartScale.GetYByValue(p);
                      RenderTarget.DrawText("gTPOSD3L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoSD3Brush); }
                }
            }
        }

        // ---- Prior-session level labels ----
        private void RenderSessionLevelLabels(ChartScale chartScale, SessionProfile session, int endX)
        {
            int   labelX = endX - 75;
            int   lw     = 72;
            int   lh     = 12;
            // Volume levels
            if (ShowVPOC && session.VPOCLevel > 0 && vpocBrush != null)
            { double p = session.VPOCLevel * rowSize + (rowSize * 0.5); float y = chartScale.GetYByValue(p);
              RenderTarget.DrawText("VPOC " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), vpocBrush); }
            if (ShowVolumeValueArea)
            {
                if (session.VVAH > 0 && vvahLineBrush != null)
                { double p = session.VVAH * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("VVAH " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), vvahLineBrush); }
                if (session.VVAL > 0 && vvalLineBrush != null)
                { double p = session.VVAL * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("VVAL " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), vvalLineBrush); }
            }
            if (ShowSD2VA)
            {
                if (session.SD2H > 0 && sd2HLineBrush != null)
                { double p = session.SD2H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("SD2H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), sd2HLineBrush); }
                if (session.SD2L > 0 && sd2LLineBrush != null)
                { double p = session.SD2L * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("SD2L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), sd2LLineBrush); }
            }
            if (ShowSD3VA)
            {
                if (session.SD3H > 0 && sd3HLineBrush != null)
                { double p = session.SD3H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("SD3H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), sd3HLineBrush); }
                if (session.SD3L > 0 && sd3LLineBrush != null)
                { double p = session.SD3L * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("SD3L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), sd3LLineBrush); }
            }
            // TPO levels
            if (ShowTPOPOC && session.POCLevel > 0 && tpoPocBrush != null)
            { double p = session.POCLevel * rowSize + (rowSize * 0.5); float y = chartScale.GetYByValue(p);
              RenderTarget.DrawText("POC " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), tpoPocBrush); }
            if (ShowTPOVAHVAL)
            {
                if (session.TPOVAH > 0 && tpoVAHBrush != null)
                { double p = session.TPOVAH * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("TPOVAH " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), tpoVAHBrush); }
                if (session.TPOVAL > 0 && tpoVALBrush != null)
                { double p = session.TPOVAL * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("TPOVAL " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), tpoVALBrush); }
            }
            if (ShowTPOSD2)
            {
                if (session.TPOSD2H > 0 && tpoSD2Brush != null)
                { double p = session.TPOSD2H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("TPOSD2H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), tpoSD2Brush); }
                if (session.TPOSD2L > 0 && tpoSD2Brush != null)
                { double p = session.TPOSD2L * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("TPOSD2L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), tpoSD2Brush); }
                if (session.TPOSD3H > 0 && tpoSD3Brush != null)
                { double p = session.TPOSD3H * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("TPOSD3H " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), tpoSD3Brush); }
                if (session.TPOSD3L > 0 && tpoSD3Brush != null)
                { double p = session.TPOSD3L * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("TPOSD3L " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 6, lw, lh), tpoSD3Brush); }
            }
        }

        // =========================================================
        //  Individual render helpers
        // =========================================================

        private void RenderVolumeValueAreaFill(ChartScale chartScale, SessionProfile session,
            int startX, int sessionWidth)
        {
            float topY    = chartScale.GetYByValue(session.VVAH * rowSize + rowSize);
            float bottomY = chartScale.GetYByValue(session.VVAL * rowSize);
            var   rect    = new SharpDX.RectangleF(startX, topY, sessionWidth, bottomY - topY);
            RenderTarget.FillRectangle(rect, valueAreaFillBrush);
        }

        /// <summary>Generic VA fill helper used for SD2 and SD3 bands.</summary>
        private void RenderSDFill(ChartScale chartScale, int bandHigh, int bandLow,
            int startX, int sessionWidth, SharpDX.Direct2D1.Brush fillBrush)
        {
            if (fillBrush == null) return;
            float topY    = chartScale.GetYByValue(bandHigh * rowSize + rowSize);
            float bottomY = chartScale.GetYByValue(bandLow  * rowSize);
            var   rect    = new SharpDX.RectangleF(startX, topY, sessionWidth, bottomY - topY);
            RenderTarget.FillRectangle(rect, fillBrush);
        }

        // =========================================================
        //  Level zones (all-or-nothing) — zone rectangle around every
        //  displayed Volume Profile and TPO profile level.
        // =========================================================

        /// <summary>
        /// Draws a filled zone rectangle spanning x1..x2, LevelZoneTicksUp ticks above and
        /// LevelZoneTicksDown ticks below levelPrice. One shared color; all-or-nothing toggle.
        /// </summary>
        private void DrawLevelZone(ChartScale chartScale, int x1, int x2, double levelPrice)
        {
            if (levelZoneFillBrush == null || x2 <= x1) return;
            double ts  = rowSize / Math.Max(1, TicksPerRow);   // one tick of price
            double top = levelPrice + Math.Max(0, LevelZoneTicksUp)   * ts;
            double bot = levelPrice - Math.Max(0, LevelZoneTicksDown) * ts;
            float yTop = chartScale.GetYByValue(Math.Max(top, bot));
            float yBot = chartScale.GetYByValue(Math.Min(top, bot));
            if (yBot <= yTop) yBot = yTop + 1f;
            var rect = new SharpDX.RectangleF(x1, yTop, x2 - x1, yBot - yTop);
            RenderTarget.FillRectangle(rect, levelZoneFillBrush);
            if (levelZoneBorderBrush != null)
                RenderTarget.DrawRectangle(rect, levelZoneBorderBrush, 1);
        }

        /// <summary>
        /// Zones for in-session VP + TPO levels. Mirrors the exact visibility rules used
        /// when drawing each level's line (ShowLevelZones gates everything — all or nothing).
        /// </summary>
        private void RenderLevelZones(ChartScale chartScale, SessionProfile session, int startX, int endX)
        {
            if (!ShowLevelZones || levelZoneFillBrush == null || endX <= startX) return;

            bool showVolume = (session != currentSession) || ShowCurrentVolumeLevels;
            bool showTPO    = (session != currentSession) || ShowCurrentTPOLevels;

            // ---- Volume Profile levels ----
            if (showVolume)
            {
                if (ShowVPOC && session.VPOCLevel > 0)
                    DrawLevelZone(chartScale, startX, endX, session.VPOCLevel * rowSize + rowSize * 0.5);
                if (ShowVolumeValueArea)
                {
                    if (session.VVAH > 0) DrawLevelZone(chartScale, startX, endX, session.VVAH * rowSize + rowSize);
                    if (session.VVAL > 0) DrawLevelZone(chartScale, startX, endX, session.VVAL * rowSize);
                }
                if (ShowSD2VA)
                {
                    if (session.SD2H > 0) DrawLevelZone(chartScale, startX, endX, session.SD2H * rowSize + rowSize);
                    if (session.SD2L > 0) DrawLevelZone(chartScale, startX, endX, session.SD2L * rowSize);
                }
                if (ShowSD3VA)
                {
                    if (session.SD3H > 0) DrawLevelZone(chartScale, startX, endX, session.SD3H * rowSize + rowSize);
                    if (session.SD3L > 0) DrawLevelZone(chartScale, startX, endX, session.SD3L * rowSize);
                }
            }

            // ---- TPO Profile levels (RTH primary; Globex when split lines are shown) ----
            if (showTPO)
            {
                if (ShowTPOPOC && session.POCLevel > 0)
                    DrawLevelZone(chartScale, startX, endX, session.POCLevel * rowSize + rowSize * 0.5);
                if (ShowTPOVAHVAL)
                {
                    if (session.TPOVAH > 0) DrawLevelZone(chartScale, startX, endX, session.TPOVAH * rowSize + rowSize);
                    if (session.TPOVAL > 0) DrawLevelZone(chartScale, startX, endX, session.TPOVAL * rowSize);
                }
                if (ShowTPOSD2)
                {
                    if (session.TPOSD2H > 0) DrawLevelZone(chartScale, startX, endX, session.TPOSD2H * rowSize + rowSize);
                    if (session.TPOSD2L > 0) DrawLevelZone(chartScale, startX, endX, session.TPOSD2L * rowSize);
                }
                if (ShowTPOSD3)
                {
                    if (session.TPOSD3H > 0) DrawLevelZone(chartScale, startX, endX, session.TPOSD3H * rowSize + rowSize);
                    if (session.TPOSD3L > 0) DrawLevelZone(chartScale, startX, endX, session.TPOSD3L * rowSize);
                }

                if (SplitGlobexRthTPO)
                {
                    if (ShowTPOPOC && session.GlobexPOCLevel > 0)
                        DrawLevelZone(chartScale, startX, endX, session.GlobexPOCLevel * rowSize + rowSize * 0.5);
                    if (ShowTPOVAHVAL)
                    {
                        if (session.GlobexTPOVAH > 0) DrawLevelZone(chartScale, startX, endX, session.GlobexTPOVAH * rowSize + rowSize);
                        if (session.GlobexTPOVAL > 0) DrawLevelZone(chartScale, startX, endX, session.GlobexTPOVAL * rowSize);
                    }
                    if (ShowTPOSD2)
                    {
                        if (session.GlobexTPOSD2H > 0) DrawLevelZone(chartScale, startX, endX, session.GlobexTPOSD2H * rowSize + rowSize);
                        if (session.GlobexTPOSD2L > 0) DrawLevelZone(chartScale, startX, endX, session.GlobexTPOSD2L * rowSize);
                    }
                    if (ShowTPOSD3)
                    {
                        if (session.GlobexTPOSD3H > 0) DrawLevelZone(chartScale, startX, endX, session.GlobexTPOSD3H * rowSize + rowSize);
                        if (session.GlobexTPOSD3L > 0) DrawLevelZone(chartScale, startX, endX, session.GlobexTPOSD3L * rowSize);
                    }
                }
            }
        }

        private void RenderInitialBalance(ChartControl chartControl, ChartScale chartScale,
            SessionProfile session, int startX, int sessionWidth)
        {
            bool hasHigh = session.IBHighPrice > 0 || session.IBHigh > 0;
            bool hasLow  = session.IBLowPrice  > 0 || session.IBLow  > 0;
            if (!hasHigh && !hasLow) return;

            // Anchor IB lines to the RTH bar range when available
            int ibStartX = ChartControl.GetXByBarIndex(ChartBars,
                Math.Max(0, session.RTHStartBarIndex >= 0 ? session.RTHStartBarIndex : session.StartBarIndex));
            int ibEndX = ChartControl.GetXByBarIndex(ChartBars,
                Math.Min(ChartBars.Count - 1, session.RTHEndBarIndex >= 0 ? session.RTHEndBarIndex : session.EndBarIndex));
            if (ibEndX <= ibStartX) { ibStartX = startX; ibEndX = startX + sessionWidth; }

            // Formation-order colors
            SharpDX.Direct2D1.Brush highBrush = ibBrush;
            SharpDX.Direct2D1.Brush lowBrush  = ibBrush;
            bool highFormKnown = session.IBHighFormTime != DateTime.MinValue;
            bool lowFormKnown  = session.IBLowFormTime  != DateTime.MinValue;

            if (hasHigh && hasLow && highFormKnown && lowFormKnown)
            {
                if (session.IBHighFormTime < session.IBLowFormTime)
                {
                    highBrush = ibFirstBrush     ?? ibBrush; // High 1st → orange
                    lowBrush  = ibSecondLowBrush ?? ibBrush; // Low 2nd  → red
                }
                else if (session.IBLowFormTime < session.IBHighFormTime)
                {
                    lowBrush  = ibFirstBrush      ?? ibBrush; // Low 1st  → orange
                    highBrush = ibSecondHighBrush ?? ibBrush; // High 2nd → green
                }
            }
            else if (hasHigh && !hasLow)
                highBrush = ibFirstBrush ?? ibBrush;
            else if (hasLow && !hasHigh)
                lowBrush = ibFirstBrush ?? ibBrush;

            double ibhPrice = hasHigh ? IBHighPx(session) : 0;
            double iblPrice = hasLow  ? IBLowPx(session)  : 0;

            // --- Quartile levels (25 / 50 / 75 %) inside the IB range ---
            if (ShowIBQuartiles && hasHigh && hasLow && ibhPrice > iblPrice && ibQuartileBrush != null)
            {
                double range = ibhPrice - iblPrice;
                double q25 = iblPrice + range * 0.25;
                double q50 = iblPrice + range * 0.50;
                double q75 = iblPrice + range * 0.75;
                float y25 = chartScale.GetYByValue(q25);
                float y50 = chartScale.GetYByValue(q50);
                float y75 = chartScale.GetYByValue(q75);
                if (dashStrokeStyle != null)
                {
                    RenderTarget.DrawLine(new SharpDX.Vector2(ibStartX, y25), new SharpDX.Vector2(ibEndX, y25), ibQuartileBrush, 1f, dashStrokeStyle);
                    RenderTarget.DrawLine(new SharpDX.Vector2(ibStartX, y50), new SharpDX.Vector2(ibEndX, y50), ibQuartileBrush, 1f, dashStrokeStyle);
                    RenderTarget.DrawLine(new SharpDX.Vector2(ibStartX, y75), new SharpDX.Vector2(ibEndX, y75), ibQuartileBrush, 1f, dashStrokeStyle);
                }
                else
                {
                    RenderTarget.DrawLine(new SharpDX.Vector2(ibStartX, y25), new SharpDX.Vector2(ibEndX, y25), ibQuartileBrush, 1f);
                    RenderTarget.DrawLine(new SharpDX.Vector2(ibStartX, y50), new SharpDX.Vector2(ibEndX, y50), ibQuartileBrush, 1f);
                    RenderTarget.DrawLine(new SharpDX.Vector2(ibStartX, y75), new SharpDX.Vector2(ibEndX, y75), ibQuartileBrush, 1f);
                }
                if (statsFormat != null)
                {
                    RenderTarget.DrawText("25%", statsFormat, new SharpDX.RectangleF(ibStartX + 2, y25 - 7, 30, 12), ibQuartileBrush);
                    RenderTarget.DrawText("50%", statsFormat, new SharpDX.RectangleF(ibStartX + 2, y50 - 7, 30, 12), ibQuartileBrush);
                    RenderTarget.DrawText("75%", statsFormat, new SharpDX.RectangleF(ibStartX + 2, y75 - 7, 30, 12), ibQuartileBrush);
                }
            }

            // --- IB High / Low lines + labels ---
            if (hasHigh && highBrush != null)
            {
                float ibHighY = chartScale.GetYByValue(ibhPrice);
                RenderTarget.DrawLine(new SharpDX.Vector2(ibStartX, ibHighY), new SharpDX.Vector2(ibEndX, ibHighY), highBrush, 2);
                if (statsFormat != null)
                {
                    string tag = "IBH " + ibhPrice.ToString("F2");
                    if (hasLow && highFormKnown && lowFormKnown)
                    {
                        if (session.IBHighFormTime < session.IBLowFormTime) tag += " 1st";
                        else if (session.IBLowFormTime < session.IBHighFormTime) tag += " 2nd";
                    }
                    RenderTarget.DrawText(tag, statsFormat,
                        new SharpDX.RectangleF(ibEndX + 3, ibHighY - 7, 130, 14), highBrush);
                }
            }

            if (hasLow && lowBrush != null)
            {
                float ibLowY = chartScale.GetYByValue(iblPrice);
                RenderTarget.DrawLine(new SharpDX.Vector2(ibStartX, ibLowY), new SharpDX.Vector2(ibEndX, ibLowY), lowBrush, 2);
                if (statsFormat != null)
                {
                    string tag = "IBL " + iblPrice.ToString("F2");
                    if (hasHigh && highFormKnown && lowFormKnown)
                    {
                        if (session.IBLowFormTime < session.IBHighFormTime) tag += " 1st";
                        else if (session.IBHighFormTime < session.IBLowFormTime) tag += " 2nd";
                    }
                    RenderTarget.DrawText(tag, statsFormat,
                        new SharpDX.RectangleF(ibEndX + 3, ibLowY - 7, 130, 14), lowBrush);
                }
            }

            // --- Bias label once IB is complete (formation order + ending zone) ---
            if (ShowIBBiasLabel && session.IBComplete && hasHigh && hasLow && statsFormat != null)
            {
                string bias = IBBiasLabel(session);
                string zone = IBEndingZoneLabel(session);
                SharpDX.Direct2D1.Brush biasBrush = ibBrush;
                if (bias.StartsWith("LONG", StringComparison.Ordinal))
                    biasBrush = ibSecondHighBrush ?? ibBrush;
                else if (bias.StartsWith("SHORT", StringComparison.Ordinal))
                    biasBrush = ibSecondLowBrush ?? ibBrush;
                else if (bias.StartsWith("1st", StringComparison.Ordinal))
                    biasBrush = ibFirstBrush ?? ibBrush;

                float midY = chartScale.GetYByValue((ibhPrice + iblPrice) * 0.5);
                string label = bias + (zone != "?" ? "  [" + zone + "]" : "");
                RenderTarget.DrawText(label, statsFormat,
                    new SharpDX.RectangleF(ibEndX + 3, midY - 7, 180, 14), biasBrush ?? textBrush);
            }
        }

        /// <summary>
        /// Ending-zone label relative to formation order.
        /// 0-25% = closed in the outer quartile against the side expected to break (strong confirmation).
        /// 75-100% = closed back near the first-formed side (bias weak / reversed).
        /// </summary>
        private string IBEndingZoneLabel(SessionProfile sp)
        {
            if (sp == null || sp.IBClosePrice <= 0) return "?";
            double hi = IBHighPx(sp), lo = IBLowPx(sp);
            if (hi <= lo) return "?";
            double range = hi - lo;
            bool highFirst = sp.IBHighFormTime != DateTime.MinValue && sp.IBLowFormTime != DateTime.MinValue
                             && sp.IBHighFormTime < sp.IBLowFormTime;
            bool lowFirst  = sp.IBHighFormTime != DateTime.MinValue && sp.IBLowFormTime != DateTime.MinValue
                             && sp.IBLowFormTime < sp.IBHighFormTime;
            double distFromExpected;
            if (lowFirst)
                distFromExpected = (hi - sp.IBClosePrice) / range; // expected break = high → 0 = at high
            else if (highFirst)
                distFromExpected = (sp.IBClosePrice - lo) / range; // expected break = low  → 0 = at low
            else
            {
                double pct = (sp.IBClosePrice - lo) / range;
                if (pct <= 0.25) return "0-25%";
                if (pct <= 0.50) return "25-50%";
                if (pct <= 0.75) return "50-75%";
                return "75-100%";
            }
            if (distFromExpected <= 0.25) return "0-25%";
            if (distFromExpected <= 0.50) return "25-50%";
            if (distFromExpected <= 0.75) return "50-75%";
            return "75-100%";
        }

        /// <summary>
        /// Combined formation-order + ending-zone bias label used on the chart and in the Output window.
        /// LONG / SHORT only when formation order is clear AND close is in the 0-25% rejection zone.
        /// </summary>
        private string IBBiasLabel(SessionProfile sp)
        {
            if (sp == null) return "—";
            bool highFirst = sp.IBHighFormTime != DateTime.MinValue && sp.IBLowFormTime != DateTime.MinValue
                             && sp.IBHighFormTime < sp.IBLowFormTime;
            bool lowFirst  = sp.IBHighFormTime != DateTime.MinValue && sp.IBLowFormTime != DateTime.MinValue
                             && sp.IBLowFormTime < sp.IBHighFormTime;
            if (!highFirst && !lowFirst) return "NO EDGE";

            string zone = IBEndingZoneLabel(sp);
            if (zone == "0-25%")
                return lowFirst ? "LONG bias" : "SHORT bias";
            if (zone == "25-50%")
                return lowFirst ? "LONG lean" : "SHORT lean";
            if (zone == "75-100%")
                return "FADE / weak";
            return lowFirst ? "1st=Low" : "1st=High";
        }

        private void RenderVolumeProfile(ChartControl chartControl, ChartScale chartScale,
            SessionProfile session, int startX, int sessionWidth)
        {
            if (session.VolumeAtPrice.Count == 0) return;

            long   maxVolume   = session.VolumeAtPrice.Values.Max();
            if (maxVolume == 0) return;

            long   totalVolume  = session.VolumeAtPrice.Values.Sum();
            double avgVolume    = (double)totalVolume / session.VolumeAtPrice.Count;
            double hvnThreshold = avgVolume * 1.3;
            double lvnThreshold = avgVolume * 0.7;

            bool  isCurrent          = (session == currentSession);
            // For the current session, respect ShowCurrentVolumeLevels; prior sessions always draw
            bool  showVolLevelLines  = !isCurrent || ShowCurrentVolumeLevels;
            int   vpWidth   = isCurrent ? CurrentSessionVbPWidth : PriorSessionVbPWidth;
            float rightEdge = isCurrent
                ? (ChartPanel.X + ChartPanel.W - CurrentSessionRightOffset)
                : (startX + sessionWidth);

            // Volume VA outlines — Box mode draws inset rectangles; Lines mode draws H/L horizontals
            if (VADisplayStyle == STVPDisplayStyle.Box)
            {
                // Box mode: 4 DrawLine calls per band (top, left, bottom, right) so each H and L
                // boundary carries its own color, width, and stroke style independently.
                // Side lines follow the H-level settings (they connect downward from the H boundary).
                if (ShowSD3VA && session.SD3H > 0 && session.SD3L > 0 && showVolLevelLines)
                {
                    float hY  = chartScale.GetYByValue(session.SD3H * rowSize + rowSize); // top of high row
                    float lY  = chartScale.GetYByValue(session.SD3L * rowSize);           // bottom of low row
                    float bL  = rightEdge - vpWidth - 4;
                    float bR  = rightEdge + 2;
                    RenderTarget.DrawLine(new SharpDX.Vector2(bL, hY), new SharpDX.Vector2(bR, hY), sd3HLineBrush, 2); // top
                    RenderTarget.DrawLine(new SharpDX.Vector2(bL, lY), new SharpDX.Vector2(bR, lY), sd3LLineBrush, 2); // bottom
                    RenderTarget.DrawLine(new SharpDX.Vector2(bL, hY), new SharpDX.Vector2(bL, lY), sd3HLineBrush, 2); // left
                    RenderTarget.DrawLine(new SharpDX.Vector2(bR, hY), new SharpDX.Vector2(bR, lY), sd3HLineBrush, 2); // right
                }
                if (ShowSD2VA && session.SD2H > 0 && session.SD2L > 0 && showVolLevelLines)
                {
                    float hY  = chartScale.GetYByValue(session.SD2H * rowSize + rowSize); // top of high row
                    float lY  = chartScale.GetYByValue(session.SD2L * rowSize);           // bottom of low row
                    float bL  = rightEdge - vpWidth - 3;
                    float bR  = rightEdge + 1;
                    RenderTarget.DrawLine(new SharpDX.Vector2(bL, hY), new SharpDX.Vector2(bR, hY), sd2HLineBrush, 2); // top
                    RenderTarget.DrawLine(new SharpDX.Vector2(bL, lY), new SharpDX.Vector2(bR, lY), sd2LLineBrush, 2); // bottom
                    RenderTarget.DrawLine(new SharpDX.Vector2(bL, hY), new SharpDX.Vector2(bL, lY), sd2HLineBrush, 2); // left
                    RenderTarget.DrawLine(new SharpDX.Vector2(bR, hY), new SharpDX.Vector2(bR, lY), sd2HLineBrush, 2); // right
                }
                if (ShowVolumeValueArea && session.VVAH > 0 && session.VVAL > 0 && showVolLevelLines)
                {
                    float hY  = chartScale.GetYByValue(session.VVAH * rowSize + rowSize); // top of high row
                    float lY  = chartScale.GetYByValue(session.VVAL * rowSize);           // bottom of low row
                    float bL  = rightEdge - vpWidth - 2;
                    float bR  = rightEdge + 2;
                    RenderTarget.DrawLine(new SharpDX.Vector2(bL, hY), new SharpDX.Vector2(bR, hY), vvahLineBrush, 2); // top
                    RenderTarget.DrawLine(new SharpDX.Vector2(bL, lY), new SharpDX.Vector2(bR, lY), vvalLineBrush, 2); // bottom
                    RenderTarget.DrawLine(new SharpDX.Vector2(bL, hY), new SharpDX.Vector2(bL, lY), vvahLineBrush, 2); // left
                    RenderTarget.DrawLine(new SharpDX.Vector2(bR, hY), new SharpDX.Vector2(bR, lY), vvahLineBrush, 2); // right
                }
            }
            else // STVPDisplayStyle.Lines — each of the 6 VA boundaries is an independent horizontal line
            {
                float lineLeft  = startX;
                // Current session: extend to the far right edge of the chart
                float lineRight = isCurrent && ChartPanel != null
                    ? (ChartPanel.X + ChartPanel.W)
                    : rightEdge;

                if (ShowSD3VA && session.SD3H > 0 && session.SD3L > 0 && showVolLevelLines)
                {
                    float hY = chartScale.GetYByValue(session.SD3H * rowSize + rowSize); // top of high row
                    float lY = chartScale.GetYByValue(session.SD3L * rowSize);           // bottom of low row
                    RenderTarget.DrawLine(new SharpDX.Vector2(lineLeft, hY), new SharpDX.Vector2(lineRight, hY), sd3HLineBrush, 2);
                    RenderTarget.DrawLine(new SharpDX.Vector2(lineLeft, lY), new SharpDX.Vector2(lineRight, lY), sd3LLineBrush, 2);
                }
                if (ShowSD2VA && session.SD2H > 0 && session.SD2L > 0 && showVolLevelLines)
                {
                    float hY = chartScale.GetYByValue(session.SD2H * rowSize + rowSize); // top of high row
                    float lY = chartScale.GetYByValue(session.SD2L * rowSize);           // bottom of low row
                    RenderTarget.DrawLine(new SharpDX.Vector2(lineLeft, hY), new SharpDX.Vector2(lineRight, hY), sd2HLineBrush, 2);
                    RenderTarget.DrawLine(new SharpDX.Vector2(lineLeft, lY), new SharpDX.Vector2(lineRight, lY), sd2LLineBrush, 2);
                }
                if (ShowVolumeValueArea && session.VVAH > 0 && session.VVAL > 0 && showVolLevelLines)
                {
                    float hY = chartScale.GetYByValue(session.VVAH * rowSize + rowSize); // top of high row
                    float lY = chartScale.GetYByValue(session.VVAL * rowSize);           // bottom of low row
                    RenderTarget.DrawLine(new SharpDX.Vector2(lineLeft, hY), new SharpDX.Vector2(lineRight, hY), vvahLineBrush, 2);
                    RenderTarget.DrawLine(new SharpDX.Vector2(lineLeft, lY), new SharpDX.Vector2(lineRight, lY), vvalLineBrush, 2);
                }
            }

            // LVN extension fills — one semi-transparent rectangle per LVN row, spanning from
            // the session start bar to the profile right edge.  Drawn before the histogram bars
            // so bars always render on top of the tinted background.
            if (ShowLVNExtension && rightEdge > startX)
            {
                float fillWidth = rightEdge - startX;
                foreach (var kvp in session.VolumeAtPrice)
                {
                    int  level  = kvp.Key;
                    long volume = kvp.Value;

                    // Only LVN rows (exclude VPOC even if its volume happens to be low).
                    // In HVN/LVN Only Mode the LVN color is applied to any bar below avgVolume,
                    // so the extension fill gate must match that same boundary.
                    bool isRenderedAsLvn = HvnLvnOnlyMode ? (volume < avgVolume) : (volume <= lvnThreshold);
                    if (level == session.VPOCLevel || !isRenderedAsLvn) continue;

                    // Pick the brush for this side of the VPOC
                    SharpDX.Direct2D1.Brush fillBrush = level > session.VPOCLevel
                        ? lvnExtAboveBrush
                        : lvnExtBelowBrush;
                    if (fillBrush == null) continue;

                    float rowTopY, rowBottomY;
                    GetLevelYBounds(chartScale, level, out rowTopY, out rowBottomY);
                    float rowH       = rowBottomY - rowTopY;
                    if (rowH <= 0) continue;

                    var fillRect = new SharpDX.RectangleF(startX, rowTopY, fillWidth, rowH);
                    RenderTarget.FillRectangle(fillRect, fillBrush);
                }
            }

            if (VolumeBarDisplayMode == STVPBarDisplayMode.Delta)
            {
                // ── DELTA MODE ────────────────────────────────────────────────────────────
                // Layer 1 — standard volume bars drawn first so the familiar HVN/LVN/VPOC
                //           shape is visible as a background reference.
                foreach (var kvp in session.VolumeAtPrice)
                {
                    int   level   = kvp.Key;
                    long  volume  = kvp.Value;

                    float priceY, priceTY;
                    GetLevelYBounds(chartScale, level, out priceTY, out priceY);
                    float rowH    = priceY - priceTY;

                    float barW    = (float)volume / maxVolume * vpWidth;
                    float barLeft = rightEdge - barW;

                    SharpDX.Direct2D1.Brush barBrush;
                    if (level == session.VPOCLevel)
                        barBrush = vpocBrush;
                    else if (volume >= hvnThreshold)
                        barBrush = hvnBrush;
                    else if (volume <= lvnThreshold)
                        barBrush = lvnBrush;
                    else
                        barBrush = HvnLvnOnlyMode
                            ? (volume >= avgVolume ? hvnBrush : lvnBrush)
                            : mvnBrush;

                    var rect = new SharpDX.RectangleF(barLeft, priceTY, barW, Math.Max(1f, rowH - 1f));
                    RenderTarget.FillRectangle(rect, barBrush);
                    RenderTarget.DrawRectangle(rect, volumeBarOutlineBrush, 1);
                }

                // Layer 2 — delta bars overlaid on top with a semi-transparent fill so the
                //           volume background shows through.  Bar width scales to maxAbsDelta
                //           so the delta shape is independent of the volume shape.
                //           Green = net buy pressure (delta > 0), Red = net sell pressure.
                //           Zero-delta rows keep a 1 px hairline so every level stays visible.
                long maxAbsDelta = 1;  // avoid divide-by-zero
                foreach (var kvp in session.VolumeAtPrice)
                {
                    int  level = kvp.Key;
                    long buy   = session.BuyVolumeAtPrice.ContainsKey(level)  ? session.BuyVolumeAtPrice[level]  : 0;
                    long sell  = session.SellVolumeAtPrice.ContainsKey(level) ? session.SellVolumeAtPrice[level] : 0;
                    long absDelta = Math.Abs(buy - sell);
                    if (absDelta > maxAbsDelta) maxAbsDelta = absDelta;
                }

                foreach (var kvp in session.VolumeAtPrice)
                {
                    int  level = kvp.Key;
                    long buy   = session.BuyVolumeAtPrice.ContainsKey(level)  ? session.BuyVolumeAtPrice[level]  : 0;
                    long sell  = session.SellVolumeAtPrice.ContainsKey(level) ? session.SellVolumeAtPrice[level] : 0;
                    long delta = buy - sell;

                    float priceY, priceTY;
                    GetLevelYBounds(chartScale, level, out priceTY, out priceY);
                    float rowH    = priceY - priceTY;

                    float barW    = Math.Max(1f, (float)Math.Abs(delta) / maxAbsDelta * vpWidth);
                    float barLeft = rightEdge - barW;

                    SharpDX.Direct2D1.Brush fillBrush    = delta >= 0 ? deltaPositiveFillBrush : deltaNegativeFillBrush;
                    SharpDX.Direct2D1.Brush outlineBrush = delta >= 0 ? deltaPositiveBrush     : deltaNegativeBrush;
                    if (fillBrush == null || outlineBrush == null) continue;

                    var rect = new SharpDX.RectangleF(barLeft, priceTY, barW, Math.Max(1f, rowH - 1f));
                    RenderTarget.FillRectangle(rect, fillBrush);
                    RenderTarget.DrawRectangle(rect, outlineBrush, 1);

                    // Delta label inside bar (only when it fits)
                    if (ShowVolumeLabels && rowH >= 8 && barW >= 20)
                    {
                        string lbl = (delta >= 0 ? "+" : "") + delta.ToString();
                        RenderTarget.DrawText(lbl, volumeFormat,
                            new SharpDX.RectangleF(barLeft + 2, priceTY, barW - 4, rowH), textBrush);
                    }
                }
            }
            else if (VolumeBarDisplayMode == STVPBarDisplayMode.BidAsk)
            {
                // ── BUY/SELL FOOTPRINT + VOLUME PROFILE ─────────────────────────────────
                // Ladder-style columns (left → right), matching GemsTraderLadder:
                //
                //  Session Sell | Window Sell | Window Buy | Session Buy | Vol Profile
                //  (outer red)    (inner)       (inner)      (outer green)
                //
                // Either outer pair, inner pair, or both can be enabled.

                bool showSess = ShowSessionBuysSells;
                bool showWin  = ShowSlidingWindowBuysSells;
                if (!showSess && !showWin) showSess = true; // always show at least session

                // Scale font first, then size columns tightly around digit width (ladder-dense).
                float sampleRowH = Math.Abs(chartScale.GetYByValue(0) - chartScale.GetYByValue(rowSize));
                float fpFontSize = Math.Max(5f, Math.Min(16f, sampleRowH * 0.72f));
                // ~5 monospace digits + small pad; clamp so 4 columns stay compact next to the profile
                float colW = Math.Max(16f, Math.Min(32f, fpFontSize * 2.4f + 6f));

                int nFpCols = (showSess ? 2 : 0) + (showWin ? 2 : 0);
                float fpWidth = colW * nFpCols;
                float fpRight = rightEdge - vpWidth;                  // vol-profile left edge
                float fpLeft  = fpRight - fpWidth;                    // footprint left edge

                // Column left edges (L→R) — packed, no extra gap between columns
                float x0 = fpLeft;
                float x1 = fpLeft + colW;
                float x2 = fpLeft + colW * 2f;
                float x3 = fpLeft + colW * 3f;
                // Map logical columns depending on which pairs are on:
                // If both: 0=sessSell, 1=winSell, 2=winBuy, 3=sessBuy
                // If only session: 0=sessSell, 1=sessBuy
                // If only window:  0=winSell,  1=winBuy
                float sessSellX = showSess ? x0 : -1f;
                float winSellX  = showWin  ? (showSess ? x1 : x0) : -1f;
                float winBuyX   = showWin  ? (showSess ? x2 : x1) : -1f;
                float sessBuyX  = showSess ? (showWin ? x3 : x1) : -1f;

                SharpDX.DirectWrite.TextFormat fpBidFormat = null; // trailing (sells face inward)
                SharpDX.DirectWrite.TextFormat fpAskFormat = null; // leading  (buys face inward)
                try
                {
                    fpBidFormat = new SharpDX.DirectWrite.TextFormat(
                        NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", fpFontSize);
                    fpBidFormat.TextAlignment = TextAlignment.Trailing;
                    fpBidFormat.WordWrapping  = WordWrapping.NoWrap;
                    fpAskFormat = new SharpDX.DirectWrite.TextFormat(
                        NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", fpFontSize);
                    fpAskFormat.TextAlignment = TextAlignment.Leading;
                    fpAskFormat.WordWrapping  = WordWrapping.NoWrap;
                }
                catch { /* fall back to cached formats below */ }

                // Session totals for the top/bottom control summary.
                long totalBidVol = 0;  // sell aggressor total
                long totalAskVol = 0;  // buy  aggressor total
                long totalWinBuy  = 0; // sliding-window buy  aggressor total (same levels as the window columns)
                long totalWinSell = 0; // sliding-window sell aggressor total
                int  winMinLevel = int.MaxValue; // lowest / highest level with live sliding-window trades
                int  winMaxLevel = int.MinValue;
                foreach (var kvp2 in session.VolumeAtPrice)
                {
                    int  lv  = kvp2.Key;
                    long b   = session.SellVolumeAtPrice.ContainsKey(lv) ? session.SellVolumeAtPrice[lv] : 0;
                    long a   = session.BuyVolumeAtPrice.ContainsKey(lv)  ? session.BuyVolumeAtPrice[lv]  : 0;
                    totalBidVol += b;
                    totalAskVol += a;

                    long wbT, wsT;
                    bool hasWb = windowBuyAtPrice.TryGetValue(lv, out wbT)  && wbT > 0;
                    bool hasWs = windowSellAtPrice.TryGetValue(lv, out wsT) && wsT > 0;
                    if (hasWb) totalWinBuy  += wbT;
                    if (hasWs) totalWinSell += wsT;
                    if (hasWb || hasWs)
                    {
                        if (lv < winMinLevel) winMinLevel = lv;
                        if (lv > winMaxLevel) winMaxLevel = lv;
                    }
                }

                // ── Auction zones: structure first → horizontal control → imbalance confirms ──
                //
                // Order of operations (market auction theory):
                //   1) Volume-profile structure — HVN / VPOC bands = acceptance body;
                //      thinner / LVN areas = transition or excess.
                //   2) Horizontal bid vs ask — who owned each level inside that structure.
                //   3) Diagonal imbalance — optional initiative confirmation (does not define
                //      the zone; boosts conviction when it agrees with horizontal control).
                //
                // Strong zones  = acceptance body (HVN/VPOC) with clear horizontal side.
                // Base zones    = one-sided horizontal control outside the acceptance body
                //                 (transition / responsive activity through thinner book).
                if (ShowImbalanceZones && buyZoneFillBrush != null && sellZoneFillBrush != null)
                {
                    long horizFloor = Math.Max(1L, (long)ZoneHorizontalControlFloor);

                    // Per-level facts
                    var levels = new List<int>(session.VolumeAtPrice.Keys);
                    levels.Sort();

                    // horizSign[lv]: +1 buy owned level, -1 sell owned, 0 neutral
                    // isAccept[lv]:  true if HVN or VPOC (acceptance structure)
                    // diagSign[lv]:  +1 buy initiative, -1 sell initiative, 0 none
                    var horizSign = new Dictionary<int, int>();
                    var isAccept  = new Dictionary<int, bool>();
                    var diagSign  = new Dictionary<int, int>();
                    var netHoriz  = new Dictionary<int, long>(); // ask - bid raw

                    foreach (int lv in levels)
                    {
                        long ask  = session.BuyVolumeAtPrice.ContainsKey(lv)  ? session.BuyVolumeAtPrice[lv]  : 0;
                        long bid  = session.SellVolumeAtPrice.ContainsKey(lv) ? session.SellVolumeAtPrice[lv] : 0;
                        long vol  = session.VolumeAtPrice[lv];
                        long bBel = session.SellVolumeAtPrice.ContainsKey(lv - 1) ? session.SellVolumeAtPrice[lv - 1] : 0;
                        long aAbv = session.BuyVolumeAtPrice.ContainsKey(lv + 1)  ? session.BuyVolumeAtPrice[lv + 1]  : 0;

                        long hEx = ask - bid;
                        netHoriz[lv] = hEx;
                        if (hEx >= horizFloor)       horizSign[lv] =  1;
                        else if (-hEx >= horizFloor) horizSign[lv] = -1;
                        else                         horizSign[lv] =  0;

                        isAccept[lv] = (lv == session.VPOCLevel) || (vol >= hvnThreshold);

                        // Zone confirmation always uses diagonal initiative (horizontal ownership is separate)
                        long buyOpp  = bBel;
                        long sellOpp = aAbv;
                        bool zBuy  = IsDiagonalImbalance(ask, buyOpp);
                        bool zSell = IsDiagonalImbalance(bid, sellOpp);
                        double rBuy  = DiagonalRatio(ask, buyOpp);
                        double rSell = DiagonalRatio(bid, sellOpp);
                        if (zBuy && (!zSell || rBuy > rSell))        diagSign[lv] =  1;
                        else if (zSell && (!zBuy || rSell > rBuy))   diagSign[lv] = -1;
                        else                                         diagSign[lv] =  0;
                    }

                    float zoneLeft  = fpLeft;
                    float zoneWidth = rightEdge - fpLeft;

                    // Helper: draw one zone rectangle
                    System.Action<int, int, bool, bool> paintZone = (lo, hi, isBuy, strong) =>
                    {
                        float topY = chartScale.GetYByValue((hi + 1) * rowSize);
                        float botY = chartScale.GetYByValue(lo * rowSize);
                        float zh   = botY - topY;
                        if (zh <= 0) return;
                        SharpDX.Direct2D1.Brush fillBr = isBuy
                            ? (strong ? buyZoneFillStrongBrush   : buyZoneFillBrush)
                            : (strong ? sellZoneFillStrongBrush  : sellZoneFillBrush);
                        SharpDX.Direct2D1.Brush borderBr = isBuy
                            ? (strong ? buyZoneBorderStrongBrush : buyZoneBorderBrush)
                            : (strong ? sellZoneBorderStrongBrush: sellZoneBorderBrush);
                        float stroke = strong ? 2.5f : 1.5f;
                        var zr = new SharpDX.RectangleF(zoneLeft, topY, zoneWidth, zh);
                        if (fillBr != null)   RenderTarget.FillRectangle(zr, fillBr);
                        if (borderBr != null) RenderTarget.DrawRectangle(zr, borderBr, stroke);
                    };

                    // Track level ranges already claimed by acceptance zones so transition
                    // zones do not double-paint the same rows.
                    var claimed = new HashSet<int>();

                    // ── Pass 1: ACCEPTANCE zones from HVN / VPOC structure ─────────────
                    // Contiguous runs of acceptance rows. Side = net horizontal delta
                    // across the band (who owned value). Imbalance agreement is confirmatory
                    // only — zone still forms from structure + horizontal alone.
                    {
                        int i = 0;
                        while (i < levels.Count)
                        {
                            if (!isAccept[levels[i]]) { i++; continue; }
                            int j = i;
                            while (j + 1 < levels.Count
                                   && levels[j + 1] == levels[j] + 1
                                   && isAccept[levels[j + 1]])
                                j++;

                            int lo = levels[i];
                            int hi = levels[j];
                            int span = hi - lo + 1;
                            if (span >= MinLevelsForImbalanceZone)
                            {
                                long net = 0;
                                int  imbAgreeBuy = 0, imbAgreeSell = 0;
                                for (int lv = lo; lv <= hi; lv++)
                                {
                                    long nh;
                                    if (netHoriz.TryGetValue(lv, out nh)) net += nh;
                                    int ds;
                                    if (diagSign.TryGetValue(lv, out ds))
                                    {
                                        if (ds > 0) imbAgreeBuy++;
                                        if (ds < 0) imbAgreeSell++;
                                    }
                                }
                                // Need a clear horizontal side on the acceptance body
                                if (net != 0)
                                {
                                    bool isBuy = net > 0;
                                    // Always strong — this is acceptance structure.
                                    // Imbalance agreement is informational only here.
                                    paintZone(lo, hi, isBuy, true);
                                    for (int lv = lo; lv <= hi; lv++) claimed.Add(lv);
                                }
                            }
                            i = j + 1;
                        }
                    }

                    // ── Pass 2: TRANSITION zones from horizontal control outside value ─
                    // Contiguous same-side horizontal ownership through non-acceptance
                    // rows (MVN/LVN). These are responsive / breakout / excess character.
                    // Diagonal imbalance agreement upgrades base → slightly stronger feel
                    // by using the strong brush only when a majority of rows confirm.
                    {
                        int i = 0;
                        while (i < levels.Count)
                        {
                            int side = horizSign[levels[i]];
                            if (side == 0 || claimed.Contains(levels[i])) { i++; continue; }

                            int j = i;
                            while (j + 1 < levels.Count
                                   && levels[j + 1] == levels[j] + 1
                                   && horizSign[levels[j + 1]] == side
                                   && !claimed.Contains(levels[j + 1]))
                                j++;

                            int lo = levels[i];
                            int hi = levels[j];
                            int span = hi - lo + 1;
                            if (span >= MinLevelsForImbalanceZone)
                            {
                                int confirm = 0, rows = 0;
                                for (int lv = lo; lv <= hi; lv++)
                                {
                                    rows++;
                                    int ds;
                                    if (diagSign.TryGetValue(lv, out ds) && ds == side)
                                        confirm++;
                                }
                                // Majority imbalance agreement → treat as confirmed transition
                                bool confirmed = rows > 0 && confirm * 2 >= rows;
                                paintZone(lo, hi, side > 0, confirmed);
                            }
                            i = j + 1;
                        }
                    }
                }

                float boxTopY    = float.MaxValue;
                float boxBottomY = float.MinValue;

                foreach (var kvp in session.VolumeAtPrice)
                {
                    int  level  = kvp.Key;
                    long volume = kvp.Value;
                    long sessSell = session.SellVolumeAtPrice.ContainsKey(level) ? session.SellVolumeAtPrice[level] : 0;
                    long sessBuy  = session.BuyVolumeAtPrice.ContainsKey(level)  ? session.BuyVolumeAtPrice[level]  : 0;
                    long winBuy = 0, winSell = 0;
                    windowBuyAtPrice.TryGetValue(level, out winBuy);
                    windowSellAtPrice.TryGetValue(level, out winSell);

                    // Imbalance / dominance use session totals (structure is session-based)
                    long bid = sessSell;
                    long ask = sessBuy;

                    float priceY, priceTY;
                    GetLevelYBounds(chartScale, level, out priceTY, out priceY);
                    float rowH    = priceY - priceTY;
                    float h       = Math.Max(1f, rowH - 1f);

                    // Track bounds for the vol-profile box
                    if (priceTY < boxTopY)    boxTopY    = priceTY;
                    if (priceY  > boxBottomY) boxBottomY = priceY;

                    // ── Imbalance highlights on the active buy/sell cells ────────────────
                    if (ShowDiagonalImbalance && (buyingImbalanceBrush != null || sellingImbalanceBrush != null))
                    {
                        // Helper: paint buy and/or sell imbalance cell(s) for one data source
                        Action<long, long, long, long, float, float> paintImb = (buyVol, sellVol, sellBelow, buyAbove, buyX, sellX) =>
                        {
                            // Diagonal initiative only for cell highlights (horizontal colors the numbers separately)
                            long buyOpp  = sellBelow;
                            long sellOpp = buyAbove;
                            double buyExcess  = DiagonalRatio(buyVol, buyOpp);
                            double sellExcess = DiagonalRatio(sellVol, sellOpp);
                            bool buyImb  = IsDiagonalImbalance(buyVol, buyOpp)  && buyingImbalanceBrush  != null;
                            bool sellImb = IsDiagonalImbalance(sellVol, sellOpp) && sellingImbalanceBrush != null;

                            if (buyImb && sellImb)
                            {
                                if (buyExcess > sellExcess && buyX >= 0)
                                    RenderTarget.FillRectangle(new SharpDX.RectangleF(buyX, priceTY, colW, h), buyingImbalanceBrush);
                                else if (sellExcess > buyExcess && sellX >= 0)
                                    RenderTarget.FillRectangle(new SharpDX.RectangleF(sellX, priceTY, colW, h), sellingImbalanceBrush);
                            }
                            else if (buyImb && buyX >= 0)
                                RenderTarget.FillRectangle(new SharpDX.RectangleF(buyX, priceTY, colW, h), buyingImbalanceBrush);
                            else if (sellImb && sellX >= 0)
                                RenderTarget.FillRectangle(new SharpDX.RectangleF(sellX, priceTY, colW, h), sellingImbalanceBrush);
                        };

                        // Session imbalances → outer session columns
                        if (showSess)
                        {
                            long sessSellBelow = session.SellVolumeAtPrice.ContainsKey(level - 1)
                                ? session.SellVolumeAtPrice[level - 1] : 0;
                            long sessBuyAbove  = session.BuyVolumeAtPrice.ContainsKey(level + 1)
                                ? session.BuyVolumeAtPrice[level + 1] : 0;
                            paintImb(sessBuy, sessSell, sessSellBelow, sessBuyAbove, sessBuyX, sessSellX);
                        }

                        // Sliding-window imbalances → inner window columns (same Multiple / Mode / Min Volume)
                        if (showWin)
                        {
                            long winSellBelow = 0, winBuyAbove = 0;
                            windowSellAtPrice.TryGetValue(level - 1, out winSellBelow);
                            windowBuyAtPrice.TryGetValue(level + 1, out winBuyAbove);
                            paintImb(winBuy, winSell, winSellBelow, winBuyAbove, winBuyX, winSellX);
                        }
                    }

                    // ── Column dividers ──
                    for (int ci = 1; ci < nFpCols; ci++)
                    {
                        float dx = fpLeft + colW * ci;
                        RenderTarget.DrawLine(
                            new SharpDX.Vector2(dx, priceTY),
                            new SharpDX.Vector2(dx, priceTY + h),
                            textBrush, 0.5f);
                    }

                    // ── Footprint numbers (ladder order L→R): ──
                    // Session Sell | Window Sell | Window Buy | Session Buy
                    if (ShowVolumeLabels && rowH >= 5 && textBrush != null)
                    {
                        var sellFmt = fpBidFormat ?? volumeFormat;   // trailing
                        var buyFmt  = fpAskFormat ?? bidAskAskFormat; // leading

                        // Horizontal imbalance: color the winning side only when multiple is met; else both neutral
                        SharpDX.Direct2D1.Brush sessBuyBrush  = textBrush;
                        SharpDX.Direct2D1.Brush sessSellBrush = textBrush;
                        if (IsHorizontalImbalance(sessBuy, sessSell) && sameLevelBuyDomBrush != null)
                            sessBuyBrush = sameLevelBuyDomBrush;
                        else if (IsHorizontalImbalance(sessSell, sessBuy) && sameLevelSellDomBrush != null)
                            sessSellBrush = sameLevelSellDomBrush;

                        SharpDX.Direct2D1.Brush winBuyBrush  = textBrush;
                        SharpDX.Direct2D1.Brush winSellBrush = textBrush;
                        if (IsHorizontalImbalance(winBuy, winSell) && sameLevelBuyDomBrush != null)
                            winBuyBrush = sameLevelBuyDomBrush;
                        else if (IsHorizontalImbalance(winSell, winBuy) && sameLevelSellDomBrush != null)
                            winSellBrush = sameLevelSellDomBrush;

                        if (showSess && sellFmt != null && sessSell > 0)
                            RenderTarget.DrawText(sessSell.ToString(), sellFmt,
                                new SharpDX.RectangleF(sessSellX, priceTY, colW - 2f, rowH), sessSellBrush);
                        if (showWin && sellFmt != null && winSell > 0)
                            RenderTarget.DrawText(winSell.ToString(), sellFmt,
                                new SharpDX.RectangleF(winSellX, priceTY, colW - 2f, rowH), winSellBrush);
                        if (showWin && buyFmt != null && winBuy > 0)
                            RenderTarget.DrawText(winBuy.ToString(), buyFmt,
                                new SharpDX.RectangleF(winBuyX + 2f, priceTY, colW - 2f, rowH), winBuyBrush);
                        if (showSess && buyFmt != null && sessBuy > 0)
                            RenderTarget.DrawText(sessBuy.ToString(), buyFmt,
                                new SharpDX.RectangleF(sessBuyX + 2f, priceTY, colW - 2f, rowH), sessBuyBrush);
                    }

                    // ── Volume profile: bars grow LEFT→RIGHT from fpRight ──
                    float barW    = (float)volume / maxVolume * vpWidth;

                    SharpDX.Direct2D1.Brush barBrush;
                    if (level == session.VPOCLevel)
                        barBrush = vpocBrush;
                    else if (volume >= hvnThreshold)
                        barBrush = hvnBrush;
                    else if (volume <= lvnThreshold)
                        barBrush = lvnBrush;
                    else
                        barBrush = HvnLvnOnlyMode
                            ? (volume >= avgVolume ? hvnBrush : lvnBrush)
                            : mvnBrush;

                    RenderTarget.FillRectangle(new SharpDX.RectangleF(fpRight, priceTY, barW, h), barBrush);
                    RenderTarget.DrawRectangle(new SharpDX.RectangleF(fpRight, priceTY, barW, h), volumeBarOutlineBrush, 1);
                }

                // ── Volume profile bounding box ──────────────────────────────────────────
                // A thin outline around the vol-profile column separates it visually from
                // the footprint.  Uses textBrush at half-weight so it is subtle.
                if (boxBottomY > boxTopY)
                    RenderTarget.DrawRectangle(
                        new SharpDX.RectangleF(fpRight, boxTopY, vpWidth, boxBottomY - boxTopY),
                        textBrush, 0.5f);

                // ── Session Buy vs Sell totals at top and bottom of the footprint ────────
                // Placed under the session columns when those are shown.
                if (boxBottomY > boxTopY && textBrush != null && showSess)
                {
                    SharpDX.Direct2D1.Brush bidTotBrush = textBrush;
                    SharpDX.Direct2D1.Brush askTotBrush = textBrush;
                    if (totalBidVol > totalAskVol && sameLevelSellDomBrush != null)
                        bidTotBrush = sameLevelSellDomBrush;
                    else if (totalAskVol > totalBidVol && sameLevelBuyDomBrush != null)
                        askTotBrush = sameLevelBuyDomBrush;

                    var totSellFmt = volumeFormat;
                    var totBuyFmt  = bidAskAskFormat;
                    float totH = Math.Max(12f, Math.Min(18f, sampleRowH + 4f));
                    float sellX = sessSellX >= 0 ? sessSellX : fpLeft;
                    float buyX  = sessBuyX  >= 0 ? sessBuyX  : fpLeft + colW;

                    float topY = boxTopY - totH - 2f;
                    if (totSellFmt != null)
                        RenderTarget.DrawText(totalBidVol.ToString(), totSellFmt,
                            new SharpDX.RectangleF(sellX, topY, colW - 2f, totH), bidTotBrush);
                    if (totBuyFmt != null)
                        RenderTarget.DrawText(totalAskVol.ToString(), totBuyFmt,
                            new SharpDX.RectangleF(buyX + 2f, topY, colW - 2f, totH), askTotBrush);

                    float botY = boxBottomY + 2f;
                    if (totSellFmt != null)
                        RenderTarget.DrawText(totalBidVol.ToString(), totSellFmt,
                            new SharpDX.RectangleF(sellX, botY, colW - 2f, totH), bidTotBrush);
                    if (totBuyFmt != null)
                        RenderTarget.DrawText(totalAskVol.ToString(), totBuyFmt,
                            new SharpDX.RectangleF(buyX + 2f, botY, colW - 2f, totH), askTotBrush);
                }

                // ── Sliding-window Buy vs Sell totals at top and bottom of the window columns ──
                // Same treatment as the session totals above: winner highlighted.
                if (boxBottomY > boxTopY && textBrush != null && showWin && ShowSlidingWindowTotals
                    && winMaxLevel >= winMinLevel)
                {
                    SharpDX.Direct2D1.Brush winSellTotBrush = textBrush;
                    SharpDX.Direct2D1.Brush winBuyTotBrush  = textBrush;
                    if (totalWinSell > totalWinBuy && sameLevelSellDomBrush != null)
                        winSellTotBrush = sameLevelSellDomBrush;
                    else if (totalWinBuy > totalWinSell && sameLevelBuyDomBrush != null)
                        winBuyTotBrush = sameLevelBuyDomBrush;

                    // Totals are drawn 2 pt larger than the footprint numbers (so they stand out),
                    // scale with zoom (fpFontSize follows row height), and get a white box.
                    var wTotFmt = new SharpDX.DirectWrite.TextFormat(
                        NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", fpFontSize + 2f);
                    wTotFmt.TextAlignment = TextAlignment.Leading;
                    wTotFmt.WordWrapping  = WordWrapping.NoWrap;
                    var wBoxBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);

                    // Anchor to the live sliding-window range (not the whole profile) so the
                    // totals travel with the window. Offset = Ticks Per Row x multiple, in ticks above / below that range.
                    float winTopEdgeY, winBotEdgeY, dummyY;
                    GetLevelYBounds(chartScale, winMaxLevel, out winTopEdgeY, out dummyY);
                    GetLevelYBounds(chartScale, winMinLevel, out dummyY, out winBotEdgeY);
                    int offTicks = Math.Max(1, TicksPerRow) * SlidingWindowTotalsOffsetMultiple;
                    float offPx = offTicks
                                  * Math.Abs(chartScale.GetYByValue(0) - chartScale.GetYByValue(TickSize));

                    float wSellRightX = winSellX + colW - 2f; // sell total hugs the inner edge, grows left
                    float wBuyLeftX   = winBuyX + 2f;         // buy total hugs the inner edge, grows right
                    float wTopEdge = winTopEdgeY - offPx;
                    float wBotEdge = winBotEdgeY + offPx;
                    string wSellTxt = totalWinSell.ToString();
                    string wBuyTxt  = totalWinBuy.ToString();

                    DrawWindowTotal(wSellTxt, wTotFmt, wSellRightX, 1, wTopEdge, true,  winSellTotBrush, wBoxBrush);
                    float aggBoxH =
                    DrawWindowTotal(wBuyTxt,  wTotFmt, wBuyLeftX,   0, wTopEdge, true,  winBuyTotBrush,  wBoxBrush);
                    DrawWindowTotal(wSellTxt, wTotFmt, wSellRightX, 1, wBotEdge, false, winSellTotBrush, wBoxBrush);
                    DrawWindowTotal(wBuyTxt,  wTotFmt, wBuyLeftX,   0, wBotEdge, false, winBuyTotBrush,  wBoxBrush);

                    // Go / No-Go circle — replaces the old margin number entirely.
                    // Centered between the window Buy/Sell totals (top + bottom of the live window).
                    // Filled circle in winner color = GO (window agrees with session)
                    // Filled circle in yellow       = NO-GO (disagree or either side tied)
                    int sessSide = totalAskVol > totalBidVol ? 1 : (totalBidVol > totalAskVol ? -1 : 0); // +1 buy, -1 sell
                    int winSide  = totalWinBuy > totalWinSell ? 1 : (totalWinSell > totalWinBuy ? -1 : 0);
                    bool agree   = sessSide != 0 && winSide != 0 && sessSide == winSide;

                    SharpDX.Direct2D1.SolidColorBrush noGoBrush = new SharpDX.Direct2D1.SolidColorBrush(
                        RenderTarget, new SharpDX.Color4(1.00f, 0.85f, 0.00f, 1f)); // yellow
                    SharpDX.Direct2D1.Brush circleBrush = noGoBrush;
                    if (agree)
                    {
                        if (winSide > 0 && sameLevelBuyDomBrush != null)
                            circleBrush = sameLevelBuyDomBrush;
                        else if (winSide < 0 && sameLevelSellDomBrush != null)
                            circleBrush = sameLevelSellDomBrush;
                    }

                    float wCenterX = (wSellRightX + wBuyLeftX) * 0.5f;
                    float radius = Math.Max(5f, Math.Min(9f, fpFontSize * 0.55f + 2f));
                    // Top circle (above the window totals)
                    float topCy = wTopEdge - aggBoxH - 2f - radius;
                    RenderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(wCenterX, topCy), radius, radius), circleBrush);
                    RenderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(wCenterX, topCy), radius, radius), wBoxBrush, 1f);
                    // Bottom circle (below the window totals)
                    float botCy = wBotEdge + aggBoxH + 2f + radius;
                    RenderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(wCenterX, botCy), radius, radius), circleBrush);
                    RenderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(wCenterX, botCy), radius, radius), wBoxBrush, 1f);

                    noGoBrush.Dispose();
                    wBoxBrush.Dispose();
                    wTotFmt.Dispose();
                }

                if (fpBidFormat != null) { fpBidFormat.Dispose(); fpBidFormat = null; }
                if (fpAskFormat != null) { fpAskFormat.Dispose(); fpAskFormat = null; }
            }
            else
            {
                // ── VOLUME MODE (default) ────────────────────────────────────────────────
                foreach (var kvp in session.VolumeAtPrice)
                {
                    int   level   = kvp.Key;
                    long  volume  = kvp.Value;

                    float priceY, priceTY;
                    GetLevelYBounds(chartScale, level, out priceTY, out priceY);
                    float rowH    = priceY - priceTY;

                    float barW    = (float)volume / maxVolume * vpWidth;
                    float barLeft = rightEdge - barW;

                    SharpDX.Direct2D1.Brush barBrush;
                    if (level == session.VPOCLevel)
                        barBrush = vpocBrush;
                    else if (volume >= hvnThreshold)
                        barBrush = hvnBrush;
                    else if (volume <= lvnThreshold)
                        barBrush = lvnBrush;
                    else
                        // HVN/LVN Only Mode: re-color MVN bars as HVN or LVN depending on
                        // whether their volume is above or below the session average.
                        // Every bar still renders — only the color classification changes.
                        barBrush = HvnLvnOnlyMode
                            ? (volume >= avgVolume ? hvnBrush : lvnBrush)
                            : mvnBrush;

                    var rect = new SharpDX.RectangleF(barLeft, priceTY, barW, Math.Max(1f, rowH - 1f));
                    RenderTarget.FillRectangle(rect, barBrush);
                    RenderTarget.DrawRectangle(rect, volumeBarOutlineBrush, 1);

                    // Volume label inside bar (only when it fits)
                    if (ShowVolumeLabels && rowH >= 8 && barW >= 20)
                    {
                        SharpDX.Direct2D1.Brush lblBrush = (level == session.VPOCLevel) ? vpocBrush
                            : (volume >= hvnThreshold) ? hvnBrush
                            : textBrush;
                        RenderTarget.DrawText(volume.ToString(), volumeFormat,
                            new SharpDX.RectangleF(barLeft + 2, priceTY, barW - 4, rowH), lblBrush);
                    }
                }
            }
        }

        private void RenderVPOCLine(ChartScale chartScale, SessionProfile session, int startX, int endX)
        {
            // VPOC line at mid-row so it sits centred in the bar
            double vpocPrice = session.VPOCLevel * rowSize + (rowSize * 0.5);
            float  y         = chartScale.GetYByValue(vpocPrice);
            RenderTarget.DrawLine(
                new SharpDX.Vector2(startX, y),
                new SharpDX.Vector2(endX,   y),
                vpocBrush, 2); // solid within session; extension lines use dashStrokeStyle
        }

        private void RenderSessionStats(ChartScale chartScale, SessionProfile session, int startX)
        {
            if (session.SessionLow == double.MaxValue) return;

            float panelY = chartScale.GetYByValue(session.SessionLow) + 5f;
            int   panelX = startX + 5;
            int   lineH  = 11;
            int   y      = (int)panelY;

            // Date
            RenderTarget.DrawText(session.SessionDate.ToString("MM/dd"), statsFormat,
                new SharpDX.RectangleF(panelX, y, 60, lineH), textBrush);
            y += lineH;

            // Session range
            if (session.SessionHigh != double.MinValue)
            {
                double range = session.SessionHigh - session.SessionLow;
                RenderTarget.DrawText("Rng: " + range.ToString("F2"), statsFormat,
                    new SharpDX.RectangleF(panelX, y, 80, lineH), textBrush);
                y += lineH;
            }

            // IB range
            if (session.IBComplete && session.IBHigh > 0 && session.IBLow > 0)
            {
                double ibRange = IBHighPx(session) - IBLowPx(session);
                RenderTarget.DrawText("IB: " + ibRange.ToString("F2"), statsFormat,
                    new SharpDX.RectangleF(panelX, y, 80, lineH), ibBrush);
                y += lineH;
            }

            // VPOC price
            if (session.VPOCLevel > 0)
            {
                double vpocPrice = session.VPOCLevel * rowSize + (rowSize * 0.5);
                RenderTarget.DrawText("VPOC: " + vpocPrice.ToString("F2"), statsFormat,
                    new SharpDX.RectangleF(panelX, y, 100, lineH), vpocBrush);
                y += lineH;
            }

            // 1σ VA range
            if (session.VVAH > 0 && session.VVAL > 0)
            {
                double hi = session.VVAH * rowSize + rowSize;
                double lo = session.VVAL * rowSize;
                RenderTarget.DrawText("1σ: " + lo.ToString("F0") + "–" + hi.ToString("F0"),
                    statsFormat, new SharpDX.RectangleF(panelX, y, 110, lineH), volumeVABoxBrush);
                y += lineH;
            }

            // 2σ VA range
            if (ShowSD2VA && session.SD2H > 0 && session.SD2L > 0)
            {
                double hi = session.SD2H * rowSize + rowSize;
                double lo = session.SD2L * rowSize;
                RenderTarget.DrawText("2σ: " + lo.ToString("F0") + "–" + hi.ToString("F0"),
                    statsFormat, new SharpDX.RectangleF(panelX, y, 110, lineH), sd2VABoxBrush);
                y += lineH;
            }

            // 3σ VA range
            if (ShowSD3VA && session.SD3H > 0 && session.SD3L > 0)
            {
                double hi = session.SD3H * rowSize + rowSize;
                double lo = session.SD3L * rowSize;
                RenderTarget.DrawText("3σ: " + lo.ToString("F0") + "–" + hi.ToString("F0"),
                    statsFormat, new SharpDX.RectangleF(panelX, y, 110, lineH), sd3VABoxBrush);
            }
        }

        #endregion

        // =========================================================
        //  TPO RENDERING LAYER
        // =========================================================

        #region TPO Rendering

        private void RenderTPOLayer(ChartControl chartControl, ChartScale chartScale,
            SessionProfile session, int startX, int endX, int sessionWidth)
        {
            if (session.TPOsAtPrice.Count == 0) return;

            float rowHeight = Math.Abs(chartScale.GetYByValue(0) - chartScale.GetYByValue(rowSize));
            bool  isCurrentSess     = (session == currentSession);
            bool  showTPOLevelLines = !isCurrentSess || ShowCurrentTPOLevels;

            // TPO letters
            if (ShowTPOLetters && session.Periods.Count > 0 && letterFormat != null && tpoLetterBrush != null)
            {
                int   numPeriods = session.Periods.Count;
                float colW       = numPeriods > 0 ? (float)sessionWidth / numPeriods : sessionWidth;
                for (int pi = 0; pi < numPeriods; pi++)
                {
                    var   period = session.Periods[pi];
                    float colX   = startX + pi * colW;
                    foreach (int level in period.PriceLevelsTouched)
                    {
                        double lo = LevelToPrice(level);
                        double mid = (TicksPerRow > 1) ? (lo + rowSize * 0.5) : lo;
                        float y  = chartScale.GetYByValue(mid);
                        RenderTarget.DrawText(period.Letter.ToString(), letterFormat,
                            new SharpDX.RectangleF(colX, y - rowHeight * 0.5f, Math.Max(colW, 4f), rowHeight),
                            tpoLetterBrush);
                    }
                }
            }

            // TPO POC line
            if (ShowTPOPOC && session.POCLevel > 0 && tpoPocBrush != null && showTPOLevelLines)
            {
                float pocY = chartScale.GetYByValue(session.POCLevel * rowSize + rowSize * 0.5f);
                RenderTarget.DrawLine(new SharpDX.Vector2(startX, pocY), new SharpDX.Vector2(endX, pocY), tpoPocBrush, 2);
            }

            // TPO VA box + VAH/VAL lines
            if (ShowTPOVAHVAL && session.TPOVAH > 0 && session.TPOVAL > 0 && showTPOLevelLines)
            {
                float vahY = chartScale.GetYByValue(session.TPOVAH * rowSize + rowSize); // top of high row
                float valY = chartScale.GetYByValue(session.TPOVAL * rowSize);           // bottom of low row

                if (tpoVABoxBrush != null)
                    RenderTarget.DrawRectangle(
                        new SharpDX.RectangleF(startX - 1, vahY, sessionWidth + 2, valY - vahY),
                        tpoVABoxBrush, 2);

                if (tpoVAHBrush != null)
                    RenderTarget.DrawLine(new SharpDX.Vector2(startX, vahY), new SharpDX.Vector2(endX, vahY), tpoVAHBrush, 2);
                if (tpoVALBrush != null)
                    RenderTarget.DrawLine(new SharpDX.Vector2(startX, valY), new SharpDX.Vector2(endX, valY), tpoVALBrush, 2);
            }

            // TPO SD2 lines
            if (ShowTPOSD2 && session.TPOSD2H > 0 && session.TPOSD2L > 0 && tpoSD2Brush != null && showTPOLevelLines)
            {
                float sd2hY = chartScale.GetYByValue(session.TPOSD2H * rowSize + rowSize);
                float sd2lY = chartScale.GetYByValue(session.TPOSD2L * rowSize);
                RenderTarget.DrawLine(new SharpDX.Vector2(startX, sd2hY), new SharpDX.Vector2(endX, sd2hY), tpoSD2Brush, 2);
                RenderTarget.DrawLine(new SharpDX.Vector2(startX, sd2lY), new SharpDX.Vector2(endX, sd2lY), tpoSD2Brush, 2);
            }
            if (ShowTPOSD3 && session.TPOSD3H > 0 && session.TPOSD3L > 0 && tpoSD3Brush != null && showTPOLevelLines)
            {
                float sd3hY = chartScale.GetYByValue(session.TPOSD3H * rowSize + rowSize);
                float sd3lY = chartScale.GetYByValue(session.TPOSD3L * rowSize);
                RenderTarget.DrawLine(new SharpDX.Vector2(startX, sd3hY), new SharpDX.Vector2(endX, sd3hY), tpoSD3Brush, 2);
                RenderTarget.DrawLine(new SharpDX.Vector2(startX, sd3lY), new SharpDX.Vector2(endX, sd3lY), tpoSD3Brush, 2);
            }

            // Globex TPO levels (split mode only) — dashed so they read distinctly from RTH
            if (SplitGlobexRthTPO && showTPOLevelLines && dashStrokeStyle != null)
            {
                if (ShowTPOPOC && session.GlobexPOCLevel > 0 && tpoPocBrush != null)
                {
                    float y = chartScale.GetYByValue(session.GlobexPOCLevel * rowSize + rowSize * 0.5f);
                    RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), tpoPocBrush, 1.5f, dashStrokeStyle);
                }
                if (ShowTPOVAHVAL)
                {
                    if (session.GlobexTPOVAH > 0 && tpoVAHBrush != null)
                    {
                        float y = chartScale.GetYByValue(session.GlobexTPOVAH * rowSize + rowSize);
                        RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), tpoVAHBrush, 1.5f, dashStrokeStyle);
                    }
                    if (session.GlobexTPOVAL > 0 && tpoVALBrush != null)
                    {
                        float y = chartScale.GetYByValue(session.GlobexTPOVAL * rowSize);
                        RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), tpoVALBrush, 1.5f, dashStrokeStyle);
                    }
                }
                if (ShowTPOSD2 && tpoSD2Brush != null)
                {
                    if (session.GlobexTPOSD2H > 0)
                    {
                        float y = chartScale.GetYByValue(session.GlobexTPOSD2H * rowSize + rowSize);
                        RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), tpoSD2Brush, 1.5f, dashStrokeStyle);
                    }
                    if (session.GlobexTPOSD2L > 0)
                    {
                        float y = chartScale.GetYByValue(session.GlobexTPOSD2L * rowSize);
                        RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), tpoSD2Brush, 1.5f, dashStrokeStyle);
                    }
                }
                if (ShowTPOSD3 && tpoSD3Brush != null)
                {
                    if (session.GlobexTPOSD3H > 0)
                    {
                        float y = chartScale.GetYByValue(session.GlobexTPOSD3H * rowSize + rowSize);
                        RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), tpoSD3Brush, 1.5f, dashStrokeStyle);
                    }
                    if (session.GlobexTPOSD3L > 0)
                    {
                        float y = chartScale.GetYByValue(session.GlobexTPOSD3L * rowSize);
                        RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), tpoSD3Brush, 1.5f, dashStrokeStyle);
                    }
                }
            }

            // Single prints — current session only (dashed zone rectangles).
            // Extend to the far right edge (endX is already chart-right for current session).
            if (ShowSinglePrints && session == currentSession && singlePrintBrush != null && dashStrokeStyle != null)
            {
                float zoneW = Math.Max(sessionWidth, endX - startX);
                foreach (var zone in session.SinglePrintZones)
                {
                    float topY    = chartScale.GetYByValue(zone.HighLevel * rowSize + rowSize);
                    float bottomY = chartScale.GetYByValue(zone.LowLevel  * rowSize);
                    var   rect    = new SharpDX.RectangleF(startX, topY, zoneW, bottomY - topY);
                    RenderTarget.DrawRectangle(rect, singlePrintBrush, 2, dashStrokeStyle);
                }
            }

            // Poor highs/lows
            if (ShowPoorHighsLows && session != currentSession && poorHighLowBrush != null)
            {
                if (session.HasPoorHigh && session.PoorHighPrice > 0)
                { float y = chartScale.GetYByValue(session.PoorHighPrice);
                  RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), poorHighLowBrush, 2); }
                if (session.HasPoorLow && session.PoorLowPrice > 0)
                { float y = chartScale.GetYByValue(session.PoorLowPrice);
                  RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), poorHighLowBrush, 2); }
            }

            // Excess
            if (ShowExcess && session != currentSession && excessBrush != null)
            {
                if (session.HasExcessHigh && session.ExcessHighPrice > 0)
                { float y = chartScale.GetYByValue(session.ExcessHighPrice + rowSize);
                  RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), excessBrush, 2); }
                if (session.HasExcessLow && session.ExcessLowPrice > 0)
                { float y = chartScale.GetYByValue(session.ExcessLowPrice);
                  RenderTarget.DrawLine(new SharpDX.Vector2(startX, y), new SharpDX.Vector2(endX, y), excessBrush, 2); }
            }
        }

        #endregion

        // =========================================================
        //  Extension line helpers
        // =========================================================

        #region Extension Line Helpers

        /// <summary>
        /// Returns the X coordinate the extension line should stop at.
        /// Untested levels run all the way to the far right edge of the chart.
        /// If breakTime is set, the line stops at the session where the level was tested
        /// (or at the far right if the break occurred in the current session).
        /// </summary>
        private int GetExtensionEndX(DateTime breakTime)
        {
            // Far right edge of the price panel
            int fullX = ChartPanel != null
                ? (int)(ChartPanel.X + ChartPanel.W)
                : ChartControl.GetXByBarIndex(ChartBars, ChartBars.Count - 1) + 5;

            if (breakTime == DateTime.MinValue) return fullX;

            // Breaks that belong to the live session must always extend to the right
            // edge. Do not rely only on currentSession.StartTime: on time-based primary
            // charts that timestamp can lag the ticks that formed the first bar, so a
            // valid breakTime can sort *before* StartTime and fall through to the prior
            // session (which returns endX == sessionEndX and suppresses the line).
            // That is why pTPOVAL could vanish on a 5‑minute chart but appear on volume bars.
            if (currentSession != null)
            {
                if (breakTime >= currentSession.StartTime)
                    return fullX;

                // Prior session already closed before this break → treat as current
                if (mostRecentCompletedSession != null
                    && mostRecentCompletedSession.EndTime > DateTime.MinValue
                    && breakTime > mostRecentCompletedSession.EndTime)
                    return fullX;

                // First primary bar of the live session is a reliable lower bound even
                // when StartTime was set from a later bar-close timestamp.
                if (currentSession.StartBarIndex >= 0 && ChartBars != null
                    && ChartBars.Bars != null
                    && currentSession.StartBarIndex < ChartBars.Count)
                {
                    DateTime firstBarTime = ChartBars.Bars.GetTime(currentSession.StartBarIndex);
                    // Bar time is typically the close of a time-based bar; include ticks
                    // that printed during that first bar by using the bar period lookback.
                    DateTime firstBarOpenApprox = firstBarTime;
                    try
                    {
                        if (BarsPeriod != null && BarsPeriod.BarsPeriodType == BarsPeriodType.Minute
                            && BarsPeriod.Value > 0)
                            firstBarOpenApprox = firstBarTime.AddMinutes(-BarsPeriod.Value);
                        else if (BarsPeriod != null && BarsPeriod.BarsPeriodType == BarsPeriodType.Second
                            && BarsPeriod.Value > 0)
                            firstBarOpenApprox = firstBarTime.AddSeconds(-BarsPeriod.Value);
                    }
                    catch { /* BarsPeriod may be unavailable in some host contexts */ }

                    if (breakTime >= firstBarOpenApprox)
                        return fullX;
                }
            }

            // Break occurred inside a completed session → terminate at that session's end
            for (int i = sessions.Count - 1; i >= 0; i--)
            {
                var s = sessions[i];
                if (s == currentSession) continue;
                if (s.StartBarIndex < 0) continue;
                if (breakTime < s.StartTime) continue;
                int endIdx = s.EndBarIndex >= 0 ? s.EndBarIndex : s.StartBarIndex;
                if (endIdx < 0) continue;
                int ex = ChartControl.GetXByBarIndex(ChartBars,
                    Math.Min(ChartBars.Count - 1, endIdx));
                return Math.Min(ex, fullX);
            }
            return fullX;
        }

        /// <summary>
        /// X position for level labels at the far right of the chart,
        /// inset by <see cref="LevelLabelRightOffset"/> so labels can be
        /// shifted left of the right edge.
        /// </summary>
        private float GetCanonicalLabelAnchorX()
        {
            if (ChartPanel != null)
                return ChartPanel.X + ChartPanel.W - LevelLabelRightOffset;
            if (currentSession == null || currentSession.EndBarIndex < 0)
                return ChartControl.GetXByBarIndex(ChartBars, ChartBars.Count - 1) + 5;
            return ChartControl.GetXByBarIndex(ChartBars,
                Math.Min(ChartBars.Count - 1, currentSession.EndBarIndex)) + 5f;
        }

        private void RenderExtensionLine(ChartScale chartScale, int sessionEndX,
            double price, DateTime breakTime, SharpDX.Direct2D1.Brush brush, string label, bool drawZone = false)
        {
            if (brush == null) return;
            int endX = GetExtensionEndX(breakTime);
            if (endX <= sessionEndX) return;

            if (drawZone && ShowLevelZones)
                DrawLevelZone(chartScale, sessionEndX, endX, price);

            float y = chartScale.GetYByValue(price);
            RenderTarget.DrawLine(
                new SharpDX.Vector2(sessionEndX, y),
                new SharpDX.Vector2(endX, y),
                brush, 2, dashStrokeStyle);

            // Show label only when line reaches the right margin or was broken in current session
            bool brokenInCurrent = breakTime != DateTime.MinValue
                && currentSession != null && breakTime >= currentSession.StartTime;

            if (breakTime == DateTime.MinValue || brokenInCurrent)
            {
                float labelX = GetCanonicalLabelAnchorX();
                RenderTarget.DrawText(label, statsFormat,
                    new SharpDX.RectangleF(labelX, y - 7, 120, 14), brush);
            }
        }

        private void RenderExtensionZone(ChartScale chartScale, SinglePrintZone zone, int sessionEndX)
        {
            if (zone.IsFullyTested || singlePrintBrush == null || dashStrokeStyle == null) return;
            int endX = GetExtensionEndX(zone.BreakTime);
            if (endX <= sessionEndX) return;
            int low  = zone.UntestedLevels.Count > 0 ? zone.UntestedLevels.Min() : zone.LowLevel;
            int high = zone.UntestedLevels.Count > 0 ? zone.UntestedLevels.Max() : zone.HighLevel;
            float topY    = chartScale.GetYByValue(high * rowSize + rowSize);
            float bottomY = chartScale.GetYByValue(low  * rowSize);
            var rect = new SharpDX.RectangleF(sessionEndX, topY, endX - sessionEndX, bottomY - topY);
            RenderTarget.DrawRectangle(rect, singlePrintBrush, 2, dashStrokeStyle);
        }

        #endregion
        // =========================================================

        // =========================================================
        //  REGIME & DOMINANCE (non-lagging order-flow + structure)
        // =========================================================

        #region Regime & Dominance

        /// <summary>
        /// Pure non-lagging regime engine.
        /// Uses only:
        ///   • aggressive bid/ask volume already accumulated per primary bar
        ///   • session cumulative delta
        ///   • developing TPO kurtosis / single-print presence
        ///   • consecutive same-side directional control
        /// No ATR, no SMA, no lagging volatility averages.
        /// </summary>
        /// <summary>
        /// Tick-path regime/dominance update for the forming primary bar.
        /// Runs on every tick so IN CHARGE Clear/Strong flips alert and mark mid-bar.
        /// </summary>
        private void UpdateRegimeAndDominanceRealtime(int primaryBarIdx)
        {
            if (!EnableRegimeDetection) return;
            if (currentSession == null) return;
            if (primaryBarIdx < DominanceWindowBars + 2) return;
            if (BarsArray == null || BarsArray[0] == null) return;
            if (primaryBarIdx < 0 || primaryBarIdx >= BarsArray[0].Count) return;

            // Reuse full regime engine against the live primary series state.
            // Open/High/Low/Close[0] are the developing primary bar when called mid-bar.
            int saved = CurrentBars[0];
            // UpdateRegimeAndDominance always uses CurrentBars[0]; when we are on the tick
            // series that index already points at the forming primary bar.
            UpdateRegimeAndDominance();

            // Ensure flip check ran for this primary bar with the latest dominance
            // (UpdateRegimeAndDominance already calls CheckInChargeClearStrongFlip).
            // Force a second check in case bar index differed.
            if (dominanceAtBar.ContainsKey(primaryBarIdx))
                CheckInChargeClearStrongFlip(primaryBarIdx, dominanceAtBar[primaryBarIdx]);
            else
                CheckInChargeClearStrongFlip(primaryBarIdx, dominance);
            // Vacuum (VU/VD) is evaluated on CLOSED bars only — see EvaluateVacuumFailureAt
            EvaluateIntrabarFailedBreakouts();
            CheckLimitEntryExpiry();
        }

        private void UpdateRegimeAndDominance()
        {
            if (CurrentBars[0] < DominanceWindowBars + 2) return;
            if (currentSession == null) return;

            int window = DominanceWindowBars;
            int barIdx = CurrentBars[0];

            // ── 1. Aggressive imbalance intensity over the window ────────────────
            long winAsk = 0;   // buy aggressor
            long winBid = 0;   // sell aggressor
            for (int i = 0; i < window; i++)
            {
                int b = barIdx - i;
                if (b < 0) break;
                long a = 0, s = 0;
                primaryBarAskVol.TryGetValue(b, out a);
                primaryBarBidVol.TryGetValue(b, out s);
                winAsk += a;
                winBid += s;
            }
            long winTotal = winAsk + winBid;
            double intensity = 0.0;          // 0…1
            double signedDom  = 0.0;         // −1…+1
            if (winTotal > 0)
            {
                long net = winAsk - winBid;
                intensity = Math.Abs((double)net) / winTotal;
                signedDom = (double)net / winTotal;
            }

            // ── 2. Consecutive same-side directional control ─────────────────────
            // Count how many of the last N bars closed in the same direction as the
            // current dominant aggressor (or simply same close direction).
            int sameDirCount = 0;
            int refDir = 0;
            if (Close[0] > Open[0]) refDir = 1;
            else if (Close[0] < Open[0]) refDir = -1;

            if (refDir != 0)
            {
                for (int i = 0; i < window; i++)
                {
                    if (Close[i] > Open[i] && refDir > 0) sameDirCount++;
                    else if (Close[i] < Open[i] && refDir < 0) sameDirCount++;
                    else break;   // streak broken
                }
            }
            double streakScore = (double)sameDirCount / window;   // 0…1

            // ── 3. Structure: open discovery vs forming value ────────────────────
            // High TPO kurtosis → price clustered → balanced / auction character.
            // Presence of single-print zones that remain untested → still discovery.
            double kurt = ComputeTPOKurtosis(currentSession);
            double kurtScore = 0.0;   // higher = more balanced
            if (!double.IsNaN(kurt))
            {
                // Excess kurtosis > 1 → strongly leptokurtic (balanced)
                // < −0.5 → platykurtic (trend / discovery)
                kurtScore = Math.Max(-1.0, Math.Min(1.0, kurt / 2.0));
            }

            bool hasOpenSinglePrints = currentSession.SinglePrintZones != null
                && currentSession.SinglePrintZones.Any(z => !z.IsFullyTested);

            // ── 4. Composite OneSidedness (0…1) ─────────────────────────────────
            // Intensity above floor is the primary driver; streak amplifies it;
            // open single prints add a structural boost.
            double oneSided = 0.0;
            if (intensity >= ImbalanceIntensityFloor)
            {
                oneSided = intensity;
                oneSided = Math.Min(1.0, oneSided + 0.25 * streakScore);
                if (hasOpenSinglePrints) oneSided = Math.Min(1.0, oneSided + 0.15);
            }
            else
            {
                // Even below the floor a strong streak still contributes modestly
                oneSided = 0.35 * streakScore;
            }

            // ── 5. Depth signal (live L2 or tick-derived proxy) ───────────────────
            // Goal: every bar — historical or live — contributes a depth component
            // so historical regime bands are not missing market-depth influence.
            //
            // Priority:
            //   1) Live L2 book (realtime only)
            //   2) Tick-derived proxy from aggressive volume vs bar range
            //      (works on pure historical data where native depth is unavailable)
            double bookImb  = 0.0;
            double absorp   = 0.0;
            bool   fromLive = false;

            bool l2Live = UseLevel2Book
                && State == State.Realtime
                && l2LastUpdateTime != DateTime.MinValue
                && (GetMarketNow() - l2LastUpdateTime).TotalSeconds < 5.0;

            if (l2Live)
            {
                bookImb  = l2BookImbalance;
                absorp   = l2AbsorptionScore;
                fromLive = true;
            }
            else if (UseLevel2Book)
            {
                // Tick-derived depth proxy (historical + live when L2 not flowing)
                // Book imbalance proxy: opposing aggressor residual.
                // If buyers were aggressive but price did not expand up → resting offers absorbed them
                // (ask-heavy book proxy). Symmetric for sellers.
                double rangeTicks = 0;
                if (High[0] > Low[0] && TickSize > 0)
                    rangeTicks = (High[0] - Low[0]) / TickSize;

                long barAsk = 0, barBid = 0;
                primaryBarAskVol.TryGetValue(barIdx, out barAsk);
                primaryBarBidVol.TryGetValue(barIdx, out barBid);
                long barTot = barAsk + barBid;

                if (barTot > 0 && rangeTicks >= 0)
                {
                    double askFrac = (double)barAsk / barTot;
                    double bidFrac = (double)barBid / barTot;

                    // High aggression + small range = absorption (resting size held)
                    // High aggression + large range = initiative (book was thin / pulled)
                    double aggressIntensity = Math.Abs(askFrac - bidFrac);
                    double rangeNorm = Math.Min(1.0, rangeTicks / 20.0); // ~20 ticks as "wide" for NQ-ish

                    // Absorption proxy: strong one-sided aggression that did NOT produce range
                    absorp = aggressIntensity * (1.0 - rangeNorm);
                    absorp = Math.Max(0.0, Math.Min(1.0, absorp));

                    // Book imbalance proxy from residual:
                    // If buyers aggressive but absorbed → book was ask-heavy (negative)
                    // If sellers aggressive but absorbed → book was bid-heavy (positive)
                    if (absorp > 0.10)
                        bookImb = (bidFrac > askFrac) ? absorp : -absorp;
                    else
                        // No absorption: book leaned with the aggressors (thin on the passive side)
                        bookImb = signedDom * (1.0 - aggressIntensity * 0.5);

                    bookImb = Math.Max(-1.0, Math.Min(1.0, bookImb));
                }
            }

            // ── 6. Balance score (0…1) ───────────────────────────────────────────
            // Opposing participation (low intensity) + high kurtosis + no open singles
            // + depth absorption (pulls toward Auction)
            double balance = 0.0;
            balance += (1.0 - intensity) * 0.55;
            if (!double.IsNaN(kurt) && kurt > 0.5)
                balance += Math.Min(0.35, kurt * 0.15);
            if (!hasOpenSinglePrints && currentSession.TPOsAtPrice.Count > 8)
                balance += 0.15;
            if (UseLevel2Book && absorp > 0.15)
                balance += L2AbsorptionWeight * absorp;
            balance = Math.Min(1.0, balance);

            // ── 7. RegimeScore & state ───────────────────────────────────────────
            regimeScore = oneSided - balance;          // −1 … +1
            regimeScore = Math.Max(-1.0, Math.Min(1.0, regimeScore));

            if (regimeScore >= DiscoveryThreshold)
                regimeState = STVPRegimeState.Discovery;
            else if (regimeScore <= AuctionThreshold)
                regimeState = STVPRegimeState.Auction;
            else
                regimeState = STVPRegimeState.Transition;

            // ── 8. Dominance (aggressor + depth book) ────────────────────────────
            {
                long cAsk = 0, cBid = 0;
                primaryBarAskVol.TryGetValue(barIdx, out cAsk);
                primaryBarBidVol.TryGetValue(barIdx, out cBid);
                double bd, pd;
                double cwd = ControlWindowDom(barIdx, signedDom);
                // Tick-driven call: BarsInProgress == 1, so Open[0]/High[0]/Low[0]/Close[0] are one tick.
                // Use the forming primary bar's OHLC built from ticks. Bar-close call (BIP 0) uses real bar values.
                bool useFb = BarsInProgress == 1 && fbBar == barIdx;
                double cO = useFb ? fbOpen : Open[0],  cH = useFb ? fbHigh : High[0];
                double cL = useFb ? fbLow  : Low[0],   cC = useFb ? fbLast : Close[0];
                dominance = ComputeControl(cAsk, cBid, cO, cH, cL, cC,
                                           cwd, out bd, out pd);
                if (LogControlOnNewBar && State == State.Realtime)
                    LogControlForBar(barIdx, cAsk + cBid, dominance, bd, pd, cwd);
            }

            lastRegimeBar = barIdx;

            // Persist for historical rendering (regime + depth always stored)
            regimeScoreAtBar[barIdx]   = regimeScore;
            dominanceAtBar[barIdx]     = dominance;
            regimeStateAtBar[barIdx]   = regimeState;
            bookImbalanceAtBar[barIdx] = bookImb;
            absorptionAtBar[barIdx]    = absorp;
            bookFromLiveL2[barIdx]     = fromLive;

            // In-charge Clear/Strong flip → alert + chart mark (same style as trade entry)
            CheckInChargeClearStrongFlip(barIdx, dominance);
            // Vacuum (VU/VD) is evaluated on CLOSED bars only — see EvaluateVacuumFailureAt
            // Entries: EvaluateMeanRevertVAEdge + EvaluateValueAreaRejection (+ optional IL/IZ) from tick path
        }

        /// <summary>Realtime Control log: one Output line per tick from the start of a new bar until the bar
        /// reaches ControlLogContracts. Only prints when the bar volume changed since the last line.</summary>

        /// <summary>
        /// If primary series is NT8 Volumetric, dump bid/ask footprint of the bar into the Output window.
        /// Lets you compare volumetric rows to our tick-built control / imbalance levels (YM or NQ tick charts).
        /// </summary>
        private void LogVolumetricBarSnapshot(int barIdx)
        {
            if (!LogVolumetricToControl || State != State.Realtime) return;
            if (BarsArray == null || BarsArray.Length == 0 || barIdx < 0) return;

            try
            {
                var volType = BarsArray[0].BarsType as VolumetricBarsType;
                if (volType == null)
                {
                    // One-time notice per session when user expected volumetric
                    if (barIdx == 0 || barIdx == 1)
                        Print("SessionTPOVolumeProfile VOLUMETRIC: primary series is not Volumetric bars (type="
                            + (BarsArray[0].BarsType != null ? BarsArray[0].BarsType.Name : "?")
                            + "). Use Chart Style → Volumetric to enable footprint rows in the Control log.");
                    return;
                }

                if (volType.Volumes == null || barIdx >= volType.Volumes.Length || volType.Volumes[barIdx] == null)
                    return;

                var vol = volType.Volumes[barIdx];
                long barDelta = 0;
                long total = 0;
                try { barDelta = vol.BarDelta; } catch { }
                try { total = vol.TotalVolume; } catch { total = 0; }

                double lo = Low[0], hi = High[0];
                if (lo <= 0 || hi <= 0 || hi < lo) return;

                long maxBid = 0, maxAsk = 0, maxTot = 0;
                double maxBidPx = 0, maxAskPx = 0, maxTotPx = 0;
                int buyImbRows = 0, sellImbRows = 0;
                long summed = 0;

                // Walk price levels in the bar (cap rows logged to avoid spam)
                var topImb = new List<string>();
                int levelsSeen = 0;
                for (double p = lo; p <= hi + TickSize * 0.1; p = Instrument.MasterInstrument.RoundToTickSize(p + TickSize))
                {
                    levelsSeen++;
                    if (levelsSeen > 500) break;

                    long bid = 0, ask = 0;
                    try { bid = vol.GetBidVolumeForPrice(p); } catch { }
                    try { ask = vol.GetAskVolumeForPrice(p); } catch { }
                    long rowTot = bid + ask;
                    if (rowTot <= 0) continue;
                    summed += rowTot;

                    if (bid > maxBid) { maxBid = bid; maxBidPx = p; }
                    if (ask > maxAsk) { maxAsk = ask; maxAskPx = p; }
                    if (rowTot > maxTot) { maxTot = rowTot; maxTotPx = p; }

                    // Diagonal imbalance vs one tick below/above (same definition as profile marks)
                    long bidBelow = 0, askAbove = 0;
                    double pBelow = Instrument.MasterInstrument.RoundToTickSize(p - TickSize);
                    double pAbove = Instrument.MasterInstrument.RoundToTickSize(p + TickSize);
                    try { bidBelow = vol.GetBidVolumeForPrice(pBelow); } catch { }
                    try { askAbove = vol.GetAskVolumeForPrice(pAbove); } catch { }

                    if (IsDiagonalImbalance(ask, bidBelow))
                    {
                        buyImbRows++;
                        if (topImb.Count < 8)
                            topImb.Add("BUY@" + p.ToString("F2") + " ask=" + ask + " x" + DiagonalRatio(ask, bidBelow).ToString("F1"));
                    }
                    if (IsDiagonalImbalance(bid, askAbove))
                    {
                        sellImbRows++;
                        if (topImb.Count < 8)
                            topImb.Add("SELL@" + p.ToString("F2") + " bid=" + bid + " x" + DiagonalRatio(bid, askAbove).ToString("F1"));
                    }
                }

                if (total <= 0) total = summed;

                Print("SessionTPOVolumeProfile VOLUMETRIC bar " + Time[0].ToString("HH:mm:ss")
                    + "  total=" + total
                    + "  delta=" + barDelta.ToString("+0;-0;0")
                    + "  POC-in-bar=" + maxTotPx.ToString("F2") + " (" + maxTot + ")"
                    + "  maxBid=" + maxBidPx.ToString("F2") + "/" + maxBid
                    + "  maxAsk=" + maxAskPx.ToString("F2") + "/" + maxAsk
                    + "  imbRows buy=" + buyImbRows + " sell=" + sellImbRows);

                if (topImb.Count > 0)
                    Print("SessionTPOVolumeProfile VOLUMETRIC imbalances: " + string.Join(" | ", topImb));
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile VOLUMETRIC log error: " + ex.Message);
            }
        }


        private void LogControlForBar(int barIdx, long barVol, double ctrl, double barDelta, double priceDir, double windowDom)
        {
            double armThr = ICArmThreshold();
            int zone = ctrl >= armThr ? 1 : (ctrl <= -armThr ? -1 : 0);

            if (barIdx != ctrlLogBar)
            {
                if (ControlLogBarSummary && ctrlLogBar >= 0 && ctrlStatLastVol >= 0)
                {
                    Print("BAR SUMMARY " + ctrlStatStart.ToString("HH:mm:ss") + "  contracts=" + ctrlStatLastVol
                        + "  finalCtrl=" + ctrlStatLast.ToString("+0.00;-0.00;0.00")
                        + "  peak=" + ctrlStatMax.ToString("+0.00;-0.00;0.00") + "@" + ctrlStatMaxVol
                        + "  trough=" + ctrlStatMin.ToString("+0.00;-0.00;0.00") + "@" + ctrlStatMinVol
                        + "  firstArm=" + (ctrlStatFirstArmSide > 0 ? "BUY@" + ctrlStatFirstArmVol : (ctrlStatFirstArmSide < 0 ? "SELL@" + ctrlStatFirstArmVol : "none"))
                        + "  zoneChanges=" + ctrlStatFlips + "  lastChange@" + ctrlStatLastFlipVol
                        + "  finalZone=" + (ctrlStatZone > 0 ? "BUY" : (ctrlStatZone < 0 ? "SELL" : "neutral")));
                }
                ctrlLogBar     = barIdx;
                ctrlLogVol     = -1;
                ctrlLogDone    = false;
                ctrlLogReached = false;
                ctrlLogZone    = 99;
                ctrlLogNextStep = 0;
                ctrlStatStart  = Time[0];
                ctrlStatLastVol = -1; ctrlStatMaxVol = 0; ctrlStatMinVol = 0; ctrlStatLastFlipVol = 0; ctrlStatFirstArmVol = 0;
                ctrlStatLast = 0; ctrlStatMax = double.MinValue; ctrlStatMin = double.MaxValue;
                ctrlStatFlips = 0; ctrlStatFirstArmSide = 0; ctrlStatZone = 99;
                Print("SessionTPOVolumeProfile CONTROL LOG ---- new bar " + Time[0].ToString("HH:mm:ss")
                    + "  (logging until " + ControlLogContracts + " contracts" + (ControlLogUntilFire ? " and IC fires" : "")
                    + ", every " + (ControlLogStepContracts > 0 ? ControlLogStepContracts + " contracts" : "tick") + " + zone changes"
                    + ", IC arms at +/-" + armThr.ToString("0.00") + ")");
                LogVolumetricBarSnapshot(barIdx);
            }

            // Per-bar stats run for the whole bar, independent of the print window
            if (barVol != ctrlStatLastVol)
            {
                ctrlStatLastVol = barVol;
                ctrlStatLast = ctrl;
                if (ctrl > ctrlStatMax) { ctrlStatMax = ctrl; ctrlStatMaxVol = barVol; }
                if (ctrl < ctrlStatMin) { ctrlStatMin = ctrl; ctrlStatMinVol = barVol; }
                if (zone != ctrlStatZone)
                {
                    if (ctrlStatZone != 99) ctrlStatFlips++;
                    ctrlStatZone = zone;
                    ctrlStatLastFlipVol = barVol;
                    if (zone != 0 && ctrlStatFirstArmSide == 0) { ctrlStatFirstArmSide = zone; ctrlStatFirstArmVol = barVol; }
                }
            }

            if (ctrlLogReached && (!ControlLogUntilFire || ctrlLogFiredBar == barIdx)) ctrlLogDone = true;
            if (ctrlLogDone || barVol == ctrlLogVol) return;
            ctrlLogVol = barVol;

            bool crossing = !ctrlLogReached && barVol >= ControlLogContracts;
            if (crossing) ctrlLogReached = true;
            bool firstLine = ctrlLogZone == 99;
            bool zoneChanged = zone != ctrlLogZone && !firstLine;
            bool stepHit = ControlLogStepContracts <= 0 || barVol >= ctrlLogNextStep;
            // Print on: first line of the bar, every N contracts, any arm-zone change, or the count crossing
            if (!(firstLine || stepHit || zoneChanged || crossing)) return;
            if (ControlLogStepContracts > 0 && barVol >= ctrlLogNextStep)
                ctrlLogNextStep = (barVol / ControlLogStepContracts + 1) * (long)ControlLogStepContracts;
            ctrlLogZone = zone;

            string tag = zone > 0 ? "  << IC arm BUY" : (zone < 0 ? "  << IC arm SELL" : "");
            Print("CTRL " + Time[0].ToString("HH:mm:ss.fff")
                + "  px=" + Close[0].ToString("F2")
                + "  vol=" + barVol
                + "  ctrl=" + ctrl.ToString("+0.00;-0.00;0.00")
                + "  (barDelta=" + barDelta.ToString("+0.00;-0.00;0.00")
                + " priceDir=" + priceDir.ToString("+0.00;-0.00;0.00")
                + " persist=" + windowDom.ToString("+0.00;-0.00;0.00") + ")"
                + tag + (zoneChanged ? "  [zone change]" : "")
                + (crossing ? "  ** " + ControlLogContracts + " CONTRACTS REACHED **" : ""));
        }

        /// <summary>Control log: marks the moment IN CHARGE actually fires (immediate path).
        /// The persist path already prints its own IC FIRE line.</summary>
        private void IcLogImmediateFire(int barIdx, int side, double dom)
        {
            ctrlLogFiredBar = barIdx;
            if (!LogControlOnNewBar || State != State.Realtime) return;
            long a = 0, b = 0;
            primaryBarAskVol.TryGetValue(barIdx, out a);
            primaryBarBidVol.TryGetValue(barIdx, out b);
            double pxFire = 0;
            try { if (Close != null && Close.Count > 0) pxFire = Close[0]; } catch { }
            Print("SessionTPOVolumeProfile IC FIRE " + (side > 0 ? "BUY" : "SELL") + " (immediate)"
                + "  " + Time[0].ToString("HH:mm:ss.fff") + "  px=" + pxFire.ToString("F2")
                + "  vol=" + (a + b) + "  control=" + dom.ToString("+0.00;-0.00")
                + "  armAt=+/-" + ICArmThreshold().ToString("0.00"));
            if (false && pxFire > 0)
                Print("SessionTPOVolumeProfile IC CTX " + FormatICAuctionContext(side, pxFire));
        }

        /// <summary>Aggressor balance over the Control window (ControlWindowBars) ending at 'bar'.
        /// -1 or equal to the Dominance Window = reuse the regime window value; 0 = unused.</summary>
        private double ControlWindowDom(int bar, double regimeWindowDom)
        {
            int w = ControlWindowBars;
            if (w < 0 || w == DominanceWindowBars) return regimeWindowDom;
            if (w == 0) return 0.0;
            long a = 0, b = 0;
            for (int i = 0; i < w; i++)
            {
                int idx = bar - i;
                if (idx < 0) break;
                long av = 0, bv = 0;
                primaryBarAskVol.TryGetValue(idx, out av);
                primaryBarBidVol.TryGetValue(idx, out bv);
                a += av; b += bv;
            }
            long t = a + b;
            return t > 0 ? (double)(a - b) / t : 0.0;
        }

        /// <summary>
        /// Who is in charge, -1 (sellers) ... +1 (buyers). ONE formula used by live bars and
        /// rebuilt history so they always agree. Inputs are order flow + price only (no depth):
        ///   40%  this bar's own aggressor balance (buy aggressors minus sell aggressors)
        ///   30%  this bar's price direction (where it closed inside its own range, open->close)
        ///   30%  trailing-window aggressor balance (persistence over the last N bars)
        /// </summary>
        private double ComputeControl(long barAsk, long barBid,
                                      double open, double high, double low, double close,
                                      double windowDom, out double barDelta, out double priceDir)
        {
            long tot = barAsk + barBid;
            barDelta = tot > 0 ? (double)(barAsk - barBid) / tot : 0.0;
            double range = high - low;
            priceDir = range > 0 ? (close - open) / range : 0.0;
            double c;
            if (ControlWindowBars == 0)
                c = (0.40 * barDelta + 0.30 * priceDir) / 0.70;   // persistence off: reweight so Control still spans -1..+1
            else
                c = 0.40 * barDelta + 0.30 * priceDir + 0.30 * windowDom;
            return Math.Max(-1.0, Math.Min(1.0, c));
        }

        private float GetRegimePanelHeight()
        {
            float h = false ? 56f : 42f;
            if (ShowRegimeDetailLine) h += 30f;
            return h;
        }

        /// <summary>
        /// Computes regime / control / depth-proxy for ONE bar purely from stored per-bar
        /// aggressive volume + that bar's OHLC. Used by the history rebuild and by the
        /// signal engine, so both read exactly the same numbers.
        /// Returns false when the bar has no tick volume (or is too early in the series).
        /// </summary>
        private bool ComputeRegimeAtBar(int bar, out double score, out double dom, out STVPRegimeState st,
                                        out double bookImb, out double absorp,
                                        out double barDelta, out double priceDir, out double windowDom)
        {
            score = 0; dom = 0; st = STVPRegimeState.Transition; bookImb = 0; absorp = 0;
            barDelta = 0; priceDir = 0; windowDom = 0;

            Bars pb = BarsArray[0];
            int window = DominanceWindowBars;
            if (pb == null || bar < window + 2 || bar >= pb.Count) return false;

            long winAsk = 0, winBid = 0;
            for (int i = 0; i < window; i++)
            {
                long a = 0, sv = 0;
                primaryBarAskVol.TryGetValue(bar - i, out a);
                primaryBarBidVol.TryGetValue(bar - i, out sv);
                winAsk += a; winBid += sv;
            }
            long winTotal = winAsk + winBid;
            if (winTotal <= 0) return false;

            windowDom = (double)(winAsk - winBid) / winTotal;
            double intensity = Math.Abs(windowDom);

            double cl = pb.GetClose(bar), op = pb.GetOpen(bar);
            int refDir = cl > op ? 1 : (cl < op ? -1 : 0);
            int same = 0;
            if (refDir != 0)
            {
                for (int i = 0; i < window; i++)
                {
                    double c = pb.GetClose(bar - i), o = pb.GetOpen(bar - i);
                    if ((c > o && refDir > 0) || (c < o && refDir < 0)) same++;
                    else break;
                }
            }
            double streakScore = (double)same / window;

            double oneSided;
            if (intensity >= ImbalanceIntensityFloor)
                oneSided = Math.Min(1.0, intensity + 0.25 * streakScore);
            else
                oneSided = 0.35 * streakScore;

            long barAsk = 0, barBid = 0;
            primaryBarAskVol.TryGetValue(bar, out barAsk);
            primaryBarBidVol.TryGetValue(bar, out barBid);

            if (UseLevel2Book)
            {
                long barTot = barAsk + barBid;
                if (barTot > 0)
                {
                    double rangeTicks = TickSize > 0 ? (pb.GetHigh(bar) - pb.GetLow(bar)) / TickSize : 0;
                    double askFrac = (double)barAsk / barTot;
                    double bidFrac = (double)barBid / barTot;
                    double aggress = Math.Abs(askFrac - bidFrac);
                    double rangeNorm = Math.Min(1.0, rangeTicks / 20.0);
                    absorp = Math.Max(0.0, Math.Min(1.0, aggress * (1.0 - rangeNorm)));
                    if (absorp > 0.10) bookImb = (bidFrac > askFrac) ? absorp : -absorp;
                    else               bookImb = windowDom * (1.0 - aggress * 0.5);
                    bookImb = Math.Max(-1.0, Math.Min(1.0, bookImb));
                }
            }

            double balance = (1.0 - intensity) * 0.55 + 0.15;
            if (UseLevel2Book && absorp > 0.15)
                balance += L2AbsorptionWeight * absorp;
            balance = Math.Min(1.0, balance);

            score = Math.Max(-1.0, Math.Min(1.0, oneSided - balance));
            st = score >= DiscoveryThreshold ? STVPRegimeState.Discovery
               : (score <= AuctionThreshold ? STVPRegimeState.Auction
                                            : STVPRegimeState.Transition);

            dom = ComputeControl(barAsk, barBid, op, pb.GetHigh(bar), pb.GetLow(bar), cl,
                                 ControlWindowDom(bar, windowDom), out barDelta, out priceDir);
            return true;
        }

        /// <summary>
        /// Rebuilds regime / control / depth for EVERY loaded bar from the stored per-bar
        /// aggressive bid/ask volume, so history never depends on having watched the chart live.
        /// Bars already stored by the live engine are left alone. Session-structure terms
        /// (TPO kurtosis, open single prints) can't be reconstructed per bar, so a typical
        /// developed-profile constant is used for those bars.
        /// </summary>
        private void RebuildRegimeHistory()
        {
            regimeHistoryRebuilt = true;
            if (!EnableRegimeDetection || BarsArray == null || BarsArray[0] == null) return;

            Bars pb = BarsArray[0];
            int count  = pb.Count;
            int window = DominanceWindowBars;
            int filled = 0, noVol = 0, kept = 0;
            int lastFilled = -1;

            for (int bar = window + 2; bar < count; bar++)
            {
                if (regimeStateAtBar.ContainsKey(bar)) { kept++; continue; }

                double score, dom, bookImb, absorp, bd, pd, wd;
                STVPRegimeState st;
                if (!ComputeRegimeAtBar(bar, out score, out dom, out st, out bookImb, out absorp,
                                        out bd, out pd, out wd))
                { noVol++; continue; }

                regimeScoreAtBar[bar]   = score;
                dominanceAtBar[bar]     = dom;
                regimeStateAtBar[bar]   = st;
                bookImbalanceAtBar[bar] = bookImb;
                absorptionAtBar[bar]    = absorp;
                bookFromLiveL2[bar]     = false;
                filled++;
                lastFilled = bar;

                if (count - bar <= 6)
                    Print("  bar " + bar + " " + pb.GetTime(bar).ToString("MM/dd HH:mm")
                        + "  barDelta " + bd.ToString("+0.00;-0.00")
                        + "  priceDir " + pd.ToString("+0.00;-0.00")
                        + "  window " + wd.ToString("+0.00;-0.00")
                        + "  => control " + dom.ToString("+0.00;-0.00"));
            }

            // Panel values on a closed market: seed from the newest stored bar
            if (lastRegimeBar < 0 && lastFilled >= 0)
            {
                lastRegimeBar = lastFilled;
                regimeScore   = regimeScoreAtBar[lastFilled];
                dominance     = dominanceAtBar[lastFilled];
                regimeState   = regimeStateAtBar[lastFilled];
            }

            Print("SessionTPOVolumeProfile regime history: bars=" + count
                + " rebuilt=" + filled + " alreadyStored=" + kept
                + " noTickVolume=" + noVol
                + (noVol > 0 ? "  <-- those bars have no tick data loaded (check Days to load / tick history)" : ""));

            // Walk history once so Clear/Strong flips get chart marks on load
            ScanInChargeFlipsFromHistory();
        }

        /// <summary>
        /// Historical strips, one cell per bar:
        ///   Row 1  MARKET     blue = Price Discovery, purple = Auction, grey = Transition
        ///   Row 2  IN CHARGE  green = buyers, orange = sellers (brighter = stronger)
        ///   Row 3  DEPTH      optional / advanced
        /// </summary>
        private void RenderRegimeHistoryStrips(ChartControl chartControl)
        {
            if (ChartPanel == null || ChartBars == null) return;
            if (regimeStateAtBar == null || regimeStateAtBar.Count == 0) return;
            if (regimeBuyerBrush == null || regimeSellerBrush == null) return;

            int fromBar = Math.Max(0, ChartBars.FromIndex);
            int toBar   = Math.Min(ChartBars.Count - 1, ChartBars.ToIndex);
            if (toBar < fromBar) return;

            float spacing = 4f;
            if (toBar > fromBar)
                spacing = Math.Max(2f, chartControl.GetXByBarIndex(ChartBars, fromBar + 1)
                                     - chartControl.GetXByBarIndex(ChartBars, fromBar));

            const float stripH = 10f;
            float y1 = ChartPanel.Y + 24f;   // clear the chart's indicator-title line
            if (ShowRegimePanel && RegimePanelPosition == STVPPanelPosition.TopLeft)
            {
                float panelY = 10f;
                if (ShowAnalyticsPanel && AnalyticsPanelPosition == RegimePanelPosition) panelY += 90f;
                y1 = ChartPanel.Y + panelY + GetRegimePanelHeight() + 6f;
            }
            float y2 = y1 + stripH + 2f;
            float y3 = y2 + stripH + 2f;

            float baseBuy  = regimeBuyerBrush.Opacity;
            float baseSell = regimeSellerBrush.Opacity;

            SharpDX.Direct2D1.SolidColorBrush discB = null, auctB = null, transB = null;
            try
            {
                discB  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.25f, 0.55f, 1.00f, 0.85f));
                auctB  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.70f, 0.45f, 0.95f, 0.85f));
                transB = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.60f, 0.60f, 0.60f, 0.55f));

                for (int bar = fromBar; bar <= toBar; bar++)
                {
                    float x    = chartControl.GetXByBarIndex(ChartBars, bar);
                    float left = x - spacing * 0.5f;
                    float w    = Math.Max(1f, spacing - 1f);

                    STVPRegimeState st;
                    if (regimeStateAtBar.TryGetValue(bar, out st))
                    {
                        SharpDX.Direct2D1.Brush rb = st == STVPRegimeState.Discovery ? (SharpDX.Direct2D1.Brush)discB
                                                   : (st == STVPRegimeState.Auction ? auctB : transB);
                        RenderTarget.FillRectangle(new SharpDX.RectangleF(left, y1, w, stripH), rb);
                    }

                    double dom;
                    if (dominanceAtBar.TryGetValue(bar, out dom))
                        DrawStripCell(left, y2, w, stripH, dom, 1.0f, baseBuy, baseSell);

                    if (ShowDepthHistoryStrip)
                    {
                        double book;
                        if (bookImbalanceAtBar.TryGetValue(bar, out book))
                        {
                            bool live = false;
                            bookFromLiveL2.TryGetValue(bar, out live);
                            DrawStripCell(left, y3, w, stripH, book, live ? 1.0f : 0.55f, baseBuy, baseSell);
                        }
                    }
                }
            }
            finally
            {
                regimeBuyerBrush.Opacity  = baseBuy;
                regimeSellerBrush.Opacity = baseSell;
                if (discB  != null) discB.Dispose();
                if (auctB  != null) auctB.Dispose();
                if (transB != null) transB.Dispose();
            }

            if (regimePanelFormat != null && regimePanelTextBrush != null)
            {
                RenderTarget.DrawText("MARKET  blue=Discovery  purple=Auction  grey=Transition", regimePanelFormat,
                    new SharpDX.RectangleF(ChartPanel.X + 6f, y1 - 1f, 520f, 12f), regimePanelTextBrush);
                RenderTarget.DrawText("IN CHARGE  green=Buyers  orange=Sellers", regimePanelFormat,
                    new SharpDX.RectangleF(ChartPanel.X + 6f, y2 - 1f, 520f, 12f), regimePanelTextBrush);
                if (ShowDepthHistoryStrip)
                    RenderTarget.DrawText("DEPTH  green=bid-heavy  orange=ask-heavy  (dim = proxy)", regimePanelFormat,
                        new SharpDX.RectangleF(ChartPanel.X + 6f, y3 - 1f, 520f, 12f), regimePanelTextBrush);
            }
        }

        private void DrawStripCell(float x, float y, float w, float h, double value, float srcAlpha,
                                   float baseBuy, float baseSell)
        {
            if (Math.Abs(value) < 0.005) return;
            double mag = Math.Min(1.0, Math.Abs(value) / 0.5);   // 0.5 = full brightness
            float a = (float)(0.35 + 0.65 * mag) * srcAlpha;
            SharpDX.Direct2D1.Brush b = value > 0 ? regimeBuyerBrush : regimeSellerBrush;
            b.Opacity = (value > 0 ? baseBuy : baseSell) * a;
            RenderTarget.FillRectangle(new SharpDX.RectangleF(x, y, w, h), b);
        }

        /// <summary>
        /// Renders historical regime background segments across visible bars
        /// plus the live status panel. When you scroll to a previous session
        /// you see the Discovery / Auction timeline and can read who was in charge.
        /// </summary>
        private void RenderRegimeOverlay(ChartControl chartControl, ChartScale chartScale)
        {
            if (!EnableRegimeDetection) return;
            if (ChartPanel == null || ChartBars == null) return;

            // ── Historical background segments ──────────────────────────────────
            // Paint contiguous runs of the same regime state as vertical bands
            // so the full timeline is visible when scrolling past sessions.
            if (ShowRegimeBackground && regimeStateAtBar != null && regimeStateAtBar.Count > 0)
            {
                int fromBar = ChartBars.FromIndex;
                int toBar   = ChartBars.ToIndex;
                if (fromBar < 0) fromBar = 0;
                if (toBar >= ChartBars.Count) toBar = ChartBars.Count - 1;

                int runStart = -1;
                STVPRegimeState runState = STVPRegimeState.Transition;

                for (int bar = fromBar; bar <= toBar + 1; bar++)
                {
                    STVPRegimeState state = STVPRegimeState.Transition;
                    bool has = (bar <= toBar) && regimeStateAtBar.TryGetValue(bar, out state);

                    if (!has || bar == toBar + 1 || state != runState)
                    {
                        // close previous run
                        if (runStart >= 0 && runStart <= toBar)
                        {
                            int runEnd = Math.Min(bar - 1, toBar);
                            if (runEnd >= runStart)
                            {
                                float x1 = chartControl.GetXByBarIndex(ChartBars, runStart);
                                float x2 = chartControl.GetXByBarIndex(ChartBars, runEnd);
                                // extend half a bar width on each side for continuous look
                                float half = (x2 - x1) / Math.Max(1, runEnd - runStart) * 0.5f;
                                if (runEnd == runStart) half = 4f;
                                float left  = x1 - half;
                                float right = x2 + half;
                                if (right > left)
                                {
                                    SharpDX.Direct2D1.Brush bg = null;
                                    switch (runState)
                                    {
                                        case STVPRegimeState.Discovery:  bg = regimeDiscoveryBgBrush;  break;
                                        case STVPRegimeState.Auction:    bg = regimeAuctionBgBrush;    break;
                                        default:                         bg = regimeTransitionBgBrush; break;
                                    }
                                    if (bg != null)
                                    {
                                        var rect = new SharpDX.RectangleF(
                                            left, ChartPanel.Y,
                                            right - left, ChartPanel.H);
                                        RenderTarget.FillRectangle(rect, bg);
                                    }
                                }
                            }
                        }

                        if (bar > toBar) break;
                        if (has)
                        {
                            runStart = bar;
                            runState = state;
                        }
                        else
                        {
                            runStart = -1;
                        }
                    }
                }
            }

            // ── Historical dominance + depth strips ─────────────────────────────
            if (ShowRegimeHistoryStrips)
                RenderRegimeHistoryStrips(chartControl);

            // ── Status panel (live / current values) ────────────────────────────
            if (!ShowRegimePanel || regimePanelFormat == null || regimePanelTextBrush == null)
                return;

            // ── Plain-English status panel ──────────────────────────────────────
            bool showDetail = ShowRegimeDetailLine;
            float panelW = 300f;
            float panelH = GetRegimePanelHeight();
            float panelX = RegimePanelPosition == STVPPanelPosition.TopRight
                ? RenderTarget.Size.Width - panelW - 10f
                : 10f;
            float panelY = 10f;
            if (ShowAnalyticsPanel && AnalyticsPanelPosition == RegimePanelPosition)
                panelY = 10f + 90f;   // sit below analytics panel

            if (regimePanelBgBrush != null)
                RenderTarget.FillRectangle(
                    new SharpDX.RectangleF(panelX, panelY, panelW, panelH), regimePanelBgBrush);

            string marketStr;
            switch (regimeState)
            {
                case STVPRegimeState.Discovery:
                    marketStr = "MARKET: PRICE DISCOVERY (one-sided move)"; break;
                case STVPRegimeState.Auction:
                    marketStr = "MARKET: AUCTION (two-sided, balanced)"; break;
                default:
                    marketStr = "MARKET: TRANSITION (between the two)"; break;
            }

            double ad = Math.Abs(dominance);
            string strength = ad >= 0.50 ? "strong" : (ad >= 0.25 ? "clear" : "slight");
            string controlStr;
            SharpDX.Direct2D1.Brush domBrush = regimePanelTextBrush;
            if (dominance >= 0.08)
            {
                controlStr = "IN CHARGE: BUYERS (" + strength + ")";
                domBrush = regimeBuyerBrush ?? regimePanelTextBrush;
            }
            else if (dominance <= -0.08)
            {
                controlStr = "IN CHARGE: SELLERS (" + strength + ")";
                domBrush = regimeSellerBrush ?? regimePanelTextBrush;
            }
            else
            {
                controlStr = "IN CHARGE: NEITHER (even)";
            }

            RenderTarget.DrawText(marketStr, regimePanelFormat,
                new SharpDX.RectangleF(panelX + 6f, panelY + 4f, panelW - 12f, 16f), regimePanelTextBrush);
            RenderTarget.DrawText(controlStr, regimePanelFormat,
                new SharpDX.RectangleF(panelX + 6f, panelY + 21f, panelW - 12f, 16f), domBrush);

            float lineY = panelY + 38f;
            if (false)
            {
                int nSig = 0, nBuy = 0;
                DateTime curKey = currentSession != null ? currentSession.SessionDate : DateTime.MinValue;
                lock (signalLock)
                {
                    for (int i = 0; i < signals.Count; i++)
                        if (signals[i].SessionKey == curKey) { nSig++; if (signals[i].Dir > 0) nBuy++; }
                }
                string sigLine = nSig > 0
                    ? "SIGNALS: " + nSig + " this session (" + nBuy + " buy / " + (nSig - nBuy) + " sell)"
                    : "SIGNALS: none yet (" + diagRaw + " setups seen)";
                RenderTarget.DrawText(sigLine, regimePanelFormat,
                    new SharpDX.RectangleF(panelX + 6f, lineY, panelW - 12f, 16f), regimePanelTextBrush);
                lineY += 17f;
            }

            if (showDetail)
            {
                string detail = string.Format("detail: score {0:+0.00;-0.00}  control {1:+0.00;-0.00}",
                    regimeScore, dominance);
                RenderTarget.DrawText(detail, regimePanelFormat,
                    new SharpDX.RectangleF(panelX + 6f, lineY, panelW - 12f, 14f), regimePanelTextBrush);
                string skip = "skipped: ctrl " + diagCtrl + " day " + diagDay + " stop " + diagRisk
                            + " confirm " + diagConf + " target " + diagNoTP;
                RenderTarget.DrawText(skip, regimePanelFormat,
                    new SharpDX.RectangleF(panelX + 6f, lineY + 14f, panelW - 12f, 14f), regimePanelTextBrush);
            }
        }

        #endregion

        // =========================================================
        //  CLUSTER ZONE DETECTION
        // =========================================================

        #region Cluster Zones

        /// <summary>
        /// Collects all active untested naked levels from prior completed sessions
        /// (both volume and TPO frameworks), sorts by price, groups using nearest-neighbor
        /// chaining within 60, scores each cluster, and returns only
        /// those meeting 2.
        /// </summary>
        /// <summary>
        /// Collects all price levels from all sessions, sorts by price, and groups using
        /// nearest-neighbor chaining within 60. A cluster forms wherever
        /// 2+ levels from any session(s) stack up at the same price zone.
        /// Score: +1 per level, +2 if both Vol and TPO frameworks, +1 per extra session beyond 1.
        /// </summary>
        private List<ClusterZone> BuildClusterZones()
        {
            // Cluster zones removed.
            return new List<ClusterZone>();
        }


        private void RenderClusterZones(ChartControl chartControl, ChartScale chartScale)
        {
            // Cluster zones removed.
            return;
        }


        #endregion

        // =========================================================
        //  ANALYTICS PANEL (Delta Z-Score / Vol Skewness / VA Migration / TPO Kurtosis)
        // =========================================================

        #region Analytics Panel

        /// <summary>
        /// Returns the Z-Score of the current session's running cumulative delta
        /// against the rolling history of prior session final deltas.
        /// Returns double.NaN if insufficient history.
        /// </summary>
        private double GetDeltaZScore()
        {
            if (sessionFinalDeltaHistory.Count < 3) return double.NaN;
            if (currentSession == null) return double.NaN;

            long[] vals = sessionFinalDeltaHistory.ToArray();  // Queue<long>.ToArray() → long[]
            double mean = 0;
            for (int i = 0; i < vals.Length; i++) mean += vals[i];
            mean /= vals.Length;

            double variance = 0;
            for (int i = 0; i < vals.Length; i++)
            {
                double d = vals[i] - mean;
                variance += d * d;
            }
            variance /= vals.Length;
            double std = Math.Sqrt(variance);

            if (std < 1) return double.NaN;

            // currentSession.FinalDelta is kept current each primary bar by ProcessPrimaryBar
            // via cumulativeDeltaIndicator.DeltaClose[0] (OrderFlow+ Session period).
            return (currentSession.FinalDelta - mean) / std;
        }

        /// <summary>
        /// Computes Pearson's volume-weighted skewness of a completed session's
        /// VolumeAtPrice distribution around its VPOC.
        /// Positive (right-skew) = most volume below VPOC → bullish lean (thin upper tail).
        /// Negative (left-skew)  = most volume above VPOC → bearish lean.
        /// </summary>
        private double ComputeVolumeSkewness(SessionProfile session)
        {
            if (session == null || session.VolumeAtPrice.Count < 3 || session.VPOCLevel == 0)
                return double.NaN;

            long totalVol = 0;
            double weightedSum = 0;
            foreach (var kvp in session.VolumeAtPrice)
            {
                totalVol     += kvp.Value;
                weightedSum  += (double)kvp.Key * kvp.Value;
            }
            if (totalVol == 0) return double.NaN;

            double mean = weightedSum / totalVol;

            double variance = 0, skewRaw = 0;
            foreach (var kvp in session.VolumeAtPrice)
            {
                double d = kvp.Key - mean;
                variance += d * d * kvp.Value;
                skewRaw  += d * d * d * kvp.Value;
            }
            variance /= totalVol;
            double std = Math.Sqrt(variance);
            if (std < 1e-9) return 0;

            return (skewRaw / totalVol) / (std * std * std);
        }

        /// <summary>
        /// Computes excess kurtosis of the developing current session's TPO distribution.
        /// Positive (leptokurtic) = price tightly clustered near POC → balanced day likely.
        /// Negative (platykurtic) = price distributed widely → trend day developing.
        /// </summary>
        private double ComputeTPOKurtosis(SessionProfile session)
        {
            if (session == null || session.TPOsAtPrice.Count < 4) return double.NaN;

            int totalTPOs = 0;
            double weightedSum = 0;
            foreach (var kvp in session.TPOsAtPrice)
            {
                int cnt = kvp.Value.Count;
                totalTPOs    += cnt;
                weightedSum  += (double)kvp.Key * cnt;
            }
            if (totalTPOs == 0) return double.NaN;

            double mean = weightedSum / totalTPOs;

            double variance = 0, kurtRaw = 0;
            foreach (var kvp in session.TPOsAtPrice)
            {
                double d = kvp.Key - mean;
                int    c = kvp.Value.Count;
                variance += d * d * c;
                kurtRaw  += d * d * d * d * c;
            }
            variance /= totalTPOs;
            double std = Math.Sqrt(variance);
            if (std < 1e-9) return 0;

            double kurtosis = (kurtRaw / totalTPOs) / (std * std * std * std);
            return kurtosis - 3.0;  // excess kurtosis (normal dist = 0)
        }

        /// <summary>
        /// Returns a regime label based on how the value area has migrated over the
        /// last <see cref="VAMigrationLookback"/> completed sessions.
        /// </summary>
        private string GetVAMigrationRegime()
        {
            // We need at least VAMigrationLookback+1 completed sessions for VAMigrationLookback comparisons
            var completed = new List<SessionProfile>();
            for (int i = 0; i < sessions.Count; i++)
                if (sessions[i] != currentSession && sessions[i].VVAH > 0 && sessions[i].VVAL > 0)
                    completed.Add(sessions[i]);

            int needed = VAMigrationLookback + 1;
            if (completed.Count < needed) return "Insufficient data";

            // Compare last VAMigrationLookback session pairs
            int upCount = 0, downCount = 0, overlapCount = 0;
            for (int i = completed.Count - VAMigrationLookback; i < completed.Count; i++)
            {
                var cur  = completed[i];
                var prev = completed[i - 1];

                double curVAHPrice  = cur.VVAH  * rowSize + rowSize;
                double curVALPrice  = cur.VVAL  * rowSize;
                double prevVAHPrice = prev.VVAH * rowSize + rowSize;
                double prevVALPrice = prev.VVAL * rowSize;

                if (curVALPrice > prevVAHPrice)          upCount++;
                else if (curVAHPrice < prevVALPrice)     downCount++;
                else                                     overlapCount++;
            }

            // Determine regime
            if (upCount == VAMigrationLookback)   return "Trending Up";
            if (downCount == VAMigrationLookback) return "Trending Down";
            if (overlapCount >= VAMigrationLookback - 1)
            {
                // Fully overlapping — check if VAs are narrowing (converging toward a cluster)
                var recent = completed[completed.Count - 1];
                var older  = completed[completed.Count - VAMigrationLookback - 1];
                double recentSpan = (recent.VVAH - recent.VVAL) * rowSize;
                double olderSpan  = (older.VVAH  - older.VVAL)  * rowSize;
                return recentSpan < olderSpan * 0.85 ? "Converging to Cluster" : "Balanced / Mean Rev";
            }
            return upCount > downCount ? "Bias Up" : downCount > upCount ? "Bias Down" : "Balanced / Mean Rev";
        }

        private void RenderAnalyticsPanel(ChartControl chartControl, ChartScale chartScale)
        {
            if (analyticsPanelFormat == null || analyticsPanelBrush == null) return;

            float lineH     = 18f;
            float panelW    = 230f;
            float lineCount = 0f;
            if (ShowDeltaZScore)    lineCount++;
            if (ShowVolumeSkewness) lineCount++;
            if (ShowVAMigration)    lineCount++;
            if (ShowTPOKurtosis)    lineCount++;
            if (lineCount == 0) return;

            float panelH = 6f + lineCount * lineH + 4f;
            float panelY = 10f;
            float panelX = AnalyticsPanelPosition == STVPPanelPosition.TopRight
                ? RenderTarget.Size.Width - panelW - 10f
                : 10f;

            // Semi-transparent background
            if (analyticsPanelBgBrush != null)
                RenderTarget.FillRectangle(new SharpDX.RectangleF(panelX, panelY, panelW, panelH), analyticsPanelBgBrush);

            float ty = panelY + 4f;

            // --- Delta Z-Score ---
            if (ShowDeltaZScore)
            {
                double z = GetDeltaZScore();
                string zLabel;
                if (double.IsNaN(z))
                    zLabel = "ΔZ: building...";
                else
                {
                    string dir = z >= DeltaZScoreThreshold ? " [BREAK↑]"
                               : z <= -DeltaZScoreThreshold ? " [BREAK↓]"
                               : Math.Abs(z) < 0.5 ? " [HOLD]"
                               : "";
                    zLabel = string.Format("ΔZ: {0:+0.00;-0.00;0.00}σ{1}", z, dir);
                }
                RenderTarget.DrawText(zLabel, analyticsPanelFormat,
                    new SharpDX.RectangleF(panelX + 4f, ty, panelW - 8f, lineH), analyticsPanelBrush);
                ty += lineH;
            }

            // --- Volume Skewness ---
            if (ShowVolumeSkewness)
            {
                SessionProfile prior = null;
                for (int i = sessions.Count - 1; i >= 0; i--)
                    if (sessions[i] != currentSession) { prior = sessions[i]; break; }

                double skew = ComputeVolumeSkewness(prior);
                string skewLabel;
                if (double.IsNaN(skew))
                    skewLabel = "Vol Skew: n/a";
                else
                {
                    string bias = skew >  0.3 ? " (Bull lean)"
                               : skew < -0.3 ? " (Bear lean)"
                               : " (Balanced)";
                    skewLabel = string.Format("Vol Skew: {0:+0.00;-0.00;0.00}{1}", skew, bias);
                }
                RenderTarget.DrawText(skewLabel, analyticsPanelFormat,
                    new SharpDX.RectangleF(panelX + 4f, ty, panelW - 8f, lineH), analyticsPanelBrush);
                ty += lineH;
            }

            // --- VA Migration Regime ---
            if (ShowVAMigration)
            {
                string regime = GetVAMigrationRegime();
                RenderTarget.DrawText("VA: " + regime, analyticsPanelFormat,
                    new SharpDX.RectangleF(panelX + 4f, ty, panelW - 8f, lineH), analyticsPanelBrush);
                ty += lineH;
            }

            // --- TPO Kurtosis ---
            if (ShowTPOKurtosis && currentSession != null)
            {
                double kurt = ComputeTPOKurtosis(currentSession);
                string kLabel;
                if (double.IsNaN(kurt))
                    kLabel = "TPO Kurt: building...";
                else
                {
                    string dayType = kurt >  1.0 ? " (Balanced day)"
                                  : kurt < -0.5 ? " (Trend day)"
                                  : " (Transition)";
                    kLabel = string.Format("TPO Kurt: {0:+0.00;-0.00;0.00}{1}", kurt, dayType);
                }
                RenderTarget.DrawText(kLabel, analyticsPanelFormat,
                    new SharpDX.RectangleF(panelX + 4f, ty, panelW - 8f, lineH), analyticsPanelBrush);
            }
        }

        #endregion

        // =========================================================
        //  TRADE SIGNALS
        // =========================================================

        // =========================================================
        //  IN CHARGE Clear/Strong transitions
        // =========================================================

        /// <summary>
        /// Panel strength: strong |dom|>=0.50, clear |dom|>=0.25, slight |dom|>=0.08.
        /// Clear or Strong buyers  => dom >= 0.25
        /// Clear or Strong sellers => dom <= -0.25
        /// </summary>
        /// <summary>Control level at which IN CHARGE arms: Slight (0.08, same as the panel) or the Clear/Strong Gate.</summary>
        private double ICArmThreshold()
        {
            // IC entry properties removed; fixed Clear threshold for any residual control logging.
            return 0.35;
        }

        private int ClearStrongSide(double dom)
        {
            double thr = ICArmThreshold();
            if (dom >= thr) return 1;
            if (dom <= -thr) return -1;
            return 0;
        }

        /// <summary>
        /// ClearStrongSide with hysteresis. A side arms at +/-0.35; once armed it stays
        /// armed until dominance drops below 0.15 on that side. Crossing to the opposite
        /// Clear gate flips directly.
        /// </summary>
        private int ResolveClearStrongSide(double dom, int current)
        {
            int enter = ClearStrongSide(dom);
            if (enter != 0) return enter;
            double release = Math.Min(0.15, ICArmThreshold());
            if (current > 0 && dom >= release) return 1;
            if (current < 0 && dom <= -release) return -1;
            return 0;
        }

        private DateTime IcNow()
        {
            return lastTickTimeSeen != DateTime.MinValue ? lastTickTimeSeen : DateTime.Now;
        }

        /// <summary>
        /// Market/session time for L2 walls.
        /// Prefer the depth event's own time, then last depth time, then last tick / bar time.
        /// NEVER fall back to wall-clock when a market time is available — mixing clocks
        /// breaks Wall Persist and stale cleanup under Market Replay (and fast playback).
        /// </summary>
        private DateTime GetMarketDepthTime(MarketDepthEventArgs e)
        {
            try
            {
                if (e != null && e.Time.Year > 2000)
                    return e.Time;
            }
            catch { }
            if (lastDepthMarketTime.Year > 2000)
                return lastDepthMarketTime;
            DateTime chart = GetChartNow();
            if (chart != DateTime.MinValue && chart.Year > 2000)
                return chart;
            // Last resort only (no depth/ticks yet)
            return DateTime.Now;
        }

        /// <summary>
        /// Best available market time for wall age / persist / freshness checks.
        /// Uses the more recent of last depth vs last tick so sparse ticks do not
        /// mark a live book "stale" and so depth alone still advances persist timers in replay.
        /// </summary>
        private DateTime GetMarketNow()
        {
            DateTime depth = lastDepthMarketTime;
            DateTime tick  = lastTickTimeSeen;
            if (depth.Year > 2000 && tick.Year > 2000)
                return depth >= tick ? depth : tick;
            if (depth.Year > 2000) return depth;
            if (tick.Year > 2000) return tick;
            try { if (Times != null && Times[0].Count > 0) return Times[0][0]; } catch { }
            try
            {
                if (BarsArray != null && BarsArray[0] != null && CurrentBars[0] >= 0)
                    return BarsArray[0].GetTime(CurrentBars[0]);
            }
            catch { }
            return DateTime.Now;
        }

        private double IcPrice(int barIdx)
        {
            try
            {
                if (BarsArray != null && BarsArray[0] != null && barIdx >= 0 && barIdx < BarsArray[0].Count)
                    return BarsArray[0].GetClose(barIdx);
            }
            catch { }
            return 0;
        }

        private double IcDriftTicks(int side, double price)
        {
            if (TickSize <= 0 || price <= 0 || icPendingArmPrice <= 0) return 0;
            return (price - icPendingArmPrice) * side / TickSize;   // + = moved in the signal's favour
        }

        private void IcCancelPending(string reason, double dom, double price)
        {
            int side = icPendingSide;
            double secs = (IcNow() - icPendingStart).TotalSeconds;
            double drift = IcDriftTicks(side, price);
            icPendingSide = 0; icPendingBar = -1; icPendingStart = DateTime.MinValue;
            lastClearStrongSide = 0;   // allow a fresh arm from +/-0.35
            Print("SessionTPOVolumeProfile IC CANCEL " + (side > 0 ? "BUY" : "SELL")
                + " after " + secs.ToString("0.0") + "s (" + reason + ")  control=" + dom.ToString("+0.00;-0.00")
                + "  drift=" + drift.ToString("+0.0;-0.0;0.0") + " ticks");
        }



        /// <summary>
        /// Convert a session level index to the price used for level lines / signals.
        /// TicksPerRow = 1: exact tick price (aligned with SuperDOM). Multi-tick: mid of bucket.
        /// </summary>
        private double LevelIndexToPrice(int level)
        {
            if (rowSize <= 0 || level == 0) return 0;
            double lo = LevelToPrice(level);
            return (TicksPerRow > 1) ? (lo + rowSize * 0.5) : lo;
        }

        /// <summary>
        /// True if price is within maxTicks of levelPrice.
        /// </summary>
        private bool PriceNearLevel(double price, double levelPrice, int maxTicks)
        {
            if (price <= 0 || levelPrice <= 0 || TickSize <= 0 || maxTicks <= 0) return false;
            return Math.Abs(price - levelPrice) / TickSize <= maxTicks + 1e-9;
        }

        /// <summary>
        /// Collect current-session HVN or LVN mid-row prices (same thresholds as VbP render).
        /// </summary>
        private void CollectHvnLvnPrices(SessionProfile session, bool wantHvn, List<double> into)
        {
            if (session == null || session.VolumeAtPrice == null || session.VolumeAtPrice.Count == 0) return;
            long total = 0;
            int n = 0;
            foreach (var kv in session.VolumeAtPrice)
            {
                if (kv.Value <= 0) continue;
                total += kv.Value;
                n++;
            }
            if (n == 0) return;
            double avg = (double)total / n;
            double hvnThr = avg * 1.3;
            double lvnThr = avg * 0.7;
            foreach (var kv in session.VolumeAtPrice)
            {
                int lvl = kv.Key;
                long vol = kv.Value;
                if (vol <= 0) continue;
                if (lvl == session.VPOCLevel) continue;
                bool isHvn = vol >= hvnThr;
                bool isLvn = vol <= lvnThr;
                if (wantHvn && isHvn)
                {
                    double p = LevelIndexToPrice(lvl);
                    if (p > 0) into.Add(p);
                }
                else if (!wantHvn && isLvn)
                {
                    double p = LevelIndexToPrice(lvl);
                    if (p > 0) into.Add(p);
                }
            }
        }

        /// <summary>
        /// Add a named level if near price. tag is stored in candidates when matched.
        /// </summary>
        private void TryAddNear(double price, double levelPrice, int maxTicks, string tag, List<string> candidates)
        {
            if (levelPrice <= 0) return;
            if (PriceNearLevel(price, levelPrice, maxTicks) && !candidates.Contains(tag))
                candidates.Add(tag);
        }

        private void TryAddNearLevelIdx(double price, int levelIdx, int maxTicks, string tag, List<string> candidates)
        {
            if (levelIdx == 0) return;
            TryAddNear(price, LevelIndexToPrice(levelIdx), maxTicks, tag, candidates);
        }

        /// <summary>
        /// Whether a prior-session level is still in play for confluence:
        /// previous completed day always counts; older sessions only if still naked (untested).
        /// </summary>
        private bool PriorLevelInPlay(SessionProfile session, DateTime breakTime)
        {
            if (session == null) return false;
            if (session == mostRecentCompletedSession) return true; // prior day always
            return breakTime == DateTime.MinValue;                 // older: naked only
        }

        /// <summary>
        /// Collect TPO + VPOC structure tags near price from one session.
        /// includeSd2: TPO SD2H/L. includeNodes: HVN/LVN (current session only typically).
        /// Directional: side > 0 long (VAL/SD2L/LVN), side < 0 short (VAH/SD2H/HVN); POC/VPOC always.
        /// </summary>
        private void CollectSessionStructureNear(SessionProfile session, int side, double price, int maxTicks,
            bool includeSd2, bool includeNodes, string prefix, List<string> candidates)
        {
            if (session == null) return;

            // Shared magnets
            TryAddNearLevelIdx(price, session.POCLevel, maxTicks, prefix + "TPO-POC", candidates);
            TryAddNearLevelIdx(price, session.VPOCLevel, maxTicks, prefix + "VPOC", candidates);

            if (side > 0)
            {
                TryAddNearLevelIdx(price, session.TPOVAL, maxTicks, prefix + "TPO-VAL", candidates);
                if (includeSd2)
                    TryAddNearLevelIdx(price, session.TPOSD2L, maxTicks, prefix + "TPO-SD2L", candidates);
                if (includeNodes)
                {
                    var lvns = new List<double>();
                    CollectHvnLvnPrices(session, wantHvn: false, lvns);
                    foreach (double p in lvns)
                    {
                        if (PriceNearLevel(price, p, maxTicks))
                        {
                            string tag = prefix + "LVN@" + p.ToString("F2");
                            if (!candidates.Contains(tag)) candidates.Add(tag);
                            break;
                        }
                    }
                }
            }
            else if (side < 0)
            {
                TryAddNearLevelIdx(price, session.TPOVAH, maxTicks, prefix + "TPO-VAH", candidates);
                if (includeSd2)
                    TryAddNearLevelIdx(price, session.TPOSD2H, maxTicks, prefix + "TPO-SD2H", candidates);
                if (includeNodes)
                {
                    var hvns = new List<double>();
                    CollectHvnLvnPrices(session, wantHvn: true, hvns);
                    foreach (double p in hvns)
                    {
                        if (PriceNearLevel(price, p, maxTicks))
                        {
                            string tag = prefix + "HVN@" + p.ToString("F2");
                            if (!candidates.Contains(tag)) candidates.Add(tag);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// IC structure confluence:
        /// Current session: TPO POC / VAH / VAL / SD2H / SD2L + VPOC + HVN/LVN.
        /// Prior day (most recent completed): same TPO levels + VPOC (always).
        /// Older sessions: same levels only while still naked (untested).
        /// Volume VVAH/VVAL and SD3 are NOT used.
        /// </summary>


        // =========================================================
        //  Imbalance zones as primary entry (auction acceptance)
        // =========================================================

        private class ImbalanceZone
        {
            public int    Side;      // +1 buy zone, -1 sell zone
            public int    LowLevel;
            public int    HighLevel;
            public bool   Strong;    // acceptance / confirmed
            public double LowPrice;
            public double HighPrice;
            public string Tag;
        }

        private DateTime lastIzEval = DateTime.MinValue;
        private DateTime lastIzFireBuy = DateTime.MinValue;
        private DateTime lastIzFireSell = DateTime.MinValue;
        // Prior/naked VA rejection: at most once per level per primary bar (2000-tick etc.)
        private int vaFiredBarIndex = -1;
        private HashSet<string> vaFiredThisBar = new HashSet<string>();
        private string   lastIzKey = "";
        private DateTime lastDayTypeBlockLog = DateTime.MinValue;
        private string   lastDayTypeBlockMsg = "";

        /// <summary>
        /// Build stacked imbalance zones from session bid/ask-at-price (same rules as chart paint).
        /// Buy zone = ask-owned levels; Sell zone = bid-owned levels. Strong = HVN/VPOC body or diag confirm.
        /// </summary>
        private List<ImbalanceZone> BuildImbalanceZones(SessionProfile session)
        {
            var result = new List<ImbalanceZone>();
            if (session == null || session.VolumeAtPrice == null || session.VolumeAtPrice.Count == 0)
                return result;
            if (rowSize <= 0) return result;

            long horizFloor = Math.Max(1L, (long)ZoneHorizontalControlFloor);

            // HVN threshold from average volume
            double sum = 0; int cnt = 0;
            foreach (var kv in session.VolumeAtPrice) { sum += kv.Value; cnt++; }
            double avg = cnt > 0 ? sum / cnt : 0;
            double hvnThreshold = avg * 1.3;

            var levels = new List<int>(session.VolumeAtPrice.Keys);
            levels.Sort();

            var horizSign = new Dictionary<int, int>();
            var isAccept  = new Dictionary<int, bool>();
            var diagSign  = new Dictionary<int, int>();

            foreach (int lv in levels)
            {
                long ask  = session.BuyVolumeAtPrice != null && session.BuyVolumeAtPrice.ContainsKey(lv)  ? session.BuyVolumeAtPrice[lv]  : 0;
                long bid  = session.SellVolumeAtPrice != null && session.SellVolumeAtPrice.ContainsKey(lv) ? session.SellVolumeAtPrice[lv] : 0;
                long vol  = session.VolumeAtPrice[lv];
                long bBel = session.SellVolumeAtPrice != null && session.SellVolumeAtPrice.ContainsKey(lv - 1) ? session.SellVolumeAtPrice[lv - 1] : 0;
                long aAbv = session.BuyVolumeAtPrice != null && session.BuyVolumeAtPrice.ContainsKey(lv + 1)  ? session.BuyVolumeAtPrice[lv + 1]  : 0;

                long hEx = ask - bid;
                if (hEx >= horizFloor)       horizSign[lv] =  1;
                else if (-hEx >= horizFloor) horizSign[lv] = -1;
                else                         horizSign[lv] =  0;

                isAccept[lv] = (lv == session.VPOCLevel) || (vol >= hvnThreshold);

                // Zone confirmation always uses diagonal initiative (horizontal ownership is separate)
                long buyOpp  = bBel;
                long sellOpp = aAbv;
                bool zBuy  = IsDiagonalImbalance(ask, buyOpp);
                bool zSell = IsDiagonalImbalance(bid, sellOpp);
                double rBuy  = DiagonalRatio(ask, buyOpp);
                double rSell = DiagonalRatio(bid, sellOpp);
                if (zBuy && (!zSell || rBuy > rSell))        diagSign[lv] =  1;
                else if (zSell && (!zBuy || rSell > rBuy))   diagSign[lv] = -1;
                else                                         diagSign[lv] =  0;
            }

            int minSpan = Math.Max(1, MinLevelsForImbalanceZone);
            var claimed = new HashSet<int>();

            System.Action<int, int, int, bool, string> addZone = (lo, hi, side, strong, tag) =>
            {
                if (hi - lo + 1 < minSpan) return;
                for (int lv = lo; lv <= hi; lv++) claimed.Add(lv);
                result.Add(new ImbalanceZone
                {
                    Side = side,
                    LowLevel = lo,
                    HighLevel = hi,
                    Strong = strong,
                    LowPrice = lo * rowSize,
                    HighPrice = (hi + 1) * rowSize,
                    Tag = tag
                });
            };

            // Pass 1: acceptance (HVN/VPOC) contiguous runs
            {
                int i = 0;
                while (i < levels.Count)
                {
                    int lv = levels[i];
                    if (!isAccept.ContainsKey(lv) || !isAccept[lv]) { i++; continue; }
                    int lo = lv, hi = lv;
                    long net = 0;
                    int j = i;
                    while (j < levels.Count && isAccept.ContainsKey(levels[j]) && isAccept[levels[j]])
                    {
                        int lj = levels[j];
                        // require contiguous levels
                        if (j > i && levels[j] != levels[j - 1] + 1) break;
                        hi = lj;
                        long ask = session.BuyVolumeAtPrice != null && session.BuyVolumeAtPrice.ContainsKey(lj) ? session.BuyVolumeAtPrice[lj] : 0;
                        long bid = session.SellVolumeAtPrice != null && session.SellVolumeAtPrice.ContainsKey(lj) ? session.SellVolumeAtPrice[lj] : 0;
                        net += (ask - bid);
                        j++;
                    }
                    int side = net >= 0 ? 1 : -1;
                    if (Math.Abs(net) >= horizFloor)
                        addZone(lo, hi, side, true, side > 0 ? "IZ-BUY-accept" : "IZ-SELL-accept");
                    i = Math.Max(j, i + 1);
                }
            }

            // Pass 2: transition — contiguous same horizSign not claimed
            {
                int i = 0;
                while (i < levels.Count)
                {
                    int lv = levels[i];
                    int hs = horizSign.ContainsKey(lv) ? horizSign[lv] : 0;
                    if (hs == 0 || claimed.Contains(lv)) { i++; continue; }
                    int lo = lv, hi = lv, side = hs;
                    int j = i;
                    while (j < levels.Count)
                    {
                        int lj = levels[j];
                        if (j > i && lj != levels[j - 1] + 1) break;
                        int hsj = horizSign.ContainsKey(lj) ? horizSign[lj] : 0;
                        if (hsj != side || claimed.Contains(lj)) break;
                        hi = lj;
                        j++;
                    }
                    if (hi - lo + 1 >= minSpan)
                    {
                        int confirm = 0, rows = 0;
                        for (int lv2 = lo; lv2 <= hi; lv2++)
                        {
                            rows++;
                            int ds = diagSign.ContainsKey(lv2) ? diagSign[lv2] : 0;
                            if (ds == side) confirm++;
                        }
                        bool strong = rows > 0 && confirm * 2 >= rows;
                        addZone(lo, hi, side, strong, side > 0 ? "IZ-BUY-trans" : "IZ-SELL-trans");
                    }
                    i = Math.Max(j, i + 1);
                }
            }

            return result;
        }

        private List<ImbalanceZone> GetActiveImbalanceZones()
        {
            var all = new List<ImbalanceZone>();
            // Current session always (zones form during IB; entries still respect day-type / optional post-IB trade window)
            if (currentSession != null)
                all.AddRange(BuildImbalanceZones(currentSession));
            // Prior session zones (established auction)
            if (mostRecentCompletedSession != null && mostRecentCompletedSession != currentSession)
            {
                foreach (var z in BuildImbalanceZones(mostRecentCompletedSession))
                {
                    z.Tag = "p" + z.Tag;
                    all.Add(z);
                }
            }
            return all;
        }

        /// <summary>
        /// Primary entry: trade WITH an imbalance zone (acceptance), not against it.
        /// Long: in/near buy zone or reclaim above sell zone high. Short: mirror.
        /// </summary>

        /// <summary>
        /// Imbalance LEVELS = discrete prices with diagonal bid/ask imbalance
        /// (same short red/green marks beside the profile — not Initial Balance, not zones).
        /// dir &gt; 0 → next buy-imbalance level above price; dir &lt; 0 → next sell-imbalance below.
        /// </summary>

        /// <summary>
        /// Diagonal imbalance test: the dominant cell must be at least MinImbalanceVolume
        /// and at least DiagonalImbalanceRatio x the opposing diagonal cell.
        /// A zero opposing cell is treated as 1 so a lone print can still qualify
        /// (MinImbalanceVolume keeps tiny prints from doing so).
        /// </summary>
        private bool IsDiagonalImbalance(long dominant, long opposing)
        {
            if (dominant < MinImbalanceVolume || dominant <= 0) return false;
            return (double)dominant >= DiagonalImbalanceRatio * (double)Math.Max(1L, opposing);
        }

        /// <summary>
        /// Horizontal (same-level) imbalance: dominant vs opposite at the same price using
        /// HorizontalImbalanceRatio. Used to color footprint buy/sell numbers.
        /// </summary>
        private bool IsHorizontalImbalance(long dominant, long opposing)
        {
            if (dominant < MinImbalanceVolume || dominant <= 0) return false;
            return (double)dominant >= HorizontalImbalanceRatio * (double)Math.Max(1L, opposing);
        }

        /// <summary>
        /// Profile / zone imbalance test. Always diagonal initiative for confirmation
        /// (horizontal ownership is handled separately via ZoneHorizontalControlFloor).
        /// Caller must supply the correct opposing cell for diagonal.
        /// </summary>
        private bool IsProfileImbalance(long dominant, long opposing)
        {
            return IsDiagonalImbalance(dominant, opposing);
        }

        /// <summary>Dominant / opposing ratio (opposing floored at 1) — used to pick the stronger side.</summary>
        private double DiagonalRatio(long dominant, long opposing)
        {
            return (double)dominant / (double)Math.Max(1L, opposing);
        }

        /// <summary>
        /// Discrete diagonal imbalance LEVELS — same short red/green marks on the profile.
        /// Buying level: Ask[lv] >= Multiple x Bid[lv-1] (green).
        /// Selling level: Bid[lv] >= Multiple x Ask[lv+1] (red).
        /// These are LEVELS, not stacked zones.
        /// </summary>
        private void CollectImbalanceLevels(out List<double> buyLevels, out List<double> sellLevels)
        {
            buyLevels = new List<double>();
            sellLevels = new List<double>();
            if (currentSession == null || currentSession.VolumeAtPrice == null || TickSize <= 0)
                return;

            var levels = new List<int>(currentSession.VolumeAtPrice.Keys);
            levels.Sort();

            for (int i = 0; i < levels.Count; i++)
            {
                int lv = levels[i];
                double px = Instrument.MasterInstrument.RoundToTickSize(lv * rowSize);

                long ask = 0, bid = 0, bidBelow = 0, askAbove = 0;
                if (currentSession.BuyVolumeAtPrice != null)
                {
                    currentSession.BuyVolumeAtPrice.TryGetValue(lv, out ask);
                    currentSession.BuyVolumeAtPrice.TryGetValue(lv + 1, out askAbove);
                }
                if (currentSession.SellVolumeAtPrice != null)
                {
                    currentSession.SellVolumeAtPrice.TryGetValue(lv, out bid);
                    currentSession.SellVolumeAtPrice.TryGetValue(lv - 1, out bidBelow);
                }

                // buying imbalance at this ask cell / selling imbalance at this bid cell
                if (IsDiagonalImbalance(ask, bidBelow))
                    buyLevels.Add(px);
                if (IsDiagonalImbalance(bid, askAbove))
                    sellLevels.Add(px);
            }
        }

        private bool FindNearestImbalanceLevel(List<double> levels, double price, int preferDir, double maxDistTicks, out double level)
        {
            level = 0;
            if (levels == null || levels.Count == 0 || TickSize <= 0) return false;
            double maxD = maxDistTicks * TickSize;
            double best = 0; double bestDist = double.MaxValue;
            foreach (double lv in levels)
            {
                double d = lv - price;
                if (preferDir > 0 && d < -TickSize) continue; // want level at or above
                if (preferDir < 0 && d > TickSize) continue;  // want level at or below
                double ad = Math.Abs(d);
                if (ad <= maxD && ad < bestDist)
                {
                    bestDist = ad;
                    best = lv;
                }
            }
            // Also allow nearest in either direction if preferDir == 0
            if (preferDir == 0)
            {
                foreach (double lv in levels)
                {
                    double ad = Math.Abs(lv - price);
                    if (ad <= maxD && ad < bestDist)
                    {
                        bestDist = ad;
                        best = lv;
                    }
                }
            }
            if (best > 0)
            {
                level = best;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Opposing imbalance LEVEL inside the planned risk window?
        /// Short: any buy-level between price and stopBelow.
        /// Long: any sell-level between price and stopAbove.
        /// </summary>
        private bool HasOpposingImbalanceInRisk(int dir, double price, double stop, out double oppLevel, out string detail)
        {
            oppLevel = 0; detail = "";
            List<double> buys, sells;
            CollectImbalanceLevels(out buys, out sells);
            var opp = dir > 0 ? sells : buys; // long fears sell levels above; short fears buy levels below
            if (opp == null || opp.Count == 0) return false;

            double lo = Math.Min(price, stop);
            double hi = Math.Max(price, stop);
            // expand 1 tick so boundary counts
            lo -= TickSize; hi += TickSize;

            double nearest = 0; double nearestDist = double.MaxValue;
            foreach (double lv in opp)
            {
                if (lv >= lo && lv <= hi)
                {
                    double d = Math.Abs(lv - price);
                    if (d < nearestDist)
                    {
                        nearestDist = d;
                        nearest = lv;
                    }
                }
            }
            if (nearest > 0)
            {
                oppLevel = nearest;
                detail = (dir > 0 ? "sell-level@" : "buy-level@") + nearest.ToString("F2") + " inside risk";
                return true;
            }
            return false;
        }

        private bool FindNextImbalanceLevel(int dir, double price, out double level, out string tag)
        {
            level = 0; tag = "";
            List<double> buys, sells;
            CollectImbalanceLevels(out buys, out sells);
            // In trade direction: long → next buy level above (continuation support becomes magnets? actually next sell for target resistance)
            // Targets: long wants next SELL imbalance above (offer to hit); short wants next BUY imbalance below
            var list = dir > 0 ? sells : buys;
            string kind = dir > 0 ? "imbSELL@" : "imbBUY@";
            if (list == null || list.Count == 0)
            {
                // fallback same-side
                list = dir > 0 ? buys : sells;
                kind = dir > 0 ? "imbBUY@" : "imbSELL@";
            }
            double best = 0; double bestDist = double.MaxValue;
            foreach (double lv in list)
            {
                double d = dir > 0 ? (lv - price) : (price - lv);
                if (d >= TickSize && d < bestDist)
                {
                    bestDist = d;
                    best = lv;
                }
            }
            if (best > 0)
            {
                level = best;
                tag = kind + level.ToString("F2");
                return true;
            }
            return false;
        }

        /// <summary>
        /// PRIMARY path: enter FROM imbalance LEVELS (short red/green marks), not zones.
        /// Long at buy-level with buy control; short at sell-level with sell control.
        /// Stop beyond nearest opposing level (or min floor). Never enter if opposing level is inside risk.
        /// </summary>

        /// <summary>
        /// Prior-day Value Area rejection (works NQ / YM / ES via TickSize).
        /// Long: test of prior VAL + buy imbalance level near price + control up.
        /// Short: test of prior VAH + sell imbalance level near price + control down.
        /// No new entries while price is strictly inside prior value (between VAL and VAH).
        /// </summary>

        /// <summary>
        /// Fixed-tick stop and target only (user Stop-Loss Ticks / Take-Profit Ticks).
        /// No ATR, no structure, no MinAuctionRisk.
        /// </summary>
        private bool TryFixedTickStopTarget(int dir, double price, out double stop, out double tgt, out string detail)
        {
            stop = 0; tgt = 0; detail = "";
            if (dir == 0 || price <= 0 || TickSize <= 0) return false;

            int sl = Math.Max(1, StopLossTicks);
            int tp = Math.Max(1, TakeProfitTicks);
            stop = Instrument.MasterInstrument.RoundToTickSize(price - dir * sl * TickSize);
            tgt  = Instrument.MasterInstrument.RoundToTickSize(price + dir * tp * TickSize);
            detail = "fixed ticks stop=" + sl + "t tgt=" + tp + "t";
            return true;
        }



        private bool TryGetCurrentTpoVA(out double vah, out double val, out double poc)
        {
            vah = 0; val = 0; poc = 0;
            if (currentSession == null || rowSize <= 0) return false;
            if (currentSession.TPOVAH > 0)
                vah = Instrument.MasterInstrument.RoundToTickSize(currentSession.TPOVAH * rowSize + rowSize);
            if (currentSession.TPOVAL > 0)
                val = Instrument.MasterInstrument.RoundToTickSize(currentSession.TPOVAL * rowSize);
            if (currentSession.POCLevel > 0)
                poc = Instrument.MasterInstrument.RoundToTickSize(currentSession.POCLevel * rowSize + rowSize * 0.5);
            return vah > 0 && val > 0 && vah > val;
        }

        /// <summary>
        /// Mean-reversion at current session TPO value edges (after IB).
        /// Buy at/below VAL with buy imbalance or L2 support; sell at/above VAH with sell imbalance or L2 offer.
        /// Target: TPO POC or opposite VA edge (often >20 ticks). Stop beyond the edge + buffer.
        /// </summary>
        private void MrBlockPrint(string msg)
        {
            DateTime n = DateTime.Now;
            if (msg == lastMrBlockKey && (n - lastMrBlockPrint).TotalSeconds < 2.0)
                return;
            lastMrBlockKey = msg;
            lastMrBlockPrint = n;
            Print(msg);
        }

        private void EvaluateMeanRevertVAEdge()
        {
            if (!EnableMeanRevertVAEdge) return;
            if (State != State.Realtime) return;
            if (currentSession == null || TickSize <= 0 || rowSize <= 0) return;

            // Throttle ~5 Hz
            DateTime now = DateTime.Now;
            if ((now - lastMrEvalTime).TotalMilliseconds < 200)
                return;
            lastMrEvalTime = now;

            // Start gate: RTH Start + Auto-Trade Start After RTH (minutes) — NOT IB Duration
            {
                string startReason;
                if (IsBeforeAutoTradeStart(out startReason))
                {
                    if (lastMrBlockKey != "wait-start")
                    {
                        lastMrBlockKey = "wait-start";
                        Print("SessionTPOVolumeProfile MR wait — " + startReason);
                    }
                    return;
                }
            }

            double price = (fbBar == CurrentBars[0] && fbLast > 0) ? fbLast : 0;
            if (price <= 0)
            {
                try { price = Close[0]; } catch { return; }
            }
            if (price <= 0) return;

            // Current session DEVELOPING TPO VAH / VAL (recalculated as the day builds)
            if (currentSession.TPOVAH <= 0 || currentSession.TPOVAL <= 0)
            {
                if (lastMrBlockKey != "no-va")
                {
                    lastMrBlockKey = "no-va";
                    Print("SessionTPOVolumeProfile MR wait developing TPO VA not ready yet");
                }
                return;
            }

            double vah = Instrument.MasterInstrument.RoundToTickSize(currentSession.TPOVAH * rowSize + rowSize);
            double val = Instrument.MasterInstrument.RoundToTickSize(currentSession.TPOVAL * rowSize);
            double poc = 0;
            if (currentSession.POCLevel > 0)
                poc = Instrument.MasterInstrument.RoundToTickSize(currentSession.POCLevel * rowSize + rowSize * 0.5);

            if (vah <= val)
                return;

            double prox = Math.Max(1, MRVAEdgeProximityTicks) * TickSize;

            // Rejection only: long at developing VAL, short at developing VAH
            int dir = 0;
            double edge = 0;
            string edgeName = "";
            if (price <= val + prox)
            {
                dir = 1;
                edge = val;
                edgeName = "tVAL=" + val.ToString("F2");
            }
            else if (price >= vah - prox)
            {
                dir = -1;
                edge = vah;
                edgeName = "tVAH=" + vah.ToString("F2");
            }
            else
                return; // mid-value — silent

            // Control/dominance is NOT used for MR (IC path removed).

            long barAsk = 0, barBid = 0;
            int barIdxCd = CurrentBars[0];
            if (primaryBarAskVol != null) primaryBarAskVol.TryGetValue(barIdxCd, out barAsk);
            if (primaryBarBidVol != null) primaryBarBidVol.TryGetValue(barIdxCd, out barBid);
            long barCumDelta = barAsk - barBid;
            long sessCumDelta = 0;
            try
            {
                if (currentSession != null)
                    sessCumDelta = currentSession.FinalDelta;
            }
            catch { }

            if (MRRequireCumDeltaConfirm)
            {
                long need = Math.Max(0, MRCumDeltaMinContracts);
                if (dir > 0 && barCumDelta < need)
                {
                    MrBlockPrint("SessionTPOVolumeProfile MR BLOCK BUY " + edgeName
                        + " px=" + price.ToString("F2")
                        + " barΔ=" + barCumDelta.ToString("+0;-0;0")
                        + " cum-delta not confirmed");
                    return;
                }
                if (dir < 0 && barCumDelta > -need)
                {
                    MrBlockPrint("SessionTPOVolumeProfile MR BLOCK SELL " + edgeName
                        + " px=" + price.ToString("F2")
                        + " barΔ=" + barCumDelta.ToString("+0;-0;0")
                        + " cum-delta not confirmed");
                    return;
                }
            }

            string conf = "source=TPO_VA_REJECT developing";
            // Optional L2 wall note only (NOT required for entry)
            try
            {
                int needSide = dir > 0 ? 1 : -1;
                double wallProx = Math.Max(1, MRWallProximityTicks) * TickSize;
                lock (wallLock)
                {
                    if (activeWalls != null)
                    {
                        for (int wi = 0; wi < activeWalls.Count; wi++)
                        {
                            var w = activeWalls[wi];
                            if (w == null || w.Side != needSide) continue;
                            if (needSide > 0 && w.Price > price + TickSize * 0.5) continue;
                            if (needSide < 0 && w.Price < price - TickSize * 0.5) continue;
                            double d = Math.Abs(w.Price - price);
                            if (d <= wallProx)
                            {
                                conf += " L2 " + (needSide > 0 ? "bid@" : "ask@")
                                    + w.Price.ToString("F2") + " sz=" + Math.Max(w.Size, w.PeakSize);
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            string winReason;
            if (IsAutoTradeEntryWindowClosed(out winReason))
            {
                MrBlockPrint("SessionTPOVolumeProfile MR BLOCK " + (dir > 0 ? "BUY " : "SELL ") + edgeName
                    + " window closed: " + winReason);
                return;
            }

            if (EnableAutoTrade && AutoTradeOnlyWhenFlat)
            {
                if (ResolveLivePositionDir() != 0)
                {
                    MrBlockPrint("SessionTPOVolumeProfile MR BLOCK " + (dir > 0 ? "BUY " : "SELL ") + edgeName
                        + " already in position");
                    return;
                }
            }

            string key = "MR|" + dir + "|" + ((int)(edge / TickSize));
            if (MROneSignalPerEdge && signalKeys.Contains(key))
                return;

            // Cooldown shared with other signal fires
            DateTime last = dir > 0 ? lastIzFireBuy : lastIzFireSell;
            if (last != DateTime.MinValue && (now - last).TotalSeconds < 15)
                return;

            double stop, tgt;
            string riskDetail;
            if (!TryFixedTickStopTarget(dir, price, out stop, out tgt, out riskDetail))
            {
                Print("SessionTPOVolumeProfile fixed-tick stop/target failed");
                return;
            }


            if (MROneSignalPerEdge)
                signalKeys.Add(key);
            if (dir > 0) lastIzFireBuy = now; else lastIzFireSell = now;

            Print("SessionTPOVolumeProfile MR FIRE " + (dir > 0 ? "BUY" : "SELL")
                + " @ " + price.ToString("F2")
                + "  " + edgeName
                + "  " + conf
                + "  barΔ=" + barCumDelta.ToString("+0;-0;0")
                + "  sessΔ=" + sessCumDelta.ToString("+0;-0;0")
                + "  " + riskDetail);

            EmitImbalanceZoneTrade(CurrentBars[0], dir, price, stop, tgt, "MR " + edgeName + " " + conf);
        }




        private struct VARejectCand
        {
            public double Price;
            public string Name;
            public bool   IsSupport; // true = long/support, false = short/resistance
        }

        /// <summary>
        /// Prior-session VA levels always; optional naked levels and highs/lows across lookback sessions.
        /// </summary>
        private List<VARejectCand> CollectVARejectionCandidates(SessionProfile prior)
        {
            var list = new List<VARejectCand>();
            if (sessions == null || rowSize <= 0 || TickSize <= 0) return list;

            System.Action<double, string, bool> add = (px, name, support) =>
            {
                if (px <= 0 || double.IsNaN(px) || double.IsInfinity(px)) return;
                if (px == double.MinValue || px == double.MaxValue) return;
                px = Instrument.MasterInstrument.RoundToTickSize(px);
                for (int i = 0; i < list.Count; i++)
                {
                    if (Math.Abs(list[i].Price - px) <= TickSize * 0.5)
                        return;
                }
                list.Add(new VARejectCand { Price = px, Name = name, IsSupport = support });
            };

            int lookback = Math.Max(1, VANakedLookbackSessions);
            int completed = 0;
            for (int i = sessions.Count - 1; i >= 0 && completed < lookback; i--)
            {
                var s = sessions[i];
                if (s == null || s == currentSession) continue;
                if (s.VolumeAtPrice.Count == 0 && s.TPOsAtPrice.Count == 0) continue;
                completed++;
                bool isPrior = (prior != null && s == prior);
                string date = s.SessionDate.ToString("MM/dd");
                string pfx = isPrior ? "p" : "n";

                if (!isPrior && !VAIncludeNakedLevels && !VAIncludeHighLow)
                    continue;

                // --- VAH / VAL (prior always; older only if still naked) ---
                bool useVolVah = s.VVAH > 0 && (isPrior || (VAIncludeNakedLevels && !s.VVAHTested));
                bool useVolVal = s.VVAL > 0 && (isPrior || (VAIncludeNakedLevels && !s.VVALTested));
                bool useTpoVah = s.TPOVAH > 0 && (isPrior || (VAIncludeNakedLevels && !s.TPOVAHTested));
                bool useTpoVal = s.TPOVAL > 0 && (isPrior || (VAIncludeNakedLevels && !s.TPOVALTested));

                if (VAPreferTpoLevels)
                {
                    if (useTpoVah) add(s.TPOVAH * rowSize + rowSize, pfx + "tVAH(" + date + ")", false);
                    else if (useVolVah) add(s.VVAH * rowSize + rowSize, pfx + "vVAH(" + date + ")", false);
                    if (useTpoVal) add(s.TPOVAL * rowSize, pfx + "tVAL(" + date + ")", true);
                    else if (useVolVal) add(s.VVAL * rowSize, pfx + "vVAL(" + date + ")", true);
                }
                else
                {
                    if (useVolVah) add(s.VVAH * rowSize + rowSize, pfx + "vVAH(" + date + ")", false);
                    else if (useTpoVah) add(s.TPOVAH * rowSize + rowSize, pfx + "tVAH(" + date + ")", false);
                    if (useVolVal) add(s.VVAL * rowSize, pfx + "vVAL(" + date + ")", true);
                    else if (useTpoVal) add(s.TPOVAL * rowSize, pfx + "tVAL(" + date + ")", true);
                }

                // POC / SD levels intentionally excluded — only VAH/VAL + High/Low per user.

                // High / Low — prior session and naked from older sessions
                if (VAIncludeHighLow)
                {
                    if (s.SessionHigh > 0 && s.SessionHigh != double.MinValue
                        && (isPrior || !s.HighTested))
                        add(s.SessionHigh, pfx + "HI(" + date + ")", false);
                    if (s.SessionLow > 0 && s.SessionLow != double.MaxValue
                        && (isPrior || !s.LowTested))
                        add(s.SessionLow, pfx + "LO(" + date + ")", true);
                }
            }

            return list;
        }

        private void EvaluateValueAreaRejection()
        {
            if (!EnableValueAreaRejection) return;
            if (State != State.Realtime) return;
            if (currentSession == null || TickSize <= 0 || rowSize <= 0) return;

            var prior = GetPriorSession();
            if (prior == null) return;

            double price = 0;
            try { price = Close[0]; } catch { return; }
            if (price <= 0) return;

            string winReason;
            if (IsAutoTradeEntryWindowClosed(out winReason)) return;

            if (EnableAutoTrade && AutoTradeOnlyWhenFlat)
            {
                if (ResolveLivePositionDir() != 0) return;
            }

            double prox = Math.Max(1, VATestProximityTicks) * TickSize;

            // Build candidate rejection levels: prior-session VA + optional naked levels + highs/lows
            // Control/dominance is NOT used (IC path removed) — pure structure rejection.
            var cands = CollectVARejectionCandidates(prior);
            if (cands.Count == 0) return;

            // Closest level within proximity: support → long, resistance → short
            int dir = 0;
            double level = 0;
            string why = "";
            double bestDist = double.MaxValue;
            double poc = 0;
            {
                double volPoc = prior.VPOCLevel > 0 ? prior.VPOCLevel * rowSize + rowSize * 0.5 : 0;
                double tpoPoc = prior.POCLevel > 0 ? prior.POCLevel * rowSize + rowSize * 0.5 : 0;
                if (VAPreferTpoLevels)
                    poc = tpoPoc > 0 ? tpoPoc : volPoc;
                else
                    poc = volPoc > 0 ? volPoc : tpoPoc;
            }

            for (int ci = 0; ci < cands.Count; ci++)
            {
                var c = cands[ci];
                if (c.Price <= 0) continue;
                double d = Math.Abs(price - c.Price);
                if (d > prox) continue;

                // Support (VAL/LO) → long; Resistance (VAH/HI) → short
                int wantDir = c.IsSupport ? 1 : -1;

                if (d < bestDist)
                {
                    bestDist = d;
                    dir = wantDir;
                    level = c.Price;
                    why = "VA reject " + (wantDir > 0 ? "LONG" : "SHORT")
                        + " @" + c.Name + "=" + c.Price.ToString("F2");
                }
            }

            if (dir == 0) return;

            // Cum delta confirmation (same bar aggressor delta as MR)
            long barAsk = 0, barBid = 0;
            int barIdxCd = CurrentBars[0];
            if (primaryBarAskVol != null) primaryBarAskVol.TryGetValue(barIdxCd, out barAsk);
            if (primaryBarBidVol != null) primaryBarBidVol.TryGetValue(barIdxCd, out barBid);
            long barCumDelta = barAsk - barBid;

            if (VARequireCumDeltaConfirm)
            {
                long need = Math.Max(0, VACumDeltaMinContracts);
                if (dir > 0 && barCumDelta < need)
                {
                    // silent / light throttle — same bar can retest when delta flips
                    return;
                }
                if (dir < 0 && barCumDelta > -need)
                    return;
            }

            string dayReason;
            if (!IcDayTypeAllows(dir, price, out dayReason))
            {
                LogDayTypeBlock("VA", dir, price, why + "  " + dayReason);
                return;
            }

            double stop, tgt;
            string riskDetail;
            if (!TryFixedTickStopTarget(dir, price, out stop, out tgt, out riskDetail))
            {
                Print("SessionTPOVolumeProfile fixed-tick stop/target failed");
                return;
            }


            // Once per level per primary bar (e.g. each 2000-tick bar). New bar → level can fire again.
            int barIdx = CurrentBars[0];
            if (barIdx != vaFiredBarIndex)
            {
                vaFiredBarIndex = barIdx;
                vaFiredThisBar.Clear();
            }
            string vaKey = "VA|" + dir + "|" + ((int)(level / Math.Max(TickSize, 0.01)));
            if (vaFiredThisBar.Contains(vaKey))
                return;
            vaFiredThisBar.Add(vaKey);

            DateTime now = DateTime.Now;
            if (dir > 0) lastIzFireBuy = now; else lastIzFireSell = now;

            Print("SessionTPOVolumeProfile VA FIRE " + (dir > 0 ? "BUY" : "SELL")
                + " @ " + price.ToString("F2") + "  " + why
                + "  barΔ=" + barCumDelta.ToString("+0;-0;0")
                + "  stop=" + stop.ToString("F2") + " tgt=" + tgt.ToString("F2")
                + "  " + riskDetail);

            EmitImbalanceZoneTrade(CurrentBars[0], dir, price, stop, tgt, "VA " + why);
        }


        private void EvaluateImbalanceLevelEntries()
        {
            // Removed — only MR + prior/naked VA rejection remain as trade signals.
            return;
        }


        private bool FindNextOpposingZoneEdge(int dir, double price, out double edge, out string tag)
        {
            edge = 0; tag = "";
            var zones = GetActiveImbalanceZones();
            double best = 0;
            string bestTag = "";
            foreach (var z in zones)
            {
                if (dir > 0 && z.Side < 0 && z.LowPrice > price + TickSize)
                {
                    if (best <= 0 || z.LowPrice < best)
                    {
                        best = z.LowPrice;
                        bestTag = "zoneSELL@" + z.LowPrice.ToString("F2");
                    }
                }
                if (dir < 0 && z.Side > 0 && z.HighPrice < price - TickSize)
                {
                    if (best <= 0 || z.HighPrice > best)
                    {
                        best = z.HighPrice;
                        bestTag = "zoneBUY@" + z.HighPrice.ToString("F2");
                    }
                }
            }
            if (best > 0)
            {
                edge = Instrument.MasterInstrument.RoundToTickSize(best);
                tag = bestTag;
                return true;
            }
            return false;
        }

        /// <summary>
        /// On TrendUp/TrendDown bias: stretch target to at least RunnerMinR and prefer
        /// next imbalance LEVEL or opposing zone edge (capped at RunnerMaxR).
        /// </summary>
        private void ApplyTrendRunnerTarget(int dir, double price, double stop, ref double target, out string detail)
        {
            detail = "";
            if (!EnableTrendRunner || dir == 0 || price <= 0 || stop <= 0 || TickSize <= 0)
                return;

            string biasLab;
            int bias = GetLiveDayBias(out biasLab);
            bool trend = (bias == 1 && dir > 0) || (bias == -1 && dir < 0);
            if (!trend)
            {
                detail = "runner=off (" + biasLab + ")";
                return;
            }

            double risk = Math.Abs(price - stop);
            if (risk < TickSize) return;

            double minTgt = Instrument.MasterInstrument.RoundToTickSize(price + dir * RunnerMinR * risk);
            double maxTgt = Instrument.MasterInstrument.RoundToTickSize(price + dir * RunnerMaxR * risk);

            double pick = 0;
            string src = "";
            double imbLvl; string imbTag;
            if (FindNextImbalanceLevel(dir, price, out imbLvl, out imbTag))
            {
                double dist = Math.Abs(imbLvl - price);
                if (dist >= RunnerMinR * risk * 0.9 && dist <= RunnerMaxR * risk * 1.05)
                {
                    pick = imbLvl;
                    src = imbTag;
                }
            }
            double zoneEdge; string zoneTag;
            if (FindNextOpposingZoneEdge(dir, price, out zoneEdge, out zoneTag))
            {
                double dist = Math.Abs(zoneEdge - price);
                if (dist >= RunnerMinR * risk * 0.9 && dist <= RunnerMaxR * risk * 1.05)
                {
                    // Prefer nearer of level vs zone if both valid
                    if (pick <= 0 || dist < Math.Abs(pick - price))
                    {
                        pick = zoneEdge;
                        src = zoneTag;
                    }
                }
            }

            if (pick <= 0)
                pick = minTgt;
            else
            {
                // Clamp into [minR, maxR]
                if (dir > 0)
                {
                    if (pick < minTgt) pick = minTgt;
                    if (pick > maxTgt) pick = maxTgt;
                }
                else
                {
                    if (pick > minTgt) pick = minTgt;
                    if (pick < maxTgt) pick = maxTgt;
                }
            }

            // Only extend, never shorten an already better target
            bool better = (dir > 0 && pick > target + TickSize) || (dir < 0 && (target <= 0 || pick < target - TickSize));
            if (target <= 0 || better)
                target = Instrument.MasterInstrument.RoundToTickSize(pick);

            detail = "runner " + biasLab + " tgt=" + target.ToString("F2")
                + " (" + (src != "" ? src : (RunnerMinR.ToString("0.0") + "R")) + ")"
                + " R=" + (Math.Abs(target - price) / risk).ToString("0.00");
        }


        private void EvaluateImbalanceZoneEntries()
        {
            // Removed — only MR + prior/naked VA rejection remain as trade signals.
            return;
        }


        private void EmitImbalanceZoneTrade(int barIdx, int dir, double price, double stop, double target, string label)
        {
            if (dir == 0 || price <= 0) return;
            string key = "IZ|" + barIdx + "|" + dir + "|" + ((int)(price / Math.Max(TickSize, 0.01)));
            if (signalKeys.Contains(key)) return;
            signalKeys.Add(key);

            if (State == State.Realtime && SignalAlerts)
            {
                try
                {
                    string msg = Instrument.FullName + "  " + label + "  " + (dir > 0 ? "BUY" : "SELL") + " @ " + price.ToString("F2");
                    Alert("STVP_IZ_" + dir + "_" + DateTime.Now.Ticks, Priority.High, msg, "", 30,
                          dir > 0 ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.OrangeRed,
                          System.Windows.Media.Brushes.Black);
                }
                catch { }
            }

            // Auto-trade MR/VA when enabled
            if (State == State.Realtime && EnableAutoTrade)
            {
                string riskReason;
                double icStop = stop, icTgt = target;
                if (!ApplyAutoTradeRiskGates(dir, price, ref icStop, ref icTgt, label, out riskReason))
                {
                    Print("SessionTPOVolumeProfile auto-trade skip (" + label + "): risk gate — " + riskReason);
                }
                else
                    TryAutoTradeOnAlert(dir, icStop, icTgt, label, price);
            }

            try { ForceRefresh(); } catch { }
        }


        // =========================================================
        //  Auction context: gap, IB, live day bias, L2 risk, log
        // =========================================================

        private bool IsInitialBalanceComplete()
        {
            return currentSession != null && currentSession.IBComplete;
        }

        private double GetPriorSessionClose()
        {
            if (mostRecentCompletedSession != null)
            {
                if (mostRecentCompletedSession.RTHClose > 0)
                    return mostRecentCompletedSession.RTHClose;
                if (mostRecentCompletedSession.ClosePrice > 0)
                    return mostRecentCompletedSession.ClosePrice;
            }
            if (sessions != null)
            {
                for (int i = sessions.Count - 1; i >= 0; i--)
                {
                    var s = sessions[i];
                    if (s == null || s == currentSession) continue;
                    if (s.RTHClose > 0) return s.RTHClose;
                    if (s.ClosePrice > 0) return s.ClosePrice;
                }
            }
            return 0;
        }

        /// <summary>Gap vs prior session close. + = gap up, − = gap down (points).</summary>
        private void GetSessionGap(double price, out int gapDir, out double gapPoints, out string label)
        {
            gapDir = 0; gapPoints = 0; label = "gap=none";
            double prior = GetPriorSessionClose();
            if (prior <= 0 || price <= 0 || TickSize <= 0) { label = "gap=?"; return; }
            gapPoints = price - prior;
            double ticks = Math.Abs(gapPoints) / TickSize;
            if (ticks < 4) { gapDir = 0; label = "gap=none"; return; }
            gapDir = gapPoints > 0 ? 1 : -1;
            label = gapDir > 0
                ? ("gapUP+" + gapPoints.ToString("0.00"))
                : ("gapDN" + gapPoints.ToString("0.00"));
        }

        /// <summary>
        /// Live session bias for IC gating (not final classic day-type — that needs full session).
        /// TrendUp/TrendDown after IB extension; MeanRevert when stuck inside IB; else Unknown.
        /// </summary>
        private int GetLiveDayBias(out string label)
        {
            label = "bias=Unknown";
            if (currentSession == null || TickSize <= 0) return 0;
            double px = 0;
            try { if (Close != null && Close.Count > 0) px = Close[0]; } catch { }
            if (px <= 0) return 0;

            int gapDir; double gapPts; string gapLab;
            GetSessionGap(px, out gapDir, out gapPts, out gapLab);

            double prior = GetPriorSessionClose();
            double openPx = currentSession.RTHOpen > 0 ? currentSession.RTHOpen
                          : (currentSession.OpenPrice > 0 ? currentSession.OpenPrice : 0);

            // --- Gap-forced trend with early invalidation after IB ---
            int gapTrendMin = Math.Max(8, GapTrendMinTicks);
            double row = TickSize;
            try { if (rowSize > 0) row = rowSize; } catch { }

            bool ibDone = currentSession.IBComplete && currentSession.IBHigh > 0 && currentSession.IBLow > 0;
            double ibH = 0, ibL = 0, ibMid = 0, ibRange = 0;
            if (ibDone)
            {
                ibH = IBHighPx(currentSession);
                ibL = IBLowPx(currentSession);
                ibMid = 0.5 * (ibH + ibL);
                ibRange = ibH - ibL;
            }

            if (prior > 0 && openPx > 0)
            {
                double gapOpen = openPx - prior;
                double gapOpenTicks = Math.Abs(gapOpen) / TickSize;
                if (gapOpenTicks >= gapTrendMin)
                {
                    // Retrace vs open: 35% of gap closed is enough to drop gap-trend (was 60% — too sticky)
                    double retrace = gapOpen > 0 ? (openPx - px) / gapOpen : (px - openPx) / (-gapOpen);
                    bool gapStillValid = retrace < 0.35;

                    // After IB: invalidate if price is back inside IB (balance) or below/above IB mid against gap
                    if (ibDone && ibRange > TickSize)
                    {
                        bool insideIb = px <= ibH && px >= ibL;
                        bool lostIbMid = (gapOpen > 0 && px < ibMid) || (gapOpen < 0 && px > ibMid);
                        // Failed extension: went beyond IB then came back through IB mid
                        if (insideIb || lostIbMid)
                            gapStillValid = false;
                    }

                    // Also: if RTH high/low shows failed auction after IB (sold off from high of day while gap was up)
                    if (ibDone && gapOpen > 0 && currentSession.RTHHigh > 0)
                    {
                        double fromHigh = currentSession.RTHHigh - px;
                        if (fromHigh > Math.Max(ibRange * 0.5, 40 * TickSize) && px < openPx)
                            gapStillValid = false;
                    }
                    if (ibDone && gapOpen < 0 && currentSession.RTHLow > 0)
                    {
                        double fromLow = px - currentSession.RTHLow;
                        if (fromLow > Math.Max(ibRange * 0.5, 40 * TickSize) && px > openPx)
                            gapStillValid = false;
                    }

                    if (gapStillValid)
                    {
                        if (gapOpen > 0)
                        {
                            label = "bias=TrendUp(gap)";
                            return 1;
                        }
                        label = "bias=TrendDown(gap)";
                        return -1;
                    }
                }
            }

            if (!ibDone)
            {
                if (gapDir != 0)
                {
                    double gTicks = Math.Abs(gapPts) / TickSize;
                    if (gTicks >= gapTrendMin * 0.5)
                    {
                        if (gapDir > 0) { label = "bias=TrendUp(preIB+gap)"; return 1; }
                        if (gapDir < 0) { label = "bias=TrendDown(preIB+gap)"; return -1; }
                    }
                }
                label = "bias=Unknown(preIB)";
                return 0;
            }

            if (ibRange <= TickSize) { label = "bias=Unknown"; return 0; }

            double extUp = px - ibH;
            double extDn = ibL - px;
            if (extUp > ibRange * 0.5 && dominance >= 0.05)
            {
                label = "bias=TrendUp";
                return 1;
            }
            if (extDn > ibRange * 0.5 && dominance <= -0.05)
            {
                label = "bias=TrendDown";
                return -1;
            }
            if (px <= ibH + ibRange * 0.25 && px >= ibL - ibRange * 0.25)
            {
                label = "bias=MeanRevert";
                return 2;
            }
            label = "bias=Unknown";
            return 0;
        }

        /// <summary>
        /// Day-type gate for IC: block fade on trend days; block chase at extremes on MR days.
        /// Returns false = block trade.
        /// </summary>

        private void LogDayTypeBlock(string src, int side, double price, string detail)
        {
            string msg = src + " " + (side > 0 ? "BUY" : "SELL") + " @ " + price.ToString("F2") + "  " + detail;
            DateTime now = DateTime.Now;
            // Same message: at most once per 30s; any day-type block: at most once per 5s
            if (msg == lastDayTypeBlockMsg && (now - lastDayTypeBlockLog).TotalSeconds < 30.0)
                return;
            if ((now - lastDayTypeBlockLog).TotalSeconds < 5.0)
                return;
            lastDayTypeBlockLog = now;
            lastDayTypeBlockMsg = msg;
            Print("SessionTPOVolumeProfile " + src + " BLOCK day-type " + (side > 0 ? "BUY" : "SELL")
                + " @ " + price.ToString("F2") + "  " + detail);
        }

        private bool IcDayTypeAllows(int side, double price, out string reason)
        {
            reason = "";
            if (!false || side == 0) { reason = "dayType=off"; return true; }

            string biasLab;
            int bias = GetLiveDayBias(out biasLab);
            int gapDir; double gapPts; string gapLab;
            GetSessionGap(price, out gapDir, out gapPts, out gapLab);

            // Trend up: only longs
            if (bias == 1)
            {
                if (side < 0)
                {
                    reason = "blocked " + biasLab + " no fade shorts";
                    return false;
                }
                reason = biasLab + " long OK";
                return true;
            }
            // Trend down: only shorts
            if (bias == -1)
            {
                if (side > 0)
                {
                    reason = "blocked " + biasLab + " no fade longs";
                    return false;
                }
                reason = biasLab + " short OK";
                return true;
            }
            // Mean reversion / balance: only trade extremes of value, never mid-range.
            // Classic auction: responsive activity at range extremes; stand aside in middle of value.
            if (bias == 2 && currentSession != null && currentSession.IBComplete)
            {
                double row = TickSize;
                try { if (rowSize > 0) row = rowSize; } catch { }
                double ibH = IBHighPx(currentSession);
                double ibL = IBLowPx(currentSession);
                double ibRange = ibH - ibL;
                if (ibRange < TickSize * 4)
                {
                    reason = biasLab + " OK (IB thin)";
                    return true;
                }
                double ibMid = 0.5 * (ibH + ibL);
                // Middle 50% of IB = no new IC/IZ initiative
                double band = ibRange * 0.25;
                if (price > ibMid - band && price < ibMid + band)
                {
                    reason = "blocked MR mid-value (no initiative in balance)";
                    return false;
                }
                // Chase: buying the high of balance or selling the low
                if (side > 0 && price >= ibH - 4 * TickSize)
                {
                    reason = "blocked MR chase long at IB high";
                    return false;
                }
                if (side < 0 && price <= ibL + 4 * TickSize)
                {
                    reason = "blocked MR fade-chase short at IB low";
                    return false;
                }
                // Responsive only: long near IB low half, short near IB high half
                if (side > 0 && price > ibMid)
                {
                    reason = "blocked MR long above IB mid";
                    return false;
                }
                if (side < 0 && price < ibMid)
                {
                    reason = "blocked MR short below IB mid";
                    return false;
                }
                reason = biasLab + " extreme OK";
                return true;
            }

            reason = biasLab + " " + gapLab;
            return true;
        }

        private string FormatICAuctionContext(int side, double price)
        {
            int gapDir; double gapPts; string gapLab;
            GetSessionGap(price, out gapDir, out gapPts, out gapLab);
            string biasLab; GetLiveDayBias(out biasLab);
            string ib = IsInitialBalanceComplete() ? "IB=done" : "IB=open";

            string book = "bid/ask=?";
            try
            {
                book = "bid=" + l2BestBidSize + "@" + (l2BestBidPrice > 0 ? l2BestBidPrice.ToString("F2") : "?")
                    + " ask=" + l2BestAskSize + "@" + (l2BestAskPrice > 0 ? l2BestAskPrice.ToString("F2") : "?")
                    + " bookImb=" + l2BookImbalance.ToString("+0.00;-0.00");
            }
            catch { }

            string walls = "";
            try
            {
                var opp = FindOpposingWallAhead(side, price);
                var sup = FindSupportingWall(side, price);
                if (opp != null)
                    walls += " oppWall=" + opp.Price.ToString("F2") + "x" + Math.Max(opp.Size, opp.PeakSize);
                else
                    walls += " oppWall=none";
                if (sup != null)
                    walls += " supWall=" + sup.Price.ToString("F2") + "x" + Math.Max(sup.Size, sup.PeakSize);
                else
                    walls += " supWall=none";
            }
            catch { walls = " walls=?"; }

            return gapLab + "  " + biasLab + "  " + ib + "  " + book + walls;
        }

        /// <summary>
        /// Build stop/target from L2 opposing wall (stop beyond) and supporting wall / scan (target).
        /// Returns false if book too thin — caller falls back to tick/ATR/structure modes.
        /// </summary>
        private bool TryAuctionStopTarget(int dir, double price, out double stop, out double target, out string detail)
        {
            stop = 0; target = 0; detail = "";
            if (!false || dir == 0 || price <= 0 || TickSize <= 0) return false;

            int minRiskTicks = Math.Max(4, 0);
            double minRisk = minRiskTicks * TickSize;
            double buf = Math.Max(4, 2) * TickSize;

            // Require meaningful L2 size — ignore thin playback noise
            long minWall = Math.Max(WallMinSize, 20);

            var opp = FindOpposingWallAhead(dir, price);
            var sup = FindSupportingWall(dir, price);

            bool usedBook = false;
            if (opp != null && Math.Max(opp.Size, opp.PeakSize) >= minWall)
            {
                stop = dir > 0
                    ? Instrument.MasterInstrument.RoundToTickSize(opp.Price - buf)
                    : Instrument.MasterInstrument.RoundToTickSize(opp.Price + buf);
                usedBook = true;
            }
            else
            {
                int n; long largest; double nearest;
                SumOpposingBookAhead(dir, price, out n, out largest, out nearest);
                if (nearest > 0 && largest >= minWall)
                {
                    stop = dir > 0
                        ? Instrument.MasterInstrument.RoundToTickSize(nearest - buf)
                        : Instrument.MasterInstrument.RoundToTickSize(nearest + buf);
                    usedBook = true;
                }
            }

            // Floor: if no book or stop tighter than min risk, place stop at min risk
            double floorStop = Instrument.MasterInstrument.RoundToTickSize(price - dir * minRisk);
            if (stop <= 0)
            {
                stop = floorStop;
                usedBook = false;
            }
            else
            {
                double risk0 = Math.Abs(price - stop);
                if (risk0 + TickSize * 0.1 < minRisk)
                {
                    stop = floorStop;
                    detail = "auction: book stop < min " + minRiskTicks + "t — floored";
                }
            }

            if (dir > 0 && stop >= price) stop = floorStop;
            if (dir < 0 && stop <= price) stop = floorStop;

            double risk = Math.Abs(price - stop);
            if (risk < minRisk - TickSize * 0.1)
            {
                stop = floorStop;
                risk = Math.Abs(price - stop);
            }

            // Cap to 0
            if (0 > 0 && risk > 0 * TickSize)
            {
                stop = Instrument.MasterInstrument.RoundToTickSize(price - dir * 0 * TickSize);
                risk = Math.Abs(price - stop);
            }

            // Target: supporting wall beyond min R, else 1.0R
            if (sup != null && Math.Max(sup.Size, sup.PeakSize) >= minWall)
            {
                double supPx = Instrument.MasterInstrument.RoundToTickSize(sup.Price);
                if (dir > 0 && supPx > price + risk * 0.5) target = supPx;
                if (dir < 0 && supPx < price - risk * 0.5) target = supPx;
            }
            if (target <= 0)
                target = Instrument.MasterInstrument.RoundToTickSize(price + dir * risk);

            if (dir > 0 && target <= price) target = Instrument.MasterInstrument.RoundToTickSize(price + risk);
            if (dir < 0 && target >= price) target = Instrument.MasterInstrument.RoundToTickSize(price - risk);

            if (0.0 > 0)
            {
                double need = 0.0 * risk;
                if (Math.Abs(target - price) + 1e-9 < need)
                    target = Instrument.MasterInstrument.RoundToTickSize(price + dir * need);
            }

            detail = "auction stop=" + stop.ToString("F2") + " tgt=" + target.ToString("F2")
                + " risk=" + (risk / TickSize).ToString("0") + "t"
                + (usedBook ? " book" : " floor");
            return true;
        }

        
        private bool IcStructureAllows(int side, double price, out string detail)
        {
            detail = "";
            if (!false)
            {
                detail = "off";
                return true;
            }
            if (price <= 0 || TickSize <= 0)
            {
                detail = "no price";
                return false;
            }

            int maxTicks = Math.Max(1, 12);
            var candidates = new List<string>();

            // (1) Current session — only AFTER Initial Balance (avoids early noise / false blocks)
            if (currentSession != null)
            {
                bool useCurrent = !false || currentSession.IBComplete;
                if (useCurrent)
                    CollectSessionStructureNear(currentSession, side, price, maxTicks,
                        includeSd2: true, includeNodes: true, prefix: "", candidates);
                else if (candidates.Count == 0)
                {
                    // leave empty for now; prior/naked may still qualify below
                }
            }

            // (2) Prior day + naked older TPO/VPOC
            if (sessions != null)
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (s == null || s == currentSession) continue;

                    bool isPriorDay = (s == mostRecentCompletedSession);

                    // VPOC
                    if (s.VPOCLevel != 0 && PriorLevelInPlay(s, s.VPOCBreakTime))
                        TryAddNearLevelIdx(price, s.VPOCLevel, maxTicks,
                            (isPriorDay ? "p" : "n") + "VPOC", candidates);

                    // TPO POC
                    if (s.POCLevel != 0 && PriorLevelInPlay(s, s.TPOPOCBreakTime))
                        TryAddNearLevelIdx(price, s.POCLevel, maxTicks,
                            (isPriorDay ? "p" : "n") + "TPO-POC", candidates);

                    if (side > 0)
                    {
                        if (s.TPOVAL != 0 && PriorLevelInPlay(s, s.TPOVALBreakTime))
                            TryAddNearLevelIdx(price, s.TPOVAL, maxTicks,
                                (isPriorDay ? "p" : "n") + "TPO-VAL", candidates);
                        if (s.TPOSD2L != 0 && PriorLevelInPlay(s, s.TPOSD2LBreakTime))
                            TryAddNearLevelIdx(price, s.TPOSD2L, maxTicks,
                                (isPriorDay ? "p" : "n") + "TPO-SD2L", candidates);
                    }
                    else if (side < 0)
                    {
                        if (s.TPOVAH != 0 && PriorLevelInPlay(s, s.TPOVAHBreakTime))
                            TryAddNearLevelIdx(price, s.TPOVAH, maxTicks,
                                (isPriorDay ? "p" : "n") + "TPO-VAH", candidates);
                        if (s.TPOSD2H != 0 && PriorLevelInPlay(s, s.TPOSD2HBreakTime))
                            TryAddNearLevelIdx(price, s.TPOSD2H, maxTicks,
                                (isPriorDay ? "p" : "n") + "TPO-SD2H", candidates);
                    }
                }
            }

            if (candidates.Count > 0)
            {
                detail = string.Join("+", candidates);
                return true;
            }

            detail = "none within " + maxTicks + " ticks (TPO POC/VAH/VAL/SD2, VPOC, HVN/LVN, prior/naked)";
            return false;
        }

        private void IcFirePending(int barIdx, double dom, double price)
        {
            // IC trade signals removed — MR + VA rejection only.
            return;
        }


        /// <summary>
        /// Live path: IN CHARGE newly becomes Buyers Clear/Strong or Sellers Clear/Strong.
        /// With 0 > 0 (live only) the flip first ARMS; the alert / auto-trade fire only if
        /// control stays beyond the hold level on that side for the full timer, on the same bar.
        /// </summary>
        private void CheckInChargeClearStrongFlip(int barIdx, double dom)
        {
            if (!EnableRegimeDetection) return;

            bool persist = 0 > 0 && State == State.Realtime;

            // Persistence — resolve a pending IC signal first
            if (persist && icPendingSide != 0)
            {
                double px = IcPrice(barIdx);
                if (barIdx != icPendingBar)
                {
                    IcCancelPending("bar closed before hold time", dom, px);
                }
                else
                {
                    // Hold test uses control built from the primary bar's own OHLC + stored bid/ask volume
                    // (same formula as closed bars), so it does not depend on which data series called us.
                    double holdDom = dom;
                    double hSc, hDm, hBk, hAb, hBd, hPd, hWd;
                    STVPRegimeState hSt;
                    if (ComputeRegimeAtBar(barIdx, out hSc, out hDm, out hSt, out hBk, out hAb, out hBd, out hPd, out hWd))
                        holdDom = hDm;

                    double hold = Math.Max(0.0, 0.0);
                    bool holding = icPendingSide > 0 ? holdDom > hold : holdDom < -hold;
                    if (!holding)
                    {
                        IcCancelPending("control lost hold level", holdDom, px);
                        // fall through: the opposite side may arm on this same tick
                    }
                    else if ((IcNow() - icPendingStart).TotalSeconds >= 0)
                    {
                        IcFirePending(barIdx, holdDom, px);
                        return;
                    }
                    else
                        return;   // still waiting out the timer
                }
            }

            // Gate 1 — minimum bar activity. Do not touch the latch at all while the bar is thin:
            // the reading is re-evaluated on the next tick, so a real flip fires as soon as the bar qualifies.
            long gateAsk = 0, gateBid = 0;
            primaryBarAskVol.TryGetValue(barIdx, out gateAsk);
            primaryBarBidVol.TryGetValue(barIdx, out gateBid);
            if (0 > 0 && gateAsk + gateBid < 0) return;

            // Gate 2 — hysteresis: arm at +/-0.35, release only below 0.15.
            int now = ResolveClearStrongSide(dom, lastClearStrongSide);
            if (now == 0)
            {
                lastClearStrongSide = 0;
                return;
            }
            if (now == lastClearStrongSide)
                return;

            // Gate 2b — component alignment (optional). Block IC when this bar's own
            // barDelta / priceDir strongly fight the Control side (common early-bar noise).
            if (false)
            {
                long tot = gateAsk + gateBid;
                double barDelta = tot > 0 ? (double)(gateAsk - gateBid) / tot : 0.0;
                double priceDir = 0.0;
                bool useFb = BarsInProgress == 1 && fbBar == barIdx;
                double o = useFb ? fbOpen : (CurrentBars[0] >= 0 ? Open[0] : 0);
                double h = useFb ? fbHigh : (CurrentBars[0] >= 0 ? High[0] : 0);
                double l = useFb ? fbLow  : (CurrentBars[0] >= 0 ? Low[0]  : 0);
                double c = useFb ? fbLast : (CurrentBars[0] >= 0 ? Close[0] : 0);
                double range = h - l;
                if (range > 0) priceDir = (c - o) / range;

                // BUY: reject if delta or price is clearly leaning sell; SELL is the mirror.
                if (now > 0 && (barDelta < -0.15 || priceDir < -0.30)) return;
                if (now < 0 && (barDelta >  0.15 || priceDir >  0.30)) return;
            }

            // Gate 3 — per-side cooldown. Latch is NOT updated when blocked, so the flip
            // fires after the cooldown if that side is still in charge.
            if (State == State.Realtime && 0 > 0)
            {
                DateTime last = now > 0 ? lastICFireBuy : lastICFireSell;
                if ((DateTime.Now - last).TotalSeconds < 0)
                    return;
            }

            // Gate 2c — structure confluence (TPO POC/VAH/VAL + VPOC + HVN/LVN; no volume VA)
            {
                double pxStruct = IcPrice(barIdx);
                string structDetail;
                if (!IcStructureAllows(now, pxStruct, out structDetail))
                {
                    // Log at most once per bar+side, and at most every 30s if the bar keeps blocking
                    DateTime nowTs = IcNow();
                    bool newBarSide = (barIdx != icStructBlockBar || now != icStructBlockSide);
                    bool intervalOk = (icStructBlockLast == DateTime.MinValue
                        || (nowTs - icStructBlockLast).TotalSeconds >= 30.0);
                    if (newBarSide || intervalOk)
                    {
                        icStructBlockBar  = barIdx;
                        icStructBlockSide = now;
                        icStructBlockLast = nowTs;
                        Print("SessionTPOVolumeProfile IC BLOCK structure " + (now > 0 ? "BUY" : "SELL")
                            + " @ " + pxStruct.ToString("F2") + "  control=" + dom.ToString("+0.00;-0.00")
                            + "  " + structDetail);
                    }
                    return;
                }
            }

            // Gate 2d — day type / gap bias (no fade trend, no MR chase)
            {
                double pxDay = IcPrice(barIdx);
                string dayReason;
                if (!IcDayTypeAllows(now, pxDay, out dayReason))
                {
                    LogDayTypeBlock("IC", now, pxDay, "control=" + dom.ToString("+0.00;-0.00") + "  " + dayReason);
                    return;
                }
            }

            if (persist)
            {
                // ARM — latch the side so it does not re-arm every tick; fire only after the hold timer
                lastClearStrongSide = now;
                icPendingSide     = now;
                icPendingBar      = barIdx;
                icPendingStart    = IcNow();
                icPendingArmPrice = IcPrice(barIdx);
                string armStruct;
                IcStructureAllows(now, icPendingArmPrice, out armStruct);
                Print("SessionTPOVolumeProfile IC ARM " + (now > 0 ? "BUY" : "SELL")
                    + " @ " + icPendingArmPrice.ToString("F2") + "  control=" + dom.ToString("+0.00;-0.00")
                    + "  waiting " + 0 + "s (hold " + (now > 0 ? ">" : "<") + (now > 0 ? "" : "-")
                    + Math.Max(0.0, 0.0).ToString("0.00") + ")"
                    + (false ? ("  struct=" + armStruct) : ""));
                if (false)
                    Print("SessionTPOVolumeProfile IC CTX " + FormatICAuctionContext(now, icPendingArmPrice));
                return;
            }

            lastClearStrongSide = now;
            if (State == State.Realtime)
            {
                if (now > 0) lastICFireBuy = DateTime.Now; else lastICFireSell = DateTime.Now;
            }
            IcLogImmediateFire(barIdx, now, dom);
            EmitInChargeFlip(barIdx, now, canAlert: true);
        }

        /// <summary>
        /// Historical path: scan stored dominance in bar order for the same transitions.
        /// </summary>
        private void ScanInChargeFlipsFromHistory()
        {
            if (!EnableRegimeDetection || dominanceAtBar == null || dominanceAtBar.Count == 0)
                return;

            var bars = new List<int>(dominanceAtBar.Keys);
            bars.Sort();
            int prev = 0;
            foreach (int bar in bars)
            {
                double dom;
                if (!dominanceAtBar.TryGetValue(bar, out dom)) continue;
                int now = ResolveClearStrongSide(dom, prev);
                if (now == 0)
                {
                    prev = 0;
                    continue;
                }
                if (now != prev)
                    EmitInChargeFlip(bar, now, canAlert: false);
                prev = now;
            }
            // Do not freeze live detection to the last historical side.
            // After a realtime rebuild, sync from *current* dominance so the next
            // true transition (e.g. neutral → sellers clear) can still fire.
            if (State == State.Realtime)
                lastClearStrongSide = ResolveClearStrongSide(dominance, 0);
            else
                lastClearStrongSide = prev;
        }

        /// <summary>
        /// Mark the chart like a long/short trade entry and optionally sound an alert.
        /// Logic is only the Clear/Strong transition — nothing else.
        /// </summary>
        private void EmitInChargeFlip(int barIdx, int side, bool canAlert)
        {
            // IC trade/chart signals removed — MR + VA rejection only.
            // Regime/control logging still available via IcLogImmediateFire.
            return;
        }



        // =========================================================
        //  AUTO TRADE — market entry + ATR stop/target (draggable)
        // =========================================================


        // =========================================================
        //  Chart UI — Auto Trade on/off button (lower left)
        // =========================================================


        /// <summary>
        /// Vacuum signals — FAILURE-CONFIRMED fade. Runs on CLOSED primary bars only (history and
        /// live behave identically; nothing is evaluated mid-bar).
        ///
        /// Step 1 — SETUP: a closed bar commits in price (|priceDir| >= min) without aggressor Clear
        ///   on that side (bar delta weak, control not Clear). It is stored as a pending setup only.
        ///   Nothing is drawn, alerted or traded on the vacuum bar itself.
        /// Step 2 — TRIGGER: the NEXT closed bar must fail to extend the vacuum bar.
        ///   VU (fade short): next high &lt;= vacuum high + tol, and (optionally) next close &lt; vacuum close.
        ///   VD (fade long):  mirror.
        ///   Pass → emit VU/VD at the confirmation bar's close. Fail (it extended) → setup dropped:
        ///   the trend is running and we do not fade it.
        /// </summary>
        private void EvaluateVacuumFailureAt(int idx)
        {
            // Removed — vacuum trade signals disabled (MR + VA rejection only).
            return;
        }


        private void EmitVacuumSignal(int barIdx, int dir, double price, double extremePx, double priceDir, double barDelta, double dom)
        {
            // Vacuum trade signals removed — MR + VA rejection only.
            return;
        }


        private void EnsureAutoTradeButtonHook(ChartControl chartControl)
        {
            if (chartControl == null) return;
            if (autoTradeBtnHooked && autoTradeBtnChartControl == chartControl) return;

            UnhookAutoTradeButton();
            try
            {
                chartControl.MouseLeftButtonDown += AutoTradeButton_MouseLeftButtonDown;
                autoTradeBtnChartControl = chartControl;
                autoTradeBtnHooked = true;
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile: button hook error — " + ex.Message);
            }
        }

        private void UnhookAutoTradeButton()
        {
            if (autoTradeBtnChartControl != null && autoTradeBtnHooked)
            {
                try { autoTradeBtnChartControl.MouseLeftButtonDown -= AutoTradeButton_MouseLeftButtonDown; } catch { }
            }
            autoTradeBtnChartControl = null;
            autoTradeBtnHooked = false;
        }

        private void AutoTradeButton_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ChartPanel == null) return;
            try
            {
                System.Windows.Point pos = e.GetPosition(ChartPanel);
                float mx = (float)pos.X;
                float my = (float)pos.Y;
                // autoTradeBtnRect is in ChartPanel-local coordinates (X/Y relative to panel)
                if (mx < autoTradeBtnRect.Left || mx > autoTradeBtnRect.Right
                    || my < autoTradeBtnRect.Top || my > autoTradeBtnRect.Bottom)
                    return;

                e.Handled = true;
                EnableAutoTrade = !EnableAutoTrade;

                if (EnableAutoTrade)
                {
                    AttachAutoTradeAccount();
                    Print("SessionTPOVolumeProfile: AUTO TRADE ON  (account=" + (AutoTradeAccountName ?? "") + ")");
                }
                else
                {
                    // Do not cancel open orders — only block new entries
                    Print("SessionTPOVolumeProfile: AUTO TRADE OFF  (no new entries)");
                }

                try { ForceRefresh(); } catch { }
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile: button click error — " + ex.Message);
            }
        }

        private void EnsureAutoTradeButtonResources()
        {
            if (RenderTarget == null) return;
            if (autoTradeBtnOnBrush != null) return;
            try
            {
                // ON = green, OFF = dark red/gray
                autoTradeBtnOnBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.10f, 0.55f, 0.25f, 0.92f));
                autoTradeBtnOffBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.45f, 0.12f, 0.12f, 0.92f));
                autoTradeBtnTextBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1f, 1f, 1f, 1f));
                autoTradeBtnFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 11);
                autoTradeBtnFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Center;
                autoTradeBtnFormat.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
                autoTradeBtnFormat.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile: button resources error — " + ex.Message);
            }
        }

        private void DisposeAutoTradeButtonResources()
        {
            if (autoTradeBtnOnBrush != null)  { autoTradeBtnOnBrush.Dispose();  autoTradeBtnOnBrush = null; }
            if (autoTradeBtnOffBrush != null) { autoTradeBtnOffBrush.Dispose(); autoTradeBtnOffBrush = null; }
            if (autoTradeBtnTextBrush != null){ autoTradeBtnTextBrush.Dispose();autoTradeBtnTextBrush = null; }
            if (autoTradeBtnFormat != null)   { autoTradeBtnFormat.Dispose();   autoTradeBtnFormat = null; }
        }

        private void RenderAutoTradeButton(ChartControl chartControl, ChartScale chartScale)
        {
            if (ChartPanel == null || RenderTarget == null) return;
            EnsureAutoTradeButtonResources();
            if (autoTradeBtnOnBrush == null || autoTradeBtnFormat == null) return;

            const float bw = 118f;
            const float bh = 26f;
            // Lower-left of the main price panel (panel-local coords for hit-testing)
            float localX = 8f;
            float localY = ChartPanel.H - bh - 8f;
            // Draw in absolute chart coordinates
            float drawX = ChartPanel.X + localX;
            float drawY = ChartPanel.Y + localY;

            autoTradeBtnRect = new SharpDX.RectangleF(localX, localY, bw, bh);

            var fill = EnableAutoTrade ? autoTradeBtnOnBrush : autoTradeBtnOffBrush;
            var rect = new SharpDX.RectangleF(drawX, drawY, bw, bh);
            RenderTarget.FillRectangle(rect, fill);
            RenderTarget.DrawRectangle(rect, autoTradeBtnTextBrush, 1.2f);

            string label = EnableAutoTrade ? "AUTO TRADE: ON" : "AUTO TRADE: OFF";
            RenderTarget.DrawText(label, autoTradeBtnFormat, rect, autoTradeBtnTextBrush);
        }


        private void AttachAutoTradeAccount()
        {
            DetachAutoTradeAccount();
            if (!EnableAutoTrade) return;

            autoTradeAccount = ResolveAutoTradeAccount();
            if (autoTradeAccount == null) return;

            try
            {
                autoTradeAccount.OrderUpdate += OnAutoTradeOrderUpdate;
                autoTradeAccount.ExecutionUpdate += OnAutoTradeExecutionUpdate;
                string conn = (autoTradeAccount.Connection != null)
                    ? autoTradeAccount.Connection.Status.ToString()
                    : "no-connection";
                Print("SessionTPOVolumeProfile auto-trade attached: account='" + autoTradeAccount.Name
                    + "' connection=" + conn);
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile auto-trade attach error: " + ex.Message);
                autoTradeAccount = null;
            }
        }

        /// <summary>Exact name first, then case-insensitive match. Prints available accounts on failure.</summary>
        private Account ResolveAutoTradeAccount()
        {
            string want = (AutoTradeAccountName ?? string.Empty).Trim();
            if (want.Length == 0)
            {
                Print("SessionTPOVolumeProfile auto-trade: Account Name is empty. Set it in properties (e.g. Sim101).");
                return null;
            }

            Account exact = null, loose = null;
            var names = new System.Text.StringBuilder();
            foreach (Account a in Account.All)
            {
                if (a == null || string.IsNullOrEmpty(a.Name)) continue;
                if (names.Length > 0) names.Append(", ");
                names.Append(a.Name);
                if (a.Name == want) exact = a;
                else if (loose == null && string.Equals(a.Name, want, StringComparison.OrdinalIgnoreCase))
                    loose = a;
            }
            if (exact != null) return exact;
            if (loose != null)
            {
                Print("SessionTPOVolumeProfile auto-trade: using account '" + loose.Name + "' (case-insensitive match for '" + want + "').");
                return loose;
            }
            Print("SessionTPOVolumeProfile auto-trade: account '" + want + "' not found. Available: ["
                + names + "]. Set 'Account Name' in properties to one of these.");
            return null;
        }

        private void DetachAutoTradeAccount()
        {
            if (autoTradeAccount != null)
            {
                try { autoTradeAccount.OrderUpdate -= OnAutoTradeOrderUpdate; } catch { }
                try { autoTradeAccount.ExecutionUpdate -= OnAutoTradeExecutionUpdate; } catch { }
            }
            autoTradeAccount = null;
            workingEntryOrder = null;
            limitCancelRequested = false;
            workingStopOrder = null;
            workingTargetOrder = null;
            pendingEntrySignal = null;
        }

        /// <summary>
        /// Called when a live alert fires. Submits market entry; stop + target are
        /// attached on fill as working orders (drag on chart to adjust).
        /// </summary>

        /// <summary>
        /// Fixed-tick only: ensure stop/target sides are valid. Does not change distances.
        /// </summary>
        private bool ApplyAutoTradeRiskGates(int dir, double entry, ref double stop, ref double target, string signalTag, out string reason)
        {
            reason = "";
            if (entry <= 0 || TickSize <= 0 || dir == 0)
            {
                reason = "bad entry/dir";
                return false;
            }
            // Always re-apply user tick distances so gates cannot widen or shrink
            int sl = Math.Max(1, StopLossTicks);
            int tp = Math.Max(1, TakeProfitTicks);
            stop   = Instrument.MasterInstrument.RoundToTickSize(entry - dir * sl * TickSize);
            target = Instrument.MasterInstrument.RoundToTickSize(entry + dir * tp * TickSize);
            return true;
        }


        private void TryAutoTradeOnAlert(int dir, double stopPrice, double targetPrice, string signalTag, double signalPrice = 0)
        {
            try
            {
                if (!EnableAutoTrade)
                {
                    Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): EnableAutoTrade is OFF.");
                    return;
                }
                if (State != State.Realtime)
                {
                    Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): not realtime (State=" + State + ").");
                    return;
                }
                if (dir == 0) return;

                // Last Signal Time + Force Flat Time: no new auto entries
                {
                    string winReason;
                    if (IsAutoTradeEntryWindowClosed(out winReason))
                    {
                        Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): " + winReason + ".");
                        return;
                    }
                }

                // Risk gates (0 / 0.0) — cap stop, enforce R:R
                {
                    double e0 = signalPrice > 0 ? signalPrice : 0;
                    try { if (e0 <= 0 && Close.Count > 0) e0 = Close[0]; } catch { }
                    if (e0 <= 0 && BarsArray != null && BarsArray[0] != null && CurrentBars[0] >= 0)
                        e0 = BarsArray[0].GetClose(CurrentBars[0]);
                    double s0 = stopPrice, t0 = targetPrice;
                    string riskReason;
                    if (e0 > 0 && !ApplyAutoTradeRiskGates(dir, e0, ref s0, ref t0, signalTag, out riskReason))
                    {
                        Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): risk gate — " + riskReason);
                        return;
                    }
                    stopPrice = s0;
                    targetPrice = t0;
                }

                // Phase A: skip entry when opposing passive size blocks the path
                {
                    double pathPx = signalPrice > 0 ? signalPrice : (Close.Count > 0 ? Close[0] : 0);
                    string pathReason;
                    if (pathPx > 0 && IsEntryPathBlocked(dir, pathPx, out pathReason))
                    {
                        Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag
                            + "): Phase A large opposing level — " + pathReason);
                        return;
                    }
                }

                // RTH-open blackout: no auto entries for the first N seconds of RTH (manual trades unaffected)
                if (AutoTradeRthBlackoutSeconds > 0)
                {
                    DateTime nowTick = lastTickTimeSeen;
                    if (nowTick == DateTime.MinValue)
                    {
                        try { nowTick = Times[1][0]; } catch { nowTick = DateTime.MinValue; }
                    }
                    if (nowTick != DateTime.MinValue)
                    {
                        TimeSpan tod = nowTick.TimeOfDay;
                        DateTime rthOpenDt     = GetRthStartDt(nowTick);
                        DateTime blackoutEndDt = rthOpenDt.AddSeconds(AutoTradeRthBlackoutSeconds);
                        if (nowTick >= rthOpenDt && nowTick < blackoutEndDt)
                        {
                            Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): RTH-open blackout until "
                                + blackoutEndDt.TimeOfDay.ToString(@"hh\:mm\:ss") + " (tick time " + tod.ToString(@"hh\:mm\:ss") + ").");
                            return;
                        }
                    }
                }

                if (autoTradeAccount == null)
                    AttachAutoTradeAccount();
                if (autoTradeAccount == null)
                {
                    Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): no account attached.");
                    return;
                }

                if (autoTradeAccount.Connection != null
                    && autoTradeAccount.Connection.Status != ConnectionStatus.Connected)
                {
                    Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): account not connected ("
                        + autoTradeAccount.Connection.Status + ").");
                    return;
                }

                if (AutoTradeQuantity < 1)
                {
                    Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): quantity < 1.");
                    return;
                }

                // Fixed ticks only for stop / target
                double entryPx = signalPrice > 0 ? signalPrice : 0;
                try { if (entryPx <= 0 && Close.Count > 0) entryPx = Close[0]; } catch { }
                if (entryPx <= 0 && BarsArray != null && BarsArray[0] != null && CurrentBars[0] >= 0)
                    entryPx = BarsArray[0].GetClose(CurrentBars[0]);
                if (entryPx <= 0)
                {
                    Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): no entry price");
                    return;
                }
                {
                    int sl = Math.Max(1, StopLossTicks);
                    int tp = Math.Max(1, TakeProfitTicks);
                    stopPrice   = Instrument.MasterInstrument.RoundToTickSize(entryPx - dir * sl * TickSize);
                    targetPrice = Instrument.MasterInstrument.RoundToTickSize(entryPx + dir * tp * TickSize);
                }

                bool useLimit = (AutoTradeEntryOrderType == STVPEntryOrderType.LimitAtSignal);
                double limitPx = useLimit ? Instrument.MasterInstrument.RoundToTickSize(entryPx) : 0;

                lock (autoTradeLock)
                {
                    if (AutoTradeOnlyWhenFlat)
                    {
                        foreach (Position p in autoTradeAccount.Positions)
                        {
                            if (p == null || p.Instrument == null) continue;
                            if (p.Instrument.FullName != Instrument.FullName) continue;
                            if (p.MarketPosition != MarketPosition.Flat)
                            {
                                Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): already in position ("
                                    + p.MarketPosition + " " + p.Quantity + ").");
                                return;
                            }
                        }
                    }

                    if (pendingEntrySignal != null)
                    {
                        Print("SessionTPOVolumeProfile auto-trade skip (" + signalTag + "): entry already pending ("
                            + pendingEntrySignal + ").");
                        return;
                    }

                    OrderAction action = dir > 0 ? OrderAction.Buy : OrderAction.Sell;
                    string name = "STVP " + signalTag + (dir > 0 ? " LE" : " SE");

                    pendingDir         = dir;
                    pendingStop        = stopPrice;
                    pendingTarget      = targetPrice;
                    pendingQty         = AutoTradeQuantity;

                    // Short, safe signal name (long MR labels with spaces/=/@ can break CreateOrder)
                    string safeName = "STVP_MR_" + (dir > 0 ? "LE" : "SE") + "_" + (DateTime.Now.Ticks % 1000000000);
                    pendingEntrySignal = safeName;

                    Account acct = autoTradeAccount;
                    Instrument instr = Instrument;
                    if (acct == null || instr == null || instr.MasterInstrument == null)
                    {
                        pendingEntrySignal = null;
                        Print("SessionTPOVolumeProfile auto-trade ENTRY ERROR: account or Instrument is null");
                        return;
                    }

                    int qty = Math.Max(1, AutoTradeQuantity);
                    double lim = useLimit ? limitPx : 0;
                    bool limFlag = useLimit;
                    double stopP = stopPrice;
                    double tgtP = targetPrice;
                    OrderAction act = action;

                    Action submit = () =>
                    {
                        try
                        {
                            if (acct == null || instr == null)
                            {
                                pendingEntrySignal = null;
                                Print("SessionTPOVolumeProfile auto-trade ENTRY ERROR: null account/instrument on submit");
                                return;
                            }
                            Order entry = acct.CreateOrder(
                                instr,
                                act,
                                limFlag ? OrderType.Limit : OrderType.Market,
                                OrderEntry.Manual,
                                TimeInForce.Day,
                                qty,
                                lim, 0,
                                string.Empty,
                                safeName,
                                Core.Globals.MinDate,
                                null);
                            if (entry == null)
                            {
                                pendingEntrySignal = null;
                                Print("SessionTPOVolumeProfile auto-trade ENTRY ERROR: CreateOrder returned null");
                                return;
                            }
                            workingEntryOrder = entry;
                            if (limFlag)
                            {
                                limitEntrySubmitTime = DateTime.Now;
                                limitEntryBarIdx     = CurrentBars[0];
                                limitCancelRequested = false;
                            }
                            acct.Submit(new[] { entry });
                            Print("SessionTPOVolumeProfile auto-trade ENTRY submitted: " + safeName
                                + (limFlag ? " LIMIT @ " + lim.ToString("F2") : " MARKET")
                                + " qty=" + qty
                                + " stop=" + stopP.ToString("F2")
                                + " target=" + tgtP.ToString("F2")
                                + " account=" + acct.Name
                                + "  (" + signalTag + ")");
                        }
                        catch (Exception ex)
                        {
                            pendingEntrySignal = null;
                            workingEntryOrder = null;
                            Print("SessionTPOVolumeProfile auto-trade ENTRY ERROR: " + ex.GetType().Name + ": " + ex.Message
                                + (ex.InnerException != null ? " | inner: " + ex.InnerException.Message : ""));
                        }
                    };

                    // Prefer direct submit on this thread (live + playback). Dispatcher only if required.
                    try
                    {
                        if (ChartControl != null && ChartControl.Dispatcher != null
                            && !ChartControl.Dispatcher.CheckAccess())
                            ChartControl.Dispatcher.InvokeAsync(submit);
                        else
                            submit();
                    }
                    catch (Exception ex)
                    {
                        Print("SessionTPOVolumeProfile auto-trade dispatch fallback: " + ex.Message);
                        try { submit(); } catch (Exception ex2)
                        {
                            pendingEntrySignal = null;
                            Print("SessionTPOVolumeProfile auto-trade ENTRY ERROR: " + ex2.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile auto-trade unexpected error (" + signalTag + "): " + ex.Message);
            }
        }

        private void OnAutoTradeExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            // NT8: order is on Execution, not on ExecutionEventArgs
            if (e == null || e.Execution == null) return;
            Order execOrder = e.Execution.Order;
            if (execOrder == null) return;
            if (autoTradeAccount == null) return;
            if (e.Execution.Instrument != Instrument) return;
            if (e.Execution.Quantity <= 0) return;

            lock (autoTradeLock)
            {
                // --- Exit fills (stop / target / STVP EXIT) → store for fill-based P&L ---
                bool isExitFill = false;
                string exitName = execOrder.Name ?? "";
                if (workingStopOrder != null && execOrder == workingStopOrder)
                    isExitFill = true;
                else if (workingTargetOrder != null && execOrder == workingTargetOrder)
                    isExitFill = true;
                else if (exitName == "STVP Stop" || exitName == "STVP Target"
                         || exitName.StartsWith("STVP EXIT", StringComparison.Ordinal))
                    isExitFill = true;

                if (isExitFill && e.Execution.Price > 0)
                {
                    tradeMonExitFill = e.Execution.Price;
                    tradeMonExitFillName = exitName;
                    Print("SessionTPOVolumeProfile auto-trade EXIT FILL: " + exitName
                        + " @ " + tradeMonExitFill.ToString("F2")
                        + " qty=" + e.Execution.Quantity);
                    // do not return — entry path may still be independent
                }

                // Attach stop + target once our entry fills
                if (pendingEntrySignal == null) return;
                if (execOrder.Name != pendingEntrySignal && (workingEntryOrder == null || execOrder != workingEntryOrder))
                    return;

                // Entry filled (or partially — still place protective orders once)
                string oco = "STVP_OCO_" + DateTime.Now.Ticks.ToString();
                OrderAction exitAction = pendingDir > 0 ? OrderAction.Sell : OrderAction.Buy;
                int qty = pendingQty > 0 ? pendingQty : e.Execution.Quantity;

                // Measure stop/target from ACTUAL fill.
                // Tick mode: ALWAYS exact StopLossTicks / TakeProfitTicks from fill — never widen with 0.
                // Structure/other: keep pending geometry if still valid; optional min-risk floor only then.
                double fillPx = e.Execution.Price;
                if (fillPx > 0 && pendingDir != 0)
                {
                    // Fixed ticks only — from fill, exactly as configured
                    int slTicks = Math.Max(1, StopLossTicks);
                    int tpTicks = Math.Max(1, TakeProfitTicks);
                    pendingStop   = Instrument.MasterInstrument.RoundToTickSize(fillPx - pendingDir * slTicks * TickSize);
                    pendingTarget = Instrument.MasterInstrument.RoundToTickSize(fillPx + pendingDir * tpTicks * TickSize);
                }

                try
                {
                    Order stopOrd = autoTradeAccount.CreateOrder(
                        Instrument,
                        exitAction,
                        OrderType.StopMarket,
                        OrderEntry.Manual,
                        TimeInForce.Day,
                        qty,
                        0,
                        pendingStop,
                        oco,
                        "STVP Stop",
                        Core.Globals.MinDate,
                        null);

                    Order tgtOrd = autoTradeAccount.CreateOrder(
                        Instrument,
                        exitAction,
                        OrderType.Limit,
                        OrderEntry.Manual,
                        TimeInForce.Day,
                        qty,
                        pendingTarget,
                        0,
                        oco,
                        "STVP Target",
                        Core.Globals.MinDate,
                        null);

                    workingStopOrder   = stopOrd;
                    workingTargetOrder = tgtOrd;
                    autoTradeAccount.Submit(new[] { stopOrd, tgtOrd });

                    // Seed in-trade manager with fill + protective prices
                    if (fillPx > 0)
                    {
                        tradeMonEntry = fillPx;
                        tradeMonDir = pendingDir;
                        tradeMonQty = qty > 0 ? qty : (AutoTradeQuantity > 0 ? AutoTradeQuantity : 1);
                        tradeMonExitFill = 0;
                        tradeMonExitFillName = "";
                        tradeMonStopPrice = pendingStop;
                        tradeMonTargetPrice = pendingTarget;
                        tradeMonCautionApplied = false;
                        tradeMonExitApplied = false;
                    }

                    Print("SessionTPOVolumeProfile auto-trade protective orders submitted (draggable on chart): stop="
                        + pendingStop.ToString("F2") + " target=" + pendingTarget.ToString("F2") + " oco=" + oco);
                }
                catch (Exception ex)
                {
                    Print("SessionTPOVolumeProfile auto-trade stop/target error: " + ex.Message);
                }
                finally
                {
                    pendingEntrySignal = null;
                    pendingDir = 0;
                    pendingStop = 0;
                    pendingTarget = 0;
                    pendingQty = 0;
                    workingEntryOrder = null;
                }
            }
        }

        /// <summary>
        /// Limit At Signal: cancel an unfilled limit entry after the configured seconds (or at the close of the
        /// signal bar when seconds = 0). The order-update handler clears the pending state on cancel.
        /// </summary>
        private void CheckLimitEntryExpiry()
        {
            if (workingEntryOrder == null || pendingEntrySignal == null || limitCancelRequested) return;
            if (State != State.Realtime || autoTradeAccount == null) return;
            if (workingEntryOrder.OrderType != OrderType.Limit) return;

            OrderState os = workingEntryOrder.OrderState;
            if (os != OrderState.Working && os != OrderState.Accepted && os != OrderState.Submitted) return;

            bool expired;
            if (AutoTradeLimitCancelSeconds > 0)
                expired = (DateTime.Now - limitEntrySubmitTime).TotalSeconds >= AutoTradeLimitCancelSeconds;
            else
                expired = limitEntryBarIdx >= 0 && CurrentBars[0] > limitEntryBarIdx;
            if (!expired) return;

            limitCancelRequested = true;
            Order toCancel = workingEntryOrder;
            Action cancel = () =>
            {
                try
                {
                    autoTradeAccount.Cancel(new[] { toCancel });
                    Print("SessionTPOVolumeProfile auto-trade limit entry cancel requested (unfilled): " + toCancel.Name);
                }
                catch (Exception ex)
                {
                    limitCancelRequested = false;
                    Print("SessionTPOVolumeProfile auto-trade limit cancel ERROR: " + ex.Message);
                }
            };
            try
            {
                if (ChartControl != null && ChartControl.Dispatcher != null && !ChartControl.Dispatcher.CheckAccess())
                    ChartControl.Dispatcher.InvokeAsync(cancel);
                else
                    cancel();
            }
            catch { cancel(); }
        }

        private void OnAutoTradeOrderUpdate(object sender, OrderEventArgs e)
        {
            if (e == null || e.Order == null) return;
            if (e.Order.Instrument != Instrument) return;

            // Clear pending if entry is cancelled / rejected before fill
            lock (autoTradeLock)
            {
                if (pendingEntrySignal != null && e.Order.Name == pendingEntrySignal)
                {
                    if (e.Order.OrderState == OrderState.Cancelled
                        || e.Order.OrderState == OrderState.Rejected
                        || e.Order.OrderState == OrderState.Filled)
                    {
                        if (e.Order.OrderState != OrderState.Filled)
                        {
                            Print("SessionTPOVolumeProfile auto-trade entry " + e.Order.OrderState + ": " + e.Order.Name);
                            pendingEntrySignal = null;
                            workingEntryOrder = null;
                        }
                    }
                }
            }
        }


        #region Trade Signals

        private const double ControlGate = 0.08;   // same threshold as the IN CHARGE panel

        private class SigLevel
        {
            public double   Price;
            public string   Name;
            public DateTime BreakTime;   // MinValue = never tested
            public bool     Recent;      // belongs to the most recent completed session
        }

        private class SigCtx
        {
            public int      idx;
            public double   o, h, l, c, pc, ph, pl;
            public DateTime t, pt;
            public double   dom;
            public STVPRegimeState st;
            public string   sk;          // session key text
        }

        private class ZoneSnap { public double Lo; public double Hi; }
        private class RejectedSetup { public int BarIndex; public int Dir; public string Setup; public string Reason; }

        private class TradeSignal
        {
            public int      BarIndex;
            public DateTime Time;
            public DateTime SessionKey;
            public int      Dir;                 // +1 buy, -1 sell
            public string   Setup;               // e.g. "NK" or "NK+FB"
            public string   Detail;
            public double   Entry;
            public double   Stop;
            public double[] TP       = new double[3];
            public string[] TPName   = new string[3];
            public int      TPCount;
            public int[]    TPHitBar = new int[] { -1, -1, -1 };
            public int      StopHitBar = -1;
            public int      EndBar = -1;
            public int      Points;
            public string   Grade;
            public int      Tier;      // 1 = control Clear/Strong, 2 = Slight, 3 = neither (control gate off)
            public int      Conf;      // confirmations passed
            public string   Flags;     // letters of the confirmations that passed
        }

        // ---- brushes / format (created lazily on the render thread) ----
        private SharpDX.Direct2D1.SolidColorBrush sigBuyBrush, sigSellBrush, sigStopBrush, sigTPBrush, sigEntryBrush, sigIcBuyBrush, sigIcSellBrush;
        private SharpDX.DirectWrite.TextFormat    sigLabelFormat;

        private static string GradeText(int pts)
        {
            return pts >= 10 ? "A+" : (pts >= 8 ? "A" : (pts >= 6 ? "B" : "C"));
        }

        // ===== LevelAccessor (additive, read-only) - added for LevelAbsorption =====
        // Exposes prior/naked session levels (VPOC, VVAH, VVAL, HI, LO, plus SPL/SPH = bottom/top edge of each single print
        // band) and signal times to other indicators on the
        // same chart. Keeps a lightweight archive that is NOT trimmed to MaxSessionsToShow, so history older than the
        // drawn sessions can still be analysed. Nothing here changes how levels are calculated or drawn.
        #region LevelAccessor

        public class STVPLevelRecord
        {
            public string   Type;          // VPOC, VVAH, VVAL, HI, LO, SPL, SPH
            public double   Price;
            public DateTime SessionStart;
            public DateTime SessionEnd;
            public DateTime BreakTime;     // first time a tick printed in the level's row; MinValue = never tested.
                                           // SPL/SPH: time the whole single print band was filled (MinValue = still open)
        }

        public class STVPSignalRecord
        {
            public DateTime Time;
            public string   Setup;
            public int      Dir;           // +1 buy, -1 sell
            public double   Entry;
        }

        private class LevelArchiveEntry
        {
            public SessionProfile          Src;      // live reference while the host still tracks the session
            public List<STVPLevelRecord>   Frozen;   // copied once the host trims the session
            public DateTime                End;
        }

        private readonly List<LevelArchiveEntry> levelArchive = new List<LevelArchiveEntry>();
        private readonly object levelArchiveLock = new object();
        private const int LevelArchiveMaxSessions = 800;

        private List<STVPLevelRecord> BuildLevelRecords(SessionProfile s)
        {
            var list = new List<STVPLevelRecord>();
            if (s == null) return list;
            if (s.VolumeAtPrice.Count == 0 && s.TPOsAtPrice.Count == 0) return list;
            Action<string, double, DateTime> add = (type, price, bt) =>
            {
                if (price > 0)
                    list.Add(new STVPLevelRecord { Type = type, Price = price, SessionStart = s.StartTime, SessionEnd = s.EndTime, BreakTime = bt });
            };
            if (s.VPOCLevel > 0) add("VPOC", s.VPOCLevel * rowSize + rowSize * 0.5, s.VPOCBreakTime);
            if (s.VVAH > 0)      add("VVAH", s.VVAH * rowSize + rowSize,            s.VVAHBreakTime);
            if (s.VVAL > 0)      add("VVAL", s.VVAL * rowSize,                      s.VVALBreakTime);
            if (s.SessionHigh > 0 && s.SessionHigh != double.MinValue) add("HI", s.SessionHigh, s.HighBreakTime);
            if (s.SessionLow  > 0 && s.SessionLow  != double.MaxValue) add("LO", s.SessionLow,  s.LowBreakTime);
            if (s.SinglePrintZones != null)
                foreach (var z in s.SinglePrintZones)
                {
                    add("SPL", z.LowLevel * rowSize,             z.BreakTime);   // same edge prices the host uses for "SPlo"/"SPhi"
                    add("SPH", z.HighLevel * rowSize + rowSize,  z.BreakTime);
                }
            return list;
        }

        // Called right after a session is added (and the list trimmed). Freezes entries the host has dropped.
        private void ArchiveLevelSession(SessionProfile s)
        {
            if (s == null) return;
            lock (levelArchiveLock)
            {
                for (int i = 0; i < levelArchive.Count; i++)
                {
                    LevelArchiveEntry e = levelArchive[i];
                    if (e.Src != null && !sessions.Contains(e.Src))
                    {
                        e.Frozen = BuildLevelRecords(e.Src);
                        e.End = e.Src.EndTime;
                        e.Src = null;
                    }
                }
                levelArchive.Add(new LevelArchiveEntry { Src = s });
                while (levelArchive.Count > LevelArchiveMaxSessions) levelArchive.RemoveAt(0);
            }
        }

        /// <summary>
        /// Levels of the last maxPriorSessions sessions that had COMPLETED at asOf. BreakTime is the host's first-touch
        /// time as of now: the caller must treat a level as naked at time t only if BreakTime is MinValue or >= t.
        /// </summary>
        public List<STVPLevelRecord> GetLevelSnapshot(DateTime asOf, int maxPriorSessions)
        {
            var result = new List<STVPLevelRecord>();
            var completed = new List<SessionProfile>();
            lock (levelArchiveLock)
            {
                // Primary source: non-current sessions that already started before asOf.
                // Do NOT require EndTime <= asOf — at RTH open the Globex session is still
                // the "current" trading day and prior days must still be visible.
                if (sessions != null)
                {
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        SessionProfile s = sessions[i];
                        if (s == null || s == currentSession) continue;
                        if (s.StartTime >= asOf) continue;
                        if ((s.VolumeAtPrice == null || s.VolumeAtPrice.Count == 0)
                            && (s.TPOsAtPrice == null || s.TPOsAtPrice.Count == 0))
                            continue;
                        completed.Add(s);
                    }
                }
                // Older sessions trimmed from the display list but frozen in the archive
                foreach (LevelArchiveEntry e in levelArchive)
                {
                    if (e.Src != null) continue; // still live in sessions — already considered
                    if (e.Frozen == null || e.Frozen.Count == 0) continue;
                    if (e.End > DateTime.MinValue && e.End > asOf) continue;
                    // Use frozen records directly later via a parallel list
                    // Represent as a synthetic pass by appending records after the loop
                }
                int take = Math.Max(1, maxPriorSessions);
                int first = Math.Max(0, completed.Count - take);
                for (int i = first; i < completed.Count; i++)
                {
                    List<STVPLevelRecord> recs = BuildLevelRecords(completed[i]);
                    if (recs != null) result.AddRange(recs);
                }
                // Append frozen archive (oldest first), still capped overall by take sessions worth
                int frozenSessions = 0;
                for (int i = levelArchive.Count - 1; i >= 0 && frozenSessions < take; i--)
                {
                    LevelArchiveEntry e = levelArchive[i];
                    if (e.Src != null || e.Frozen == null || e.Frozen.Count == 0) continue;
                    if (e.End > DateTime.MinValue && e.End > asOf) continue;
                    // Avoid duplicates if these prices already came from sessions list — cheap skip by session end
                    result.InsertRange(0, e.Frozen);
                    frozenSessions++;
                }
            }
            return result;
        }

        /// <summary>Copy of the signals the host has generated (time, setup, direction, entry).</summary>
        public List<STVPSignalRecord> GetSignalSnapshot()
        {
            var list = new List<STVPSignalRecord>();
            try
            {
                TradeSignal[] copy = signals.ToArray();
                foreach (TradeSignal s in copy)
                    list.Add(new STVPSignalRecord { Time = s.Time, Setup = s.Setup, Dir = s.Dir, Entry = s.Entry });
            }
            catch (Exception) { }
            return list;
        }

        #endregion

        private SessionProfile GetPriorSession()
        {
            for (int i = sessions.Count - 1; i >= 0; i--)
                if (sessions[i] != currentSession && sessions[i].VolumeAtPrice.Count > 0)
                    return sessions[i];
            return null;
        }

        /// <summary>All key levels of every prior session, with their first-test time.</summary>
        private List<SigLevel> GetPriorLevels()
        {
            var list = new List<SigLevel>();
            var recent = GetPriorSession();
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s == currentSession) continue;
                if (s.VolumeAtPrice.Count == 0 && s.TPOsAtPrice.Count == 0) continue;
                bool rec = (s == recent);
                string p = rec ? "p" : "n";
                Action<double, string, DateTime> add = (price, nm, bt) =>
                {
                    if (price > 0) list.Add(new SigLevel { Price = price, Name = p + nm, BreakTime = bt, Recent = rec });
                };

                if (s.VPOCLevel > 0) add(s.VPOCLevel * rowSize + rowSize * 0.5, "VPOC", s.VPOCBreakTime);
                if (s.VVAH  > 0) add(s.VVAH  * rowSize + rowSize, "VAH",  s.VVAHBreakTime);
                if (s.VVAL  > 0) add(s.VVAL  * rowSize,           "VAL",  s.VVALBreakTime);
                if (s.SD2H  > 0) add(s.SD2H  * rowSize + rowSize, "SD2H", s.SD2HBreakTime);
                if (s.SD2L  > 0) add(s.SD2L  * rowSize,           "SD2L", s.SD2LBreakTime);
                if (s.SD3H  > 0) add(s.SD3H  * rowSize + rowSize, "SD3H", s.SD3HBreakTime);
                if (s.SD3L  > 0) add(s.SD3L  * rowSize,           "SD3L", s.SD3LBreakTime);
                if (s.POCLevel > 0) add(s.POCLevel * rowSize + rowSize * 0.5, "tPOC", s.TPOPOCBreakTime);
                if (s.TPOVAH > 0)  add(s.TPOVAH  * rowSize + rowSize, "tVAH",  s.TPOVAHBreakTime);
                if (s.TPOVAL > 0)  add(s.TPOVAL  * rowSize,           "tVAL",  s.TPOVALBreakTime);
                if (s.TPOSD2H > 0) add(s.TPOSD2H * rowSize + rowSize, "tSD2H", s.TPOSD2HBreakTime);
                if (s.TPOSD2L > 0) add(s.TPOSD2L * rowSize,           "tSD2L", s.TPOSD2LBreakTime);
                if (s.TPOSD3H > 0) add(s.TPOSD3H * rowSize + rowSize, "tSD3H", s.TPOSD3HBreakTime);
                if (s.TPOSD3L > 0) add(s.TPOSD3L * rowSize,           "tSD3L", s.TPOSD3LBreakTime);
                if (s.SessionHigh > 0 && s.SessionHigh != double.MinValue) add(s.SessionHigh, "HI", s.HighBreakTime);
                if (s.SessionLow  > 0 && s.SessionLow  != double.MaxValue) add(s.SessionLow,  "LO", s.LowBreakTime);
            }
            return list;
        }

        /// <summary>Key levels of the session being built right now (used for targets).</summary>
        private List<SigLevel> GetCurrentLevels()
        {
            var list = new List<SigLevel>();
            var s = currentSession;
            if (s == null) return list;
            Action<double, string> add = (price, nm) =>
            {
                if (price > 0) list.Add(new SigLevel { Price = price, Name = nm, BreakTime = DateTime.MinValue });
            };
            if (s.VPOCLevel > 0) add(s.VPOCLevel * rowSize + rowSize * 0.5, "VPOC");
            if (s.VVAH > 0) add(s.VVAH * rowSize + rowSize, "VAH");
            if (s.VVAL > 0) add(s.VVAL * rowSize, "VAL");
            if (s.SD2H > 0) add(s.SD2H * rowSize + rowSize, "SD2H");
            if (s.SD2L > 0) add(s.SD2L * rowSize, "SD2L");
            if (s.SD3H > 0) add(s.SD3H * rowSize + rowSize, "SD3H");
            if (s.SD3L > 0) add(s.SD3L * rowSize, "SD3L");
            if (s.POCLevel > 0) add(s.POCLevel * rowSize + rowSize * 0.5, "tPOC");
            if (s.TPOVAH > 0) add(s.TPOVAH * rowSize + rowSize, "tVAH");
            if (s.TPOVAL > 0) add(s.TPOVAL * rowSize, "tVAL");
            if (s.TPOSD2H > 0) add(s.TPOSD2H * rowSize + rowSize, "tSD2H");
            if (s.TPOSD2L > 0) add(s.TPOSD2L * rowSize, "tSD2L");
            if (s.TPOSD3H > 0) add(s.TPOSD3H * rowSize + rowSize, "tSD3H");
            if (s.TPOSD3L > 0) add(s.TPOSD3L * rowSize, "tSD3L");
            if (s.IBComplete && s.IBHigh > 0) add(IBHighPx(s), "IBH");
            if (s.IBComplete && s.IBLow  > 0) add(IBLowPx(s),  "IBL");
            if (s.SessionHigh > 0 && s.SessionHigh != double.MinValue) add(s.SessionHigh, "HOD");
            if (s.SessionLow  > 0 && s.SessionLow  != double.MaxValue) add(s.SessionLow,  "LOD");
            if (s.VolumeAtPrice.Count >= 5)
            {
                long totV = 0;
                foreach (var v in s.VolumeAtPrice.Values) totV += v;
                double hvnThr = (double)totV / s.VolumeAtPrice.Count * 1.3;
                foreach (var kv in s.VolumeAtPrice)
                {
                    if (kv.Value < hvnThr) continue;
                    long up = 0, dn = 0;
                    s.VolumeAtPrice.TryGetValue(kv.Key + 1, out up);
                    s.VolumeAtPrice.TryGetValue(kv.Key - 1, out dn);
                    if (kv.Value >= up && kv.Value >= dn) add((kv.Key + 0.5) * rowSize, "HVN");
                }
            }
            if (s.SinglePrintZones != null)
                foreach (var z in s.SinglePrintZones)
                {
                    add(z.LowLevel * rowSize, "SPlo");
                    add(z.HighLevel * rowSize + rowSize, "SPhi");
                }
            if (s.HasPoorHigh && s.PoorHighPrice > 0) add(s.PoorHighPrice, "poorH");
            if (s.HasPoorLow  && s.PoorLowPrice  > 0) add(s.PoorLowPrice,  "poorL");
            return list;
        }

        // ------------------------------------------------------------------
        //  Evaluation driver
        // ------------------------------------------------------------------

        private void EvaluateSignalsPending()
        {
            // Legacy closed-bar signal scan removed — MR + VA rejection only.
            return;
        }


        private TimeSpan GetSignalEndTime()
        {
            int hh = Math.Max(0, Math.Min(23, SignalEndHHMM / 100));
            int mm = Math.Max(0, Math.Min(59, SignalEndHHMM % 100));
            return new TimeSpan(hh, mm, 0);
        }

        private TimeSpan GetForceFlatTime()
        {
            if (AutoTradeForceFlatHHMM <= 0) return TimeSpan.Zero;
            int hh = Math.Max(0, Math.Min(23, AutoTradeForceFlatHHMM / 100));
            int mm = Math.Max(0, Math.Min(59, AutoTradeForceFlatHHMM % 100));
            return new TimeSpan(hh, mm, 0);
        }

        /// <summary>Force-flat clock time for log text (template-relative when Minutes Before Close is set).</summary>
        private string GetForceFlatLabel()
        {
            if (AutoTradeForceFlatMinutesBeforeClose > 0 && templateDayEnd > DateTime.MinValue)
                return templateDayEnd.AddMinutes(-AutoTradeForceFlatMinutesBeforeClose).TimeOfDay.ToString(@"hh\:mm");
            return GetForceFlatTime().ToString(@"hh\:mm");
        }

        /// <summary>Chart/tick time used for signal-end and force-flat (prefers last tick, else bar time).</summary>
        private DateTime GetChartNow()
        {
            if (lastTickTimeSeen != DateTime.MinValue) return lastTickTimeSeen;
            try { if (Times != null && Times[0].Count > 0) return Times[0][0]; } catch { }
            try { if (BarsArray != null && BarsArray[0] != null && CurrentBars[0] >= 0) return BarsArray[0].GetTime(CurrentBars[0]); } catch { }
            return DateTime.MinValue;
        }

        /// <summary>True when chart time is at/after Last Signal Time (SignalEndHHMM). 0 HHMM = off.</summary>
        private bool IsPastSignalEndTime()
        {
            if (SignalEndHHMM <= 0) return false;
            DateTime now = GetChartNow();
            if (now == DateTime.MinValue) return false;
            return now.TimeOfDay >= GetSignalEndTime();
        }

        /// <summary>True when chart time is at/after Force Flat Time. 0 = off.</summary>
        private bool IsPastForceFlatTime()
        {
            DateTime now = GetChartNow();
            if (now == DateTime.MinValue) return false;

            // Minutes-before-close mode follows the trading-hours template (works for HE / LE and overnight products).
            if (AutoTradeForceFlatMinutesBeforeClose > 0
                && templateDayBegin != DateTime.MinValue && templateDayEnd > templateDayBegin
                && now >= templateDayBegin)
                return now >= templateDayEnd.AddMinutes(-AutoTradeForceFlatMinutesBeforeClose);

            if (AutoTradeForceFlatHHMM <= 0) return false;
            TimeSpan ff = GetForceFlatTime();

            // Safety: a clock time AFTER this contract's close would never fire and leave a position open
            // (e.g. 15:00 on a market that closes 13:05). Flatten shortly before the close instead.
            if (templateDayBegin != DateTime.MinValue && templateDayEnd > templateDayBegin
                && now >= templateDayBegin && templateDayEnd.Date == templateDayBegin.Date
                && ff > templateDayEnd.TimeOfDay)
            {
                if (!forceFlatClampLogged)
                {
                    forceFlatClampLogged = true;
                    Print("SessionTPOVolumeProfile: Force Flat Time " + ff.ToString(@"hh\:mm") + " is after this session's close "
                        + templateDayEnd.TimeOfDay.ToString(@"hh\:mm") + " - flattening 5 min before the close instead.");
                }
                return now >= templateDayEnd.AddMinutes(-5);
            }
            return now.TimeOfDay >= ff;
        }

        /// <summary>No new auto entries after Last Signal Time or Force Flat Time.</summary>
        /// <summary>
        /// Earliest chart time-of-day for MR / VA signals and auto-trade entries:
        /// RTH Start Time + AutoTradeStartMinutesAfterRth.
        /// </summary>
        private TimeSpan GetAutoTradeStartTimeOfDay()
        {
            int mins = Math.Max(0, AutoTradeStartMinutesAfterRth);
            return RTHStartTime.Add(TimeSpan.FromMinutes(mins));
        }

        /// <summary>
        /// True while chart time is still before the configured auto-trade start
        /// (RTH Start + Auto-Trade Start After RTH minutes).
        /// </summary>
        private bool IsBeforeAutoTradeStart(out string reason)
        {
            reason = "";
            if (AutoTradeStartMinutesAfterRth <= 0)
                return false;
            DateTime now = GetChartNow();
            if (now == DateTime.MinValue)
            {
                reason = "no chart time yet";
                return true; // fail closed until we have time
            }
            DateTime rthOpenDt = GetRthStartDt(now);
            DateTime startDt   = rthOpenDt.AddMinutes(Math.Max(0, AutoTradeStartMinutesAfterRth));
            // Only gate during the RTH day portion after the calendar open window starts
            if (now < rthOpenDt)
            {
                reason = "before RTH Start " + rthOpenDt.TimeOfDay.ToString(@"hh\:mm");
                return true;
            }
            if (now < startDt)
            {
                reason = "auto-trade starts at " + startDt.TimeOfDay.ToString(@"hh\:mm")
                    + " (RTH " + rthOpenDt.TimeOfDay.ToString(@"hh\:mm")
                    + " + " + AutoTradeStartMinutesAfterRth + "m)";
                return true;
            }
            return false;
        }

        private bool IsAutoTradeEntryWindowClosed(out string reason)
        {
            string startReason;
            if (IsBeforeAutoTradeStart(out startReason))
            {
                reason = startReason;
                return true;
            }

            reason = "";
            if (IsPastForceFlatTime())
            {
                reason = "past Force Flat Time " + GetForceFlatLabel();
                return true;
            }
            if (IsPastSignalEndTime())
            {
                reason = "past Last Signal Time " + GetSignalEndTime().ToString(@"hh\:mm");
                return true;
            }
            return false;
        }

        /// <summary>Once per chart day: flatten open position at/after Force Flat Time.</summary>
        private void CheckAutoTradeForceFlat()
        {
            if (!EnableAutoTrade || (AutoTradeForceFlatHHMM <= 0 && AutoTradeForceFlatMinutesBeforeClose <= 0)) return;
            if (State != State.Realtime) return;
            if (!IsPastForceFlatTime()) return;

            DateTime now = GetChartNow();
            if (now == DateTime.MinValue) return;
            DateTime day = now.Date;
            if (autoTradeForceFlatDoneDate == day) return;

            int dir = ResolveLivePositionDir();
            if (dir == 0)
            {
                autoTradeForceFlatDoneDate = day;
                return;
            }

            autoTradeForceFlatDoneDate = day;
            Print("SessionTPOVolumeProfile auto-trade FORCE FLAT at "
                + now.TimeOfDay.ToString(@"hh\:mm\:ss")
                + " (configured " + GetForceFlatLabel() + ")  side="
                + (dir > 0 ? "LONG" : "SHORT"));
            FlattenManagedPosition(dir, "force-flat " + GetForceFlatLabel());
        }

        private void EvaluateSignalsAt(int idx)
        {
            // Legacy multi-setup signals (FB/EX/IB/IF/etc.) removed — MR + VA rejection only.
            return;
        }


        // ------------------------------------------------------------------
        //  Outcome tracking (stop checked before targets within a bar = conservative)
        // ------------------------------------------------------------------

        private void UpdateSignalOutcomes(SigCtx x, TimeSpan endT)
        {
            lock (signalLock)
            {
                for (int i = 0; i < signals.Count; i++)
                {
                    var s = signals[i];
                    if (s.EndBar >= 0 || s.BarIndex >= x.idx) continue;

                    if (currentSession != null && s.SessionKey != currentSession.SessionDate)
                    {
                        s.EndBar = Math.Max(s.BarIndex, x.idx - 1);
                        continue;
                    }

                    if (s.Dir > 0)
                    {
                        if (x.l <= s.Stop) { s.StopHitBar = x.idx; s.EndBar = x.idx; continue; }
                        for (int k = 0; k < s.TPCount; k++)
                            if (s.TPHitBar[k] < 0 && x.h >= s.TP[k]) s.TPHitBar[k] = x.idx;
                    }
                    else
                    {
                        if (x.h >= s.Stop) { s.StopHitBar = x.idx; s.EndBar = x.idx; continue; }
                        for (int k = 0; k < s.TPCount; k++)
                            if (s.TPHitBar[k] < 0 && x.l <= s.TP[k]) s.TPHitBar[k] = x.idx;
                    }

                    if (s.TPCount > 0 && s.TPHitBar[s.TPCount - 1] >= 0) s.EndBar = x.idx;
                    else if (x.t.TimeOfDay > endT) s.EndBar = x.idx;   // time stop
                }
            }
        }

        // ------------------------------------------------------------------
        //  Targets, grading, emission
        // ------------------------------------------------------------------

        /// <summary>
        /// Auction-market structure targets (volume + TPO + unfinished auction).
        /// Rank by auction role, not raw distance:
        ///   0 POC / balance magnet
        ///   1 VA edges (developing value)
        ///   2 Unfinished auction (single prints, poor H/L, HVN)
        ///   3 Initial balance
        ///   4 Prior value (opposite side of prior session value)
        ///   5 Session extremes (stretch only)
        /// TP1 is capped (2.0 / 0) so short-TF trades
        /// do not inherit prior-day highs as the first objective.
        /// </summary>
        private int BuildTargets(int dir, double entry, double risk, double[] tp, string[] names,
                                 double forcedPrice, string forcedName, bool fillRMultiples = false)
        {
            double minDist = Math.Max(12 * TickSize, 0.0 * risk);
            double spacing = 8 * TickSize;

            // TP1 reach cap
            double atrv = 0;
            try
            {
                if (atrIndicator != null && CurrentBars[0] >= 14)
                    atrv = atrIndicator[0];
            }
            catch { }
            double maxTp1 = double.MaxValue;
            if (2.0 > 0 && atrv > 0)
                maxTp1 = Math.Min(maxTp1, 2.0 * atrv);
            if (0 > 0 && TickSize > 0)
                maxTp1 = Math.Min(maxTp1, 0 * TickSize);
            if (maxTp1 >= double.MaxValue / 2)
                maxTp1 = Math.Max(minDist * 4, risk * 2); // fallback if no ATR yet

            // Developing value (volume VA preferred; TPO VA fallback)
            double vaLow = double.NaN, vaHigh = double.NaN;
            bool insideValue = false;
            if (currentSession != null && rowSize > 0)
            {
                var s = currentSession;
                if (s.VVAL > 0 && s.VVAH > 0)
                {
                    vaLow = s.VVAL * rowSize;
                    vaHigh = s.VVAH * rowSize + rowSize;
                }
                else if (s.TPOVAL > 0 && s.TPOVAH > 0)
                {
                    vaLow = s.TPOVAL * rowSize;
                    vaHigh = s.TPOVAH * rowSize + rowSize;
                }
                if (!double.IsNaN(vaLow) && !double.IsNaN(vaHigh) && vaHigh > vaLow)
                    insideValue = entry >= vaLow && entry <= vaHigh;
            }

            var cand = new List<SigLevel>();
            cand.AddRange(GetCurrentLevels());
            DateTime sessStart = currentSession != null ? currentSession.StartTime : DateTime.MinValue;
            foreach (var L in GetPriorLevels())
                if (L.Recent || L.BreakTime == DateTime.MinValue || L.BreakTime >= sessStart)
                    cand.Add(L);

            // Score each candidate: (bucket, distance)
            var scored = new List<Tuple<int, double, SigLevel>>();
            foreach (var L in cand)
            {
                double dist = (L.Price - entry) * dir;
                if (dist < minDist) continue;

                int bucket = AuctionTargetBucket(L.Name, dir, entry, vaLow, vaHigh, insideValue);
                // Inside value: suppress session extremes for early slots (still available as stretch)
                if (insideValue && bucket >= 5)
                    bucket = 6; // after everything else

                scored.Add(Tuple.Create(bucket, dist, L));
            }

            scored.Sort((a, b) =>
            {
                int c = a.Item1.CompareTo(b.Item1);
                if (c != 0) return c;
                return a.Item2.CompareTo(b.Item2);
            });

            int n = 0;
            foreach (var item in scored)
            {
                if (n >= 3) break;
                double dist = item.Item2;
                var L = item.Item3;

                // TP1 must sit inside the short-TF cap
                if (n == 0 && dist > maxTp1)
                    continue;
                // TP2/TP3 may extend further (2x / 3x cap) but still prefer nearer auction levels via sort
                if (n == 1 && dist > maxTp1 * 2.5)
                    continue;
                if (n == 2 && dist > maxTp1 * 4.0)
                    continue;

                if (n > 0 && Math.Abs(L.Price - tp[n - 1]) < spacing)
                {
                    if (IsTpoLevelName(L.Name) && !IsTpoLevelName(names[n - 1]))
                    {
                        tp[n - 1] = Instrument.MasterInstrument.RoundToTickSize(L.Price);
                        names[n - 1] = L.Name;
                    }
                    continue;
                }

                tp[n] = Instrument.MasterInstrument.RoundToTickSize(L.Price);
                names[n] = L.Name;
                n++;
            }

            // Optional R-multiple fill (structure mode keeps this false)
            if (fillRMultiples)
            {
                int m = 1;
                while (n < 3 && m <= 15)
                {
                    double p = entry + dir * m * risk;
                    bool beyondDist = Math.Abs(p - entry) >= minDist;
                    bool beyondLast = n == 0 || ((dir > 0 ? p > tp[n - 1] : p < tp[n - 1]) && Math.Abs(p - tp[n - 1]) >= spacing);
                    if (beyondDist && beyondLast)
                    {
                        tp[n] = Instrument.MasterInstrument.RoundToTickSize(p);
                        names[n] = m + "R";
                        n++;
                    }
                    m++;
                }
            }

            // Setup-mandated far target (80% rule)
            if (forcedPrice > 0)
            {
                bool valid = dir > 0 ? forcedPrice >= entry + minDist : forcedPrice <= entry - minDist;
                bool dup = false;
                for (int i = 0; i < n; i++)
                    if (Math.Abs(tp[i] - forcedPrice) < spacing) dup = true;
                if (valid && !dup)
                {
                    int slot = n < 3 ? n : 2;
                    tp[slot] = Instrument.MasterInstrument.RoundToTickSize(forcedPrice);
                    names[slot] = forcedName;
                    if (n < 3) n++;
                    for (int i = 0; i < n; i++)
                        for (int j = i + 1; j < n; j++)
                            if (Math.Abs(tp[j] - entry) < Math.Abs(tp[i] - entry))
                            {
                                double tpp = tp[i]; tp[i] = tp[j]; tp[j] = tpp;
                                string tn = names[i]; names[i] = names[j]; names[j] = tn;
                            }
                }
            }

            // Only TP1 is used — drop TP2 / TP3 (chart lines, hit tracking and signal end-bar all key off TPCount)
            if (n > 1) n = 1;
            return n;
        }

        /// <summary>
        /// Auction role for a structure level name. Lower = higher priority target.
        /// </summary>
        private static int AuctionTargetBucket(string name, int dir, double entry,
                                               double vaLow, double vaHigh, bool insideValue)
        {
            if (string.IsNullOrEmpty(name)) return 5;
            string n = name;

            // Strip prior-session prefix (p / n)
            if (n.Length > 1 && (n[0] == 'p' || n[0] == 'n') && char.IsUpper(n[1]))
                n = n.Substring(1);
            else if (n.StartsWith("p", StringComparison.Ordinal) || n.StartsWith("n", StringComparison.Ordinal))
            {
                // pVPOC, nVAH, pHI, ...
                if (n.Length > 1) n = n.Substring(1);
            }

            string u = n.ToUpperInvariant();

            // 0 — POC / balance magnet
            if (u == "VPOC" || u == "TPOC" || u == "POC")
                return 0;

            // 1 — value area edges (volume or TPO); far edge preferred when inside value
            if (u == "VAH" || u == "VAL" || u == "TVAH" || u == "TVAL"
                || u == "SD2H" || u == "SD2L" || u == "SD3H" || u == "SD3L"
                || u == "TSD2H" || u == "TSD2L" || u == "TSD3H" || u == "TSD3L")
                return 1;

            // 2 — unfinished auction
            if (u.StartsWith("SP") || u == "POORH" || u == "POORL" || u == "HVN"
                || u == "POOR HIGH" || u == "POOR LOW")
                return 2;
            if (name.IndexOf("poor", StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            if (name.IndexOf("SP", StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;

            // 3 — initial balance
            if (u == "IBH" || u == "IBL")
                return 3;

            // 4 — prior value (already stripped prefix; names like VAH from prior list stay bucket 1
            //     unless we detect prior-only tags). Prior HI/LO handled below.
            // Prior session value levels still named VAH/VAL after strip → bucket 1 is correct
            // for "other side of value". Extremes:

            // 5 — session extremes (stretch)
            if (u == "HOD" || u == "LOD" || u == "HI" || u == "LO"
                || u == "HIGH" || u == "LOW")
                return 5;

            return 4; // other named structure
        }

        // ------------------------------------------------------------------
        //  Footprint / profile helpers
        // ------------------------------------------------------------------

        private static void AddBarLevelVol(Dictionary<int, Dictionary<int, long>> d, int bar, int lvl, long v)
        {
            Dictionary<int, long> row;
            if (!d.TryGetValue(bar, out row))
            {
                row = new Dictionary<int, long>();
                d[bar] = row;
                d.Remove(bar - 4000);      // keep memory bounded
            }
            long cur;
            row.TryGetValue(lvl, out cur);
            row[lvl] = cur + v;
        }

        /// <summary>
        /// Convert price to a stable volume-profile row index using integer tick math.
        /// Prevents skipped/duplicate rows on 0.025-tick instruments (HE/LE) that occur
        /// with Floor(price / rowSize) under floating-point noise.
        /// When TicksPerRow > 1, adjacent ticks are intentionally grouped into one row.
        /// </summary>
        // Exact IB high / low prices; falls back to the row edges if exact price wasn't captured.
        private double IBHighPx(SessionProfile sp)
        {
            return sp.IBHighPrice > 0 ? sp.IBHighPrice : sp.IBHigh * rowSize + rowSize;
        }
        private double IBLowPx(SessionProfile sp)
        {
            return sp.IBLowPrice > 0 ? sp.IBLowPrice : sp.IBLow * rowSize;
        }

        private int LevelOf(double price)
        {
            if (TickSize <= 0)
                return 0;

            double rounded = Instrument != null
                ? Instrument.MasterInstrument.RoundToTickSize(price)
                : Math.Round(price / TickSize) * TickSize;

            long tickIndex = (long)Math.Round(rounded / TickSize);
            int tpr = TicksPerRow > 0 ? TicksPerRow : 1;

            if (tickIndex >= 0)
                return (int)(tickIndex / tpr);

            return (int)((tickIndex - (tpr - 1)) / tpr);
        }

        /// <summary>Bottom (lowest) price of a profile row bucket.</summary>
        private double LevelToPrice(int level)
        {
            int tpr = TicksPerRow > 0 ? TicksPerRow : 1;
            return level * tpr * TickSize;
        }

        /// <summary>
        /// Chart Y bounds for a profile row, centered on the price so rows line up with
        /// SuperDOM / ladder price levels.
        /// TicksPerRow = 1: centered on the exact tick price (half-tick above and below).
        /// TicksPerRow &gt; 1: centered on the mid-price of the multi-tick bucket.
        /// </summary>
        // Draws a sliding-window total as measured text with a white outline box.
        // align: 0 = anchorX is the left edge (buy side), 1 = anchorX is the right edge (sell side),
        //        2 = anchorX is the horizontal center (delta).
        // edgeY = the line the box sits against: its bottom if above, its top if below.
        // Returns the outer height of the box so callers can stack another box beyond it.
        private float DrawWindowTotal(string text, SharpDX.DirectWrite.TextFormat fmt, float anchorX,
            int align, float edgeY, bool above,
            SharpDX.Direct2D1.Brush textB, SharpDX.Direct2D1.Brush boxB)
        {
            if (fmt == null || textB == null || boxB == null) return 0f;
            using (var layout = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory, text, fmt, 1000f, 1000f))
            {
                const float pad = 2f;
                float w = layout.Metrics.Width;
                float h = layout.Metrics.Height;
                float left = (align == 1) ? anchorX - w : (align == 2) ? anchorX - w * 0.5f : anchorX;
                float top  = above ? edgeY - h - pad : edgeY + pad;
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(left, top), layout, textB);
                RenderTarget.DrawRectangle(
                    new SharpDX.RectangleF(left - pad, top - pad, w + 2f * pad, h + 2f * pad), boxB, 1f);
                return h + 2f * pad;
            }
        }

        private void GetLevelYBounds(ChartScale chartScale, int level, out float yTop, out float yBottom)
        {
            double lo = LevelToPrice(level);
            // TPR=1 → mid = exact trade price (matches ladder row). Multi-tick → mid of bucket.
            double mid = (TicksPerRow > 1) ? (lo + rowSize * 0.5) : lo;
            double half = rowSize * 0.5;
            // Higher price → smaller Y on NT charts
            yTop    = chartScale.GetYByValue(mid + half);
            yBottom = chartScale.GetYByValue(mid - half);
        }

        private long BarAskAt(int bar, int lvl)
        {
            Dictionary<int, long> r; long v;
            return (barAskAtPrice.TryGetValue(bar, out r) && r.TryGetValue(lvl, out v)) ? v : 0;
        }

        private long BarBidAt(int bar, int lvl)
        {
            Dictionary<int, long> r; long v;
            return (barBidAtPrice.TryGetValue(bar, out r) && r.TryGetValue(lvl, out v)) ? v : 0;
        }

        /// <summary>Longest run of consecutive price rows with a diagonal imbalance in the trade direction.</summary>
        private int StackedImbalanceRows(int bar, int dir)
        {
            Dictionary<int, long> ar, br;
            barAskAtPrice.TryGetValue(bar, out ar);
            barBidAtPrice.TryGetValue(bar, out br);
            if (ar == null && br == null) return 0;

            int lo = int.MaxValue, hi = int.MinValue;
            if (ar != null) foreach (var k in ar.Keys) { if (k < lo) lo = k; if (k > hi) hi = k; }
            if (br != null) foreach (var k in br.Keys) { if (k < lo) lo = k; if (k > hi) hi = k; }
            if (lo > hi) return 0;

            double ratio = DiagonalImbalanceRatio;
            long minVol = MinImbalanceVolume;
            int run = 0, best = 0;
            for (int l = lo; l <= hi; l++)
            {
                bool imb;
                if (dir > 0)
                {
                    long a = BarAskAt(bar, l), bBelow = BarBidAt(bar, l - 1);
                    imb = a >= minVol && a >= ratio * Math.Max(1L, bBelow);
                }
                else
                {
                    long b = BarBidAt(bar, l), aAbove = BarAskAt(bar, l + 1);
                    imb = b >= minVol && b >= ratio * Math.Max(1L, aAbove);
                }
                if (imb) { run++; if (run > best) best = run; } else run = 0;
            }
            return best;
        }

        private double AvgRowVolume(SessionProfile sp)
        {
            if (sp == null || sp.VolumeAtPrice.Count == 0) return 0;
            long tot = 0;
            foreach (var v in sp.VolumeAtPrice.Values) tot += v;
            return (double)tot / sp.VolumeAtPrice.Count;
        }

        private Dictionary<int, HashSet<char>> ActiveTPOMap(SessionProfile sp)
        {
            if (sp == null) return null;
            if (SplitGlobexRthTPO && sp.RthTPOsAtPrice != null && sp.RthTPOsAtPrice.Count > 0)
                return sp.RthTPOsAtPrice;
            return sp.TPOsAtPrice;
        }

        /// <summary>TPO-weighted skewness (same sign convention as the volume skewness: + = bullish lean).</summary>
        private double ComputeTPOSkewness(Dictionary<int, HashSet<char>> map)
        {
            if (map == null || map.Count < 4) return double.NaN;
            int tot = 0; double wsum = 0;
            foreach (var kv in map) { int c = kv.Value.Count; tot += c; wsum += (double)kv.Key * c; }
            if (tot == 0) return double.NaN;
            double mean = wsum / tot, var2 = 0, sk = 0;
            foreach (var kv in map)
            {
                double d = kv.Key - mean; int c = kv.Value.Count;
                var2 += d * d * c; sk += d * d * d * c;
            }
            var2 /= tot;
            double sd = Math.Sqrt(var2);
            if (sd < 1e-9) return 0;
            return (sk / tot) / (sd * sd * sd);
        }

        /// <summary>
        /// Walks away from the trade direction from the level and returns the outer edge of the
        /// TPO body (rows with 2+ letters) — beyond it the profile tapers into single prints / thin tail.
        /// The stop belongs just beyond that edge. Falls back to the volume taper if no TPO data.
        /// </summary>
        private double FindTaperPrice(int dir, double levelPrice)
        {
            var sp  = currentSession;
            var map = ActiveTPOMap(sp);
            int lastThick = LevelOf(levelPrice);

            if (map != null && map.Count >= 4)
            {
                double avgCnt = 0;
                foreach (var h in map.Values) avgCnt += h.Count;
                avgCnt /= map.Count;
                double thin = Math.Max(1.5, avgCnt * 0.4);
                for (int i = 0; i < 60; i++)
                {
                    int nxt = lastThick - dir;        // buy: walk down, sell: walk up
                    HashSet<char> letters;
                    if (!map.TryGetValue(nxt, out letters) || letters.Count < thin) break;
                    lastThick = nxt;
                }
                return dir > 0 ? lastThick * rowSize : (lastThick + 1) * rowSize;
            }

            double avg = AvgRowVolume(sp);
            if (avg <= 0) return levelPrice;
            double thinV = avg * 0.5;
            for (int i = 0; i < 60; i++)
            {
                int nxt = lastThick - dir;
                long v;
                if (!sp.VolumeAtPrice.TryGetValue(nxt, out v) || v < thinV) break;
                lastThick = nxt;
            }
            return dir > 0 ? lastThick * rowSize : (lastThick + 1) * rowSize;
        }

        // ------------------------------------------------------------------
        //  Confirmation layer
        // ------------------------------------------------------------------

        // F — bid/ask flow at the level: aggression absorbed there, or stacked imbalances in our direction
        private bool CheckFlowAtLevel(SigCtx x, int dir, double levelPrice)
        {
            int lv  = LevelOf(levelPrice);
            int pad = Math.Max(1, (int)Math.Ceiling(2 * TickSize / rowSize));

            long zAsk = 0, zBid = 0;
            for (int b = x.idx - 1; b <= x.idx; b++)
                for (int l = lv - pad; l <= lv + pad; l++)
                { zAsk += BarAskAt(b, l); zBid += BarBidAt(b, l); }

            long sA = 0, sB = 0;
            primaryBarAskVol.TryGetValue(x.idx, out sA);
            primaryBarBidVol.TryGetValue(x.idx, out sB);
            long barTot = sA + sB;
            long zTot   = zAsk + zBid;

            if (zTot > 0 && barTot > 0 && zTot >= 0.10 * barTot)
            {
                double counterShare = dir > 0 ? (double)zBid / zTot : (double)zAsk / zTot;
                bool held = dir > 0 ? x.c > levelPrice : x.c < levelPrice;
                if (counterShare >= 0.55 && held) return true;      // aggression absorbed, price held
            }
            return StackedImbalanceRows(x.idx, dir) >= 2;           // stacked diagonal imbalances with us
        }

        // D — delta + volume participation on the signal bar (or a delta flip against the prior bar)
        private bool CheckParticipation(SigCtx x, int dir)
        {
            long a = 0, b = 0;
            primaryBarAskVol.TryGetValue(x.idx, out a);
            primaryBarBidVol.TryGetValue(x.idx, out b);
            long vol = a + b;
            if (vol <= 0) return false;

            double sum = 0; int n = 0;
            for (int k = 1; k <= 20; k++)
            {
                int bb = x.idx - k;
                if (bb < 0) break;
                long aa = 0, bv = 0;
                primaryBarAskVol.TryGetValue(bb, out aa);
                primaryBarBidVol.TryGetValue(bb, out bv);
                if (aa + bv > 0) { sum += aa + bv; n++; }
            }
            double avg = n > 0 ? sum / n : vol;

            double delta = (double)(a - b) / vol;
            long pa = 0, pb2 = 0;
            primaryBarAskVol.TryGetValue(x.idx - 1, out pa);
            primaryBarBidVol.TryGetValue(x.idx - 1, out pb2);
            double pdelta = (pa + pb2) > 0 ? (double)(pa - pb2) / (pa + pb2) : 0;

            bool volOK    = vol >= avg;
            bool aligned  = delta * dir >= 0.10;
            bool flip     = pdelta * dir <= -0.05 && delta * dir > 0;
            return volOK && (aligned || flip);
        }

        // ===== TPO checks (primary) =====

        // L — TPO location: where price sits against the TPO value area / POC
        private bool CheckTPOLocation(int dir, bool fadeFit, double entry)
        {
            var sp = currentSession;
            if (sp.POCLevel <= 0 || sp.TPOVAH <= 0 || sp.TPOVAL <= 0) return false;
            double poc = sp.POCLevel * rowSize + rowSize * 0.5;
            double vah = sp.TPOVAH * rowSize + rowSize;
            double val = sp.TPOVAL * rowSize;
            double span = Math.Max(vah - val, rowSize);

            if (fadeFit)
            {
                // fade from the edge of (or outside) the TPO value area with the TPO POC still ahead
                bool pocAhead = (poc - entry) * dir >= 12 * TickSize;
                bool atEdge   = dir > 0 ? entry <= val + 0.35 * span : entry >= vah - 0.35 * span;
                return pocAhead && atEdge;
            }
            // continuation: accepted beyond the TPO value area in our direction, POC behind us
            bool beyond = dir > 0 ? entry >= vah : entry <= val;
            return beyond && (entry - poc) * dir > 0;
        }

        // S — TPO shape: skew / P-b shape lean our way (his rule: skew owns direction)
        //     AND kurtosis fits the trade type (balanced/compact → fades, wide/trending → continuation)
        private bool CheckTPOShape(int dir, bool fadeFit)
        {
            var sp  = currentSession;
            var map = ActiveTPOMap(sp);

            bool lean = false;
            double skew = ComputeTPOSkewness(map);
            if (!double.IsNaN(skew) && Math.Abs(skew) >= 0.10 && Math.Sign(skew) == dir) lean = true;
            if (sp.Classification == DayType.PShape && dir > 0) lean = true;
            if (sp.Classification == DayType.BShape && dir < 0) lean = true;
            if (!lean) return false;

            double kurt = ComputeTPOKurtosis(sp);
            if (!double.IsNaN(kurt))
                return fadeFit ? kurt >= 0 : kurt < 0;

            var dt = sp.Classification;
            if (fadeFit)
                return dt == DayType.Normal || dt == DayType.NormalVariation || dt == DayType.Neutral
                    || dt == DayType.DoubleDistribution;
            return dt == DayType.Trend || dt == DayType.PShape || dt == DayType.BShape;
        }

        private bool NearTPOLevel(double price, double tol)
        {
            foreach (var L in GetCurrentLevels())
            {
                string n = L.Name;
                bool tpoRef = n.StartsWith("t") || n.StartsWith("IB") || n.StartsWith("poor")
                           || n == "HOD" || n == "LOD" || n.StartsWith("SP");
                if (tpoRef && Math.Abs(L.Price - price) <= tol) return true;
            }
            foreach (var L in GetPriorLevels())
            {
                string n = L.Name.Length > 1 ? L.Name.Substring(1) : L.Name;
                bool tpoRef = n.StartsWith("t") || n == "HI" || n == "LO";
                if (tpoRef && Math.Abs(L.Price - price) <= tol) return true;
            }
            return false;
        }

        // X — TPO structure at the level: excess / poor high-low / a TPO reference level / single prints
        private bool CheckTPOStructure(int dir, bool fadeFit, double levelPrice, double entry)
        {
            var sp  = currentSession;
            double row = rowSize;
            double tol = 2 * TickSize + row;

            if (fadeFit)
            {
                if (dir > 0 && sp.SessionLow > 0 && sp.SessionLow != double.MaxValue
                    && Math.Abs(levelPrice - sp.SessionLow) <= 3 * row)
                {
                    if (sp.HasPoorLow)   return false;   // unfinished business below: likely revisited
                    if (sp.HasExcessLow) return true;    // clean rejection tail
                }
                if (dir < 0 && sp.SessionHigh > 0 && sp.SessionHigh != double.MinValue
                    && Math.Abs(levelPrice - sp.SessionHigh) <= 3 * row)
                {
                    if (sp.HasPoorHigh)   return false;
                    if (sp.HasExcessHigh) return true;
                }
                return NearTPOLevel(levelPrice, tol);
            }

            // continuation: initiative single prints left behind us, or unfinished business ahead (poor extreme = magnet)
            bool poorAhead = dir > 0 ? sp.HasPoorHigh : sp.HasPoorLow;
            if (poorAhead) return true;
            if (sp.SinglePrintZones != null)
            {
                foreach (var z in sp.SinglePrintZones)
                {
                    double zHi = z.HighLevel * row + row, zLo = z.LowLevel * row;
                    if (dir > 0 && zHi <= entry) return true;
                    if (dir < 0 && zLo >= entry) return true;
                }
            }
            return false;
        }

        // ===== Volume / order-flow checks (supplement) =====

        // v — volume nodes: a magnet (HVN) ahead with room for fades; open air (no HVN wall within 1R) for continuation
        private bool CheckNodeRoom(int dir, bool fadeFit, double entry, double risk)
        {
            var sp = currentSession;
            if (sp.VolumeAtPrice.Count < 5) return false;
            double hvn = AvgRowVolume(sp) * 1.3;
            int minL = int.MaxValue, maxL = int.MinValue;
            foreach (var k in sp.VolumeAtPrice.Keys) { if (k < minL) minL = k; if (k > maxL) maxL = k; }

            double hvnDist = double.MaxValue;
            for (int l = LevelOf(entry) + dir; l >= minL - 1 && l <= maxL + 1; l += dir)
            {
                long v;
                if (sp.VolumeAtPrice.TryGetValue(l, out v) && v >= hvn)
                { hvnDist = Math.Abs((l + 0.5) * rowSize - entry); break; }
            }
            double needed = Math.Max(risk, 12 * TickSize);
            if (fadeFit) return hvnDist != double.MaxValue && hvnDist >= needed;
            return hvnDist == double.MaxValue || hvnDist >= needed;
        }

        private void ConfirmSignal(SigCtx x, int dir, double levelPrice, bool fadeFit,
                                   double entry, double risk,
                                   out int tpoN, out int volN, out string flags, out bool l2ok)
        {
            tpoN = 0; volN = 0;
            var t = new System.Text.StringBuilder();
            var v = new System.Text.StringBuilder();

            // TPO — primary evidence
            if (CheckTPOLocation(dir, fadeFit, entry))                 { tpoN++; t.Append('L'); }
            if (CheckTPOShape(dir, fadeFit))                           { tpoN++; t.Append('S'); }
            if (CheckTPOStructure(dir, fadeFit, levelPrice, entry))    { tpoN++; t.Append('X'); }

            // Volume / order flow — supplement
            if (CheckFlowAtLevel(x, dir, levelPrice))                  { volN++; v.Append('f'); }
            if (CheckParticipation(x, dir))                            { volN++; v.Append('d'); }
            if (CheckNodeRoom(dir, fadeFit, entry, risk))              { volN++; v.Append('v'); }

            // Live-only: the real Level-2 book agrees right now (cannot exist on history)
            l2ok = State == State.Realtime && UseLevel2Book && x.idx >= CurrentBars[0] - 1
                   && l2LastUpdateTime != DateTime.MinValue
                   && (GetMarketNow() - l2LastUpdateTime).TotalSeconds < 5.0
                   && l2BookImbalance * dir >= 0.10;

            flags = "TPO " + tpoN + "/3 " + (t.Length > 0 ? t.ToString() : "-")
                  + "  VOL " + volN + "/3 " + (v.Length > 0 ? v.ToString() : "-")
                  + (l2ok ? "  +BOOK" : "");
        }

        /// <summary>0 = inside 1σ (value area), 1 = beyond 1σ, 2 = beyond 2σ, 3 = beyond 3σ — either side of value.</summary>
        private int SigmaBand(double price, bool tpo)
        {
            var sp = currentSession;
            int h1 = tpo ? sp.TPOVAH  : sp.VVAH,  l1 = tpo ? sp.TPOVAL  : sp.VVAL;
            int h2 = tpo ? sp.TPOSD2H : sp.SD2H,  l2 = tpo ? sp.TPOSD2L : sp.SD2L;
            int h3 = tpo ? sp.TPOSD3H : sp.SD3H,  l3 = tpo ? sp.TPOSD3L : sp.SD3L;
            if (h3 > 0 && price >= h3 * rowSize + rowSize) return 3;
            if (l3 > 0 && price <= l3 * rowSize)           return 3;
            if (h2 > 0 && price >= h2 * rowSize + rowSize) return 2;
            if (l2 > 0 && price <= l2 * rowSize)           return 2;
            if (h1 > 0 && price >= h1 * rowSize + rowSize) return 1;
            if (l1 > 0 && price <= l1 * rowSize)           return 1;
            return 0;
        }

        private static readonly HashSet<string> TpoLevelNames = new HashSet<string>
        {
            "tPOC","tVAH","tVAL","tSD2H","tSD2L","tSD3H","tSD3L","IBH","IBL","poorH","poorL","SPlo","SPhi","HOD","LOD","HI","LO"
        };

        private static bool IsTpoLevelName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return TpoLevelNames.Contains(n) || (n.Length > 1 && TpoLevelNames.Contains(n.Substring(1)));
        }

        private void RejectSetup(SigCtx x, int dir, string code, string reason)
        {
            string k = x.idx + "|" + dir + "|" + code;
            if (!rejectedKeys.Add(k)) return;
            lock (signalLock)
            {
                if (rejected.Count > 3000) rejected.RemoveRange(0, 500);
                rejected.Add(new RejectedSetup { BarIndex = x.idx, Dir = dir, Setup = code, Reason = reason });
            }
        }

        /// <summary>ATR value for bar idx, falling back to the forming bar's ATR. 0 if unavailable.</summary>
        private double GetAtrOrCurrent(int idx)
        {
            double a = GetAtrAt(idx);
            if (a <= 0 && atrIndicator != null && CurrentBars[0] >= 14)
            {
                try { a = atrIndicator[0]; } catch { a = 0; }
            }
            return a;
        }

        private double GetAtrAt(int idx)
        {
            if (atrIndicator == null) return 0;
            if (CurrentBars[0] < 14) return 0;
            int barsAgo = CurrentBars[0] - idx;
            if (barsAgo < 0) barsAgo = 0;
            if (barsAgo > CurrentBars[0]) return 0;
            try
            {
                double v = atrIndicator[barsAgo];
                if (v > 0) return v;
            }
            catch { }
            try { return atrIndicator[0]; } catch { return 0; }
        }

        private int GradeBonus(SigCtx x, int dir, double levelPrice)
        {
            int pts = 0;
            double tol = TickSize * 2;
            try
            {
                foreach (var cz in BuildClusterZones())
                {
                    if (levelPrice >= cz.BottomPrice - tol && levelPrice <= cz.TopPrice + tol)
                    {
                        pts += cz.Tier >= 3 ? 2 : (cz.Tier == 2 ? 1 : 0);
                        break;
                    }
                }
            }
            catch { }
            double z = GetDeltaZScore();
            if (!double.IsNaN(z) && ((dir > 0 && z >= 1.0) || (dir < 0 && z <= -1.0))) pts++;
            return pts;
        }

        private void TryEmit(SigCtx x, int dir, string code, string detail, double levelPrice,
                             double refExtreme, string key, bool fadeFit, bool blockOnTrend,
                             double forcedPrice = 0, string forcedName = null)
        {
            // Filter: buyers/sellers in charge must agree. TP/SL are computed for display
            // (ATR or structure) — they do not block the signal.
            if (signalKeys.Contains(key)) return;
            if (!sigEvalIntrabar) diagRaw++;

            if (false)
            {
                if ((dir > 0 && x.dom < ControlGate) || (dir < 0 && x.dom > -ControlGate))
                {
                    if (!sigEvalIntrabar) diagCtrl++;
                    if (code == "FB" && State == State.Realtime && !sigEvalIntrabar)
                        Print("SessionTPOVolumeProfile FB REJECTED (control gate) " + (dir > 0 ? "BUY" : "SELL") + " " + detail
                            + "  bar " + x.idx + " " + x.t.ToString("HH:mm") + "  dom=" + x.dom.ToString("+0.00;-0.00")
                            + "  gate=" + ControlGate.ToString("0.00"));
                    return;
                }
            }

            double adom = Math.Abs(x.dom);
            int tier = adom >= 0.25 ? 1 : (adom >= ControlGate ? 2 : 3);

            double entry = x.c;
            double buf   = 4 * TickSize;

            // Stop: beyond signal extreme (+ optional profile taper)
            double stopPx = dir > 0 ? refExtreme - buf : refExtreme + buf;
            if (false && (code == "NK" || code == "SP" || code == "IB"))
            {
                double levelStop = dir > 0 ? levelPrice - buf : levelPrice + buf;
                double taper     = FindTaperPrice(dir, levelPrice);
                double taperStop = dir > 0 ? taper - buf : taper + buf;
                stopPx = dir > 0 ? Math.Min(levelStop, Math.Max(taperStop, stopPx))
                                 : Math.Max(levelStop, Math.Min(taperStop, stopPx));
            }
            // ATR stop-loss overrides the structural stop when selected and ATR is available
            if (true)
            {
                stopPx = entry - dir * StopLossTicks * TickSize;
            }
            else if (false)
            {
                double atrStop = GetAtrOrCurrent(x.idx);
                if (atrStop > 0)
                    stopPx = entry - dir * (1.0 * atrStop);
            }
            double stop = Instrument.MasterInstrument.RoundToTickSize(stopPx);
            if ((dir > 0 && stop >= entry) || (dir < 0 && stop <= entry))
                stop = Instrument.MasterInstrument.RoundToTickSize(entry - dir * Math.Max(buf, TickSize * 4));

            double risk = Math.Max(Math.Abs(entry - stop), TickSize);

            // Take-profit levels (ATR multiple or structure) — never blocks emission
            double[] tp = new double[3];
            string[] tn = new string[3];
            int n = 0;
            try
            {
                if (true)
                {
                    tp[0] = Instrument.MasterInstrument.RoundToTickSize(entry + dir * TakeProfitTicks * TickSize);
                    tn[0] = TakeProfitTicks + " ticks";
                    n = 1;
                }
                else if (false)
                {
                    double atrv = GetAtrAt(x.idx);
                    if (atrv <= 0 && atrIndicator != null && CurrentBars[0] >= 14)
                        atrv = atrIndicator[0];
                    if (atrv > 0)
                    {
                        double reach = 1.0 * atrv;
                        tp[0] = Instrument.MasterInstrument.RoundToTickSize(entry + dir * reach);
                        tn[0] = 1.0.ToString("0.##") + " ATR(" + 14 + ")";
                        n = 1;
                    }
                }
                else
                {
                    n = BuildTargets(dir, entry, risk, tp, tn, forcedPrice, forcedName, fillRMultiples: false);
                }
            }
            catch { n = 0; }

            // Merge same-bar same-direction setups into one arrow
            lock (signalLock)
            {
                for (int i = 0; i < signals.Count; i++)
                {
                    if (signals[i].BarIndex == x.idx && signals[i].Dir == dir)
                    {
                        if (code == "FB" && State == State.Realtime)
                            Print("SessionTPOVolumeProfile FB MERGED into existing " + signals[i].Setup + " signal  bar " + x.idx
                                + " " + x.t.ToString("HH:mm") + "  evaluated " + DateTime.Now.ToString("HH:mm:ss"));
                        signals[i].Setup += "+" + code;
                        // Keep the wider stop / first available targets if missing
                        if (dir > 0 && stop < signals[i].Stop) signals[i].Stop = stop;
                        if (dir < 0 && stop > signals[i].Stop) signals[i].Stop = stop;
                        if (signals[i].TPCount == 0 && n > 0)
                        {
                            signals[i].TPCount = n;
                            for (int k = 0; k < n; k++)
                            {
                                signals[i].TP[k] = tp[k];
                                signals[i].TPName[k] = tn[k];
                            }
                        }
                        signalKeys.Add(key);
                        return;
                    }
                }
            }

            var sig = new TradeSignal
            {
                BarIndex   = x.idx,
                Time       = x.t,
                SessionKey = currentSession.SessionDate,
                Dir        = dir,
                Setup      = code,
                Detail     = detail,
                Entry      = entry,
                Stop       = stop,
                TPCount    = n,
                Points     = 0,
                Grade      = "",
                Tier       = tier,
                Conf       = 0,
                Flags      = ""
            };
            for (int i = 0; i < n; i++) { sig.TP[i] = tp[i]; sig.TPName[i] = tn[i]; }

            signalKeys.Add(key);
            lock (signalLock) { signals.Add(sig); }

            if (code == "FB" && State == State.Realtime)
                Print("SessionTPOVolumeProfile FB EMIT" + (sigEvalIntrabar ? " (intrabar)" : "") + " " + (dir > 0 ? "BUY" : "SELL") + " " + detail
                    + "  bar " + x.idx + " (" + x.t.ToString("HH:mm") + ")  current bar " + CurrentBars[0]
                    + "  evaluated " + DateTime.Now.ToString("HH:mm:ss") + "  dom=" + x.dom.ToString("+0.00;-0.00"));

            if (State == State.Realtime && x.idx >= CurrentBars[0] - 1)
            {
                if (SignalAlerts)
                {
                    string msg = Instrument.FullName + (dir > 0 ? "  BUY " : "  SELL ") + code
                               + "  " + detail + "  @ " + entry.ToString("F2");
                    if (n > 0) msg += "  TP " + tp[0].ToString("F2");
                    msg += "  SL " + stop.ToString("F2");
                    Alert("STVP_SIG_" + x.idx + "_" + dir, Priority.High, msg, "", 30,
                          dir > 0 ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.OrangeRed,
                          System.Windows.Media.Brushes.Black);
                }
                if (false)
                    TryAutoTradeOnAlert(dir, stop, n > 0 ? tp[0] : 0, code, entry);
            }
        }

        // ------------------------------------------------------------------
        //  The six setups
        // ------------------------------------------------------------------

        private bool PiercedRecently(SigCtx x, bool up)
        {
            var cs = currentSession;
            Bars pb = BarsArray[0];
            for (int k = 0; k < 3; k++)
            {
                int b = x.idx - k;
                if (b < 0) break;
                if (up)
                {
                    if (cs.SD2H > 0 && pb.GetHigh(b) >= cs.SD2H * rowSize + rowSize) return true;
                    if (cs.SD3H > 0 && pb.GetHigh(b) >= cs.SD3H * rowSize + rowSize) return true;
                }
                else
                {
                    if (cs.SD2L > 0 && pb.GetLow(b) <= cs.SD2L * rowSize) return true;
                    if (cs.SD3L > 0 && pb.GetLow(b) <= cs.SD3L * rowSize) return true;
                }
            }
            return false;
        }

        private DateTime fbIntrabarLastEval = DateTime.MinValue;
        private bool      sigEvalIntrabar   = false;

        /// <summary>
        /// Live only: evaluate FB on the FORMING bar using the current price, so the alert and auto-trade
        /// fire the moment price trades back through the level (not at the bar close).
        /// Uses the same keys as the closed-bar pass, so the close does not re-emit it.
        /// Throttled to ~4 evaluations per second.
        /// </summary>
        private void EvaluateIntrabarFailedBreakouts()
        {
            // Removed — failed-breakout trade signals disabled (MR + VA rejection only).
            return;
        }


        /// <summary>
        /// FB setups: previous bar closed beyond a level and the evaluated close is back through it.
        /// Called with a CLOSED bar (history / bar close) or, live, with the forming bar where
        /// x.c is the current price (intrabar mode).
        /// </summary>
        private void RunFailedBreakouts(SigCtx x)
        {
            var cs  = currentSession;
            double row = rowSize;

            // ── 2. FB — failed breakout of the current session's SD / value-area edges ─
            if (false)
            {
                var highs = new List<SigLevel>();
                var lows  = new List<SigLevel>();
                Action<List<SigLevel>, double, string> mk = (lst, price, nm) =>
                { if (price > 0) lst.Add(new SigLevel { Price = price, Name = nm }); };
                if (cs.SD2H > 0)    mk(highs, cs.SD2H * row + row, "SD2H");
                if (cs.SD3H > 0)    mk(highs, cs.SD3H * row + row, "SD3H");
                if (cs.VVAH > 0)    mk(highs, cs.VVAH * row + row, "VAH");
                if (cs.TPOVAH > 0)  mk(highs, cs.TPOVAH * row + row, "tVAH");
                if (cs.TPOSD2H > 0) mk(highs, cs.TPOSD2H * row + row, "tSD2H");
                if (cs.TPOSD3H > 0) mk(highs, cs.TPOSD3H * row + row, "tSD3H");
                if (cs.SD2L > 0)    mk(lows,  cs.SD2L * row, "SD2L");
                if (cs.SD3L > 0)    mk(lows,  cs.SD3L * row, "SD3L");
                if (cs.VVAL > 0)    mk(lows,  cs.VVAL * row, "VAL");
                if (cs.TPOVAL > 0)  mk(lows,  cs.TPOVAL * row, "tVAL");
                if (cs.TPOSD2L > 0) mk(lows,  cs.TPOSD2L * row, "tSD2L");
                if (cs.TPOSD3L > 0) mk(lows,  cs.TPOSD3L * row, "tSD3L");

                foreach (var L in highs)
                {
                    if (!(x.pc > L.Price && x.c < L.Price)) continue;
                    if (L.Name == "VAH" && false && !PiercedRecently(x, true)) continue;
                    TryEmit(x, -1, "FB", "failed break " + L.Name, L.Price, Math.Max(x.h, x.ph),
                            "FB|" + L.Name + "|" + x.idx, true, true);
                }
                foreach (var L in lows)
                {
                    if (!(x.pc < L.Price && x.c > L.Price)) continue;
                    if (L.Name == "VAL" && false && !PiercedRecently(x, false)) continue;
                    TryEmit(x, +1, "FB", "failed break " + L.Name, L.Price, Math.Min(x.l, x.pl),
                            "FB|" + L.Name + "|" + x.idx, true, true);
                }
            }

            // ── 2b. FB on prior-session and naked 1σ / 2σ / 3σ levels (TPO and volume) ─────
            if (false)
            {
                DateTime sessStart = cs.StartTime;
                foreach (var L in GetPriorLevels())
                {
                    string nm = L.Name.Length > 1 ? L.Name.Substring(1) : L.Name;
                    bool isHigh = nm == "VAH" || nm == "SD2H" || nm == "SD3H" || nm == "tVAH" || nm == "tSD2H" || nm == "tSD3H";
                    bool isLow  = nm == "VAL" || nm == "SD2L" || nm == "SD3L" || nm == "tVAL" || nm == "tSD2L" || nm == "tSD3L";
                    if (!isHigh && !isLow) continue;
                    if (!(L.Recent || L.BreakTime == DateTime.MinValue || L.BreakTime >= sessStart)) continue;   // naked or still in play
                    string pk = "FB|" + L.Name + "|" + Math.Round(L.Price, 2) + "|" + x.idx;
                    if (isHigh && x.pc > L.Price && x.c < L.Price)
                        TryEmit(x, -1, "FB", "failed break " + L.Name, L.Price, Math.Max(x.h, x.ph), pk, true, true);
                    else if (isLow && x.pc < L.Price && x.c > L.Price)
                        TryEmit(x, +1, "FB", "failed break " + L.Name, L.Price, Math.Min(x.l, x.pl), pk, true, true);
                }
            }
        }

        private void RunSignalSetups(SigCtx x)
        {
            var cs  = currentSession;
            double row = rowSize;
            double tol = 2 * TickSize;

            // ── 1. NK — naked prior-session level, first test rejects ───────────────
            if (false)
            {
                foreach (var L in GetPriorLevels())
                {
                    if (!(L.BreakTime == DateTime.MinValue || L.BreakTime >= x.pt)) continue;   // already tested earlier
                    if (x.l > L.Price + tol || x.h < L.Price - tol) continue;                    // bar never reached it
                    string key = "NK|" + L.Name + "|" + Math.Round(L.Price, 2);
                    if (x.pc > L.Price && x.c > L.Price && x.c > x.o)
                        TryEmit(x, +1, "NK", "naked " + L.Name, L.Price, x.l, key, true, false);
                    else if (x.pc < L.Price && x.c < L.Price && x.c < x.o)
                        TryEmit(x, -1, "NK", "naked " + L.Name, L.Price, x.h, key, true, false);
                }
            }

            // ── 2 / 2b. FB — failed breakouts (closed-bar here; also run intrabar live, see EvaluateIntrabarFailedBreakouts)
            RunFailedBreakouts(x);

            // ── 3. EX — TPO excess tail rejected ────────────────────────────────────
            if (false)
            {
                if (cs.HasExcessHigh && cs.ExcessHighPrice > 0)
                {
                    double ex = cs.ExcessHighPrice;
                    if (Math.Max(x.h, x.ph) >= ex && x.c < ex - row && x.c < x.o)
                        TryEmit(x, -1, "EX", "excess high " + ex.ToString("F2"), ex,
                                Math.Max(ex + row, Math.Max(x.h, x.ph)),
                                "EX|H|" + x.sk + "|" + Math.Round(ex, 2), true, true);
                }
                if (cs.HasExcessLow && cs.ExcessLowPrice > 0)
                {
                    double ex = cs.ExcessLowPrice;
                    if (Math.Min(x.l, x.pl) <= ex + row && x.c > ex + 2 * row && x.c > x.o)
                        TryEmit(x, +1, "EX", "excess low " + ex.ToString("F2"), ex,
                                Math.Min(ex, Math.Min(x.l, x.pl)),
                                "EX|L|" + x.sk + "|" + Math.Round(ex, 2), true, true);
                }
            }

            // ── 4/5. IB break (continuation) and IB failure (fade) ───────────────────
            if ((false || false) && cs.IBComplete && cs.IBHigh > 0 && cs.IBLow > 0)
            {
                double ibH = IBHighPx(cs);
                double ibL = IBLowPx(cs);

                if (false)
                {
                    if (x.pc <= ibH && x.c > ibH && x.st == STVPRegimeState.Discovery)
                        TryEmit(x, +1, "IB", "IB break up", ibH, Math.Min(x.l, ibH),
                                "IB|U|" + x.sk, false, false);
                    if (x.pc >= ibL && x.c < ibL && x.st == STVPRegimeState.Discovery)
                        TryEmit(x, -1, "IB", "IB break down", ibL, Math.Max(x.h, ibL),
                                "IB|D|" + x.sk, false, false);
                }
                if (false)
                {
                    if (x.pc > ibH && x.c <= ibH && x.st != STVPRegimeState.Discovery)
                        TryEmit(x, -1, "IF", "IB up-break failed", ibH, Math.Max(x.h, x.ph),
                                "IF|U|" + x.sk, true, true);
                    if (x.pc < ibL && x.c >= ibL && x.st != STVPRegimeState.Discovery)
                        TryEmit(x, +1, "IF", "IB down-break failed", ibL, Math.Min(x.l, x.pl),
                                "IF|D|" + x.sk, true, true);
                }
            }

            // ── 6. SP — pullback into an untested single-print zone ──────────────────
            if (false)
            {
                foreach (var z in prevZones)
                {
                    string zk = "SP|" + x.sk + "|" + Math.Round(z.Lo, 2);
                    if (x.pc > z.Hi && x.l <= z.Hi && x.l >= z.Lo - tol && x.c > z.Hi && x.c > x.o)
                        TryEmit(x, +1, "SP", "single-print pullback", z.Hi, Math.Min(x.l, z.Lo),
                                zk + "|B", false, false);
                    else if (x.pc < z.Lo && x.h >= z.Lo && x.h <= z.Hi + tol && x.c < z.Lo && x.c < x.o)
                        TryEmit(x, -1, "SP", "single-print pullback", z.Lo, Math.Max(x.h, z.Hi),
                                zk + "|S", false, false);
                }
            }

            // ── 7. 80 — 80% rule ──────────────────────────────────────────────────────
            if (false)
                CheckEightyPercent(x);
        }

        private void CheckEightyPercent(SigCtx x)
        {
            if (eightyDone) return;
            var ps = GetPriorSession();
            if (ps == null || ps.TPOVAH <= 0 || ps.TPOVAL <= 0) return;
            double vah = ps.TPOVAH * rowSize + rowSize;
            double val = ps.TPOVAL * rowSize;

            if (!eightyInit)
            {
                if (currentSession.RTHOpen <= 0) return;
                eightyInit = true;
                double ro = currentSession.RTHOpen;
                eightySide = ro > vah ? 1 : (ro < val ? -1 : 0);
                if (eightySide == 0) { eightyDone = true; return; }   // opened inside value: rule doesn't apply
            }

            // price ran straight through value to the other side: setup is void
            if ((eightySide > 0 && x.c < val) || (eightySide < 0 && x.c > vah)) { eightyDone = true; return; }

            bool inside = x.c <= vah && x.c >= val;
            if (!inside) { eightyInsideSince = DateTime.MinValue; return; }

            if (eightyInsideSince == DateTime.MinValue) { eightyInsideSince = x.pt; return; }

            if ((x.t - eightyInsideSince).TotalMinutes >= 30)
            {
                eightyDone = true;
                if (eightySide > 0)
                    TryEmit(x, -1, "80", "80% rule: opened above value", vah, Math.Max(vah, x.h),
                            "80|" + x.sk, true, true, val, "pVAL");
                else
                    TryEmit(x, +1, "80", "80% rule: opened below value", val, Math.Min(val, x.l),
                            "80|" + x.sk, true, true, vah, "pVAH");
            }
        }

        // ------------------------------------------------------------------
        //  Summary printed once history has loaded
        // ------------------------------------------------------------------

        private void PrintSignalSummary()
        {
            if (!false) return;
            TradeSignal[] snap;
            lock (signalLock) { snap = signals.ToArray(); }
            int buys = 0, sells = 0, tp1 = 0, stopped = 0, open = 0;
            var bySetup = new Dictionary<string, int>();
            foreach (var s in snap)
            {
                if (s.Dir > 0) buys++; else sells++;
                if (s.TPHitBar[0] >= 0) tp1++;
                else if (s.StopHitBar >= 0) stopped++;
                else open++;
                foreach (var code in s.Setup.Split('+'))
                {
                    int c; bySetup.TryGetValue(code, out c); bySetup[code] = c + 1;
                }
            }
            string parts = string.Join("  ", bySetup.Select(kv => kv.Key + ":" + kv.Value).ToArray());
            Print("SessionTPOVolumeProfile signals: total=" + snap.Length + " (buy " + buys + ", sell " + sells + ")"
                + "  reachedTP1=" + tp1 + "  stoppedBeforeTP1=" + stopped + "  unresolved=" + open
                + "   by setup -> " + parts);
            Print("SessionTPOVolumeProfile signal diagnostics: barsEvaluated=" + diagEval
                + " barsWithoutRegimeData=" + diagNoRegime
                + " setupTriggers=" + diagRaw
                + " blockedByControl=" + diagCtrl
                + " blockedByDayType=" + diagDay
                + " blockedByStopWidth=" + diagRisk
                + " blockedByConfirmation=" + diagConf
                + " noTargets=" + diagNoTP);
        }

        // ------------------------------------------------------------------
        //  Rendering
        // ------------------------------------------------------------------

        private void EnsureSignalResources()
        {
            if (RenderTarget == null || sigBuyBrush != null) return;
            try
            {
                sigBuyBrush   = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.20f, 0.90f, 0.35f, 1f));
                sigSellBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1.00f, 0.35f, 0.25f, 1f));
                // IN CHARGE levels: buy = blue, sell = magenta (exact price on the bar)
                sigIcBuyBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.20f, 0.45f, 1.00f, 1f));   // blue
                sigIcSellBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1.00f, 0.00f, 1.00f, 1f));   // magenta
                sigStopBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1.00f, 0.20f, 0.20f, 0.95f));
                sigTPBrush    = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.25f, 0.95f, 0.75f, 0.95f));
                sigEntryBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.85f, 0.85f, 0.85f, 0.90f));
                sigLabelFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 10);
                sigLabelFormat.TextAlignment = TextAlignment.Leading;
                sigLabelFormat.WordWrapping  = WordWrapping.NoWrap;
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile: signal resources error — " + ex.Message);
                DisposeSignalResources();
            }
        }

        private void DisposeSignalResources()
        {
            if (sigBuyBrush    != null) { sigBuyBrush.Dispose();    sigBuyBrush    = null; }
            if (sigSellBrush   != null) { sigSellBrush.Dispose();   sigSellBrush   = null; }
            if (sigIcBuyBrush  != null) { sigIcBuyBrush.Dispose();  sigIcBuyBrush  = null; }
            if (sigIcSellBrush != null) { sigIcSellBrush.Dispose(); sigIcSellBrush = null; }
            if (sigStopBrush   != null) { sigStopBrush.Dispose();   sigStopBrush   = null; }
            if (sigTPBrush     != null) { sigTPBrush.Dispose();     sigTPBrush     = null; }
            if (sigEntryBrush  != null) { sigEntryBrush.Dispose();  sigEntryBrush  = null; }
            if (sigLabelFormat != null) { sigLabelFormat.Dispose(); sigLabelFormat = null; }
        }

        private string SignalResult(TradeSignal s)
        {
            int hits = 0;
            for (int k = 0; k < s.TPCount; k++) if (s.TPHitBar[k] >= 0) hits++;
            if (s.StopHitBar >= 0) return hits > 0 ? "  [TP" + hits + " then stop]" : "  [stopped]";
            if (s.TPCount > 0 && hits >= s.TPCount) return "  [all targets]";
            if (hits > 0) return "  [TP" + hits + "]";
            return "";
        }

        private void FillSignalTriangle(float x, float yTip, bool up, SharpDX.Direct2D1.Brush brush)
        {
            const float w = 7f, h = 12f;
            using (var geo = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
            {
                using (var sink = geo.Open())
                {
                    float yBase = up ? yTip + h : yTip - h;
                    sink.BeginFigure(new SharpDX.Vector2(x, yTip), SharpDX.Direct2D1.FigureBegin.Filled);
                    sink.AddLine(new SharpDX.Vector2(x - w, yBase));
                    sink.AddLine(new SharpDX.Vector2(x + w, yBase));
                    sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
                    sink.Close();
                }
                RenderTarget.FillGeometry(geo, brush);
            }
        }

        private void DrawSignalLine(ChartScale chartScale, double price, float x1, float x2,
                                    SharpDX.Direct2D1.Brush brush, string label, bool dashed)
        {
            float y = chartScale.GetYByValue(price);
            if (y < ChartPanel.Y || y > ChartPanel.Y + ChartPanel.H) return;
            if (dashed && dashStrokeStyle != null)
                RenderTarget.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y), brush, 1f, dashStrokeStyle);
            else
                RenderTarget.DrawLine(new SharpDX.Vector2(x1, y), new SharpDX.Vector2(x2, y), brush, 1f);
            if (sigLabelFormat != null)
                RenderTarget.DrawText(label, sigLabelFormat,
                    new SharpDX.RectangleF(x2 + 3f, y - 7f, 230f, 14f), brush);
        }

        private void RenderRejectedSetups(ChartControl chartControl, ChartScale chartScale, TradeSignal[] snap)
        {
            RejectedSetup[] rs;
            lock (signalLock) { rs = rejected.ToArray(); }
            int from = ChartBars.FromIndex, to = ChartBars.ToIndex;
            var stack = new Dictionary<string, int>();
            foreach (var r in rs)
            {
                if (r.BarIndex < from || r.BarIndex > to) continue;
                bool hasSig = false;
                foreach (var sg in snap)
                    if (sg.BarIndex == r.BarIndex && sg.Dir == r.Dir) { hasSig = true; break; }
                if (hasSig) continue;

                string sk = r.BarIndex + "|" + r.Dir;
                int n; stack.TryGetValue(sk, out n); stack[sk] = n + 1;

                float x = chartControl.GetXByBarIndex(ChartBars, r.BarIndex);
                double px = r.Dir > 0 ? ChartBars.Bars.GetLow(r.BarIndex) : ChartBars.Bars.GetHigh(r.BarIndex);
                float y = chartScale.GetYByValue(px) + (r.Dir > 0 ? 22f + n * 13f : -22f - n * 13f);
                RenderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new SharpDX.Vector2(x, y), 4f, 4f), sigEntryBrush, 1f);
                RenderTarget.DrawText((r.Dir > 0 ? "buy " : "sell ") + r.Setup + ": " + r.Reason, sigLabelFormat,
                    new SharpDX.RectangleF(x + 8f, y - 7f, 360f, 14f), sigEntryBrush);
            }
        }

        private void RenderSignals(ChartControl chartControl, ChartScale chartScale)
        {
            if (ChartBars == null || ChartPanel == null) return;
            EnsureSignalResources();
            if (sigBuyBrush == null || sigLabelFormat == null) return;

            TradeSignal[] snap;
            lock (signalLock) { snap = signals.ToArray(); }
            // Invalid / rejected setups are never drawn — only valid signals print.
            // if (false) RenderRejectedSetups(chartControl, chartScale, snap);
            if (snap.Length == 0) return;

            int from = ChartBars.FromIndex, to = ChartBars.ToIndex;
            int lastBar = ChartBars.Count - 1;
            float rightLimit = ChartPanel.X + ChartPanel.W - 6f;

            foreach (var s in snap)
            {
                if (s.BarIndex > to) continue;
                int endBar = s.EndBar >= 0 ? s.EndBar : lastBar;
                if (endBar < from) continue;

                bool buy = s.Dir > 0;
                bool isIc = s.Setup != null && (s.Setup == "IC" || s.Setup.StartsWith("IC ") || s.Setup.StartsWith("IC+") || s.Setup.StartsWith("IC W") || s.Setup.Contains("+IC"));
                SharpDX.Direct2D1.Brush dirBrush;
                if (isIc)
                    dirBrush = buy
                        ? (SharpDX.Direct2D1.Brush)(sigIcBuyBrush  ?? sigBuyBrush)   // blue
                        : (SharpDX.Direct2D1.Brush)(sigIcSellBrush ?? sigSellBrush); // magenta
                else
                    dirBrush = buy ? (SharpDX.Direct2D1.Brush)sigBuyBrush : sigSellBrush;
                float x = chartControl.GetXByBarIndex(ChartBars, s.BarIndex);

                // Arrow + label next to the signal bar
                double barLow  = ChartBars.Bars.GetLow(s.BarIndex);
                double barHigh = ChartBars.Bars.GetHigh(s.BarIndex);
                float yEdge = chartScale.GetYByValue(buy ? barLow : barHigh);
                float yTip  = buy ? yEdge + 6f : yEdge - 6f;
                if (s.BarIndex >= from)
                {
                    // Exact IN CHARGE price level across the signal bar (blue buy / magenta sell)
                    if (isIc && s.Entry > 0)
                    {
                        float yLvl = chartScale.GetYByValue(s.Entry);
                        float half = Math.Max(6f, chartControl.GetBarPaintWidth(ChartBars) * 0.5f + 4f);
                        RenderTarget.DrawLine(
                            new SharpDX.Vector2(x - half, yLvl),
                            new SharpDX.Vector2(x + half, yLvl),
                            dirBrush, 2.5f);
                    }

                    FillSignalTriangle(x, yTip, buy, dirBrush);
                    string txt = s.Setup + (buy ? " Buy" : " Sell");   // short label: IC Buy, VU Sell, VD Buy, FB Sell...
                    float ty = buy ? yTip + 14f : yTip - 28f;
                    RenderTarget.DrawText(txt, sigLabelFormat,
                        new SharpDX.RectangleF(x - 18f, ty, 380f, 14f), dirBrush);
                }

                if (!ShowSignalLines) continue;

                float x2 = s.EndBar >= 0
                    ? chartControl.GetXByBarIndex(ChartBars, s.EndBar)
                    : chartControl.GetXByBarIndex(ChartBars, lastBar) + 40f;
                x2 = Math.Min(x2, rightLimit - 120f);
                float x1 = Math.Max(x, ChartPanel.X);
                if (x2 <= x1 + 4f) x2 = x1 + 24f;

                DrawSignalLine(chartScale, s.Entry, x1, x2, sigEntryBrush,
                    s.Setup + (buy ? " Buy" : " Sell"), true);
                DrawSignalLine(chartScale, s.Stop, x1, x2, sigStopBrush,
                    "STOP " + s.Stop.ToString("F2") + (s.StopHitBar >= 0 ? "  (hit)" : ""), true);
                for (int k = 0; k < s.TPCount; k++)
                    DrawSignalLine(chartScale, s.TP[k], x1, x2, sigTPBrush,
                        "TP" + (k + 1) + " " + s.TP[k].ToString("F2") + "  " + s.TPName[k]
                        + (s.TPHitBar[k] >= 0 ? "  (hit)" : ""), false);
            }
        }

        #endregion

        #region Resource Management

        public override void OnRenderTargetChanged()
        {
            DisposeResources();
            if (RenderTarget != null)
                CreateResources();
        }

        private void CreateResources()
        {
            if (RenderTarget == null) return;
            try
            {
                vpocBrush             = DxBrush(VPOCColor);
                hvnBrush              = DxBrush(HVNColor);
                lvnBrush              = DxBrush(LVNColor);
                lvnExtAboveBrush      = DxBrush(LVNExtAboveColor);
                lvnExtBelowBrush      = DxBrush(LVNExtBelowColor);
                mvnBrush              = DxBrush(VolumeBarColor);
                deltaPositiveBrush    = DxBrush(DeltaPositiveColor);
                deltaNegativeBrush    = DxBrush(DeltaNegativeColor);
                deltaPositiveFillBrush = DxBrushWithAlpha(DeltaPositiveColor, 0.70f);
                deltaNegativeFillBrush = DxBrushWithAlpha(DeltaNegativeColor, 0.70f);
                bidAskAskBrush         = DxBrush(BidAskAskColor);
                bidAskBidBrush         = DxBrush(BidAskBidColor);
                bidAskAskFillBrush     = DxBrushWithAlpha(BidAskAskColor, 0.30f);
                bidAskBidFillBrush     = DxBrushWithAlpha(BidAskBidColor, 0.30f);
                buyingImbalanceBrush   = DxBrushWithAlpha(BuyingImbalanceColor,  0.55f);
                sellingImbalanceBrush  = DxBrushWithAlpha(SellingImbalanceColor, 0.55f);
                // Base zones: visible but restrained. Strong (HVN/VPOC overlap): clearly heavier.
                buyZoneFillBrush         = DxBrushWithAlpha(BuyZoneColor,  0.20f);
                sellZoneFillBrush        = DxBrushWithAlpha(SellZoneColor, 0.20f);
                buyZoneBorderBrush       = DxBrushWithAlpha(BuyZoneColor,  0.70f);
                sellZoneBorderBrush      = DxBrushWithAlpha(SellZoneColor, 0.70f);
                buyZoneFillStrongBrush   = DxBrushWithAlpha(BuyZoneColor,  0.45f);
                sellZoneFillStrongBrush  = DxBrushWithAlpha(SellZoneColor, 0.45f);
                buyZoneBorderStrongBrush = DxBrushWithAlpha(BuyZoneColor,  1.00f);
                sellZoneBorderStrongBrush= DxBrushWithAlpha(SellZoneColor, 1.00f);
                sameLevelBuyDomBrush   = DxBrush(SameLevelBuyDominanceColor);
                sameLevelSellDomBrush  = DxBrush(SameLevelSellDominanceColor);
                volumeBarOutlineBrush = DxBrush(VolumeBarOutlineColor);
                volumeVABoxBrush      = DxBrush(VolumeVABoxColor);
                valueAreaFillBrush    = DxBrush(ValueAreaFillColor);
                levelZoneFillBrush    = DxBrushWithAlpha(LevelZoneColor, 0.20f);
                levelZoneBorderBrush  = DxBrushWithAlpha(LevelZoneColor, 0.70f);
                ibBrush               = DxBrush(IBColor);
                ibFirstBrush          = DxBrush(IBFirstSideColor);
                ibSecondHighBrush     = DxBrush(IBSecondHighColor);
                ibSecondLowBrush      = DxBrush(IBSecondLowColor);
                ibQuartileBrush       = DxBrush(IBQuartileColor);
                nakedVpocBrush        = DxBrush(NakedVPOCColor);
                nakedVABrush          = DxBrush(NakedVAColor);
                nakedVAHBrush         = DxBrush(NakedVAHColor);

                // 2σ brushes — full opacity for box/labels, semi-transparent for fill
                sd2VABoxBrush  = DxBrush(SD2Color);
                nakedSD2Brush  = DxBrush(SD2Color);
                sd2VAFillBrush = DxBrushWithAlpha(SD2Color, 0.08f);

                // 3σ brushes — full opacity for box/labels, semi-transparent for fill
                sd3VABoxBrush  = DxBrush(SD3Color);
                nakedSD3Brush  = DxBrush(SD3Color);
                sd3VAFillBrush = DxBrushWithAlpha(SD3Color, 0.06f);

                priorHighLowBrush     = DxBrush(PriorHighLowColor);
                textBrush             = DxBrush(TextColor);

                volumeFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", VolumeFontSize);
                volumeFormat.TextAlignment = TextAlignment.Trailing;
                volumeFormat.WordWrapping  = WordWrapping.NoWrap;

                bidAskAskFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", VolumeFontSize);
                bidAskAskFormat.TextAlignment = TextAlignment.Leading;
                bidAskAskFormat.WordWrapping  = WordWrapping.NoWrap;

                statsFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", LabelFontSize);
                statsFormat.TextAlignment = TextAlignment.Leading;
                statsFormat.WordWrapping  = WordWrapping.NoWrap;

                vaLabelFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", VALabelFontSize);
                vaLabelFormat.TextAlignment = TextAlignment.Leading;
                vaLabelFormat.WordWrapping  = WordWrapping.NoWrap;

                dashStrokeStyle = new StrokeStyle(RenderTarget.Factory,
                    new StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
                dotStrokeStyle = new StrokeStyle(RenderTarget.Factory,
                    new StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dot });
                dashDotStrokeStyle = new StrokeStyle(RenderTarget.Factory,
                    new StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.DashDot });

                // Per-level VA line brushes
                vvahLineBrush = DxBrush(VVAHLineColor);
                vvalLineBrush = DxBrush(VVALLineColor);
                sd2HLineBrush = DxBrush(SD2HLineColor);
                sd2LLineBrush = DxBrush(SD2LLineColor);
                sd3HLineBrush = DxBrush(SD3HLineColor);
                sd3LLineBrush = DxBrush(SD3LLineColor);

                // TPO brushes
                tpoLetterBrush   = DxBrush(TPOLetterColor);
                tpoPocBrush      = DxBrush(TPOPOCColor);
                tpoVAHBrush      = DxBrush(TPOVAHColor);
                tpoVALBrush      = DxBrush(TPOVALColor);
                nakedTPOPocBrush = DxBrush(NakedTPOPOCColor);
                nakedTPOVABrush  = DxBrush(NakedTPOVAColor);
                tpoSD2Brush      = DxBrush(TPOSDTwoColor);
                nakedTPOSD2Brush = DxBrush(NakedTPOSDTwoColor);
                tpoSD3Brush      = DxBrush(TPOSDThreeColor);
                nakedTPOSD3Brush = DxBrush(NakedTPOSDThreeColor);
                singlePrintBrush = DxBrush(SinglePrintColor);
                poorHighLowBrush = DxBrush(PoorHighLowColor);
                excessBrush      = DxBrush(ExcessColor);
                tpoVABoxBrush    = DxBrush(TPOVABoxColor);

                letterFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", TPOFontSize);
                letterFormat.TextAlignment = TextAlignment.Center;
                letterFormat.WordWrapping  = WordWrapping.NoWrap;

                // Cluster zone brushes unused (feature removed) — null-safe no-ops
                clusterTier1Brush = null;
                clusterTier2Brush = null;
                clusterTier3Brush = null;
                clusterTier1BorderBrush = null;
                clusterTier2BorderBrush = null;
                clusterTier3BorderBrush = null;

                clusterLabelFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 9);
                clusterLabelFormat.TextAlignment = TextAlignment.Leading;
                clusterLabelFormat.WordWrapping  = WordWrapping.NoWrap;

                // ── Analytics Panel brush and format ──
                analyticsPanelBrush = DxBrush(TextColor);
                analyticsPanelBgBrush = new SharpDX.Direct2D1.SolidColorBrush(
                    RenderTarget, new SharpDX.Color4(0f, 0f, 0f, 0.55f));
                analyticsPanelFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 10);
                analyticsPanelFormat.TextAlignment = TextAlignment.Leading;
                analyticsPanelFormat.WordWrapping  = WordWrapping.NoWrap;

                // ── Regime & Dominance brushes ──
                regimeDiscoveryBgBrush  = DxBrush(DiscoveryBgColor);
                regimeAuctionBgBrush    = DxBrush(AuctionBgColor);
                regimeTransitionBgBrush = DxBrush(TransitionBgColor);
                regimeBuyerBrush        = DxBrush(BuyerDominanceColor);
                regimeSellerBrush       = DxBrush(SellerDominanceColor);
                regimePanelBgBrush = new SharpDX.Direct2D1.SolidColorBrush(
                    RenderTarget, new SharpDX.Color4(0f, 0f, 0f, 0.60f));
                regimePanelTextBrush    = DxBrush(TextColor);
                regimePanelFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 11);
                regimePanelFormat.TextAlignment = TextAlignment.Leading;
                regimePanelFormat.WordWrapping  = WordWrapping.NoWrap;

                // L2 wall / in-trade monitor brushes
                wallSeenBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.65f, 0.65f, 0.70f, 0.85f));
                wallBidBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.25f, 0.75f, 0.45f, 0.80f));
                wallAskBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.90f, 0.35f, 0.30f, 0.80f));
                wallTestingBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1.00f, 0.90f, 0.20f, 0.90f));
                wallAbsorbBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1.00f, 0.55f, 0.10f, 0.92f));
                wallDimBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.45f, 0.45f, 0.45f, 0.40f));
                tradeMonBgBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0f, 0f, 0f, 0.62f));
                tradeMonHoldBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.30f, 0.85f, 0.50f, 1f));
                tradeMonCautionBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1.00f, 0.80f, 0.15f, 1f));
                tradeMonExitBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1.00f, 0.30f, 0.25f, 1f));
                wallLabelFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 9);
                wallLabelFormat.TextAlignment = TextAlignment.Leading;
                wallLabelFormat.WordWrapping = WordWrapping.NoWrap;
                tradeMonFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", 10);
                tradeMonFormat.TextAlignment = TextAlignment.Leading;
                tradeMonFormat.WordWrapping = WordWrapping.NoWrap;

                resourcesCreated = true;
            }
            catch (Exception ex)
            {
                Print("SessionTPOVolumeProfile: CreateResources error — " + ex.Message);
                resourcesCreated = false;
            }
        }

        /// <summary>Converts a WPF SolidColorBrush to a SharpDX brush bound to the current RenderTarget.</summary>
        private SharpDX.Direct2D1.Brush DxBrush(System.Windows.Media.Brush mediaBrush)
        {
            var solid = mediaBrush as System.Windows.Media.SolidColorBrush;
            if (solid != null)
            {
                var c = solid.Color;
                return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f));
            }
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.White);
        }

        /// <summary>
        /// Same as DxBrush but overrides the alpha to <paramref name="alpha"/>
        /// so fill rectangles can be semi-transparent while box outlines stay opaque.
        /// </summary>
        private SharpDX.Direct2D1.Brush DxBrushWithAlpha(System.Windows.Media.Brush mediaBrush, float alpha)
        {
            var solid = mediaBrush as System.Windows.Media.SolidColorBrush;
            if (solid != null)
            {
                var c = solid.Color;
                return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, alpha));
            }
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                new SharpDX.Color4(1f, 1f, 1f, alpha));
        }

        /// <summary>Returns the StrokeStyle that matches a STVPLineStyle enum value.
        /// Returns null for Solid so Direct2D uses its fast default solid path.</summary>
        private StrokeStyle StrokeStyleFor(STVPLineStyle style)
        {
            switch (style)
            {
                case STVPLineStyle.Dash:    return dashStrokeStyle;
                case STVPLineStyle.Dot:     return dotStrokeStyle;
                case STVPLineStyle.DashDot: return dashDotStrokeStyle;
                default:                  return null;   // Solid
            }
        }

        private void DisposeResources()
        {
            DisposeSignalResources();
            DisposeAutoTradeButtonResources();
            if (vpocBrush             != null) { vpocBrush.Dispose();             vpocBrush             = null; }
            if (hvnBrush              != null) { hvnBrush.Dispose();              hvnBrush              = null; }
            if (lvnBrush              != null) { lvnBrush.Dispose();              lvnBrush              = null; }
            if (lvnExtAboveBrush      != null) { lvnExtAboveBrush.Dispose();      lvnExtAboveBrush      = null; }
            if (lvnExtBelowBrush      != null) { lvnExtBelowBrush.Dispose();      lvnExtBelowBrush      = null; }
            if (mvnBrush              != null) { mvnBrush.Dispose();              mvnBrush              = null; }
            if (deltaPositiveBrush    != null) { deltaPositiveBrush.Dispose();    deltaPositiveBrush    = null; }
            if (deltaNegativeBrush    != null) { deltaNegativeBrush.Dispose();    deltaNegativeBrush    = null; }
            if (deltaPositiveFillBrush != null) { deltaPositiveFillBrush.Dispose(); deltaPositiveFillBrush = null; }
            if (deltaNegativeFillBrush != null) { deltaNegativeFillBrush.Dispose(); deltaNegativeFillBrush = null; }
            if (bidAskAskBrush         != null) { bidAskAskBrush.Dispose();         bidAskAskBrush         = null; }
            if (bidAskBidBrush         != null) { bidAskBidBrush.Dispose();         bidAskBidBrush         = null; }
            if (bidAskAskFillBrush     != null) { bidAskAskFillBrush.Dispose();     bidAskAskFillBrush     = null; }
            if (bidAskBidFillBrush     != null) { bidAskBidFillBrush.Dispose();     bidAskBidFillBrush     = null; }
            if (buyingImbalanceBrush   != null) { buyingImbalanceBrush.Dispose();   buyingImbalanceBrush   = null; }
            if (sellingImbalanceBrush  != null) { sellingImbalanceBrush.Dispose();  sellingImbalanceBrush  = null; }
            if (buyZoneFillBrush         != null) { buyZoneFillBrush.Dispose();         buyZoneFillBrush         = null; }
            if (sellZoneFillBrush        != null) { sellZoneFillBrush.Dispose();        sellZoneFillBrush        = null; }
            if (buyZoneBorderBrush       != null) { buyZoneBorderBrush.Dispose();       buyZoneBorderBrush       = null; }
            if (sellZoneBorderBrush      != null) { sellZoneBorderBrush.Dispose();      sellZoneBorderBrush      = null; }
            if (buyZoneFillStrongBrush   != null) { buyZoneFillStrongBrush.Dispose();   buyZoneFillStrongBrush   = null; }
            if (sellZoneFillStrongBrush  != null) { sellZoneFillStrongBrush.Dispose();  sellZoneFillStrongBrush  = null; }
            if (buyZoneBorderStrongBrush != null) { buyZoneBorderStrongBrush.Dispose(); buyZoneBorderStrongBrush = null; }
            if (sellZoneBorderStrongBrush!= null) { sellZoneBorderStrongBrush.Dispose();sellZoneBorderStrongBrush= null; }
            if (sameLevelBuyDomBrush   != null) { sameLevelBuyDomBrush.Dispose();   sameLevelBuyDomBrush   = null; }
            if (sameLevelSellDomBrush  != null) { sameLevelSellDomBrush.Dispose();  sameLevelSellDomBrush  = null; }
            if (volumeBarOutlineBrush != null) { volumeBarOutlineBrush.Dispose(); volumeBarOutlineBrush = null; }
            if (volumeVABoxBrush      != null) { volumeVABoxBrush.Dispose();      volumeVABoxBrush      = null; }
            if (valueAreaFillBrush    != null) { valueAreaFillBrush.Dispose();    valueAreaFillBrush    = null; }
            if (levelZoneFillBrush    != null) { levelZoneFillBrush.Dispose();    levelZoneFillBrush    = null; }
            if (levelZoneBorderBrush  != null) { levelZoneBorderBrush.Dispose();  levelZoneBorderBrush  = null; }
            if (ibBrush               != null) { ibBrush.Dispose();               ibBrush               = null; }
            if (ibFirstBrush          != null) { ibFirstBrush.Dispose();          ibFirstBrush          = null; }
            if (ibSecondHighBrush     != null) { ibSecondHighBrush.Dispose();     ibSecondHighBrush     = null; }
            if (ibSecondLowBrush      != null) { ibSecondLowBrush.Dispose();      ibSecondLowBrush      = null; }
            if (ibQuartileBrush       != null) { ibQuartileBrush.Dispose();       ibQuartileBrush       = null; }
            if (nakedVpocBrush        != null) { nakedVpocBrush.Dispose();        nakedVpocBrush        = null; }
            if (nakedVABrush          != null) { nakedVABrush.Dispose();          nakedVABrush          = null; }
            if (nakedVAHBrush         != null) { nakedVAHBrush.Dispose();         nakedVAHBrush         = null; }
            if (sd2VABoxBrush         != null) { sd2VABoxBrush.Dispose();         sd2VABoxBrush         = null; }
            if (sd2VAFillBrush        != null) { sd2VAFillBrush.Dispose();        sd2VAFillBrush        = null; }
            if (nakedSD2Brush         != null) { nakedSD2Brush.Dispose();         nakedSD2Brush         = null; }
            if (sd3VABoxBrush         != null) { sd3VABoxBrush.Dispose();         sd3VABoxBrush         = null; }
            if (sd3VAFillBrush        != null) { sd3VAFillBrush.Dispose();        sd3VAFillBrush        = null; }
            if (nakedSD3Brush         != null) { nakedSD3Brush.Dispose();         nakedSD3Brush         = null; }
            if (priorHighLowBrush     != null) { priorHighLowBrush.Dispose();     priorHighLowBrush     = null; }
            if (textBrush             != null) { textBrush.Dispose();             textBrush             = null; }
            if (volumeFormat          != null) { volumeFormat.Dispose();          volumeFormat          = null; }
            if (statsFormat           != null) { statsFormat.Dispose();           statsFormat           = null; }
            if (vaLabelFormat         != null) { vaLabelFormat.Dispose();         vaLabelFormat         = null; }
            if (bidAskAskFormat       != null) { bidAskAskFormat.Dispose();       bidAskAskFormat       = null; }
            if (dashStrokeStyle       != null) { dashStrokeStyle.Dispose();       dashStrokeStyle       = null; }
            if (dotStrokeStyle        != null) { dotStrokeStyle.Dispose();        dotStrokeStyle        = null; }
            if (dashDotStrokeStyle    != null) { dashDotStrokeStyle.Dispose();    dashDotStrokeStyle    = null; }
            if (vvahLineBrush         != null) { vvahLineBrush.Dispose();         vvahLineBrush         = null; }
            if (vvalLineBrush         != null) { vvalLineBrush.Dispose();         vvalLineBrush         = null; }
            if (sd2HLineBrush         != null) { sd2HLineBrush.Dispose();         sd2HLineBrush         = null; }
            if (sd2LLineBrush         != null) { sd2LLineBrush.Dispose();         sd2LLineBrush         = null; }
            if (sd3HLineBrush         != null) { sd3HLineBrush.Dispose();         sd3HLineBrush         = null; }
            if (sd3LLineBrush         != null) { sd3LLineBrush.Dispose();         sd3LLineBrush         = null; }

            // TPO brushes
            if (tpoLetterBrush   != null) { tpoLetterBrush.Dispose();   tpoLetterBrush   = null; }
            if (tpoPocBrush      != null) { tpoPocBrush.Dispose();      tpoPocBrush      = null; }
            if (tpoVAHBrush      != null) { tpoVAHBrush.Dispose();      tpoVAHBrush      = null; }
            if (tpoVALBrush      != null) { tpoVALBrush.Dispose();      tpoVALBrush      = null; }
            if (nakedTPOPocBrush != null) { nakedTPOPocBrush.Dispose(); nakedTPOPocBrush = null; }
            if (nakedTPOVABrush  != null) { nakedTPOVABrush.Dispose();  nakedTPOVABrush  = null; }
            if (tpoSD2Brush      != null) { tpoSD2Brush.Dispose();      tpoSD2Brush      = null; }
            if (nakedTPOSD2Brush != null) { nakedTPOSD2Brush.Dispose(); nakedTPOSD2Brush = null; }
            if (tpoSD3Brush      != null) { tpoSD3Brush.Dispose();      tpoSD3Brush      = null; }
            if (nakedTPOSD3Brush != null) { nakedTPOSD3Brush.Dispose(); nakedTPOSD3Brush = null; }
            if (singlePrintBrush != null) { singlePrintBrush.Dispose(); singlePrintBrush = null; }
            if (poorHighLowBrush != null) { poorHighLowBrush.Dispose(); poorHighLowBrush = null; }
            if (excessBrush      != null) { excessBrush.Dispose();      excessBrush      = null; }
            if (tpoVABoxBrush    != null) { tpoVABoxBrush.Dispose();    tpoVABoxBrush    = null; }
            if (letterFormat     != null) { letterFormat.Dispose();     letterFormat     = null; }

            // Cluster zone brushes
            if (clusterTier1Brush       != null) { clusterTier1Brush.Dispose();       clusterTier1Brush       = null; }
            if (clusterTier2Brush       != null) { clusterTier2Brush.Dispose();       clusterTier2Brush       = null; }
            if (clusterTier3Brush       != null) { clusterTier3Brush.Dispose();       clusterTier3Brush       = null; }
            if (clusterTier1BorderBrush != null) { clusterTier1BorderBrush.Dispose(); clusterTier1BorderBrush = null; }
            if (clusterTier2BorderBrush != null) { clusterTier2BorderBrush.Dispose(); clusterTier2BorderBrush = null; }
            if (clusterTier3BorderBrush != null) { clusterTier3BorderBrush.Dispose(); clusterTier3BorderBrush = null; }
            if (clusterLabelFormat      != null) { clusterLabelFormat.Dispose();      clusterLabelFormat      = null; }

            // Analytics panel
            if (analyticsPanelBrush     != null) { analyticsPanelBrush.Dispose();     analyticsPanelBrush     = null; }
            if (analyticsPanelBgBrush   != null) { analyticsPanelBgBrush.Dispose();   analyticsPanelBgBrush   = null; }
            if (analyticsPanelFormat    != null) { analyticsPanelFormat.Dispose();    analyticsPanelFormat    = null; }

            // Regime & Dominance
            if (regimeDiscoveryBgBrush  != null) { regimeDiscoveryBgBrush.Dispose();  regimeDiscoveryBgBrush  = null; }
            if (regimeAuctionBgBrush    != null) { regimeAuctionBgBrush.Dispose();    regimeAuctionBgBrush    = null; }
            if (regimeTransitionBgBrush != null) { regimeTransitionBgBrush.Dispose(); regimeTransitionBgBrush = null; }
            if (regimeBuyerBrush        != null) { regimeBuyerBrush.Dispose();        regimeBuyerBrush        = null; }
            if (regimeSellerBrush       != null) { regimeSellerBrush.Dispose();       regimeSellerBrush       = null; }
            if (regimePanelBgBrush      != null) { regimePanelBgBrush.Dispose();      regimePanelBgBrush      = null; }
            if (regimePanelTextBrush    != null) { regimePanelTextBrush.Dispose();    regimePanelTextBrush    = null; }
            if (regimePanelFormat       != null) { regimePanelFormat.Dispose();       regimePanelFormat       = null; }

            if (wallSeenBrush != null) { wallSeenBrush.Dispose(); wallSeenBrush = null; }
            if (wallBidBrush != null) { wallBidBrush.Dispose(); wallBidBrush = null; }
            if (wallAskBrush != null) { wallAskBrush.Dispose(); wallAskBrush = null; }
            if (wallTestingBrush != null) { wallTestingBrush.Dispose(); wallTestingBrush = null; }
            if (wallAbsorbBrush != null) { wallAbsorbBrush.Dispose(); wallAbsorbBrush = null; }
            if (wallDimBrush != null) { wallDimBrush.Dispose(); wallDimBrush = null; }
            if (tradeMonBgBrush != null) { tradeMonBgBrush.Dispose(); tradeMonBgBrush = null; }
            if (tradeMonHoldBrush != null) { tradeMonHoldBrush.Dispose(); tradeMonHoldBrush = null; }
            if (tradeMonCautionBrush != null) { tradeMonCautionBrush.Dispose(); tradeMonCautionBrush = null; }
            if (tradeMonExitBrush != null) { tradeMonExitBrush.Dispose(); tradeMonExitBrush = null; }
            if (wallLabelFormat != null) { wallLabelFormat.Dispose(); wallLabelFormat = null; }
            if (tradeMonFormat != null) { tradeMonFormat.Dispose(); tradeMonFormat = null; }

            resourcesCreated = false;
        }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SessionTPOVolumeProfile[] cacheSessionTPOVolumeProfile;
		public SessionTPOVolumeProfile SessionTPOVolumeProfile(TimeSpan rTHStartTime, TimeSpan rTHEndTime, int ticksPerRow, int iBDurationMinutes, int maxSessionsToShow, STVPProfilePeriod profilePeriod, bool showVPOC, bool showVolumeValueArea, bool showInitialBalance, bool showIBQuartiles, bool showIBBiasLabel, bool showNakedVPOC, bool showPriorSessionVPOC, bool showNakedVAHVAL, bool showPriorSessionLevels, bool showVolumeLabels, bool showPerBarBidAskTotals, bool showSessionStats, bool showSD2VA, bool showSD3VA, bool showVA1Fill, bool showSD2Fill, bool showSD3Fill, bool showNakedSD2, bool showNakedSD3, int volumeFontSize, int vALabelFontSize, int vALabelXOffset, int vABoxLineWidth, STVPDisplayStyle vADisplayStyle, bool showLVNExtension, bool showCurrentVolumeLevels, bool hvnLvnOnlyMode, STVPBarDisplayMode volumeBarDisplayMode, bool showSessionBuysSells, bool showSlidingWindowBuysSells, int tradeSlidingWindowSeconds, bool showSlidingWindowTotals, int slidingWindowTotalsOffsetMultiple, STVPTradeClassification tradeClassification, bool showDiagonalImbalance, STVPImbalanceMode imbalanceMode, double diagonalImbalanceRatio, double horizontalImbalanceRatio, int minImbalanceVolume, int zoneHorizontalControlFloor, bool showImbalanceZones, int minLevelsForImbalanceZone, int labelFontSize, int extensionLineWidth, int currentSessionVbPWidth, int priorSessionVbPWidth, int currentSessionRightOffset, int levelLabelRightOffset, System.Windows.Media.Brush vPOCColor, System.Windows.Media.Brush hVNColor, System.Windows.Media.Brush lVNColor, System.Windows.Media.Brush volumeBarColor, System.Windows.Media.Brush volumeBarOutlineColor, System.Windows.Media.Brush volumeVABoxColor, System.Windows.Media.Brush valueAreaFillColor, System.Windows.Media.Brush iBColor, System.Windows.Media.Brush iBFirstSideColor, System.Windows.Media.Brush iBSecondHighColor, System.Windows.Media.Brush iBSecondLowColor, System.Windows.Media.Brush iBQuartileColor, System.Windows.Media.Brush nakedVPOCColor, System.Windows.Media.Brush nakedVAColor, System.Windows.Media.Brush nakedVAHColor, System.Windows.Media.Brush priorHighLowColor, System.Windows.Media.Brush textColor, System.Windows.Media.Brush sD2Color, System.Windows.Media.Brush sD3Color, System.Windows.Media.Brush lVNExtAboveColor, System.Windows.Media.Brush lVNExtBelowColor, System.Windows.Media.Brush deltaPositiveColor, System.Windows.Media.Brush deltaNegativeColor, System.Windows.Media.Brush bidAskAskColor, System.Windows.Media.Brush bidAskBidColor, System.Windows.Media.Brush buyingImbalanceColor, System.Windows.Media.Brush sellingImbalanceColor, System.Windows.Media.Brush buyZoneColor, System.Windows.Media.Brush sellZoneColor, System.Windows.Media.Brush sameLevelBuyDominanceColor, System.Windows.Media.Brush sameLevelSellDominanceColor, System.Windows.Media.Brush vVAHLineColor, int vVAHLineWidth, STVPLineStyle vVAHLineStyle, System.Windows.Media.Brush vVALLineColor, int vVALLineWidth, STVPLineStyle vVALLineStyle, System.Windows.Media.Brush sD2HLineColor, int sD2HLineWidth, STVPLineStyle sD2HLineStyle, System.Windows.Media.Brush sD2LLineColor, int sD2LLineWidth, STVPLineStyle sD2LLineStyle, System.Windows.Media.Brush sD3HLineColor, int sD3HLineWidth, STVPLineStyle sD3HLineStyle, System.Windows.Media.Brush sD3LLineColor, int sD3LLineWidth, STVPLineStyle sD3LLineStyle, int tPOPeriodMinutes, bool splitGlobexRthTPO, bool showTPOLetters, bool showTPOPOC, bool showTPOVAHVAL, bool showTPOSD2, bool showTPOSD3, bool showNakedTPOPOC, bool showNakedTPOVAHVAL, bool showNakedTPOSD2, bool showNakedTPOSD3, bool showSinglePrints, bool showNakedSinglePrints, bool showPoorHighsLows, bool showExcess, bool showDayType, int tPOFontSize, bool showCurrentTPOLevels, System.Windows.Media.Brush tPOLetterColor, System.Windows.Media.Brush tPOPOCColor, System.Windows.Media.Brush tPOVAHColor, System.Windows.Media.Brush tPOVALColor, System.Windows.Media.Brush nakedTPOPOCColor, System.Windows.Media.Brush nakedTPOVAColor, System.Windows.Media.Brush tPOSDTwoColor, System.Windows.Media.Brush nakedTPOSDTwoColor, System.Windows.Media.Brush tPOSDThreeColor, System.Windows.Media.Brush nakedTPOSDThreeColor, System.Windows.Media.Brush singlePrintColor, System.Windows.Media.Brush poorHighLowColor, System.Windows.Media.Brush excessColor, System.Windows.Media.Brush tPOVABoxColor, bool showAnalyticsPanel, STVPPanelPosition analyticsPanelPosition, bool showDeltaZScore, int deltaZScoreLookbackSessions, double deltaZScoreThreshold, bool showVolumeSkewness, bool showVAMigration, int vAMigrationLookback, bool showTPOKurtosis, bool printLevelsAtSessionStart, bool printLevelsOnDemand, bool enableRegimeDetection, int dominanceWindowBars, double discoveryThreshold, double auctionThreshold, double imbalanceIntensityFloor, bool useLevel2Book, int l2DepthLevels, double l2DominanceWeight, double l2AbsorptionWeight, bool showRegimeBackground, bool showRegimePanel, STVPPanelPosition regimePanelPosition, System.Windows.Media.Brush discoveryBgColor, System.Windows.Media.Brush auctionBgColor, System.Windows.Media.Brush transitionBgColor, System.Windows.Media.Brush buyerDominanceColor, System.Windows.Media.Brush sellerDominanceColor, bool enableValueAreaRejection, bool vAPreferTpoLevels, bool vAIncludeNakedLevels, bool vAIncludeHighLow, bool vARequireCumDeltaConfirm, bool enableMeanRevertVAEdge, bool mRRequireTpoVaEdge, bool mRRequireImbalanceOrL2, bool mRRequireCumDeltaConfirm, bool mROneSignalPerEdge, bool enableTrendRunner, bool enableAutoTrade, string autoTradeAccountName, int autoTradeQuantity, bool autoTradeOnlyWhenFlat, int autoTradeStartMinutesAfterRth, bool enableWallMonitor, int wallMinSize, double wallMedianMultiple, int wallNearLevels, int wallScanRangeTicks, double wallPersistSeconds, int wallTestingTicks, double wallAbsorbRatio, double wallPullFraction, bool showWallsOnChart, int wallMarkerWidth, bool blockEntryOnOpposingSum, int entryMaxOpposingSize)
		{
			return SessionTPOVolumeProfile(Input, rTHStartTime, rTHEndTime, ticksPerRow, iBDurationMinutes, maxSessionsToShow, profilePeriod, showVPOC, showVolumeValueArea, showInitialBalance, showIBQuartiles, showIBBiasLabel, showNakedVPOC, showPriorSessionVPOC, showNakedVAHVAL, showPriorSessionLevels, showVolumeLabels, showPerBarBidAskTotals, showSessionStats, showSD2VA, showSD3VA, showVA1Fill, showSD2Fill, showSD3Fill, showNakedSD2, showNakedSD3, volumeFontSize, vALabelFontSize, vALabelXOffset, vABoxLineWidth, vADisplayStyle, showLVNExtension, showCurrentVolumeLevels, hvnLvnOnlyMode, volumeBarDisplayMode, showSessionBuysSells, showSlidingWindowBuysSells, tradeSlidingWindowSeconds, showSlidingWindowTotals, slidingWindowTotalsOffsetMultiple, tradeClassification, showDiagonalImbalance, imbalanceMode, diagonalImbalanceRatio, horizontalImbalanceRatio, minImbalanceVolume, zoneHorizontalControlFloor, showImbalanceZones, minLevelsForImbalanceZone, labelFontSize, extensionLineWidth, currentSessionVbPWidth, priorSessionVbPWidth, currentSessionRightOffset, levelLabelRightOffset, vPOCColor, hVNColor, lVNColor, volumeBarColor, volumeBarOutlineColor, volumeVABoxColor, valueAreaFillColor, iBColor, iBFirstSideColor, iBSecondHighColor, iBSecondLowColor, iBQuartileColor, nakedVPOCColor, nakedVAColor, nakedVAHColor, priorHighLowColor, textColor, sD2Color, sD3Color, lVNExtAboveColor, lVNExtBelowColor, deltaPositiveColor, deltaNegativeColor, bidAskAskColor, bidAskBidColor, buyingImbalanceColor, sellingImbalanceColor, buyZoneColor, sellZoneColor, sameLevelBuyDominanceColor, sameLevelSellDominanceColor, vVAHLineColor, vVAHLineWidth, vVAHLineStyle, vVALLineColor, vVALLineWidth, vVALLineStyle, sD2HLineColor, sD2HLineWidth, sD2HLineStyle, sD2LLineColor, sD2LLineWidth, sD2LLineStyle, sD3HLineColor, sD3HLineWidth, sD3HLineStyle, sD3LLineColor, sD3LLineWidth, sD3LLineStyle, tPOPeriodMinutes, splitGlobexRthTPO, showTPOLetters, showTPOPOC, showTPOVAHVAL, showTPOSD2, showTPOSD3, showNakedTPOPOC, showNakedTPOVAHVAL, showNakedTPOSD2, showNakedTPOSD3, showSinglePrints, showNakedSinglePrints, showPoorHighsLows, showExcess, showDayType, tPOFontSize, showCurrentTPOLevels, tPOLetterColor, tPOPOCColor, tPOVAHColor, tPOVALColor, nakedTPOPOCColor, nakedTPOVAColor, tPOSDTwoColor, nakedTPOSDTwoColor, tPOSDThreeColor, nakedTPOSDThreeColor, singlePrintColor, poorHighLowColor, excessColor, tPOVABoxColor, showAnalyticsPanel, analyticsPanelPosition, showDeltaZScore, deltaZScoreLookbackSessions, deltaZScoreThreshold, showVolumeSkewness, showVAMigration, vAMigrationLookback, showTPOKurtosis, printLevelsAtSessionStart, printLevelsOnDemand, enableRegimeDetection, dominanceWindowBars, discoveryThreshold, auctionThreshold, imbalanceIntensityFloor, useLevel2Book, l2DepthLevels, l2DominanceWeight, l2AbsorptionWeight, showRegimeBackground, showRegimePanel, regimePanelPosition, discoveryBgColor, auctionBgColor, transitionBgColor, buyerDominanceColor, sellerDominanceColor, enableValueAreaRejection, vAPreferTpoLevels, vAIncludeNakedLevels, vAIncludeHighLow, vARequireCumDeltaConfirm, enableMeanRevertVAEdge, mRRequireTpoVaEdge, mRRequireImbalanceOrL2, mRRequireCumDeltaConfirm, mROneSignalPerEdge, enableTrendRunner, enableAutoTrade, autoTradeAccountName, autoTradeQuantity, autoTradeOnlyWhenFlat, autoTradeStartMinutesAfterRth, enableWallMonitor, wallMinSize, wallMedianMultiple, wallNearLevels, wallScanRangeTicks, wallPersistSeconds, wallTestingTicks, wallAbsorbRatio, wallPullFraction, showWallsOnChart, wallMarkerWidth, blockEntryOnOpposingSum, entryMaxOpposingSize);
		}

		public SessionTPOVolumeProfile SessionTPOVolumeProfile(ISeries<double> input, TimeSpan rTHStartTime, TimeSpan rTHEndTime, int ticksPerRow, int iBDurationMinutes, int maxSessionsToShow, STVPProfilePeriod profilePeriod, bool showVPOC, bool showVolumeValueArea, bool showInitialBalance, bool showIBQuartiles, bool showIBBiasLabel, bool showNakedVPOC, bool showPriorSessionVPOC, bool showNakedVAHVAL, bool showPriorSessionLevels, bool showVolumeLabels, bool showPerBarBidAskTotals, bool showSessionStats, bool showSD2VA, bool showSD3VA, bool showVA1Fill, bool showSD2Fill, bool showSD3Fill, bool showNakedSD2, bool showNakedSD3, int volumeFontSize, int vALabelFontSize, int vALabelXOffset, int vABoxLineWidth, STVPDisplayStyle vADisplayStyle, bool showLVNExtension, bool showCurrentVolumeLevels, bool hvnLvnOnlyMode, STVPBarDisplayMode volumeBarDisplayMode, bool showSessionBuysSells, bool showSlidingWindowBuysSells, int tradeSlidingWindowSeconds, bool showSlidingWindowTotals, int slidingWindowTotalsOffsetMultiple, STVPTradeClassification tradeClassification, bool showDiagonalImbalance, STVPImbalanceMode imbalanceMode, double diagonalImbalanceRatio, double horizontalImbalanceRatio, int minImbalanceVolume, int zoneHorizontalControlFloor, bool showImbalanceZones, int minLevelsForImbalanceZone, int labelFontSize, int extensionLineWidth, int currentSessionVbPWidth, int priorSessionVbPWidth, int currentSessionRightOffset, int levelLabelRightOffset, System.Windows.Media.Brush vPOCColor, System.Windows.Media.Brush hVNColor, System.Windows.Media.Brush lVNColor, System.Windows.Media.Brush volumeBarColor, System.Windows.Media.Brush volumeBarOutlineColor, System.Windows.Media.Brush volumeVABoxColor, System.Windows.Media.Brush valueAreaFillColor, System.Windows.Media.Brush iBColor, System.Windows.Media.Brush iBFirstSideColor, System.Windows.Media.Brush iBSecondHighColor, System.Windows.Media.Brush iBSecondLowColor, System.Windows.Media.Brush iBQuartileColor, System.Windows.Media.Brush nakedVPOCColor, System.Windows.Media.Brush nakedVAColor, System.Windows.Media.Brush nakedVAHColor, System.Windows.Media.Brush priorHighLowColor, System.Windows.Media.Brush textColor, System.Windows.Media.Brush sD2Color, System.Windows.Media.Brush sD3Color, System.Windows.Media.Brush lVNExtAboveColor, System.Windows.Media.Brush lVNExtBelowColor, System.Windows.Media.Brush deltaPositiveColor, System.Windows.Media.Brush deltaNegativeColor, System.Windows.Media.Brush bidAskAskColor, System.Windows.Media.Brush bidAskBidColor, System.Windows.Media.Brush buyingImbalanceColor, System.Windows.Media.Brush sellingImbalanceColor, System.Windows.Media.Brush buyZoneColor, System.Windows.Media.Brush sellZoneColor, System.Windows.Media.Brush sameLevelBuyDominanceColor, System.Windows.Media.Brush sameLevelSellDominanceColor, System.Windows.Media.Brush vVAHLineColor, int vVAHLineWidth, STVPLineStyle vVAHLineStyle, System.Windows.Media.Brush vVALLineColor, int vVALLineWidth, STVPLineStyle vVALLineStyle, System.Windows.Media.Brush sD2HLineColor, int sD2HLineWidth, STVPLineStyle sD2HLineStyle, System.Windows.Media.Brush sD2LLineColor, int sD2LLineWidth, STVPLineStyle sD2LLineStyle, System.Windows.Media.Brush sD3HLineColor, int sD3HLineWidth, STVPLineStyle sD3HLineStyle, System.Windows.Media.Brush sD3LLineColor, int sD3LLineWidth, STVPLineStyle sD3LLineStyle, int tPOPeriodMinutes, bool splitGlobexRthTPO, bool showTPOLetters, bool showTPOPOC, bool showTPOVAHVAL, bool showTPOSD2, bool showTPOSD3, bool showNakedTPOPOC, bool showNakedTPOVAHVAL, bool showNakedTPOSD2, bool showNakedTPOSD3, bool showSinglePrints, bool showNakedSinglePrints, bool showPoorHighsLows, bool showExcess, bool showDayType, int tPOFontSize, bool showCurrentTPOLevels, System.Windows.Media.Brush tPOLetterColor, System.Windows.Media.Brush tPOPOCColor, System.Windows.Media.Brush tPOVAHColor, System.Windows.Media.Brush tPOVALColor, System.Windows.Media.Brush nakedTPOPOCColor, System.Windows.Media.Brush nakedTPOVAColor, System.Windows.Media.Brush tPOSDTwoColor, System.Windows.Media.Brush nakedTPOSDTwoColor, System.Windows.Media.Brush tPOSDThreeColor, System.Windows.Media.Brush nakedTPOSDThreeColor, System.Windows.Media.Brush singlePrintColor, System.Windows.Media.Brush poorHighLowColor, System.Windows.Media.Brush excessColor, System.Windows.Media.Brush tPOVABoxColor, bool showAnalyticsPanel, STVPPanelPosition analyticsPanelPosition, bool showDeltaZScore, int deltaZScoreLookbackSessions, double deltaZScoreThreshold, bool showVolumeSkewness, bool showVAMigration, int vAMigrationLookback, bool showTPOKurtosis, bool printLevelsAtSessionStart, bool printLevelsOnDemand, bool enableRegimeDetection, int dominanceWindowBars, double discoveryThreshold, double auctionThreshold, double imbalanceIntensityFloor, bool useLevel2Book, int l2DepthLevels, double l2DominanceWeight, double l2AbsorptionWeight, bool showRegimeBackground, bool showRegimePanel, STVPPanelPosition regimePanelPosition, System.Windows.Media.Brush discoveryBgColor, System.Windows.Media.Brush auctionBgColor, System.Windows.Media.Brush transitionBgColor, System.Windows.Media.Brush buyerDominanceColor, System.Windows.Media.Brush sellerDominanceColor, bool enableValueAreaRejection, bool vAPreferTpoLevels, bool vAIncludeNakedLevels, bool vAIncludeHighLow, bool vARequireCumDeltaConfirm, bool enableMeanRevertVAEdge, bool mRRequireTpoVaEdge, bool mRRequireImbalanceOrL2, bool mRRequireCumDeltaConfirm, bool mROneSignalPerEdge, bool enableTrendRunner, bool enableAutoTrade, string autoTradeAccountName, int autoTradeQuantity, bool autoTradeOnlyWhenFlat, int autoTradeStartMinutesAfterRth, bool enableWallMonitor, int wallMinSize, double wallMedianMultiple, int wallNearLevels, int wallScanRangeTicks, double wallPersistSeconds, int wallTestingTicks, double wallAbsorbRatio, double wallPullFraction, bool showWallsOnChart, int wallMarkerWidth, bool blockEntryOnOpposingSum, int entryMaxOpposingSize)
		{
			if (cacheSessionTPOVolumeProfile != null)
				for (int idx = 0; idx < cacheSessionTPOVolumeProfile.Length; idx++)
					if (cacheSessionTPOVolumeProfile[idx] != null && cacheSessionTPOVolumeProfile[idx].RTHStartTime == rTHStartTime && cacheSessionTPOVolumeProfile[idx].RTHEndTime == rTHEndTime && cacheSessionTPOVolumeProfile[idx].TicksPerRow == ticksPerRow && cacheSessionTPOVolumeProfile[idx].IBDurationMinutes == iBDurationMinutes && cacheSessionTPOVolumeProfile[idx].MaxSessionsToShow == maxSessionsToShow && cacheSessionTPOVolumeProfile[idx].ProfilePeriod == profilePeriod && cacheSessionTPOVolumeProfile[idx].ShowVPOC == showVPOC && cacheSessionTPOVolumeProfile[idx].ShowVolumeValueArea == showVolumeValueArea && cacheSessionTPOVolumeProfile[idx].ShowInitialBalance == showInitialBalance && cacheSessionTPOVolumeProfile[idx].ShowIBQuartiles == showIBQuartiles && cacheSessionTPOVolumeProfile[idx].ShowIBBiasLabel == showIBBiasLabel && cacheSessionTPOVolumeProfile[idx].ShowNakedVPOC == showNakedVPOC && cacheSessionTPOVolumeProfile[idx].ShowPriorSessionVPOC == showPriorSessionVPOC && cacheSessionTPOVolumeProfile[idx].ShowNakedVAHVAL == showNakedVAHVAL && cacheSessionTPOVolumeProfile[idx].ShowPriorSessionLevels == showPriorSessionLevels && cacheSessionTPOVolumeProfile[idx].ShowVolumeLabels == showVolumeLabels && cacheSessionTPOVolumeProfile[idx].ShowPerBarBidAskTotals == showPerBarBidAskTotals && cacheSessionTPOVolumeProfile[idx].ShowSessionStats == showSessionStats && cacheSessionTPOVolumeProfile[idx].ShowSD2VA == showSD2VA && cacheSessionTPOVolumeProfile[idx].ShowSD3VA == showSD3VA && cacheSessionTPOVolumeProfile[idx].ShowVA1Fill == showVA1Fill && cacheSessionTPOVolumeProfile[idx].ShowSD2Fill == showSD2Fill && cacheSessionTPOVolumeProfile[idx].ShowSD3Fill == showSD3Fill && cacheSessionTPOVolumeProfile[idx].ShowNakedSD2 == showNakedSD2 && cacheSessionTPOVolumeProfile[idx].ShowNakedSD3 == showNakedSD3 && cacheSessionTPOVolumeProfile[idx].VolumeFontSize == volumeFontSize && cacheSessionTPOVolumeProfile[idx].VALabelFontSize == vALabelFontSize && cacheSessionTPOVolumeProfile[idx].VALabelXOffset == vALabelXOffset && cacheSessionTPOVolumeProfile[idx].VABoxLineWidth == vABoxLineWidth && cacheSessionTPOVolumeProfile[idx].VADisplayStyle == vADisplayStyle && cacheSessionTPOVolumeProfile[idx].ShowLVNExtension == showLVNExtension && cacheSessionTPOVolumeProfile[idx].ShowCurrentVolumeLevels == showCurrentVolumeLevels && cacheSessionTPOVolumeProfile[idx].HvnLvnOnlyMode == hvnLvnOnlyMode && cacheSessionTPOVolumeProfile[idx].VolumeBarDisplayMode == volumeBarDisplayMode && cacheSessionTPOVolumeProfile[idx].ShowSessionBuysSells == showSessionBuysSells && cacheSessionTPOVolumeProfile[idx].ShowSlidingWindowBuysSells == showSlidingWindowBuysSells && cacheSessionTPOVolumeProfile[idx].TradeSlidingWindowSeconds == tradeSlidingWindowSeconds && cacheSessionTPOVolumeProfile[idx].ShowSlidingWindowTotals == showSlidingWindowTotals && cacheSessionTPOVolumeProfile[idx].SlidingWindowTotalsOffsetMultiple == slidingWindowTotalsOffsetMultiple && cacheSessionTPOVolumeProfile[idx].TradeClassification == tradeClassification && cacheSessionTPOVolumeProfile[idx].ShowDiagonalImbalance == showDiagonalImbalance && cacheSessionTPOVolumeProfile[idx].ImbalanceMode == imbalanceMode && cacheSessionTPOVolumeProfile[idx].DiagonalImbalanceRatio == diagonalImbalanceRatio && cacheSessionTPOVolumeProfile[idx].HorizontalImbalanceRatio == horizontalImbalanceRatio && cacheSessionTPOVolumeProfile[idx].MinImbalanceVolume == minImbalanceVolume && cacheSessionTPOVolumeProfile[idx].ZoneHorizontalControlFloor == zoneHorizontalControlFloor && cacheSessionTPOVolumeProfile[idx].ShowImbalanceZones == showImbalanceZones && cacheSessionTPOVolumeProfile[idx].MinLevelsForImbalanceZone == minLevelsForImbalanceZone && cacheSessionTPOVolumeProfile[idx].LabelFontSize == labelFontSize && cacheSessionTPOVolumeProfile[idx].ExtensionLineWidth == extensionLineWidth && cacheSessionTPOVolumeProfile[idx].CurrentSessionVbPWidth == currentSessionVbPWidth && cacheSessionTPOVolumeProfile[idx].PriorSessionVbPWidth == priorSessionVbPWidth && cacheSessionTPOVolumeProfile[idx].CurrentSessionRightOffset == currentSessionRightOffset && cacheSessionTPOVolumeProfile[idx].LevelLabelRightOffset == levelLabelRightOffset && cacheSessionTPOVolumeProfile[idx].VPOCColor == vPOCColor && cacheSessionTPOVolumeProfile[idx].HVNColor == hVNColor && cacheSessionTPOVolumeProfile[idx].LVNColor == lVNColor && cacheSessionTPOVolumeProfile[idx].VolumeBarColor == volumeBarColor && cacheSessionTPOVolumeProfile[idx].VolumeBarOutlineColor == volumeBarOutlineColor && cacheSessionTPOVolumeProfile[idx].VolumeVABoxColor == volumeVABoxColor && cacheSessionTPOVolumeProfile[idx].ValueAreaFillColor == valueAreaFillColor && cacheSessionTPOVolumeProfile[idx].IBColor == iBColor && cacheSessionTPOVolumeProfile[idx].IBFirstSideColor == iBFirstSideColor && cacheSessionTPOVolumeProfile[idx].IBSecondHighColor == iBSecondHighColor && cacheSessionTPOVolumeProfile[idx].IBSecondLowColor == iBSecondLowColor && cacheSessionTPOVolumeProfile[idx].IBQuartileColor == iBQuartileColor && cacheSessionTPOVolumeProfile[idx].NakedVPOCColor == nakedVPOCColor && cacheSessionTPOVolumeProfile[idx].NakedVAColor == nakedVAColor && cacheSessionTPOVolumeProfile[idx].NakedVAHColor == nakedVAHColor && cacheSessionTPOVolumeProfile[idx].PriorHighLowColor == priorHighLowColor && cacheSessionTPOVolumeProfile[idx].TextColor == textColor && cacheSessionTPOVolumeProfile[idx].SD2Color == sD2Color && cacheSessionTPOVolumeProfile[idx].SD3Color == sD3Color && cacheSessionTPOVolumeProfile[idx].LVNExtAboveColor == lVNExtAboveColor && cacheSessionTPOVolumeProfile[idx].LVNExtBelowColor == lVNExtBelowColor && cacheSessionTPOVolumeProfile[idx].DeltaPositiveColor == deltaPositiveColor && cacheSessionTPOVolumeProfile[idx].DeltaNegativeColor == deltaNegativeColor && cacheSessionTPOVolumeProfile[idx].BidAskAskColor == bidAskAskColor && cacheSessionTPOVolumeProfile[idx].BidAskBidColor == bidAskBidColor && cacheSessionTPOVolumeProfile[idx].BuyingImbalanceColor == buyingImbalanceColor && cacheSessionTPOVolumeProfile[idx].SellingImbalanceColor == sellingImbalanceColor && cacheSessionTPOVolumeProfile[idx].BuyZoneColor == buyZoneColor && cacheSessionTPOVolumeProfile[idx].SellZoneColor == sellZoneColor && cacheSessionTPOVolumeProfile[idx].SameLevelBuyDominanceColor == sameLevelBuyDominanceColor && cacheSessionTPOVolumeProfile[idx].SameLevelSellDominanceColor == sameLevelSellDominanceColor && cacheSessionTPOVolumeProfile[idx].VVAHLineColor == vVAHLineColor && cacheSessionTPOVolumeProfile[idx].VVAHLineWidth == vVAHLineWidth && cacheSessionTPOVolumeProfile[idx].VVAHLineStyle == vVAHLineStyle && cacheSessionTPOVolumeProfile[idx].VVALLineColor == vVALLineColor && cacheSessionTPOVolumeProfile[idx].VVALLineWidth == vVALLineWidth && cacheSessionTPOVolumeProfile[idx].VVALLineStyle == vVALLineStyle && cacheSessionTPOVolumeProfile[idx].SD2HLineColor == sD2HLineColor && cacheSessionTPOVolumeProfile[idx].SD2HLineWidth == sD2HLineWidth && cacheSessionTPOVolumeProfile[idx].SD2HLineStyle == sD2HLineStyle && cacheSessionTPOVolumeProfile[idx].SD2LLineColor == sD2LLineColor && cacheSessionTPOVolumeProfile[idx].SD2LLineWidth == sD2LLineWidth && cacheSessionTPOVolumeProfile[idx].SD2LLineStyle == sD2LLineStyle && cacheSessionTPOVolumeProfile[idx].SD3HLineColor == sD3HLineColor && cacheSessionTPOVolumeProfile[idx].SD3HLineWidth == sD3HLineWidth && cacheSessionTPOVolumeProfile[idx].SD3HLineStyle == sD3HLineStyle && cacheSessionTPOVolumeProfile[idx].SD3LLineColor == sD3LLineColor && cacheSessionTPOVolumeProfile[idx].SD3LLineWidth == sD3LLineWidth && cacheSessionTPOVolumeProfile[idx].SD3LLineStyle == sD3LLineStyle && cacheSessionTPOVolumeProfile[idx].TPOPeriodMinutes == tPOPeriodMinutes && cacheSessionTPOVolumeProfile[idx].SplitGlobexRthTPO == splitGlobexRthTPO && cacheSessionTPOVolumeProfile[idx].ShowTPOLetters == showTPOLetters && cacheSessionTPOVolumeProfile[idx].ShowTPOPOC == showTPOPOC && cacheSessionTPOVolumeProfile[idx].ShowTPOVAHVAL == showTPOVAHVAL && cacheSessionTPOVolumeProfile[idx].ShowTPOSD2 == showTPOSD2 && cacheSessionTPOVolumeProfile[idx].ShowTPOSD3 == showTPOSD3 && cacheSessionTPOVolumeProfile[idx].ShowNakedTPOPOC == showNakedTPOPOC && cacheSessionTPOVolumeProfile[idx].ShowNakedTPOVAHVAL == showNakedTPOVAHVAL && cacheSessionTPOVolumeProfile[idx].ShowNakedTPOSD2 == showNakedTPOSD2 && cacheSessionTPOVolumeProfile[idx].ShowNakedTPOSD3 == showNakedTPOSD3 && cacheSessionTPOVolumeProfile[idx].ShowSinglePrints == showSinglePrints && cacheSessionTPOVolumeProfile[idx].ShowNakedSinglePrints == showNakedSinglePrints && cacheSessionTPOVolumeProfile[idx].ShowPoorHighsLows == showPoorHighsLows && cacheSessionTPOVolumeProfile[idx].ShowExcess == showExcess && cacheSessionTPOVolumeProfile[idx].ShowDayType == showDayType && cacheSessionTPOVolumeProfile[idx].TPOFontSize == tPOFontSize && cacheSessionTPOVolumeProfile[idx].ShowCurrentTPOLevels == showCurrentTPOLevels && cacheSessionTPOVolumeProfile[idx].TPOLetterColor == tPOLetterColor && cacheSessionTPOVolumeProfile[idx].TPOPOCColor == tPOPOCColor && cacheSessionTPOVolumeProfile[idx].TPOVAHColor == tPOVAHColor && cacheSessionTPOVolumeProfile[idx].TPOVALColor == tPOVALColor && cacheSessionTPOVolumeProfile[idx].NakedTPOPOCColor == nakedTPOPOCColor && cacheSessionTPOVolumeProfile[idx].NakedTPOVAColor == nakedTPOVAColor && cacheSessionTPOVolumeProfile[idx].TPOSDTwoColor == tPOSDTwoColor && cacheSessionTPOVolumeProfile[idx].NakedTPOSDTwoColor == nakedTPOSDTwoColor && cacheSessionTPOVolumeProfile[idx].TPOSDThreeColor == tPOSDThreeColor && cacheSessionTPOVolumeProfile[idx].NakedTPOSDThreeColor == nakedTPOSDThreeColor && cacheSessionTPOVolumeProfile[idx].SinglePrintColor == singlePrintColor && cacheSessionTPOVolumeProfile[idx].PoorHighLowColor == poorHighLowColor && cacheSessionTPOVolumeProfile[idx].ExcessColor == excessColor && cacheSessionTPOVolumeProfile[idx].TPOVABoxColor == tPOVABoxColor && cacheSessionTPOVolumeProfile[idx].ShowAnalyticsPanel == showAnalyticsPanel && cacheSessionTPOVolumeProfile[idx].AnalyticsPanelPosition == analyticsPanelPosition && cacheSessionTPOVolumeProfile[idx].ShowDeltaZScore == showDeltaZScore && cacheSessionTPOVolumeProfile[idx].DeltaZScoreLookbackSessions == deltaZScoreLookbackSessions && cacheSessionTPOVolumeProfile[idx].DeltaZScoreThreshold == deltaZScoreThreshold && cacheSessionTPOVolumeProfile[idx].ShowVolumeSkewness == showVolumeSkewness && cacheSessionTPOVolumeProfile[idx].ShowVAMigration == showVAMigration && cacheSessionTPOVolumeProfile[idx].VAMigrationLookback == vAMigrationLookback && cacheSessionTPOVolumeProfile[idx].ShowTPOKurtosis == showTPOKurtosis && cacheSessionTPOVolumeProfile[idx].PrintLevelsAtSessionStart == printLevelsAtSessionStart && cacheSessionTPOVolumeProfile[idx].PrintLevelsOnDemand == printLevelsOnDemand && cacheSessionTPOVolumeProfile[idx].EnableRegimeDetection == enableRegimeDetection && cacheSessionTPOVolumeProfile[idx].DominanceWindowBars == dominanceWindowBars && cacheSessionTPOVolumeProfile[idx].DiscoveryThreshold == discoveryThreshold && cacheSessionTPOVolumeProfile[idx].AuctionThreshold == auctionThreshold && cacheSessionTPOVolumeProfile[idx].ImbalanceIntensityFloor == imbalanceIntensityFloor && cacheSessionTPOVolumeProfile[idx].UseLevel2Book == useLevel2Book && cacheSessionTPOVolumeProfile[idx].L2DepthLevels == l2DepthLevels && cacheSessionTPOVolumeProfile[idx].L2DominanceWeight == l2DominanceWeight && cacheSessionTPOVolumeProfile[idx].L2AbsorptionWeight == l2AbsorptionWeight && cacheSessionTPOVolumeProfile[idx].ShowRegimeBackground == showRegimeBackground && cacheSessionTPOVolumeProfile[idx].ShowRegimePanel == showRegimePanel && cacheSessionTPOVolumeProfile[idx].RegimePanelPosition == regimePanelPosition && cacheSessionTPOVolumeProfile[idx].DiscoveryBgColor == discoveryBgColor && cacheSessionTPOVolumeProfile[idx].AuctionBgColor == auctionBgColor && cacheSessionTPOVolumeProfile[idx].TransitionBgColor == transitionBgColor && cacheSessionTPOVolumeProfile[idx].BuyerDominanceColor == buyerDominanceColor && cacheSessionTPOVolumeProfile[idx].SellerDominanceColor == sellerDominanceColor && cacheSessionTPOVolumeProfile[idx].EnableValueAreaRejection == enableValueAreaRejection && cacheSessionTPOVolumeProfile[idx].VAPreferTpoLevels == vAPreferTpoLevels && cacheSessionTPOVolumeProfile[idx].VAIncludeNakedLevels == vAIncludeNakedLevels && cacheSessionTPOVolumeProfile[idx].VAIncludeHighLow == vAIncludeHighLow && cacheSessionTPOVolumeProfile[idx].VARequireCumDeltaConfirm == vARequireCumDeltaConfirm && cacheSessionTPOVolumeProfile[idx].EnableMeanRevertVAEdge == enableMeanRevertVAEdge && cacheSessionTPOVolumeProfile[idx].MRRequireTpoVaEdge == mRRequireTpoVaEdge && cacheSessionTPOVolumeProfile[idx].MRRequireImbalanceOrL2 == mRRequireImbalanceOrL2 && cacheSessionTPOVolumeProfile[idx].MRRequireCumDeltaConfirm == mRRequireCumDeltaConfirm && cacheSessionTPOVolumeProfile[idx].MROneSignalPerEdge == mROneSignalPerEdge && cacheSessionTPOVolumeProfile[idx].EnableTrendRunner == enableTrendRunner && cacheSessionTPOVolumeProfile[idx].EnableAutoTrade == enableAutoTrade && cacheSessionTPOVolumeProfile[idx].AutoTradeAccountName == autoTradeAccountName && cacheSessionTPOVolumeProfile[idx].AutoTradeQuantity == autoTradeQuantity && cacheSessionTPOVolumeProfile[idx].AutoTradeOnlyWhenFlat == autoTradeOnlyWhenFlat && cacheSessionTPOVolumeProfile[idx].AutoTradeStartMinutesAfterRth == autoTradeStartMinutesAfterRth && cacheSessionTPOVolumeProfile[idx].EnableWallMonitor == enableWallMonitor && cacheSessionTPOVolumeProfile[idx].WallMinSize == wallMinSize && cacheSessionTPOVolumeProfile[idx].WallMedianMultiple == wallMedianMultiple && cacheSessionTPOVolumeProfile[idx].WallNearLevels == wallNearLevels && cacheSessionTPOVolumeProfile[idx].WallScanRangeTicks == wallScanRangeTicks && cacheSessionTPOVolumeProfile[idx].WallPersistSeconds == wallPersistSeconds && cacheSessionTPOVolumeProfile[idx].WallTestingTicks == wallTestingTicks && cacheSessionTPOVolumeProfile[idx].WallAbsorbRatio == wallAbsorbRatio && cacheSessionTPOVolumeProfile[idx].WallPullFraction == wallPullFraction && cacheSessionTPOVolumeProfile[idx].ShowWallsOnChart == showWallsOnChart && cacheSessionTPOVolumeProfile[idx].WallMarkerWidth == wallMarkerWidth && cacheSessionTPOVolumeProfile[idx].BlockEntryOnOpposingSum == blockEntryOnOpposingSum && cacheSessionTPOVolumeProfile[idx].EntryMaxOpposingSize == entryMaxOpposingSize && cacheSessionTPOVolumeProfile[idx].EqualsInput(input))
						return cacheSessionTPOVolumeProfile[idx];
			return CacheIndicator<SessionTPOVolumeProfile>(new SessionTPOVolumeProfile(){ RTHStartTime = rTHStartTime, RTHEndTime = rTHEndTime, TicksPerRow = ticksPerRow, IBDurationMinutes = iBDurationMinutes, MaxSessionsToShow = maxSessionsToShow, ProfilePeriod = profilePeriod, ShowVPOC = showVPOC, ShowVolumeValueArea = showVolumeValueArea, ShowInitialBalance = showInitialBalance, ShowIBQuartiles = showIBQuartiles, ShowIBBiasLabel = showIBBiasLabel, ShowNakedVPOC = showNakedVPOC, ShowPriorSessionVPOC = showPriorSessionVPOC, ShowNakedVAHVAL = showNakedVAHVAL, ShowPriorSessionLevels = showPriorSessionLevels, ShowVolumeLabels = showVolumeLabels, ShowPerBarBidAskTotals = showPerBarBidAskTotals, ShowSessionStats = showSessionStats, ShowSD2VA = showSD2VA, ShowSD3VA = showSD3VA, ShowVA1Fill = showVA1Fill, ShowSD2Fill = showSD2Fill, ShowSD3Fill = showSD3Fill, ShowNakedSD2 = showNakedSD2, ShowNakedSD3 = showNakedSD3, VolumeFontSize = volumeFontSize, VALabelFontSize = vALabelFontSize, VALabelXOffset = vALabelXOffset, VABoxLineWidth = vABoxLineWidth, VADisplayStyle = vADisplayStyle, ShowLVNExtension = showLVNExtension, ShowCurrentVolumeLevels = showCurrentVolumeLevels, HvnLvnOnlyMode = hvnLvnOnlyMode, VolumeBarDisplayMode = volumeBarDisplayMode, ShowSessionBuysSells = showSessionBuysSells, ShowSlidingWindowBuysSells = showSlidingWindowBuysSells, TradeSlidingWindowSeconds = tradeSlidingWindowSeconds, ShowSlidingWindowTotals = showSlidingWindowTotals, SlidingWindowTotalsOffsetMultiple = slidingWindowTotalsOffsetMultiple, TradeClassification = tradeClassification, ShowDiagonalImbalance = showDiagonalImbalance, ImbalanceMode = imbalanceMode, DiagonalImbalanceRatio = diagonalImbalanceRatio, HorizontalImbalanceRatio = horizontalImbalanceRatio, MinImbalanceVolume = minImbalanceVolume, ZoneHorizontalControlFloor = zoneHorizontalControlFloor, ShowImbalanceZones = showImbalanceZones, MinLevelsForImbalanceZone = minLevelsForImbalanceZone, LabelFontSize = labelFontSize, ExtensionLineWidth = extensionLineWidth, CurrentSessionVbPWidth = currentSessionVbPWidth, PriorSessionVbPWidth = priorSessionVbPWidth, CurrentSessionRightOffset = currentSessionRightOffset, LevelLabelRightOffset = levelLabelRightOffset, VPOCColor = vPOCColor, HVNColor = hVNColor, LVNColor = lVNColor, VolumeBarColor = volumeBarColor, VolumeBarOutlineColor = volumeBarOutlineColor, VolumeVABoxColor = volumeVABoxColor, ValueAreaFillColor = valueAreaFillColor, IBColor = iBColor, IBFirstSideColor = iBFirstSideColor, IBSecondHighColor = iBSecondHighColor, IBSecondLowColor = iBSecondLowColor, IBQuartileColor = iBQuartileColor, NakedVPOCColor = nakedVPOCColor, NakedVAColor = nakedVAColor, NakedVAHColor = nakedVAHColor, PriorHighLowColor = priorHighLowColor, TextColor = textColor, SD2Color = sD2Color, SD3Color = sD3Color, LVNExtAboveColor = lVNExtAboveColor, LVNExtBelowColor = lVNExtBelowColor, DeltaPositiveColor = deltaPositiveColor, DeltaNegativeColor = deltaNegativeColor, BidAskAskColor = bidAskAskColor, BidAskBidColor = bidAskBidColor, BuyingImbalanceColor = buyingImbalanceColor, SellingImbalanceColor = sellingImbalanceColor, BuyZoneColor = buyZoneColor, SellZoneColor = sellZoneColor, SameLevelBuyDominanceColor = sameLevelBuyDominanceColor, SameLevelSellDominanceColor = sameLevelSellDominanceColor, VVAHLineColor = vVAHLineColor, VVAHLineWidth = vVAHLineWidth, VVAHLineStyle = vVAHLineStyle, VVALLineColor = vVALLineColor, VVALLineWidth = vVALLineWidth, VVALLineStyle = vVALLineStyle, SD2HLineColor = sD2HLineColor, SD2HLineWidth = sD2HLineWidth, SD2HLineStyle = sD2HLineStyle, SD2LLineColor = sD2LLineColor, SD2LLineWidth = sD2LLineWidth, SD2LLineStyle = sD2LLineStyle, SD3HLineColor = sD3HLineColor, SD3HLineWidth = sD3HLineWidth, SD3HLineStyle = sD3HLineStyle, SD3LLineColor = sD3LLineColor, SD3LLineWidth = sD3LLineWidth, SD3LLineStyle = sD3LLineStyle, TPOPeriodMinutes = tPOPeriodMinutes, SplitGlobexRthTPO = splitGlobexRthTPO, ShowTPOLetters = showTPOLetters, ShowTPOPOC = showTPOPOC, ShowTPOVAHVAL = showTPOVAHVAL, ShowTPOSD2 = showTPOSD2, ShowTPOSD3 = showTPOSD3, ShowNakedTPOPOC = showNakedTPOPOC, ShowNakedTPOVAHVAL = showNakedTPOVAHVAL, ShowNakedTPOSD2 = showNakedTPOSD2, ShowNakedTPOSD3 = showNakedTPOSD3, ShowSinglePrints = showSinglePrints, ShowNakedSinglePrints = showNakedSinglePrints, ShowPoorHighsLows = showPoorHighsLows, ShowExcess = showExcess, ShowDayType = showDayType, TPOFontSize = tPOFontSize, ShowCurrentTPOLevels = showCurrentTPOLevels, TPOLetterColor = tPOLetterColor, TPOPOCColor = tPOPOCColor, TPOVAHColor = tPOVAHColor, TPOVALColor = tPOVALColor, NakedTPOPOCColor = nakedTPOPOCColor, NakedTPOVAColor = nakedTPOVAColor, TPOSDTwoColor = tPOSDTwoColor, NakedTPOSDTwoColor = nakedTPOSDTwoColor, TPOSDThreeColor = tPOSDThreeColor, NakedTPOSDThreeColor = nakedTPOSDThreeColor, SinglePrintColor = singlePrintColor, PoorHighLowColor = poorHighLowColor, ExcessColor = excessColor, TPOVABoxColor = tPOVABoxColor, ShowAnalyticsPanel = showAnalyticsPanel, AnalyticsPanelPosition = analyticsPanelPosition, ShowDeltaZScore = showDeltaZScore, DeltaZScoreLookbackSessions = deltaZScoreLookbackSessions, DeltaZScoreThreshold = deltaZScoreThreshold, ShowVolumeSkewness = showVolumeSkewness, ShowVAMigration = showVAMigration, VAMigrationLookback = vAMigrationLookback, ShowTPOKurtosis = showTPOKurtosis, PrintLevelsAtSessionStart = printLevelsAtSessionStart, PrintLevelsOnDemand = printLevelsOnDemand, EnableRegimeDetection = enableRegimeDetection, DominanceWindowBars = dominanceWindowBars, DiscoveryThreshold = discoveryThreshold, AuctionThreshold = auctionThreshold, ImbalanceIntensityFloor = imbalanceIntensityFloor, UseLevel2Book = useLevel2Book, L2DepthLevels = l2DepthLevels, L2DominanceWeight = l2DominanceWeight, L2AbsorptionWeight = l2AbsorptionWeight, ShowRegimeBackground = showRegimeBackground, ShowRegimePanel = showRegimePanel, RegimePanelPosition = regimePanelPosition, DiscoveryBgColor = discoveryBgColor, AuctionBgColor = auctionBgColor, TransitionBgColor = transitionBgColor, BuyerDominanceColor = buyerDominanceColor, SellerDominanceColor = sellerDominanceColor, EnableValueAreaRejection = enableValueAreaRejection, VAPreferTpoLevels = vAPreferTpoLevels, VAIncludeNakedLevels = vAIncludeNakedLevels, VAIncludeHighLow = vAIncludeHighLow, VARequireCumDeltaConfirm = vARequireCumDeltaConfirm, EnableMeanRevertVAEdge = enableMeanRevertVAEdge, MRRequireTpoVaEdge = mRRequireTpoVaEdge, MRRequireImbalanceOrL2 = mRRequireImbalanceOrL2, MRRequireCumDeltaConfirm = mRRequireCumDeltaConfirm, MROneSignalPerEdge = mROneSignalPerEdge, EnableTrendRunner = enableTrendRunner, EnableAutoTrade = enableAutoTrade, AutoTradeAccountName = autoTradeAccountName, AutoTradeQuantity = autoTradeQuantity, AutoTradeOnlyWhenFlat = autoTradeOnlyWhenFlat, AutoTradeStartMinutesAfterRth = autoTradeStartMinutesAfterRth, EnableWallMonitor = enableWallMonitor, WallMinSize = wallMinSize, WallMedianMultiple = wallMedianMultiple, WallNearLevels = wallNearLevels, WallScanRangeTicks = wallScanRangeTicks, WallPersistSeconds = wallPersistSeconds, WallTestingTicks = wallTestingTicks, WallAbsorbRatio = wallAbsorbRatio, WallPullFraction = wallPullFraction, ShowWallsOnChart = showWallsOnChart, WallMarkerWidth = wallMarkerWidth, BlockEntryOnOpposingSum = blockEntryOnOpposingSum, EntryMaxOpposingSize = entryMaxOpposingSize }, input, ref cacheSessionTPOVolumeProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SessionTPOVolumeProfile SessionTPOVolumeProfile(TimeSpan rTHStartTime, TimeSpan rTHEndTime, int ticksPerRow, int iBDurationMinutes, int maxSessionsToShow, STVPProfilePeriod profilePeriod, bool showVPOC, bool showVolumeValueArea, bool showInitialBalance, bool showIBQuartiles, bool showIBBiasLabel, bool showNakedVPOC, bool showPriorSessionVPOC, bool showNakedVAHVAL, bool showPriorSessionLevels, bool showVolumeLabels, bool showPerBarBidAskTotals, bool showSessionStats, bool showSD2VA, bool showSD3VA, bool showVA1Fill, bool showSD2Fill, bool showSD3Fill, bool showNakedSD2, bool showNakedSD3, int volumeFontSize, int vALabelFontSize, int vALabelXOffset, int vABoxLineWidth, STVPDisplayStyle vADisplayStyle, bool showLVNExtension, bool showCurrentVolumeLevels, bool hvnLvnOnlyMode, STVPBarDisplayMode volumeBarDisplayMode, bool showSessionBuysSells, bool showSlidingWindowBuysSells, int tradeSlidingWindowSeconds, bool showSlidingWindowTotals, int slidingWindowTotalsOffsetMultiple, STVPTradeClassification tradeClassification, bool showDiagonalImbalance, STVPImbalanceMode imbalanceMode, double diagonalImbalanceRatio, double horizontalImbalanceRatio, int minImbalanceVolume, int zoneHorizontalControlFloor, bool showImbalanceZones, int minLevelsForImbalanceZone, int labelFontSize, int extensionLineWidth, int currentSessionVbPWidth, int priorSessionVbPWidth, int currentSessionRightOffset, int levelLabelRightOffset, System.Windows.Media.Brush vPOCColor, System.Windows.Media.Brush hVNColor, System.Windows.Media.Brush lVNColor, System.Windows.Media.Brush volumeBarColor, System.Windows.Media.Brush volumeBarOutlineColor, System.Windows.Media.Brush volumeVABoxColor, System.Windows.Media.Brush valueAreaFillColor, System.Windows.Media.Brush iBColor, System.Windows.Media.Brush iBFirstSideColor, System.Windows.Media.Brush iBSecondHighColor, System.Windows.Media.Brush iBSecondLowColor, System.Windows.Media.Brush iBQuartileColor, System.Windows.Media.Brush nakedVPOCColor, System.Windows.Media.Brush nakedVAColor, System.Windows.Media.Brush nakedVAHColor, System.Windows.Media.Brush priorHighLowColor, System.Windows.Media.Brush textColor, System.Windows.Media.Brush sD2Color, System.Windows.Media.Brush sD3Color, System.Windows.Media.Brush lVNExtAboveColor, System.Windows.Media.Brush lVNExtBelowColor, System.Windows.Media.Brush deltaPositiveColor, System.Windows.Media.Brush deltaNegativeColor, System.Windows.Media.Brush bidAskAskColor, System.Windows.Media.Brush bidAskBidColor, System.Windows.Media.Brush buyingImbalanceColor, System.Windows.Media.Brush sellingImbalanceColor, System.Windows.Media.Brush buyZoneColor, System.Windows.Media.Brush sellZoneColor, System.Windows.Media.Brush sameLevelBuyDominanceColor, System.Windows.Media.Brush sameLevelSellDominanceColor, System.Windows.Media.Brush vVAHLineColor, int vVAHLineWidth, STVPLineStyle vVAHLineStyle, System.Windows.Media.Brush vVALLineColor, int vVALLineWidth, STVPLineStyle vVALLineStyle, System.Windows.Media.Brush sD2HLineColor, int sD2HLineWidth, STVPLineStyle sD2HLineStyle, System.Windows.Media.Brush sD2LLineColor, int sD2LLineWidth, STVPLineStyle sD2LLineStyle, System.Windows.Media.Brush sD3HLineColor, int sD3HLineWidth, STVPLineStyle sD3HLineStyle, System.Windows.Media.Brush sD3LLineColor, int sD3LLineWidth, STVPLineStyle sD3LLineStyle, int tPOPeriodMinutes, bool splitGlobexRthTPO, bool showTPOLetters, bool showTPOPOC, bool showTPOVAHVAL, bool showTPOSD2, bool showTPOSD3, bool showNakedTPOPOC, bool showNakedTPOVAHVAL, bool showNakedTPOSD2, bool showNakedTPOSD3, bool showSinglePrints, bool showNakedSinglePrints, bool showPoorHighsLows, bool showExcess, bool showDayType, int tPOFontSize, bool showCurrentTPOLevels, System.Windows.Media.Brush tPOLetterColor, System.Windows.Media.Brush tPOPOCColor, System.Windows.Media.Brush tPOVAHColor, System.Windows.Media.Brush tPOVALColor, System.Windows.Media.Brush nakedTPOPOCColor, System.Windows.Media.Brush nakedTPOVAColor, System.Windows.Media.Brush tPOSDTwoColor, System.Windows.Media.Brush nakedTPOSDTwoColor, System.Windows.Media.Brush tPOSDThreeColor, System.Windows.Media.Brush nakedTPOSDThreeColor, System.Windows.Media.Brush singlePrintColor, System.Windows.Media.Brush poorHighLowColor, System.Windows.Media.Brush excessColor, System.Windows.Media.Brush tPOVABoxColor, bool showAnalyticsPanel, STVPPanelPosition analyticsPanelPosition, bool showDeltaZScore, int deltaZScoreLookbackSessions, double deltaZScoreThreshold, bool showVolumeSkewness, bool showVAMigration, int vAMigrationLookback, bool showTPOKurtosis, bool printLevelsAtSessionStart, bool printLevelsOnDemand, bool enableRegimeDetection, int dominanceWindowBars, double discoveryThreshold, double auctionThreshold, double imbalanceIntensityFloor, bool useLevel2Book, int l2DepthLevels, double l2DominanceWeight, double l2AbsorptionWeight, bool showRegimeBackground, bool showRegimePanel, STVPPanelPosition regimePanelPosition, System.Windows.Media.Brush discoveryBgColor, System.Windows.Media.Brush auctionBgColor, System.Windows.Media.Brush transitionBgColor, System.Windows.Media.Brush buyerDominanceColor, System.Windows.Media.Brush sellerDominanceColor, bool enableValueAreaRejection, bool vAPreferTpoLevels, bool vAIncludeNakedLevels, bool vAIncludeHighLow, bool vARequireCumDeltaConfirm, bool enableMeanRevertVAEdge, bool mRRequireTpoVaEdge, bool mRRequireImbalanceOrL2, bool mRRequireCumDeltaConfirm, bool mROneSignalPerEdge, bool enableTrendRunner, bool enableAutoTrade, string autoTradeAccountName, int autoTradeQuantity, bool autoTradeOnlyWhenFlat, int autoTradeStartMinutesAfterRth, bool enableWallMonitor, int wallMinSize, double wallMedianMultiple, int wallNearLevels, int wallScanRangeTicks, double wallPersistSeconds, int wallTestingTicks, double wallAbsorbRatio, double wallPullFraction, bool showWallsOnChart, int wallMarkerWidth, bool blockEntryOnOpposingSum, int entryMaxOpposingSize)
		{
			return indicator.SessionTPOVolumeProfile(Input, rTHStartTime, rTHEndTime, ticksPerRow, iBDurationMinutes, maxSessionsToShow, profilePeriod, showVPOC, showVolumeValueArea, showInitialBalance, showIBQuartiles, showIBBiasLabel, showNakedVPOC, showPriorSessionVPOC, showNakedVAHVAL, showPriorSessionLevels, showVolumeLabels, showPerBarBidAskTotals, showSessionStats, showSD2VA, showSD3VA, showVA1Fill, showSD2Fill, showSD3Fill, showNakedSD2, showNakedSD3, volumeFontSize, vALabelFontSize, vALabelXOffset, vABoxLineWidth, vADisplayStyle, showLVNExtension, showCurrentVolumeLevels, hvnLvnOnlyMode, volumeBarDisplayMode, showSessionBuysSells, showSlidingWindowBuysSells, tradeSlidingWindowSeconds, showSlidingWindowTotals, slidingWindowTotalsOffsetMultiple, tradeClassification, showDiagonalImbalance, imbalanceMode, diagonalImbalanceRatio, horizontalImbalanceRatio, minImbalanceVolume, zoneHorizontalControlFloor, showImbalanceZones, minLevelsForImbalanceZone, labelFontSize, extensionLineWidth, currentSessionVbPWidth, priorSessionVbPWidth, currentSessionRightOffset, levelLabelRightOffset, vPOCColor, hVNColor, lVNColor, volumeBarColor, volumeBarOutlineColor, volumeVABoxColor, valueAreaFillColor, iBColor, iBFirstSideColor, iBSecondHighColor, iBSecondLowColor, iBQuartileColor, nakedVPOCColor, nakedVAColor, nakedVAHColor, priorHighLowColor, textColor, sD2Color, sD3Color, lVNExtAboveColor, lVNExtBelowColor, deltaPositiveColor, deltaNegativeColor, bidAskAskColor, bidAskBidColor, buyingImbalanceColor, sellingImbalanceColor, buyZoneColor, sellZoneColor, sameLevelBuyDominanceColor, sameLevelSellDominanceColor, vVAHLineColor, vVAHLineWidth, vVAHLineStyle, vVALLineColor, vVALLineWidth, vVALLineStyle, sD2HLineColor, sD2HLineWidth, sD2HLineStyle, sD2LLineColor, sD2LLineWidth, sD2LLineStyle, sD3HLineColor, sD3HLineWidth, sD3HLineStyle, sD3LLineColor, sD3LLineWidth, sD3LLineStyle, tPOPeriodMinutes, splitGlobexRthTPO, showTPOLetters, showTPOPOC, showTPOVAHVAL, showTPOSD2, showTPOSD3, showNakedTPOPOC, showNakedTPOVAHVAL, showNakedTPOSD2, showNakedTPOSD3, showSinglePrints, showNakedSinglePrints, showPoorHighsLows, showExcess, showDayType, tPOFontSize, showCurrentTPOLevels, tPOLetterColor, tPOPOCColor, tPOVAHColor, tPOVALColor, nakedTPOPOCColor, nakedTPOVAColor, tPOSDTwoColor, nakedTPOSDTwoColor, tPOSDThreeColor, nakedTPOSDThreeColor, singlePrintColor, poorHighLowColor, excessColor, tPOVABoxColor, showAnalyticsPanel, analyticsPanelPosition, showDeltaZScore, deltaZScoreLookbackSessions, deltaZScoreThreshold, showVolumeSkewness, showVAMigration, vAMigrationLookback, showTPOKurtosis, printLevelsAtSessionStart, printLevelsOnDemand, enableRegimeDetection, dominanceWindowBars, discoveryThreshold, auctionThreshold, imbalanceIntensityFloor, useLevel2Book, l2DepthLevels, l2DominanceWeight, l2AbsorptionWeight, showRegimeBackground, showRegimePanel, regimePanelPosition, discoveryBgColor, auctionBgColor, transitionBgColor, buyerDominanceColor, sellerDominanceColor, enableValueAreaRejection, vAPreferTpoLevels, vAIncludeNakedLevels, vAIncludeHighLow, vARequireCumDeltaConfirm, enableMeanRevertVAEdge, mRRequireTpoVaEdge, mRRequireImbalanceOrL2, mRRequireCumDeltaConfirm, mROneSignalPerEdge, enableTrendRunner, enableAutoTrade, autoTradeAccountName, autoTradeQuantity, autoTradeOnlyWhenFlat, autoTradeStartMinutesAfterRth, enableWallMonitor, wallMinSize, wallMedianMultiple, wallNearLevels, wallScanRangeTicks, wallPersistSeconds, wallTestingTicks, wallAbsorbRatio, wallPullFraction, showWallsOnChart, wallMarkerWidth, blockEntryOnOpposingSum, entryMaxOpposingSize);
		}

		public Indicators.SessionTPOVolumeProfile SessionTPOVolumeProfile(ISeries<double> input , TimeSpan rTHStartTime, TimeSpan rTHEndTime, int ticksPerRow, int iBDurationMinutes, int maxSessionsToShow, STVPProfilePeriod profilePeriod, bool showVPOC, bool showVolumeValueArea, bool showInitialBalance, bool showIBQuartiles, bool showIBBiasLabel, bool showNakedVPOC, bool showPriorSessionVPOC, bool showNakedVAHVAL, bool showPriorSessionLevels, bool showVolumeLabels, bool showPerBarBidAskTotals, bool showSessionStats, bool showSD2VA, bool showSD3VA, bool showVA1Fill, bool showSD2Fill, bool showSD3Fill, bool showNakedSD2, bool showNakedSD3, int volumeFontSize, int vALabelFontSize, int vALabelXOffset, int vABoxLineWidth, STVPDisplayStyle vADisplayStyle, bool showLVNExtension, bool showCurrentVolumeLevels, bool hvnLvnOnlyMode, STVPBarDisplayMode volumeBarDisplayMode, bool showSessionBuysSells, bool showSlidingWindowBuysSells, int tradeSlidingWindowSeconds, bool showSlidingWindowTotals, int slidingWindowTotalsOffsetMultiple, STVPTradeClassification tradeClassification, bool showDiagonalImbalance, STVPImbalanceMode imbalanceMode, double diagonalImbalanceRatio, double horizontalImbalanceRatio, int minImbalanceVolume, int zoneHorizontalControlFloor, bool showImbalanceZones, int minLevelsForImbalanceZone, int labelFontSize, int extensionLineWidth, int currentSessionVbPWidth, int priorSessionVbPWidth, int currentSessionRightOffset, int levelLabelRightOffset, System.Windows.Media.Brush vPOCColor, System.Windows.Media.Brush hVNColor, System.Windows.Media.Brush lVNColor, System.Windows.Media.Brush volumeBarColor, System.Windows.Media.Brush volumeBarOutlineColor, System.Windows.Media.Brush volumeVABoxColor, System.Windows.Media.Brush valueAreaFillColor, System.Windows.Media.Brush iBColor, System.Windows.Media.Brush iBFirstSideColor, System.Windows.Media.Brush iBSecondHighColor, System.Windows.Media.Brush iBSecondLowColor, System.Windows.Media.Brush iBQuartileColor, System.Windows.Media.Brush nakedVPOCColor, System.Windows.Media.Brush nakedVAColor, System.Windows.Media.Brush nakedVAHColor, System.Windows.Media.Brush priorHighLowColor, System.Windows.Media.Brush textColor, System.Windows.Media.Brush sD2Color, System.Windows.Media.Brush sD3Color, System.Windows.Media.Brush lVNExtAboveColor, System.Windows.Media.Brush lVNExtBelowColor, System.Windows.Media.Brush deltaPositiveColor, System.Windows.Media.Brush deltaNegativeColor, System.Windows.Media.Brush bidAskAskColor, System.Windows.Media.Brush bidAskBidColor, System.Windows.Media.Brush buyingImbalanceColor, System.Windows.Media.Brush sellingImbalanceColor, System.Windows.Media.Brush buyZoneColor, System.Windows.Media.Brush sellZoneColor, System.Windows.Media.Brush sameLevelBuyDominanceColor, System.Windows.Media.Brush sameLevelSellDominanceColor, System.Windows.Media.Brush vVAHLineColor, int vVAHLineWidth, STVPLineStyle vVAHLineStyle, System.Windows.Media.Brush vVALLineColor, int vVALLineWidth, STVPLineStyle vVALLineStyle, System.Windows.Media.Brush sD2HLineColor, int sD2HLineWidth, STVPLineStyle sD2HLineStyle, System.Windows.Media.Brush sD2LLineColor, int sD2LLineWidth, STVPLineStyle sD2LLineStyle, System.Windows.Media.Brush sD3HLineColor, int sD3HLineWidth, STVPLineStyle sD3HLineStyle, System.Windows.Media.Brush sD3LLineColor, int sD3LLineWidth, STVPLineStyle sD3LLineStyle, int tPOPeriodMinutes, bool splitGlobexRthTPO, bool showTPOLetters, bool showTPOPOC, bool showTPOVAHVAL, bool showTPOSD2, bool showTPOSD3, bool showNakedTPOPOC, bool showNakedTPOVAHVAL, bool showNakedTPOSD2, bool showNakedTPOSD3, bool showSinglePrints, bool showNakedSinglePrints, bool showPoorHighsLows, bool showExcess, bool showDayType, int tPOFontSize, bool showCurrentTPOLevels, System.Windows.Media.Brush tPOLetterColor, System.Windows.Media.Brush tPOPOCColor, System.Windows.Media.Brush tPOVAHColor, System.Windows.Media.Brush tPOVALColor, System.Windows.Media.Brush nakedTPOPOCColor, System.Windows.Media.Brush nakedTPOVAColor, System.Windows.Media.Brush tPOSDTwoColor, System.Windows.Media.Brush nakedTPOSDTwoColor, System.Windows.Media.Brush tPOSDThreeColor, System.Windows.Media.Brush nakedTPOSDThreeColor, System.Windows.Media.Brush singlePrintColor, System.Windows.Media.Brush poorHighLowColor, System.Windows.Media.Brush excessColor, System.Windows.Media.Brush tPOVABoxColor, bool showAnalyticsPanel, STVPPanelPosition analyticsPanelPosition, bool showDeltaZScore, int deltaZScoreLookbackSessions, double deltaZScoreThreshold, bool showVolumeSkewness, bool showVAMigration, int vAMigrationLookback, bool showTPOKurtosis, bool printLevelsAtSessionStart, bool printLevelsOnDemand, bool enableRegimeDetection, int dominanceWindowBars, double discoveryThreshold, double auctionThreshold, double imbalanceIntensityFloor, bool useLevel2Book, int l2DepthLevels, double l2DominanceWeight, double l2AbsorptionWeight, bool showRegimeBackground, bool showRegimePanel, STVPPanelPosition regimePanelPosition, System.Windows.Media.Brush discoveryBgColor, System.Windows.Media.Brush auctionBgColor, System.Windows.Media.Brush transitionBgColor, System.Windows.Media.Brush buyerDominanceColor, System.Windows.Media.Brush sellerDominanceColor, bool enableValueAreaRejection, bool vAPreferTpoLevels, bool vAIncludeNakedLevels, bool vAIncludeHighLow, bool vARequireCumDeltaConfirm, bool enableMeanRevertVAEdge, bool mRRequireTpoVaEdge, bool mRRequireImbalanceOrL2, bool mRRequireCumDeltaConfirm, bool mROneSignalPerEdge, bool enableTrendRunner, bool enableAutoTrade, string autoTradeAccountName, int autoTradeQuantity, bool autoTradeOnlyWhenFlat, int autoTradeStartMinutesAfterRth, bool enableWallMonitor, int wallMinSize, double wallMedianMultiple, int wallNearLevels, int wallScanRangeTicks, double wallPersistSeconds, int wallTestingTicks, double wallAbsorbRatio, double wallPullFraction, bool showWallsOnChart, int wallMarkerWidth, bool blockEntryOnOpposingSum, int entryMaxOpposingSize)
		{
			return indicator.SessionTPOVolumeProfile(input, rTHStartTime, rTHEndTime, ticksPerRow, iBDurationMinutes, maxSessionsToShow, profilePeriod, showVPOC, showVolumeValueArea, showInitialBalance, showIBQuartiles, showIBBiasLabel, showNakedVPOC, showPriorSessionVPOC, showNakedVAHVAL, showPriorSessionLevels, showVolumeLabels, showPerBarBidAskTotals, showSessionStats, showSD2VA, showSD3VA, showVA1Fill, showSD2Fill, showSD3Fill, showNakedSD2, showNakedSD3, volumeFontSize, vALabelFontSize, vALabelXOffset, vABoxLineWidth, vADisplayStyle, showLVNExtension, showCurrentVolumeLevels, hvnLvnOnlyMode, volumeBarDisplayMode, showSessionBuysSells, showSlidingWindowBuysSells, tradeSlidingWindowSeconds, showSlidingWindowTotals, slidingWindowTotalsOffsetMultiple, tradeClassification, showDiagonalImbalance, imbalanceMode, diagonalImbalanceRatio, horizontalImbalanceRatio, minImbalanceVolume, zoneHorizontalControlFloor, showImbalanceZones, minLevelsForImbalanceZone, labelFontSize, extensionLineWidth, currentSessionVbPWidth, priorSessionVbPWidth, currentSessionRightOffset, levelLabelRightOffset, vPOCColor, hVNColor, lVNColor, volumeBarColor, volumeBarOutlineColor, volumeVABoxColor, valueAreaFillColor, iBColor, iBFirstSideColor, iBSecondHighColor, iBSecondLowColor, iBQuartileColor, nakedVPOCColor, nakedVAColor, nakedVAHColor, priorHighLowColor, textColor, sD2Color, sD3Color, lVNExtAboveColor, lVNExtBelowColor, deltaPositiveColor, deltaNegativeColor, bidAskAskColor, bidAskBidColor, buyingImbalanceColor, sellingImbalanceColor, buyZoneColor, sellZoneColor, sameLevelBuyDominanceColor, sameLevelSellDominanceColor, vVAHLineColor, vVAHLineWidth, vVAHLineStyle, vVALLineColor, vVALLineWidth, vVALLineStyle, sD2HLineColor, sD2HLineWidth, sD2HLineStyle, sD2LLineColor, sD2LLineWidth, sD2LLineStyle, sD3HLineColor, sD3HLineWidth, sD3HLineStyle, sD3LLineColor, sD3LLineWidth, sD3LLineStyle, tPOPeriodMinutes, splitGlobexRthTPO, showTPOLetters, showTPOPOC, showTPOVAHVAL, showTPOSD2, showTPOSD3, showNakedTPOPOC, showNakedTPOVAHVAL, showNakedTPOSD2, showNakedTPOSD3, showSinglePrints, showNakedSinglePrints, showPoorHighsLows, showExcess, showDayType, tPOFontSize, showCurrentTPOLevels, tPOLetterColor, tPOPOCColor, tPOVAHColor, tPOVALColor, nakedTPOPOCColor, nakedTPOVAColor, tPOSDTwoColor, nakedTPOSDTwoColor, tPOSDThreeColor, nakedTPOSDThreeColor, singlePrintColor, poorHighLowColor, excessColor, tPOVABoxColor, showAnalyticsPanel, analyticsPanelPosition, showDeltaZScore, deltaZScoreLookbackSessions, deltaZScoreThreshold, showVolumeSkewness, showVAMigration, vAMigrationLookback, showTPOKurtosis, printLevelsAtSessionStart, printLevelsOnDemand, enableRegimeDetection, dominanceWindowBars, discoveryThreshold, auctionThreshold, imbalanceIntensityFloor, useLevel2Book, l2DepthLevels, l2DominanceWeight, l2AbsorptionWeight, showRegimeBackground, showRegimePanel, regimePanelPosition, discoveryBgColor, auctionBgColor, transitionBgColor, buyerDominanceColor, sellerDominanceColor, enableValueAreaRejection, vAPreferTpoLevels, vAIncludeNakedLevels, vAIncludeHighLow, vARequireCumDeltaConfirm, enableMeanRevertVAEdge, mRRequireTpoVaEdge, mRRequireImbalanceOrL2, mRRequireCumDeltaConfirm, mROneSignalPerEdge, enableTrendRunner, enableAutoTrade, autoTradeAccountName, autoTradeQuantity, autoTradeOnlyWhenFlat, autoTradeStartMinutesAfterRth, enableWallMonitor, wallMinSize, wallMedianMultiple, wallNearLevels, wallScanRangeTicks, wallPersistSeconds, wallTestingTicks, wallAbsorbRatio, wallPullFraction, showWallsOnChart, wallMarkerWidth, blockEntryOnOpposingSum, entryMaxOpposingSize);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SessionTPOVolumeProfile SessionTPOVolumeProfile(TimeSpan rTHStartTime, TimeSpan rTHEndTime, int ticksPerRow, int iBDurationMinutes, int maxSessionsToShow, STVPProfilePeriod profilePeriod, bool showVPOC, bool showVolumeValueArea, bool showInitialBalance, bool showIBQuartiles, bool showIBBiasLabel, bool showNakedVPOC, bool showPriorSessionVPOC, bool showNakedVAHVAL, bool showPriorSessionLevels, bool showVolumeLabels, bool showPerBarBidAskTotals, bool showSessionStats, bool showSD2VA, bool showSD3VA, bool showVA1Fill, bool showSD2Fill, bool showSD3Fill, bool showNakedSD2, bool showNakedSD3, int volumeFontSize, int vALabelFontSize, int vALabelXOffset, int vABoxLineWidth, STVPDisplayStyle vADisplayStyle, bool showLVNExtension, bool showCurrentVolumeLevels, bool hvnLvnOnlyMode, STVPBarDisplayMode volumeBarDisplayMode, bool showSessionBuysSells, bool showSlidingWindowBuysSells, int tradeSlidingWindowSeconds, bool showSlidingWindowTotals, int slidingWindowTotalsOffsetMultiple, STVPTradeClassification tradeClassification, bool showDiagonalImbalance, STVPImbalanceMode imbalanceMode, double diagonalImbalanceRatio, double horizontalImbalanceRatio, int minImbalanceVolume, int zoneHorizontalControlFloor, bool showImbalanceZones, int minLevelsForImbalanceZone, int labelFontSize, int extensionLineWidth, int currentSessionVbPWidth, int priorSessionVbPWidth, int currentSessionRightOffset, int levelLabelRightOffset, System.Windows.Media.Brush vPOCColor, System.Windows.Media.Brush hVNColor, System.Windows.Media.Brush lVNColor, System.Windows.Media.Brush volumeBarColor, System.Windows.Media.Brush volumeBarOutlineColor, System.Windows.Media.Brush volumeVABoxColor, System.Windows.Media.Brush valueAreaFillColor, System.Windows.Media.Brush iBColor, System.Windows.Media.Brush iBFirstSideColor, System.Windows.Media.Brush iBSecondHighColor, System.Windows.Media.Brush iBSecondLowColor, System.Windows.Media.Brush iBQuartileColor, System.Windows.Media.Brush nakedVPOCColor, System.Windows.Media.Brush nakedVAColor, System.Windows.Media.Brush nakedVAHColor, System.Windows.Media.Brush priorHighLowColor, System.Windows.Media.Brush textColor, System.Windows.Media.Brush sD2Color, System.Windows.Media.Brush sD3Color, System.Windows.Media.Brush lVNExtAboveColor, System.Windows.Media.Brush lVNExtBelowColor, System.Windows.Media.Brush deltaPositiveColor, System.Windows.Media.Brush deltaNegativeColor, System.Windows.Media.Brush bidAskAskColor, System.Windows.Media.Brush bidAskBidColor, System.Windows.Media.Brush buyingImbalanceColor, System.Windows.Media.Brush sellingImbalanceColor, System.Windows.Media.Brush buyZoneColor, System.Windows.Media.Brush sellZoneColor, System.Windows.Media.Brush sameLevelBuyDominanceColor, System.Windows.Media.Brush sameLevelSellDominanceColor, System.Windows.Media.Brush vVAHLineColor, int vVAHLineWidth, STVPLineStyle vVAHLineStyle, System.Windows.Media.Brush vVALLineColor, int vVALLineWidth, STVPLineStyle vVALLineStyle, System.Windows.Media.Brush sD2HLineColor, int sD2HLineWidth, STVPLineStyle sD2HLineStyle, System.Windows.Media.Brush sD2LLineColor, int sD2LLineWidth, STVPLineStyle sD2LLineStyle, System.Windows.Media.Brush sD3HLineColor, int sD3HLineWidth, STVPLineStyle sD3HLineStyle, System.Windows.Media.Brush sD3LLineColor, int sD3LLineWidth, STVPLineStyle sD3LLineStyle, int tPOPeriodMinutes, bool splitGlobexRthTPO, bool showTPOLetters, bool showTPOPOC, bool showTPOVAHVAL, bool showTPOSD2, bool showTPOSD3, bool showNakedTPOPOC, bool showNakedTPOVAHVAL, bool showNakedTPOSD2, bool showNakedTPOSD3, bool showSinglePrints, bool showNakedSinglePrints, bool showPoorHighsLows, bool showExcess, bool showDayType, int tPOFontSize, bool showCurrentTPOLevels, System.Windows.Media.Brush tPOLetterColor, System.Windows.Media.Brush tPOPOCColor, System.Windows.Media.Brush tPOVAHColor, System.Windows.Media.Brush tPOVALColor, System.Windows.Media.Brush nakedTPOPOCColor, System.Windows.Media.Brush nakedTPOVAColor, System.Windows.Media.Brush tPOSDTwoColor, System.Windows.Media.Brush nakedTPOSDTwoColor, System.Windows.Media.Brush tPOSDThreeColor, System.Windows.Media.Brush nakedTPOSDThreeColor, System.Windows.Media.Brush singlePrintColor, System.Windows.Media.Brush poorHighLowColor, System.Windows.Media.Brush excessColor, System.Windows.Media.Brush tPOVABoxColor, bool showAnalyticsPanel, STVPPanelPosition analyticsPanelPosition, bool showDeltaZScore, int deltaZScoreLookbackSessions, double deltaZScoreThreshold, bool showVolumeSkewness, bool showVAMigration, int vAMigrationLookback, bool showTPOKurtosis, bool printLevelsAtSessionStart, bool printLevelsOnDemand, bool enableRegimeDetection, int dominanceWindowBars, double discoveryThreshold, double auctionThreshold, double imbalanceIntensityFloor, bool useLevel2Book, int l2DepthLevels, double l2DominanceWeight, double l2AbsorptionWeight, bool showRegimeBackground, bool showRegimePanel, STVPPanelPosition regimePanelPosition, System.Windows.Media.Brush discoveryBgColor, System.Windows.Media.Brush auctionBgColor, System.Windows.Media.Brush transitionBgColor, System.Windows.Media.Brush buyerDominanceColor, System.Windows.Media.Brush sellerDominanceColor, bool enableValueAreaRejection, bool vAPreferTpoLevels, bool vAIncludeNakedLevels, bool vAIncludeHighLow, bool vARequireCumDeltaConfirm, bool enableMeanRevertVAEdge, bool mRRequireTpoVaEdge, bool mRRequireImbalanceOrL2, bool mRRequireCumDeltaConfirm, bool mROneSignalPerEdge, bool enableTrendRunner, bool enableAutoTrade, string autoTradeAccountName, int autoTradeQuantity, bool autoTradeOnlyWhenFlat, int autoTradeStartMinutesAfterRth, bool enableWallMonitor, int wallMinSize, double wallMedianMultiple, int wallNearLevels, int wallScanRangeTicks, double wallPersistSeconds, int wallTestingTicks, double wallAbsorbRatio, double wallPullFraction, bool showWallsOnChart, int wallMarkerWidth, bool blockEntryOnOpposingSum, int entryMaxOpposingSize)
		{
			return indicator.SessionTPOVolumeProfile(Input, rTHStartTime, rTHEndTime, ticksPerRow, iBDurationMinutes, maxSessionsToShow, profilePeriod, showVPOC, showVolumeValueArea, showInitialBalance, showIBQuartiles, showIBBiasLabel, showNakedVPOC, showPriorSessionVPOC, showNakedVAHVAL, showPriorSessionLevels, showVolumeLabels, showPerBarBidAskTotals, showSessionStats, showSD2VA, showSD3VA, showVA1Fill, showSD2Fill, showSD3Fill, showNakedSD2, showNakedSD3, volumeFontSize, vALabelFontSize, vALabelXOffset, vABoxLineWidth, vADisplayStyle, showLVNExtension, showCurrentVolumeLevels, hvnLvnOnlyMode, volumeBarDisplayMode, showSessionBuysSells, showSlidingWindowBuysSells, tradeSlidingWindowSeconds, showSlidingWindowTotals, slidingWindowTotalsOffsetMultiple, tradeClassification, showDiagonalImbalance, imbalanceMode, diagonalImbalanceRatio, horizontalImbalanceRatio, minImbalanceVolume, zoneHorizontalControlFloor, showImbalanceZones, minLevelsForImbalanceZone, labelFontSize, extensionLineWidth, currentSessionVbPWidth, priorSessionVbPWidth, currentSessionRightOffset, levelLabelRightOffset, vPOCColor, hVNColor, lVNColor, volumeBarColor, volumeBarOutlineColor, volumeVABoxColor, valueAreaFillColor, iBColor, iBFirstSideColor, iBSecondHighColor, iBSecondLowColor, iBQuartileColor, nakedVPOCColor, nakedVAColor, nakedVAHColor, priorHighLowColor, textColor, sD2Color, sD3Color, lVNExtAboveColor, lVNExtBelowColor, deltaPositiveColor, deltaNegativeColor, bidAskAskColor, bidAskBidColor, buyingImbalanceColor, sellingImbalanceColor, buyZoneColor, sellZoneColor, sameLevelBuyDominanceColor, sameLevelSellDominanceColor, vVAHLineColor, vVAHLineWidth, vVAHLineStyle, vVALLineColor, vVALLineWidth, vVALLineStyle, sD2HLineColor, sD2HLineWidth, sD2HLineStyle, sD2LLineColor, sD2LLineWidth, sD2LLineStyle, sD3HLineColor, sD3HLineWidth, sD3HLineStyle, sD3LLineColor, sD3LLineWidth, sD3LLineStyle, tPOPeriodMinutes, splitGlobexRthTPO, showTPOLetters, showTPOPOC, showTPOVAHVAL, showTPOSD2, showTPOSD3, showNakedTPOPOC, showNakedTPOVAHVAL, showNakedTPOSD2, showNakedTPOSD3, showSinglePrints, showNakedSinglePrints, showPoorHighsLows, showExcess, showDayType, tPOFontSize, showCurrentTPOLevels, tPOLetterColor, tPOPOCColor, tPOVAHColor, tPOVALColor, nakedTPOPOCColor, nakedTPOVAColor, tPOSDTwoColor, nakedTPOSDTwoColor, tPOSDThreeColor, nakedTPOSDThreeColor, singlePrintColor, poorHighLowColor, excessColor, tPOVABoxColor, showAnalyticsPanel, analyticsPanelPosition, showDeltaZScore, deltaZScoreLookbackSessions, deltaZScoreThreshold, showVolumeSkewness, showVAMigration, vAMigrationLookback, showTPOKurtosis, printLevelsAtSessionStart, printLevelsOnDemand, enableRegimeDetection, dominanceWindowBars, discoveryThreshold, auctionThreshold, imbalanceIntensityFloor, useLevel2Book, l2DepthLevels, l2DominanceWeight, l2AbsorptionWeight, showRegimeBackground, showRegimePanel, regimePanelPosition, discoveryBgColor, auctionBgColor, transitionBgColor, buyerDominanceColor, sellerDominanceColor, enableValueAreaRejection, vAPreferTpoLevels, vAIncludeNakedLevels, vAIncludeHighLow, vARequireCumDeltaConfirm, enableMeanRevertVAEdge, mRRequireTpoVaEdge, mRRequireImbalanceOrL2, mRRequireCumDeltaConfirm, mROneSignalPerEdge, enableTrendRunner, enableAutoTrade, autoTradeAccountName, autoTradeQuantity, autoTradeOnlyWhenFlat, autoTradeStartMinutesAfterRth, enableWallMonitor, wallMinSize, wallMedianMultiple, wallNearLevels, wallScanRangeTicks, wallPersistSeconds, wallTestingTicks, wallAbsorbRatio, wallPullFraction, showWallsOnChart, wallMarkerWidth, blockEntryOnOpposingSum, entryMaxOpposingSize);
		}

		public Indicators.SessionTPOVolumeProfile SessionTPOVolumeProfile(ISeries<double> input , TimeSpan rTHStartTime, TimeSpan rTHEndTime, int ticksPerRow, int iBDurationMinutes, int maxSessionsToShow, STVPProfilePeriod profilePeriod, bool showVPOC, bool showVolumeValueArea, bool showInitialBalance, bool showIBQuartiles, bool showIBBiasLabel, bool showNakedVPOC, bool showPriorSessionVPOC, bool showNakedVAHVAL, bool showPriorSessionLevels, bool showVolumeLabels, bool showPerBarBidAskTotals, bool showSessionStats, bool showSD2VA, bool showSD3VA, bool showVA1Fill, bool showSD2Fill, bool showSD3Fill, bool showNakedSD2, bool showNakedSD3, int volumeFontSize, int vALabelFontSize, int vALabelXOffset, int vABoxLineWidth, STVPDisplayStyle vADisplayStyle, bool showLVNExtension, bool showCurrentVolumeLevels, bool hvnLvnOnlyMode, STVPBarDisplayMode volumeBarDisplayMode, bool showSessionBuysSells, bool showSlidingWindowBuysSells, int tradeSlidingWindowSeconds, bool showSlidingWindowTotals, int slidingWindowTotalsOffsetMultiple, STVPTradeClassification tradeClassification, bool showDiagonalImbalance, STVPImbalanceMode imbalanceMode, double diagonalImbalanceRatio, double horizontalImbalanceRatio, int minImbalanceVolume, int zoneHorizontalControlFloor, bool showImbalanceZones, int minLevelsForImbalanceZone, int labelFontSize, int extensionLineWidth, int currentSessionVbPWidth, int priorSessionVbPWidth, int currentSessionRightOffset, int levelLabelRightOffset, System.Windows.Media.Brush vPOCColor, System.Windows.Media.Brush hVNColor, System.Windows.Media.Brush lVNColor, System.Windows.Media.Brush volumeBarColor, System.Windows.Media.Brush volumeBarOutlineColor, System.Windows.Media.Brush volumeVABoxColor, System.Windows.Media.Brush valueAreaFillColor, System.Windows.Media.Brush iBColor, System.Windows.Media.Brush iBFirstSideColor, System.Windows.Media.Brush iBSecondHighColor, System.Windows.Media.Brush iBSecondLowColor, System.Windows.Media.Brush iBQuartileColor, System.Windows.Media.Brush nakedVPOCColor, System.Windows.Media.Brush nakedVAColor, System.Windows.Media.Brush nakedVAHColor, System.Windows.Media.Brush priorHighLowColor, System.Windows.Media.Brush textColor, System.Windows.Media.Brush sD2Color, System.Windows.Media.Brush sD3Color, System.Windows.Media.Brush lVNExtAboveColor, System.Windows.Media.Brush lVNExtBelowColor, System.Windows.Media.Brush deltaPositiveColor, System.Windows.Media.Brush deltaNegativeColor, System.Windows.Media.Brush bidAskAskColor, System.Windows.Media.Brush bidAskBidColor, System.Windows.Media.Brush buyingImbalanceColor, System.Windows.Media.Brush sellingImbalanceColor, System.Windows.Media.Brush buyZoneColor, System.Windows.Media.Brush sellZoneColor, System.Windows.Media.Brush sameLevelBuyDominanceColor, System.Windows.Media.Brush sameLevelSellDominanceColor, System.Windows.Media.Brush vVAHLineColor, int vVAHLineWidth, STVPLineStyle vVAHLineStyle, System.Windows.Media.Brush vVALLineColor, int vVALLineWidth, STVPLineStyle vVALLineStyle, System.Windows.Media.Brush sD2HLineColor, int sD2HLineWidth, STVPLineStyle sD2HLineStyle, System.Windows.Media.Brush sD2LLineColor, int sD2LLineWidth, STVPLineStyle sD2LLineStyle, System.Windows.Media.Brush sD3HLineColor, int sD3HLineWidth, STVPLineStyle sD3HLineStyle, System.Windows.Media.Brush sD3LLineColor, int sD3LLineWidth, STVPLineStyle sD3LLineStyle, int tPOPeriodMinutes, bool splitGlobexRthTPO, bool showTPOLetters, bool showTPOPOC, bool showTPOVAHVAL, bool showTPOSD2, bool showTPOSD3, bool showNakedTPOPOC, bool showNakedTPOVAHVAL, bool showNakedTPOSD2, bool showNakedTPOSD3, bool showSinglePrints, bool showNakedSinglePrints, bool showPoorHighsLows, bool showExcess, bool showDayType, int tPOFontSize, bool showCurrentTPOLevels, System.Windows.Media.Brush tPOLetterColor, System.Windows.Media.Brush tPOPOCColor, System.Windows.Media.Brush tPOVAHColor, System.Windows.Media.Brush tPOVALColor, System.Windows.Media.Brush nakedTPOPOCColor, System.Windows.Media.Brush nakedTPOVAColor, System.Windows.Media.Brush tPOSDTwoColor, System.Windows.Media.Brush nakedTPOSDTwoColor, System.Windows.Media.Brush tPOSDThreeColor, System.Windows.Media.Brush nakedTPOSDThreeColor, System.Windows.Media.Brush singlePrintColor, System.Windows.Media.Brush poorHighLowColor, System.Windows.Media.Brush excessColor, System.Windows.Media.Brush tPOVABoxColor, bool showAnalyticsPanel, STVPPanelPosition analyticsPanelPosition, bool showDeltaZScore, int deltaZScoreLookbackSessions, double deltaZScoreThreshold, bool showVolumeSkewness, bool showVAMigration, int vAMigrationLookback, bool showTPOKurtosis, bool printLevelsAtSessionStart, bool printLevelsOnDemand, bool enableRegimeDetection, int dominanceWindowBars, double discoveryThreshold, double auctionThreshold, double imbalanceIntensityFloor, bool useLevel2Book, int l2DepthLevels, double l2DominanceWeight, double l2AbsorptionWeight, bool showRegimeBackground, bool showRegimePanel, STVPPanelPosition regimePanelPosition, System.Windows.Media.Brush discoveryBgColor, System.Windows.Media.Brush auctionBgColor, System.Windows.Media.Brush transitionBgColor, System.Windows.Media.Brush buyerDominanceColor, System.Windows.Media.Brush sellerDominanceColor, bool enableValueAreaRejection, bool vAPreferTpoLevels, bool vAIncludeNakedLevels, bool vAIncludeHighLow, bool vARequireCumDeltaConfirm, bool enableMeanRevertVAEdge, bool mRRequireTpoVaEdge, bool mRRequireImbalanceOrL2, bool mRRequireCumDeltaConfirm, bool mROneSignalPerEdge, bool enableTrendRunner, bool enableAutoTrade, string autoTradeAccountName, int autoTradeQuantity, bool autoTradeOnlyWhenFlat, int autoTradeStartMinutesAfterRth, bool enableWallMonitor, int wallMinSize, double wallMedianMultiple, int wallNearLevels, int wallScanRangeTicks, double wallPersistSeconds, int wallTestingTicks, double wallAbsorbRatio, double wallPullFraction, bool showWallsOnChart, int wallMarkerWidth, bool blockEntryOnOpposingSum, int entryMaxOpposingSize)
		{
			return indicator.SessionTPOVolumeProfile(input, rTHStartTime, rTHEndTime, ticksPerRow, iBDurationMinutes, maxSessionsToShow, profilePeriod, showVPOC, showVolumeValueArea, showInitialBalance, showIBQuartiles, showIBBiasLabel, showNakedVPOC, showPriorSessionVPOC, showNakedVAHVAL, showPriorSessionLevels, showVolumeLabels, showPerBarBidAskTotals, showSessionStats, showSD2VA, showSD3VA, showVA1Fill, showSD2Fill, showSD3Fill, showNakedSD2, showNakedSD3, volumeFontSize, vALabelFontSize, vALabelXOffset, vABoxLineWidth, vADisplayStyle, showLVNExtension, showCurrentVolumeLevels, hvnLvnOnlyMode, volumeBarDisplayMode, showSessionBuysSells, showSlidingWindowBuysSells, tradeSlidingWindowSeconds, showSlidingWindowTotals, slidingWindowTotalsOffsetMultiple, tradeClassification, showDiagonalImbalance, imbalanceMode, diagonalImbalanceRatio, horizontalImbalanceRatio, minImbalanceVolume, zoneHorizontalControlFloor, showImbalanceZones, minLevelsForImbalanceZone, labelFontSize, extensionLineWidth, currentSessionVbPWidth, priorSessionVbPWidth, currentSessionRightOffset, levelLabelRightOffset, vPOCColor, hVNColor, lVNColor, volumeBarColor, volumeBarOutlineColor, volumeVABoxColor, valueAreaFillColor, iBColor, iBFirstSideColor, iBSecondHighColor, iBSecondLowColor, iBQuartileColor, nakedVPOCColor, nakedVAColor, nakedVAHColor, priorHighLowColor, textColor, sD2Color, sD3Color, lVNExtAboveColor, lVNExtBelowColor, deltaPositiveColor, deltaNegativeColor, bidAskAskColor, bidAskBidColor, buyingImbalanceColor, sellingImbalanceColor, buyZoneColor, sellZoneColor, sameLevelBuyDominanceColor, sameLevelSellDominanceColor, vVAHLineColor, vVAHLineWidth, vVAHLineStyle, vVALLineColor, vVALLineWidth, vVALLineStyle, sD2HLineColor, sD2HLineWidth, sD2HLineStyle, sD2LLineColor, sD2LLineWidth, sD2LLineStyle, sD3HLineColor, sD3HLineWidth, sD3HLineStyle, sD3LLineColor, sD3LLineWidth, sD3LLineStyle, tPOPeriodMinutes, splitGlobexRthTPO, showTPOLetters, showTPOPOC, showTPOVAHVAL, showTPOSD2, showTPOSD3, showNakedTPOPOC, showNakedTPOVAHVAL, showNakedTPOSD2, showNakedTPOSD3, showSinglePrints, showNakedSinglePrints, showPoorHighsLows, showExcess, showDayType, tPOFontSize, showCurrentTPOLevels, tPOLetterColor, tPOPOCColor, tPOVAHColor, tPOVALColor, nakedTPOPOCColor, nakedTPOVAColor, tPOSDTwoColor, nakedTPOSDTwoColor, tPOSDThreeColor, nakedTPOSDThreeColor, singlePrintColor, poorHighLowColor, excessColor, tPOVABoxColor, showAnalyticsPanel, analyticsPanelPosition, showDeltaZScore, deltaZScoreLookbackSessions, deltaZScoreThreshold, showVolumeSkewness, showVAMigration, vAMigrationLookback, showTPOKurtosis, printLevelsAtSessionStart, printLevelsOnDemand, enableRegimeDetection, dominanceWindowBars, discoveryThreshold, auctionThreshold, imbalanceIntensityFloor, useLevel2Book, l2DepthLevels, l2DominanceWeight, l2AbsorptionWeight, showRegimeBackground, showRegimePanel, regimePanelPosition, discoveryBgColor, auctionBgColor, transitionBgColor, buyerDominanceColor, sellerDominanceColor, enableValueAreaRejection, vAPreferTpoLevels, vAIncludeNakedLevels, vAIncludeHighLow, vARequireCumDeltaConfirm, enableMeanRevertVAEdge, mRRequireTpoVaEdge, mRRequireImbalanceOrL2, mRRequireCumDeltaConfirm, mROneSignalPerEdge, enableTrendRunner, enableAutoTrade, autoTradeAccountName, autoTradeQuantity, autoTradeOnlyWhenFlat, autoTradeStartMinutesAfterRth, enableWallMonitor, wallMinSize, wallMedianMultiple, wallNearLevels, wallScanRangeTicks, wallPersistSeconds, wallTestingTicks, wallAbsorbRatio, wallPullFraction, showWallsOnChart, wallMarkerWidth, blockEntryOnOpposingSum, entryMaxOpposingSize);
		}
	}
}

#endregion
