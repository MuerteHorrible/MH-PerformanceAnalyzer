using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Writes one recording session's files into its folder inside Output, through OutputStore only.
    /// The main thread queues appends and whole-file replaces; a background thread writes them about once
    /// a second, so the game never waits for the disk. Flush() writes everything at once (stop, quit).
    /// </summary>
    internal sealed class SessionWriter : IDisposable
    {
        private sealed class Item
        {
            public string File = "";
            public string Text = "";
            public bool Replace;
        }

        private const int RetryCapChars = 4 * 1024 * 1024;

        private readonly OutputStore store;
        private readonly ConcurrentQueue<Item> queue = new ConcurrentQueue<Item>();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private readonly object ioLock = new object();
        private readonly Thread thread;
        // Appends that failed (file locked by another program, disk full) are retried on the next write.
        private readonly Dictionary<string, StringBuilder> retry = new Dictionary<string, StringBuilder>();
        private volatile bool stopping;
        private volatile string? lastError;
        private int errorCount;

        /// <summary>The session folder's name inside Output.</summary>
        public string FolderName { get; }

        public int ErrorCount => Volatile.Read(ref errorCount);

        /// <summary>The last write error, as a file name and an error type only (never a path).</summary>
        public string? LastError => lastError;

        public SessionWriter(OutputStore store, string folderName)
        {
            this.store = store;
            FolderName = folderName;
            store.CreateDirectory(folderName);
            thread = new Thread(Run) { IsBackground = true, Name = "MHPerformanceAnalyzer writer" };
            thread.Start();
        }

        public void Append(string file, string text) => queue.Enqueue(new Item { File = file, Text = text });

        public void Replace(string file, string text) => queue.Enqueue(new Item { File = file, Text = text, Replace = true });

        public void Flush() => Drain();

        public void Dispose()
        {
            stopping = true;
            signal.Set();
            try
            {
                thread.Join(2000);
            }
            catch (Exception)
            {
            }
            Drain();
        }

        private void Run()
        {
            while (!stopping)
            {
                signal.WaitOne(1000);
                Drain();
            }
        }

        private void Drain()
        {
            lock (ioLock)
            {
                if (queue.IsEmpty && retry.Count == 0)
                {
                    return;
                }
                // Keep the order per file: consecutive appends are joined, a replace starts the file again.
                var order = new List<string>();
                var appends = new Dictionary<string, StringBuilder>();
                var replaces = new Dictionary<string, string>();
                foreach (var kv in retry)
                {
                    order.Add(kv.Key);
                    appends[kv.Key] = kv.Value;
                }
                retry.Clear();
                while (queue.TryDequeue(out var item))
                {
                    if (!appends.ContainsKey(item.File) && !replaces.ContainsKey(item.File))
                    {
                        order.Add(item.File);
                    }
                    if (item.Replace)
                    {
                        replaces[item.File] = item.Text;
                        appends.Remove(item.File);
                    }
                    else
                    {
                        if (!appends.TryGetValue(item.File, out var sb))
                        {
                            sb = new StringBuilder();
                            appends[item.File] = sb;
                        }
                        sb.Append(item.Text);
                    }
                }
                foreach (string file in order)
                {
                    string relative = FolderName + "\\" + file;
                    if (replaces.TryGetValue(file, out var whole))
                    {
                        try
                        {
                            store.WriteText(relative, whole);
                        }
                        catch (Exception ex)
                        {
                            NoteError(file, ex);
                        }
                    }
                    if (appends.TryGetValue(file, out var text))
                    {
                        try
                        {
                            store.AppendText(relative, text.ToString());
                        }
                        catch (Exception ex)
                        {
                            NoteError(file, ex);
                            if (text.Length <= RetryCapChars)
                            {
                                retry[file] = text;
                            }
                        }
                    }
                }
            }
        }

        private void NoteError(string file, Exception ex)
        {
            Interlocked.Increment(ref errorCount);
            lastError = file + ": " + ex.GetType().Name;
        }
    }
}
