using System.Diagnostics;
using Interop.UIAutomationClient;

// What a screen reader is told that the managed UI Automation client cannot see: which
// texts are headings, and the announcements an app raises (its toasts). Listens for the
// given number of seconds and prints what arrived from ClaudeTracker.
namespace UiaListen;

internal static class Program
{
    private const int HeadingLevelProperty = 30173;
    private const int NoHeading = 80050;

    [MTAThread]
    private static int Main(string[] args)
    {
        var seconds = args.Length > 0 ? int.Parse(args[0]) : 12;
        var process = Process.GetProcessesByName("ClaudeTracker").FirstOrDefault();
        if (process is null)
        {
            Console.WriteLine("app not running");
            return 1;
        }

        var automation = new CUIAutomation8();
        var root = automation.GetRootElement();
        var mine = automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ProcessIdPropertyId, process.Id);

        Console.WriteLine("=== headings ===");
        var windows = root.FindAll(TreeScope.TreeScope_Children, mine);
        for (var w = 0; w < windows.Length; w++)
        {
            var window = windows.GetElement(w);
            var all = window.FindAll(TreeScope.TreeScope_Descendants, automation.CreateTrueCondition());
            for (var i = 0; i < all.Length; i++)
            {
                var element = all.GetElement(i);
                var level = element.GetCurrentPropertyValue(HeadingLevelProperty) is int value ? value : NoHeading;
                if (level != NoHeading) Console.WriteLine($"  level {level - NoHeading}: '{element.CurrentName}' in '{window.CurrentName}'");
            }
        }

        Console.WriteLine($"=== listening for {seconds} s ===");
        var handler = new Listener(process.Id);
        ((IUIAutomation5)automation).AddNotificationEventHandler(root, TreeScope.TreeScope_Subtree, null, handler);
        Console.WriteLine("ready");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        ((IUIAutomation5)automation).RemoveNotificationEventHandler(root, handler);
        Console.WriteLine($"=== {handler.Count} announcement(s) from the app ===");
        return 0;
    }

    private sealed class Listener(int processId) : IUIAutomationNotificationEventHandler
    {
        public int Count { get; private set; }

        public void HandleNotificationEvent(IUIAutomationElement sender, NotificationKind kind, NotificationProcessing processing, string displayString, string activityId)
        {
            int from;
            try { from = sender.CurrentProcessId; }
            catch (System.Runtime.InteropServices.COMException) { return; }
            if (from != processId) return;
            Count++;
            Console.WriteLine($"  announced: \"{displayString}\"  ({kind}, {processing}, id {activityId})");
        }
    }
}
