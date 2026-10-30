using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Hosting;

namespace HearthFileManager.engine
{
    public class FileTask
    {
        public string Id { get; set; }
        public string Owner { get; set; }
        public string Kind { get; set; } // "zip" or "unzip"
        public string Title { get; set; }
        public string FolderPath { get; set; }
        public string ResultPath { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedUtc { get; set; }
        public string Status { get; set; } = "running"; // "running", "done", "canceled", "error"
        public string Message { get; set; } = "";
        public bool Dismissed { get; set; }
        public readonly FsService.obZipProgress Progress = new FsService.obZipProgress();
        public readonly CancellationTokenSource CancelSource = new CancellationTokenSource();

        public bool Done => Status != "running";

        public long ElapsedMs
        {
            get
            {
                DateTime end = CompletedUtc ?? DateTime.UtcNow;
                return Math.Max(0, (long)(end - StartedUtc).TotalMilliseconds);
            }
        }

        public int Percent
        {
            get
            {
                if (Status == "done") return 100;
                long tb = Interlocked.Read(ref Progress.TotalBytes);
                long db = Interlocked.Read(ref Progress.DoneBytes);
                if (tb > 0) return (int)Math.Min(99, Math.Max(0, db * 100 / tb));
                int tf = Progress.TotalFiles;
                int df = Progress.DoneFiles;
                if (tf > 0) return (int)Math.Min(99, Math.Max(0, (long)df * 100 / tf));
                return 0;
            }
        }

        public object ToDto()
        {
            return new
            {
                TaskId = Id,
                Kind,
                Title,
                FolderPath,
                ResultPath,
                Status,
                Done,
                Percent,
                DoneFiles = Progress.DoneFiles,
                TotalFiles = Progress.TotalFiles,
                DoneBytes = Interlocked.Read(ref Progress.DoneBytes),
                TotalBytes = Interlocked.Read(ref Progress.TotalBytes),
                CurrentFile = Progress.CurrentFile ?? "",
                Message,
                StartedUtc = StartedUtc.ToString("o"),
                CompletedUtc = CompletedUtc.HasValue ? CompletedUtc.Value.ToString("o") : null,
                ElapsedMs
            };
        }
    }

    /// <summary>
    /// Runs long-running Zip / Unzip tasks in the background with byte-level progress and cancellation.
    /// Keeps completed tasks until the user dismisses the completion box (or pruned after 2 hours).
    /// </summary>
    public static class FileTaskManager
    {
        static readonly ConcurrentDictionary<string, FileTask> Tasks = new ConcurrentDictionary<string, FileTask>();
        static readonly object StartLock = new object();

        public static FileTask StartZip(string owner, FsService fs, IList<string> rels, string destZipRel)
        {
            if (rels == null || rels.Count == 0) throw new FsException("Nothing selected.");
            string targetZipRel = fs.UniqueName(destZipRel);
            string zipName = FsService.NameOf(targetZipRel);
            string folderRel = FsService.ParentOf(targetZipRel);
            string title = $"Zipping {rels.Count} item(s) → {zipName}";

            return StartInternal(owner, "zip", title, folderRel, (task, ct) =>
            {
                string createdRel = fs.Zip(rels, targetZipRel, task.Progress, ct);
                task.ResultPath = createdRel;
                string full = fs.Resolve(createdRel);
                long finalSize = File.Exists(full) ? new FileInfo(full).Length : 0;
                task.Message = $"Created {FsService.NameOf(createdRel)} ({ task.Progress.DoneFiles } file(s), { FormatSize(finalSize) })";
            });
        }

        public static FileTask StartUnzip(string owner, FsService fs, string zipRel, string destFolderRel)
        {
            zipRel = FsService.NormalizeRel(zipRel);
            destFolderRel = FsService.NormalizeRel(destFolderRel);
            if (!File.Exists(fs.Resolve(zipRel))) throw new FsException("Zip file not found: " + zipRel);
            string zipName = FsService.NameOf(zipRel);
            string destLabel = string.IsNullOrEmpty(destFolderRel) ? "/" : "/" + destFolderRel;
            string title = $"Extracting {zipName} → {destLabel}";

            return StartInternal(owner, "unzip", title, destFolderRel, (task, ct) =>
            {
                int count = fs.Unzip(zipRel, destFolderRel, task.Progress, ct);
                task.ResultPath = destFolderRel;
                task.Message = $"Extracted {count} file(s) from {zipName} into {destLabel}";
            });
        }

        static FileTask StartInternal(string owner, string kind, string title, string folderRel, Action<FileTask, CancellationToken> work)
        {
            Prune();
            FileTask task;
            lock (StartLock)
            {
                foreach (var kv in Tasks)
                {
                    FileTask existing = kv.Value;
                    if (!string.Equals(existing.Owner, owner, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!existing.Done)
                        throw new FsException("Another zip or unzip operation is already running. Wait for it to finish or cancel it first.");
                    existing.Dismissed = true;
                }

                task = new FileTask
                {
                    Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N").Substring(0, 8),
                    Owner = owner ?? "",
                    Kind = kind,
                    Title = title,
                    FolderPath = FsService.NormalizeRel(folderRel)
                };
                Tasks[task.Id] = task;
            }

            Action<CancellationToken> runner = hostCt =>
            {
                try
                {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCt, task.CancelSource.Token))
                    {
                        work(task, linked.Token);
                        task.Status = "done";
                    }
                }
                catch (OperationCanceledException)
                {
                    task.Status = "canceled";
                    if (task.Kind == "zip")
                        task.Message = "Zip canceled — incomplete zip file was cleaned up.";
                    else
                        task.Message = $"Unzip canceled after extracting {task.Progress.DoneFiles} of {task.Progress.TotalFiles} file(s) — half-written file was cleaned up.";
                }
                catch (Exception ex)
                {
                    if (task.CancelSource.IsCancellationRequested)
                    {
                        task.Status = "canceled";
                        task.Message = task.Kind == "zip"
                            ? "Zip canceled — incomplete zip file was cleaned up."
                            : $"Unzip canceled after extracting {task.Progress.DoneFiles} of {task.Progress.TotalFiles} file(s) — half-written file was cleaned up.";
                    }
                    else
                    {
                        task.Status = "error";
                        task.Message = ex.Message;
                    }
                }
                finally
                {
                    task.CompletedUtc = DateTime.UtcNow;
                }
            };

            if (HostingEnvironment.IsHosted)
                HostingEnvironment.QueueBackgroundWorkItem(runner);
            else
                Task.Run(() => runner(CancellationToken.None));

            return task;
        }

        public static FileTask Get(string id, string owner)
        {
            FileTask t;
            if (string.IsNullOrEmpty(id) || !Tasks.TryGetValue(id, out t)) return null;
            if (!string.Equals(t.Owner, owner, StringComparison.OrdinalIgnoreCase)) return null;
            return t;
        }

        /// <summary>Returns the user's running task, or their most recent un-dismissed completed task.</summary>
        public static FileTask LatestForUser(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return null;
            FileTask latest = null;
            foreach (var kv in Tasks)
            {
                FileTask t = kv.Value;
                if (!string.Equals(t.Owner, owner, StringComparison.OrdinalIgnoreCase) || t.Dismissed) continue;
                if (!t.Done) return t;
                if (latest == null || t.StartedUtc > latest.StartedUtc) latest = t;
            }
            return latest;
        }

        public static FileTask Cancel(string id, string owner)
        {
            FileTask t = Get(id, owner);
            if (t == null) throw new FsException("Task not found.");
            if (!t.Done)
            {
                try { t.CancelSource.Cancel(); } catch { }
            }
            return t;
        }

        public static void Dismiss(string id, string owner)
        {
            FileTask t = Get(id, owner);
            if (t != null) t.Dismissed = true;
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            string[] u = { "KB", "MB", "GB", "TB" };
            double n = bytes;
            int i = -1;
            do { n /= 1024.0; i++; } while (n >= 1024.0 && i < u.Length - 1);
            return (n < 10 ? n.ToString("0.0") : Math.Round(n).ToString("0")) + " " + u[i];
        }

        static void Prune()
        {
            foreach (var kv in Tasks)
            {
                FileTask t = kv.Value;
                if (t.Done && DateTime.UtcNow - t.StartedUtc > TimeSpan.FromHours(2))
                {
                    FileTask removed;
                    Tasks.TryRemove(kv.Key, out removed);
                }
            }
        }
    }
}
