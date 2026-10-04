using System.IO;
using System.Windows.Threading;
using MiniDrop.Application;
using MiniDrop.Domain;
using MiniDrop.Storage;
using MiniDrop.Tests;
using MiniDrop.Windows;
using MiniDrop.Windows.ViewModels;
using MiniDrop.Windows.Views;
using MiniDrop.Windows.Infrastructure;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class ReliabilityChecks
{
    public static void Run()
    {
        using var h = new AppHarness();
        Services.Configure(h.Db, () => h.Options, h.Send, h.Sync, h.Maintenance, h.Delete, h.Download, h.Pump, h.Transfers);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 40; i++)
        {
            var at = now.AddSeconds(-i);
            var id = Ulid.NewAt(at.ToUnixTimeMilliseconds());
            h.SeedRemoteMessage(UlidClock.MonthOf(id), id);
        }
        var vm = new MainViewModel(() => true);
        Wait(vm.RefreshAsync());
        Check(vm.Timeline.Count == 20 && vm.ShowLoadOlder, "First remote page must expose older history");
        Wait(vm.LoadOlderAsync());
        Check(vm.Timeline.Count == 40, "Loading older remote history must immediately display the new records");
        for (var i = 0; i < 4 && vm.ShowLoadOlder; i++) Wait(vm.LoadOlderAsync());
        Check(!vm.ShowLoadOlder, "Hide older history only after reaching the remote end");
        vm.ReloadFromDb();
        Check(!vm.ShowLoadOlder, "Local refresh must preserve the known remote end");
        Wait(vm.RefreshAsync());
        Check(vm.ShowLoadOlder, "Manual refresh must allow rescanning remote history");

        var draft = new string('a', Limits.MaxTextBytes + 1);
        vm.InputText = draft;
        Wait(vm.SendInputAsync());
        Check(vm.InputText == draft && !vm.Busy, "Failed send must preserve the draft and unlock input");
        vm.InputText = "accepted draft";
        Wait(vm.SendInputAsync());
        Check(vm.InputText.Length == 0 && h.Messages.Count() == 41, "Successful send must clear its submitted draft");
        vm.InputText = "first draft";
        void EditDraft() => vm.InputText = "next draft";
        h.Pump.WakeRequested += EditDraft;
        Wait(vm.SendInputAsync());
        h.Pump.WakeRequested -= EditDraft;
        Check(vm.InputText == "next draft", "Enqueue completion must not erase a newer draft");

        var earliest = now.AddDays(-1);
        h.Db.Write(tx =>
        {
            for (var i = 0; i < 220; i++)
            {
                var at = earliest.AddSeconds(-i);
                var id = Ulid.NewAt(at.ToUnixTimeMilliseconds());
                h.Messages.Insert(tx, new MessageRow(id, UlidClock.MonthOf(id), TestEnv.DeviceId, "Desktop",
                    at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), "local history", Direction.In, JobDao.Now()));
            }
        });
        // Local history remains usable with no account configured and never opens settings.
        var localOnly = new MainViewModel(() => false);
        Check(localOnly.Timeline.Count == 200, "First local page must remain bounded");
        Wait(localOnly.LoadOlderAsync());
        Check(localOnly.Timeline.Count == 262, "Older local records must load without a network request");
        Console.WriteLine("Reliability UI checks passed: remote/local history, remote end, rejected/successful/newer drafts.");
        CheckComposerAndOfflineSettings();
    }

    private static void CheckComposerAndOfflineSettings()
    {
        using var h = new AppHarness();
        Services.Configure(h.Db, () => h.Options, h.Send, h.Sync, h.Maintenance, h.Delete, h.Download, h.Pump, h.Transfers);
        var vm = new MainViewModel(() => true);
        var source = h.MakeTempFile("附件.txt", [1, 2, 3]);
        vm.AddAttachments([source]);
        Check(h.Messages.Count() == 0, "Picking attachments must not send immediately");
        vm.InputText = new string('a', Limits.MaxTextBytes + 1);
        Wait(vm.SendInputAsync());
        Check(vm.Attachments.Count == 1 && vm.InputText.Length > 0 && !vm.SendBusy, "Rejected caption must preserve text and attachments");
        vm.Busy = true; // A remote refresh is in progress; local enqueue must remain available.
        Check(vm.SendCommand.CanExecute(null), "Refreshing must not disable sending");
        vm.InputText = "附件说明";
        Wait(vm.SendInputAsync());
        var message = h.Messages.TimelinePage(1, 0).Single();
        Check(message.Message.Text == "附件说明" && h.Files.GetByMessage(message.Message.Id).Single().SourcePath == source,
            "Text and attachments must belong to one queued message");
        Check(vm.Attachments.Count == 0 && vm.InputText == "" && vm.Busy, "Sending must clear its draft without clearing refresh state");
        vm.AddAttachments([source]);
        vm.RemoveAttachmentCommand.Execute(vm.Attachments[0]);
        Check(File.Exists(source) && vm.Attachments.Count == 0, "Removing an attachment must keep the original file");
        vm.AddAttachments([source]);
        Wait(vm.SendInputAsync());
        Check(h.Messages.Count() == 2 && vm.Attachments.Count == 0, "Attachment-only drafts must send");

        var settings = new AppSettings { RootUrl = "https://127.0.0.1:1/MiniDrop/", Account = "test" };
        AppSettings? saved = null;
        var settingsVm = new SettingsViewModel(() => settings, value => saved = value, () => "test-password");
        settingsVm.DownloadDir = h.Options.DownloadDir;
        var save = settingsVm.SaveAsync("test-password", new Window());
        Wait(save);
        Check(save.Result.Ok && saved?.DownloadDir == h.Options.DownloadDir && settingsVm.TestResult == "",
            "Changing only local settings must save without testing the offline connection");

        var fileId = Guid.NewGuid().ToString();
        var incomingId = Ulid.New();
        var content = new byte[] { 4, 5, 6 };
        h.Server.PutFile($"/MiniDrop/files/{fileId}", content);
        h.SeedRemoteMessage(UlidClock.MonthOf(incomingId), incomingId, "文件快捷操作",
            [new MessageJson.DraftFile(fileId, "资料.txt", content.Length, "text/plain", null)]);
        Wait(h.Sync.RefreshAsync(CancellationToken.None));
        var actions = new List<(string Path, string Action)>();
        var filesVm = new MainViewModel(() => true, (path, action) => actions.Add((path, action)));
        var fileVm = filesVm.Timeline.Single(m => m.Id == incomingId).Files.Single();
        Wait(filesVm.RunFileActionAsync(fileVm, "open"));
        Check(actions.Count == 1 && actions[0].Action == "open" && File.ReadAllBytes(actions[0].Path).SequenceEqual(content),
            "One click must download before opening the completed file");
        var requests = h.Server.CountRequests("GET /MiniDrop/files/");
        Wait(filesVm.RunFileActionAsync(fileVm, "copy"));
        Wait(filesVm.RunFileActionAsync(fileVm, "reveal"));
        Check(actions.Select(a => a.Action).SequenceEqual(new[] { "open", "copy", "reveal" })
            && h.Server.CountRequests("GET /MiniDrop/files/") == requests, "Cached quick actions must reuse the local file");
        File.Delete(actions[0].Path);
        h.Server.FailNext("GET", 503, 1, $"/MiniDrop/files/{fileId}");
        Wait(filesVm.RunFileActionAsync(fileVm, "open"));
        Check(actions.Count == 3 && !fileVm.Busy, "A failed download must not open a file or leave the action locked");

        Console.WriteLine("Workflow checks passed: combined/attachment-only sends, retained drafts, send during refresh, removal, offline settings, download/open/copy/reveal.");
    }

    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (task.IsCompleted) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }
        task.GetAwaiter().GetResult();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
