using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;

namespace BridgeSurface
{
    /// <summary>
    /// Add-in shell: one menu command that opens the Bridge Surface PropertyManager page.
    /// </summary>
    [ComVisible(true)]
    [Guid("7B76E24C-D2EF-421D-9FF9-110BDE3AB686")]
    [ProgId("BridgeSurface.SwAddin")]
    public class SwAddin : ISwAddin
    {
        public static SldWorks SwApp;

        /// <summary>
        /// The open PropertyManager page. MUST be held here: if nothing references it, the
        /// garbage collector frees it (and its controls) while SOLIDWORKS is still using them,
        /// which shows up as dead number boxes and access violations.
        /// </summary>
        public static BridgePage ActivePage;

        private int _cookie;
        private ICommandManager _cmdMgr;
        private const int CmdGroupId = 7301;
        private const int CmdBridgeId = 1;

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            SwApp = (SldWorks)ThisSW;
            _cookie = Cookie;
            SwApp.SetAddinCallbackInfo2(0, this, _cookie);
            _cmdMgr = SwApp.GetCommandManager(_cookie);
            AddCommands();
            return true;
        }

        public bool DisconnectFromSW()
        {
            RemoveCommands();
            if (_cmdMgr != null) Marshal.ReleaseComObject(_cmdMgr);
            _cmdMgr = null;
            SwApp = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        private void AddCommands()
        {
            int err = 0;
            ICommandGroup grp = _cmdMgr.CreateCommandGroup2(
                CmdGroupId, "Bridge Surface", "Bridge Surface v0.0.1 (experimental)", "", -1, true, ref err);

            grp.AddCommandItem2(
                "Bridge Surface", -1,
                "Create a bridge surface between two edges or sketch curves",
                "Bridge Surface", 0,
                nameof(OnBridgeSurface), nameof(OnBridgeSurfaceEnable),
                CmdBridgeId, (int)swCommandItemType_e.swMenuItem);

            grp.HasMenu = true;
            grp.HasToolbar = false;
            grp.Activate();
        }

        private void RemoveCommands()
        {
            if (_cmdMgr != null) _cmdMgr.RemoveCommandGroup(CmdGroupId);
        }

        // ---- command callbacks (public: SOLIDWORKS calls them by name) ----

        public void OnBridgeSurface()
        {
            var model = SwApp.ActiveDoc as ModelDoc2;
            if (model == null) return;
            ActivePage = new BridgePage(model, null, null);
            ActivePage.Show();
        }

        public int OnBridgeSurfaceEnable()
        {
            var model = SwApp.ActiveDoc as ModelDoc2;
            return (model != null && model.GetType() == (int)swDocumentTypes_e.swDocPART) ? 1 : 0;
        }

        // ---- COM registration: tells SOLIDWORKS the add-in exists ----

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            string addinKey = @"SOFTWARE\SolidWorks\Addins\{" + t.GUID.ToString() + "}";
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(addinKey))
            {
                k.SetValue(null, 0);
                k.SetValue("Description", "Bridge surface feature v0.0.1 (experimental, provided as is)");
                k.SetValue("Title", "Bridge Surface");
            }
            string startupKey = @"Software\SolidWorks\AddInsStartup\{" + t.GUID.ToString() + "}";
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(startupKey))
            {
                k.SetValue(null, 1);   // load at startup
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            try { Registry.LocalMachine.DeleteSubKey(@"SOFTWARE\SolidWorks\Addins\{" + t.GUID.ToString() + "}", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKey(@"Software\SolidWorks\AddInsStartup\{" + t.GUID.ToString() + "}", false); } catch { }
        }
    }
}
