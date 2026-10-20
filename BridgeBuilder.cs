using System;
using System.Text;
using SolidWorks.Interop.sldworks;
using BridgeSurface.Geometry;

namespace BridgeSurface
{
    public enum UMode { Auto = 0, Manual = 1, Fit = 2 }

    /// <summary>Feature parameters. Stored on the macro feature as name/value pairs.</summary>
    public sealed class BridgeOptions
    {
        public UMode Mode = UMode.Fit;
        public int ManualDegree = 3;
        public int ManualCVs = 10;
        public double FitTol = 1e-5;       // m (0.01 mm)
        public int FitMaxCVs = 80;

        // continuity per side: 0 = G0, 1 = G1, 2 = G2
        public int ContA = 0, ContB = 0;
        public double MagA = 1.0, MagB = 1.0;      // G1: 1 = local span / V degree
        public double Mag2A = 1.0, Mag2B = 1.0;    // G2: in-plane leg of row 2 as a multiple of the G1 leg (1 = equal legs)
        public bool FlipA = false, FlipB = false;
        public int FlowA = 0, FlowB = 0;
        public bool Averaged = false;              // global override: fair the CV net, then match G1/G2
        public int AveragedIterations = 30;
        public double G1TolDeg = 0.05;             // FIT: max seam tangent-plane angle
        public double G2TolPct = 2.5;              // FIT: max measured G2 relative deviation, (R1-R2)/(R1+R2) in % ...
        public double G2TolAbs = 1e-2;             // ... or this absolute deviation, 1/m (0.00001 /mm), whichever passes

        // partial edges: arc-length fractions along each edge, measured from the bridge's
        // "start" end (after alignment, so Start/End mean the same end on both sides)
        public double StartA = 0, EndA = 1, StartB = 0, EndB = 1;
        public const double MinRange = 0.01;


        public BridgeOptions Copy() { return (BridgeOptions)MemberwiseClone(); }

        public bool IsPartial(int side)
        {
            return side == 0 ? (StartA > 0 || EndA < 1) : (StartB > 0 || EndB < 1);
        }

        public static readonly string[] ParamNames =
        {
            "UMode", "ManualDegree", "ManualCVs", "FitTol",
            "ContA", "ContB", "MagA", "MagB", "FlipA", "FlipB", "FlowA", "FlowB", "Averaged",
            "StartA", "EndA", "StartB", "EndB",
            "Mag2A", "Mag2B"
        };

        public string[] ToValues()
        {
            return new[]
            {
                ((int)Mode).ToString(),
                ManualDegree.ToString(),
                ManualCVs.ToString(),
                FitTol.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                ContA.ToString(),
                ContB.ToString(),
                MagA.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                MagB.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                FlipA ? "1" : "0",
                FlipB ? "1" : "0",
                FlowA.ToString(),
                FlowB.ToString(),
                Averaged ? "1" : "0",
                StartA.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                EndA.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                StartB.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                EndB.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Mag2A.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                Mag2B.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        public static BridgeOptions FromValues(string[] names, string[] values)
        {
            var o = new BridgeOptions();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < names.Length; i++)
            {
                switch (names[i])
                {
                    case "UMode": o.Mode = (UMode)int.Parse(values[i], ci); break;
                    case "ManualDegree": o.ManualDegree = int.Parse(values[i], ci); break;
                    case "ManualCVs": o.ManualCVs = int.Parse(values[i], ci); break;
                    case "FitTol": o.FitTol = double.Parse(values[i], ci); break;
                    // absent on features saved by the G0-only version -> defaults (G0/G0)
                    case "ContA": o.ContA = int.Parse(values[i], ci); break;
                    case "ContB": o.ContB = int.Parse(values[i], ci); break;
                    case "MagA": o.MagA = double.Parse(values[i], ci); break;
                    case "MagB": o.MagB = double.Parse(values[i], ci); break;
                    case "FlipA": o.FlipA = values[i] == "1"; break;
                    case "FlipB": o.FlipB = values[i] == "1"; break;
                    case "FlowA": o.FlowA = int.Parse(values[i], ci); break;
                    case "FlowB": o.FlowB = int.Parse(values[i], ci); break;
                    case "Averaged": o.Averaged = values[i] == "1"; break;
                    // absent on features saved before partial edges -> whole edges
                    case "StartA": o.StartA = double.Parse(values[i], ci); break;
                    case "EndA": o.EndA = double.Parse(values[i], ci); break;
                    case "StartB": o.StartB = double.Parse(values[i], ci); break;
                    case "EndB": o.EndB = double.Parse(values[i], ci); break;
                    // absent on features saved before separate G2 magnitudes -> 1 (equal legs, the old behaviour)
                    case "Mag2A": o.Mag2A = double.Parse(values[i], ci); break;
                    case "Mag2B": o.Mag2B = double.Parse(values[i], ci); break;
                    // "SameAsA" (removed option) is ignored: B's own values were saved alongside it
                }
            }
            return o;
        }
    }

    /// <summary>
    /// Builds the bridge sheet body from two edges. Used by both the live preview
    /// and the macro feature's Regenerate, so they can never disagree.
    /// G0/G1/G2 per side; V is a single Bezier span of degree rowsA + rowsB - 1.
    /// </summary>
    public static class BridgeBuilder
    {
        /// <summary>CV grid of the most recent successful build (read by the page for the CV-net preview).</summary>
        public static CvGrid LastGrid;

        /// <summary>true: append the full build log (edge reads, mode, flow notes) to the page report.
        /// It always goes to the debug output.</summary>
        public static readonly bool VerboseReport = false;

        /// <param name="refA">An Edge or a SketchSegment. A side with no face (a sketch segment or a curve's edge) is G0 only.</param>
        public static Body2 Build(SldWorks app, object refA, object refB, BridgeOptions opt, out string report)
        {
            Edge edgeA = refA as Edge, edgeB = refB as Edge;
            var curveDiag = new StringBuilder();
            bool curveA = FaceSampler.IsCurve(refA), curveB = FaceSampler.IsCurve(refB);
            bool forcedA = curveA && opt.ContA > 0, forcedB = curveB && opt.ContB > 0;
            if (forcedA || forcedB)
            {
                opt = opt.Copy();   // never change the page's own options
                if (forcedA) opt.ContA = 0;
                if (forcedB) opt.ContB = 0;
            }
            report = null;
            LastGrid = null;
            Diag.Clear();
            var modeler = (Modeler)app.GetModeler();
            var sb = new StringBuilder();

            // --- read edges at tight tolerance, then restore ---
            double old0 = modeler.SetToleranceValue(EdgeReader.TolIdBsCurve, EdgeReader.ReadTolerance);
            double old1 = modeler.SetToleranceValue(EdgeReader.TolIdBsCurveNonRat, EdgeReader.ReadTolerance);
            string errA, errB;
            BSpl A, B;
            try
            {
                A = EdgeReader.ReadAny(app, refA, "Side A", curveDiag, out errA);
                B = EdgeReader.ReadAny(app, refB, "Side B", curveDiag, out errB);
            }
            finally
            {
                modeler.SetToleranceValue(EdgeReader.TolIdBsCurve, old0);
                modeler.SetToleranceValue(EdgeReader.TolIdBsCurveNonRat, old1);
            }
            System.Diagnostics.Debug.WriteLine("[BridgeSurface] INPUTS: A = " + FaceSampler.Describe(refA) + ", B = " + FaceSampler.Describe(refB) +
                                               (curveDiag.Length > 0 ? " | " + curveDiag.ToString().Replace("\n", " | ") : ""));
            if (A == null) { report = "Side A: " + errA; Com.Release(modeler); return null; }
            if (B == null) { report = "Side B: " + errB; Com.Release(modeler); return null; }
            sb.AppendFormat("Side A read: order {0}, {1} CVs\n", A.Ord, A.N);
            sb.AppendFormat("Side B read: order {0}, {1} CVs\n", B.Ord, B.N);

            // --- alignment ---
            double dSame = V.Dist(A.CvAt(0), B.CvAt(0)) + V.Dist(A.CvAt(A.N - 1), B.CvAt(B.N - 1));
            double dCross = V.Dist(A.CvAt(0), B.CvAt(B.N - 1)) + V.Dist(A.CvAt(A.N - 1), B.CvAt(0));
            if (dCross < dSame) { B.Reverse(); sb.Append("Side B reversed to align\n"); }

            // --- partial edges: exact sub-curves by arc length (alignment above uses the whole edges) ---
            string rangeErr = CheckRange("A", opt.StartA, opt.EndA) ?? CheckRange("B", opt.StartB, opt.EndB);
            if (rangeErr != null) { report = rangeErr; Com.Release(modeler); return null; }
            if (opt.IsPartial(0))
            {
                A = Trim.ByArc(A, opt.StartA, opt.EndA);
                sb.AppendFormat("Side A range {0:0.000} to {1:0.000}: {2} CVs\n", opt.StartA, opt.EndA, A.N);
            }
            if (opt.IsPartial(1))
            {
                B = Trim.ByArc(B, opt.StartB, opt.EndB);
                sb.AppendFormat("Side B range {0:0.000} to {1:0.000}: {2} CVs\n", opt.StartB, opt.EndB, B.N);
            }

            // --- U basis and boundary rows ---
            BSpl rowA, rowB;
            double devA = 0, devB = 0;
            var arcA = new ArcTable(A);
            var arcB = new ArcTable(B);

            if (opt.Mode == UMode.Auto)
            {
                bool aMaster = A.N > B.N || (A.N == B.N && A.Ord >= B.Ord);
                BSpl M = aMaster ? A : B;
                var arcM = aMaster ? arcA : arcB;
                if (aMaster)
                {
                    rowA = A;
                    rowB = A.SameBasis(B) ? B : Fit.Curve(B, arcB, M, arcM, out devB);
                }
                else
                {
                    rowB = B;
                    rowA = A.SameBasis(B) ? A : Fit.Curve(A, arcA, M, arcM, out devA);
                }
                sb.AppendFormat("U mode: AUTO (master {0})\n", aMaster ? "A" : "B");
            }
            else
            {
                int ord = opt.ManualDegree + 1;
                int nStart = opt.Mode == UMode.Manual ? opt.ManualCVs : ord;
                int nEnd = opt.Mode == UMode.Manual ? opt.ManualCVs : opt.FitMaxCVs;
                if (nStart < ord) nStart = ord;
                rowA = rowB = null;
                for (int n = nStart; n <= nEnd; n++)
                {
                    var T = BSpl.Uniform(ord, n);
                    rowA = Fit.Curve(A, arcA, T, null, out devA);
                    rowB = Fit.Curve(B, arcB, T, null, out devB);
                    if (devA <= opt.FitTol && devB <= opt.FitTol) break;
                }
                sb.AppendFormat("U mode: {0}\n", opt.Mode == UMode.Manual ? "MANUAL" : "FIT");
            }
            sb.AppendFormat("Edge fit deviation  A: {0:0.0000} mm  B: {1:0.0000} mm\n", devA * 1000, devB * 1000);


            // --- interior rows (G1/G2), the surface, and its measured seam continuity ---
            int cA = opt.ContA, cB = opt.ContB;
            int nV = cA + cB + 2, degV = nV - 1;
            BSpl a1 = null, a2 = null, b1 = null, b2 = null;
            double angA = 0, curvA = 0, angB = 0, curvB = 0, absA = 0, absB = 0;   // construction estimates: debug log only
            string noteA = "", noteB = "";
            Face2 fA = null, fB = null;
            Surface sA = null, sB = null, surf = null;
            bool flipDirA = false, flipDirB = false;   // direction flip after the face choice (see FaceSampler.MatchFace)
            SeamResult mA = null, mB = null;
            System.Collections.Generic.List<BSpl> rows = null;
            System.Collections.Generic.List<CvGrid.Role> roles = null;
            Body2 body;
            try
            {
                int nfA, nfB;
                if (cA > 0)
                {
                    fA = FaceSampler.MatchFace(edgeA, opt.FlipA, out flipDirA, out nfA);
                    if (fA == null) { report = "Side A has no adjacent face (G1/G2 need one)"; return null; }
                    sA = fA.GetSurface() as Surface;
                    if (nfA == 2) sb.AppendFormat("Side A: edge has 2 faces, matching the {0} one\n", opt.FlipA ? "second (Flip)" : "first");
                }
                if (cB > 0)
                {
                    fB = FaceSampler.MatchFace(edgeB, opt.FlipB, out flipDirB, out nfB);
                    if (fB == null) { report = "Side B has no adjacent face (G1/G2 need one)"; return null; }
                    sB = fB.GetSurface() as Surface;
                    if (nfB == 2) sb.AppendFormat("Side B: edge has 2 faces, matching the {0} one\n", opt.FlipB ? "second (Flip)" : "first");
                }

                while (true)
                {
                    Diag.Clear();   // rows are rebuilt on each FIT pass: keep only the final pass's warnings
                    if (cA > 0 || cB > 0)
                    {
                        var uk = rowA.SpanSamples(Fit.SamplesPerSpan);
                        int m = uk.Length;
                        var EA = new double[m][]; var TA = new double[m][];
                        var EB = new double[m][]; var TB = new double[m][];
                        for (int i = 0; i < m; i++)
                        {
                            EA[i] = rowA.Eval(uk[i]); TA[i] = rowA.Tangent(uk[i]);
                            EB[i] = rowB.Eval(uk[i]); TB[i] = rowB.Tangent(uk[i]);
                        }
                        angA = curvA = angB = curvB = absA = absB = 0;
                        double[] eA0 = null, eA1 = null, eB0 = null, eB1 = null;
                        if (cA > 0 && opt.FlowA == (int)Flow.MatchEnds) EndDirs(edgeA, fA, sA, EA, TA, out eA0, out eA1);
                        if (cB > 0 && opt.FlowB == (int)Flow.MatchEnds) EndDirs(edgeB, fB, sB, EB, TB, out eB0, out eB1);
                        if (cA > 0 && opt.FlowA == (int)Flow.MatchEnds && (eA0 == null || eA1 == null))
                            Diag.Warn("Side A: Match ends found no side edge at one end (or it is mid-edge); normal to edge used there");
                        if (cB > 0 && opt.FlowB == (int)Flow.MatchEnds && (eB0 == null || eB1 == null))
                            Diag.Warn("Side B: Match ends found no side edge at one end (or it is mid-edge); normal to edge used there");
                        string err;
                        if (opt.Averaged)
                        {
                            System.Collections.Generic.List<BSpl> inner;
                            string noteAvg;
                            if (!FaceSampler.BuildRowsAveraged(fA, sA, cA, flipDirA, fB, sB, cB, flipDirB,
                                                               (Flow)opt.FlowA, eA0, eA1, (Flow)opt.FlowB, eB0, eB1,
                                                               degV, rowA, rowB, uk, EA, TA, EB, TB, opt.AveragedIterations,
                                                               out inner, ref angA, ref curvA, ref angB, ref curvB, ref absA, ref absB,
                                                               out noteAvg, out err))
                            { report = err; return null; }
                            // inner = rows 1..n-1 in grid order: A1 [A2] .. [B2] B1
                            int ix = 0;
                            if (cA >= 1) a1 = inner[ix++];
                            if (cA >= 2) a2 = inner[ix++];
                            int iy = inner.Count - 1;
                            if (cB >= 1) b1 = inner[iy--];
                            if (cB >= 2) b2 = inner[iy--];
                            noteA = noteAvg; noteB = "";
                        }
                        else
                        {
                            if (cA > 0 && !FaceSampler.BuildRows(fA, sA, cA, opt.MagA, opt.Mag2A, flipDirA, degV, (Flow)opt.FlowA, eA0, eA1, rowA, uk, EA, TA, EB,
                                                                 out a1, out a2, ref angA, ref curvA, ref absA, out noteA, out err))
                            { report = "Side A: " + err; return null; }
                            if (cB > 0 && !FaceSampler.BuildRows(fB, sB, cB, opt.MagB, opt.Mag2B, flipDirB, degV, (Flow)opt.FlowB, eB0, eB1, rowB, uk, EB, TB, EA,
                                                                 out b1, out b2, ref angB, ref curvB, ref absB, out noteB, out err))
                            { report = "Side B: " + err; return null; }
                        }
                    }

                    // build the real surface, then measure it like the Surface Analysis G2 test
                    Com.Release(surf);
                    surf = MakeSurface(modeler, rowA, rowB, a1, a2, b1, b2, cA, cB, out rows, out roles);
                    if (surf == null) { report = "CreateBsplineSurface failed"; return null; }
                    mA = cA > 0 ? Seam.Measure(surf, 0.0, rowA, fA, sA) : null;
                    mB = cB > 0 ? Seam.Measure(surf, 1.0, rowB, fB, sB) : null;

                    if (cA == 0 && cB == 0) break;
                    bool inTol = SeamOk(mA, cA, opt) && SeamOk(mB, cB, opt);
                    if (opt.Mode != UMode.Fit || inTol || rowA.N >= opt.FitMaxCVs) break;

                    // FIT: one more CV in U, refit both edges, rebuild rows
                    var T = BSpl.Uniform(rowA.Ord, rowA.N + 1);
                    rowA = Fit.Curve(A, arcA, T, null, out devA);
                    rowB = Fit.Curve(B, arcB, T, null, out devB);
                }

                body = modeler.CreateSheetFromSurface(surf, new double[] { 0, 1, 0, 1 }) as Body2;
                if (body == null) { report = "CreateSheetFromSurface failed"; return null; }
            }
            finally
            {
                Com.Release(surf, sA, fA, sB, fB, modeler);
            }

            sb.AppendFormat("Continuity A: G{0}{1}\n", cA, noteA);
            if (cA > 0) sb.AppendFormat("  Construction estimate A: G1 {0:0.0000} deg, G2 {1:0.00} %\n", angA, curvA);
            sb.AppendFormat("Continuity B: G{0}{1}\n", cB, noteB);
            if (cB > 0) sb.AppendFormat("  Construction estimate B: G1 {0:0.0000} deg, G2 {1:0.00} %\n", angB, curvB);
            LogSeam(sb, "A", mA); LogSeam(sb, "B", mB);
            if (opt.Mode == UMode.Fit) sb.AppendFormat("Edge fit deviation  A: {0:0.0000} mm  B: {1:0.0000} mm\n", devA * 1000, devB * 1000);
            if (forcedA) Diag.Warn("Side A is a curve: G0 only (no face to match)");
            if (forcedB) Diag.Warn("Side B is a curve: G0 only (no face to match)");
            if (mA != null && mA.Failed > 0) Diag.Warn(string.Format("Side A: {0} of {1} seam samples could not be measured", mA.Failed, Seam.Samples));
            if (mB != null && mB.Failed > 0) Diag.Warn(string.Format("Side B: {0} of {1} seam samples could not be measured", mB.Failed, Seam.Samples));

            int nU = rowA.N;
            var grid = new CvGrid { NU = nU, UOrd = rowA.Ord, UKn = (double[])rowA.Kn.Clone() };
            for (int v = 0; v < nV; v++) { grid.Rows.Add((double[])rows[v].Cv.Clone()); grid.Roles.Add(roles[v]); }
            LastGrid = grid;

            sb.AppendFormat("Surface: U degree {0} x {1} CVs, V degree {2} x {3} CVs", rowA.Degree, nU, degV, nV);
            System.Diagnostics.Debug.WriteLine("[BridgeSurface] BUILD: " + sb.ToString().Replace("\n", " | "));

            // --- what the user needs, measured on the built surface (G2 as Surface Analysis / Rhino report it) ---
            var r = new StringBuilder();
            r.AppendFormat("G0   A {0}   B {1}\n", Val(true, devA * 1000, "0.0000", " mm"), Val(true, devB * 1000, "0.0000", " mm"));
            if (cA > 0 || cB > 0)
                r.AppendFormat("G1   A {0}   B {1}\n", Val(Has(mA), Has(mA) ? mA.MaxAngDeg : 0, "0.000", "\u00B0"),
                                                       Val(Has(mB), Has(mB) ? mB.MaxAngDeg : 0, "0.000", "\u00B0"));
            if (cA > 1 || cB > 1)
            {
                bool gA = cA > 1 && Has(mA), gB = cB > 1 && Has(mB);
                r.AppendFormat("G2 absolute   A {0}   B {1}\n", Val(gA, gA ? mA.WorstAbs / 1000 : 0, "0.000000", " /mm"),
                                                                 Val(gB, gB ? mB.WorstAbs / 1000 : 0, "0.000000", " /mm"));
                r.AppendFormat("G2 relative   A {0}   B {1}\n", Val(gA, gA ? mA.WorstRel * 100 : 0, "0.00", " %"),
                                                                 Val(gB, gB ? mB.WorstRel * 100 : 0, "0.00", " %"));
            }
            r.AppendFormat("Surface   U degree {0}, {1} CVs   V degree {2}, {3} CVs", rowA.Degree, nU, degV, nV);
            foreach (var w in Diag.Warnings) r.Append("\nWarning: " + w);
            if (VerboseReport) r.Append("\n\n" + sb);
            report = r.ToString();
            return body;
        }

        private static bool Has(SeamResult m) { return m != null && m.Count > 0; }

        /// <summary>FIT acceptance on the measured seam: G1 angle, and G2 by relative OR absolute deviation.</summary>
        private static bool SeamOk(SeamResult m, int cont, BridgeOptions opt)
        {
            if (cont == 0) return true;
            if (!Has(m)) return false;
            if (m.MaxAngDeg > opt.G1TolDeg) return false;
            if (cont < 2) return true;
            return Math.Abs(m.WorstRel) * 100 <= opt.G2TolPct || Math.Abs(m.WorstAbs) <= opt.G2TolAbs;
        }

        private static void LogSeam(StringBuilder sb, string side, SeamResult m)
        {
            if (m == null) return;
            sb.AppendFormat("  Measured {0}: G1 {1:0.0000} deg, G2 worst at u={2:0.000}: abs {3:0.000000} /mm, rel {4:0.00} % ({5} samples, {6} failed)\n",
                            side, m.MaxAngDeg, m.WorstU, m.WorstAbs / 1000, m.WorstRel * 100, m.Count, m.Failed);
        }

        /// <summary>CV grid A0 [A1] [A2] .. [B2] [B1] B0 (U fastest within each V row) -> SOLIDWORKS surface.</summary>
        private static Surface MakeSurface(Modeler modeler, BSpl rowA, BSpl rowB, BSpl a1, BSpl a2, BSpl b1, BSpl b2, int cA, int cB,
                                           out System.Collections.Generic.List<BSpl> rows,
                                           out System.Collections.Generic.List<CvGrid.Role> roles)
        {
            rows = new System.Collections.Generic.List<BSpl> { rowA };
            roles = new System.Collections.Generic.List<CvGrid.Role> { CvGrid.Role.Boundary };
            if (cA >= 1) { rows.Add(a1); roles.Add(CvGrid.Role.G1); }
            if (cA >= 2) { rows.Add(a2); roles.Add(CvGrid.Role.G2); }
            if (cB >= 2) { rows.Add(b2); roles.Add(CvGrid.Role.G2); }
            if (cB >= 1) { rows.Add(b1); roles.Add(CvGrid.Role.G1); }
            rows.Add(rowB); roles.Add(CvGrid.Role.Boundary);

            int nV = rows.Count, nU = rowA.N;
            var props = new double[]
            {
                Pack(rowA.Ord, nV),     // Uorder, Vorder (single Bezier span in V)
                Pack(nV, nU),           // numV, numU
                Pack(0, 0),             // non-periodic
                Pack(3, 0)              // non-rational
            };
            var vKnots = new double[2 * nV];
            for (int i = nV; i < 2 * nV; i++) vKnots[i] = 1.0;
            var cp = new double[nU * nV * 3];
            for (int v = 0; v < nV; v++) Array.Copy(rows[v].Cv, 0, cp, v * nU * 3, nU * 3);
            return modeler.CreateBsplineSurface(props, rowA.Kn, vKnots, cp) as Surface;
        }

        private static string Val(bool used, double v, string fmt, string unit)
        {
            return used ? v.ToString(fmt) + unit : "-";
        }

        private static string CheckRange(string side, double s0, double s1)
        {
            if (s0 < 0 || s1 > 1 || s1 - s0 < BridgeOptions.MinRange - 1e-12)
                return string.Format("Edge {0}: Start must be below End by at least {1:0.00} (now {2:0.000} to {3:0.000})",
                                     side, BridgeOptions.MinRange, s0, s1);
            return null;
        }

        /// <summary>MatchEnds: side-edge directions at the two ends of a boundary row (u = 0 and u = 1).</summary>
        private static void EndDirs(Edge edge, Face2 face, Surface surf, double[][] E, double[][] T, out double[] d0, out double[] d1)
        {
            d0 = d1 = null;
            int m = E.Length;
            double[] N, d; double k; string why; int fb = 0;
            if (FaceSampler.Frame(face, surf, E[0], T[0], out N, out d, out k, ref fb, out why))
                d0 = FaceSampler.SideEdgeDirection(edge, E[0], T[0], N);
            if (FaceSampler.Frame(face, surf, E[m - 1], T[m - 1], out N, out d, out k, ref fb, out why))
                d1 = FaceSampler.SideEdgeDirection(edge, E[m - 1], T[m - 1], N);
        }

        /// <summary>Two ints packed into one double, first int in the low 32 bits (confirmed in Phase 1).</summary>
        private static double Pack(int lo, int hi)
        {
            long bits = ((long)hi << 32) | (uint)lo;
            return BitConverter.Int64BitsToDouble(bits);
        }
    }
}
