using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

namespace BridgeSurface.Geometry
{
    /// <summary>
    /// Face sampling and G1/G2 row construction, ported from the validated VBA (Phase 3).
    ///   Row 1 (G1): P1 = E + a d          d = unit N x T, pointing away from the face
    ///   Row 2 (G2): P2 = P1 + b d + h N   h = n/(n-1) * II(d,d) * a^2
    ///   a = G1 magnitude * local span / n  (n = V degree)
    ///   b = G2 magnitude * a               (shape only: the end curvature depends on a and h, not b)
    /// </summary>
    /// <summary>Free: averaged placement only - the G1 direction floats in the tangent plane (acts as NormalToEdge otherwise).</summary>
    public enum Flow { NormalToEdge = 0, MatchEnds = 1, TowardsOther = 2, Isocurve = 3, Free = 4 }

    /// <summary>Warnings worth showing the user (the full notes go to the debug log). Cleared per build.</summary>
    public static class Diag
    {
        [ThreadStatic] private static List<string> _w;
        public static List<string> Warnings { get { return _w ?? (_w = new List<string>()); } }
        public static void Clear() { Warnings.Clear(); }
        public static void Warn(string s) { if (!Warnings.Contains(s)) Warnings.Add(s); }
    }

    /// <summary>Measured continuity along one seam of the built bridge (see Seam.Measure).</summary>
    public sealed class SeamResult
    {
        public double MaxAngDeg;   // G1: largest angle between bridge and face normals, degrees
        public double WorstAbs;    // G2 at the worst point: kFace - kBridge, 1/m (signed)
        public double WorstRel;    // G2 at the worst point: (R1 - R2) / (R1 + R2), R1 face, R2 bridge (signed fraction)
        public double WorstU;      // seam parameter of the worst point, 0..1
        public int Count, Failed;
    }

    /// <summary>
    /// Measures the bridge's continuity to an adjacent face on the SURFACE SOLIDWORKS CREATED, the same
    /// way the Surface Analysis add-in's Test G2 Continuity does (benchmarked against Rhino EdgeContinuity):
    ///   - samples along the seam, inset 0.01 % from each end;
    ///   - normal curvature of both surfaces in the crossing direction d = N x T (T = seam tangent);
    ///   - face normal flipped for FaceInSurfaceSense, bridge normal oriented to agree with it;
    ///   - the worst point is the largest absolute deviation; absolute and relative are reported there;
    ///   - relative = (R1 - R2) / (R1 + R2), both flat -> 0, only one flat -> +-1.
    /// G1 is the largest angle between the two normals along the seam.
    /// </summary>
    public static class Seam
    {
        public const int Samples = 50;
        private const double Flat = 1e-12;   // 1/m: treated as infinite radius

        /// <param name="v">0 for the edge A seam, 1 for edge B (the bridge's V boundaries).</param>
        /// <param name="boundary">The boundary row the seam should coincide with (detects u/v order).</param>
        public static SeamResult Measure(Surface bridge, double v, BSpl boundary, Face2 face, Surface faceSurf)
        {
            var r = new SeamResult();
            bool sense = false;
            try { sense = face.FaceInSurfaceSense(); } catch { }

            // u along the edges is expected; confirm on the real surface, swap if SOLIDWORKS has them the other way
            bool swap = false;
            var want = boundary.Eval(0.5);
            var p1 = bridge.Evaluate(0.5, v, 0, 0) as double[];
            var p2 = bridge.Evaluate(v, 0.5, 0, 0) as double[];
            if (p1 != null && p2 != null && V.Dist(Vec(p2, 0), want) < V.Dist(Vec(p1, 0), want)) swap = true;

            double worst = -1;
            for (int j = 0; j < Samples; j++)
            {
                double u = 0.0001 + 0.9998 * j / (Samples - 1);
                var e = (swap ? bridge.Evaluate(v, u, 2, 2) : bridge.Evaluate(u, v, 2, 2)) as double[];
                if (e == null || e.Length < 30) { r.Failed++; continue; }
                var P = Vec(e, 0); var Su = Vec(e, 1); var Suu = Vec(e, 2);
                var Sv = Vec(e, 3); var Suv = Vec(e, 4); var Svv = Vec(e, 6);
                double nl = V.Len(Vec(e, 9));
                if (nl == 0) { r.Failed++; continue; }
                var NB = V.Unit(Vec(e, 9));
                var T = V.Unit(swap ? Sv : Su);

                double[] Nf, d, fSu, fSv; double kf; string why; int fb = 0;
                if (!FaceSampler.FrameEx(face, faceSurf, P, T, out Nf, out d, out kf, out fSu, out fSv, ref fb, out why))
                { r.Failed++; continue; }
                if (sense) { Nf = new[] { -Nf[0], -Nf[1], -Nf[2] }; kf = -kf; }

                // G1
                double c = Math.Min(1.0, Math.Abs(V.Dot(NB, Nf)));
                r.MaxAngDeg = Math.Max(r.MaxAngDeg, Math.Acos(c) * 180.0 / Math.PI);

                // bridge normal curvature in d, relative to a normal pointing the same way as the face's
                double sg = V.Dot(NB, Nf) >= 0 ? 1.0 : -1.0;
                var N = new[] { NB[0] * sg, NB[1] * sg, NB[2] * sg };
                double dn = V.Dot(d, N);
                var dB = V.Unit(new[] { d[0] - dn * N[0], d[1] - dn * N[1], d[2] - dn * N[2] });
                var forms = new[] { V.Dot(Su, Su), V.Dot(Su, Sv), V.Dot(Sv, Sv), V.Dot(Suu, N), V.Dot(Suv, N), V.Dot(Svv, N) };
                double kb = FaceSampler.NormalCurvature(forms, Su, Sv, dB);

                double diff = kf - kb;
                if (Math.Abs(diff) > worst)
                {
                    worst = Math.Abs(diff);
                    r.WorstAbs = diff;
                    r.WorstRel = Relative(kf, kb);
                    r.WorstU = u;
                }
                r.Count++;
            }
            return r;
        }

        /// <summary>(R1 - R2) / (R1 + R2) from curvatures; both flat -> 0, only one flat -> +-1.</summary>
        public static double Relative(double k1, double k2)
        {
            bool f1 = Math.Abs(k1) < Flat, f2 = Math.Abs(k2) < Flat;
            if (f1 && f2) return 0;
            if (f1) return 1;
            if (f2) return -1;
            double R1 = 1 / k1, R2 = 1 / k2, den = R1 + R2;
            if (den == 0) return R1 > R2 ? 1 : -1;
            return (R1 - R2) / den;
        }

        private static double[] Vec(double[] a, int i) { return new[] { a[i * 3], a[i * 3 + 1], a[i * 3 + 2] }; }
    }

    public static class FaceSampler
    {
        /// <summary>g11, g12, g22, L, M, N of the most recent FrameEx evaluation.</summary>
        [ThreadStatic] private static double[] LastForms;

        /// <summary>
        /// Normal curvature of the face in tangent direction dir: II(w,w) / I(w,w), where w = (du, dv)
        /// is dir expressed in the face's parameter directions Su, Sv.
        /// </summary>
        public static double NormalCurvature(double[] forms, double[] Su, double[] Sv, double[] dir)
        {
            double g11 = forms[0], g12 = forms[1], g22 = forms[2];
            double b1 = V.Dot(Su, dir), b2 = V.Dot(Sv, dir);
            double det = g11 * g22 - g12 * g12;
            if (det == 0) return 0;
            double du = (b1 * g22 - b2 * g12) / det;
            double dv = (g11 * b2 - g12 * b1) / det;
            double I = g11 * du * du + 2 * g12 * du * dv + g22 * dv * dv;
            double II = forms[3] * du * du + 2 * forms[4] * du * dv + forms[5] * dv * dv;
            return I > 0 ? II / I : 0;
        }
        public const double FlatKappa = 1e-3;     // 1/m (radius 1 km): below this a face counts as flat for relative curvature

        /// <summary>
        /// The face a side matches. An open surface edge has one face: Flip reverses the bridge's direction
        /// (flipDirection = flip). An edge with two faces (split line, solid edge, knitted interior edge):
        /// Flip picks the other face instead, and the direction is simply "away from that face".
        /// </summary>
        public static Face2 MatchFace(Edge edge, bool flip, out bool flipDirection, out int faceCount)
        {
            flipDirection = false; faceCount = 0;
            var fs = edge.GetTwoAdjacentFaces2() as object[];
            var faces = new List<Face2>();
            if (fs != null) foreach (var f in fs) { var face = f as Face2; if (face != null) faces.Add(face); }
            faceCount = faces.Count;
            if (faces.Count == 0) return null;
            if (faces.Count == 1) { flipDirection = flip; return faces[0]; }
            var pick = flip ? faces[1] : faces[0];
            Com.Release(flip ? faces[0] : faces[1]);
            return pick;
        }

        /// <summary>A side is a curve (G0 only) when its input has no face: a sketch segment, or a curve's edge.</summary>
        public static bool IsCurve(object input)
        {
            var e = input as Edge;
            if (e == null) return true;
            bool dir; int n;
            var f = MatchFace(e, false, out dir, out n);
            Com.Release(f);
            return n == 0;
        }

        /// <summary>Short description of an input, for the debug log.</summary>
        public static string Describe(object input)
        {
            if (input is SketchSegment) return "sketch segment";
            var e = input as Edge;
            if (e == null) return "unknown";
            bool dir; int n;
            var f = MatchFace(e, false, out dir, out n);
            Com.Release(f);
            return string.Format("edge with {0} face{1}", n, n == 1 ? "" : "s");
        }

        /// <summary>First adjacent face of an edge (surface edges have one).</summary>
        public static Face2 AdjacentFace(Edge edge)
        {
            var fs = edge.GetTwoAdjacentFaces2() as object[];
            if (fs == null) return null;
            foreach (var f in fs)
            {
                var face = f as Face2;
                if (face != null) return face;
            }
            return null;
        }

        /// <summary>
        /// Face data at point P: unit normal N (surface sense), d = unit N x T, kappa = II(d,d).
        /// UV lookup routes: Face2.ReverseEvaluate, Surface.ReverseEvaluate, then
        /// Surface.ReverseEvaluate of the face's closest point (needed on trimmed faces).
        /// </summary>
        public static bool Frame(Face2 face, Surface surf, double[] P, double[] T,
                                 out double[] N, out double[] d, out double kappa, ref int fallbacks, out string why)
        {
            double[] su, sv;
            return FrameEx(face, surf, P, T, out N, out d, out kappa, out su, out sv, ref fallbacks, out why);
        }

        /// <summary>Frame plus the face's parameter directions Su, Sv at P (for Isocurve flow).</summary>
        public static bool FrameEx(Face2 face, Surface surf, double[] P, double[] T,
                                   out double[] N, out double[] d, out double kappa,
                                   out double[] SuOut, out double[] SvOut, ref int fallbacks, out string why)
        {
            N = d = null; kappa = 0; why = ""; SuOut = SvOut = null;
            double[] ev = null;
            for (int route = 1; route <= 3 && ev == null; route++)
            {
                double[] uv = null;
                try
                {
                    if (route == 1) uv = face.ReverseEvaluate(P[0], P[1], P[2]) as double[];
                    else if (route == 2) uv = surf.ReverseEvaluate(P[0], P[1], P[2]) as double[];
                    else
                    {
                        var cp = face.GetClosestPointOn(P[0], P[1], P[2]) as double[];
                        if (cp != null) uv = surf.ReverseEvaluate(cp[0], cp[1], cp[2]) as double[];
                    }
                }
                catch (Exception ex) { why += " r" + route + ":" + ex.Message; }
                if (uv == null || uv.Length < 2) { why += " r" + route + ":no uv"; continue; }

                var e = surf.Evaluate(uv[0], uv[1], 2, 2) as double[];
                if (e == null || e.Length < 30) { why += " r" + route + ":Evaluate failed"; continue; }
                double nLen = Math.Sqrt(e[27] * e[27] + e[28] * e[28] + e[29] * e[29]);
                double off = Math.Sqrt((e[0] - P[0]) * (e[0] - P[0]) + (e[1] - P[1]) * (e[1] - P[1]) + (e[2] - P[2]) * (e[2] - P[2]));
                if (nLen > 0 && off < 1e-4) { ev = e; if (route > 1) fallbacks++; }
                else why += string.Format(" r{0}:off {1:0.000} mm", route, off * 1000);
            }
            if (ev == null) return false;

            // vectors (index i + 3j): 0 P, 1 Su, 2 Suu, 3 Sv, 4 Suv, 6 Svv, 9 N
            var Su = Vec(ev, 1); var Suu = Vec(ev, 2); var Sv = Vec(ev, 3);
            var Suv = Vec(ev, 4); var Svv = Vec(ev, 6);
            N = V.Unit(Vec(ev, 9));
            SuOut = Su; SvOut = Sv;
            // first and second fundamental forms at this point (unit N), for NormalCurvature
            LastForms = new[] { V.Dot(Su, Su), V.Dot(Su, Sv), V.Dot(Sv, Sv),
                                V.Dot(Suu, N), V.Dot(Suv, N), V.Dot(Svv, N) };

            var c = V.Cross(N, T);
            if (V.Len(c) == 0) { why = "edge tangent parallel to normal"; return false; }
            d = V.Unit(c);

            double g11 = V.Dot(Su, Su), g12 = V.Dot(Su, Sv), g22 = V.Dot(Sv, Sv);
            double b1 = V.Dot(Su, d), b2 = V.Dot(Sv, d);
            double det = g11 * g22 - g12 * g12;
            if (det == 0) { why = "degenerate surface metric"; return false; }
            double du = (b1 * g22 - b2 * g12) / det;
            double dv = (g11 * b2 - g12 * b1) / det;
            kappa = du * du * V.Dot(Suu, N) + 2 * du * dv * V.Dot(Suv, N) + dv * dv * V.Dot(Svv, N);
            return true;
        }

        /// <summary>+1 if d points away from the face, -1 if into it (a 0.5 mm step that lands on the face is inside).</summary>
        public static double DirectionSign(Face2 face, double[] E, double[] Eo, double[] d)
        {
            const double eps = 0.0005;
            var q = new[] { E[0] + eps * d[0], E[1] + eps * d[1], E[2] + eps * d[2] };
            try
            {
                var c = face.GetClosestPointOn(q[0], q[1], q[2]) as double[];
                if (c != null) return V.Dist(c, q) < eps * 0.25 ? -1.0 : 1.0;
            }
            catch { }
            // fallback: point toward the other edge
            return V.Dot(V.Sub(Eo, E), d) >= 0 ? 1.0 : -1.0;
        }

        /// <summary>
        /// Build rows 1 (and 2 for G2) for one side, fit them onto the U basis, and measure the
        /// seam continuity of the fitted result (tangent-plane angle, normal-curvature mismatch).
        /// E/T are samples of this side's FITTED boundary row; Eo of the other side's.
        /// </summary>
        public static bool BuildRows(Face2 face, Surface surf, int cont, double mag, double mag2, bool flip, int degV,
                                     Flow flow, double[] endDir0, double[] endDir1,
                                     BSpl row0, double[] uk, double[][] E, double[][] T, double[][] Eo,
                                     out BSpl row1, out BSpl row2,
                                     ref double maxAng, ref double maxCurv, ref double maxAbs,
                                     out string note, out string err)
        {
            row1 = row2 = null; note = ""; err = null;
            int m = uk.Length;
            var Q1 = new double[m][]; var Q2 = new double[m][];
            var NN = new double[m][]; var KK = new double[m];
            double[] N, d; double kappa; string why; int fallbacks = 0;

            int im = m / 2;
            double[] Su, Sv;
            if (!FrameEx(face, surf, E[im], T[im], out N, out d, out kappa, out Su, out Sv, ref fallbacks, out why))
            { err = "surface evaluation failed at the middle sample:" + why; return false; }
            double sign = DirectionSign(face, E[im], Eo[im], d) * (flip ? -1 : 1);
            // Isocurve: use whichever parameter direction crosses the edge more steeply, chosen once per side
            bool isoUseV = Math.Abs(V.Dot(V.Unit(Sv), T[im])) < Math.Abs(V.Dot(V.Unit(Su), T[im]));
            int isoFallbacks = 0;

            double kMin = double.MaxValue, kMax = double.MinValue;
            double theta0 = 0, theta1 = 0;   // MatchEnds: flow angle from "normal to edge" at u = 0 / 1
            fallbacks = 0;
            for (int i = 0; i < m; i++)
            {
                if (!FrameEx(face, surf, E[i], T[i], out N, out d, out kappa, out Su, out Sv, ref fallbacks, out why))
                { err = "surface evaluation failed at sample " + i + " of " + m + ":" + why; return false; }
                var forms = LastForms;   // capture now: the MatchEnds end-frame below re-evaluates and overwrites it
                for (int k = 0; k < 3; k++) d[k] *= sign;
                if (flow == Flow.MatchEnds && i == 0) theta0 = AngleInPlane(d, endDir0, N);
                if (flow == Flow.MatchEnds && i == 0)
                {
                    // the end angle at u = 1 needs the frame there: evaluate it now
                    double[] Ne, de; double ke; string w2; int fb2 = 0;
                    if (Frame(face, surf, E[m - 1], T[m - 1], out Ne, out de, out ke, ref fb2, out w2))
                    {
                        for (int k = 0; k < 3; k++) de[k] *= sign;
                        theta1 = AngleInPlane(de, endDir1, Ne);
                    }
                }
                if (flow == Flow.Isocurve)
                {
                    var iso = V.Unit(isoUseV ? Sv : Su);
                    if (Math.Abs(V.Dot(iso, T[i])) > 0.98) isoFallbacks++;        // runs along the edge here: keep normal
                    else d = V.Dot(iso, d) >= 0 ? iso : new[] { -iso[0], -iso[1], -iso[2] };
                }
                else d = ApplyFlow(flow, d, N, E[i], Eo[i], theta0 + (theta1 - theta0) * uk[i]);
                // curvature in the direction actually used (the flow may have rotated d)
                kappa = NormalCurvature(forms, Su, Sv, d);
                NN[i] = N; KK[i] = kappa;
                double a = mag * V.Dist(E[i], Eo[i]) / degV;
                double h = (double)degV / (degV - 1) * kappa * a * a;
                double b = mag2 * a;
                Q1[i] = new double[3]; Q2[i] = new double[3];
                for (int k = 0; k < 3; k++)
                {
                    Q1[i][k] = E[i][k] + a * d[k];
                    Q2[i][k] = Q1[i][k] + b * d[k] + h * N[k];
                }
                kMin = Math.Min(kMin, kappa); kMax = Math.Max(kMax, kappa);
            }

            double dev;
            row1 = Fit.Samples(row0, uk, Q1, out dev);
            if (cont >= 2)
            {
                row2 = Fit.Samples(row0, uk, Q2, out dev);
                note = string.Format(" (face curvature {0:0.000000} to {1:0.000000} 1/mm)", kMin / 1000, kMax / 1000);
            }

            // continuity of the fitted rows at the seam
            for (int i = 0; i < m; i++)
            {
                var p0 = row0.Eval(uk[i]); var p1 = row1.Eval(uk[i]);
                var v1 = V.Sub(p1, p0); double L1 = V.Len(v1);
                if (L1 == 0) continue;
                double nDot = Math.Max(-1, Math.Min(1, V.Dot(v1, NN[i]) / L1));
                maxAng = Math.Max(maxAng, Math.Abs(Math.Asin(nDot)) * 180.0 / Math.PI);
                if (cont >= 2)
                {
                    var v2 = V.Sub(row2.Eval(uk[i]), p1);
                    double kb = (double)(degV - 1) / degV * V.Dot(v2, NN[i]) / (L1 * L1);
                    Curvature(kb, KK[i], ref maxCurv, ref maxAbs);
                }
            }
            if (fallbacks > 0) note += string.Format(" [{0}/{1} samples via snap-to-face]", fallbacks, m);
            if (flow == Flow.MatchEnds)
                note += string.Format(" [flow: match ends {0:0.0} / {1:0.0} deg{2}]", theta0 * 180 / Math.PI, theta1 * 180 / Math.PI,
                                      endDir0 == null || endDir1 == null ? ", no side edge at one end (or the end is mid-edge): normal used there" : "");
            else if (flow == Flow.TowardsOther) note += " [flow: towards other edge]";
            else if (flow == Flow.Isocurve)
                note += string.Format(" [flow: isocurve, face {0} direction{1}]", isoUseV ? "V" : "U",
                                      isoFallbacks > 0 ? string.Format(", {0} samples fell back to normal", isoFallbacks) : "");
            return true;
        }

        /// <summary>Cross-boundary direction for the chosen flow. d is "normal to edge", already pointing away from the face.</summary>
        private static double[] ApplyFlow(Flow flow, double[] d, double[] N, double[] E, double[] Eo, double theta)
        {
            if (flow == Flow.TowardsOther)
            {
                var w = V.Sub(Eo, E);
                double wn = V.Dot(w, N);
                var t = new[] { w[0] - wn * N[0], w[1] - wn * N[1], w[2] - wn * N[2] };
                double L = V.Len(t);
                if (L < 1e-12) return d;
                t = new[] { t[0] / L, t[1] / L, t[2] / L };
                return V.Dot(t, d) > 0.1 ? t : d;   // never aim back into the face
            }
            if (flow == Flow.MatchEnds && theta != 0)
            {
                var nd = V.Cross(N, d);   // in-plane, perpendicular to d
                double c = Math.Cos(theta), s = Math.Sin(theta);
                return V.Unit(new[] { d[0] * c + nd[0] * s, d[1] * c + nd[1] * s, d[2] * c + nd[2] * s });
            }
            return d;
        }

        /// <summary>Signed angle (about N) from d to dir projected into the tangent plane; 0 if dir is missing or unusable.</summary>
        private static double AngleInPlane(double[] d, double[] dir, double[] N)
        {
            if (dir == null) return 0;
            double dn = V.Dot(dir, N);
            var p = new[] { dir[0] - dn * N[0], dir[1] - dn * N[1], dir[2] - dn * N[2] };
            if (V.Len(p) < 1e-12) return 0;
            p = V.Unit(p);
            double a = Math.Atan2(V.Dot(N, V.Cross(d, p)), V.Dot(d, p));
            // must still leave the face: limit to +-80 degrees from normal-to-edge
            double lim = 80.0 * Math.PI / 180.0;
            return Math.Max(-lim, Math.Min(lim, a));
        }

        /// <summary>
        /// MatchEnds: direction continuing the face's neighbouring side edge beyond the vertex at point P
        /// (i.e. pointing out of the face, along the side edge). Found geometrically: of the other edges at
        /// that vertex, the one closest to the face's tangent plane and least parallel to the reference edge.
        /// Returns null if none qualifies.
        /// </summary>
        public const double VertexTol = 1e-5;   // m: a row end this close to a vertex counts as at it

        public static double[] SideEdgeDirection(Edge refEdge, double[] P, double[] T, double[] N)
        {
            Vertex vtx = null;
            object[] edges = null;
            try
            {
                var v0 = refEdge.GetStartVertex() as Vertex;
                var v1 = refEdge.GetEndVertex() as Vertex;
                if (v0 == null || v1 == null) { Com.Release(v0, v1); return null; }
                var p0 = (double[])v0.GetPoint(); var p1 = (double[])v1.GetPoint();
                if (V.Dist(p0, P) <= V.Dist(p1, P)) { vtx = v0; Com.Release(v1); }
                else { vtx = v1; Com.Release(v0); }
                var pv = (double[])vtx.GetPoint();
                // partial edge: this end of the bridge is mid-edge, so there is no side edge to follow
                if (V.Dist(pv, P) > VertexTol) return null;

                edges = vtx.GetEdges() as object[];
                if (edges == null) return null;

                double[] best = null; double bestScore = double.MaxValue;
                foreach (var o in edges)
                {
                    var e = o as Edge;
                    if (e == null) continue;
                    var dir = DirectionAwayFromVertex(e, pv);
                    if (dir == null) continue;
                    double along = Math.Abs(V.Dot(dir, T));      // ~1 for the reference edge itself
                    double offPlane = Math.Abs(V.Dot(dir, N));   // ~0 for edges of this face
                    if (along > 0.98 || offPlane > 0.5) continue;
                    double score = offPlane + 0.1 * along;
                    if (score < bestScore) { bestScore = score; best = dir; }
                }
                if (best == null) return null;
                // the side edge runs INTO the face from the vertex; the bridge continues it OUT of the face
                return new[] { -best[0], -best[1], -best[2] };
            }
            catch { return null; }
            finally
            {
                if (edges != null) foreach (var o in edges) Com.Release(o);
                Com.Release(vtx);
            }
        }

        /// <summary>Unit tangent of edge e at vertex point pv, pointing along the edge away from the vertex.</summary>
        private static double[] DirectionAwayFromVertex(Edge e, double[] pv)
        {
            Curve c = null; Vertex a = null, b = null;
            try
            {
                c = e.GetCurve() as Curve;
                a = e.GetStartVertex() as Vertex;
                b = e.GetEndVertex() as Vertex;
                if (c == null || a == null || b == null) return null;
                var pa = (double[])a.GetPoint(); var pb = (double[])b.GetPoint();
                var other = V.Dist(pa, pv) < V.Dist(pb, pv) ? pb : pa;   // the far end of this edge
                var cp = c.GetClosestPointOn(pv[0], pv[1], pv[2]) as double[];
                if (cp == null || cp.Length < 4) return null;
                var ev = c.Evaluate2(cp[3], 1) as double[];
                if (ev == null || ev.Length < 6) return null;
                var t = V.Unit(new[] { ev[3], ev[4], ev[5] });
                return V.Dot(t, V.Sub(other, pv)) >= 0 ? t : new[] { -t[0], -t[1], -t[2] };
            }
            catch { return null; }
            finally { Com.Release(c, a, b); }
        }

        // ==================================================================
        // Averaged CV placement: fair the whole net, then match G1/G2 per side.
        // ==================================================================

        private sealed class SideFrames
        {
            public double[][] N, Dout, Su, Sv, Forms;
            public double[][] Fdir;    // flow direction per sample (unit, tangent plane, outward); null = Free
            public int Fallbacks;
            public string FlowNote = "";
        }

        /// <summary>Flow direction per sample, same rules as BuildRows. Leaves sf.Fdir null for Free.</summary>
        private static void SideFlow(SideFrames sf, Flow flow, double[] endDir0, double[] endDir1,
                                     double[][] E, double[][] T, double[][] Eo, double[] uk)
        {
            if (flow == Flow.Free) { sf.FlowNote = "free"; return; }
            int m = E.Length, im = m / 2;
            sf.Fdir = new double[m][];
            bool isoUseV = Math.Abs(V.Dot(V.Unit(sf.Sv[im]), T[im])) < Math.Abs(V.Dot(V.Unit(sf.Su[im]), T[im]));
            double theta0 = 0, theta1 = 0;
            if (flow == Flow.MatchEnds)
            {
                theta0 = AngleInPlane(sf.Dout[0], endDir0, sf.N[0]);
                theta1 = AngleInPlane(sf.Dout[m - 1], endDir1, sf.N[m - 1]);
            }
            int isoFb = 0;
            for (int i = 0; i < m; i++)
            {
                var d = sf.Dout[i];
                if (flow == Flow.Isocurve)
                {
                    var iso = V.Unit(isoUseV ? sf.Sv[i] : sf.Su[i]);
                    if (Math.Abs(V.Dot(iso, T[i])) > 0.98) isoFb++;
                    else d = V.Dot(iso, d) >= 0 ? iso : new[] { -iso[0], -iso[1], -iso[2] };
                }
                else d = ApplyFlow(flow, d, sf.N[i], E[i], Eo[i], theta0 + (theta1 - theta0) * uk[i]);
                sf.Fdir[i] = d;
            }
            switch (flow)
            {
                case Flow.NormalToEdge: sf.FlowNote = "normal to edge"; break;
                case Flow.TowardsOther: sf.FlowNote = "towards other edge"; break;
                case Flow.MatchEnds: sf.FlowNote = string.Format("match ends {0:0.0} / {1:0.0} deg", theta0 * 180 / Math.PI, theta1 * 180 / Math.PI); break;
                case Flow.Isocurve: sf.FlowNote = "isocurve, face " + (isoUseV ? "V" : "U") + (isoFb > 0 ? string.Format(", {0} samples normal", isoFb) : ""); break;
            }
        }

        /// <summary>Per-sample frames for one side: N, outward normal-to-edge d, Su, Sv, fundamental forms.</summary>
        private static bool SampleSide(Face2 face, Surface surf, bool flip, double[][] E, double[][] T, double[][] Eo,
                                       out SideFrames sf, out string err)
        {
            int m = E.Length;
            sf = new SideFrames { N = new double[m][], Dout = new double[m][], Su = new double[m][], Sv = new double[m][], Forms = new double[m][] };
            err = null;
            double[] N, d, Su, Sv; double k; string why; int fb = 0;
            int im = m / 2;
            if (!FrameEx(face, surf, E[im], T[im], out N, out d, out k, out Su, out Sv, ref fb, out why))
            { err = "surface evaluation failed at the middle sample:" + why; return false; }
            double sign = DirectionSign(face, E[im], Eo[im], d) * (flip ? -1 : 1);
            fb = 0;
            for (int i = 0; i < m; i++)
            {
                if (!FrameEx(face, surf, E[i], T[i], out N, out d, out k, out Su, out Sv, ref fb, out why))
                { err = "surface evaluation failed at sample " + i + " of " + m + ":" + why; return false; }
                sf.N[i] = N; sf.Su[i] = Su; sf.Sv[i] = Sv; sf.Forms[i] = LastForms;
                sf.Dout[i] = new[] { d[0] * sign, d[1] * sign, d[2] * sign };
            }
            sf.Fallbacks = fb;
            return true;
        }

        /// <summary>
        /// Builds all interior rows together. Q[i][k]: sample i along U, row k = 0..n across V.
        /// Rows 1 (G1) and 2 (G2) from each constrained side are matched to that side's face; any
        /// rows in between are free. Returns interior rows 1..n-1 fitted onto row0's U basis.
        /// </summary>
        public static bool BuildRowsAveraged(Face2 faceA, Surface surfA, int contA, bool flipA,
                                             Face2 faceB, Surface surfB, int contB, bool flipB,
                                             Flow flowA, double[] eA0, double[] eA1,
                                             Flow flowB, double[] eB0, double[] eB1,
                                             int degV, BSpl rowA0, BSpl rowB0, double[] uk,
                                             double[][] EA, double[][] TA, double[][] EB, double[][] TB,
                                             int iterations,
                                             out List<BSpl> inner,
                                             ref double angA, ref double curvA, ref double angB, ref double curvB,
                                             ref double absA, ref double absB,
                                             out string note, out string err)
        {
            inner = null; note = ""; err = null;
            int m = uk.Length, n = degV;
            SideFrames fa = null, fbS = null;
            if (contA > 0 && !SampleSide(faceA, surfA, flipA, EA, TA, EB, out fa, out err)) { err = "Side A: " + err; return false; }
            if (contB > 0 && !SampleSide(faceB, surfB, flipB, EB, TB, EA, out fbS, out err)) { err = "Side B: " + err; return false; }
            if (fa != null) SideFlow(fa, flowA, eA0, eA1, EA, TA, EB, uk);
            if (fbS != null) SideFlow(fbS, flowB, eB0, eB1, EB, TB, EA, uk);

            // --- every constrained side has a flow: equal CV spacing along the flow lines ---
            bool alongFlow = (contA == 0 || fa.Fdir != null) && (contB == 0 || fbS.Fdir != null) && (contA + contB > 0);
            if (alongFlow)
            {
                int fallbacks;
                var Qf = EqualAlongFlow(fa, contA, fbS, contB, n, EA, EB, out fallbacks);
                if (fallbacks > 0)
                    Diag.Warn(string.Format("Averaged: {0} of {1} CV columns could not be spaced exactly evenly (closest spacing used)", fallbacks, m));
                inner = new List<BSpl>();
                for (int k = 1; k < n; k++)
                {
                    var pts = new double[m][];
                    for (int i = 0; i < m; i++) pts[i] = Qf[i][k];
                    double dev;
                    inner.Add(Fit.Samples(rowA0, uk, pts, out dev));
                }
                if (contA > 0) CheckSeam(rowA0, inner[0], contA >= 2 ? inner[1] : null, uk, fa, n, ref angA, ref curvA, ref absA);
                if (contB > 0) CheckSeam(rowB0, inner[n - 2], contB >= 2 ? inner[n - 3] : null, uk, fbS, n, ref angB, ref curvB, ref absB);
                note = string.Format(" [averaged along flow: equal CV spacing per column; flow A: {0}, B: {1}{2}]",
                                     fa != null ? fa.FlowNote : "-", fbS != null ? fbS.FlowNote : "-",
                                     fallbacks > 0 ? string.Format("; {0}/{1} columns could not be evened (flows diverge), used span / n", fallbacks, m) : "");
                return true;
            }

            // --- start: straight blend between the boundary rows ---
            var Q = new double[m][][];
            for (int i = 0; i < m; i++)
            {
                Q[i] = new double[n + 1][];
                for (int k = 0; k <= n; k++)
                {
                    double t = (double)k / n;
                    Q[i][k] = new[] { EA[i][0] + t * (EB[i][0] - EA[i][0]),
                                      EA[i][1] + t * (EB[i][1] - EA[i][1]),
                                      EA[i][2] + t * (EB[i][2] - EA[i][2]) };
                }
            }

            // --- average, then match; finish on a match so G1/G2 hold exactly ---
            for (int it = 0; it < iterations; it++)
            {
                Smooth(Q, m, n, EA, EB);
                if (contA > 0) Match(Q, fa, contA, n, 0, +1, EA, EB);
                if (contB > 0) Match(Q, fbS, contB, n, n, -1, EB, EA);
            }

            // --- fit interior rows ---
            inner = new List<BSpl>();
            for (int k = 1; k < n; k++)
            {
                var pts = new double[m][];
                for (int i = 0; i < m; i++) pts[i] = Q[i][k];
                double dev;
                inner.Add(Fit.Samples(rowA0, uk, pts, out dev));
            }

            // --- seam check on the fitted rows ---
            if (contA > 0) CheckSeam(rowA0, inner[0], contA >= 2 ? inner[1] : null, uk, fa, n, ref angA, ref curvA, ref absA);
            if (contB > 0) CheckSeam(rowB0, inner[n - 2], contB >= 2 ? inner[n - 3] : null, uk, fbS, n, ref angB, ref curvB, ref absB);

            note = string.Format(" [averaged placement, {0} iterations; flow A: {1}, B: {2}]", iterations,
                                 fa != null ? fa.FlowNote : "-", fbS != null ? fbS.FlowNote : "-");
            return true;
        }

        /// <summary>
        /// One smoothing pass over the interior rows.
        ///   Across V: Laplacian on positions (evens out row spacing).
        ///   Along U:  Laplacian on the OFFSET from the straight A-B blend, not on positions. The blend
        ///             already carries the edges' own curvature along U, so only deviations are averaged:
        ///             removes wiggles without shrinking rows toward the chord (which made rows cross).
        ///   At the U ends: offset extrapolated linearly from the next two columns (plain one-sided
        ///             averaging pulled the end columns inward; no smoothing left them hooking).
        /// </summary>
        private static void Smooth(double[][][] Q, int m, int n, double[][] EA, double[][] EB)
        {
            var next = new double[m][][];
            for (int i = 0; i < m; i++)
            {
                next[i] = new double[n + 1][];
                for (int k = 0; k <= n; k++)
                {
                    if (k == 0 || k == n) { next[i][k] = Q[i][k]; continue; }
                    double t = (double)k / n;
                    var p = new double[3];
                    for (int c = 0; c < 3; c++)
                    {
                        double vAvg = 0.5 * (Q[i][k - 1][c] + Q[i][k + 1][c]);
                        double baseC = EA[i][c] + t * (EB[i][c] - EA[i][c]);
                        double uTerm;
                        if (m < 3) uTerm = Q[i][k][c];
                        else if (i == 0 || i == m - 1)
                        {
                            // End column: continue the offset trend of the next two columns in
                            // (linear extrapolation), so the end CV lines up with its neighbours
                            // instead of hooking. Works on offsets, so it doesn't pull inward.
                            int s = i == 0 ? 1 : -1;
                            int i1 = i + s, i2 = i + 2 * s;
                            double b1 = EA[i1][c] + t * (EB[i1][c] - EA[i1][c]);
                            double b2 = EA[i2][c] + t * (EB[i2][c] - EA[i2][c]);
                            double off1 = Q[i1][k][c] - b1, off2 = Q[i2][k][c] - b2;
                            uTerm = baseC + (2 * off1 - off2);
                        }
                        else
                        {
                            double bPrev = EA[i - 1][c] + t * (EB[i - 1][c] - EA[i - 1][c]);
                            double bNext = EA[i + 1][c] + t * (EB[i + 1][c] - EA[i + 1][c]);
                            double offAvg = 0.5 * ((Q[i - 1][k][c] - bPrev) + (Q[i + 1][k][c] - bNext));
                            uTerm = baseC + offAvg;
                        }
                        p[c] = 0.5 * vAvg + 0.5 * uTerm;
                    }
                    next[i][k] = p;
                }
            }
            for (int i = 0; i < m; i++) Q[i] = next[i];
        }

        /// <summary>
        /// Match one side's rows to its face. k0 = boundary row index, dir = +1 (side A, rows 1,2)
        /// or -1 (side B, rows n-1, n-2). G1: row 1 into the tangent plane, leaving the face.
        /// G2: row 2's height above the tangent plane set from the face's normal curvature
        /// in row 1's actual direction: h = n/(n-1) * kappa * a^2.
        /// </summary>
        private static void Match(double[][][] Q, SideFrames f, int cont, int n, int k0, int dir, double[][] E, double[][] Eo)
        {
            int m = Q.Length;
            for (int i = 0; i < m; i++)
            {
                var P0 = Q[i][k0];
                var N = f.N[i];
                var P1 = Q[i][k0 + dir];
                // G1: into the tangent plane
                var w = V.Sub(P1, P0);
                double wn = V.Dot(w, N);
                w = new[] { w[0] - wn * N[0], w[1] - wn * N[1], w[2] - wn * N[2] };
                // must leave the face: at least 5% of the local span along the outward normal-to-edge
                double minOut = 0.05 * V.Dist(E[i], Eo[i]) / n;
                if (f.Fdir != null)
                {
                    // flow sets the direction; averaging only sets the length (no sliding along the edge)
                    var F = f.Fdir[i];
                    double len = Math.Max(V.Dot(w, F), minOut);
                    w = new[] { F[0] * len, F[1] * len, F[2] * len };
                }
                else
                {
                    double out1 = V.Dot(w, f.Dout[i]);
                    if (out1 < minOut) for (int c = 0; c < 3; c++) w[c] += (minOut - out1) * f.Dout[i][c];
                }
                P1 = new[] { P0[0] + w[0], P0[1] + w[1], P0[2] + w[2] };
                Q[i][k0 + dir] = P1;

                if (cont >= 2)
                {
                    double a = V.Len(w);
                    if (a <= 0) continue;
                    var dirU = new[] { w[0] / a, w[1] / a, w[2] / a };
                    double kappa = NormalCurvature(f.Forms[i], f.Su[i], f.Sv[i], dirU);
                    double h = (double)n / (n - 1) * kappa * a * a;
                    var P2 = Q[i][k0 + 2 * dir];
                    double cur = V.Dot(V.Sub(P2, P1), N);
                    for (int c = 0; c < 3; c++) P2[c] += (h - cur) * N[c];
                    Q[i][k0 + 2 * dir] = P2;
                }
            }
        }

        /// <summary>
        /// Averaged placement when every constrained side has a flow. Each CV column is solved on its own:
        /// a side's rows lie on that side's flow line (P1 = P0 + L F, P2 = P1 + b F + h N), and one leg
        /// length L is found so that every leg of the column is the same length, including the free leg
        /// between the last A row and the last B row: |lastA(L) - lastB(L)| = L (bisection).
        /// The G2 row keeps h = n/(n-1) kappa L^2 for curvature; its in-plane step b is chosen so the
        /// 3D leg P1-P2 is also L. Returns Q[i][k], k = 0..n across V.
        /// </summary>
        private static double[][][] EqualAlongFlow(SideFrames fa, int contA, SideFrames fb, int contB, int n,
                                                   double[][] EA, double[][] EB, out int fallbacks)
        {
            int m = EA.Length;
            fallbacks = 0;
            var Q = new double[m][][];
            for (int i = 0; i < m; i++)
            {
                double D = V.Dist(EA[i], EB[i]);
                int col = i;
                Func<double, double> gap = len =>
                    V.Dist(SideLegs(fa, col, EA[col], contA, n, len)[contA], SideLegs(fb, col, EB[col], contB, n, len)[contB]) - len;

                // gap(L) is not monotonic: as L grows the two sides' rows approach, meet, then pass each
                // other and the gap grows again. Scan up from 0 for the FIRST sign change, then bisect it.
                double L;
                double lo = 0, hi = -1;
                double bestT = D / n, bestGap = double.MaxValue;   // closest to even, if no exact answer exists
                const int steps = 200;                       // scan 0 .. 2D
                for (int s = 1; s <= steps && D > 0; s++)
                {
                    double t = 2.0 * D * s / steps;
                    double g = gap(t);
                    if (g <= 0) { hi = t; break; }
                    if (g < bestGap) { bestGap = g; bestT = t; }
                    lo = t;
                }
                // no exact answer (e.g. a G0 curve side with the other side's flow running across it):
                // take the leg length whose middle leg comes closest to the others
                if (hi < 0) { L = bestT; fallbacks++; }
                else
                {
                    for (int it = 0; it < 60; it++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (gap(mid) > 0) lo = mid; else hi = mid;
                    }
                    L = 0.5 * (lo + hi);
                }

                var a = SideLegs(fa, i, EA[i], contA, n, L);
                var b = SideLegs(fb, i, EB[i], contB, n, L);
                Q[i] = new double[n + 1][];
                for (int k = 0; k <= contA; k++) Q[i][k] = a[k];            // A0 A1 A2
                for (int k = 0; k <= contB; k++) Q[i][n - k] = b[k];        // B0 B1 B2 from the far end
            }
            return Q;
        }

        /// <summary>Points 0..cont of one side's column: the edge point, then rows along the flow with leg length L.</summary>
        private static double[][] SideLegs(SideFrames f, int i, double[] P0, int cont, int n, double L)
        {
            var pts = new double[cont + 1][];
            pts[0] = P0;
            if (cont == 0) return pts;
            var F = f.Fdir[i]; var N = f.N[i];
            pts[1] = new[] { P0[0] + L * F[0], P0[1] + L * F[1], P0[2] + L * F[2] };
            if (cont >= 2)
            {
                double kappa = NormalCurvature(f.Forms[i], f.Su[i], f.Sv[i], F);
                double h = (double)n / (n - 1) * kappa * L * L;
                double bIn = Math.Sqrt(Math.Max(L * L - h * h, 0.0025 * L * L));   // keep at least 5% of L in-plane
                var P1 = pts[1];
                pts[2] = new[] { P1[0] + bIn * F[0] + h * N[0], P1[1] + bIn * F[1] + h * N[1], P1[2] + bIn * F[2] + h * N[2] };
            }
            return pts;
        }

        /// <summary>
        /// Curvature mismatch at one seam sample, bridge kb vs face kf (both 1/m, same direction).
        /// Absolute: |kb - kf|. Relative: |kb - kf| / max(|kb|, |kf|), skipped where both are ~flat
        /// (there only the absolute value means anything).
        /// </summary>
        private static void Curvature(double kb, double kf, ref double maxRelPct, ref double maxAbs)
        {
            double d = Math.Abs(kb - kf);
            maxAbs = Math.Max(maxAbs, d);
            double den = Math.Max(Math.Abs(kb), Math.Abs(kf));
            if (den > FlatKappa) maxRelPct = Math.Max(maxRelPct, d / den * 100);
        }

        /// <summary>Seam continuity of fitted rows; curvature compared in each row's own direction.</summary>
        private static void CheckSeam(BSpl row0, BSpl row1, BSpl row2, double[] uk, SideFrames f, int n,
                                      ref double maxAng, ref double maxCurv, ref double maxAbs)
        {
            for (int i = 0; i < uk.Length; i++)
            {
                var p0 = row0.Eval(uk[i]); var p1 = row1.Eval(uk[i]);
                var v1 = V.Sub(p1, p0); double L1 = V.Len(v1);
                if (L1 == 0) continue;
                var N = f.N[i];
                double nDot = Math.Max(-1, Math.Min(1, V.Dot(v1, N) / L1));
                maxAng = Math.Max(maxAng, Math.Abs(Math.Asin(nDot)) * 180.0 / Math.PI);
                if (row2 == null) continue;
                var dirU = V.Unit(new[] { v1[0] - V.Dot(v1, N) * N[0], v1[1] - V.Dot(v1, N) * N[1], v1[2] - V.Dot(v1, N) * N[2] });
                double kFace = NormalCurvature(f.Forms[i], f.Su[i], f.Sv[i], dirU);
                var v2 = V.Sub(row2.Eval(uk[i]), p1);
                double kb = (double)(n - 1) / n * V.Dot(v2, N) / (L1 * L1);
                Curvature(kb, kFace, ref maxCurv, ref maxAbs);
            }
        }

        private static double[] Vec(double[] a, int i) { return new[] { a[i * 3], a[i * 3 + 1], a[i * 3 + 2] }; }
    }
}
