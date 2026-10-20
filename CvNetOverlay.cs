using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace BridgeSurface
{
    /// <summary>
    /// Edge A / Edge B colours, shared by the page (COLORREF 0x00BBGGRR), the picked-edge highlight
    /// and the CV net. Magenta and red: clear of the blue preview and SOLIDWORKS' light-blue edges.
    /// </summary>
    public static class EdgeColours
    {
        public static readonly float[] A = { 0.86f, 0.10f, 0.62f };   // magenta
        public static readonly float[] B = { 0.90f, 0.10f, 0.10f };   // red
        public static int ARef { get { return Ref(A); } }
        public static int BRef { get { return Ref(B); } }
        private static int Ref(float[] c)
        {
            return (int)(c[0] * 255) | ((int)(c[1] * 255) << 8) | ((int)(c[2] * 255) << 16);
        }
    }

    /// <summary>CV grid of the previewed bridge: V rows, each a U row of CVs (x,y,z), with a role per row.</summary>
    public sealed class CvGrid
    {
        public enum Role { Boundary = 0, G1 = 1, G2 = 2 }
        public readonly List<double[]> Rows = new List<double[]>();
        public readonly List<Role> Roles = new List<Role>();
        public int NU;
        public int UOrd;          // U order (degree + 1)
        public double[] UKn;      // U knots, [0,1]

        /// <summary>Point on the bridge at (u, v): rows evaluated in U, Bezier (de Casteljau) in V.</summary>
        public double[] Eval(double u, double v)
        {
            int nV = Rows.Count;
            var pts = new double[nV][];
            for (int r = 0; r < nV; r++)
            {
                var b = new Geometry.BSpl { Ord = UOrd, N = NU, Kn = UKn, Cv = Rows[r] };
                pts[r] = b.Eval(u);
            }
            for (int k = 1; k < nV; k++)
                for (int r = 0; r < nV - k; r++)
                    for (int c = 0; c < 3; c++)
                        pts[r][c] = (1 - v) * pts[r][c] + v * pts[r + 1][c];
            return pts[0];
        }
    }

    /// <summary>Triangle mesh for the zebra: interleaved x,y,z,nx,ny,nz per vertex, 3 vertices per triangle.</summary>
    public sealed class ZebraMesh
    {
        public float[] Data;

        /// <summary>Mesh of the bridge itself, from its CV grid. Normals by finite differences.</summary>
        public static ZebraMesh FromGrid(CvGrid g, int nu = 60, int nv = 30)
        {
            var P = new double[nu + 1, nv + 1][];
            var Nm = new double[nu + 1, nv + 1][];
            const double h = 1e-4;
            for (int i = 0; i <= nu; i++)
                for (int j = 0; j <= nv; j++)
                {
                    double u = (double)i / nu, v = (double)j / nv;
                    P[i, j] = g.Eval(u, v);
                    double u0 = Math.Max(0, u - h), u1 = Math.Min(1, u + h);
                    double v0 = Math.Max(0, v - h), v1 = Math.Min(1, v + h);
                    var du = Geometry.V.Sub(g.Eval(u1, v), g.Eval(u0, v));
                    var dv = Geometry.V.Sub(g.Eval(u, v1), g.Eval(u, v0));
                    var n = Geometry.V.Cross(du, dv);
                    double L = Geometry.V.Len(n);
                    Nm[i, j] = L > 0 ? new[] { n[0] / L, n[1] / L, n[2] / L } : new double[] { 0, 0, 1 };
                }
            var data = new List<float>(nu * nv * 36);
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    Add(data, P[i, j], Nm[i, j]); Add(data, P[i + 1, j], Nm[i + 1, j]); Add(data, P[i + 1, j + 1], Nm[i + 1, j + 1]);
                    Add(data, P[i, j], Nm[i, j]); Add(data, P[i + 1, j + 1], Nm[i + 1, j + 1]); Add(data, P[i, j + 1], Nm[i, j + 1]);
                }
            return new ZebraMesh { Data = data.ToArray() };
        }

        /// <summary>Mesh of a model face from SOLIDWORKS's own display tessellation.</summary>
        public static ZebraMesh FromFace(Face2 face)
        {
            var tri = face.GetTessTriangles(true) as float[];
            var nrm = face.GetTessNorms() as float[];
            if (tri == null || nrm == null || tri.Length != nrm.Length) return null;
            var data = new float[tri.Length * 2];
            for (int i = 0; i < tri.Length / 3; i++)
            {
                data[i * 6] = tri[i * 3]; data[i * 6 + 1] = tri[i * 3 + 1]; data[i * 6 + 2] = tri[i * 3 + 2];
                data[i * 6 + 3] = nrm[i * 3]; data[i * 6 + 4] = nrm[i * 3 + 1]; data[i * 6 + 5] = nrm[i * 3 + 2];
            }
            return new ZebraMesh { Data = data };
        }

        /// <summary>
        /// Mesh from SOLIDWORKS's tessellation engine with explicit tolerances, as the Surface Analysis
        /// add-in does (AppendBodyTessellation): independent of the part's image quality setting.
        /// faces = null tessellates the whole body (the bridge preview); otherwise only those faces.
        /// Tolerance in mm, angle in degrees. Normals are SOLIDWORKS's display normals (face-oriented).
        /// </summary>
        public static ZebraMesh FromTessellation(Body2 body, Face2[] faces, double tolMM, double angleDeg)
        {
            if (body == null) return null;
            ITessellation tess = null;
            object[] all = null;
            try
            {
                tess = body.GetTessellation(faces) as ITessellation;
                if (tess == null) return null;
                tess.NeedVertexNormal = true;
                tess.NeedFaceFacetMap = true;
                double tol = tolMM / 1000.0, ang = angleDeg * Math.PI / 180.0;
                tess.CurveChordTolerance = tol;
                tess.CurveChordAngleTolerance = ang;
                tess.SurfacePlaneTolerance = tol;
                tess.SurfacePlaneAngleTolerance = ang;
                tess.ImprovedQuality = true;
                if (!tess.Tessellate()) return null;

                IEnumerable<object> list = faces;
                if (faces == null) { all = body.GetFaces() as object[]; list = all; }
                if (list == null) return null;

                var data = new List<float>();
                foreach (var fo in list)
                {
                    var facets = tess.GetFaceFacets(fo) as int[];
                    if (facets == null) continue;
                    foreach (int facet in facets)
                    {
                        var fins = tess.GetFacetFins(facet) as int[];
                        if (fins == null || fins.Length < 3) continue;
                        for (int i = 0; i < 3; i++)
                        {
                            var vs = tess.GetFinVertices(fins[i]) as int[];
                            var p = tess.GetVertexPoint(vs[0]) as double[];
                            var n = tess.GetVertexNormal(vs[0]) as double[];
                            Add(data, p, n);
                        }
                    }
                }
                return data.Count > 0 ? new ZebraMesh { Data = data.ToArray() } : null;
            }
            catch (Exception ex)
            {
                Debug.Print("[BridgeSurface] FromTessellation failed: " + ex.Message);
                return null;
            }
            finally
            {
                if (all != null) foreach (var f in all) Com.Release(f);
                Com.Release(tess);
            }
        }

        /// <summary>Normal at the mesh vertex nearest to p.</summary>
        public double[] NormalNear(double[] p)
        {
            int best = -1; double bd = double.MaxValue;
            for (int i = 0; i < Data.Length; i += 6)
            {
                double dx = Data[i] - p[0], dy = Data[i + 1] - p[1], dz = Data[i + 2] - p[2];
                double d = dx * dx + dy * dy + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            return best < 0 ? null : new double[] { Data[best + 3], Data[best + 4], Data[best + 5] };
        }

        public void FlipNormals()
        {
            for (int i = 0; i < Data.Length; i += 6) { Data[i + 3] = -Data[i + 3]; Data[i + 4] = -Data[i + 4]; Data[i + 5] = -Data[i + 5]; }
        }

        private static void Add(List<float> d, double[] p, double[] n)
        {
            d.Add((float)p[0]); d.Add((float)p[1]); d.Add((float)p[2]);
            d.Add((float)n[0]); d.Add((float)n[1]); d.Add((float)n[2]);
        }
    }

    /// <summary>
    /// Draws the CV net over the model while the PropertyManager page is open
    /// (create / edit only). Same hook as the draft shader add-in: ModelView
    /// GraphicsRenderPostNotify, where the GL modelview is model space (metres),
    /// so CVs are drawn with their own coordinates. Immediate mode, state saved
    /// and restored, skipped when no GL context is current.
    /// </summary>
    public sealed class CvNetOverlay
    {
        private ModelView _view;
        private CvGrid _grid;
        public bool Visible = true;

        // ---- zebra (stripes only), same shader and defaults as the draft shader add-in ----
        public bool ZebraVisible = false;
        public float LineFrequency = 6f, LineWidth = 0.08f, LineFeather = 0.5f;
        public float[] RefDir = { 0f, 1f, 0f };     // unused in matcap mode; kept for the shader uniform
        public bool MatcapAxisY = true;             // environment zebra axis: Y = horizontal stripes, X = vertical
        private List<ZebraMesh> _zebra = new List<ZebraMesh>();
        private ShaderProgram _shader;

        public void SetZebra(List<ZebraMesh> meshes) { _zebra = meshes ?? new List<ZebraMesh>(); Redraw(); }

        /// <summary>Stripe reference direction = current view direction (as "Set Plane (parallel to screen)").</summary>
        public void SetRefFromView()
        {
            try
            {
                if (_view == null) return;
                var xf = (MathTransform)_view.Transform;
                var a = (double[])xf.ArrayData;
                double L = Math.Sqrt(a[6] * a[6] + a[7] * a[7] + a[8] * a[8]);
                if (L < 1e-4) return;
                RefDir = new[] { (float)(a[6] / L), (float)(a[7] / L), (float)(a[8] / L) };
                Redraw();
            }
            catch (Exception ex) { Debug.Print("[BridgeSurface] SetRefFromView failed: " + ex.Message); }
        }

        public void Attach(ModelDoc2 model)
        {
            Detach();
            _view = model.ActiveView as ModelView;
            if (_view == null) return;
            _view.GraphicsRenderPostNotify += OnRender;
        }

        public void Detach()
        {
            if (_view == null) return;
            try { _view.GraphicsRenderPostNotify -= OnRender; } catch { }
            try { _view.GraphicsRedraw(null); } catch { }
            _view = null;
            _grid = null;
            _zebra = new List<ZebraMesh>();
            _shader = null;   // program belongs to SOLIDWORKS's GL context; just drop our handle
        }

        public void SetGrid(CvGrid grid)
        {
            _grid = grid;
            Redraw();
        }

        public void Redraw()
        {
            try { if (_view != null) _view.GraphicsRedraw(null); } catch { }
        }

        private int OnRender()
        {
            try
            {
                if (Gl.GetCurrentContext() == IntPtr.Zero) return 0;   // no context this frame: skip
                if (ZebraVisible && _zebra.Count > 0) DrawZebra();
                if (Visible && _grid != null && _grid.Rows.Count > 0) Draw(_grid);
            }
            catch (Exception ex)
            {
                Debug.Print("[BridgeSurface] CV net draw failed: " + ex.Message);
            }
            return 0;
        }

        private void DrawZebra()
        {
            if (_shader == null || !_shader.IsValid) _shader = new ShaderProgram(ZebraShaders.Vertex, ZebraShaders.Fragment);

            bool prevDepth = Gl.IsEnabled(Gl.GL_DEPTH_TEST);
            bool prevCull = Gl.IsEnabled(Gl.GL_CULL_FACE);
            bool prevLighting = Gl.IsEnabled(Gl.GL_LIGHTING);
            bool prevBlend = Gl.IsEnabled(Gl.GL_BLEND);
            bool prevOffset = Gl.IsEnabled(Gl.GL_POLYGON_OFFSET_FILL);
            int prevProgram; Gl.GetIntegerv(Gl.GL_CURRENT_PROGRAM, out prevProgram);
            int prevDepthFunc; Gl.GetIntegerv(Gl.GL_DEPTH_FUNC, out prevDepthFunc);

            Gl.Enable(Gl.GL_DEPTH_TEST);
            Gl.Disable(Gl.GL_CULL_FACE);
            Gl.Disable(Gl.GL_LIGHTING);
            Gl.Enable(Gl.GL_BLEND);
            Gl.BlendFunc(Gl.GL_SRC_ALPHA, Gl.GL_ONE_MINUS_SRC_ALPHA);
            Gl.DepthFunc(Gl.GL_LEQUAL);
            Gl.Enable(Gl.GL_POLYGON_OFFSET_FILL);
            Gl.PolygonOffset(-2f, -10f);

            _shader.Use();
            _shader.SetVec3("uRefDir", RefDir[0], RefDir[1], RefDir[2]);
            _shader.SetFloat("uLineFrequency", LineFrequency);
            _shader.SetFloat("uLineWidth", LineWidth);
            _shader.SetFloat("uLineFeather", LineFeather);
            _shader.SetFloat("uMode", 1f);   // matcap / environment zebra: view-space normal vs axis
            _shader.SetVec3("uMatcapAxis", MatcapAxisY ? 0f : 1f, MatcapAxisY ? 1f : 0f, 0f);
            _shader.SetFloat("uStripesOnly", 1f);

            foreach (var m in _zebra)
            {
                if (m == null || m.Data == null) continue;
                var d = m.Data;
                Gl.Begin(Gl.GL_TRIANGLES);
                for (int i = 0; i < d.Length; i += 6)
                {
                    Gl.Normal3f(d[i + 3], d[i + 4], d[i + 5]);
                    Gl.Vertex3f(d[i], d[i + 1], d[i + 2]);
                }
                Gl.End();
            }

            if (prevProgram == 0 || Gl.IsProgram((uint)prevProgram)) Gl.UseProgram((uint)prevProgram);
            else Gl.UseProgram(0);
            Gl.DepthFunc((uint)prevDepthFunc);
            if (!prevOffset) Gl.Disable(Gl.GL_POLYGON_OFFSET_FILL);
            if (!prevBlend) Gl.Disable(Gl.GL_BLEND);
            if (!prevDepth) Gl.Disable(Gl.GL_DEPTH_TEST);
            if (prevCull) Gl.Enable(Gl.GL_CULL_FACE);
            if (prevLighting) Gl.Enable(Gl.GL_LIGHTING);
        }

        private static void Draw(CvGrid g)
        {
            // --- save state ---
            bool prevDepth = Gl.IsEnabled(Gl.GL_DEPTH_TEST);
            bool prevLighting = Gl.IsEnabled(Gl.GL_LIGHTING);
            int prevProgram; Gl.GetIntegerv(Gl.GL_CURRENT_PROGRAM, out prevProgram);
            var prevLineWidth = new double[1]; Gl.GetDoublev(Gl.GL_LINE_WIDTH, prevLineWidth);
            var prevPointSize = new double[1]; Gl.GetDoublev(Gl.GL_POINT_SIZE, prevPointSize);

            // --- overlay state: show through the surface, unlit, fixed pipeline ---
            Gl.Disable(Gl.GL_DEPTH_TEST);
            Gl.Disable(Gl.GL_LIGHTING);
            Gl.UseProgram(0);

            int nV = g.Rows.Count, nU = g.NU;

            // V polylines (across the bridge): grey
            Gl.LineWidth(1.0f);
            Gl.Color3f(0.55f, 0.55f, 0.55f);
            for (int u = 0; u < nU; u++)
            {
                Gl.Begin(Gl.GL_LINE_STRIP);
                for (int v = 0; v < nV; v++) Vertex(g.Rows[v], u);
                Gl.End();
            }

            // U polylines (along the edges): boundary rows in the Edge A / Edge B colours and
            // heavier, interior rows coloured by role (green G1, orange G2)
            for (int v = 0; v < nV; v++)
            {
                bool boundary = v == 0 || v == nV - 1;
                Gl.LineWidth(boundary ? 3.0f : 1.5f);
                SetRowColour(g, v);
                Gl.Begin(Gl.GL_LINE_STRIP);
                for (int u = 0; u < nU; u++) Vertex(g.Rows[v], u);
                Gl.End();
            }

            // CV points
            Gl.PointSize(6.0f);
            for (int v = 0; v < nV; v++)
            {
                SetRowColour(g, v);
                Gl.Begin(Gl.GL_POINTS);
                for (int u = 0; u < nU; u++) Vertex(g.Rows[v], u);
                Gl.End();
            }

            // --- restore ---
            Gl.PointSize((float)prevPointSize[0]);
            Gl.LineWidth((float)prevLineWidth[0]);
            if (prevProgram == 0 || Gl.IsProgram((uint)prevProgram)) Gl.UseProgram((uint)prevProgram);
            else Gl.UseProgram(0);
            if (prevLighting) Gl.Enable(Gl.GL_LIGHTING);
            if (prevDepth) Gl.Enable(Gl.GL_DEPTH_TEST);
        }

        private static void Vertex(double[] row, int u)
        {
            Gl.Vertex3d(row[u * 3], row[u * 3 + 1], row[u * 3 + 2]);
        }

        /// <summary>Row 0 is on edge A, the last row on edge B; the rest by role.</summary>
        private static void SetRowColour(CvGrid g, int v)
        {
            if (v == 0) { Gl.Color3f(EdgeColours.A[0], EdgeColours.A[1], EdgeColours.A[2]); return; }
            if (v == g.Rows.Count - 1) { Gl.Color3f(EdgeColours.B[0], EdgeColours.B[1], EdgeColours.B[2]); return; }
            SetRoleColour(g.Roles[v]);
        }

        private static void SetRoleColour(CvGrid.Role r)
        {
            switch (r)
            {
                case CvGrid.Role.Boundary: Gl.Color3f(0.10f, 0.35f, 0.95f); break;   // blue: on the edges
                case CvGrid.Role.G1: Gl.Color3f(0.10f, 0.70f, 0.20f); break;         // green: tangent rows
                default: Gl.Color3f(0.90f, 0.45f, 0.05f); break;                     // orange: curvature rows
            }
        }
    }

    /// <summary>The draft shader add-in's stripe shaders, unchanged.</summary>
    internal static class ZebraShaders
    {
        public const string Vertex = @"
#version 120
varying vec3 vNormal;

void main()
{
    gl_Position = gl_ModelViewProjectionMatrix * gl_Vertex;
    vNormal = gl_Normal;
}";

        public const string Fragment = @"
#version 120
varying vec3 vNormal;

uniform vec3 uRefDir;
uniform float uLineFrequency;
uniform float uLineWidth;
uniform float uLineFeather;
uniform float uMode;
uniform vec3 uMatcapAxis;
uniform float uStripesOnly;

void main()
{
    vec3 N_model = normalize(vNormal);
    float ndotv;
    float rawBand;

    if (uMode > 0.5)
    {
        vec3 N_view = normalize(gl_NormalMatrix * N_model);
        ndotv = clamp(dot(N_view, uMatcapAxis), -1.0, 1.0);
        float angleDeg = degrees(acos(ndotv));
        rawBand = angleDeg * uLineFrequency / 90.0;
    }
    else
    {
        ndotv = clamp(dot(N_model, uRefDir), -1.0, 1.0);
        float angleDeg = degrees(acos(ndotv));
        rawBand = angleDeg * uLineFrequency / 90.0;
    }

    float band = fract(rawBand);

    float aa = fwidth(rawBand);
    float feather = max(uLineFeather * aa, 0.0001);

    float lowEdge = smoothstep(uLineWidth - feather, uLineWidth + feather, band);
    float highEdge = smoothstep(1.0 - uLineWidth - feather, 1.0 - uLineWidth + feather, band);
    float isLine = (1.0 - lowEdge) + highEdge;
    isLine = clamp(isLine, 0.0, 1.0);

    vec3 baseColor = mix(vec3(0.15, 0.35, 0.9), vec3(0.9, 0.25, 0.15), (ndotv + 1.0) * 0.5);
    vec3 lineColor = vec3(0.05, 0.05, 0.05);

    vec3 finalColor = mix(mix(baseColor, lineColor, isLine), lineColor, uStripesOnly);

    float alpha = mix(1.0, isLine, uStripesOnly);

    if (alpha < 0.01) discard;

    gl_FragColor = vec4(finalColor, alpha);
}";
    }

    /// <summary>The draft shader add-in's ShaderProgram, ported.</summary>
    internal sealed class ShaderProgram
    {
        private readonly uint _handle;
        public bool IsValid { get { return Gl.IsProgram(_handle); } }

        public ShaderProgram(string vertexSrc, string fragmentSrc)
        {
            uint vs = Compile(Gl.GL_VERTEX_SHADER, vertexSrc);
            uint fs = Compile(Gl.GL_FRAGMENT_SHADER, fragmentSrc);
            _handle = Gl.CreateProgram();
            Gl.AttachShader(_handle, vs);
            Gl.AttachShader(_handle, fs);
            Gl.LinkProgram(_handle);
            if (Gl.GetProgramiv(_handle, Gl.GL_LINK_STATUS) == 0) Debug.Print("[BridgeSurface] shader link failed");
            Gl.DeleteShader(vs);
            Gl.DeleteShader(fs);
        }

        private static uint Compile(uint type, string src)
        {
            uint s = Gl.CreateShader(type);
            Gl.ShaderSource(s, src);
            Gl.CompileShader(s);
            if (Gl.GetShaderiv(s, Gl.GL_COMPILE_STATUS) == 0) Debug.Print("[BridgeSurface] shader compile failed");
            return s;
        }

        public void Use() { Gl.UseProgram(_handle); }

        public void SetFloat(string name, float v)
        {
            int loc = Gl.GetUniformLocation(_handle, name);
            if (loc != -1) Gl.Uniform1f(loc, v);
        }

        public void SetVec3(string name, float x, float y, float z)
        {
            int loc = Gl.GetUniformLocation(_handle, name);
            if (loc != -1) Gl.Uniform3f(loc, x, y, z);
        }
    }

    /// <summary>OpenGL bindings: GL 1.1 exports direct, GL 2.0 entry points via wglGetProcAddress (cached).</summary>
    internal static class Gl
    {
        private const string Lib = "opengl32.dll";
        public const uint GL_POINTS = 0x0000;
        public const uint GL_LINE_STRIP = 0x0003;
        public const uint GL_TRIANGLES = 0x0004;
        public const uint GL_LIGHTING = 0x0B50;
        public const uint GL_DEPTH_TEST = 0x0B71;
        public const uint GL_DEPTH_FUNC = 0x0B74;
        public const uint GL_LEQUAL = 0x0203;
        public const uint GL_CULL_FACE = 0x0B44;
        public const uint GL_BLEND = 0x0BE2;
        public const uint GL_SRC_ALPHA = 0x0302;
        public const uint GL_ONE_MINUS_SRC_ALPHA = 0x0303;
        public const uint GL_POLYGON_OFFSET_FILL = 0x8037;
        public const uint GL_CURRENT_PROGRAM = 0x8B8D;
        public const uint GL_LINE_WIDTH = 0x0B21;
        public const uint GL_POINT_SIZE = 0x0B11;
        public const uint GL_VERTEX_SHADER = 0x8B31;
        public const uint GL_FRAGMENT_SHADER = 0x8B30;
        public const uint GL_COMPILE_STATUS = 0x8B81;
        public const uint GL_LINK_STATUS = 0x8B82;

        [DllImport(Lib, EntryPoint = "wglGetCurrentContext")] public static extern IntPtr GetCurrentContext();
        [DllImport(Lib, EntryPoint = "glIsEnabled")] [return: MarshalAs(UnmanagedType.I1)] public static extern bool IsEnabled(uint cap);
        [DllImport(Lib, EntryPoint = "glEnable")] public static extern void Enable(uint cap);
        [DllImport(Lib, EntryPoint = "glDisable")] public static extern void Disable(uint cap);
        [DllImport(Lib, EntryPoint = "glColor3f")] public static extern void Color3f(float r, float g, float b);
        [DllImport(Lib, EntryPoint = "glLineWidth")] public static extern void LineWidth(float width);
        [DllImport(Lib, EntryPoint = "glPointSize")] public static extern void PointSize(float size);
        [DllImport(Lib, EntryPoint = "glGetDoublev")] public static extern void GetDoublev(uint pname, double[] data);
        [DllImport(Lib, EntryPoint = "glGetIntegerv")] public static extern void GetIntegerv(uint pname, out int data);
        [DllImport(Lib, EntryPoint = "glBegin")] public static extern void Begin(uint mode);
        [DllImport(Lib, EntryPoint = "glEnd")] public static extern void End();
        [DllImport(Lib, EntryPoint = "glVertex3d")] public static extern void Vertex3d(double x, double y, double z);
        [DllImport(Lib, EntryPoint = "glVertex3f")] public static extern void Vertex3f(float x, float y, float z);
        [DllImport(Lib, EntryPoint = "glNormal3f")] public static extern void Normal3f(float x, float y, float z);
        [DllImport(Lib, EntryPoint = "glBlendFunc")] public static extern void BlendFunc(uint sfactor, uint dfactor);
        [DllImport(Lib, EntryPoint = "glDepthFunc")] public static extern void DepthFunc(uint func);
        [DllImport(Lib, EntryPoint = "glPolygonOffset")] public static extern void PolygonOffset(float factor, float units);
        [DllImport(Lib, EntryPoint = "wglGetProcAddress")] private static extern IntPtr WglGetProcAddress(string name);

        private static readonly Dictionary<string, Delegate> _procs = new Dictionary<string, Delegate>();
        private static T Proc<T>(string name) where T : class
        {
            Delegate d;
            if (_procs.TryGetValue(name, out d)) return d as T;
            IntPtr p = WglGetProcAddress(name);
            d = p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T));
            if (d != null) _procs[name] = d;   // don't cache failures: may resolve once a context is current
            return d as T;
        }

        private delegate void UseProgramD(uint program);
        private delegate byte IsProgramD(uint program);
        private delegate uint CreateShaderD(uint type);
        private delegate void ShaderSourceD(uint shader, int count, IntPtr[] strings, int[] lengths);
        private delegate void CompileShaderD(uint shader);
        private delegate void GetShaderivD(uint shader, uint pname, out int param);
        private delegate void DeleteShaderD(uint shader);
        private delegate uint CreateProgramD();
        private delegate void AttachShaderD(uint program, uint shader);
        private delegate void LinkProgramD(uint program);
        private delegate void GetProgramivD(uint program, uint pname, out int param);
        private delegate int GetUniformLocationD(uint program, string name);
        private delegate void Uniform1fD(int location, float v);
        private delegate void Uniform3fD(int location, float x, float y, float z);

        public static void UseProgram(uint program) { var f = Proc<UseProgramD>("glUseProgram"); if (f != null) f(program); }
        public static bool IsProgram(uint program) { var f = Proc<IsProgramD>("glIsProgram"); return f != null && f(program) != 0; }
        public static uint CreateShader(uint type) { var f = Proc<CreateShaderD>("glCreateShader"); return f != null ? f(type) : 0; }
        public static void ShaderSource(uint shader, string src)
        {
            var f = Proc<ShaderSourceD>("glShaderSource"); if (f == null) return;
            IntPtr p = Marshal.StringToHGlobalAnsi(src);
            try { f(shader, 1, new[] { p }, null); } finally { Marshal.FreeHGlobal(p); }
        }
        public static void CompileShader(uint shader) { var f = Proc<CompileShaderD>("glCompileShader"); if (f != null) f(shader); }
        public static int GetShaderiv(uint shader, uint pname) { int v = 0; var f = Proc<GetShaderivD>("glGetShaderiv"); if (f != null) f(shader, pname, out v); return v; }
        public static void DeleteShader(uint shader) { var f = Proc<DeleteShaderD>("glDeleteShader"); if (f != null) f(shader); }
        public static uint CreateProgram() { var f = Proc<CreateProgramD>("glCreateProgram"); return f != null ? f() : 0; }
        public static void AttachShader(uint program, uint shader) { var f = Proc<AttachShaderD>("glAttachShader"); if (f != null) f(program, shader); }
        public static void LinkProgram(uint program) { var f = Proc<LinkProgramD>("glLinkProgram"); if (f != null) f(program); }
        public static int GetProgramiv(uint program, uint pname) { int v = 0; var f = Proc<GetProgramivD>("glGetProgramiv"); if (f != null) f(program, pname, out v); return v; }
        public static int GetUniformLocation(uint program, string name) { var f = Proc<GetUniformLocationD>("glGetUniformLocation"); return f != null ? f(program, name) : -1; }
        public static void Uniform1f(int loc, float v) { var f = Proc<Uniform1fD>("glUniform1f"); if (f != null) f(loc, v); }
        public static void Uniform3f(int loc, float x, float y, float z) { var f = Proc<Uniform3fD>("glUniform3f"); if (f != null) f(loc, x, y, z); }
    }
}
