using System;
using System.ComponentModel.Design;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace AiderVS
{
    /// <summary>The two menu commands: open the chat, and "Add to Aider chat".</summary>
    internal static class Commands
    {
        static readonly Guid CommandSet = new Guid("cb085003-4cca-442c-9580-7b7221a8541c");
        const int ShowChatCommandId = 0x0100;
        const int AddToChatCommandId = 0x0101;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;

            commandService.AddCommand(new MenuCommand(
                (s, e) => package.JoinableTaskFactory.RunAsync(() => ShowChatAsync(package)),
                new CommandID(CommandSet, ShowChatCommandId)));

            commandService.AddCommand(new MenuCommand(
                (s, e) => package.JoinableTaskFactory.RunAsync(() => AddToChatAsync(package)),
                new CommandID(CommandSet, AddToChatCommandId)));
        }

        static async System.Threading.Tasks.Task<AiderToolWindow> ShowChatAsync(AsyncPackage package)
        {
            var window = await package.ShowToolWindowAsync(typeof(AiderToolWindow), 0, true, package.DisposalToken);
            return window as AiderToolWindow;
        }

        static async Task AddToChatAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Read the selection first, because opening the chat moves focus away from it.
            var dte = IdeContext.GetDte();
            var state = IdeContext.GetActiveState(dte);
            var snippet = state != null && state.FromEditor ? IdeContext.GetEditorSelection(dte) : null;

            var window = await ShowChatAsync(package);
            window?.Chat.PinFromIde(state, snippet);
        }
    }
}
