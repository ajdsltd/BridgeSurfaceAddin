using System;
using SolidWorks.Interop.sldworks;

namespace BridgeSurface.Geometry
{
    /// <summary>
    /// Reads an edge as an exact, oriented, normalised B-spline.
    /// Chain validated in the VBA phase:
    ///   GetCurve -> snap tolerant vertices (GetClosestPointOn) -> CreateTrimmedCurve2
    ///   -> GetBCurveParams5(false, true, true, false), oriented by CV0 vs start vertex.
    /// Caller must tighten the B-curve output tolerance first (see ReadTolerance).
    /// </summary>
    public static class EdgeReader
    {
        public const int TolIdBsCurve = 0;          // confirmed: default 0.01 m
        public const int TolIdBsCurveNonRat = 1;    // confirmed: default 0.001 m
        public const double ReadTolerance = 1e-6;   // 0.001 mm

        public static BSpl Read(Edge edge, out string error)
        {
            error = null;
            Curve crv = null, tc = null;
            Vertex vS = null, vE = null;
            SplineParamData spd = null;
            try
            {
                crv = edge.GetCurve() as Curve;
                if (crv == null) { error = "edge has no curve"; return null; }

                vS = edge.GetStartVertex() as Vertex;
                vE = edge.GetEndVertex() as Vertex;
                if (vS == null || vE == null) { error = "closed edges are not supported"; return null; }

                var pS = (double[])vS.GetPoint();
                var pE = (double[])vE.GetPoint();
                var cS = (double[])crv.GetClosestPointOn(pS[0], pS[1], pS[2]);
                var cE = (double[])crv.GetClosestPointOn(pE[0], pE[1], pE[2]);

                tc = crv.CreateTrimmedCurve2(cS[0], cS[1], cS[2], cE[0], cE[1], cE[2]);
                if (tc == null) tc = crv.CreateTrimmedCurve2(cE[0], cE[1], cE[2], cS[0], cS[1], cS[2]);
                if (tc == null) { error = "CreateTrimmedCurve2 failed"; return null; }

                spd = tc.GetBCurveParams5(false, true, true, false) as SplineParamData;
                if (spd == null) { error = "GetBCurveParams5 failed"; return null; }
                if (spd.Dimension != 3) { error = "rational curve data"; return null; }

                object cpsObj, ktsObj;
                if (!spd.GetControlPoints(out cpsObj) || !spd.GetKnotPoints(out ktsObj))
                {
                    error = "could not get control points / knots"; return null;
                }
                var cps = (double[])cpsObj;
                var kts = (double[])ktsObj;

                var b = new BSpl { Ord = spd.Order, N = spd.ControlPointsCount };
                b.Cv = new double[b.N * 3];
                Array.Copy(cps, b.Cv, b.N * 3);
                b.Kn = new double[b.N + b.Ord];
                double k0 = kts[0], k1 = kts[b.N + b.Ord - 1];
                for (int i = 0; i < b.Kn.Length; i++) b.Kn[i] = (kts[i] - k0) / (k1 - k0);

                // trimmed curves can come back reversed
                if (V.Dist(b.CvAt(0), pS) > V.Dist(b.CvAt(0), pE)) b.Reverse();
                return b;
            }
            finally
            {
                Com.Release(spd, tc, crv, vS, vE);
            }
        }

        // ==================================================================
        // Sketch segments (2D and 3D sketches) as bridge inputs, G0 only.
        // SketchSegment.GetCurve comes back in SKETCH space for 2D sketches and model space for 3D
        // sketches (confirmed 20 Oct). Rather than rely on that, both candidate end-point sets are
        // snapped to the curve and the one that lands on it wins; the diag text (debug log) says which.
        // Sketch lines are infinite underneath, so the segment's own sketch points set the ends.
        // ==================================================================

        /// <summary>Edge or sketch segment -> oriented, normalised B-spline (caller tightens tolerance).</summary>
        public static BSpl ReadAny(SldWorks app, object input, string side, System.Text.StringBuilder diag, out string error)
        {
            var e = input as Edge;
            if (e != null) return Read(e, out error);
            var seg = input as SketchSegment;
            if (seg != null) return ReadSegment(app, seg, side, diag, out error);
            error = "not an edge or a sketch curve";
            return null;
        }

        public static BSpl ReadSegment(SldWorks app, SketchSegment seg, string side, System.Text.StringBuilder diag, out string error)
        {
            error = null;
            Curve crv = null, tc = null;
            Sketch sk = null;
            SplineParamData spd = null;
            MathTransform toModel = null;
            try
            {
                crv = seg.GetCurve() as Curve;
                if (crv == null) { error = "sketch segment has no curve"; return null; }
                int type = seg.GetType();
                sk = seg.GetSketch();
                bool is3D = sk != null && sk.Is3D();

                // ends as the curve reports them
                double t0, t1; bool closed, periodic;
                bool okP = crv.GetEndParams(out t0, out t1, out closed, out periodic);
                double[] cS = okP ? Pt(crv.Evaluate2(t0, 0)) : null;
                double[] cE = okP ? Pt(crv.Evaluate2(t1, 0)) : null;

                // ends from the segment's own sketch points (sketch space), and those mapped to model space
                double[] kS, kE;
                SketchEnds(seg, out kS, out kE);
                double[] mS = null, mE = null;
                if (kS != null && sk != null)
                {
                    var xf = sk.ModelToSketchTransform as MathTransform;
                    toModel = xf != null ? xf.Inverse() as MathTransform : null;
                    Com.Release(xf);
                    if (toModel != null) { mS = Apply(app, toModel, kS); mE = Apply(app, toModel, kE); }
                }

                diag.AppendFormat("{0}: sketch {1} in a {2} sketch; curve trimmed {3}, params {4} {5:0.######} .. {6:0.######}{7}\n",
                                  side, TypeName(type), is3D ? "3D" : "2D", crv.IsTrimmedCurve(), okP ? "ok" : "FAILED", t0, t1,
                                  closed ? " (closed)" : "");
                diag.AppendFormat("  curve ends     {0} / {1}\n", Mm(cS), Mm(cE));
                diag.AppendFormat("  sketch points  {0} / {1}  (sketch space)\n", Mm(kS), Mm(kE));
                diag.AppendFormat("  -> model space {0} / {1}\n", Mm(mS), Mm(mE));

                if (kS == null) { error = "unsupported sketch segment type (" + TypeName(type) + ")"; return null; }
                if (closed && V.Dist(kS, kE) < 1e-9) { error = "closed sketch curves are not supported"; return null; }

                // which space is the curve in? whichever pair of end points actually lies on it
                double offModel = mS != null ? Math.Max(OffCurve(crv, mS), OffCurve(crv, mE)) : double.MaxValue;
                double offSketch = Math.Max(OffCurve(crv, kS), OffCurve(crv, kE));
                bool curveInModel = offModel <= offSketch;
                diag.AppendFormat("  end points off the curve: model-space {0:0.000000} mm, sketch-space {1:0.000000} mm -> curve is in {2} space\n",
                                  offModel * 1000, offSketch * 1000, curveInModel ? "MODEL" : "SKETCH");
                double[] pS = curveInModel ? mS : kS, pE = curveInModel ? mE : kE;

                // same chain as edges: snap, trim, read, orient
                var qS = (double[])crv.GetClosestPointOn(pS[0], pS[1], pS[2]);
                var qE = (double[])crv.GetClosestPointOn(pE[0], pE[1], pE[2]);
                tc = crv.CreateTrimmedCurve2(qS[0], qS[1], qS[2], qE[0], qE[1], qE[2]);
                if (tc == null) tc = crv.CreateTrimmedCurve2(qE[0], qE[1], qE[2], qS[0], qS[1], qS[2]);
                if (tc == null) { error = "CreateTrimmedCurve2 failed on the sketch curve"; return null; }
                spd = tc.GetBCurveParams5(false, true, true, false) as SplineParamData;
                if (spd == null) { error = "GetBCurveParams5 failed on the sketch curve"; return null; }
                if (spd.Dimension != 3) { error = "rational sketch curve data"; return null; }
                object cpsObj, ktsObj;
                if (!spd.GetControlPoints(out cpsObj) || !spd.GetKnotPoints(out ktsObj)) { error = "could not get control points / knots"; return null; }
                var cps = (double[])cpsObj; var kts = (double[])ktsObj;
                var b = new BSpl { Ord = spd.Order, N = spd.ControlPointsCount };
                b.Cv = new double[b.N * 3];
                Array.Copy(cps, b.Cv, b.N * 3);
                b.Kn = new double[b.N + b.Ord];
                double k0 = kts[0], k1 = kts[b.N + b.Ord - 1];
                for (int i = 0; i < b.Kn.Length; i++) b.Kn[i] = (kts[i] - k0) / (k1 - k0);
                if (V.Dist(b.CvAt(0), pS) > V.Dist(b.CvAt(0), pE)) b.Reverse();

                // curve in sketch space: move the CVs to model space (exact for a non-rational B-spline)
                if (!curveInModel)
                {
                    if (toModel == null) { error = "sketch curve is in sketch space but the sketch transform is unavailable"; return null; }
                    for (int i = 0; i < b.N; i++)
                    {
                        var p = Apply(app, toModel, b.CvAt(i));
                        b.Cv[i * 3] = p[0]; b.Cv[i * 3 + 1] = p[1]; b.Cv[i * 3 + 2] = p[2];
                    }
                    diag.Append("  CVs transformed sketch -> model\n");
                }
                diag.AppendFormat("  read: order {0}, {1} CVs\n", b.Ord, b.N);
                return b;
            }
            finally
            {
                Com.Release(spd, tc, crv, toModel, sk);
            }
        }

        private static void SketchEnds(SketchSegment seg, out double[] s, out double[] e)
        {
            s = e = null;
            object a = null, b = null;
            object[] pts = null;
            try
            {
                var ln = seg as SketchLine;
                var arc = seg as SketchArc;
                var spl = seg as SketchSpline;
                if (ln != null) { a = ln.GetStartPoint2(); b = ln.GetEndPoint2(); }
                else if (arc != null) { a = arc.GetStartPoint2(); b = arc.GetEndPoint2(); }
                else if (spl != null)
                {
                    pts = spl.GetPoints2() as object[];
                    if (pts != null && pts.Length >= 2) { a = pts[0]; b = pts[pts.Length - 1]; }
                }
                var pa = a as SketchPoint; var pb = b as SketchPoint;
                if (pa != null && pb != null) { s = new[] { pa.X, pa.Y, pa.Z }; e = new[] { pb.X, pb.Y, pb.Z }; }
            }
            finally
            {
                if (pts != null) foreach (var o in pts) Com.Release(o);
                else Com.Release(a, b);
            }
        }

        private static double[] Apply(SldWorks app, MathTransform xf, double[] p)
        {
            var mu = app.GetMathUtility() as MathUtility;
            var mp = mu.CreatePoint(new[] { p[0], p[1], p[2] }) as MathPoint;
            var mq = mp.MultiplyTransform(xf) as MathPoint;
            var r = (double[])mq.ArrayData;
            Com.Release(mq, mp, mu);
            return new[] { r[0], r[1], r[2] };
        }

        private static double OffCurve(Curve c, double[] p)
        {
            var q = c.GetClosestPointOn(p[0], p[1], p[2]) as double[];
            return q == null ? double.MaxValue : V.Dist(new[] { q[0], q[1], q[2] }, p);
        }

        private static double[] Pt(object ev)
        {
            var a = ev as double[];
            return a == null || a.Length < 3 ? null : new[] { a[0], a[1], a[2] };
        }

        private static string Mm(double[] p)
        {
            return p == null ? "-" : string.Format("({0:0.000}, {1:0.000}, {2:0.000}) mm", p[0] * 1000, p[1] * 1000, p[2] * 1000);
        }

        private static string TypeName(int t)
        {
            switch (t) { case 0: return "line"; case 1: return "arc"; case 2: return "ellipse"; case 3: return "spline"; case 5: return "parabola"; default: return "type " + t; }
        }
    }
}
