using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace BridgeSurface
{
    /// <summary>
    /// PropertyManager page for creating (feat == null) or editing (feat != null) a bridge.
    /// Shows a live temporary-body preview whenever both edges are selected.
    /// </summary>
    [ComVisible(true)]
    public class BridgePage : IPropertyManagerPage2Handler9
    {
        private readonly ModelDoc2 _model;
        private readonly Feature _editFeat;          // null when creating
        private readonly MacroFeatureData _editData;  // the SAME object AccessSelections was called on
        private readonly PropertyManagerPage2 _page;
        private readonly BridgeOptions _opt;

        private PropertyManagerPageSelectionbox _selA, _selB;
        private PropertyManagerPageCombobox _cmbMode;
        private PropertyManagerPageNumberbox _numDeg, _numCVs, _numTol;
        private PropertyManagerPageCombobox _cmbContA, _cmbContB, _cmbFlowA, _cmbFlowB;
        private PropertyManagerPageNumberbox _numMagA, _numMagB, _numMag2A, _numMag2B;
        private PropertyManagerPageNumberbox _numStartA, _numEndA, _numStartB, _numEndB;
        private PropertyManagerPageCheckbox _chkFlipA, _chkFlipB, _chkShowNet, _chkZebra, _chkAveraged;
        private PropertyManagerPageNumberbox _numStripes, _numStripeW, _numTessTol, _numTessAng;
        // zebra mesh density, page-only (defaults as Surface Analysis): chord tolerance mm, chord angle degrees
        private double _tessTolMM = 0.1, _tessAngDeg = 10.0;
        private bool _needFull, _needZebra;   // what the debounce timer should do when it fires
        private readonly CvNetOverlay _overlay = new CvNetOverlay();

        /// <summary>
        /// Every group and control created on the page. SOLIDWORKS does not keep its own
        /// reference to them: if the add-in drops one, the GC releases it and SOLIDWORKS
        /// destroys it (and the controls inside a group) while the page is still open.
        /// </summary>
        private readonly System.Collections.Generic.List<object> _keepAlive = new System.Collections.Generic.List<object>();

        private Body2 _preview;
        private readonly System.Windows.Forms.Timer _debounce;
        private int _closeReason;
        private bool _pendingFocusB;   // move selection focus to side B after A is picked
        // curve sides (no face): continuity is locked to G0; the user's choice is kept to restore if an edge is picked again
        private bool _curveA, _curveB;
        private int _savedContA, _savedContB;

        private const int IdGrpEdges = 10, IdSelA = 11, IdSelB = 12;
        private const int IdGrpU = 20, IdMode = 21, IdDeg = 22, IdCVs = 23, IdTol = 24;
        private const int IdLblDeg = 25, IdLblCVs = 26, IdLblTol = 27;
        private const int IdGrpC = 40, IdContA = 41, IdMagA = 42, IdFlipA = 43, IdContB = 44, IdMagB = 45, IdFlipB = 46;
        private const int IdLblContA = 47, IdLblMagA = 48, IdLblContB = 49, IdLblMagB = 50;
        private const int IdGrpDisp = 60, IdShowNet = 61, IdGrpCB = 62;
        private const int IdZebra = 63, IdStripes = 64, IdStripeW = 65, IdLblStripes = 66, IdLblStripeW = 67;
        private const int IdTessTol = 110, IdTessAng = 111, IdLblTessTol = 112, IdLblTessAng = 113;
        private const int IdGrpPlace = 80, IdAveraged = 81;
        private const int IdBtnView = 68, IdBtnX = 74, IdBtnY = 75, IdBtnZ = 76;
        private const int IdFlowA = 70, IdFlowB = 71, IdLblFlowA = 72, IdLblFlowB = 73;
        private const int IdStartA = 90, IdEndA = 91, IdStartB = 92, IdEndB = 93;
        private const int IdLblRangeA = 94, IdLblRangeB = 95;
        private const int IdMag2A = 100, IdMag2B = 101;

        public BridgePage(ModelDoc2 model, Feature editFeat, MacroFeatureData editData)
        {
            _model = model;
            _editFeat = editFeat;
            _editData = editData;
            _opt = editFeat != null
                ? BridgeFeature.ReadOptions(editData)
                : new BridgeOptions();

            int err = 0;
            int pageOpts = (int)(swPropertyManagerPageOptions_e.swPropertyManagerOptions_OkayButton |
                                 swPropertyManagerPageOptions_e.swPropertyManagerOptions_CancelButton);
            _page = (PropertyManagerPage2)SwAddin.SwApp.CreatePropertyManagerPage(
                "Bridge Surface", pageOpts, this, ref err);
            BuildControls();

            // Preview updates run from this timer, not inside control events:
            // updating graphics/messages inside a number box event stops the box responding.
            _debounce = new System.Windows.Forms.Timer { Interval = 150 };
            _debounce.Tick += (sender, e) =>
            {
                _debounce.Stop();
                if (_pendingFocusB)
                {
                    _pendingFocusB = false;
                    Guarded(() => { if (_selB.ItemCount == 0) _selB.SetSelectionFocus(); });
                }
                bool full = _needFull, zebra = _needZebra;
                _needFull = _needZebra = false;
                if (full) Guarded(UpdatePreview);
                else if (zebra) Guarded(RebuildZebra);
            };
        }

        public void Show()
        {
            _page.Show2(0);
            ApplyEdgeColours();
            _overlay.Attach(_model);
            // Enabled states set before Show2 don't stick: apply them again now the page is visible.
            _enabledForMode = -1;
            UpdateEnables();
            if (_editFeat != null)
            {
                // put the stored edges back into the selection boxes (marks 1 / 2)
                object eA, eB;
                if (BridgeFeature.ReadEdges(_editData, out eA, out eB))
                    BridgeFeature.SelectEdges(_model, eA, eB);
            }
            UpdatePreview();
        }

        // ------------------------------------------------------------------
        // Controls
        // ------------------------------------------------------------------
        private void BuildControls()
        {
            int grpOpen = (int)(swAddGroupBoxOptions_e.swGroupBoxOptions_Visible | swAddGroupBoxOptions_e.swGroupBoxOptions_Expanded);
            int grpShut = (int)swAddGroupBoxOptions_e.swGroupBoxOptions_Visible;   // collapsed by default
            int ctlOpts = (int)(swAddControlOptions_e.swControlOptions_Visible | swAddControlOptions_e.swControlOptions_Enabled);
            short left = (short)swPropertyManagerPageControlLeftAlign_e.swControlAlign_LeftEdge;
            var unitless = swNumberboxUnitType_e.swNumberBox_UnitlessDouble;

            // --- Edges: pick an edge, then its extent ---
            var gE = (PropertyManagerPageGroup)_page.AddGroupBox(IdGrpEdges, "Edges", grpOpen);
            _keepAlive.Add(gE);
            _selA = MakeSel(gE, IdSelA, "Side A: an edge, or a sketch curve (G0 only)", 1, ctlOpts, left);
            _lblRangeA = MakeLabel(gE, IdLblRangeA, "Side A range (start, end)", ctlOpts, left);
            _numStartA = MakeNum(gE, IdStartA, "Start of side A (0-1, by arc length)", ctlOpts, left, unitless, 0.0, 1.0, 0.01, _opt.StartA);
            _numEndA = MakeNum(gE, IdEndA, "End of side A (0-1, by arc length)", ctlOpts, left, unitless, 0.0, 1.0, 0.01, _opt.EndA);
            _selB = MakeSel(gE, IdSelB, "Side B: an edge, or a sketch curve (G0 only)", 2, ctlOpts, left);
            _lblRangeB = MakeLabel(gE, IdLblRangeB, "Side B range (start, end)", ctlOpts, left);
            _numStartB = MakeNum(gE, IdStartB, "Start of side B (0-1, by arc length)", ctlOpts, left, unitless, 0.0, 1.0, 0.01, _opt.StartB);
            _numEndB = MakeNum(gE, IdEndB, "End of side B (0-1, by arc length)", ctlOpts, left, unitless, 0.0, 1.0, 0.01, _opt.EndB);

            // --- Along the edges (U): how the edges are read into the surface, so it sits with them ---
            var gU = (PropertyManagerPageGroup)_page.AddGroupBox(IdGrpU, "Along edges (U)", grpOpen);
            _keepAlive.Add(gU);
            _cmbMode = (PropertyManagerPageCombobox)gU.AddControl2(IdMode,
                (short)swPropertyManagerPageControlType_e.swControlType_Combobox, "Mode", left, ctlOpts, "U parameterisation");
            _cmbMode.AddItems(new[] { "Auto (densest edge)", "Manual (degree + CVs)", "Fit (fewest CVs in tolerance)" });
            _cmbMode.CurrentSelection = (short)_opt.Mode;
            MakeLabel(gU, IdLblDeg, "Degree", ctlOpts, left);
            _numDeg = MakeNum(gU, IdDeg, "U degree (Manual and Fit)", ctlOpts, left, swNumberboxUnitType_e.swNumberBox_UnitlessInteger, 1, 7, 1, _opt.ManualDegree);
            MakeLabel(gU, IdLblCVs, "CVs (Manual)", ctlOpts, left);
            _numCVs = MakeNum(gU, IdCVs, "U CV count (Manual)", ctlOpts, left, swNumberboxUnitType_e.swNumberBox_UnitlessInteger, 2, 200, 1, _opt.ManualCVs);
            MakeLabel(gU, IdLblTol, "Fit tolerance, mm (Fit)", ctlOpts, left);
            _numTol = MakeNum(gU, IdTol, "Fit tolerance, mm (Fit)", ctlOpts, left, unitless, 0.0001, 1.0, 0.001, _opt.FitTol * 1000);

            // --- CV placement: global, and it overrides the magnitudes in both edge groups, so it comes first ---
            var gP = (PropertyManagerPageGroup)_page.AddGroupBox(IdGrpPlace, "CV placement", grpOpen);
            _keepAlive.Add(gP);
            _chkAveraged = MakeCheck(gP, IdAveraged, "Averaged (overrides magnitudes)", ctlOpts, left, _opt.Averaged);

            // --- Side A ---
            var gA = (PropertyManagerPageGroup)_page.AddGroupBox(IdGrpC, "Side A", grpOpen);
            _keepAlive.Add(gA);
            _lblsA.Add(MakeLabel(gA, IdLblContA, "Continuity", ctlOpts, left));
            _cmbContA = MakeContCombo(gA, IdContA, ctlOpts, left, _opt.ContA);
            _lblsA.Add(MakeLabel(gA, IdLblFlowA, "Flow", ctlOpts, left));
            _cmbFlowA = MakeFlowCombo(gA, IdFlowA, ctlOpts, left, _opt.FlowA);
            _lblsA.Add(MakeLabel(gA, IdLblMagA, "G1 / G2 magnitude", ctlOpts, left));
            _numMagA = MakeNum(gA, IdMagA, "G1 magnitude: tangent leg, 1 = local span / V degree", ctlOpts, left, unitless, 0.1, 5.0, 0.05, _opt.MagA);
            _numMag2A = MakeNum(gA, IdMag2A, "G2 magnitude: second leg as a multiple of the G1 leg", ctlOpts, left, unitless, 0.1, 5.0, 0.05, _opt.Mag2A);
            _chkFlipA = MakeCheck(gA, IdFlipA, "Flip", ctlOpts, left, _opt.FlipA);

            // --- Side B ---
            var gB = (PropertyManagerPageGroup)_page.AddGroupBox(IdGrpCB, "Side B", grpOpen);
            _keepAlive.Add(gB);
            _lblsB.Add(MakeLabel(gB, IdLblContB, "Continuity", ctlOpts, left));
            _cmbContB = MakeContCombo(gB, IdContB, ctlOpts, left, _opt.ContB);
            _lblsB.Add(MakeLabel(gB, IdLblFlowB, "Flow", ctlOpts, left));
            _cmbFlowB = MakeFlowCombo(gB, IdFlowB, ctlOpts, left, _opt.FlowB);
            _lblsB.Add(MakeLabel(gB, IdLblMagB, "G1 / G2 magnitude", ctlOpts, left));
            _numMagB = MakeNum(gB, IdMagB, "G1 magnitude: tangent leg, 1 = local span / V degree", ctlOpts, left, unitless, 0.1, 5.0, 0.05, _opt.MagB);
            _numMag2B = MakeNum(gB, IdMag2B, "G2 magnitude: second leg as a multiple of the G1 leg", ctlOpts, left, unitless, 0.1, 5.0, 0.05, _opt.Mag2B);
            _chkFlipB = MakeCheck(gB, IdFlipB, "Flip", ctlOpts, left, _opt.FlipB);

            // --- Display (page-only: nothing is stored on the feature) ---
            var gD = (PropertyManagerPageGroup)_page.AddGroupBox(IdGrpDisp, "Display", grpShut);
            _keepAlive.Add(gD);
            _chkShowNet = MakeCheck(gD, IdShowNet, "Show CV net", ctlOpts, left, true);
            _chkZebra = MakeCheck(gD, IdZebra, "Zebra stripes (bridge + adjacent faces)", ctlOpts, left, false);
            MakeLabel(gD, IdLblStripes, "Stripe count", ctlOpts, left);
            _numStripes = MakeNum(gD, IdStripes, "Stripe count", ctlOpts, left, swNumberboxUnitType_e.swNumberBox_UnitlessInteger, 1, 40, 1, 6);
            MakeLabel(gD, IdLblStripeW, "Stripe width", ctlOpts, left);
            _numStripeW = MakeNum(gD, IdStripeW, "Stripe width", ctlOpts, left, unitless, 0.01, 0.45, 0.01, 0.08);
            MakeLabel(gD, IdLblTessTol, "Mesh tolerance, mm (zebra)", ctlOpts, left);
            _numTessTol = MakeNum(gD, IdTessTol, "Zebra mesh chord tolerance, mm", ctlOpts, left, unitless, 0.0001, 1.0, 0.01, _tessTolMM);
            MakeLabel(gD, IdLblTessAng, "Chord angle, deg (zebra)", ctlOpts, left);
            _numTessAng = MakeNum(gD, IdTessAng, "Zebra mesh chord angle, degrees", ctlOpts, left, unitless, 0.5, 45.0, 0.5, _tessAngDeg);
            MakeButton(gD, IdBtnY, "Zebra axis: Y (horizontal)", ctlOpts, left);
            MakeButton(gD, IdBtnX, "Zebra axis: X (vertical)", ctlOpts, left);

            ApplyEdgeColours();
            UpdateEnables();
        }

        // ------------------------------------------------------------------
        // Side A / Side B colours: the same pair in the page, on the picked inputs, and in the CV net.
        // Applied at build time AND again after Show2 (page properties set before Show2 may not stick).
        // ------------------------------------------------------------------
        private PropertyManagerPageControl _lblRangeA, _lblRangeB;
        private readonly List<PropertyManagerPageControl> _lblsA = new List<PropertyManagerPageControl>();
        private readonly List<PropertyManagerPageControl> _lblsB = new List<PropertyManagerPageControl>();

        private void ApplyEdgeColours()
        {
            Guarded(() =>
            {
                _selA.SetSelectionColor(true, EdgeColours.ARef);
                _selB.SetSelectionColor(true, EdgeColours.BRef);
                ((PropertyManagerPageControl)_selA).TextColor = EdgeColours.ARef;
                ((PropertyManagerPageControl)_selB).TextColor = EdgeColours.BRef;
                _lblRangeA.TextColor = EdgeColours.ARef;
                _lblRangeB.TextColor = EdgeColours.BRef;
                foreach (var l in _lblsA) l.TextColor = EdgeColours.ARef;
                foreach (var l in _lblsB) l.TextColor = EdgeColours.BRef;
            });
        }

        private PropertyManagerPageSelectionbox MakeSel(PropertyManagerPageGroup g, int id, string caption, int mark, int opts, short left)
        {
            var s = (PropertyManagerPageSelectionbox)g.AddControl2(id,
                (short)swPropertyManagerPageControlType_e.swControlType_Selectionbox, caption, left, opts, caption);
            s.SetSelectionFilters(new[] { (int)swSelectType_e.swSelEDGES, (int)swSelectType_e.swSelREFEDGES,
                                          (int)swSelectType_e.swSelSKETCHSEGS, (int)swSelectType_e.swSelEXTSKETCHSEGS });
            s.SingleEntityOnly = true;
            s.Mark = mark;
            s.Height = 15;
            return s;
        }

        private PropertyManagerPageCombobox MakeContCombo(PropertyManagerPageGroup g, int id, int opts, short left, int value)
        {
            var c = (PropertyManagerPageCombobox)g.AddControl2(id,
                (short)swPropertyManagerPageControlType_e.swControlType_Combobox, "Continuity", left, opts, "Continuity to the edge's face");
            c.AddItems(new[] { "G0 position", "G1 tangent", "G2 curvature" });
            c.CurrentSelection = (short)value;
            return c;
        }

        private PropertyManagerPageCombobox MakeFlowCombo(PropertyManagerPageGroup g, int id, int opts, short left, int value)
        {
            var c = (PropertyManagerPageCombobox)g.AddControl2(id,
                (short)swPropertyManagerPageControlType_e.swControlType_Combobox, "Flow", left, opts, "Cross-boundary direction of the rows");
            c.AddItems(new[] { "Normal to edge", "Match ends", "Towards other edge", "Isocurve", "Free (averaged only)" });
            c.CurrentSelection = (short)value;
            return c;
        }

        private void MakeButton(PropertyManagerPageGroup g, int id, string caption, int opts, short left)
        {
            _keepAlive.Add(g.AddControl2(id, (short)swPropertyManagerPageControlType_e.swControlType_Button, caption, left, opts, caption));
        }

        private PropertyManagerPageCheckbox MakeCheck(PropertyManagerPageGroup g, int id, string caption, int opts, short left, bool value)
        {
            var c = (PropertyManagerPageCheckbox)g.AddControl2(id,
                (short)swPropertyManagerPageControlType_e.swControlType_Checkbox, caption, left, opts, caption);
            c.Checked = value;
            return c;
        }

        /// <summary>Static label: caption set once at creation, never changed (changing it crashes).</summary>
        private PropertyManagerPageControl MakeLabel(PropertyManagerPageGroup g, int id, string caption, int opts, short left)
        {
            var l = g.AddControl2(id, (short)swPropertyManagerPageControlType_e.swControlType_Label, caption, left, opts, "");
            _keepAlive.Add(l);
            return (PropertyManagerPageControl)l;
        }

        private PropertyManagerPageNumberbox MakeNum(PropertyManagerPageGroup g, int id, string caption, int opts, short left,
                                                     swNumberboxUnitType_e units, double min, double max, double inc, double value)
        {
            var n = (PropertyManagerPageNumberbox)g.AddControl2(id,
                (short)swPropertyManagerPageControlType_e.swControlType_Numberbox, caption, left, opts, caption);
            n.SetRange2((int)units, min, max, true, inc, inc * 10, inc);
            n.Value = value;
            return n;
        }

        private int _enabledForMode = -1;

        /// <summary>Only touch Enabled when the mode actually changes: toggling a control
        /// inside its own event breaks its spinner/focus.</summary>
        private void UpdateEnables()
        {
            int key = (int)_opt.Mode * 100 + _opt.ContA * 10 + _opt.ContB + (_opt.Averaged ? 1000 : 0) +
                      (_curveA ? 2000 : 0) + (_curveB ? 4000 : 0);
            if (_enabledForMode == key) return;
            _enabledForMode = key;
            ((PropertyManagerPageControl)_cmbContA).Enabled = !_curveA;   // a curve side is G0 only
            ((PropertyManagerPageControl)_cmbContB).Enabled = !_curveB;
            ((PropertyManagerPageControl)_numDeg).Enabled = _opt.Mode != UMode.Auto;
            ((PropertyManagerPageControl)_numCVs).Enabled = _opt.Mode == UMode.Manual;
            ((PropertyManagerPageControl)_numTol).Enabled = _opt.Mode == UMode.Fit;
            ((PropertyManagerPageControl)_numMagA).Enabled = _opt.ContA > 0 && !_opt.Averaged;
            ((PropertyManagerPageControl)_numMag2A).Enabled = _opt.ContA > 1 && !_opt.Averaged;
            ((PropertyManagerPageControl)_chkFlipA).Enabled = _opt.ContA > 0;
            ((PropertyManagerPageControl)_cmbFlowA).Enabled = _opt.ContA > 0;
            ((PropertyManagerPageControl)_numMagB).Enabled = _opt.ContB > 0 && !_opt.Averaged;
            ((PropertyManagerPageControl)_numMag2B).Enabled = _opt.ContB > 1 && !_opt.Averaged;
            ((PropertyManagerPageControl)_chkFlipB).Enabled = _opt.ContB > 0;
            ((PropertyManagerPageControl)_cmbFlowB).Enabled = _opt.ContB > 0;
        }

        // ------------------------------------------------------------------
        // Preview
        // ------------------------------------------------------------------
        private bool GetEdges(out object eA, out object eB)
        {
            eA = eB = null;
            var selMgr = (SelectionMgr)_model.SelectionManager;
            int n = selMgr.GetSelectedObjectCount2(-1);
            for (int i = 1; i <= n; i++)
            {
                int mark = selMgr.GetSelectedObjectMark(i);
                var e = selMgr.GetSelectedObject6(i, -1);
                if (!(e is Edge) && !(e is SketchSegment)) continue;
                if (mark == 1) eA = e; else if (mark == 2) eB = e;
            }
            return eA != null && eB != null;
        }

        private void ClearPreview()
        {
            if (_preview != null)
            {
                _preview.Hide(_model);
                Com.Release(_preview);
                _preview = null;
            }
        }

        /// <summary>The controls are the source of truth: read them rather than trusting event arguments.</summary>
        private void ReadControls()
        {
            // Mode comes from the combo event's Item argument (OnComboboxSelectionChanged):
            // CurrentSelection can still hold the old index while the event is firing.
            int sel = _cmbMode.CurrentSelection;
            _opt.ManualDegree = Math.Max(1, (int)Math.Round(_numDeg.Value));
            _opt.ManualCVs = Math.Max(_opt.ManualDegree + 1, (int)Math.Round(_numCVs.Value));
            _opt.FitTol = Math.Max(0.0001, _numTol.Value) / 1000.0;   // mm -> m
            _opt.MagA = Math.Max(0.01, _numMagA.Value);
            _opt.MagB = Math.Max(0.01, _numMagB.Value);
            _opt.Mag2A = Math.Max(0.01, _numMag2A.Value);
            _opt.Mag2B = Math.Max(0.01, _numMag2B.Value);
            // ranges are passed through unclamped: an inverted or too-short range is reported by the
            // builder rather than silently fixed, so the boxes never fight the user mid-edit
            _opt.StartA = _numStartA.Value; _opt.EndA = _numEndA.Value;
            _opt.StartB = _numStartB.Value; _opt.EndB = _numEndB.Value;
            Log("ReadControls: combo=" + sel + " mode=" + _opt.Mode + " deg=" + _opt.ManualDegree +
                " cvs=" + _opt.ManualCVs + " tol(mm)=" + _numTol.Value);
        }

        /// <summary>
        /// A side whose input has no face (a sketch curve, a curve's edge) is G0 only: set its continuity to G0
        /// and lock the box; give the user's previous choice back when an edge is picked again.
        /// Runs from the preview timer, never inside a control's own event.
        /// </summary>
        private void ApplySideKinds(object eA, object eB)
        {
            bool cA = eA != null && Geometry.FaceSampler.IsCurve(eA);
            bool cB = eB != null && Geometry.FaceSampler.IsCurve(eB);
            if (cA != _curveA)
            {
                if (cA) { _savedContA = _opt.ContA; _opt.ContA = 0; } else _opt.ContA = _savedContA;
                _curveA = cA;
                if (_cmbContA.CurrentSelection != _opt.ContA) _cmbContA.CurrentSelection = (short)_opt.ContA;
            }
            if (cB != _curveB)
            {
                if (cB) { _savedContB = _opt.ContB; _opt.ContB = 0; } else _opt.ContB = _savedContB;
                _curveB = cB;
                if (_cmbContB.CurrentSelection != _opt.ContB) _cmbContB.CurrentSelection = (short)_opt.ContB;
            }
        }

        private void UpdatePreview()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ReadControls();
            UpdateEnables();
            ClearPreview();
            object eA, eB;
            bool both = GetEdges(out eA, out eB);
            ApplySideKinds(eA, eB);
            UpdateEnables();
            if (!both) { Com.Release(eA, eB); _overlay.SetGrid(null); _overlay.SetZebra(null); ShowReport("Select side A and side B (edges or sketch curves)."); return; }

            string report;
            string settings = string.Format("[mode {0}, degree {1}, CVs {2}, tol {3:0.0000} mm | A: G{4}{5}{6}{10} | B: G{7}{8}{9}{11}]\n",
                _opt.Mode, _opt.ManualDegree, _opt.ManualCVs, _opt.FitTol * 1000,
                _opt.ContA, MagText(_opt.ContA, _opt.MagA, _opt.Mag2A), _opt.FlipA ? " flip" : "",
                _opt.ContB, MagText(_opt.ContB, _opt.MagB, _opt.Mag2B), _opt.FlipB ? " flip" : "",
                _opt.IsPartial(0) ? string.Format(" {0:0.00}-{1:0.00}", _opt.StartA, _opt.EndA) : "",
                _opt.IsPartial(1) ? string.Format(" {0:0.00}-{1:0.00}", _opt.StartB, _opt.EndB) : "");
            try
            {
                _preview = BridgeBuilder.Build(SwAddin.SwApp, eA, eB, _opt, out report);
            }
            catch (Exception ex)
            {
                _preview = null;
                report = "ERROR: " + ex.GetType().Name + ": " + ex.Message;
                Log(ex.ToString());
            }
            Log(settings);
            ShowReport(report ?? "");
            Log("UpdatePreview done in " + sw.ElapsedMilliseconds + " ms");
            _overlay.SetGrid(_preview != null ? BridgeBuilder.LastGrid : null);
            // zebra meshes only while zebra is on: fine tolerances make them the slowest part of a preview
            _overlay.SetZebra(_preview != null && _overlay.ZebraVisible ? BuildZebra(eA, eB, BridgeBuilder.LastGrid) : null);
            if (_preview != null)
                _preview.Display3(_model, 0x00C08040, (int)swTempBodySelectOptions_e.swTempBodySelectOptionNone);
            Com.Release(eA, eB);
        }

        private string MagText(int cont, double m1, double m2)
        {
            if (cont == 0 || _opt.Averaged) return "";
            return cont >= 2 ? string.Format(" x{0:0.00}/{1:0.00}", m1, m2) : string.Format(" x{0:0.00}", m1);
        }

        /// <summary>Zebra meshes: the bridge (from its CV grid) and the faces adjacent to both edges.
        /// The bridge's normals are flipped if needed to agree with face A's, so the stripe phase carries across.</summary>
        private List<ZebraMesh> BuildZebra(object eA, object eB, CvGrid grid)
        {
            var list = new List<ZebraMesh>();
            if (grid == null) return list;
            ZebraMesh faceA = null;
            for (int side = 0; side < 2; side++)
            {
                var e = (side == 0 ? eA : eB) as Edge;
                if (e == null) continue;   // a sketch curve has no face to show
                bool dir; int nf;
                var f = Geometry.FaceSampler.MatchFace(e, side == 0 ? _opt.FlipA : _opt.FlipB, out dir, out nf);
                if (f == null) continue;
                try
                {
                    var fb = f.GetBody() as Body2;
                    var m = ZebraMesh.FromTessellation(fb, new[] { f }, _tessTolMM, _tessAngDeg) ?? ZebraMesh.FromFace(f);
                    Com.Release(fb);
                    if (m != null) { list.Add(m); if (faceA == null) faceA = m; }
                }
                finally { Com.Release(f); }
            }
            var bridge = ZebraMesh.FromTessellation(_preview, null, _tessTolMM, _tessAngDeg) ?? ZebraMesh.FromGrid(grid);
            var ref0 = faceA ?? (list.Count > 0 ? list[0] : null);
            if (ref0 != null)
            {
                var p = grid.Eval(0.5, 0.0);                    // on edge A
                var nf = ref0.NormalNear(p);
                var nb = bridge.NormalNear(p);
                if (nf != null && nb != null && Geometry.V.Dot(nf, nb) < 0) bridge.FlipNormals();
            }
            list.Add(bridge);
            return list;
        }

        /// <summary>Report text in the page's own message box (label captions crash when set from callbacks).</summary>
        private void ShowReport(string text)
        {
            Log("REPORT: " + text.Replace("\n", " | "));
            _page.SetMessage3(text.Replace("\n", "\r\n"),
                (int)swPropertyManagerPageMessageVisibility.swMessageBoxVisible,
                (int)swPropertyManagerPageMessageExpanded.swMessageBoxExpand,
                "Bridge Surface");
        }

        // ------------------------------------------------------------------
        // Handler events used
        // ------------------------------------------------------------------
        public void OnSelectionboxListChanged(int Id, int Count)
        {
            Log("OnSelectionboxListChanged id=" + Id + " count=" + Count);
            if (Id == IdSelA && Count > 0) _pendingFocusB = true;
            RequestPreview();
        }

        /// <summary>Rebuild only the zebra meshes (tessellation settings or zebra toggled), not the surface.</summary>
        private void RequestZebra()
        {
            _needZebra = true;
            _debounce.Stop();
            _debounce.Start();
        }

        private void RebuildZebra()
        {
            if (!_overlay.ZebraVisible || _preview == null || BridgeBuilder.LastGrid == null) { _overlay.SetZebra(null); return; }
            object eA, eB;
            if (!GetEdges(out eA, out eB)) { _overlay.SetZebra(null); return; }
            try { _overlay.SetZebra(BuildZebra(eA, eB, BridgeBuilder.LastGrid)); }
            finally { Com.Release(eA, eB); }
        }

        private void RequestPreview()
        {
            _needFull = true;
            _debounce.Stop();
            _debounce.Start();
        }

        public void OnComboboxSelectionChanged(int Id, int Item)
        {
            Log("OnComboboxSelectionChanged id=" + Id + " item=" + Item);
            if (Id == IdMode && Item >= 0 && Item <= 2) _opt.Mode = (UMode)Item;
            else if (Id == IdContA && Item >= 0 && Item <= 2) _opt.ContA = Item;
            else if (Id == IdContB && Item >= 0 && Item <= 2) _opt.ContB = Item;
            else if (Id == IdFlowA && Item >= 0 && Item <= 4) _opt.FlowA = Item;
            else if (Id == IdFlowB && Item >= 0 && Item <= 4) _opt.FlowB = Item;
            RequestPreview();
        }

        public void OnNumberboxChanged(int Id, double Value)
        {
            Log("OnNumberboxChanged id=" + Id + " value=" + Value);
            // stripe settings only change the drawing, not the surface
            if (Id == IdStripes) { _overlay.LineFrequency = (float)Math.Max(1, Value); _overlay.Redraw(); return; }
            if (Id == IdStripeW) { _overlay.LineWidth = (float)Math.Max(0.01, Value); _overlay.Redraw(); return; }
            if (Id == IdTessTol) { _tessTolMM = Math.Max(0.0001, Value); RequestZebra(); return; }
            if (Id == IdTessAng) { _tessAngDeg = Math.Max(0.5, Value); RequestZebra(); return; }
            RequestPreview();
        }

        /// <summary>SOLIDWORKS swallows exceptions thrown from page events; surface them instead.</summary>
        private void Guarded(Action a)
        {
            try { a(); }
            catch (Exception ex)
            {
                Log(ex.ToString());
                try { ShowReport("ERROR: " + ex.GetType().Name + ": " + ex.Message); } catch { }
            }
        }

        private static void Log(string msg)
        {
            System.Diagnostics.Debug.WriteLine("[BridgeSurface] " + msg);
        }

        public void OnClose(int Reason)
        {
            _debounce.Stop();
            _overlay.Detach();
            _closeReason = Reason;
            ClearPreview();
        }

        public void AfterClose()
        {
            try { AfterCloseCore(); }
            finally
            {
                _debounce.Dispose();
                if (ReferenceEquals(SwAddin.ActivePage, this)) SwAddin.ActivePage = null;
            }
        }

        private void AfterCloseCore()
        {
            bool ok = _closeReason == (int)swPropertyManagerPageCloseReasons_e.swPropertyManagerPageClose_Okay;

            if (_editFeat != null)
            {
                // AccessSelections (in BridgeFeature.Edit) rolled the model back to just before this
                // feature. It is rolled forward again only by ModifyDefinition or ReleaseSelectionAccess
                // called on that same definition object.
                if (!ok) { _editData.ReleaseSelectionAccess(); return; }

                object eA, eB;
                if (GetEdges(out eA, out eB))
                    _editData.SetSelections2(new object[] { eA, eB }, new[] { 1, 2 }, new object[] { null, null });
                // Replace the whole parameter list: SetStringByName only updates parameters the feature already
                // has, so a bridge inserted by an older build (without e.g. StartA or Mag2A) could not take the new ones.
                string[] names = BridgeOptions.ParamNames, values = _opt.ToValues();
                var types = new int[names.Length];
                for (int i = 0; i < types.Length; i++) types[i] = (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString;
                _editData.SetParameters(names, types, values);
                Log("Edit: inputs A = " + Geometry.FaceSampler.Describe(eA) + ", B = " + Geometry.FaceSampler.Describe(eB) +
                    ", " + names.Length + " parameters written");

                bool modified = _editFeat.ModifyDefinition(_editData, _model, null);
                Log("ModifyDefinition returned " + modified);
                if (!modified)
                {
                    _editData.ReleaseSelectionAccess();
                    SwAddin.SwApp.SendMsgToUser("Bridge Surface: the edit could not be applied (ModifyDefinition failed).");
                }
                return;
            }

            if (!ok) return;
            object a, b;
            if (!GetEdges(out a, out b)) { SwAddin.SwApp.SendMsgToUser("Bridge Surface: select two edges."); return; }
            string err;
            BridgeFeature.Insert(_model, a, b, _opt, out err);
            if (err != null) SwAddin.SwApp.SendMsgToUser("Bridge Surface: " + err);
        }

        // ------------------------------------------------------------------
        // Remaining IPropertyManagerPage2Handler9 members (unused)
        // If VS reports a missing or mismatched member, use Quick Actions >
        // "Implement interface" and delete the duplicate stub here.
        // ------------------------------------------------------------------
        public void AfterActivation() { }
        public bool OnHelp() { return true; }
        public bool OnPreviousPage() { return true; }
        public bool OnNextPage() { return true; }
        public bool OnPreview() { return true; }
        public void OnWhatsNew() { }
        public void OnUndo() { }
        public void OnRedo() { }
        public bool OnTabClicked(int Id) { return true; }
        public void OnGroupExpand(int Id, bool Expanded) { }
        public void OnGroupCheck(int Id, bool Checked) { }
        public void OnCheckboxCheck(int Id, bool Checked)
        {
            Log("OnCheckboxCheck id=" + Id + " checked=" + Checked);
            if (Id == IdShowNet) { _overlay.Visible = Checked; _overlay.Redraw(); return; }
            if (Id == IdZebra) { _overlay.ZebraVisible = Checked; RequestZebra(); return; }
            if (Id == IdAveraged) { _opt.Averaged = Checked; UpdateEnables(); RequestPreview(); return; }
            if (Id == IdFlipA) _opt.FlipA = Checked;
            else if (Id == IdFlipB) _opt.FlipB = Checked;
            RequestPreview();
        }
        public void OnOptionCheck(int Id) { }
        public void OnButtonPress(int Id)
        {
            if (Id == IdBtnY) { _overlay.MatcapAxisY = true; _overlay.Redraw(); }
            else if (Id == IdBtnX) { _overlay.MatcapAxisY = false; _overlay.Redraw(); }
        }
        public void OnTextboxChanged(int Id, string Text) { }
        public void OnComboboxEditChanged(int Id, string Text) { }
        public void OnListboxSelectionChanged(int Id, int Item) { }
        public void OnSelectionboxFocusChanged(int Id) { }
        public void OnSelectionboxCalloutCreated(int Id) { }
        public void OnSelectionboxCalloutDestroyed(int Id) { }
        public bool OnSubmitSelection(int Id, object Selection, int SelType, ref string ItemText) { return true; }
        public int OnActiveXControlCreated(int Id, bool Status) { return -1; }
        public int OnWindowFromHandleControlCreated(int Id, bool Status) { return 0; }
        public void OnSliderPositionChanged(int Id, double Value) { }
        public void OnSliderTrackingCompleted(int Id, double Value) { }
        public bool OnKeystroke(int Wparam, int Message, int Lparam, int Id) { return false; }
        public void OnPopupMenuItem(int Id) { }
        public void OnPopupMenuItemUpdate(int Id, ref int retval) { }
        public void OnGainedFocus(int Id) { }
        public void OnLostFocus(int Id) { }
        public void OnListboxRMBUp(int Id, int PosX, int PosY) { }
        public void OnNumberBoxTrackingCompleted(int Id, double Value) { }
    }
}
