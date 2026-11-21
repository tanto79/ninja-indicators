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
    public enum AntonsonDisplayStyle
    {
        /// <summary>Draw outline rectangles around each value area (original behaviour).</summary>
        Box,
        /// <summary>Draw a high-line and low-line for each value area, spanning the full session width.</summary>
        Lines
    }

    /// <summary>Stroke style for individual value area level lines.</summary>

    /// <summary>Controls what metric the volume profile bars display.</summary>
    public enum AntonsonBarDisplayMode
    {
        /// <summary>Bars are sized and colored by total traded volume at each price (default).</summary>
        Volume,
        /// <summary>Bars are sized by absolute tick delta (buy vol − sell vol) and colored green/red by sign.</summary>
        Delta,
        /// <summary>Footprint shows Buy volume on the LEFT and Sell volume on the RIGHT (same order as SuperDOM), with volume profile on the far right.</summary>
        BidAsk
    }

    /// <summary>How aggressive buy vs sell is assigned on each trade.</summary>
    public enum AntonsonTradeClassification
    {
        /// <summary>Quote rule: price ≥ ask → buy, price ≤ bid → sell, else Lee-Ready tick rule (default, NT-style).</summary>
        QuoteRule,
        /// <summary>Midpoint rule used by GemsTraderLadder: close &gt; (bid+ask)/2 → buy, else sell. Use this when matching ladder Session Buys/Sells numbers.</summary>
        Midpoint
    }

    /// <summary>Controls whether the volume/TPO profile resets each trading session or each week.</summary>
    public enum AntonsonProfilePeriod
    {
        /// <summary>One profile per trading session (default).</summary>
        Session,
        /// <summary>One cumulative profile for the full trading week.</summary>
        Weekly
    }

    /// <summary>How "RTH" is defined for RTH tracking.</summary>
    public enum AntonsonRthMode
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
    /// AntonsonProfile
    /// Tick-accurate, session-based Volume by Price (VbP) indicator.
    ///
    /// Features:
    ///   - Per-session VbP histogram with HVN / LVN / MVN bar coloring
    ///   - VPOC line (dashed, across session)
    ///   - Volume Value Area (VVAH / VVAL) – 70% of session volume

    ///   - Naked VPOC / VVAH / VVAL extension lines into future sessions
    ///   - Prior session High / Low extension lines

    ///   - Configurable VbP width for current vs prior sessions
    ///   - Right-edge offset so it doesn't overlap other indicators
    ///
    /// Self-contained.
    /// </summary>

    public class AntonsonProfile : Indicator
    {
        #region Inner Classes

        private class TPOPeriod
        {
            public char              Letter;
            public DateTime          StartTime;
            public DateTime          EndTime;

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

            // Incremental profile stats (maintained on each tick — avoids full rescans)
            public long TotalVolume;       // running sum of all volume in the session
            public long MaxVolumeAtVpoc;   // volume at the current VPOCLevel

            // TPO period counter (avoid List.FindAll on every new period)
            public int PeriodCount;        // sequential letter index across the session

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

            public bool     HighTested;
            public bool     LowTested;
            // RTH close — only tested during subsequent RTH periods (overnight ignored)
            public bool     RTHCloseTested;
            public DateTime VPOCBreakTime = DateTime.MinValue;
            public DateTime VVAHBreakTime = DateTime.MinValue;
            public DateTime VVALBreakTime = DateTime.MinValue;

            public DateTime HighBreakTime = DateTime.MinValue;
            public DateTime LowBreakTime  = DateTime.MinValue;
            public DateTime RTHCloseBreakTime = DateTime.MinValue;

            // ================================================================
            // TPO data — populated from tick-level letter construction
            // ================================================================

            // Raw TPO data
            public Dictionary<int, HashSet<char>> TPOsAtPrice; // price level → set of TPO letters (combined or display)
            public List<TPOPeriod>                Periods;      // ordered list of TPO time periods

            // TPO-based levels (prefixed TPO to distinguish from volume levels)
            // These hold the full-session cumulative TPO levels.
            public int  POCLevel;   // TPO Point of Control (highest TPO count level)
            public int  TPOVAH;     // TPO 1σ Value Area High (68% of TPO letters)
            public int  TPOVAL;     // TPO 1σ Value Area Low
            public int  VAM;        // midpoint of TPO VA

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

            public DateTime TPOPOCBreakTime  = DateTime.MinValue;
            public DateTime TPOVAHBreakTime  = DateTime.MinValue;
            public DateTime TPOVALBreakTime  = DateTime.MinValue;

            // Per-price tick delta — buy vol and sell vol tracked separately at each level
            public Dictionary<int, long> BuyVolumeAtPrice;
            public Dictionary<int, long> SellVolumeAtPrice;

            public SessionProfile()
            {
                VolumeAtPrice     = new Dictionary<int, long>();
                BuyVolumeAtPrice  = new Dictionary<int, long>();
                SellVolumeAtPrice = new Dictionary<int, long>();
                TPOsAtPrice       = new Dictionary<int, HashSet<char>>();

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
        private double               cachedTickSize;   // never read live TickSize from OnRender

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

        private SharpDX.Direct2D1.Brush volumeBarOutlineBrush;

        private SharpDX.Direct2D1.Brush levelZoneFillBrush;      // level-zone fill (VP + TPO levels, all-or-nothing)
        private SharpDX.Direct2D1.Brush levelZoneBorderBrush;    // level-zone outline
        private SharpDX.Direct2D1.Brush roleSupportBrush;        // level role: acting-as-support tag
        private SharpDX.Direct2D1.Brush roleResistanceBrush;     // level role: acting-as-resistance tag
        private SharpDX.Direct2D1.Brush roleBreakingBrush;       // level role: testing/breaking tag
        private SharpDX.Direct2D1.Brush roleUntestedBrush;       // level role: untested tag
        private readonly Dictionary<string, LevelRoleState> levelRoleStates = new Dictionary<string, LevelRoleState>();
        private DateTime roleLastTickTime = DateTime.MinValue;
        private double   roleLastTickPrice = double.NaN;   // last traded price, for the AMT readout
        private readonly List<RoleTracker> roleTrackers = new List<RoleTracker>();
        private readonly List<FlipLineRecord> flipLines = new List<FlipLineRecord>();
        private readonly HashSet<string> flipLineKeys = new HashSet<string>();
        private readonly object flipLinesLock = new object();
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
        private bool           roleTrackerRTHClose;
        private SharpDX.Direct2D1.Brush nakedVpocBrush;
        private SharpDX.Direct2D1.Brush nakedVABrush;      // prior/naked VVAL
        private SharpDX.Direct2D1.Brush nakedVAHBrush;     // prior/naked VVAH
        private SharpDX.Direct2D1.Brush priorHighLowBrush;
        private SharpDX.Direct2D1.Brush rthCloseBrush;
        private SharpDX.Direct2D1.Brush textBrush;
        private SharpDX.DirectWrite.TextFormat volumeFormat;
        private SharpDX.DirectWrite.TextFormat statsFormat;

        private StrokeStyle              dashStrokeStyle;
        private StrokeStyle              dotStrokeStyle;
        private StrokeStyle              dashDotStrokeStyle;

        // Per-level VA line brushes — independent color for each of the 6 VA boundaries
        private SharpDX.Direct2D1.Brush vvahLineBrush;
        private SharpDX.Direct2D1.Brush vvalLineBrush;

        private bool resourcesCreated  = false;
        private bool zOrderInitialized = false;

        // ================================================================
        // Cluster Zone fields
        // ================================================================
        // Cluster zone brushes — fill (semi-transparent) and solid border per tier

        // ================================================================
        // Delta tracking fields
        // ================================================================
        private int                      lastTickDirectionSign    = 0;  // 1 = uptick, -1 = downtick; carry-forward for equal-price ticks

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

        // Session-key cache — skip SessionIterator when tick is still inside the cached window
        private DateTime cachedSessionKey      = DateTime.MinValue;
        private DateTime cachedKeyWindowBegin  = DateTime.MinValue;
        private DateTime cachedKeyWindowEnd    = DateTime.MinValue;
        private bool     hasCachedSessionKey   = false;

        // Feature-gate caches (refreshed when needed; cheap bools for the tick path)
        private bool gateNeedBidAsk;
        private bool gateAnyTpo;
        private bool gateAnyNaked;
        private bool gateNeedRoles;
        private int  histRoleTickCounter;   // throttle role updates on historical data
        private int  histCalcBarCounter;    // throttle VPOC/VA calc on historical bars
        private const int HistRoleEveryNTicks = 8;   // update roles every N historical ticks
        private const int HistCalcEveryNBars  = 25;  // recalculate profile every N primary bars in historical

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
        public AntonsonRthMode RthMode { get; set; }

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
        public AntonsonProfilePeriod ProfilePeriod { get; set; }

        #endregion

        #region Properties — Display Options

        [NinjaScriptProperty]
        [Display(Name = "Show VPOC Line", Description = "Draw a dashed VPOC line across the session", Order = 1, GroupName = "3. Display Options")]
        public bool ShowVPOC { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Volume Value Area", Description = "Show the 70% volume value area levels (VVAH / VVAL)", Order = 2, GroupName = "3. Display Options")]
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
        [Display(Name = "Show RTH Close Levels",
                 Description = "Extend prior-session RTH close (RTHC) as a dashed line. Survives overnight and is only tested during subsequent RTH periods. Once broken in an RTH session it terminates after that break (naked thereafter if never broken).",
                 Order = 61, GroupName = "3. Display Options")]
        public bool ShowRTHCloseLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Volume Labels", Description = "Print volume numbers on each bar (only when bar is tall enough)", Order = 7, GroupName = "3. Display Options")]
        public bool ShowVolumeLabels { get; set; }

        [NinjaScriptProperty]
        [Range(6, 16)]
        [Display(Name = "Volume Label Font Size", Description = "Font size for volume numbers printed inside profile bars", Order = 16, GroupName = "3. Display Options")]
        public int VolumeFontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "VA Display Style",
                 Description = "Box: draw outline rectangles around each value area (original). Lines: draw a high and low horizontal line for each value area, spanning the full session width.",
                 Order = 20, GroupName = "3. Display Options")]
        public AntonsonDisplayStyle VADisplayStyle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show LVN Extension Fill",
                 Description = "Shade every LVN row across the full session width, from the session start bar to the profile right edge, using a semi-transparent tint of the LVN bar color.",
                 Order = 21, GroupName = "3. Display Options")]
        public bool ShowLVNExtension { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Current Session Volume Levels",
                 Description = "Show or hide the current session VPOC, VVAH, VVAL lines. Prior session within-session lines and all extension lines are not affected.",
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
        public AntonsonBarDisplayMode VolumeBarDisplayMode { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade Classification",
                 Description = "Midpoint (default): close > (bid+ask)/2 → buy, else sell — same rule as GemsTraderLadder. QuoteRule: price ≥ ask → buy, price ≤ bid → sell, else tick rule. Use Ticks Per Row = 1 for price-level parity with the ladder.",
                 Order = 244, GroupName = "3. Display Options")]
        public AntonsonTradeClassification TradeClassification { get; set; }

        [NinjaScriptProperty]
        [Range(6, 16)]
        [Display(Name = "Label Font Size", Description = "Font size for level labels (VPOC, VVAH, VVAL)", Order = 14, GroupName = "3. Display Options")]
        public int LabelFontSize { get; set; }

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
                 Description = "Live support/resistance classification beside each prior-session and naked High/Low/VPOC/VVAH/VVAL: untested, testing, held, breaking (acceptance timer) or flipped. Price side gives the role; tick interactions confirm it. Tick-driven and identical on any chart bar size.",
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
        [Display(Name = "Role Touch Tolerance Ticks", Description = "Ticks either side of a level's drawn price that count as touching it. Used for both naked-level termination and role classification. 0 = only a print at the level's exact price counts (default 8)", Order = 3, GroupName = "3B. Level Roles")]
        public int LevelRoleTouchToleranceTicks { get; set; }

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

        [NinjaScriptProperty]
        [Display(Name = "AMT Acceptance Readout", Description = "Append the auction-market-theory verdict to each role tag: how the CURRENT session's developing VbP is treating this established level (SHORT-GO / SHORT-NO / LONG-GO / stand-down). Level-role study §11.6", Order = 8, GroupName = "3B. Level Roles")]
        public bool ShowAmtReadout { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Hide Held Count (use role label only)", Description = "The study found the held-xN count does NOT predict travel (held 8x moves like held 2x). On = show the role label only; Off = keep the legacy held xN text", Order = 9, GroupName = "3B. Level Roles")]
        public bool AmtHideHeldCount { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 2.0)]
        [Display(Name = "AMT Accept Threshold", Description = "Today's volume at the level's price row, as a fraction of today's POC-row volume, below which the auction is REJECTING the level (go signal). Study: low-accept bounce shorts above value = median 103 ticks vs 8 for high-accept", Order = 10, GroupName = "3B. Level Roles")]
        public double AmtAcceptThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000000)]
        [Display(Name = "AMT Min Profile Volume", Description = "Below this total session volume, today's developing profile is too thin to read — tags show '~thin profile (trust the role)'. Covers the pre-10:00 CT hours", Order = 11, GroupName = "3B. Level Roles")]
        public long AmtMinProfileVolume { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 24.0)]
        [Display(Name = "AMT Stand-Down Hour (local)", Description = "Chart-local hour at/after which tags read 'late – stand down' (last hour of RTH). Default 15.0 = 3 PM when the chart runs in Central Time", Order = 12, GroupName = "3B. Level Roles")]
        public double AmtStandDownHour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Flip Lines", Description = "When a level flips (price holds beyond it for Role Accept Seconds), draw a horizontal line at the level: green = flipped to support, red = flipped to resistance. Live session only", Order = 13, GroupName = "3B. Level Roles")]
        public bool ShowFlipLines { get; set; }

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
        [Display(Name = "RTH Close Color", Description = "Extension line color for prior-session RTH close (RTHC) levels", Order = 111, GroupName = "4. Colors")]
        public System.Windows.Media.Brush RTHCloseColor { get; set; }
        [Browsable(false)]
        public string RTHCloseColorSerialize
        { get { return Serialize.BrushToString(RTHCloseColor); } set { RTHCloseColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Text / Stats Color", Description = "Color for general text: stats panel, session labels", Order = 12, GroupName = "4. Colors")]
        public System.Windows.Media.Brush TextColor { get; set; }
        [Browsable(false)]
        public string TextColorSerialize
        { get { return Serialize.BrushToString(TextColor); } set { TextColor = Serialize.StringToBrush(value); } }

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

        // ── VVAL ───────────────────────────────────────────────────────────────────
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "VVAL Color", Description = "Line color for the 1σ Value Area Low boundary", Order = 4, GroupName = "5. VA Level Styles")]
        public System.Windows.Media.Brush VVALLineColor { get; set; }
        [Browsable(false)]
        public string VVALLineColorSerialize
        { get { return Serialize.BrushToString(VVALLineColor); } set { VVALLineColor = Serialize.StringToBrush(value); } }

        #endregion

        #region Properties — TPO Settings

        [NinjaScriptProperty]
        [Range(5, 120)]
        [Display(Name = "TPO Period (Minutes)", Description = "Duration of each TPO time period. Use 30 for standard Market Profile.", Order = 1, GroupName = "7. TPO Settings")]
        public int TPOPeriodMinutes { get; set; }

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
                 Description = "Show or hide the current session TPO POC, VAH and VAL lines. Prior session within-session lines and all naked extension lines are not affected.",
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
                Name                     = "AntonsonProfile";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DisplayInDataBox         = true;
                DrawOnPricePanel         = true;
                IsSuspendedWhileInactive = false;

                // RTH sub-period times (ES / NQ defaults — set to match your chart template)
                RTHStartTime = new TimeSpan(8, 30, 0);
                RTHEndTime   = new TimeSpan(16, 0, 0);
                RthMode      = AntonsonRthMode.Auto;
                MergeTemplateSessions = false;

                // Profile settings
                ProfilePeriod         = AntonsonProfilePeriod.Session;
                TicksPerRow           = 1;   // 1 tick per row so buy/sell numbers match SuperDOM ladder price levels
                MaxSessionsToShow     = 20;

                // Display defaults
                ShowVPOC               = true;
                ShowVolumeValueArea    = true;
                ShowNakedVPOC          = true;
                ShowPriorSessionVPOC   = true;
                ShowNakedVAHVAL        = true;

                ShowPriorSessionLevels = true;
                ShowRTHCloseLevels     = true;
                ShowVolumeLabels       = true;
                VolumeFontSize         = 8;

                VADisplayStyle         = AntonsonDisplayStyle.Box;
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
                LevelRoleSupportColor        = System.Windows.Media.Brushes.LimeGreen;
                LevelRoleResistanceColor     = System.Windows.Media.Brushes.Red;
                LevelRoleBreakingColor       = System.Windows.Media.Brushes.Gold;
                ShowAmtReadout               = true;    // append the AMT checklist verdict to each role tag (§11.6)
                AmtHideHeldCount             = true;    // held xN does not predict travel — show the role label only
                AmtAcceptThreshold           = 0.33;    // volume at level < 33% of today's POC row = rejection (go)
                AmtMinProfileVolume          = 250000;  // today's profile too thin to read — trust the role
                AmtStandDownHour             = 15.0;    // chart-local: last hour of RTH → stand down
                ShowFlipLines                = true;    // horizontal line at the level when a flip confirms (180s held)
                HvnLvnOnlyMode         = false;  // off by default — shows all node types (HVN + MVN + LVN)
                VolumeBarDisplayMode   = AntonsonBarDisplayMode.Volume;  // default to standard volume bars

                TradeClassification    = AntonsonTradeClassification.Midpoint;  // same rule as GemsTraderLadder
                LabelFontSize          = 9;

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

                NakedVPOCColor    = System.Windows.Media.Brushes.Yellow;
                NakedVAColor      = System.Windows.Media.Brushes.DarkRed;     // prior/naked VVAL
                NakedVAHColor     = System.Windows.Media.Brushes.Green;       // prior/naked VVAH
                PriorHighLowColor = System.Windows.Media.Brushes.Gray;
                RTHCloseColor     = System.Windows.Media.Brushes.Orange;
                TextColor         = System.Windows.Media.Brushes.White;

                // Per-level VA line styles — defaults mirror the existing band colors so
                // existing charts look identical until the user changes individual levels.
                VVAHLineColor = System.Windows.Media.Brushes.MediumOrchid;

                VVALLineColor = System.Windows.Media.Brushes.MediumOrchid;

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

                // Alert defaults

                // TPO Settings defaults
                TPOPeriodMinutes     = 30;

                ShowTPOLetters       = true;
                ShowTPOPOC           = true;
                ShowTPOVAHVAL        = true;

                ShowNakedTPOPOC      = true;
                ShowNakedTPOVAHVAL   = true;
                ShowPriorSessionTPOPOC   = true;
                ShowPriorSessionTPOVAHVAL = true;

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
                cachedTickSize           = ts;
                rowSize                  = ts * Math.Max(1, TicksPerRow);
                sessions                 = new List<SessionProfile>();
                lock (levelArchiveLock) { levelArchive.Clear(); }   // LevelAccessor
                lastBarTime              = DateTime.MinValue;
                currentTPOPeriod         = null;

                // SessionIterator reads session boundaries directly from the chart's
                // trading hours template — no hardcoded ETH start time needed.
                sessionIterator = new SessionIterator(Bars);
                tradingDayFirstBegin.Clear();
                templateDayBegin = templateDayEnd = lastKeyBegin = lastKeyEnd = DateTime.MinValue;
                hasCachedSessionKey = false;
                histRoleTickCounter = 0;
                histCalcBarCounter  = 0;
                RefreshFeatureGates();

                Print("AntonsonProfile loaded [S/R per-session freeze v3 + shared touch/tick-grid POC v4] — period: " + ProfilePeriod + "  row size: " + rowSize.ToString("F4")
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

            // --- Volume accumulation (TryGetValue + incremental VPOC / total) ---
            long curVol;
            currentSession.VolumeAtPrice.TryGetValue(priceLevel, out curVol);
            curVol += tickVolume;
            currentSession.VolumeAtPrice[priceLevel] = curVol;
            currentSession.TotalVolume += tickVolume;
            if (curVol > currentSession.MaxVolumeAtVpoc)
            {
                currentSession.MaxVolumeAtVpoc = curVol;
                currentSession.VPOCLevel = priceLevel;
            }

            // --- Per-price bid/ask volume (only when a display/feature needs it) ---
            long upVol   = 0;
            long downVol = 0;

            if (gateNeedBidAsk)
            {
                double tickBid = BarsArray[1].GetBid(CurrentBars[1]);
                double tickAsk = BarsArray[1].GetAsk(CurrentBars[1]);
                bool   hasQuotes = tickBid > 0 && tickAsk > 0 && tickAsk >= tickBid;

                if (TradeClassification == AntonsonTradeClassification.Midpoint && hasQuotes)
                {
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
                    if (hasQuotes)
                    {
                        if (tickPrice >= tickAsk)
                            upVol = tickVolume;
                        else if (tickPrice <= tickBid)
                            downVol = tickVolume;
                    }

                    if (upVol == 0 && downVol == 0)
                    {
                        if (CurrentBars[1] >= 1)
                        {
                            double prevClose = Closes[1][1];
                            if (tickPrice > prevClose)      lastTickDirectionSign =  1;
                            else if (tickPrice < prevClose) lastTickDirectionSign = -1;
                        }
                        int dir = lastTickDirectionSign == 0 ? 1 : lastTickDirectionSign;
                        if (dir > 0) upVol   = tickVolume;
                        else         downVol = tickVolume;
                    }
                }

                if (upVol > 0)
                {
                    long bv;
                    currentSession.BuyVolumeAtPrice.TryGetValue(priceLevel, out bv);
                    currentSession.BuyVolumeAtPrice[priceLevel] = bv + upVol;
                }
                if (downVol > 0)
                {
                    long sv;
                    currentSession.SellVolumeAtPrice.TryGetValue(priceLevel, out sv);
                    currentSession.SellVolumeAtPrice[priceLevel] = sv + downVol;
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
            // RTH close/high/low always use the configured RTH Start/End clock window only, so
            // an overnight template session (outside that window) can never produce an RTHC level.
            bool inRthClockWindow = IsInRthClockWindow(tickTime);
            if (inRthClockWindow)
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
            if (gateAnyNaked)
                CheckNakedLevelTests(tickPrice, tickTime);

            // --- Live support/resistance role tracking ---
            // Realtime: every tick. Historical: every HistRoleEveryNTicks ticks (final pass on session close).
            if (gateNeedRoles)
            {
                if (State != State.Historical)
                {
                    UpdateLevelRoles(tickPrice, tickTime);
                }
                else
                {
                    histRoleTickCounter++;
                    if (histRoleTickCounter >= HistRoleEveryNTicks)
                    {
                        histRoleTickCounter = 0;
                        UpdateLevelRoles(tickPrice, tickTime);
                    }
                }
            }

            // Both consumers above have now seen this tick; it becomes the previous print.
            touchPrevPrice = tickPrice;

            // --- TPO letter construction (only when any TPO feature is enabled) ---
            if (gateAnyTpo)
            {
                if (currentTPOPeriod == null || tickTime >= currentTPOPeriod.EndTime)
                    StartNewTPOPeriod(tickTime);

                if (currentTPOPeriod != null)
                {
                    currentTPOPeriod.PriceLevelsTouched.Add(priceLevel);

                    HashSet<char> letters;
                    if (!currentSession.TPOsAtPrice.TryGetValue(priceLevel, out letters))
                    {
                        letters = new HashSet<char>();
                        currentSession.TPOsAtPrice[priceLevel] = letters;
                    }
                    letters.Add(currentTPOPeriod.Letter);

                }
            }
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

                // Recalculate VPOC / VA / TPO levels.
                // Realtime: every primary bar. Historical: every HistCalcEveryNBars bars
                // (session close always forces a full calc in StartNewSession).
                bool doCalc = (State != State.Historical);
                if (!doCalc)
                {
                    histCalcBarCounter++;
                    if (histCalcBarCounter >= HistCalcEveryNBars)
                    {
                        histCalcBarCounter = 0;
                        doCalc = true;
                    }
                }
                if (doCalc)
                {
                    CalculateVPOCandVA(currentSession);
                    if (gateAnyTpo)
                        CalculateTPOPOCandVA(currentSession);
                }
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
            // Fast path: tick still inside the previously resolved session window
            if (hasCachedSessionKey && t >= cachedKeyWindowBegin && t < cachedKeyWindowEnd)
            {
                lastKeyBegin = cachedKeyWindowBegin;
                lastKeyEnd   = cachedKeyWindowEnd;
                return cachedSessionKey;
            }

            sessionIterator.GetNextSession(t, true);
            DateTime sessionBegin = sessionIterator.ActualSessionBegin;
            lastKeyBegin = sessionBegin;
            lastKeyEnd   = sessionIterator.ActualSessionEnd;

            DateTime key;
            if (ProfilePeriod == AntonsonProfilePeriod.Weekly)
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
                key = d.AddDays(-offset);
            }
            else if (MergeTemplateSessions)
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
                key = firstBegin;
            }
            else
            {
                key = sessionBegin;
            }

            cachedSessionKey     = key;
            cachedKeyWindowBegin = lastKeyBegin;
            cachedKeyWindowEnd   = lastKeyEnd;
            hasCachedSessionKey  = true;
            return key;
        }

        /// <summary>
        /// True when <paramref name="t"/> counts as RTH. Manual: inside RTH Start / End. Full Session: always
        /// (every tick of a template session is RTH — day-only markets such as HE / LE).
        /// </summary>
        private bool UseFullSessionRth()
        {
            if (RthMode == AntonsonRthMode.FullSession) return true;
            if (RthMode == AntonsonRthMode.Manual) return false;
            // Auto: the template session opens inside the RTH window => there is no overnight / pre-RTH part.
            if (templateDayBegin == DateTime.MinValue) return false;
            TimeSpan b = templateDayBegin.TimeOfDay;
            return b >= RTHStartTime && b < RTHEndTime;
        }

        private bool IsRthAt(DateTime t)
        {
            if (UseFullSessionRth()) return true;
            return IsInRthClockWindow(t);
        }

        /// <summary>
        /// Strict RTH Start/End clock check — ignores FullSession / Auto overrides.
        /// Used for RTHClose accumulation.
        /// </summary>
        private bool IsInRthClockWindow(DateTime t)
        {
            TimeSpan tod = t.TimeOfDay;
            return tod >= RTHStartTime && tod < RTHEndTime;
        }

        /// <summary>
        /// True when this session's RTHClose is the real end-of-RTH print, not the overnight
        /// session's last price. Split templates hand the overnight session a final bar whose
        /// timestamp can sit on the RTH open boundary; that print must not become RTHC.
        ///
        /// Keep RTHClose when:
        ///   - the session opened inside the RTH window (day / RTH session), or
        ///   - the session opened outside RTH but ran past RTH open and did not end at/before
        ///     RTH open (combined Globex+RTH template that closes at the cash close).
        /// Clear when the session opened outside RTH and ended at or before RTH open
        /// (pure overnight segment).
        /// </summary>
        private bool HasValidRthClose(SessionProfile s)
        {
            if (s == null || s.RTHClose <= 0) return false;
            if (s.StartTime == DateTime.MinValue) return false;

            TimeSpan startTod = s.StartTime.TimeOfDay;
            bool openedInRth = startTod >= RTHStartTime && startTod < RTHEndTime;
            if (openedInRth) return true;

            // Combined Globex+RTH session: opens overnight, ends at/after the cash session.
            // Pure overnight segment: opens overnight, ends at or before RTH open.
            DateTime end = s.EndTime != DateTime.MinValue ? s.EndTime : s.StartTime;
            TimeSpan endTod = end.TimeOfDay;

            // Ended at or before RTH open the next morning → overnight only → not a valid RTHC
            if (endTod <= RTHStartTime) return false;

            // Ended after RTH open — only keep if it also reached the RTH close area
            // (combined session that includes the cash close), not a morning-only fragment.
            TimeSpan nearRthEnd = RTHEndTime > TimeSpan.FromMinutes(5)
                ? RTHEndTime - TimeSpan.FromMinutes(5)
                : RTHEndTime;
            return endTod >= nearRthEnd;
        }

        private void FinalizeRthClose(SessionProfile s)
        {
            if (s == null) return;
            if (!HasValidRthClose(s))
                s.RTHClose = 0;
        }

        private void StartNewSession(DateTime barTime, DateTime sessionDate)
        {
            // Capture the closing session before resetting currentSession
            SessionProfile priorSession = null;

            // The session that is ending: freeze its S/R states at their last calculation
            FreezeRoleStates(currentSession);

            // No trade-through across the gap between sessions
            touchPrevPrice = double.NaN;

            // Finalize the closing session
            if (currentSession != null)
            {
                // Force full profile calc so throttled historical bars still end with correct levels
                CalculateVPOCandVA(currentSession);
                if (gateAnyTpo)
                    CalculateTPOPOCandVA(currentSession);

                // Historical role throttle: one last pass so tags match a full per-tick run
                if (gateNeedRoles && State == State.Historical)
                    UpdateLevelRoles(
                        currentSession.ClosePrice > 0 ? currentSession.ClosePrice : lastTickPrice,
                        currentSession.EndTime > DateTime.MinValue ? currentSession.EndTime : barTime);

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

                // Strip overnight-session closes that were mis-tagged as RTHClose
                // (boundary bar at RTH open on a split overnight template session).
                FinalizeRthClose(currentSession);

                priorSession = currentSession;
                mostRecentCompletedSession = currentSession;
            }

            currentSession   = new SessionProfile
            {
                SessionDate = sessionDate,
                StartTime   = barTime
            };
            currentTPOPeriod = null;

            histRoleTickCounter = 0;
            histCalcBarCounter  = 0;

            // Session key window may still be valid, but force refresh next tick after boundary
            hasCachedSessionKey = false;

            sessions.Add(currentSession);

            // Trim to MaxSessionsToShow
            while (sessions.Count > MaxSessionsToShow)
                sessions.RemoveAt(0);

            ArchiveLevelSession(currentSession);   // LevelAccessor

            RefreshFeatureGates();
        }

        private void StartNewTPOPeriod(DateTime tickTime)
        {
            if (currentSession == null) return;

            // Cumulative TPO: letter index is sequential across the whole session.
            int periodCount = currentSession.PeriodCount;
            currentSession.PeriodCount++;

            int alphaIndex = periodCount % 52;
            char letter    = alphaIndex < 26
                ? (char)('A' + alphaIndex)
                : (char)('a' + (alphaIndex - 26));

            DateTime periodEnd = tickTime.AddMinutes(TPOPeriodMinutes);

            currentTPOPeriod = new TPOPeriod
            {
                Letter    = letter,
                StartTime = tickTime,
                EndTime   = periodEnd
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

            // VPOC is maintained incrementally on each tick. Re-scan only if it was never set
            // (e.g. session loaded without tick path) so behaviour stays identical.
            if (session.VPOCLevel == 0 || session.MaxVolumeAtVpoc <= 0)
            {
                long maxVolume = 0;
                foreach (var kvp in session.VolumeAtPrice)
                {
                    if (kvp.Value > maxVolume)
                    {
                        maxVolume              = kvp.Value;
                        session.VPOCLevel      = kvp.Key;
                        session.MaxVolumeAtVpoc = maxVolume;
                    }
                }
                if (session.TotalVolume <= 0)
                {
                    long sum = 0;
                    foreach (var v in session.VolumeAtPrice.Values)
                        sum += v;
                    session.TotalVolume = sum;
                }
            }

            // --- Volume value area (1σ) ---
            CalculateVolumeValueArea(session);

            // --- Halfback from session high/low levels already tracked on ticks ---
            if (session.HighLevel != 0 || session.LowLevel != 0
                || session.SessionHigh != double.MinValue)
            {
                int hi = session.HighLevel;
                int lo = session.LowLevel;
                if (hi == 0 && lo == 0 && session.VolumeAtPrice.Count > 0)
                {
                    hi = session.VolumeAtPrice.Keys.Max();
                    lo = session.VolumeAtPrice.Keys.Min();
                }
                session.Halfback = ((hi + lo) / 2.0) * rowSize;
            }
        }

        /// <summary>
        /// Calculates the 1σ volume value area (68 % of session volume → VVAH / VVAL):
        /// the band expands outward from the VPOC — at each step the higher-volume
        /// neighbour is absorbed until 68 % of total session volume is enclosed.
        /// </summary>
        private void CalculateVolumeValueArea(SessionProfile session)
        {
            if (session.VolumeAtPrice.Count == 0 || session.VPOCLevel == 0) return;

            long totalVolume = session.TotalVolume;
            if (totalVolume <= 0)
            {
                totalVolume = 0;
                foreach (var v in session.VolumeAtPrice.Values)
                    totalVolume += v;
                session.TotalVolume = totalVolume;
            }

            // 1σ — 68.27% (one standard deviation)
            int vvah, vval;
            ComputeValueAreaBand(session, totalVolume, 0.68, out vvah, out vval);
            session.VVAH = vvah;
            session.VVAL = vval;

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
            long currentVolume;
            session.VolumeAtPrice.TryGetValue(session.VPOCLevel, out currentVolume);

            while (currentVolume < targetVolume)
            {
                long volAbove, volBelow;
                session.VolumeAtPrice.TryGetValue(bandHigh + 1, out volAbove);
                session.VolumeAtPrice.TryGetValue(bandLow - 1, out volBelow);

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
        /// Computes POC + 1σ TPO value-area levels from a TPO letter map.
        /// Returns false if the map is empty.
        /// </summary>
        private bool ComputeTPOLevelsFromMap(Dictionary<int, HashSet<char>> map,
            out int poc, out int vah, out int val)
        {
            poc = vah = val = 0;
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

            return true;
        }

        private void CalculateTPOPOCandVA(SessionProfile session)
        {
            int poc, vah, val;
            if (!ComputeTPOLevelsFromMap(session.TPOsAtPrice,
                    out poc, out vah, out val))
                return;

            session.POCLevel = poc;
            session.TPOVAH   = vah;
            session.TPOVAL   = val;
            session.VAM      = (vah + val) / 2;

            AnalyzePoorHighsLows(session);
            AnalyzeExcess(session);
            ComputeSinglePrintZones(session);
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

        // Previous tick price, shared by naked-level termination and the role classifier so both
        // see the same tick-to-tick movement. Reset (NaN) at every session start so a gap between
        // sessions is never treated as price trading through the levels in between.
        private double touchPrevPrice = double.NaN;

        private double TouchTickSize() { return rowSize / Math.Max(1, TicksPerRow); }
        private double TouchEps()      { return TouchTickSize() * 1e-6; }

        /// <summary>
        /// Half-width of the touch band around a level's drawn price. Tolerance 0 means only a
        /// print at the exact price counts (a half-tick band); no clamp raises it back to 1.
        /// </summary>
        private double TouchBand()
        {
            int ticks = LevelRoleTouchToleranceTicks < 0 ? 0 : LevelRoleTouchToleranceTicks;
            return (ticks == 0 ? 0.5 : (double)ticks) * TouchTickSize();
        }

        /// <summary>The tick price is inside the band around the level's drawn price.</summary>
        private bool LevelInBand(double level, double price, double band)
        {
            return Math.Abs(price - level) <= band + TouchEps();
        }

        /// <summary>
        /// Price jumped from one side of the band to the other between two consecutive prints
        /// (no print landed inside it), i.e. the level was traded through.
        /// </summary>
        private bool LevelCrossed(double level, double prev, double price, double band)
        {
            if (double.IsNaN(prev)) return false;
            double eps = TouchEps();
            double dp  = prev  - level;
            double d   = price - level;
            if (Math.Abs(dp) <= band + eps || Math.Abs(d) <= band + eps) return false;
            return (dp > 0) != (d > 0);
        }

        /// <summary>The single touch definition: a print in the band, or a trade-through.</summary>
        private bool LevelTouched(double level, double prev, double price, double band)
        {
            return LevelInBand(level, price, band) || LevelCrossed(level, prev, price, band);
        }

        private void NakedTouch(double levelPrice, ref bool tested, ref DateTime breakAt,
            double price, double prev, double band, DateTime now)
        {
            if (tested) return;
            if (LevelTouched(levelPrice, prev, price, band))
            {
                tested  = true;
                breakAt = now;
            }
        }

        /// <summary>
        /// Price a POC line is drawn at and tested against: the profile row's centre snapped to the
        /// nearest tradable tick (an exact tie, possible with an odd Ticks Per Row, takes the lower
        /// tick, i.e. the middle tick of the row). Every consumer — lines, zones, labels, trackers,
        /// touch tests, the level accessor — uses this one value.
        /// </summary>
        private double LevelCenterPrice(int level)
        {
            // Uses only cached values – safe to call from OnRender.
            double ts = TouchTickSize();   // derived from rowSize, never live TickSize
            if (ts <= 0) return level * rowSize + rowSize * 0.5;
            double lo  = level * rowSize;
            double mid = (TicksPerRow > 1) ? lo + rowSize * 0.5 : lo;
            return Math.Round(mid / ts) * ts;
        }

        /// <summary>
        /// Checks whether the current tick price touches any prior-session naked level.
        /// Once touched the level is flagged as tested and the break time is recorded
        /// so the extension line terminates at that bar.
        /// </summary>
        private void CheckNakedLevelTests(double price, DateTime breakTime)
        {
            if (sessions.Count < 2) return;

            double band = TouchBand();
            double prev = touchPrevPrice;

            // Only iterate prior (completed) sessions
            for (int i = 0; i < sessions.Count - 1; i++)
            {
                var s = sessions[i];
                if (s.VolumeAtPrice.Count == 0) continue;

                // Skip sessions where every level we care about is already tested.
                // Session data and history are untouched — only the per-tick work is reduced.
                if (SessionFullyTested(s)) continue;

                // Each level is tested at the exact price it is drawn at, using the same touch
                // band and trade-through rule as the role classifier (see LevelTouched).
                if (!s.VPOCTested && s.VPOCLevel > 0 && (ShowNakedVPOC || ShowPriorSessionVPOC))
                    NakedTouch(LevelCenterPrice(s.VPOCLevel),  ref s.VPOCTested,  ref s.VPOCBreakTime,  price, prev, band, breakTime);
                if (!s.VVAHTested && s.VVAH > 0 && ShowNakedVAHVAL)
                    NakedTouch(s.VVAH * rowSize + rowSize,     ref s.VVAHTested,  ref s.VVAHBreakTime,  price, prev, band, breakTime);
                if (!s.VVALTested && s.VVAL > 0 && ShowNakedVAHVAL)
                    NakedTouch(s.VVAL * rowSize,               ref s.VVALTested,  ref s.VVALBreakTime,  price, prev, band, breakTime);
                if (!s.HighTested && s.HighLevel > 0 && ShowPriorSessionLevels)
                    NakedTouch(s.SessionHigh,                  ref s.HighTested,  ref s.HighBreakTime,  price, prev, band, breakTime);
                if (!s.LowTested && s.LowLevel > 0 && ShowPriorSessionLevels)
                    NakedTouch(s.SessionLow,                   ref s.LowTested,   ref s.LowBreakTime,   price, prev, band, breakTime);

                // RTH Close (RTHC): only tested during the RTH clock window. Overnight prints never terminate it.
                if (!s.RTHCloseTested && ShowRTHCloseLevels && HasValidRthClose(s) && IsInRthClockWindow(breakTime))
                    NakedTouch(s.RTHClose,                     ref s.RTHCloseTested, ref s.RTHCloseBreakTime, price, prev, band, breakTime);

                // === TPO naked level tests ===
                if (!s.TPOPOCTested && s.POCLevel > 0 && (ShowNakedTPOPOC || ShowPriorSessionTPOPOC))
                    NakedTouch(LevelCenterPrice(s.POCLevel),   ref s.TPOPOCTested,  ref s.TPOPOCBreakTime,  price, prev, band, breakTime);
                if (!s.TPOVAHTested && s.TPOVAH > 0 && (ShowNakedTPOVAHVAL || ShowPriorSessionTPOVAHVAL))
                    NakedTouch(s.TPOVAH * rowSize + rowSize,   ref s.TPOVAHTested,  ref s.TPOVAHBreakTime,  price, prev, band, breakTime);
                if (!s.TPOVALTested && s.TPOVAL > 0 && (ShowNakedTPOVAHVAL || ShowPriorSessionTPOVAHVAL))
                    NakedTouch(s.TPOVAL * rowSize,             ref s.TPOVALTested,  ref s.TPOVALBreakTime,  price, prev, band, breakTime);

                // Single print zone testing
                if (ShowNakedSinglePrints && s.SinglePrintZones != null && s.SinglePrintZones.Count > 0)
                {
                    int priceLevel = LevelOf(price);
                    foreach (var zone in s.SinglePrintZones)
                    {
                        if (zone.IsFullyTested) continue;
                        zone.UntestedLevels.Remove(priceLevel);
                        if (zone.IsFullyTested) zone.BreakTime = breakTime;
                    }
                }
            }
        }

        /// <summary>
        /// True when every naked/prior level this session still cares about (given current Show* flags)
        /// has already been tested. History remains intact; this only skips further per-tick checks.
        /// </summary>
        private bool SessionFullyTested(SessionProfile s)
        {
            if ((ShowNakedVPOC || ShowPriorSessionVPOC) && s.VPOCLevel > 0 && !s.VPOCTested) return false;
            if (ShowNakedVAHVAL && s.VVAH > 0 && !s.VVAHTested) return false;
            if (ShowNakedVAHVAL && s.VVAL > 0 && !s.VVALTested) return false;
            if (ShowPriorSessionLevels && s.HighLevel > 0 && !s.HighTested) return false;
            if (ShowPriorSessionLevels && s.LowLevel > 0 && !s.LowTested) return false;
            if (ShowRTHCloseLevels && HasValidRthClose(s) && !s.RTHCloseTested) return false;
            if ((ShowNakedTPOPOC || ShowPriorSessionTPOPOC) && s.POCLevel > 0 && !s.TPOPOCTested) return false;
            if ((ShowNakedTPOVAHVAL || ShowPriorSessionTPOVAHVAL) && s.TPOVAH > 0 && !s.TPOVAHTested) return false;
            if ((ShowNakedTPOVAHVAL || ShowPriorSessionTPOVAHVAL) && s.TPOVAL > 0 && !s.TPOVALTested) return false;
            if (ShowNakedSinglePrints && s.SinglePrintZones != null)
            {
                for (int z = 0; z < s.SinglePrintZones.Count; z++)
                    if (!s.SinglePrintZones[z].IsFullyTested) return false;
            }
            return true;
        }

        #endregion

        /// <summary>
        /// Cache which heavy paths are active so the tick loop only does a few bool reads.
        /// Call after property defaults / DataLoaded and whenever sessions reset.
        /// </summary>
        private void RefreshFeatureGates()
        {
            gateNeedBidAsk =
                VolumeBarDisplayMode != AntonsonBarDisplayMode.Volume;

            gateAnyTpo =
                ShowTPOLetters || ShowTPOPOC || ShowTPOVAHVAL
                || ShowNakedTPOPOC || ShowNakedTPOVAHVAL || ShowPriorSessionTPOPOC
                || ShowPriorSessionTPOVAHVAL
                || ShowSinglePrints || ShowNakedSinglePrints || ShowPoorHighsLows                || ShowExcess;

            gateAnyNaked =
                ShowNakedVPOC || ShowPriorSessionVPOC || ShowNakedVAHVAL || ShowPriorSessionLevels
                || ShowRTHCloseLevels
                || ShowNakedTPOPOC || ShowPriorSessionTPOPOC || ShowNakedTPOVAHVAL || ShowPriorSessionTPOVAHVAL
                || ShowNakedSinglePrints;

            gateNeedRoles = ShowLevelRoles;
        }

        #region Level Roles (live support/resistance state machine)

        private enum LRState
        {
            Untested,   // no tick has reached the touch-tolerance band this session
            Testing,    // price is inside the touch-tolerance band
            Held,       // a test was completed and the level rejected it (held)
            Breaking,   // price is beyond the level and the acceptance timer is running
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
            public int      BreakDir;     // direction of the running break attempt: +1 up, -1 down, 0 = none
            public int      Flips;        // completed flips in this view (1 = first flip: the Rule-A trigger)
            public DateTime BreakStart;   // tick-time the current run beyond the level began
            public DateTime LastTick;     // tick-time of the last update; a finished session's state never updates again
            public bool     Frozen;       // set when the session this state belongs to ends; a frozen state is never updated again

            // AMT readout (level-role study §11.6): how the CURRENT session's developing
            // volume profile is treating this established level.
            public double   AcceptRatio;  // volume today at the level's price bar / volume today at the POC bar
            public int      VaPos;        // +1 level above today's developing value area, 0 inside, -1 below
            public string   AmtVerdict;   // appended to the role tag (SHORT-GO / SHORT-NO / LONG-GO / no-go)
        }

        private class RoleTracker
        {
            public LevelRoleState State;
            public double         Level;
            public string         Key;         // full level key, for flip-line dedup
            public string         TypeText;    // HI/LO/RTHC/VPOC/VVAH/VVAL/TPOPOC/TPOVAH/TPOVAL
            public bool           IsNaked;     // source session is older than the most recent completed one
            public bool           IsMostRecent;// source session is the previous (most recent completed) session
        }

        private class FlipLineRecord
        {
            public double   Price;   // the level's price
            public DateTime Time;    // tick-time the flip confirmed (price held beyond it for Role Accept Seconds)
            public int      Side;    // +1 flipped to support, -1 flipped to resistance
        }

        private string RoleKey(SessionProfile s, string type)
        { return s.StartTime.Ticks.ToString() + "|" + type; }

        // S/R state is kept per (level, session it is running in). Each session gets its own fresh
        // state for every level; once that session ends nothing updates it again, so it stays
        // frozen at its last calculation.
        private string RoleViewKey(string levelKey, SessionProfile view)
        { return levelKey + "@" + view.SessionDate.Ticks.ToString(); }

        /// <summary>Hard-freezes every S/R state that belongs to a session that has just ended.</summary>
        private void FreezeRoleStates(SessionProfile ended)
        {
            if (ended == null) return;
            string suffix = "@" + ended.SessionDate.Ticks.ToString();
            foreach (KeyValuePair<string, LevelRoleState> kv in levelRoleStates)
                if (kv.Value != null && kv.Key.EndsWith(suffix, StringComparison.Ordinal))
                {
                    kv.Value.Frozen = true;
                    kv.Value.AmtVerdict = null;   // a finished session's AMT readout is stale: keep frozen tags role-only
                }
        }

        /// <summary>
        /// Drives the per-level support/resistance state machine from the tick stream.
        /// Roles are positional (price above a level = support, below = resistance) and
        /// are confirmed by tick interactions: tests in the tolerance band, completed
        /// rejections (held) and breaks that hold beyond the level for
        /// LevelRoleAcceptSeconds (flip). All timing uses tick timestamps, so behavior is
        /// identical on any chart bar size and rebuilds during the historical replay.
        /// Per-tick execution is allocation-free: tracked levels are cached and the
        /// cache is rebuilt only when the session set or visibility toggles change.
        /// </summary>
        private void UpdateLevelRoles(double tickPrice, DateTime tickTime)
        {
            if (currentSession == null || sessions == null || sessions.Count < 2) return;

            roleLastTickTime = tickTime;

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
                && roleTrackerPriorTPOVA == ShowPriorSessionTPOVAHVAL
                && roleTrackerRTHClose == ShowRTHCloseLevels;

            if (!cacheValid)
                RebuildRoleTrackers();

            double band = TouchBand();

            roleLastTickPrice = tickPrice;

            for (int i = 0; i < roleTrackers.Count; i++)
            {
                RoleTracker t = roleTrackers[i];
                if (t.State.Frozen) continue;   // its session has ended: keep the last calculation
                t.State.LastTick = tickTime;
                int flipsBefore = t.State.Flips;
                RunLevelRoleMachine(t.State, t.Level, tickPrice, touchPrevPrice, tickTime, band);
                if (t.State.Flips != flipsBefore)
                    RecordFlipLine(t, tickPrice, tickTime);
                if (ShowAmtReadout)
                    ComputeAmtReadout(t, tickTime);
            }
        }

        /// <summary>
        /// A level just flipped (price held beyond it for LevelRoleAcceptSeconds): remember
        /// a horizontal line at the level for the live session, deduped per (level, flip, time).
        /// </summary>
        private void RecordFlipLine(RoleTracker t, double tickPrice, DateTime flipTime)
        {
            string key = t.Key + "#" + t.State.Flips.ToString() + "#" + flipTime.Ticks.ToString();
            lock (flipLinesLock)
            {
                if (!flipLineKeys.Add(key)) return;
                FlipLineRecord rec = new FlipLineRecord();
                rec.Price = t.Level;
                rec.Time  = flipTime;
                rec.Side  = t.State.Role;
                flipLines.Add(rec);
            }
        }

        /// <summary>
        /// AMT readout for one established level (level-role study §11.6): measures how the
        /// CURRENT session's developing volume profile is treating the level. Rejection
        /// (today's volume at the level's price row well below today's POC row) at a level
        /// above today's developing value area is the strongest short cell in the 44-session
        /// study; acceptance there is the strongest no-go. Long side only fires the Rule-A
        /// exception: first flip of a naked VPOC/TPOPOC on a day moving up from the open.
        /// </summary>
        private void ComputeAmtReadout(RoleTracker t, DateTime tickTime)
        {
            LevelRoleState st = t.State;

            if (currentSession == null || currentSession.VolumeAtPrice == null
                || currentSession.VolumeAtPrice.Count == 0)
            { st.AmtVerdict = ""; return; }

            if (tickTime.TimeOfDay >= TimeSpan.FromHours(AmtStandDownHour))
            { st.AmtVerdict = " | late - stand down"; return; }

            long pocVol = 0;
            currentSession.VolumeAtPrice.TryGetValue(currentSession.VPOCLevel, out pocVol);
            if (pocVol > 0 && currentSession.TotalVolume < AmtMinProfileVolume)
            { st.AmtVerdict = " | ~thin profile (trust the role)"; return; }

            int idx = LevelOf(t.Level);   // same row-index the profile accumulates into
            long volAtIndex = 0;
            currentSession.VolumeAtPrice.TryGetValue(idx, out volAtIndex);
            st.AcceptRatio = volAtIndex / (double)Math.Max(pocVol, 1);
            st.VaPos = (currentSession.VVAH > 0 && currentSession.VVAL > 0)
                ? (idx > currentSession.VVAH ? 1 : (idx < currentSession.VVAL ? -1 : 0))
                : 0;

            string pos = st.VaPos > 0 ? "VAbv" : st.VaPos < 0 ? "VAblo" : "VAin";
            string acc = (int)Math.Round(st.AcceptRatio * 100.0) + "%";
            string touchTag = st.Touches <= 1 ? "t1" : (st.Touches == 2 ? "t2" : "t3+");

            if (st.Role < 0)
            {
                if (st.VaPos > 0)
                    st.AmtVerdict = st.AcceptRatio <= AmtAcceptThreshold
                        ? " | SHORT-GO " + pos + " acc" + acc + " " + touchTag
                        : " | SHORT-NO acc" + acc;
                else
                    st.AmtVerdict = " | short-a " + pos + " acc" + acc + " " + touchTag;
            }
            else if (st.Role > 0)
            {
                bool ruleA = st.State == LRState.Flipped && st.Flips == 1 && t.IsNaked
                             && (t.TypeText == "VPOC" || t.TypeText == "TPOPOC")
                             && roleLastTickPrice >= currentSession.OpenPrice;
                if (ruleA)
                    st.AmtVerdict = " | LONG-GO (RuleA) acc" + acc;
                else if (st.State == LRState.Flipped && st.Flips == 1
                         && (t.TypeText == "VPOC" || t.TypeText == "TPOPOC")
                         && roleLastTickPrice < currentSession.OpenPrice)
                    st.AmtVerdict = " | long-NO (down day) acc" + acc;
                else
                    st.AmtVerdict = " | long-a " + pos + " acc" + acc + " " + touchTag;
            }
            else
                st.AmtVerdict = "";
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
                        AddRoleTracker(RoleKey(s, "HI"), s.SessionHigh, isMostRecent);
                    if (s.LowLevel > 0)
                        AddRoleTracker(RoleKey(s, "LO"), s.SessionLow, isMostRecent);
                }

                if (ShowRTHCloseLevels && LevelRolesHiLo && HasValidRthClose(s))
                    AddRoleTracker(RoleKey(s, "RTHC"), s.RTHClose, isMostRecent);

                if ((isMostRecent ? ShowPriorSessionVPOC : ShowNakedVPOC) && s.VPOCLevel > 0)
                    AddRoleTracker(RoleKey(s, "VPOC"), LevelCenterPrice(s.VPOCLevel), isMostRecent);

                if (ShowNakedVAHVAL)
                {
                    if (s.VVAH > 0)
                        AddRoleTracker(RoleKey(s, "VVAH"), s.VVAH * rowSize + rowSize, isMostRecent);
                    if (s.VVAL > 0)
                        AddRoleTracker(RoleKey(s, "VVAL"), s.VVAL * rowSize, isMostRecent);
                }

                if ((isMostRecent ? ShowPriorSessionTPOPOC : ShowNakedTPOPOC) && s.POCLevel > 0)
                    AddRoleTracker(RoleKey(s, "TPOPOC"), LevelCenterPrice(s.POCLevel), isMostRecent);
                if (isMostRecent ? ShowPriorSessionTPOVAHVAL : ShowNakedTPOVAHVAL)
                {
                    if (s.TPOVAH > 0)
                        AddRoleTracker(RoleKey(s, "TPOVAH"), s.TPOVAH * rowSize + rowSize, isMostRecent);
                    if (s.TPOVAL > 0)
                        AddRoleTracker(RoleKey(s, "TPOVAL"), s.TPOVAL * rowSize, isMostRecent);
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
            roleTrackerRTHClose     = ShowRTHCloseLevels;
        }

        private void AddRoleTracker(string key, double level, bool isMostRecent)
        {
            string viewKey = RoleViewKey(key, currentSession);
            LevelRoleState st;
            if (!levelRoleStates.TryGetValue(viewKey, out st) || st == null)
            {
                st = new LevelRoleState();
                levelRoleStates[viewKey] = st;
            }

            RoleTracker t = new RoleTracker();
            t.State = st;
            t.Level = level;
            t.Key   = key;
            t.IsMostRecent = isMostRecent;
            t.IsNaked = !isMostRecent;              // study "naked" = source session older than the previous one
            int typeStart = key.IndexOf('|');
            t.TypeText = typeStart >= 0 && typeStart + 1 < key.Length
                ? key.Substring(typeStart + 1) : key;
            roleTrackers.Add(t);
        }

        /// <summary>A touch of the level: price entered the band (or traded through it).</summary>
        private void RoleBandEntry(LevelRoleState st)
        {
            // Disarms the acceptance clock (a break must be continuous) and registers the
            // approach of a new test.
            st.BreakDir = 0;
            if (st.State == LRState.Breaking)
                st.State = LRState.Testing;
            if (!st.InBand)
            {
                st.InBand        = true;
                st.PendingReject = true;
                st.Touches++;
            }
            if (st.State == LRState.Untested)
                st.State = LRState.Testing;
        }

        private void RunLevelRoleMachine(LevelRoleState st, double level, double tickPrice,
            double prevPrice, DateTime tickTime, double band)
        {
            double d = tickPrice - level;

            // Same touch definition as naked-level termination (LevelTouched).
            if (LevelInBand(level, tickPrice, band))
            {
                RoleBandEntry(st);
                return;
            }

            // Price jumped across the band between two prints: the level was traded through,
            // so register the touch before classifying the side price is now on.
            if (LevelCrossed(level, prevPrice, tickPrice, band))
                RoleBandEntry(st);

            st.InBand = false;
            int side = d > 0 ? 1 : -1;

            if (st.State == LRState.Flipped)
            {
                if (side != st.Role)
                {
                    // reclaim: the flip failed, the level is back in its original role
                    st.State         = LRState.Held;
                    st.Helds++;
                    st.Role          = side;
                    st.BreakDir      = 0;
                    st.PendingReject = false;
                }
                else if (st.PendingReject)
                {
                    // the retest of the flipped level was rejected
                    st.State         = LRState.Held;
                    st.Helds++;
                    st.PendingReject = false;
                }
            }
            else if (st.Role == 0)
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
                    // decisive return to the role side before acceptance = failed break
                    st.Helds++;
                    st.BreakDir      = 0;
                    st.PendingReject = false;
                }
                else if (st.PendingReject)
                {
                    // the band entry just left behind matured into a completed rejection
                    st.Helds++;
                    st.PendingReject = false;
                }
                st.State = LRState.Held;
            }
            else
            {
                // beyond the level on the opposite side of the confirmed role:
                // (re)start the acceptance clock when the run changes
                if (st.BreakDir != side)
                {
                    st.BreakDir   = side;
                    st.BreakStart = tickTime;
                }
                if ((tickTime - st.BreakStart).TotalSeconds >= LevelRoleAcceptSeconds)
                {
                    st.State         = LRState.Flipped;
                    st.Role          = side;
                    st.Flips++;
                    st.BreakDir      = 0;
                    st.PendingReject = false;
                }
                else
                    st.State = LRState.Breaking;
            }
        }

        /// <summary>
        /// Draws the live role tag under a prior-session or naked level line,
        /// anchored at the end of the level's own extension line (the right margin
        /// for live levels, the break bar for dead ones), mirroring how extension
        /// labels follow the line. Tags render for every tracked level whose line is
        /// drawn, regardless of which session the level was last broken in.
        /// </summary>
        private string RoleTagText(LevelRoleState st, out SharpDX.Direct2D1.Brush brush)
        {
            string roleWord = st.Role < 0 ? "Resistance" : "Support";
            string text;
            switch (st.State)
            {
                case LRState.Untested:
                    brush = roleUntestedBrush;
                    text = st.Role < 0 ? "Resistance (untested)"
                         : st.Role > 0 ? "Support (untested)" : "(untested)";
                    break;
                case LRState.Testing:
                    brush = roleBreakingBrush;
                    text = roleWord + " · testing";
                    break;
                case LRState.Held:
                    brush = st.Role < 0 ? roleResistanceBrush : roleSupportBrush;
                    text = (st.Helds > 0 && !AmtHideHeldCount)
                        ? roleWord + " · held x" + st.Helds.ToString()
                        : roleWord;
                    break;
                case LRState.Flipped:
                    brush = st.Role < 0 ? roleResistanceBrush : roleSupportBrush;
                    text = roleWord + " · flipped";
                    break;
                default: // Breaking
                    int elapsed = st.LastTick > st.BreakStart
                        ? (int)(st.LastTick - st.BreakStart).TotalSeconds : 0;
                    brush = roleBreakingBrush;
                    text = roleWord + " · BREAKING " + elapsed.ToString() + "s";
                    break;
            }
            return st.AmtVerdict != null && st.AmtVerdict.Length > 0
                ? text + st.AmtVerdict : text;
        }

        private void DrawLevelRoleTag(ChartScale chartScale, int sessionEndX, string key,
            double price, DateTime breakTime)
        {
            if (roleBreakingBrush == null || roleUntestedBrush == null) return;
            if (currentSession == null || sessions == null) return;

            int lineEndX = GetExtensionEndX(breakTime);
            if (lineEndX <= sessionEndX) return;

            float y = chartScale.GetYByValue(price);

            // The session whose S/R state the end-of-line tag shows: the session the line ends in
            // (the live session for an untested level or one broken in the live session).
            SessionProfile tagSession = currentSession;
            if (breakTime != DateTime.MinValue && breakTime < currentSession.StartTime)
            {
                for (int i = 0; i < sessions.Count; i++)
                {
                    SessionProfile v = sessions[i];
                    if (v == currentSession) continue;
                    if (breakTime >= v.StartTime && (v.EndTime == DateTime.MinValue || breakTime <= v.EndTime))
                    { tagSession = v; break; }
                }
            }

            // Frozen tags: for every finished session this line crossed, show where its S/R
            // calculation stood when that session ended.
            for (int i = 0; i < sessions.Count; i++)
            {
                SessionProfile v = sessions[i];
                if (v == currentSession || v == tagSession) continue;
                if (v.StartBarIndex < 0 || v.EndBarIndex < 0) continue;

                int vEndX = SafeGetXByBarIndex(v.EndBarIndex);
                if (vEndX <= sessionEndX || vEndX >= lineEndX) continue;
                if (ChartPanel != null && (vEndX < ChartPanel.X || vEndX > ChartPanel.X + ChartPanel.W)) continue;

                LevelRoleState fs;
                if (!levelRoleStates.TryGetValue(RoleViewKey(key, v), out fs) || fs == null) continue;
                if (fs.State == LRState.Untested) continue;   // nothing happened at this level in that session

                SharpDX.Direct2D1.Brush fb;
                string ftag = RoleTagText(fs, out fb);
                RenderTarget.DrawText(ftag, statsFormat,
                    new SharpDX.RectangleF(vEndX - 174, y + 8, 170, 14), fb);
            }

            // End-of-line tag (live for the current session)
            LevelRoleState st;
            if (!levelRoleStates.TryGetValue(RoleViewKey(key, tagSession), out st) || st == null) return;

            SharpDX.Direct2D1.Brush brush;
            string tag = RoleTagText(st, out brush);

            if (tagSession == currentSession)
            {
                int sep = key.IndexOf('|');
                PublishLevelRole(sep >= 0 ? key.Substring(sep + 1) : key, price, tag, st.Role);
            }

            // Left-align the tag with the level label (same anchor as "Level Label Right Offset")
            // whenever that label is drawn for this line; otherwise keep it at the line end.
            bool labelDrawn = breakTime == DateTime.MinValue
                || (currentSession != null && breakTime >= currentSession.StartTime);
            float tagX = labelDrawn ? GetCanonicalLabelAnchorX() : lineEndX - 174;

            RenderTarget.DrawText(tag, statsFormat,
                new SharpDX.RectangleF(tagX, y + 8, 170, 14), brush);
        }

        #endregion

        // =========================================================
        //  PIERCE-AND-RETURN ALERTS
        // =========================================================

        #region OnRender

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            try
            {
                if (sessions == null || sessions.Count == 0) return;
                if (ChartBars == null || ChartBars.Count <= 0) return;
                if (ChartPanel == null) return;

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

                // Snapshot to avoid concurrent modification while data thread adds sessions
                var sessionsSnapshot = sessions.ToList();

                // Pass 1: per-session profile + fill
                foreach (var session in sessionsSnapshot)
                {
                    if (session.StartBarIndex < 0) continue;
                    RenderSession(chartControl, chartScale, session);
                }

                // Pass 2: prior-session extension lines (drawn on top of profiles)
                ClearPublishedLevels();
                foreach (var session in sessionsSnapshot)
                {
                    if (session.StartBarIndex < 0) continue;
                    RenderSessionExtensions(chartControl, chartScale, session);
                }

                // Pass 3: current-session level labels at the right margin
                RenderCurrentSessionLevelLabels(chartControl, chartScale);

                // Pass 4: horizontal lines at prices where a level flipped (live session)
                RenderFlipLines(chartControl, chartScale);

            }
            catch (Exception ex)
            {
                // NEVER let OnRender take the indicator down. Print full stack so we can fix the real line.
                Print("=== AntonsonProfile OnRender EXCEPTION ===");
                Print(ex.ToString());
                Print("ChartBars.Count=" + (ChartBars != null ? ChartBars.Count.ToString() : "null")
                    + "  sessions.Count=" + (sessions != null ? sessions.Count.ToString() : "null")
                    + "  CurrentBar=" + CurrentBar);
                Print("=== END EXCEPTION ===");
            }
        }

        // ---- Pass 4: flip lines ----

        /// <summary>
        /// Horizontal line at every level that flipped in the live session: from the flip
        /// point to the right edge. Green = flipped to support (price accepted above a
        /// resistance), red = flipped to resistance. Only the live session's flips are
        /// drawn — older ones stay clutter-free behind the role tags.
        /// </summary>
        private void RenderFlipLines(ChartControl chartControl, ChartScale chartScale)
        {
            if (!ShowFlipLines) return;
            if (currentSession == null || roleSupportBrush == null || roleResistanceBrush == null) return;

            FlipLineRecord[] snapshot;
            lock (flipLinesLock)
            { snapshot = flipLines.ToArray(); }

            int fullX = ChartPanel != null
                ? (int)(ChartPanel.X + ChartPanel.W)
                : SafeGetXByBarIndex(ChartBars.Count - 1) + 5;

            for (int i = 0; i < snapshot.Length; i++)
            {
                FlipLineRecord rec = snapshot[i];
                if (rec.Time < currentSession.StartTime) continue;

                int x = chartControl != null
                    ? chartControl.GetXByTime(rec.Time)
                    : SafeGetXByBarIndex(currentSession.StartBarIndex);
                if (ChartPanel != null && x < ChartPanel.X) x = ChartPanel.X;   // flip scrolled off the left: start at the panel edge
                if (x > fullX) continue;

                float y = (float)chartScale.GetYByValue(rec.Price);
                SharpDX.Direct2D1.Brush lineBrush = rec.Side < 0 ? roleResistanceBrush : roleSupportBrush;
                RenderTarget.DrawLine(new SharpDX.Vector2(x, y), new SharpDX.Vector2(fullX, y), lineBrush, 2);
            }
        }

        // ---- Per-session pass ----

        private void RenderSession(ChartControl chartControl, ChartScale chartScale, SessionProfile session)
        {
            int startX       = SafeGetXByBarIndex(session.StartBarIndex);
            int endX         = SafeGetXByBarIndex(session.EndBarIndex);
            if (endX <= startX) endX = startX + 100;
            int sessionWidth = endX - startX;

            // Current-session level lines (VPOC, VA, TPO, etc.) extend to the far right
            // edge of the chart so they remain visible beyond the developing bar.
            // Prior sessions still terminate at their own end bar.
            bool isCurrent   = (session == currentSession);
            int  levelEndX   = isCurrent && ChartPanel != null
                             ? (int)(ChartPanel.X + ChartPanel.W)
                             : endX;

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

            int endX = SafeGetXByBarIndex(session.EndBarIndex);
            // Extend from the session end bar position
            renderedSessionStart = session.StartTime;
            renderedSessionEnd   = session.EndTime;
            string pfx = (session == mostRecentCompletedSession) ? "p" : "n";

            // Naked VPOC
            // Prior (most recent completed) session's VPOC has its own toggle; older sessions use Naked VPOC.
            bool vpocOn = (session == mostRecentCompletedSession) ? ShowPriorSessionVPOC : ShowNakedVPOC;
            if (vpocOn && session.VPOCLevel > 0)
            {
                double vpocPrice = LevelCenterPrice(session.VPOCLevel);
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

            // Prior / naked RTH close (RTHC) — only terminated by RTH clock-window touches.
            // HasValidRthClose rejects overnight template sessions whose last bar sat on the RTH open boundary.
            if (ShowRTHCloseLevels && HasValidRthClose(session))
            {
                RenderExtensionLine(chartScale, endX, session.RTHClose,
                    session.RTHCloseBreakTime, rthCloseBrush, pfx + "RTHC " + session.RTHClose.ToString("F2"), ShowLevelZones);
                DrawLevelRoleTag(chartScale, endX, RoleKey(session, "RTHC"), session.RTHClose, session.RTHCloseBreakTime);
            }

            // === TPO naked extensions ===
            // Primary (cumulative) TPO levels — naked extensions use these.

            // Prior (most recent completed) session's TPO POC has its own toggle; older sessions use Naked TPO POC.
            bool tpocOn = (session == mostRecentCompletedSession) ? ShowPriorSessionTPOPOC : ShowNakedTPOPOC;
            if (tpocOn && session.POCLevel > 0)
            {
                double p = LevelCenterPrice(session.POCLevel);
                SharpDX.Direct2D1.Brush tpocBrush = session == mostRecentCompletedSession
                    ? priorTPOPocBrush : nakedTPOPocBrush;
                RenderExtensionLine(chartScale, endX, p,
                    session.TPOPOCBreakTime, tpocBrush, pfx + "POC " + p.ToString("F2"), ShowLevelZones);
                DrawLevelRoleTag(chartScale, endX, RoleKey(session, "TPOPOC"), p, session.TPOPOCBreakTime);
            }
            if ((session == mostRecentCompletedSession) ? ShowPriorSessionTPOVAHVAL : ShowNakedTPOVAHVAL)
            {
                SharpDX.Direct2D1.Brush tpoVABrush = session == mostRecentCompletedSession
                    ? priorTPOVABrush : nakedTPOVABrush;

                if (session.TPOVAH > 0)
                {
                    RenderExtensionLine(chartScale, endX, session.TPOVAH * rowSize + rowSize,
                        session.TPOVAHBreakTime, tpoVABrush, pfx + "TPOVAH " + (session.TPOVAH * rowSize + rowSize).ToString("F2"), ShowLevelZones);
                    DrawLevelRoleTag(chartScale, endX, RoleKey(session, "TPOVAH"), session.TPOVAH * rowSize + rowSize, session.TPOVAHBreakTime);
                }
                if (session.TPOVAL > 0)
                {
                    RenderExtensionLine(chartScale, endX, session.TPOVAL * rowSize,
                        session.TPOVALBreakTime, tpoVABrush, pfx + "TPOVAL " + (session.TPOVAL * rowSize).ToString("F2"), ShowLevelZones);
                    DrawLevelRoleTag(chartScale, endX, RoleKey(session, "TPOVAL"), session.TPOVAL * rowSize, session.TPOVALBreakTime);
                }
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
                double p = LevelCenterPrice(currentSession.VPOCLevel);
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
            // TPO levels

            if (ShowCurrentTPOLevels && ShowTPOPOC && currentSession.POCLevel > 0 && tpoPocBrush != null)
            {
                double p = LevelCenterPrice(currentSession.POCLevel);
                float  y = chartScale.GetYByValue(p);
                RenderTarget.DrawText("POC " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoPocBrush);
            }
            if (ShowCurrentTPOLevels && ShowTPOVAHVAL)
            {
                if (currentSession.TPOVAH > 0 && tpoVAHBrush != null)
                { double p = currentSession.TPOVAH * rowSize + rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("TPOVAH " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoVAHBrush); }
                if (currentSession.TPOVAL > 0 && tpoVALBrush != null)
                { double p = currentSession.TPOVAL * rowSize; float y = chartScale.GetYByValue(p);
                  RenderTarget.DrawText("TPOVAL " + p.ToString("F2"), statsFormat, new SharpDX.RectangleF(labelX, y - 7, lw, lh), tpoVALBrush); }
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
            { double p = LevelCenterPrice(session.VPOCLevel); float y = chartScale.GetYByValue(p);
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
            // TPO levels
            if (ShowTPOPOC && session.POCLevel > 0 && tpoPocBrush != null)
            { double p = LevelCenterPrice(session.POCLevel); float y = chartScale.GetYByValue(p);
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
        }

        // =========================================================
        //  Individual render helpers
        // =========================================================

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
                    DrawLevelZone(chartScale, startX, endX, LevelCenterPrice(session.VPOCLevel));
                if (ShowVolumeValueArea)
                {
                    if (session.VVAH > 0) DrawLevelZone(chartScale, startX, endX, session.VVAH * rowSize + rowSize);
                    if (session.VVAL > 0) DrawLevelZone(chartScale, startX, endX, session.VVAL * rowSize);
                }

            }

            // ---- TPO Profile levels ----
            if (showTPO)
            {
                if (ShowTPOPOC && session.POCLevel > 0)
                    DrawLevelZone(chartScale, startX, endX, LevelCenterPrice(session.POCLevel));
                if (ShowTPOVAHVAL)
                {
                    if (session.TPOVAH > 0) DrawLevelZone(chartScale, startX, endX, session.TPOVAH * rowSize + rowSize);
                    if (session.TPOVAL > 0) DrawLevelZone(chartScale, startX, endX, session.TPOVAL * rowSize);
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
            if (VADisplayStyle == AntonsonDisplayStyle.Box)
            {
                // Box mode: 4 DrawLine calls per band (top, left, bottom, right) so each H and L
                // boundary carries its own color, width, and stroke style independently.
                // Side lines follow the H-level settings (they connect downward from the H boundary).
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
            else // AntonsonDisplayStyle.Lines — each of the 6 VA boundaries is an independent horizontal line
            {
                float lineLeft  = startX;
                // Current session: extend to the far right edge of the chart
                float lineRight = isCurrent && ChartPanel != null
                    ? (ChartPanel.X + ChartPanel.W)
                    : rightEdge;

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

            if (VolumeBarDisplayMode == AntonsonBarDisplayMode.Delta)
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
            else if (VolumeBarDisplayMode == AntonsonBarDisplayMode.BidAsk)
            {
                float fpRight = rightEdge - vpWidth;

                float boxTopY    = float.MaxValue;
                float boxBottomY = float.MinValue;

                foreach (var kvp in session.VolumeAtPrice)
                {
                    int  level  = kvp.Key;
                    long volume = kvp.Value;

                    float priceY, priceTY;
                    GetLevelYBounds(chartScale, level, out priceTY, out priceY);
                    float rowH = priceY - priceTY;
                    float h    = Math.Max(1f, rowH - 1f);

                    if (priceTY < boxTopY)    boxTopY    = priceTY;
                    if (priceY  > boxBottomY) boxBottomY = priceY;

                    float barW = (float)volume / maxVolume * vpWidth;

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

                if (boxBottomY > boxTopY)
                    RenderTarget.DrawRectangle(
                        new SharpDX.RectangleF(fpRight, boxTopY, vpWidth, boxBottomY - boxTopY),
                        textBrush, 0.5f);
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
            double vpocPrice = LevelCenterPrice(session.VPOCLevel);
            float  y         = chartScale.GetYByValue(vpocPrice);
            RenderTarget.DrawLine(
                new SharpDX.Vector2(startX, y),
                new SharpDX.Vector2(endX,   y),
                vpocBrush, 2); // solid within session; extension lines use dashStrokeStyle
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
                float pocY = chartScale.GetYByValue(LevelCenterPrice(session.POCLevel));
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
        /// Safe GetXByBarIndex – never passes an out-of-range index.
        /// Returns the right edge of the panel when ChartBars is empty/invalid.
        /// </summary>
        private int SafeGetXByBarIndex(int barIndex)
        {
            if (ChartControl == null || ChartBars == null || ChartBars.Count <= 0)
                return ChartPanel != null ? (int)(ChartPanel.X + ChartPanel.W) : 0;
            int idx = Math.Max(0, Math.Min(ChartBars.Count - 1, barIndex));
            return ChartControl.GetXByBarIndex(ChartBars, idx);
        }

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
                : SafeGetXByBarIndex(ChartBars != null ? ChartBars.Count - 1 : 0) + 5;

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
                    && ChartBars.Count > 0
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
                int ex = SafeGetXByBarIndex(endIdx);
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
                return SafeGetXByBarIndex(ChartBars != null ? ChartBars.Count - 1 : 0) + 5;
            return SafeGetXByBarIndex(currentSession.EndBarIndex) + 5f;
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
        // Exposes prior/naked session levels (VPOC, VVAH, VVAL, HI, LO, RTHC, plus SPL/SPH = bottom/top edge of each single print
        // band) and signal times to other indicators on the
        // same chart. Keeps a lightweight archive that is NOT trimmed to MaxSessionsToShow, so history older than the
        // drawn sessions can still be analysed. Nothing here changes how levels are calculated or drawn.
        #region LevelAccessor

        public class STVPLevelRecord
        {
            public string   Type;          // VPOC, VVAH, VVAL, HI, LO, RTHC, SPL, SPH
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
            if (s.VPOCLevel > 0) add("VPOC", LevelCenterPrice(s.VPOCLevel), s.VPOCBreakTime);
            if (s.VVAH > 0)      add("VVAH", s.VVAH * rowSize + rowSize,            s.VVAHBreakTime);
            if (s.VVAL > 0)      add("VVAL", s.VVAL * rowSize,                      s.VVALBreakTime);
            if (s.SessionHigh > 0 && s.SessionHigh != double.MinValue) add("HI", s.SessionHigh, s.HighBreakTime);
            if (s.SessionLow  > 0 && s.SessionLow  != double.MaxValue) add("LO", s.SessionLow,  s.LowBreakTime);
            if (HasValidRthClose(s)) add("RTHC", s.RTHClose, s.RTHCloseBreakTime);
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
            // Use cached tick size – never call live TickSize / Instrument from here
            // because this path is also reached from OnRender.
            double ts = cachedTickSize > 0 ? cachedTickSize : rowSize;
            if (ts <= 0) return 0;

            double rounded = Math.Round(price / ts) * ts;
            long tickIndex = (long)Math.Round(rounded / ts);
            int tpr = TicksPerRow > 0 ? TicksPerRow : 1;

            if (tickIndex >= 0)
                return (int)(tickIndex / tpr);

            return (int)((tickIndex - (tpr - 1)) / tpr);
        }

        /// <summary>Bottom (lowest) price of a profile row bucket.
        /// Uses cached rowSize / tick size only – safe to call from OnRender.</summary>
        private double LevelToPrice(int level)
        {
            // Prefer rowSize (already = tickSize * TicksPerRow). Fallback to cachedTickSize.
            if (rowSize > 0)
                return level * rowSize;
            double ts = cachedTickSize > 0 ? cachedTickSize : 0;
            int tpr = TicksPerRow > 0 ? TicksPerRow : 1;
            return level * tpr * ts;
        }

        /// <summary>
        /// Chart Y bounds for a profile row, centered on the price so rows line up with
        /// SuperDOM / ladder price levels.
        /// TicksPerRow = 1: centered on the exact tick price (half-tick above and below).
        /// TicksPerRow &gt; 1: centered on the mid-price of the multi-tick bucket.
        /// Safe for OnRender – never touches live TickSize.
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

                volumeBarOutlineBrush = DxBrush(VolumeBarOutlineColor);

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

                priorHighLowBrush     = DxBrush(PriorHighLowColor);
                rthCloseBrush         = DxBrush(RTHCloseColor);
                textBrush             = DxBrush(TextColor);

                volumeFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", VolumeFontSize);
                volumeFormat.TextAlignment = TextAlignment.Trailing;
                volumeFormat.WordWrapping  = WordWrapping.NoWrap;

                statsFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", LabelFontSize);
                statsFormat.TextAlignment = TextAlignment.Leading;
                statsFormat.WordWrapping  = WordWrapping.NoWrap;

                dashStrokeStyle = new StrokeStyle(RenderTarget.Factory,
                    new StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
                dotStrokeStyle = new StrokeStyle(RenderTarget.Factory,
                    new StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dot });
                dashDotStrokeStyle = new StrokeStyle(RenderTarget.Factory,
                    new StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.DashDot });

                // Per-level VA line brushes
                vvahLineBrush = DxBrush(VVAHLineColor);
                vvalLineBrush = DxBrush(VVALLineColor);

                // TPO brushes
                tpoLetterBrush   = DxBrush(TPOLetterColor);
                tpoPocBrush      = DxBrush(TPOPOCColor);
                tpoVAHBrush      = DxBrush(TPOVAHColor);
                tpoVALBrush      = DxBrush(TPOVALColor);
                nakedTPOPocBrush = DxBrush(NakedTPOPOCColor);
                priorTPOPocBrush = DxBrush(PriorSessionTPOPOCColor);
                nakedTPOVABrush  = DxBrush(NakedTPOVAColor);
                priorTPOVABrush  = DxBrush(PriorSessionTPOVAColor);
                singlePrintBrush = DxBrush(SinglePrintColor);
                poorHighLowBrush = DxBrush(PoorHighLowColor);
                excessBrush      = DxBrush(ExcessColor);
                tpoVABoxBrush    = DxBrush(TPOVABoxColor);

                letterFormat = new SharpDX.DirectWrite.TextFormat(
                    NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", TPOFontSize);
                letterFormat.TextAlignment = TextAlignment.Center;
                letterFormat.WordWrapping  = WordWrapping.NoWrap;

                resourcesCreated = true;
            }
            catch (Exception ex)
            {
                Print("AntonsonProfile: CreateResources error — " + ex.Message);
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

            if (volumeBarOutlineBrush != null) { volumeBarOutlineBrush.Dispose(); volumeBarOutlineBrush = null; }

            if (levelZoneFillBrush    != null) { levelZoneFillBrush.Dispose();    levelZoneFillBrush    = null; }
            if (levelZoneBorderBrush  != null) { levelZoneBorderBrush.Dispose();  levelZoneBorderBrush  = null; }
            if (nakedVpocBrush        != null) { nakedVpocBrush.Dispose();        nakedVpocBrush        = null; }
            if (nakedVABrush          != null) { nakedVABrush.Dispose();          nakedVABrush          = null; }
            if (nakedVAHBrush         != null) { nakedVAHBrush.Dispose();         nakedVAHBrush         = null; }
            if (roleSupportBrush      != null) { roleSupportBrush.Dispose();      roleSupportBrush      = null; }
            if (roleResistanceBrush   != null) { roleResistanceBrush.Dispose();   roleResistanceBrush   = null; }
            if (roleBreakingBrush     != null) { roleBreakingBrush.Dispose();     roleBreakingBrush     = null; }
            if (roleUntestedBrush     != null) { roleUntestedBrush.Dispose();     roleUntestedBrush     = null; }
            if (priorHighLowBrush     != null) { priorHighLowBrush.Dispose();     priorHighLowBrush     = null; }
            if (rthCloseBrush         != null) { rthCloseBrush.Dispose();         rthCloseBrush         = null; }
            if (textBrush             != null) { textBrush.Dispose();             textBrush             = null; }
            if (volumeFormat          != null) { volumeFormat.Dispose();          volumeFormat          = null; }
            if (statsFormat           != null) { statsFormat.Dispose();           statsFormat           = null; }

            if (dashStrokeStyle       != null) { dashStrokeStyle.Dispose();       dashStrokeStyle       = null; }
            if (dotStrokeStyle        != null) { dotStrokeStyle.Dispose();        dotStrokeStyle        = null; }
            if (dashDotStrokeStyle    != null) { dashDotStrokeStyle.Dispose();    dashDotStrokeStyle    = null; }
            if (vvahLineBrush         != null) { vvahLineBrush.Dispose();         vvahLineBrush         = null; }
            if (vvalLineBrush         != null) { vvalLineBrush.Dispose();         vvalLineBrush         = null; }

            // TPO brushes
            if (tpoLetterBrush   != null) { tpoLetterBrush.Dispose();   tpoLetterBrush   = null; }
            if (tpoPocBrush      != null) { tpoPocBrush.Dispose();      tpoPocBrush      = null; }
            if (tpoVAHBrush      != null) { tpoVAHBrush.Dispose();      tpoVAHBrush      = null; }
            if (tpoVALBrush      != null) { tpoVALBrush.Dispose();      tpoVALBrush      = null; }
            if (nakedTPOPocBrush != null) { nakedTPOPocBrush.Dispose(); nakedTPOPocBrush = null; }
            if (priorTPOPocBrush != null) { priorTPOPocBrush.Dispose(); priorTPOPocBrush = null; }
            if (nakedTPOVABrush  != null) { nakedTPOVABrush.Dispose();  nakedTPOVABrush  = null; }
            if (priorTPOVABrush  != null) { priorTPOVABrush.Dispose();  priorTPOVABrush  = null; }
            if (singlePrintBrush != null) { singlePrintBrush.Dispose(); singlePrintBrush = null; }
            if (poorHighLowBrush != null) { poorHighLowBrush.Dispose(); poorHighLowBrush = null; }
            if (excessBrush      != null) { excessBrush.Dispose();      excessBrush      = null; }
            if (tpoVABoxBrush    != null) { tpoVABoxBrush.Dispose();    tpoVABoxBrush    = null; }
            if (letterFormat     != null) { letterFormat.Dispose();     letterFormat     = null; }

            // Cluster zone brushes

            resourcesCreated = false;
        }

        #endregion
    }
}
