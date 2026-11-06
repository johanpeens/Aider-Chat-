using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace AiderVS
{
    /// <summary>Raises Changed whenever the VS selection or active window changes.</summary>
    public sealed class SelectionWatcher : IVsSelectionEvents, IDisposable
    {
        readonly IVsMonitorSelection _monitor;
        uint _cookie;

        public event EventHandler Changed;

        public SelectionWatcher()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _monitor = (IVsMonitorSelection)Package.GetGlobalService(typeof(SVsShellMonitorSelection));
            _monitor?.AdviseSelectionEvents(this, out _cookie);
        }

        public int OnSelectionChanged(IVsHierarchy pHierOld, uint itemidOld, IVsMultiItemSelect pMISOld, ISelectionContainer pSCOld,
                                      IVsHierarchy pHierNew, uint itemidNew, IVsMultiItemSelect pMISNew, ISelectionContainer pSCNew)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return VSConstants.S_OK;
        }

        public int OnElementValueChanged(uint elementid, object varValueOld, object varValueNew)
        {
            if (elementid == (uint)VSConstants.VSSELELEMID.SEID_WindowFrame)
                Changed?.Invoke(this, EventArgs.Empty);
            return VSConstants.S_OK;
        }

        public int OnCmdUIContextChanged(uint dwCmdUICookie, int fActive) => VSConstants.S_OK;

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_cookie != 0)
            {
                _monitor?.UnadviseSelectionEvents(_cookie);
                _cookie = 0;
            }
        }
    }
}
