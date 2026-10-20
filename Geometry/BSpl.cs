using System;

namespace BridgeSurface.Geometry
{
    /// <summary>
    /// Non-rational B-spline curve in 3D. Knots normalised to [0,1].
    /// Pure math: no SOLIDWORKS types, so it can be unit-tested on its own.
    /// </summary>
    public sealed class BSpl
    {
        public int Ord;        // order = degree + 1
        public int N;          // number of CVs
        public double[] Kn;    // N + Ord knots
        public double[] Cv;    // N * 3 (x,y,z)

        public int Degree { get { return Ord - 1; } }

        public BSpl Clone()
        {
            return new BSpl { Ord = Ord, N = N, Kn = (double[])Kn.Clone(), Cv = (double[])Cv.Clone() };
        }

        public double[] CvAt(int i) { return new[] { Cv[i * 3], Cv[i * 3 + 1], Cv[i * 3 + 2] }; }

        /// <summary>Reverse direction (CVs and knots, assuming [0,1] knots).</summary>
        public void Reverse()
        {
            for (int i = 0; i < N / 2; i++)
                for (int k = 0; k < 3; k++)
                {
                    double t = Cv[i * 3 + k];
                    Cv[i * 3 + k] = Cv[(N - 1 - i) * 3 + k];
                    Cv[(N - 1 - i) * 3 + k] = t;
                }
            int nk = N + Ord;
            var nkn = new double[nk];
            for (int i = 0; i < nk; i++) nkn[i] = 1.0 - Kn[nk - 1 - i];
            Kn = nkn;
        }

        public bool SameBasis(BSpl o)
        {
            if (Ord != o.Ord || N != o.N) return false;
            for (int i = 0; i < Kn.Length; i++)
                if (Math.Abs(Kn[i] - o.Kn[i]) > 1e-9) return false;
            return true;
        }

        // ------------------------------------------------------------------
        // Evaluation (NURBS Book A2.1 / A2.2)
        // ------------------------------------------------------------------

        public int FindSpan(double u)
        {
            int n = N - 1, p = Ord - 1;
            if (u >= Kn[n + 1]) return n;
            if (u <= Kn[p]) return p;
            int lo = p, hi = n + 1, mid = (lo + hi) / 2;
            while (u < Kn[mid] || u >= Kn[mid + 1])
            {
                if (u < Kn[mid]) hi = mid; else lo = mid;
                mid = (lo + hi) / 2;
            }
            return mid;
        }

        /// <summary>Non-zero basis functions at u: Nb[0..p] for CVs span-p .. span.</summary>
        public void BasisFuns(int span, double u, double[] Nb)
        {
            int p = Ord - 1;
            var left = new double[p + 1];
            var right = new double[p + 1];
            Nb[0] = 1.0;
            for (int j = 1; j <= p; j++)
            {
                left[j] = u - Kn[span + 1 - j];
                right[j] = Kn[span + j] - u;
                double saved = 0.0;
                for (int r = 0; r < j; r++)
                {
                    double temp = Nb[r] / (right[r + 1] + left[j - r]);
                    Nb[r] = saved + right[r + 1] * temp;
                    saved = left[j - r] * temp;
                }
                Nb[j] = saved;
            }
        }

        /// <summary>Full basis row (length N) at u.</summary>
        public double[] BasisRow(double u)
        {
            var row = new double[N];
            if (u <= 0) { row[0] = 1; return row; }
            if (u >= 1) { row[N - 1] = 1; return row; }
            int span = FindSpan(u);
            var Nb = new double[Ord];
            BasisFuns(span, u, Nb);
            for (int i = 0; i < Ord; i++) row[span - Ord + 1 + i] = Nb[i];
            return row;
        }

        public double[] Eval(double u)
        {
            var pt = new double[3];
            if (u <= 0) { Array.Copy(Cv, 0, pt, 0, 3); return pt; }
            if (u >= 1) { Array.Copy(Cv, (N - 1) * 3, pt, 0, 3); return pt; }
            int span = FindSpan(u);
            var Nb = new double[Ord];
            BasisFuns(span, u, Nb);
            int first = span - Ord + 1;
            for (int i = 0; i < Ord; i++)
                for (int k = 0; k < 3; k++)
                    pt[k] += Nb[i] * Cv[(first + i) * 3 + k];
            return pt;
        }

        /// <summary>Unit tangent by central difference.</summary>
        public double[] Tangent(double u)
        {
            const double dp = 1e-5;
            double lo = Math.Max(0, u - dp), hi = Math.Min(1, u + dp);
            var a = Eval(lo); var b = Eval(hi);
            var t = V.Sub(b, a);
            return V.Unit(t);
        }

        // ------------------------------------------------------------------
        // Knot insertion and exact sub-curves
        // ------------------------------------------------------------------

        /// <summary>Insert knot u once (Boehm, NURBS Book A5.1 with r = 1). Shape unchanged.</summary>
        public BSpl InsertKnot(double u)
        {
            int p = Ord - 1;
            int k = FindSpan(u);
            var q = new BSpl { Ord = Ord, N = N + 1, Kn = new double[Kn.Length + 1], Cv = new double[(N + 1) * 3] };
            for (int i = 0; i <= k; i++) q.Kn[i] = Kn[i];
            q.Kn[k + 1] = u;
            for (int i = k + 1; i < Kn.Length; i++) q.Kn[i + 1] = Kn[i];
            for (int i = 0; i <= N; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double v;
                    if (i <= k - p) v = Cv[i * 3 + c];
                    else if (i >= k + 1) v = Cv[(i - 1) * 3 + c];
                    else
                    {
                        double den = Kn[i + p] - Kn[i];
                        double a = den > 0 ? (u - Kn[i]) / den : 0;
                        v = a * Cv[i * 3 + c] + (1 - a) * Cv[(i - 1) * 3 + c];
                    }
                    q.Cv[i * 3 + c] = v;
                }
            }
            return q;
        }

        private int Multiplicity(double u)
        {
            int s = 0;
            foreach (var k in Kn) if (Math.Abs(k - u) <= 1e-12) s++;
            return s;
        }

        /// <summary>
        /// Exact piece of the curve between parameters u0 &lt; u1, renormalised to [0,1].
        /// Knots u0 and u1 are inserted to full multiplicity (order), which splits the
        /// knot vector into clamped pieces; no approximation, and never more CVs than needed.
        /// </summary>
        public BSpl SubCurve(double u0, double u1)
        {
            const double eps = 1e-12;
            u0 = Math.Max(0, u0); u1 = Math.Min(1, u1);
            if (u1 - u0 <= eps) throw new ArgumentException("SubCurve: empty range");
            BSpl c = Clone();

            if (u1 < 1 - eps)
            {
                // snap to an existing knot within eps, then insert to multiplicity = order
                foreach (var k in c.Kn) if (Math.Abs(k - u1) <= eps) { u1 = k; break; }
                while (c.Multiplicity(u1) < c.Ord) c = c.InsertKnot(u1);
                int j = Array.FindIndex(c.Kn, k => Math.Abs(k - u1) <= eps);   // first copy of u1
                c = c.Slice(0, j, 0, j + c.Ord);                                 // CVs [0, j), knots [0, j + ord)
            }
            if (u0 > eps)
            {
                foreach (var k in c.Kn) if (Math.Abs(k - u0) <= eps) { u0 = k; break; }
                while (c.Multiplicity(u0) < c.Ord) c = c.InsertKnot(u0);
                int j = Array.FindIndex(c.Kn, k => Math.Abs(k - u0) <= eps);   // first copy of u0
                c = c.Slice(j, c.N - j, j, c.Kn.Length - j);                     // CVs [j, N), knots [j, end)
            }

            double a = c.Kn[0], b = c.Kn[c.Kn.Length - 1];
            for (int i = 0; i < c.Kn.Length; i++) c.Kn[i] = (c.Kn[i] - a) / (b - a);
            c.Kn[0] = 0; c.Kn[c.Kn.Length - 1] = 1;
            return c;
        }

        private BSpl Slice(int cv0, int nCv, int kn0, int nKn)
        {
            var s = new BSpl { Ord = Ord, N = nCv, Kn = new double[nKn], Cv = new double[nCv * 3] };
            Array.Copy(Kn, kn0, s.Kn, 0, nKn);
            Array.Copy(Cv, cv0 * 3, s.Cv, 0, nCv * 3);
            return s;
        }

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        /// <summary>Clamped uniform basis with n CVs (CVs zeroed).</summary>
        public static BSpl Uniform(int ord, int n)
        {
            var b = new BSpl { Ord = ord, N = n, Kn = new double[n + ord], Cv = new double[n * 3] };
            int spans = n - ord + 1;
            for (int i = 0; i < n + ord; i++)
            {
                if (i < ord) b.Kn[i] = 0;
                else if (i >= n) b.Kn[i] = 1;
                else b.Kn[i] = (double)(i - ord + 1) / spans;
            }
            return b;
        }

        /// <summary>Sample parameters: perSpan per non-empty knot span, plus u = 1.</summary>
        public double[] SpanSamples(int perSpan)
        {
            int count = 1;
            for (int j = 0; j < Kn.Length - 1; j++) if (Kn[j + 1] > Kn[j]) count += perSpan;
            var uk = new double[count];
            int i = 0;
            for (int j = 0; j < Kn.Length - 1; j++)
                if (Kn[j + 1] > Kn[j])
                    for (int s = 0; s < perSpan; s++)
                        uk[i++] = Kn[j] + (Kn[j + 1] - Kn[j]) * s / perSpan;
            uk[count - 1] = 1.0;
            return uk;
        }
    }

    /// <summary>Normalised arc-length table, uniform in u.</summary>
    public sealed class ArcTable
    {
        private readonly double[] _u, _s;

        public ArcTable(BSpl b, int minCount = 1000)
        {
            int cnt = Math.Max(minCount, 20 * b.N);
            _u = new double[cnt + 1];
            _s = new double[cnt + 1];
            var q = b.Eval(0);
            for (int i = 1; i <= cnt; i++)
            {
                _u[i] = (double)i / cnt;
                var p = b.Eval(_u[i]);
                _s[i] = _s[i - 1] + V.Dist(p, q);
                q = p;
            }
            for (int i = 1; i <= cnt; i++) _s[i] /= _s[cnt];
        }

        public double ParamAtArc(double s)
        {
            if (s <= 0) return 0;
            if (s >= 1) return 1;
            int lo = 0, hi = _s.Length - 1;
            while (hi - lo > 1)
            {
                int md = (lo + hi) / 2;
                if (_s[md] < s) lo = md; else hi = md;
            }
            double f = _s[hi] > _s[lo] ? (s - _s[lo]) / (_s[hi] - _s[lo]) : 0;
            return _u[lo] + f * (_u[hi] - _u[lo]);
        }

        public double ArcAtParam(double u)
        {
            if (u <= 0) return 0;
            if (u >= 1) return 1;
            int cnt = _s.Length - 1;
            int i = Math.Min((int)(u * cnt), cnt - 1);
            double f = (u - _u[i]) / (_u[i + 1] - _u[i]);
            return _s[i] + f * (_s[i + 1] - _s[i]);
        }
    }

    public static class Trim
    {
        /// <summary>
        /// Piece of an edge curve between arc-length fractions s0 &lt; s1 (0..1 along the curve's
        /// own direction). Returns the curve itself when the range is the whole edge.
        /// </summary>
        public static BSpl ByArc(BSpl b, double s0, double s1)
        {
            if (s0 <= 1e-9 && s1 >= 1 - 1e-9) return b;
            var arc = new ArcTable(b, 4000);
            return b.SubCurve(arc.ParamAtArc(s0), arc.ParamAtArc(s1));
        }
    }

    public static class Fit
    {
        public const int SamplesPerSpan = 6;

        /// <summary>
        /// Least-squares fit of points Q (at params uk) onto basis T, ends fixed to Q's ends.
        /// </summary>
        public static BSpl Samples(BSpl T, double[] uk, double[][] Q, out double maxDev)
        {
            var F = new BSpl { Ord = T.Ord, N = T.N, Kn = (double[])T.Kn.Clone(), Cv = new double[T.N * 3] };
            int n = F.N, m = uk.Length;
            for (int k = 0; k < 3; k++)
            {
                F.Cv[k] = Q[0][k];
                F.Cv[(n - 1) * 3 + k] = Q[m - 1][k];
            }
            int nI = n - 2;
            if (nI > 0)
            {
                var AtA = new double[nI, nI];
                var AtR = new double[nI, 3];
                for (int i = 0; i < m; i++)
                {
                    var Nr = F.BasisRow(uk[i]);
                    var r = new double[3];
                    for (int k = 0; k < 3; k++)
                        r[k] = Q[i][k] - Nr[0] * F.Cv[k] - Nr[n - 1] * F.Cv[(n - 1) * 3 + k];
                    for (int a = 1; a <= nI; a++)
                    {
                        if (Nr[a] == 0) continue;
                        for (int c = 1; c <= nI; c++)
                            if (Nr[c] != 0) AtA[a - 1, c - 1] += Nr[a] * Nr[c];
                        for (int k = 0; k < 3; k++) AtR[a - 1, k] += Nr[a] * r[k];
                    }
                }
                Linear.Solve(AtA, AtR, nI);
                for (int a = 1; a <= nI; a++)
                    for (int k = 0; k < 3; k++) F.Cv[a * 3 + k] = AtR[a - 1, k];
            }
            maxDev = 0;
            for (int i = 0; i < m; i++) maxDev = Math.Max(maxDev, V.Dist(F.Eval(uk[i]), Q[i]));
            return F;
        }

        /// <summary>
        /// Fit source curve Src onto basis T. If master != null (AUTO), T param -> master
        /// arc fraction -> Src point at the same fraction; otherwise T param IS the arc fraction.
        /// maxDev includes span midpoints between samples.
        /// </summary>
        public static BSpl Curve(BSpl src, ArcTable srcArc, BSpl T, ArcTable masterArc, out double maxDev)
        {
            var uk = T.SpanSamples(SamplesPerSpan);
            var Q = new double[uk.Length][];
            for (int i = 0; i < uk.Length; i++)
                Q[i] = src.Eval(srcArc.ParamAtArc(Frac(uk[i], masterArc)));
            double dev;
            var F = Samples(T, uk, Q, out dev);
            maxDev = dev;
            for (int i = 0; i < uk.Length - 1; i++)
            {
                double um = 0.5 * (uk[i] + uk[i + 1]);
                var sp = src.Eval(srcArc.ParamAtArc(Frac(um, masterArc)));
                maxDev = Math.Max(maxDev, V.Dist(F.Eval(um), sp));
            }
            return F;
        }

        private static double Frac(double u, ArcTable masterArc)
        {
            return masterArc == null ? u : masterArc.ArcAtParam(u);
        }
    }

    public static class Linear
    {
        /// <summary>Gaussian elimination, partial pivoting. Solution overwrites B (n x 3).</summary>
        public static void Solve(double[,] A, double[,] B, int n)
        {
            for (int i = 0; i < n; i++)
            {
                int piv = i; double mx = Math.Abs(A[i, i]);
                for (int j = i + 1; j < n; j++)
                    if (Math.Abs(A[j, i]) > mx) { mx = Math.Abs(A[j, i]); piv = j; }
                if (piv != i)
                {
                    for (int k = 0; k < n; k++) { double t = A[i, k]; A[i, k] = A[piv, k]; A[piv, k] = t; }
                    for (int k = 0; k < 3; k++) { double t = B[i, k]; B[i, k] = B[piv, k]; B[piv, k] = t; }
                }
                if (Math.Abs(A[i, i]) < 1e-14) throw new InvalidOperationException("Singular fit matrix at row " + i);
                for (int j = i + 1; j < n; j++)
                {
                    double f = A[j, i] / A[i, i];
                    if (f == 0) continue;
                    for (int k = i; k < n; k++) A[j, k] -= f * A[i, k];
                    for (int k = 0; k < 3; k++) B[j, k] -= f * B[i, k];
                }
            }
            for (int i = n - 1; i >= 0; i--)
                for (int k = 0; k < 3; k++)
                {
                    double t = B[i, k];
                    for (int j = i + 1; j < n; j++) t -= A[i, j] * B[j, k];
                    B[i, k] = t / A[i, i];
                }
        }
    }

    public static class V
    {
        public static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        public static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        public static double Len(double[] a) { return Math.Sqrt(Dot(a, a)); }
        public static double Dist(double[] a, double[] b) { return Len(Sub(a, b)); }
        public static double[] Unit(double[] a) { double l = Len(a); return new[] { a[0] / l, a[1] / l, a[2] / l }; }
        public static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }
    }
}
