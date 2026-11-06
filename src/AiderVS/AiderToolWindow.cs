using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace AiderVS
{
    [Guid("2b7157ee-93ad-469b-87b4-cfefc9dad80d")]
    public sealed class AiderToolWindow : ToolWindowPane
    {
        public AiderToolWindow() : base(null)
        {
            Caption = "Aider Chat";
            Content = new ChatControl();
        }

        public ChatControl Chat => (ChatControl)Content;
    }
}
