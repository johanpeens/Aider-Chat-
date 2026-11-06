using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace AiderVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(AiderToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 420, Window = ToolWindowGuids80.SolutionExplorer)]
    public sealed class AiderPackage : AsyncPackage
    {
        public const string PackageGuidString = "f6a17576-67d3-4df9-8905-32f885059ab4";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await Commands.InitializeAsync(this);
        }
    }
}
