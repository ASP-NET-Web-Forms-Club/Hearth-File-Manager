using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Web.Hosting;
using Newtonsoft.Json.Linq;

namespace HearthFileManager.engine.Ai
{
    public class obAiEvent
    {
        public string Kind { get; set; }
        public string Text { get; set; }
    }

    public class AiTask
    {
        public string Id { get; set; }
        public string Owner { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public bool Done { get; set; }
        public obAiTurnResult Result { get; set; }
        public readonly List<obAiEvent> Events = new List<obAiEvent>();
        public CancellationTokenSource Cancel { get; } = new CancellationTokenSource();

        public void Add(string kind, string text)
        {
            lock (Events) Events.Add(new obAiEvent { Kind = kind, Text = text });
        }

        public List<obAiEvent> EventsSince(int index)
        {
            lock (Events) return index < Events.Count ? Events.GetRange(index, Events.Count - index) : new List<obAiEvent>();
        }
    }

    /// <summary>
    /// AI turns can take minutes (rate-limit waits), so they run as background work items and the
    /// browser polls for progress. In-memory: after an app-pool recycle, pollers get "not found".
    /// </summary>
    public static class AiTaskManager
    {
        static readonly ConcurrentDictionary<string, AiTask> Tasks = new ConcurrentDictionary<string, AiTask>();
        /// <summary>One running AI turn at a time across the site: both users share one free-tier key and one website.</summary>
        static int _running;

        public static AiTask Start(string owner, GeminiAgent agent, JArray history, JArray userParts)
        {
            Prune();
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                throw new FsException("The AI is already working on another request. Please wait for it to finish.");

            var task = new AiTask { Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N").Substring(0, 8), Owner = owner };
            Tasks[task.Id] = task;
            agent.OnProgress = task.Add;

            HostingEnvironment.QueueBackgroundWorkItem(ct =>
            {
                try
                {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cancel.Token))
                        task.Result = agent.RunTurn(history, userParts, task.Id, linked.Token);
                }
                catch (Exception ex) { task.Result = new obAiTurnResult { Error = ex.Message, Contents = history }; }
                finally { task.Done = true; Interlocked.Exchange(ref _running, 0); }
            });
            return task;
        }

        public static AiTask Get(string id)
        {
            AiTask t;
            return id != null && Tasks.TryGetValue(id, out t) ? t : null;
        }

        static void Prune()
        {
            foreach (var kv in Tasks)
                if (kv.Value.Done && DateTime.UtcNow - kv.Value.StartedUtc > TimeSpan.FromHours(1))
                {
                    AiTask t;
                    Tasks.TryRemove(kv.Key, out t);
                }
        }
    }
}
