#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

// Converts an ES price to a YM price that appears at the "same" auction location for this session.
//   AutoFit : live ratio  YM price / ES price  (the Dow/S&P basis is not fixed, so it tracks the spread)
//   Fixed   : user constant (e.g. 10.0 = legacy point-for-point scaling)
// Result is snapped to the YM tick size so lines sit on real YM prices.
public enum STVPYMRatioMode
{
    /// <summary>Ratio computed live from the source chart's last traded price vs this chart's last price.</summary>
    AutoFit,
    /// <summary>Constant multiplier entered by the user.</summary>
    Fixed
}

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// Reads the levels that SessionTPOVolumeProfile is currently drawing on an ES chart (prior-session
    /// and naked VPOC/VVAH/VVAL/HI/LO plus TPO POC/VAH/VAL and single prints, exactly the set enabled
    /// by the ES chart's toggles) and mirrors them on this chart as converted YM prices.
    /// Lines span the source session's time range and are labeled with the converted price.
    /// </summary>
    public class STVPYMCrossLevels : Indicator
    {
        // SharpDX resources
        private SharpDX.Direct2D1.SolidColorBrush hiLoBrush;
        private SharpDX.Direct2D1.SolidColorBrush vpocBrush;
        private SharpDX.Direct2D1.SolidColorBrush vaBrush;
        private SharpDX.Direct2D1.SolidColorBrush tpoBrush;
        private SharpDX.Direct2D1.SolidColorBrush spBrush;
        private SharpDX.Direct2D1.SolidColorBrush textBrush;
        private SharpDX.DirectWrite.TextFormat    labelFormat;
        private SharpDX.Direct2D1.StrokeStyle     dashStroke;

        // Tracks the WPF brush instances the DX brushes were built from so a property
        // change (new brush instance) triggers a rebuild.
        private System.Windows.Media.Brush builtFromHiLo;
        private System.Windows.Media.Brush builtFromVpoc;
        private System.Windows.Media.Brush builtFromVa;
        private System.Windows.Media.Brush builtFromTpo;
        private System.Windows.Media.Brush builtFromSp;
        private System.Windows.Media.Brush builtFromText;

        private DateTime lastDiagAt = DateTime.MinValue;

        private SharpDX.Direct2D1.SolidColorBrush roleSupportBrush;
        private SharpDX.Direct2D1.SolidColorBrush roleResistanceBrush;
        private SharpDX.Direct2D1.SolidColorBrush roleOtherBrush;
        private System.Windows.Media.Brush builtFromRoleSupport;
        private System.Windows.Media.Brush builtFromRoleResistance;
        private System.Windows.Media.Brush builtFromRoleOther;

        private int    diagDrawn, diagOff, diagSpan, diagOld, diagUnknown;
        private string diagOffSample = "";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = "Mirrors the SessionTPOVolumeProfile levels enabled on an ES chart onto this chart as converted YM prices.";
                Name                        = "STVPYMCrossLevels";
                Calculate                   = Calculate.OnPriceChange;
                IsOverlay                   = true;
                DisplayInDataBox            = false;
                DrawOnPricePanel            = true;
                PaintPriceMarkers           = false;
                IsSuspendedWhileInactive    = false;
                IsAutoScale                 = true;
                BarsRequiredToPlot          = 1;

                // Two invisible plots carry the highest/lowest converted level so the YM
                // panel's auto-scale expands enough to show every mirrored level.
                AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line);
                AddPlot(new Stroke(System.Windows.Media.Brushes.Transparent, 1), PlotStyle.Line);

                SourceFilter                = "ES";
                RatioMode                   = STVPYMRatioMode.AutoFit;
                FixedRatio                  = 10.0;
                MaxAgeSeconds               = 0;      // 0 = never hide stale levels
                ShowBrokenLevels            = true;   // draw levels the source chart has already tested
                ShowRoleTags                = true;
                IncludeLevelsInScale        = true;
                ShowLabels                  = true;
                LineWidth                   = 2;
                LabelFontSize               = 9;

                HighLowColor    = System.Windows.Media.Brushes.Gray;
                VpocColor       = System.Windows.Media.Brushes.Cyan;
                VaColor         = System.Windows.Media.Brushes.MediumOrchid;
                TpoColor        = System.Windows.Media.Brushes.Magenta;
                SinglePrintColor= System.Windows.Media.Brushes.Orange;
                RoleSupportColor    = System.Windows.Media.Brushes.LimeGreen;
                RoleResistanceColor = System.Windows.Media.Brushes.Red;
                RoleOtherColor      = System.Windows.Media.Brushes.Gold;
                TextColor       = System.Windows.Media.Brushes.White;
            }
            else if (State == State.Terminated)
            {
                DisposeResources();
            }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeResources();
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (RenderTarget == null || ChartBars == null || chartControl == null) return;

            if (Diagnostics && (DateTime.UtcNow - lastDiagAt).TotalMilliseconds >= 2000)
            {
                lastDiagAt = DateTime.UtcNow;
                int pubCount; double pubLast; DateTime pubAt;
                string pubInstrument = SessionTPOVolumeProfile.GetPublicationDiagnostics(SourceFilter, out pubCount, out pubLast, out pubAt);
                double probePrice;
                List<SessionTPOVolumeProfile.STVPLevelRecord> probe =
                    SessionTPOVolumeProfile.GetPublishedLevels(SourceFilter, 0, out probePrice);
                double probeRatio = ResolveRatio(pubLast);
                string msg = "YM Cross diag: stored=" + pubCount
                    + "  storedInstrument=" + pubInstrument
                    + "  myFilter=" + SourceFilter
                    + "  visible=" + ((probe == null) ? "rejected" : probe.Count.ToString())
                    + "  ageSec=" + ((pubAt == DateTime.MinValue) ? "n/a" : (DateTime.UtcNow - pubAt).TotalSeconds.ToString("F1"))
                    + "  input=" + (Instrument != null ? Instrument.FullName : "null")
                    + "  close0Series=" + (Bars != null && Bars.Instrument != null ? Bars.Instrument.FullName : "null")
                    + "  Close[0]=" + Close[0].ToString("F2")
                    + "  hostPanelSeries=" + (ChartBars != null && ChartBars.Bars != null && ChartBars.Bars.Instrument != null ? ChartBars.Bars.Instrument.FullName : "null")
                    + "  hostPanelLast=" + ((ChartBars != null && ChartBars.Bars != null && ChartBars.Bars.Count > 1) ? ChartBars.Bars.GetClose(ChartBars.Bars.Count - 1).ToString("F2") : "n/a")
                    + "  esLast=" + pubLast.ToString("F2")
                    + "  ratio=" + probeRatio.ToString("F4")
                    + "  barsLast=" + Bars.GetClose(Bars.Count - 1).ToString("F2")
                    + " barsLastTime=" + Bars.GetTime(Bars.Count - 1).ToString("HH:mm:ss");
                if (probe != null && probe.Count > 0)
                {
                    SessionTPOVolumeProfile.STVPLevelRecord r0 = probe[probe.Count - 1];
                    int bStart = Bars.GetBar(r0.SessionEnd);
                    int bEnd   = r0.BreakTime != DateTime.MinValue ? Bars.GetBar(r0.BreakTime) : ChartBars.Count - 1;
                    msg += "  lastRec=[type=" + r0.Type
                         + " es=" + r0.Price.ToString("F2")
                         + " ym=" + (r0.Price * probeRatio).ToString("F2")
                         + " bars " + bStart + ".." + bEnd
                         + " of " + (ChartBars.Count - 1).ToString() + "]";
                }
                msg += "  drawn=" + diagDrawn.ToString()
                     + "  offScreen=" + diagOff.ToString()
                     + "  skipSpan=" + diagSpan.ToString()
                     + "  skipOld=" + diagOld.ToString()
                     + "  skipUnknown=" + diagUnknown.ToString();
                if (diagOffSample.Length > 0)
                    msg += "  OFF[" + diagOffSample + " ]";
                Print(msg);
            }

            double esLastPrice;
            List<SessionTPOVolumeProfile.STVPLevelRecord> src =
                SessionTPOVolumeProfile.GetPublishedLevels(SourceFilter, MaxAgeSeconds, out esLastPrice);
            if (src == null || src.Count == 0) return;

            double ratio = ResolveRatio(esLastPrice);
            if (ratio <= 0) return;

            EnsureResources();
            if (dashStroke == null || labelFormat == null) return;

            double ymTick = Instrument != null && Instrument.MasterInstrument != null && Instrument.MasterInstrument.TickSize > 0
                ? Instrument.MasterInstrument.TickSize : 1.0;
            string fmt = "F" + PriceDecimals(ymTick).ToString();

            int lastBar = ChartBars.Count - 1;
            int drawN = 0, skipUnknown = 0, skipSpan = 0, skipOld = 0, off = 0;
            string offSample = "";
            double scaleHi = double.MinValue, scaleLo = double.MaxValue;
            for (int i = 0; i < src.Count; i++)
            {
                SessionTPOVolumeProfile.STVPLevelRecord rec = src[i];
                if (rec == null) continue;
                if (!ShowBrokenLevels && rec.BreakTime != DateTime.MinValue) { skipOld++; continue; }

                double ymPrice = Math.Round(rec.Price * ratio / ymTick) * ymTick;

                int startIdx = Bars.GetBar(rec.SessionEnd);
                int endIdx   = rec.BreakTime != DateTime.MinValue ? Bars.GetBar(rec.BreakTime) : lastBar;
                if (startIdx < 0) { skipUnknown++; continue; }
                if (endIdx > lastBar) endIdx = lastBar;
                int endDrawIdx = Math.Max(endIdx, Math.Min(lastBar, startIdx + 10));
                if (endDrawIdx <= startIdx) { skipSpan++; continue; }

                SharpDX.Direct2D1.Brush brush = BrushFor(rec.Type);
                if (brush == null) { skipUnknown++; continue; }

                float y = chartScale.GetYByValue(ymPrice);
                float xStart = chartControl.GetXByBarIndex(ChartBars, startIdx);
                float xEnd   = chartControl.GetXByBarIndex(ChartBars, endDrawIdx);

                if (y >= 0 && y <= RenderTarget.Size.Height)
                {
                    RenderTarget.DrawLine(new SharpDX.Vector2(xStart, y),
                                          new SharpDX.Vector2(xEnd, y),
                                          brush, Math.Max(1, LineWidth), dashStroke);
                    drawN++;

                    if (ShowLabels)
                    {
                        string tag = rec.Type + " " + ymPrice.ToString(fmt) + " (" + rec.Price.ToString("F2") + ")";
                        SharpDX.Direct2D1.Brush textB = textBrush != null ? textBrush : brush;
                        RenderTarget.DrawText(tag, labelFormat,
                            new SharpDX.RectangleF(xEnd + 6, y - labelFormat.FontSize - 2, 190, labelFormat.FontSize + 4), textB);
                    }
                    if (ShowRoleTags && rec.RoleText != null)
                    {
                        SharpDX.Direct2D1.Brush roleB = RoleBrushFor(rec.RoleText);
                        RenderTarget.DrawText(rec.RoleText, labelFormat,
                            new SharpDX.RectangleF(xEnd + 6, y + 2, 190, labelFormat.FontSize + 4), roleB);
                    }
                }
                else
                {
                    off++;
                    if (off <= 8)
                        offSample += " " + rec.Type + " es=" + rec.Price.ToString("F2") + " ym=" + ymPrice.ToString("F0") + (y < 0 ? " ABOVE" : " BELOW");
                }

                if (ymPrice > scaleHi) scaleHi = ymPrice;
                if (ymPrice < scaleLo) scaleLo = ymPrice;
                if (ShowRoleTags && rec.RoleText != null)
                {
                    SharpDX.Direct2D1.Brush roleB = RoleBrushFor(rec.RoleText);
                    RenderTarget.DrawText(rec.RoleText, labelFormat,
                        new SharpDX.RectangleF(xEnd + 6, y + 2, 190, labelFormat.FontSize + 4), roleB);
                }
            }

            if (CurrentBar >= 0)
            {
                if (IncludeLevelsInScale)
                {
                    Values[0][0] = scaleHi != double.MinValue ? scaleHi : Close[0];
                    Values[1][0] = scaleLo != double.MaxValue ? scaleLo : Close[0];
                }
                else
                {
                    Values[0][0] = double.NaN;
                    Values[1][0] = double.NaN;
                }
            }

            diagDrawn = drawN; diagOff = off; diagSpan = skipSpan;
            diagOld = skipOld; diagUnknown = skipUnknown; diagOffSample = offSample;
        }

        private double ResolveRatio(double esLastPrice)
        {
            if (RatioMode == STVPYMRatioMode.Fixed)
                return FixedRatio > 0 ? FixedRatio : 0;

            double ymLast = 0;
            if (ChartBars != null && ChartBars.Bars != null && ChartBars.Bars.Count > 1)
                ymLast = ChartBars.Bars.GetClose(ChartBars.Bars.Count - 1);
            if (ymLast <= 0)
                ymLast = Close[0];
            if (esLastPrice > 0 && ymLast > 0)
            {
                double r = ymLast / esLastPrice;
                if (r >= 0.5 && r <= 25.0)   // sanity clamp against a bad snapshot
                    return r;
            }
            return FixedRatio > 0 ? FixedRatio : 0;
        }

        private SharpDX.Direct2D1.Brush BrushFor(string type)
        {
            string t = (type ?? "").TrimStart('p', 'n', 'r', ' ');
            if (t.StartsWith("TPO", StringComparison.Ordinal)) return tpoBrush;
            if (t.StartsWith("SP",  StringComparison.Ordinal)) return spBrush;
            if (t.Contains("POC"))  return vpocBrush;
            if (t.Contains("VAH") || t.Contains("VAL")) return vaBrush;
            if (t.Contains("HI")  || t.Contains("LO"))  return hiLoBrush;
            return textBrush;
        }

        private SharpDX.Direct2D1.Brush RoleBrushFor(string roleText)
        {
            if (roleText.Contains("BREAKING") || roleText.Contains("testing")) return roleOtherBrush;
            if (roleText.StartsWith("Support",    StringComparison.Ordinal)) return roleSupportBrush;
            if (roleText.StartsWith("Resistance", StringComparison.Ordinal)) return roleResistanceBrush;
            return textBrush;
        }

        private static int PriceDecimals(double tick)
        {
            int dp = 0;
            while (tick > 0 && tick < 1 && dp < 8) { tick *= 10; dp++; }
            return dp;
        }

        private SharpDX.Direct2D1.SolidColorBrush DxBrush(System.Windows.Media.Brush b)
        {
            System.Windows.Media.Color c = System.Windows.Media.Colors.Gray;
            System.Windows.Media.SolidColorBrush sc = b as System.Windows.Media.SolidColorBrush;
            if (sc != null) c = sc.Color;
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                new SharpDX.Color(c.R, c.G, c.B, c.A));
        }

        private void EnsureResources()
        {
            if (RenderTarget == null) return;
            if (dashStroke != null
                && builtFromHiLo == HighLowColor && builtFromVpoc == VpocColor
                && builtFromVa == VaColor && builtFromTpo == TpoColor
                && builtFromSp == SinglePrintColor && builtFromText == TextColor
                && builtFromRoleSupport == RoleSupportColor
                && builtFromRoleResistance == RoleResistanceColor
                && builtFromRoleOther == RoleOtherColor)
                return;

            DisposeResources();

            hiLoBrush = DxBrush(HighLowColor);      builtFromHiLo = HighLowColor;
            vpocBrush = DxBrush(VpocColor);         builtFromVpoc = VpocColor;
            vaBrush   = DxBrush(VaColor);           builtFromVa   = VaColor;
            tpoBrush  = DxBrush(TpoColor);          builtFromTpo  = TpoColor;
            spBrush   = DxBrush(SinglePrintColor);  builtFromSp   = SinglePrintColor;
            textBrush = DxBrush(TextColor);         builtFromText = TextColor;

            roleSupportBrush    = DxBrush(RoleSupportColor);    builtFromRoleSupport    = RoleSupportColor;
            roleResistanceBrush = DxBrush(RoleResistanceColor); builtFromRoleResistance = RoleResistanceColor;
            roleOtherBrush      = DxBrush(RoleOtherColor);      builtFromRoleOther      = RoleOtherColor;

            labelFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory, "Consolas", Math.Max(6, LabelFontSize));
            labelFormat.WordWrapping = WordWrapping.NoWrap;

            dashStroke = new SharpDX.Direct2D1.StrokeStyle(RenderTarget.Factory,
                new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
        }

        private void DisposeResources()
        {
            if (hiLoBrush   != null) { hiLoBrush.Dispose();   hiLoBrush   = null; }
            if (vpocBrush   != null) { vpocBrush.Dispose();   vpocBrush   = null; }
            if (vaBrush     != null) { vaBrush.Dispose();     vaBrush     = null; }
            if (tpoBrush    != null) { tpoBrush.Dispose();    tpoBrush    = null; }
            if (spBrush     != null) { spBrush.Dispose();     spBrush     = null; }
            if (textBrush   != null) { textBrush.Dispose();   textBrush   = null; }
            if (roleSupportBrush    != null) { roleSupportBrush.Dispose();    roleSupportBrush    = null; }
            if (roleResistanceBrush != null) { roleResistanceBrush.Dispose(); roleResistanceBrush = null; }
            if (roleOtherBrush      != null) { roleOtherBrush.Dispose();      roleOtherBrush      = null; }
            if (labelFormat != null) { labelFormat.Dispose(); labelFormat = null; }
            if (dashStroke  != null) { dashStroke.Dispose();  dashStroke  = null; }
        }

        #region Properties

        #region Properties - Source

        [NinjaScriptProperty]
        [Display(Name = "Source Instrument Filter",
                 Description = "Only mirror levels published by a source chart trading this instrument, e.g. ES. Leave blank to accept any source.",
                 Order = 1, GroupName = "1. Source")]
        public string SourceFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0, 86400)]
        [Display(Name = "Max Publication Age (sec)",
                 Description = "Hide mirrored levels if the source chart has not refreshed within this many seconds. 0 = never hide (default), useful when the ES chart sits in an inactive tab.",
                 Order = 2, GroupName = "1. Source")]
        public int MaxAgeSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Broken Levels",
                 Description = "Also draw levels the source chart has already tested (broken). Off = naked/untested levels only.",
                 Order = 3, GroupName = "1. Source")]
        public bool ShowBrokenLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Diagnostics",
                 Description = "Print publish/read state to the NinjaScript Output window (New > NinjaScript Output) every 2 seconds for troubleshooting.",
                 Order = 4, GroupName = "1. Source")]
        public bool Diagnostics { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Role Tags",
                 Description = "Draw the live support/resistance tag (untested / testing / held / breaking / flipped) under each mirrored level, matching the ES panel.",
                 Order = 5, GroupName = "1. Source")]
        public bool ShowRoleTags { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Role Support Color", Description = "Color for tags in the support role", Order = 6, GroupName = "1. Source")]
        public System.Windows.Media.Brush RoleSupportColor { get; set; }
        [Browsable(false)]
        public string RoleSupportColorSerialize
        { get { return Serialize.BrushToString(RoleSupportColor); } set { RoleSupportColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Role Resistance Color", Description = "Color for tags in the resistance role", Order = 7, GroupName = "1. Source")]
        public System.Windows.Media.Brush RoleResistanceColor { get; set; }
        [Browsable(false)]
        public string RoleResistanceColorSerialize
        { get { return Serialize.BrushToString(RoleResistanceColor); } set { RoleResistanceColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Role Testing/Breaking Color", Description = "Color for tags while a test or break is in progress", Order = 8, GroupName = "1. Source")]
        public System.Windows.Media.Brush RoleOtherColor { get; set; }
        [Browsable(false)]
        public string RoleOtherColorSerialize
        { get { return Serialize.BrushToString(RoleOtherColor); } set { RoleOtherColor = Serialize.StringToBrush(value); } }

        #endregion

        #region Properties - Conversion

        [NinjaScriptProperty]
        [Display(Name = "Conversion Mode",
                 Description = "AutoFit computes the live ES-to-YM ratio from last traded prices on both charts. Fixed uses a constant multiplier.",
                 Order = 1, GroupName = "2. Conversion")]
        public STVPYMRatioMode RatioMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 100.0)]
        [Display(Name = "Fixed Ratio (YM = ES x ratio)",
                 Description = "Multiplier used in Fixed mode, and the fallback when AutoFit cannot compute a live ratio (default 10).",
                 Order = 2, GroupName = "2. Conversion")]
        public double FixedRatio { get; set; }

        #endregion

        #region Properties - Appearance

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Line Width", Description = "Width of the mirrored level lines", Order = 1, GroupName = "3. Appearance")]
        public int LineWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Labels", Description = "Draw the level type, converted YM price and source ES price at the right end of each line", Order = 2, GroupName = "3. Appearance")]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Include Levels in Scale",
                 Description = "Expand the chart's price scale so every mirrored level is on-screen (invisible anchor plots). Off leaves the scale fitted to candles only.",
                 Order = 2, GroupName = "2. Conversion")]
        public bool IncludeLevelsInScale { get; set; }

        [NinjaScriptProperty]
        [Range(6, 40)]
        [Display(Name = "Label Font Size", Description = "Font size of the mirrored level labels", Order = 3, GroupName = "3. Appearance")]
        public int LabelFontSize { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "High/Low Color", Description = "Color for mirrored HI / LO levels", Order = 4, GroupName = "3. Appearance")]
        public System.Windows.Media.Brush HighLowColor { get; set; }
        [Browsable(false)]
        public string HighLowColorSerialize
        { get { return Serialize.BrushToString(HighLowColor); } set { HighLowColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "VPOC Color", Description = "Color for mirrored (volume) VPOC levels", Order = 5, GroupName = "3. Appearance")]
        public System.Windows.Media.Brush VpocColor { get; set; }
        [Browsable(false)]
        public string VpocColorSerialize
        { get { return Serialize.BrushToString(VpocColor); } set { VpocColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "VA Color", Description = "Color for mirrored VVAH / VVAL / TPOVAH / TPOVAL levels", Order = 6, GroupName = "3. Appearance")]
        public System.Windows.Media.Brush VaColor { get; set; }
        [Browsable(false)]
        public string VaColorSerialize
        { get { return Serialize.BrushToString(VaColor); } set { VaColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "TPO POC Color", Description = "Color for mirrored TPO POC levels", Order = 7, GroupName = "3. Appearance")]
        public System.Windows.Media.Brush TpoColor { get; set; }
        [Browsable(false)]
        public string TpoColorSerialize
        { get { return Serialize.BrushToString(TpoColor); } set { TpoColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Single Print Color", Description = "Color for mirrored single-print band edges (SPL / SPH)", Order = 8, GroupName = "3. Appearance")]
        public System.Windows.Media.Brush SinglePrintColor { get; set; }
        [Browsable(false)]
        public string SinglePrintColorSerialize
        { get { return Serialize.BrushToString(SinglePrintColor); } set { SinglePrintColor = Serialize.StringToBrush(value); } }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Text Color", Description = "Label text color and fallback line color", Order = 9, GroupName = "3. Appearance")]
        public System.Windows.Media.Brush TextColor { get; set; }
        [Browsable(false)]
        public string TextColorSerialize
        { get { return Serialize.BrushToString(TextColor); } set { TextColor = Serialize.StringToBrush(value); } }

        #endregion

        #endregion
    }
}
