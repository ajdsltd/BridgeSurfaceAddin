using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace BridgeSurface
{
    /// <summary>
    /// The macro feature. SOLIDWORKS calls Regenerate on every rebuild; it re-reads the
    /// stored edge references + parameters and returns a fresh sheet body.
    /// Side A is stored with selection mark 1, side B with mark 2 (each an Edge or a SketchSegment).
    /// </summary>
    [ComVisible(true)]
    [Guid("AC4E4184-7CFC-4E5F-8A8D-096BBEC5A2A1")]
    [ProgId(ProgIdName)]
    public class BridgeFeature : ISwComFeature
    {
        public const string ProgIdName = "BridgeSurface.BridgeFeature";
        public const string BaseName = "Bridge Surface";

        /// <summary>Shown by SOLIDWORKS when a part with this feature is opened without the add-in.</summary>
        public const string ProviderText = "Bridge Surface add-in v0.0.1 by AJ Design Studio (ajdesignstudio.co.nz)";

        /// <summary>Stamp the provider on the definition (set in Regenerate, so older features get it too).</summary>
        private static void SetProvider(MacroFeatureData data)
        {
            try { if (data.Provider != ProviderText) data.Provider = ProviderText; }
            catch { /* informational only: never fail a rebuild over it */ }
        }

        // ------------------------------------------------------------------
        // Insert (called from the PropertyManager page on OK)
        // ------------------------------------------------------------------
        public static Feature Insert(ModelDoc2 model, object edgeA, object edgeB, BridgeOptions opt, out string error)
        {
            error = null;
            SelectEdges(model, edgeA, edgeB);

            string[] names = BridgeOptions.ParamNames;
            string[] values = opt.ToValues();
            var types = new int[names.Length];
            for (int i = 0; i < types.Length; i++)
                types[i] = (int)swMacroFeatureParamType_e.swMacroFeatureParamTypeString;

            var feat = model.FeatureManager.InsertMacroFeature3(
                BaseName, ProgIdName, null,
                names, types, values,
                null, null,          // no dimensions yet
                null,                // no edit body: the feature creates a new body
                null,                // no icons yet
                (int)swMacroFeatureOptions_e.swMacroFeatureByDefault) as Feature;

            if (feat == null) error = "InsertMacroFeature3 returned nothing";
            model.ClearSelection2(true);
            return feat;
        }

        /// <summary>Select both inputs with marks 1 / 2. An input is an Edge or a SketchSegment.</summary>
        internal static void SelectEdges(ModelDoc2 model, object edgeA, object edgeB)
        {
            model.ClearSelection2(true);
            var selMgr = (SelectionMgr)model.SelectionManager;
            var sd = (SelectData)selMgr.CreateSelectData();
            sd.Mark = 1; SelectOne(edgeA, sd);
            sd.Mark = 2; SelectOne(edgeB, sd);
        }

        private static void SelectOne(object o, SelectData sd)
        {
            var seg = o as SketchSegment;
            if (seg != null) { seg.Select4(true, sd); return; }
            var ent = o as Entity;
            if (ent != null) ent.Select4(true, sd);
        }

        // ------------------------------------------------------------------
        // ISwComFeature
        // ------------------------------------------------------------------
        public object Regenerate(object app, object modelDoc, object feature)
        {
            try
            {
                var swApp = (SldWorks)app;
                var feat = (Feature)feature;
                var data = (MacroFeatureData)feat.GetDefinition();
                SetProvider(data);

                BridgeOptions opt = ReadOptions(data);
                object eA, eB;
                if (!ReadEdges(data, out eA, out eB))
                    return "Bridge Surface: missing edge reference(s). Edit the feature and reselect.";

                string report;
                Body2 body = BridgeBuilder.Build(swApp, eA, eB, opt, out report);
                if (body == null) return "Bridge Surface: " + report;

                AssignUserIds(data, body);
                return body;
            }
            catch (Exception ex)
            {
                return "Bridge Surface: " + ex.Message;   // a string return shows as a rebuild error
            }
        }

        public object Edit(object app, object modelDoc, object feature)
        {
            var swApp = (SldWorks)app;
            // Registered but switched off in Tools > Add-ins: SOLIDWORKS still calls Edit (and Regenerate, which
            // works), but the add-in never connected, so its page cannot be set up. Say so instead of failing.
            if (SwAddin.SwApp == null)
            {
                swApp.SendMsgToUser("Bridge Surface: the add-in is not loaded. Tick Bridge Surface in Tools > Add-ins to edit this feature.");
                return false;
            }

            MacroFeatureData data = null;
            bool access = false;
            try
            {
                var model = (ModelDoc2)modelDoc;
                var feat = (Feature)feature;
                data = (MacroFeatureData)feat.GetDefinition();
                access = data.AccessSelections(model, null);
                if (!access) return false;
                SwAddin.ActivePage = new BridgePage(model, feat, data);
                SwAddin.ActivePage.Show();
                return true;
            }
            catch (Exception ex)
            {
                // roll the model forward again and report, rather than SOLIDWORKS's "serious error" dialog
                if (access) { try { data.ReleaseSelectionAccess(); } catch { } }
                SwAddin.ActivePage = null;
                swApp.SendMsgToUser("Bridge Surface: Edit Feature failed (" + ex.Message + ").");
                return false;
            }
        }

        public object Security(object app, object modelDoc, object feature)
        {
            return null;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        internal static BridgeOptions ReadOptions(MacroFeatureData data)
        {
            object names, types, values;
            data.GetParameters(out names, out types, out values);
            return BridgeOptions.FromValues(ToStrings(names), ToStrings(values));
        }

        internal static bool ReadEdges(MacroFeatureData data, out object eA, out object eB)
        {
            eA = eB = null;
            object objs, objTypes, marks, drViews, xforms;
            data.GetSelections3(out objs, out objTypes, out marks, out drViews, out xforms);
            var oa = objs as object[];
            var ma = marks as int[];
            if (oa == null || ma == null) return false;
            for (int i = 0; i < oa.Length; i++)
            {
                if (!(oa[i] is Edge) && !(oa[i] is SketchSegment)) continue;
                if (ma[i] == 1) eA = oa[i];
                else if (ma[i] == 2) eB = oa[i];
            }
            return eA != null && eB != null;
        }

        /// <summary>Stable IDs so downstream features (knit, fillet) keep their references.</summary>
        private static void AssignUserIds(MacroFeatureData data, Body2 body)
        {
            object faces, edges;
            data.GetEntitiesNeedUserId(body, out faces, out edges);
            var fa = faces as object[];
            if (fa != null)
                for (int i = 0; i < fa.Length; i++) data.SetFaceUserId((Face2)fa[i], i, 0);
            var ea = edges as object[];
            if (ea != null)
                for (int i = 0; i < ea.Length; i++) data.SetEdgeUserId((Edge)ea[i], i, 0);
        }

        private static string[] ToStrings(object o)
        {
            var s = o as string[];
            if (s != null) return s;
            var a = o as object[];
            if (a == null) return new string[0];
            var r = new string[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = Convert.ToString(a[i], System.Globalization.CultureInfo.InvariantCulture);
            return r;
        }
    }
}
