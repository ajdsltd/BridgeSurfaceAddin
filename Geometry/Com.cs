using System.Runtime.InteropServices;

namespace BridgeSurface
{
    /// <summary>
    /// Deterministic release of temporary SOLIDWORKS COM objects on the calling (main) thread.
    /// Left to the garbage collector, they are released later from the finalizer thread,
    /// which corrupts SOLIDWORKS state (dead PropertyManager controls, access violations).
    /// </summary>
    internal static class Com
    {
        public static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o))
            {
                // ReleaseComObject (one decrement per time the object was handed to us), not
                // FinalReleaseComObject: the runtime shares one wrapper per COM object, so a
                // vertex's GetEdges() can return the very wrapper the caller is still using
                // for the reference edge. A final release would kill the caller's reference.
                try { Marshal.ReleaseComObject(o); } catch { }
            }
        }

        public static void Release(params object[] objs)
        {
            foreach (var o in objs) Release(o);
        }
    }
}
