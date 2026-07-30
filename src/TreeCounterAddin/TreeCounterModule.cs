using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace TreeCounterAddin
{
    internal class TreeCounterModule : Module
    {
        private static TreeCounterModule _this;

        public static TreeCounterModule Current =>
            _this ??= (TreeCounterModule)FrameworkApplication.FindModule("TreeCounterAddin_Module");
    }
}
