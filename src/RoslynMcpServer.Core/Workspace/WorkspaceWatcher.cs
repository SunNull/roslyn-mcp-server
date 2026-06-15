using System.Collections.Concurrent;

using Microsoft.CodeAnalysis;

namespace RoslynMcpServer.Core.Workspace;

/// <summary>
/// Watches .cs/.csproj files under a root directory and notifies the
/// <see cref="IWorkspaceHost"/> when they change, so the cached
/// <see cref="Compilation"/> stays fresh without a manual reload. Uses a
/// debounce so rapid saves (formatter, IDE) don't trigger a reload storm.
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private readonly IWorkspaceHost _host;
    private readonly string _root;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTimers = new();

    // Debounce window: wait this long after the last change before reloading.
    private const int DebounceMs = 500;

    public event Action<string>? OnFileChanged;

    public WorkspaceWatcher(IWorkspaceHost host, string root)
    {
        _host = host;
        _root = Path.GetFullPath(root);
    }

    /// <summary>
    /// Starts watching. File changes trigger a debounced reload of the host's
    /// workspace so subsequent tool calls see the freshest compilation.
    /// </summary>
    public void Start()
    {
        // Watch .cs files (source changes)
        Watch("*.cs");
        // Watch .csx (C# script files)
        Watch("*.csx");
        // Watch .csproj/.sln/.slnx (project structure changes — added files, new packages)
        Watch("*.csproj");
        Watch("*.sln");
        Watch("*.slnx");
    }

    private void Watch(string pattern)
    {
        // FileSystemWatcher doesn't recurse well with multiple patterns, so
        // create one watcher per pattern with IncludeSubdirectories = true.
        var watcher = new FileSystemWatcher(_root, pattern)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;

        _watchers.Add(watcher);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        OnFileChanged?.Invoke(e.FullPath);
        DebounceReload(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        OnFileChanged?.Invoke(e.FullPath);
        DebounceReload(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // FileSystemWatcher errors (buffer overflow) are non-fatal — the next
        // change event will re-trigger. Log and continue.
    }

    /// <summary>
    /// Debounces the reload: if multiple files change in quick succession
    /// (e.g. a "fix all" refactor), we only reload once after things settle.
    /// </summary>
    private void DebounceReload(string file)
    {
        // Cancel any pending reload for this key (the root, not the file —
        // we reload the whole workspace once, not per-file).
        if (_debounceTimers.TryRemove("global", out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _debounceTimers["global"] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, cts.Token);

                // Timer wasn't cancelled — do the reload.
                if (_debounceTimers.TryRemove("global", out var installed) && installed == cts)
                    cts.Dispose();
                else
                    cts.Dispose();

                // Reload whatever project was loaded. The host knows its own path.
                var loadedPath = _host.LoadedProjectPath;
                if (!string.IsNullOrEmpty(loadedPath))
                {
                    try { await _host.LoadProjectAsync(loadedPath, _cts.Token); } catch { }
                }
            }
            catch (TaskCanceledException)
            {
                // Debounced — a newer change superseded this one.
                cts.Dispose();
            }
            catch
            {
                // Reload failures are non-fatal; the next change retries.
                cts.Dispose();
            }
        }, cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();

        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Changed -= OnChanged;
            w.Created -= OnChanged;
            w.Deleted -= OnChanged;
            w.Renamed -= OnRenamed;
            w.Error -= OnError;
            w.Dispose();
        }
        _watchers.Clear();

        foreach (var (_, cts) in _debounceTimers)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _debounceTimers.Clear();

        _cts.Dispose();
    }
}
