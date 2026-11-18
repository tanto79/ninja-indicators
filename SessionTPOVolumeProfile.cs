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




    /// <summary>Controls whether the volume/TPO profile resets each trading session or each week.</summary>
    public enum STVPProfilePeriod
    {
        /// <summary>One profile per trading session (default).</summary>
        Session,
        /// <summary>One cumulative profile for the full trading week.</summary>
        Weekly
    }

    /// <summary>How "RTH" is defined for RTH tracking.</summary>
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
    ///   - Naked VPOC / VVAH / VVAL extension lines into future sessions
    ///   - Prior session High / Low extension lines
    ///   - Session stats mini-panel (date, range, VPOC, VA)
    ///   - Configurable VbP width for current vs prior sessions
    ///   - Right-edge offset so it doesn't overlap other indicators
    ///
    /// Self-contained.
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

            // Per-price tick delta — buy vol and sell vol tracked separately at each level
            public Dictionary<int, long> BuyVolumeAtPrice;
            public Dictionary<int, long> SellVolumeAtPrice;

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
                SessionHigh       = double.MinValue;
                SessionLow        = double.MaxValue;
                RTHHigh           = double.MinValue;
                RTHLow            = double.MaxValue;
                StartBarIndex     = -1;
                EndBarIndex       = -1;
                RTHStartBarIndex  = -1;
                RTHEndBarIndex    = -1;
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
        private SharpDX.Direct2D1.Brush roleSupportBrush;        // level role: acting-as-support tag
        private SharpDX.Direct2D1.Brush roleResistanceBrush;     // level role: acting-as-resistance tag
        private SharpDX.Direct2D1.Brush roleBreakingBrush;       // level role: testing/breaking tag
        private SharpDX.Direct2D1.Brush roleUntestedBrush;       // level role: untested tag
        private readonly Dictionary<string, LevelRoleState> levelRoleStates = new Dictionary<string, LevelRoleState>();
        private DateTime roleLastTickTime       = DateTime.MinValue;
        private DateTime roleLastTickSessionKey = DateTime.MinValue;
        private readonly List<RoleTracker> roleTrackers = new List<RoleTracker>();
        private int            roleTrackerSessionCount = -1;
        private SessionProfile roleTrackerMostRecent;
        private DateTime       roleTrackerCurrentStart = DateTime.MinValue;
        private bool           roleTrackerPriorHiLo;
        private bool           roleTrackerRolesHiLo;
        private bool           roleTrackerPriorVPOC;
        private bool           roleTrackerNakedVPOC;
        private bool           roleTrackerNakedVA;
        private bool           roleTrackerNakedTPOPoc;
        private bool           roleTrackerNakedTPOVA;
        private bool           roleTrackerPriorTPOPoc;
        private bool           roleTrackerPriorTPOVA;
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
        // Cluster zone brushes — fill (semi-transparent) and solid border per tier
        private SharpDX.Direct2D1.Brush clusterTier1Brush;
        private SharpDX.Direct2D1.Brush clusterTier2Brush;
        private SharpDX.Direct2D1.Brush clusterTier3Brush;
        private SharpDX.Direct2D1.Brush clusterTier1BorderBrush;
        private SharpDX.Direct2D1.Brush clusterTier2BorderBrush;
        private SharpDX.Direct2D1.Brush clusterTier3BorderBrush;

        // Analytics panel brush and format
        private SharpDX.Direct2D1.Brush   analyticsPanelBgBrush;
        private SharpDX.DirectWrite.TextFormat clusterLabelFormat;

        // ================================================================
        // Delta tracking fields
        // ================================================================
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
        // TPO private fields
        // ================================================================
        private TPOPeriod   currentTPOPeriod; // the period currently being filled with letters

        // TPO brushes
        private SharpDX.Direct2D1.Brush tpoLetterBrush;    // TPO letter color
        private SharpDX.Direct2D1.Brush tpoPocBrush;       // TPO POC line
        private SharpDX.Direct2D1.Brush tpoVAHBrush;       // TPO VAH line
        private SharpDX.Direct2D1.Brush tpoVALBrush;       // TPO VAL line
        private SharpDX.Direct2D1.Brush nakedTPOPocBrush;  // naked TPO POC extension
        private SharpDX.Direct2D1.Brush priorTPOPocBrush;  // prior session TPO POC extension
        private SharpDX.Direct2D1.Brush nakedTPOVABrush;   // naked TPO VAH/VAL extension
        private SharpDX.Direct2D1.Brush priorTPOVABrush;   // prior session TPO VAH/VAL extension
        private SharpDX.Direct2D1.Brush tpoSD2Brush;       // TPO SD2H/SD2L lines
        private SharpDX.Direct2D1.Brush nakedTPOSD2Brush;  // naked TPO SD2H/SD2L extension
        private SharpDX.Direct2D1.Brush tpoSD3Brush;       // TPO SD3H/SD3L lines
        private SharpDX.Direct2D1.Brush nakedTPOSD3Brush;  // naked TPO SD3H/SD3L extension
        private SharpDX.Direct2D1.Brush singlePrintBrush;  // single print zones
        private SharpDX.Direct2D1.Brush poorHighLowBrush;  // poor highs/lows
        private SharpDX.Direct2D1.Brush excessBrush;       // excess highs/lows
        private SharpDX.Direct2D1.Brush tpoVABoxBrush;     // TPO value area box outline
        private SharpDX.DirectWrite.TextFormat letterFormat;  // Consolas font for TPO letters

        // Trading-hours-template session bounds for the latest tick (set in ProcessTick from GetSessionKey).
        private DateTime        templateDayBegin = DateTime.MinValue;
        private DateTime        templateDayEnd   = DateTime.MinValue;
        private DateTime        lastKeyBegin     = DateTime.MinValue;
        private DateTime        lastKeyEnd       = DateTime.MinValue;
        private Dictionary<DateTime, DateTime> tradingDayFirstBegin = new Dictionary<DateTime, DateTime>();


        #endregion

        // =========================================================
        //  PROPERTIES
        // =========================================================

        #region Properties — RTH Settings

        [NinjaScriptProperty]
        [Display(Name = "RTH Start Time",
                 Description = "Start of Regular Trading Hours (e.g. 8:30 AM). Ignored when RTH Mode = Full Session. Used for RTH-specific bar tracking. Session boundaries are always read from the chart's trading hours template.",
                 Order = 1, GroupName = "1. RTH Settings")]
        public TimeSpan RTHStartTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "RTH End Time",
                 Description = "End of Regular Trading Hours (e.g. 4:00 PM). Used for RTH bar index tracking.",
                 Order = 2, GroupName = "1. RTH Settings")]
        public TimeSpan RTHEndTime { get; set; }

        [Display(Name = "RTH Mode",
                 Description = "Auto (default) = detects it per contract from the chart's trading hours template: if the session opens inside the RTH window below (no overnight part, e.g. HE / LE) the whole session is RTH, otherwise the RTH Start / End times are used (index futures with a Globex overnight session). Manual = always use RTH Start / End. Full Session = always treat the whole template session as RTH. When the whole session is RTH, nothing is classed as Globex.",
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
        [Display(Name = "Show Session Stats", Description = "Display a mini stats panel at the session low (date, range, VPOC, VA)", Order = 8, GroupName = "3. Display Options")]
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

        #region Properties — Level Roles

        [NinjaScriptProperty]
        [Display(Name = "Show Level Roles",
                 Description = "Live support/resistance classification beside each prior-session and naked High/Low/VPOC/VVAH/VVAL: untested, testing, held, breaking (acceptance timer) or flipped. Price side gives the role; role reversals require traded-time acceptance, not a single decisive tick. Tick-driven and identical on any chart bar size.",
                 Order = 1, GroupName = "3B. Level Roles")]
        public bool ShowLevelRoles { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Classify Highs & Lows", 
                 Description = "Run the live support/resistance classifier on prior-session and naked High/Low lines. The lines themselves follow Show Prior Session Levels; this only controls their role classification and tags.",
                 Order = 7, GroupName = "3B. Level Roles")]
        public bool LevelRolesHiLo { get; set; }

        [NinjaScriptProperty]
        [Range(5, 3600)]
        [Display(Name = "Role Accept Seconds", Description = "Seconds price must hold beyond the level consecutively before a break is accepted and the role flips (default 180)", Order = 2, GroupName = "3B. Level Roles")]
        public int LevelRoleAcceptSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Role Touch Tolerance Ticks", Description = "Ticks around each level that count as touching it — used identically by the role classifier and naked-level termination. 0 = only an actual print at the line's price. (default 8)", Order = 3, GroupName = "3B. Level Roles")]
        public int LevelRoleTouchToleranceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2000000000)]
        [Display(Name = "Role Quality Min Volume", Description = "Minimum volume at a level interaction to grade the role tag's quality: absorbed (dominant aggression into the level rejected), confirmed (dominant aggression behind an accepted break), thin (below this minimum). 0 disables quality annotation.", Order = 8, GroupName = "3B. Level Roles")]
        public int RoleInteractionMinVolume { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Role Support Color", Description = "Tag color when a level is acting as support", Order = 4, GroupName = "3B. Level Roles")]
        public System.Windows.Media.Brush LevelRoleSupportColor { get; set; }
        [Browsable(false)]
        public string LevelRoleSupportColorSerialize
        { get { return Serialize.BrushToString(LevelRoleSupportColor); } set { LevelRoleSupportColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Role Resistance Color", Description = "Tag color when a level is acting as resistance", Order = 5, GroupName = "3B. Level Roles")]
        public System.Windows.Media.Brush LevelRoleResistanceColor { get; set; }
        [Browsable(false)]
        public string LevelRoleResistanceColorSerialize
        { get { return Serialize.BrushToString(LevelRoleResistanceColor); } set { LevelRoleResistanceColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Role Breaking/Testing Color", Description = "Tag color while a level is being tested or broken (acceptance timer running)", Order = 6, GroupName = "3B. Level Roles")]
        public System.Windows.Media.Brush LevelRoleBreakingColor { get; set; }
        [Browsable(false)]
        public string LevelRoleBreakingColorSerialize
        { get { return Serialize.BrushToString(LevelRoleBreakingColor); } set { LevelRoleBreakingColor = Serialize.StringToBrush(value); } }

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
        [Display(Name = "Show Prior Session TPO POC", Description = "Extend the previous session\u2019s TPO POC as a dashed line. Independent of Show Naked TPO POC, which controls the older untested TPO POCs.", Order = 10, GroupName = "7. TPO Settings")]
        public bool ShowPriorSessionTPOPOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Prior Session TPO VAH/VAL", Description = "Extend the previous session\u2019s TPO VAH/VAL as dashed lines. Independent of Show Naked TPO VAH/VAL, which controls the older untested TPO VAH/VALs.", Order = 11, GroupName = "7. TPO Settings")]
        public bool ShowPriorSessionTPOVAHVAL { get; set; }

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

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Prior Session TPO VAH/VAL Color", Order = 13, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush PriorSessionTPOVAColor { get; set; }
        [Browsable(false)]
        public string PriorSessionTPOVAColorSerialize
        { get { return Serialize.BrushToString(PriorSessionTPOVAColor); } set { PriorSessionTPOVAColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Prior Session TPO POC Color", Order = 14, GroupName = "8. TPO Colors")]
        public System.Windows.Media.Brush PriorSessionTPOPOCColor { get; set; }
        [Browsable(false)]
        public string PriorSessionTPOPOCColorSerialize
        { get { return Serialize.BrushToString(PriorSessionTPOPOCColor); } set { PriorSessionTPOPOCColor = Serialize.StringToBrush(value); } }

        #endregion


        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Tick-accurate session-based Volume by Price with TPO Market Profile.";
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
                MaxSessionsToShow     = 20;

                // Display defaults
                ShowVPOC               = true;
                ShowVolumeValueArea    = true;
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
                ShowLevelRoles               = true;
                LevelRolesHiLo               = true;
                LevelRoleAcceptSeconds       = 180;  // price must hold ~3 minutes beyond a level before its role flips
                LevelRoleTouchToleranceTicks = 8;
                RoleInteractionMinVolume     = 100;
                LevelRoleSupportColor        = System.Windows.Media.Brushes.LimeGreen;
                LevelRoleResistanceColor     = System.Windows.Media.Brushes.Red;
                LevelRoleBreakingColor       = System.Windows.Media.Brushes.Gold;
                HvnLvnOnlyMode         = false;  // off by default — shows all node types (HVN + MVN + LVN)
                VolumeBarDisplayMode   = STVPBarDisplayMode.Volume;  // default to standard volume bars
                ShowSessionBuysSells   = true;
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
                ShowPriorSessionTPOPOC   = true;
                ShowPriorSessionTPOVAHVAL = true;
                ShowNakedTPOSD2      = true;
                ShowNakedTPOSD3      = true;
                ShowSinglePrints     = true;
                ShowNakedSinglePrints = true;
                ShowPoorHighsLows    = true;
                ShowExcess           = true;
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

                var priorTPOVA = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(255, 30, 144, 255));
                priorTPOVA.Freeze();
                PriorSessionTPOVAColor = priorTPOVA;

                var priorTPOPoc = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(255, 255, 0, 255));
                priorTPOPoc.Freeze();
                PriorSessionTPOPOCColor = priorTPOPoc;

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
                currentTPOPeriod         = null;
                primaryBarBidVol         = new Dictionary<int, long>();
                primaryBarAskVol         = new Dictionary<int, long>();
                sessionCumBidAtBar       = new Dictionary<int, long>();
                sessionCumAskAtBar       = new Dictionary<int, long>();
                sessionRunningBid        = 0;
                sessionRunningAsk        = 0;

                // SessionIterator reads session boundaries directly from the chart's
                // trading hours template — no hardcoded ETH start time needed.
                sessionIterator = new SessionIterator(Bars);
                tradingDayFirstBegin.Clear();
                templateDayBegin = templateDayEnd = lastKeyBegin = lastKeyEnd = DateTime.MinValue;

                Print("SessionTPOVolumeProfile loaded — period: " + ProfilePeriod + "  row size: " + rowSize.ToString("F4")
                    + "  (RTH " + RTHStartTime.ToString(@"hh\:mm")
                    + "–" + RTHEndTime.ToString(@"hh\:mm") + ")"
                    + "  RthMode: " + RthMode + (MergeTemplateSessions ? " (merge template sessions)" : "")
                    + "  template: " + Bars.TradingHours.Name);
            }
            else if (State == State.Terminated)
            {
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





        // ------ Tick processing ------

        private void ProcessTick()
        {
            if (CurrentBars[1] < 1) return;

            DateTime tickTime   = Times[1][0];
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
            lastTickPrice     = tickPrice;
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

            }
            if (downVol > 0)
            {
                if (!currentSession.SellVolumeAtPrice.ContainsKey(priceLevel))
                    currentSession.SellVolumeAtPrice[priceLevel] = 0;
                currentSession.SellVolumeAtPrice[priceLevel] += downVol;

            }

            // --- Per primary-bar bid/ask totals (reset automatically on new bar index) ---
            // --- and session-cumulative snapshot at this primary bar ---
            int primaryBarIdx = GetPrimaryBarIndexForTick(tickTime);
            if (primaryBarIdx >= 0)
            {
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

            // --- Naked level testing against prior sessions ---
            if (ShowNakedVPOC || ShowPriorSessionVPOC || ShowNakedVAHVAL || ShowPriorSessionLevels
                || ShowNakedTPOPOC || ShowPriorSessionTPOPOC || ShowNakedTPOVAHVAL || ShowPriorSessionTPOVAHVAL
                || ShowNakedTPOSD2 || ShowNakedTPOSD3 || ShowNakedSinglePrints)
                CheckNakedLevelTests(tickPrice, tickTime);

            // --- Live support/resistance role tracking on prior-session and naked levels ---
            // Only feed the state machine ticks that fall inside the chart's trading-hours
            // template session (lastKeyBegin/lastKeyEnd come from the primary series'
            // SessionIterator in GetSessionKey above). A tick outside that window (e.g.
            // Globex when the chart runs an RTH template) never reaches the machine, so
            // roles freeze at the template close and resume at the next template open.
            if (ShowLevelRoles && tickTime >= lastKeyBegin && tickTime <= lastKeyEnd)
                UpdateLevelRoles(tickPrice, tickTime, upVol, downVol);

            // --- TPO letter construction ---
            if (ShowTPOLetters || ShowTPOPOC || ShowTPOVAHVAL || ShowTPOSD2 || ShowTPOSD3
                || ShowNakedTPOPOC || ShowPriorSessionTPOPOC || ShowNakedTPOVAHVAL || ShowPriorSessionTPOVAHVAL
                || ShowNakedTPOSD2 || ShowNakedTPOSD3
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
        }

        /// <summary>
        /// Maps a tick to the primary bar it belongs to.
        /// Historically the tick series is processed BEFORE the primary bar that contains
        /// those ticks closes, so CurrentBars[0] still points at the previous bar. Without
        /// this correction every historical bar's bid/ask volume 
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

            }

            lastBarTime = barTime;



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

            sessions.Add(currentSession);

            // Trim to MaxSessionsToShow
            while (sessions.Count > MaxSessionsToShow)
                sessions.RemoveAt(0);

            ArchiveLevelSession(currentSession);   // LevelAccessor

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
                    if (IsLevelTouched(price, PocPrice(s.VPOCLevel)))
                        { s.VPOCTested = true; s.VPOCBreakTime = breakTime; }
                }

                // VVAH test
                if (!s.VVAHTested && s.VVAH > 0)
                {
                    if (IsLevelTouched(price, s.VVAH * rowSize + rowSize))
                        { s.VVAHTested = true; s.VVAHBreakTime = breakTime; }
                }

                // VVAL test
                if (!s.VVALTested && s.VVAL > 0)
                {
                    if (IsLevelTouched(price, s.VVAL * rowSize))
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
                    if (IsLevelTouched(price, s.SessionHigh))
                        { s.HighTested = true; s.HighBreakTime = breakTime; }
                }

                // Session low test
                if (!s.LowTested && s.LowLevel > 0)
                {
                    if (IsLevelTouched(price, s.SessionLow))
                        { s.LowTested = true; s.LowBreakTime = breakTime; }
                }

                // === TPO naked level tests ===

                if (!s.TPOPOCTested && s.POCLevel > 0)
                {
                    if (IsLevelTouched(price, PocPrice(s.POCLevel)))
                        { s.TPOPOCTested = true; s.TPOPOCBreakTime = breakTime; }
                }
                if (!s.TPOVAHTested && s.TPOVAH > 0)
                {
                    if (IsLevelTouched(price, s.TPOVAH * rowSize + rowSize))
                        { s.TPOVAHTested = true; s.TPOVAHBreakTime = breakTime; }
                }
                if (!s.TPOVALTested && s.TPOVAL > 0)
                {
                    if (IsLevelTouched(price, s.TPOVAL * rowSize))
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

        #region Level Roles (live support/resistance state machine)

        private enum LRQuality
        {
            None,       // no volume evidence (or annotation disabled)
            Absorbed,   // dominant aggression into the level was rejected (strong hold)
            Thin,       // below RoleInteractionMinVolume — weak/unconfirmed interaction
            Confirmed   // dominant aggression behind the accepted break
        }

        private enum LRState
        {
            Untested,   // no tick has reached the touch-tolerance band this session
            Testing,    // price is inside the touch-tolerance band
            Held,       // a test was completed and the level rejected it (held)
            Breaking,   // an acceptance run is active; brief pokes back to the role side pause it, not fail it
            Flipped     // break accepted — the level now acts in the opposite role
        }

        private class LevelRoleState
        {
            public LRState  State         = LRState.Untested;
            public int      Role;         // last confirmed acting side: +1 = support (price above), -1 = resistance (price below), 0 = none yet
            public bool     InBand;       // price currently inside the touch-tolerance band
            public bool     PendingReject; // current band entry can mature into a completed rejection
            public int      Touches;
            public int      Helds;
            public LRQuality Quality;      // quality of the interaction behind the last hold/flip
            public long     BandBuy;       // aggressor buy volume of the current/last touch-band visit
            public long     BandSell;      // aggressor sell volume of the current/last touch-band visit
            public long     BreakBuy;      // aggressor buy volume of the current break run
            public long     BreakSell;     // aggressor sell volume of the current break run
            public int      BreakDir;     // direction of the running break attempt: +1 up, -1 down, 0 = none
            public double   BreakElapsed; // traded seconds in the current break run (template-session gaps excluded)
            public bool     RunPaused;    // acceptance run paused: price re-crossed to the role side without yet failing the run
            public bool     ReclaimRun;   // the current run is failing a flip: at acceptance the original role re-asserts
            public double   RoleElapsed;  // traded seconds the role side has re-asserted during the current paused run
        }

        private class RoleTracker
        {
            public LevelRoleState State;
            public double         Level;
        }

        private string RoleKey(SessionProfile s, string type)
        { return s.StartTime.Ticks.ToString() + "|" + type; }

        /// <summary>
        /// Price of a profile row's POC center snapped to the nearest tradable tick —
        /// used for every VPOC / TPO POC line, zone, label, tracker and touch test so
        /// the level sits on the tick grid (row centers fall between ticks whenever
        /// Ticks Per Row is odd, which would make a 0-tick tolerance untouchable).
        /// </summary>
        private double PocPrice(int level)
        {
            double raw = level * rowSize + rowSize * 0.5;
            return Instrument != null && Instrument.MasterInstrument != null
                ? Instrument.MasterInstrument.RoundToTickSize(raw)
                : Math.Round(raw / TickSize) * TickSize;
        }

        /// <summary>
        /// <summary>
        /// Touch band half-width in price. Ticks > 0: exactly that many ticks.
        /// 0 (print-at-the-line): half a tick — every tracked line price sits on
        /// the tick grid (POC centers via PocPrice), so only an actual print at or
        /// on the line's exact price registers as a touch.
        /// </summary>
        private double TouchTolerance()
        {
            double ts  = rowSize / Math.Max(1, TicksPerRow);
            int ticks  = Math.Max(0, LevelRoleTouchToleranceTicks);
            return ticks > 0 ? ticks * ts : ts * 0.5;
        }

        /// <summary>
        /// The one touch definition shared by naked-level termination and the role
        /// classifier: within Role Touch Tolerance Ticks of the exact drawn line
        /// price. A level can never end as “tested” while the classifier still
        /// shows it untested, or the reverse.
        /// </summary>
        private bool IsLevelTouched(double price, double linePrice)
        {
            return Math.Abs(price - linePrice) <= TouchTolerance();
        }

        /// <summary>
        /// Drives the per-level support/resistance state machine from the tick stream.
        /// Roles are positional (price above a level = support, below = resistance) and
        /// are confirmed by tick interactions: tests in the tolerance band, completed
        /// rejections (held) and breaks that hold beyond the level for
        /// LevelRoleAcceptSeconds of traded time (flip). All timing uses tick timestamps,
        /// so behavior is identical on any chart bar size and rebuilds during the
        /// historical replay. Only traded time inside the chart's template session
        /// accrues — frozen gaps (overnight, closed template windows) never count
        /// toward a break.
        /// Per-tick execution is allocation-free: tracked levels are cached and the
        /// cache is rebuilt only when the session set or visibility toggles change.
        /// </summary>
        private void UpdateLevelRoles(double tickPrice, DateTime tickTime,
            long upVol, long downVol)
        {
            if (currentSession == null || sessions == null || sessions.Count < 2) return;

            // Traded seconds since the previous tick: the acceptance timer runs on
            // this, so gap time (overnight, closed template windows) never completes
            // a break — a run resumed after a frozen gap keeps its banked elapsed
            // and only traded time inside the template session accrues.
            double tradedDelta = 0;
            if (roleLastTickTime != DateTime.MinValue && roleLastTickTime < tickTime
                && roleLastTickSessionKey == lastKeyBegin)
                tradedDelta = (tickTime - roleLastTickTime).TotalSeconds;
            roleLastTickTime       = tickTime;
            roleLastTickSessionKey = lastKeyBegin;

            bool cacheValid =
                roleTrackerSessionCount == sessions.Count
                && roleTrackerMostRecent == mostRecentCompletedSession
                && roleTrackerCurrentStart == currentSession.StartTime
                && roleTrackerPriorHiLo == ShowPriorSessionLevels
                && roleTrackerRolesHiLo == LevelRolesHiLo
                && roleTrackerPriorVPOC == ShowPriorSessionVPOC
                && roleTrackerNakedVPOC == ShowNakedVPOC
                && roleTrackerNakedVA == ShowNakedVAHVAL
                && roleTrackerNakedTPOPoc == ShowNakedTPOPOC
                && roleTrackerNakedTPOVA == ShowNakedTPOVAHVAL
                && roleTrackerPriorTPOPoc == ShowPriorSessionTPOPOC
                && roleTrackerPriorTPOVA == ShowPriorSessionTPOVAHVAL;

            if (!cacheValid)
                RebuildRoleTrackers();

            double tol = TouchTolerance();

            for (int i = 0; i < roleTrackers.Count; i++)
            {
                RoleTracker t = roleTrackers[i];
                RunLevelRoleMachine(t.State, t.Level, tickPrice, tickTime, tol, tradedDelta, upVol, downVol);
            }
        }

        private void RebuildRoleTrackers()
        {
            roleTrackers.Clear();

            // Mirrors the visibility rules used when each extension line is drawn.
            for (int i = 0; i < sessions.Count - 1; i++)
            {
                SessionProfile s = sessions[i];
                if (s.VolumeAtPrice.Count == 0) continue;
                bool isMostRecent = (s == mostRecentCompletedSession);

                if (ShowPriorSessionLevels && LevelRolesHiLo)
                {
                    if (s.HighLevel > 0)
                        AddRoleTracker(RoleKey(s, "HI"), s.SessionHigh);
                    if (s.LowLevel > 0)
                        AddRoleTracker(RoleKey(s, "LO"), s.SessionLow);
                }

                if ((isMostRecent ? ShowPriorSessionVPOC : ShowNakedVPOC) && s.VPOCLevel > 0)
                    AddRoleTracker(RoleKey(s, "VPOC"), PocPrice(s.VPOCLevel));

                if (ShowNakedVAHVAL)
                {
                    if (s.VVAH > 0)
                        AddRoleTracker(RoleKey(s, "VVAH"), s.VVAH * rowSize + rowSize);
                    if (s.VVAL > 0)
                        AddRoleTracker(RoleKey(s, "VVAL"), s.VVAL * rowSize);
                }

                if ((isMostRecent ? ShowPriorSessionTPOPOC : ShowNakedTPOPOC) && s.POCLevel > 0)
                    AddRoleTracker(RoleKey(s, "TPOPOC"), PocPrice(s.POCLevel));
                if (isMostRecent ? ShowPriorSessionTPOVAHVAL : ShowNakedTPOVAHVAL)
                {
                    if (s.TPOVAH > 0)
                        AddRoleTracker(RoleKey(s, "TPOVAH"), s.TPOVAH * rowSize + rowSize);
                    if (s.TPOVAL > 0)
                        AddRoleTracker(RoleKey(s, "TPOVAL"), s.TPOVAL * rowSize);
                }
            }

            roleTrackerSessionCount = sessions.Count;
            roleTrackerMostRecent   = mostRecentCompletedSession;
            roleTrackerCurrentStart = currentSession.StartTime;
            roleTrackerPriorHiLo    = ShowPriorSessionLevels;
            roleTrackerRolesHiLo    = LevelRolesHiLo;
            roleTrackerPriorVPOC    = ShowPriorSessionVPOC;
            roleTrackerNakedVPOC    = ShowNakedVPOC;
            roleTrackerNakedVA      = ShowNakedVAHVAL;
            roleTrackerNakedTPOPoc  = ShowNakedTPOPOC;
            roleTrackerNakedTPOVA   = ShowNakedTPOVAHVAL;
            roleTrackerPriorTPOPoc  = ShowPriorSessionTPOPOC;
            roleTrackerPriorTPOVA   = ShowPriorSessionTPOVAHVAL;
        }

        private void AddRoleTracker(string key, double level)
        {
            LevelRoleState st;
            if (!levelRoleStates.TryGetValue(key, out st) || st == null)
            {
                st = new LevelRoleState();
                levelRoleStates[key] = st;
            }

            RoleTracker t = new RoleTracker();
            t.State = st;
            t.Level = level;
            roleTrackers.Add(t);
        }

        private void RunLevelRoleMachine(LevelRoleState st, double level, double tickPrice,
            DateTime tickTime, double tol, double tradedDelta, long upVol, long downVol)
        {
            double d  = tickPrice - level;
            double ad = Math.Abs(d);

            // Inside the touch-tolerance band. With no acceptance run active this
            // is the approach of a new test: register it and arm the pending
            // rejection. During an ACTIVE acceptance run a retest that touches the
            // band must not erase the clock — banking the traded time beyond the
            // level and resuming once price is back beyond it lets a level that is
            // repeatedly retested from beyond flip roles correctly; only a decisive
            // return to the role side (the failed-break branch below) kills the run.
            if (ad <= tol)
            {
                if (st.BreakDir != 0)
                {
                    st.InBand      = true;
                    st.RunPaused   = false;
                    st.RoleElapsed = 0;
                    st.BandBuy    += upVol;
                    st.BandSell   += downVol;
                    return;
                }

                st.BreakElapsed = 0;
                st.RunPaused    = false;
                st.RoleElapsed  = 0;
                if (st.State == LRState.Breaking)
                    st.State = LRState.Testing;
                if (!st.InBand)
                {
                    st.InBand        = true;
                    st.PendingReject = true;
                    st.Touches++;
                    st.BandBuy       = 0;
                    st.BandSell      = 0;
                }
                st.BandBuy  += upVol;
                st.BandSell += downVol;
                if (st.State == LRState.Untested)
                    st.State = LRState.Testing;
                return;
            }

            st.InBand = false;
            int side = d > 0 ? 1 : -1;

            if (st.Role == 0)
            {
                // first decisive print of the session: seeds the role by position,
                // no hold or flip event to credit yet
                st.State         = LRState.Held;
                st.Role          = side;
                st.PendingReject = false;
            }
            else if (side == st.Role)
            {
                if (st.State == LRState.Breaking)
                {
                    // price re-crossed to the role side during an acceptance run:
                    // brief pokes do not fail the run (chop across the line must
                    // not veto an acceptance still building); the run fails only
                    // when the role side re-asserts for LevelRoleAcceptSeconds
                    st.RunPaused    = true;
                    st.RoleElapsed += tradedDelta;
                    if (st.RoleElapsed >= LevelRoleAcceptSeconds)
                    {
                        st.Helds++;
                        st.Quality       = ClassifyTest(st, st.Role);
                        st.BreakDir      = 0;
                        st.BreakElapsed  = 0;
                        st.RoleElapsed   = 0;
                        st.RunPaused     = false;
                        st.ReclaimRun    = false;
                        st.PendingReject = false;
                        st.State         = LRState.Held;
                    }
                }
                else
                {
                    st.RoleElapsed = 0;
                    if (st.State == LRState.Flipped)
                    {
                        if (st.PendingReject)
                        {
                            // the retest of the flipped level was rejected
                            st.State         = LRState.Held;
                            st.Helds++;
                            st.PendingReject = false;
                        }
                    }
                    else if (st.PendingReject)
                    {
                        // the band entry just left behind matured into a completed rejection
                        st.Helds++;
                        st.Quality       = ClassifyTest(st, st.Role);
                        st.PendingReject = false;
                        st.State         = LRState.Held;
                    }
                    else
                        st.State = LRState.Held;
                }
            }
            else
            {
                // beyond the level, opposite the current role: (re)start the
                // acceptance clock when the run changes. This also covers reclaim
                // of a flipped role — a flip must now be positively failed the
                // same way it was accepted, instead of reverting on one poke
                if (st.BreakDir != side)
                {
                    st.BreakDir     = side;
                    st.BreakElapsed = 0;
                    st.BreakBuy     = 0;
                    st.BreakSell    = 0;
                    st.ReclaimRun   = st.State == LRState.Flipped;
                }
                else
                    st.BreakElapsed += tradedDelta;
                st.RunPaused   = false;
                st.RoleElapsed = 0;
                st.BreakBuy  += upVol;
                st.BreakSell += downVol;
                if (st.BreakElapsed >= LevelRoleAcceptSeconds)
                {
                    st.Quality       = ClassifyBreak(st);
                    if (st.ReclaimRun)
                    {
                        // flips fail like they form: the original role re-asserts
                        st.State  = LRState.Held;
                        st.Helds++;
                        st.ReclaimRun = false;
                    }
                    else
                        st.State = LRState.Flipped;
                    st.Role          = side;
                    st.BreakDir      = 0;
                    st.BreakElapsed  = 0;
                    st.RoleElapsed   = 0;
                    st.PendingReject = false;
                }
                else
                    st.State = LRState.Breaking;
            }
        }

        /// <summary>Grades a completed test: dominant aggression pushed INTO the
        /// level (opposite the holding role) yet was rejected — absorbed.</summary>
        private LRQuality ClassifyTest(LevelRoleState st, int role)
        {
            long vol = st.BandBuy + st.BandSell;
            if (RoleInteractionMinVolume <= 0 || vol <= 0)
                return LRQuality.None;
            if (vol < RoleInteractionMinVolume)
                return LRQuality.Thin;
            long intoVol = role > 0 ? st.BandSell : st.BandBuy;
            return intoVol * 100 >= vol * 60 ? LRQuality.Absorbed : LRQuality.None;
        }

        /// <summary>Grades an accepted break: aggression skewed toward the break
        /// direction confirms it; below minimum volume it is thin.</summary>
        private LRQuality ClassifyBreak(LevelRoleState st)
        {
            long vol = st.BreakBuy + st.BreakSell;
            if (RoleInteractionMinVolume <= 0 || vol <= 0)
                return LRQuality.None;
            if (vol < RoleInteractionMinVolume)
                return LRQuality.Thin;
            long withVol = st.BreakDir > 0 ? st.BreakBuy : st.BreakSell;
            return withVol * 100 >= vol * 60 ? LRQuality.Confirmed : LRQuality.None;
        }

        private string FormatVol(long v)
        {
            if (v >= 100000) return (v / 1000).ToString() + "k";
            if (v >= 1000)   return (v / 1000.0).ToString("F1") + "k";
            return v.ToString();
        }

        private string RoleQualityText(LevelRoleState st, long vol)
        {
            switch (st.Quality)
            {
                case LRQuality.Absorbed:  return " · abs";
                case LRQuality.Confirmed: return " · conf";
                case LRQuality.Thin:      return " · thin";
                default:
                    return (vol > 0 && RoleInteractionMinVolume > 0) ? " · " + FormatVol(vol) : "";
            }
        }

        /// <summary>
        /// Draws the live role tag under a prior-session or naked level line,
        /// anchored at the end of the level's own extension line (the right margin
        /// for live levels, the break bar for dead ones), mirroring how extension
        /// labels follow the line. Tags render for every tracked level whose line is
        /// drawn, regardless of which session the level was last broken in.
        /// </summary>
        private void DrawLevelRoleTag(ChartScale chartScale, int sessionEndX, string key,
            double price, DateTime breakTime)
        {
            if (roleBreakingBrush == null || roleUntestedBrush == null) return;

            int lineEndX = GetExtensionEndX(breakTime);
            if (lineEndX <= sessionEndX) return;

            LevelRoleState st;
            if (!levelRoleStates.TryGetValue(key, out st) || st == null) return;
            long tradedBandVol  = st.BandBuy + st.BandSell;
            long tradedBreakVol = st.BreakBuy + st.BreakSell;

            string roleWord = st.Role < 0 ? "Resistance" : "Support";
            SharpDX.Direct2D1.Brush brush;
            string tag;
            switch (st.State)
            {
                case LRState.Untested:
                    brush = roleUntestedBrush;
                    tag   = st.Role < 0 ? "Resistance (untested)"
                          : st.Role > 0 ? "Support (untested)" : "(untested)";
                    break;
                case LRState.Testing:
                    brush = roleBreakingBrush;
                    // an unseeded level has no direction yet — never claim one
                    tag   = st.Role != 0 ? roleWord + " · testing" : "(testing)";
                    if (st.Role != 0 && tradedBandVol > 0)
                        tag += " · " + FormatVol(tradedBandVol);
                    break;
                case LRState.Held:
                    brush = st.Role < 0 ? roleResistanceBrush : roleSupportBrush;
                    if (st.Helds > 0)
                        tag = roleWord + " · held x" + st.Helds.ToString();
                    else
                        tag = roleWord;
                    tag += RoleQualityText(st, tradedBandVol);
                    break;
                case LRState.Flipped:
                    brush = st.Role < 0 ? roleResistanceBrush : roleSupportBrush;
                    tag   = roleWord + " · flipped";
                    tag  += RoleQualityText(st, tradedBreakVol);
                    break;
                default: // Breaking
                    int elapsed = (int)st.BreakElapsed;
                    brush = roleBreakingBrush;
                    tag   = roleWord + " · BREAKING " + elapsed.ToString() + "s"
                          + (st.RunPaused ? " (paused)" : "");
                    break;
            }

            int sep = key.IndexOf('|');
            PublishLevelRole(sep >= 0 ? key.Substring(sep + 1) : key, price, tag, st.Role);

            float y = chartScale.GetYByValue(price);
            RenderTarget.DrawText(tag, statsFormat,
                new SharpDX.RectangleF(lineEndX - 174, y + 8, 170, 14), brush);
        }

        #endregion

        // =========================================================
        //  PIERCE-AND-RETURN ALERTS
        // =========================================================

        #region OnRender

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);


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

            // Pass 1: per-session profile + fill
            foreach (var session in sessions)
            {
                if (session.StartBarIndex < 0) continue;
                RenderSession(chartControl, chartScale, session);
            }

            // Pass 2: prior-session extension lines (drawn on top of profiles)
            ClearPublishedLevels();
            foreach (var session in sessions)
            {
                if (session.StartBarIndex < 0) continue;
                RenderSessionExtensions(chartControl, chartScale, session);
            }

            // Pass 3: current-session level labels at the right margin
            RenderCurrentSessionLevelLabels(chartControl, chartScale);

            // Cluster zones removed

            // Pass 6: per-bar Bid vs Ask totals at top/bottom of each primary bar
            if (ShowPerBarBidAskTotals)
                RenderPerBarBidAskTotals(chartControl, chartScale);

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
            renderedSessionStart = session.StartTime;
            renderedSessionEnd   = session.EndTime;
            string pfx = (session == mostRecentCompletedSession) ? "p" : "n";

            // Naked VPOC
            // Prior (most recent completed) session's VPOC has its own toggle; older sessions use Naked VPOC.
            bool vpocOn = (session == mostRecentCompletedSession) ? ShowPriorSessionVPOC : ShowNakedVPOC;
            if (vpocOn && session.VPOCLevel > 0)
            {
                double vpocPrice = PocPrice(session.VPOCLevel);
                RenderExtensionLine(chartScale, endX, vpocPrice,
                    session.VPOCBreakTime, nakedVpocBrush, pfx + "VPOC " + vpocPrice.ToString("F2"), ShowLevelZones);
                DrawLevelRoleTag(chartScale, endX, RoleKey(session, "VPOC"), vpocPrice, session.VPOCBreakTime);
            }

            // Naked VVAH / VVAL
            if (ShowNakedVAHVAL)
            {
                if (session.VVAH > 0)
                {
                    double vvahPrice = session.VVAH * rowSize + rowSize;
                    RenderExtensionLine(chartScale, endX, vvahPrice,
                        session.VVAHBreakTime, nakedVAHBrush, pfx + "VVAH " + vvahPrice.ToString("F2"), ShowLevelZones);
                    DrawLevelRoleTag(chartScale, endX, RoleKey(session, "VVAH"), vvahPrice, session.VVAHBreakTime);
                }
                if (session.VVAL > 0)
                {
                    double vvalPrice = session.VVAL * rowSize;
                    RenderExtensionLine(chartScale, endX, vvalPrice,
                        session.VVALBreakTime, nakedVABrush, pfx + "VVAL " + vvalPrice.ToString("F2"), ShowLevelZones);
                    DrawLevelRoleTag(chartScale, endX, RoleKey(session, "VVAL"), vvalPrice, session.VVALBreakTime);
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
                {
                    RenderExtensionLine(chartScale, endX, session.SessionHigh,
                        session.HighBreakTime, priorHighLowBrush, pfx + "HI " + session.SessionHigh.ToString("F2"), ShowLevelZones);
                    DrawLevelRoleTag(chartScale, endX, RoleKey(session, "HI"), session.SessionHigh, session.HighBreakTime);
                }
                if (session.LowLevel > 0)
                {
                    RenderExtensionLine(chartScale, endX, session.SessionLow,
                        session.LowBreakTime, priorHighLowBrush, pfx + "LO " + session.SessionLow.ToString("F2"), ShowLevelZones);
                    DrawLevelRoleTag(chartScale, endX, RoleKey(session, "LO"), session.SessionLow, session.LowBreakTime);
                }
            }

            // === TPO naked extensions ===
            // Always RTH levels only (when split, primary TPO fields are RTH; Globex is never extended).
            string rPfx = SplitGlobexRthTPO ? "r" : "";
            // Prior (most recent completed) session's TPO POC has its own toggle; older sessions use Naked TPO POC.
            bool tpocOn = (session == mostRecentCompletedSession) ? ShowPriorSessionTPOPOC : ShowNakedTPOPOC;
            if (tpocOn && session.POCLevel > 0)
            {
                double p = PocPrice(session.POCLevel);
                SharpDX.Direct2D1.Brush tpocBrush = session == mostRecentCompletedSession
                    ? priorTPOPocBrush : nakedTPOPocBrush;
                RenderExtensionLine(chartScale, endX, p,
                    session.TPOPOCBreakTime, tpocBrush, pfx + rPfx + "POC " + p.ToString("F2"), ShowLevelZones);
                DrawLevelRoleTag(chartScale, endX, RoleKey(session, "TPOPOC"), p, session.TPOPOCBreakTime);
            }
            if ((session == mostRecentCompletedSession) ? ShowPriorSessionTPOVAHVAL : ShowNakedTPOVAHVAL)
            {
                SharpDX.Direct2D1.Brush tpoVABrush = session == mostRecentCompletedSession
                    ? priorTPOVABrush : nakedTPOVABrush;

                if (session.TPOVAH > 0)
                {
                    RenderExtensionLine(chartScale, endX, session.TPOVAH * rowSize + rowSize,
                        session.TPOVAHBreakTime, tpoVABrush, pfx + rPfx + "TPOVAH " + (session.TPOVAH * rowSize + rowSize).ToString("F2"), ShowLevelZones);
                    DrawLevelRoleTag(chartScale, endX, RoleKey(session, "TPOVAH"), session.TPOVAH * rowSize + rowSize, session.TPOVAHBreakTime);
                }
                if (session.TPOVAL > 0)
                {
                    RenderExtensionLine(chartScale, endX, session.TPOVAL * rowSize,
                        session.TPOVALBreakTime, tpoVABrush, pfx + rPfx + "TPOVAL " + (session.TPOVAL * rowSize).ToString("F2"), ShowLevelZones);
                    DrawLevelRoleTag(chartScale, endX, RoleKey(session, "TPOVAL"), session.TPOVAL * rowSize, session.TPOVALBreakTime);
                }
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
                double p = PocPrice(currentSession.VPOCLevel);
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
                double p = PocPrice(currentSession.POCLevel);
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
                { double p = PocPrice(currentSession.GlobexPOCLevel); float y = chartScale.GetYByValue(p);
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
            { double p = PocPrice(session.VPOCLevel); float y = chartScale.GetYByValue(p);
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
            { double p = PocPrice(session.POCLevel); float y = chartScale.GetYByValue(p);
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
                    DrawLevelZone(chartScale, startX, endX, PocPrice(session.VPOCLevel));
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
                    DrawLevelZone(chartScale, startX, endX, PocPrice(session.POCLevel));
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
                        DrawLevelZone(chartScale, startX, endX, PocPrice(session.GlobexPOCLevel));
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
                //  Session Sell | Session Buy | Vol Profile
                //  (sell)         (buy)

                bool showSess = ShowSessionBuysSells;

                // Scale font first, then size columns tightly around digit width (ladder-dense).
                float sampleRowH = Math.Abs(chartScale.GetYByValue(0) - chartScale.GetYByValue(rowSize));
                float fpFontSize = Math.Max(5f, Math.Min(16f, sampleRowH * 0.72f));
                // ~5 monospace digits + small pad; clamp so 4 columns stay compact next to the profile
                float colW = Math.Max(16f, Math.Min(32f, fpFontSize * 2.4f + 6f));

                int nFpCols = showSess ? 2 : 0;
                float fpWidth = colW * nFpCols;
                float fpRight = rightEdge - vpWidth;                  // vol-profile left edge
                float fpLeft  = fpRight - fpWidth;                    // footprint left edge

                // Column left edges (L→R) — packed, no extra gap between columns
                float x0 = fpLeft;
                float x1 = fpLeft + colW;
                // If session columns are on: 0=sessSell, 1=sessBuy
                float sessSellX = showSess ? x0 : -1f;
                float sessBuyX  = showSess ? x1 : -1f;

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
                foreach (var kvp2 in session.VolumeAtPrice)
                {
                    int  lv  = kvp2.Key;
                    long b   = session.SellVolumeAtPrice.ContainsKey(lv) ? session.SellVolumeAtPrice[lv] : 0;
                    long a   = session.BuyVolumeAtPrice.ContainsKey(lv)  ? session.BuyVolumeAtPrice[lv]  : 0;
                    totalBidVol += b;
                    totalAskVol += a;

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
                    // Session Sell | Session Buy
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

                        if (showSess && sellFmt != null && sessSell > 0)
                            RenderTarget.DrawText(sessSell.ToString(), sellFmt,
                                new SharpDX.RectangleF(sessSellX, priceTY, colW - 2f, rowH), sessSellBrush);
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
            double vpocPrice = PocPrice(session.VPOCLevel);
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

            // VPOC price
            if (session.VPOCLevel > 0)
            {
                double vpocPrice = PocPrice(session.VPOCLevel);
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
                float pocY = chartScale.GetYByValue((float)PocPrice(session.POCLevel));
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
                    float y = chartScale.GetYByValue((float)PocPrice(session.GlobexPOCLevel));
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

            PublishLevelLine(label, price, breakTime);

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

        /// <summary>
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

        /// <summary>Dominant / opposing ratio (opposing floored at 1) — used to pick the stronger side.</summary>
        private double DiagonalRatio(long dominant, long opposing)
        {
            return (double)dominant / (double)Math.Max(1L, opposing);
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











        // =========================================================
        //  AUTO TRADE — market entry + ATR stop/target (draggable)
        // =========================================================


        // =========================================================
        //  Chart UI — Auto Trade on/off button (lower left)
        // =========================================================





        /// <summary>
        /// Called when a live alert fires. Submits market entry; stop + target are
        /// attached on fill as working orders (drag on chart to adjust).
        /// </summary>



        // ===== LevelAccessor (additive, read-only) - added for LevelAbsorption =====
        // Exposes prior/naked session levels (VPOC, VVAH, VVAL, HI, LO, plus SPL/SPH = bottom/top edge of each single print
        // band) and signal times to other indicators on the
        // same chart. Keeps a lightweight archive that is NOT trimmed to MaxSessionsToShow, so history older than the
        // drawn sessions can still be analysed. Nothing here changes how levels are calculated or drawn.
        #region LevelAccessor

        public class STVPLevelRecord
        {
            public string   Type;          // VPOC, VVAH, VVAL, HI, LO, SPL, SPH
            public string   CanonType;     // bare tracker type (optional, set by the YMCross publisher)
            public string   RoleText;      // live support/resistance tag (optional, set by the YMCross publisher)
            public int      RoleDir;       // +1 support, -1 resistance, 0 none/unknown
            public double   Price;
            public DateTime SessionStart;
            public DateTime SessionEnd;
            public DateTime BreakTime;     // first time a tick printed in the level's row; MinValue = never tested.
                                           // SPL/SPH: time the whole single print band was filled (MinValue = still open)
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
            if (s.VPOCLevel > 0) add("VPOC", PocPrice(s.VPOCLevel), s.VPOCBreakTime);
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

        #endregion

        // ===== YMCross publisher (additive, read-only) =====
        // Every extension line actually drawn by RenderSessionExtensions is published here so
        // STVPYMCrossLevels (loaded on a YM chart) can mirror this chart's enabled levels.
        // Nothing here changes how levels are calculated or drawn.
        #region YMCrossPublisher

        private static readonly object          ymPublishLock         = new object();
        private static List<STVPLevelRecord>    ymPublishedLevels     = new List<STVPLevelRecord>();
        private static string                   ymPublishedInstrument = "";
        private static double                   ymPublishedLastPrice  = 0;
        private static DateTime                 ymPublishedAt         = DateTime.MinValue;

        private DateTime renderedSessionStart = DateTime.MinValue;
        private DateTime renderedSessionEnd   = DateTime.MinValue;
        private double   lastTickPrice        = 0;

        /// <summary>Snapshot of every extension line currently drawn on this chart, plus the source instrument and last traded price.</summary>
        public static List<STVPLevelRecord> GetPublishedLevels(string sourceFilter, int maxAgeSeconds, out double sourceLastPrice)
        {
            sourceLastPrice = 0;
            lock (ymPublishLock)
            {
                if (!string.Equals(ymPublishedInstrument, sourceFilter ?? "", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (maxAgeSeconds > 0 && (DateTime.UtcNow - ymPublishedAt).TotalSeconds > maxAgeSeconds)
                    return null;
                if (ymPublishedLevels.Count == 0)
                    return null;
                sourceLastPrice = ymPublishedLastPrice;
                return new List<STVPLevelRecord>(ymPublishedLevels);
            }
        }

        /// <summary>Diagnostics for YMCross: stored instrument, count, last price and publish time, regardless of filter.</summary>
        public static string GetPublicationDiagnostics(string filter, out int count, out double lastPrice, out DateTime publishedAtUtc)
        {
            lock (ymPublishLock)
            {
                count                 = ymPublishedLevels.Count;
                lastPrice             = ymPublishedLastPrice;
                publishedAtUtc        = ymPublishedAt;
                return ymPublishedInstrument;
            }
        }

        private void ClearPublishedLevels()
        {
            lock (ymPublishLock)
            {
                ymPublishedLevels     = new List<STVPLevelRecord>();
                ymPublishedInstrument = Instrument != null ? Instrument.MasterInstrument.Name : "";
                if (lastTickPrice > 0) ymPublishedLastPrice = lastTickPrice;
            }
        }

        private void PublishLevelLine(string label, double price, DateTime breakTime)
        {
            if (price <= 0) return;
            string type = label ?? "";
            int sp = type.LastIndexOf(' ');
            if (sp > 0) type = type.Substring(0, sp);
            string canon = type.TrimStart('p', 'n', 'r', ' ');
            if (canon == "POC")      canon = "TPOPOC";
            else if (canon == "VAH") canon = "TPOVAH";
            else if (canon == "VAL") canon = "TPOVAL";
            var rec = new STVPLevelRecord
            {
                Type         = type,
                CanonType    = canon,
                Price        = price,
                SessionStart = renderedSessionStart,
                SessionEnd   = renderedSessionEnd,
                BreakTime    = breakTime
            };
            lock (ymPublishLock)
            {
                ymPublishedLevels.Add(rec);
                ymPublishedInstrument = Instrument != null ? Instrument.MasterInstrument.Name : "";
                if (lastTickPrice > 0) ymPublishedLastPrice = lastTickPrice;
                ymPublishedAt = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Attaches the live support/resistance tag of a tracked level to its published
        /// record (matched by canonical type + price). Called from DrawLevelRoleTag, which
        /// runs right after the line itself is published in the same render pass.
        /// </summary>
        private void PublishLevelRole(string canonType, double price, string roleText, int roleDir)
        {
            if (canonType == null || roleText == null) return;
            lock (ymPublishLock)
            {
                for (int i = ymPublishedLevels.Count - 1; i >= 0; i--)
                {
                    STVPLevelRecord r = ymPublishedLevels[i];
                    if (r.CanonType == canonType && Math.Abs(r.Price - price) < 1e-8)
                    {
                        r.RoleText = roleText;
                        r.RoleDir  = roleDir;
                        return;
                    }
                }
            }
        }

        #endregion

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
                nakedVpocBrush        = DxBrush(NakedVPOCColor);
                nakedVABrush          = DxBrush(NakedVAColor);
                nakedVAHBrush         = DxBrush(NakedVAHColor);

                // Level role tags
                roleSupportBrush      = DxBrush(LevelRoleSupportColor);
                roleResistanceBrush   = DxBrush(LevelRoleResistanceColor);
                roleBreakingBrush     = DxBrush(LevelRoleBreakingColor);
                roleUntestedBrush     = DxBrushWithAlpha(System.Windows.Media.Brushes.Gray, 0.85f);

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
                priorTPOPocBrush = DxBrush(PriorSessionTPOPOCColor);
                nakedTPOVABrush  = DxBrush(NakedTPOVAColor);
                priorTPOVABrush  = DxBrush(PriorSessionTPOVAColor);
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

                analyticsPanelBgBrush = new SharpDX.Direct2D1.SolidColorBrush(
                    RenderTarget, new SharpDX.Color4(0f, 0f, 0f, 0.55f));

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

        private void DisposeResources()
        {
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
            if (nakedVpocBrush        != null) { nakedVpocBrush.Dispose();        nakedVpocBrush        = null; }
            if (nakedVABrush          != null) { nakedVABrush.Dispose();          nakedVABrush          = null; }
            if (nakedVAHBrush         != null) { nakedVAHBrush.Dispose();         nakedVAHBrush         = null; }
            if (roleSupportBrush      != null) { roleSupportBrush.Dispose();      roleSupportBrush      = null; }
            if (roleResistanceBrush   != null) { roleResistanceBrush.Dispose();   roleResistanceBrush   = null; }
            if (roleBreakingBrush     != null) { roleBreakingBrush.Dispose();     roleBreakingBrush     = null; }
            if (roleUntestedBrush     != null) { roleUntestedBrush.Dispose();     roleUntestedBrush     = null; }
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
            if (priorTPOPocBrush != null) { priorTPOPocBrush.Dispose(); priorTPOPocBrush = null; }
            if (nakedTPOVABrush  != null) { nakedTPOVABrush.Dispose();  nakedTPOVABrush  = null; }
            if (priorTPOVABrush  != null) { priorTPOVABrush.Dispose();  priorTPOVABrush  = null; }
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
            if (analyticsPanelBgBrush   != null) { analyticsPanelBgBrush.Dispose();   analyticsPanelBgBrush   = null; }

            resourcesCreated = false;
        }

        #endregion
    }
}
