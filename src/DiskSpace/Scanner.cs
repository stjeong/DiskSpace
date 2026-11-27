using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace DiskSpace;

/// <summary>A file or directory in the scanned tree.</summary>
public abstract class Node
{
    internal long size;

    protected Node(string name, DirNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    public string Name { get; }
    public DirNode? Parent { get; }

    /// <summary>Total bytes (grows while the scan is running).</summary>
    public long Size => Interlocked.Read(ref size);

    public abstract bool IsDirectory { get; }

    public virtual string FullPath =>
        Parent is null ? Name : Path.Combine(Parent.FullPath, Name);
}

public sealed class DirNode : Node
{
    private volatile Node[]? children;
    private volatile bool scanned;

    public DirNode(string name, DirNode? parent) : base(name, parent) { }

    public override bool IsDirectory => true;

    /// <summary>Children; null until this directory has been enumerated. Assigned exactly once.</summary>
    public Node[]? Children => children;

    public bool IsScanned => scanned;

    internal void SetChildren(Node[] items)
    {
        children = items;
        scanned = true;
    }
}

public sealed class FileNode : Node
{
    public FileNode(string name, DirNode parent, long length, int aggregateCount = 0) : base(name, parent)
    {
        size = length;
        AggregateCount = aggregateCount;
    }

    public override bool IsDirectory => false;

    /// <summary>&gt;0 when this node stands for several small files lumped together.</summary>
    public int AggregateCount { get; }

    public bool IsAggregate => AggregateCount > 0;

    public override string FullPath => IsAggregate ? Parent!.FullPath : base.FullPath;
}

/// <summary>
/// Scans a directory tree in parallel using one worker thread per logical CPU core.
/// Directories are pushed onto a shared work stack; any idle worker picks up the next one.
/// </summary>
public sealed class Scanner : IDisposable
{
    // Files smaller than this are lumped into one "[N files]" node per directory to keep memory low.
    private const long SmallFileThreshold = 64 * 1024;
    private const int MaxIndividualFiles = 256;

    private readonly BlockingCollection<DirNode> queue = new(new ConcurrentStack<DirNode>());
    private readonly CancellationTokenSource cts = new();
    private readonly long expectedBytes;
    private Thread[] workers = [];
    private int pending;
    private long dirsScanned;
    private long dirsFound;
    private long filesFound;
    private long errorCount;
    private readonly ConcurrentQueue<string> accessDenied = new();
    private volatile bool completed;
    private readonly System.Diagnostics.Stopwatch stopwatch = new();

    public Scanner(string rootPath)
    {
        RootPath = rootPath;
        Root = new DirNode(rootPath, null);
        ThreadCount = Environment.ProcessorCount;

        // When scanning a whole drive, "used space" gives a good target for the progress percentage.
        try
        {
            string? driveRoot = Path.GetPathRoot(rootPath);
            if (driveRoot is not null &&
                string.Equals(driveRoot.TrimEnd('\\'), rootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                var di = new DriveInfo(driveRoot);
                if (di.IsReady)
                    expectedBytes = di.TotalSize - di.TotalFreeSpace;
            }
        }
        catch
        {
            expectedBytes = 0;
        }
    }

    public string RootPath { get; }
    public DirNode Root { get; }
    public int ThreadCount { get; }
    public bool IsCompleted => completed;
    public bool IsCancelled => cts.IsCancellationRequested;

    /// <summary>True while the scan is in progress (not finished and not cancelled).</summary>
    public bool IsRunning => !completed && !cts.IsCancellationRequested;
    public long DirectoriesScanned => Interlocked.Read(ref dirsScanned);
    public long DirectoriesFound => Interlocked.Read(ref dirsFound);
    public long FilesFound => Interlocked.Read(ref filesFound);
    public long Errors => Interlocked.Read(ref errorCount);

    /// <summary>Number of directories that could not be opened because access was denied.</summary>
    public int AccessDeniedCount => accessDenied.Count;

    /// <summary>Snapshot of the directories that could not be opened because access was denied.</summary>
    public string[] GetAccessDeniedPaths() => accessDenied.ToArray();
    public TimeSpan Elapsed => stopwatch.Elapsed;

    /// <summary>Estimated completion in the range 0..1.</summary>
    public double Progress
    {
        get
        {
            if (completed) return 1.0;
            double p;
            if (expectedBytes > 0)
            {
                p = Root.Size / (double)expectedBytes;
            }
            else
            {
                long found = DirectoriesFound;
                p = found == 0 ? 0 : DirectoriesScanned / (double)found;
            }
            return Math.Clamp(p, 0, 0.99);
        }
    }

    public void Start()
    {
        stopwatch.Start();
        pending = 1;
        dirsFound = 1;
        queue.Add(Root);

        workers = new Thread[ThreadCount];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"Scanner #{i + 1}",
                Priority = ThreadPriority.BelowNormal,
            };
            workers[i].Start();
        }
    }

    public void Cancel()
    {
        if (!cts.IsCancellationRequested)
            cts.Cancel();
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (DirNode dir in queue.GetConsumingEnumerable(cts.Token))
            {
                ScanDirectory(dir);
                Interlocked.Increment(ref dirsScanned);

                // Children were added to 'pending' before this decrement, so 0 means everything is done.
                if (Interlocked.Decrement(ref pending) == 0)
                {
                    stopwatch.Stop();
                    completed = true;
                    queue.CompleteAdding();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            // queue completed while adding (cancellation race) – nothing to do.
        }
    }

    private readonly struct Entry(string name, bool isDir, long length)
    {
        public readonly string Name = name;
        public readonly bool IsDir = isDir;
        public readonly long Length = length;
    }

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false, // let access-denied directories throw so they can be reported
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0, // include hidden & system files (pagefile.sys, hiberfil.sys, ...)
        BufferSize = 64 * 1024,
    };

    /// <summary>
    /// Bytes the file occupies. For sparse files the logical length can be far larger than what is
    /// actually allocated (e.g. VM disks, WSL vhdx, database files), so use the allocated size instead.
    /// </summary>
    private static long GetFileSize(ref FileSystemEntry e)
    {
        long length = e.Length;
        if ((e.Attributes & FileAttributes.SparseFile) != 0)
        {
            long allocated = NativeMethods.GetAllocatedFileSize(Path.Join(e.Directory, e.FileName));
            if (allocated >= 0)
                return Math.Min(allocated, length);
        }
        return length;
    }

    private void ScanDirectory(DirNode dir)
    {
        var subDirs = new List<DirNode>();
        var files = new List<Entry>();

        try
        {
            var enumerable = new FileSystemEnumerable<Entry>(
                dir.FullPath,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.IsDirectory ? 0 : GetFileSize(ref e)),
                Options)
            {
                // Skip junctions / symlinks to avoid double counting and cycles.
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    !e.IsDirectory || (e.Attributes & FileAttributes.ReparsePoint) == 0,
            };

            foreach (Entry entry in enumerable)
            {
                if (cts.IsCancellationRequested) return;
                if (entry.IsDir)
                    subDirs.Add(new DirNode(entry.Name, dir));
                else
                    files.Add(entry);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            accessDenied.Enqueue(dir.FullPath);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref errorCount);
        }

        // Build the child list: big files individually, small ones lumped together.
        long fileTotal = 0;
        var children = new List<Node>(subDirs.Count + Math.Min(files.Count, MaxIndividualFiles) + 1);
        children.AddRange(subDirs);

        files.Sort((a, b) => b.Length.CompareTo(a.Length));
        long restSize = 0;
        int restCount = 0;
        for (int i = 0; i < files.Count; i++)
        {
            Entry f = files[i];
            fileTotal += f.Length;
            if (i < MaxIndividualFiles && f.Length >= SmallFileThreshold)
            {
                children.Add(new FileNode(f.Name, dir, f.Length));
            }
            else
            {
                restSize += f.Length;
                restCount++;
            }
        }
        if (restCount > 0)
            children.Add(new FileNode($"[{restCount:N0} files]", dir, restSize, restCount));

        dir.SetChildren(children.ToArray());

        Interlocked.Add(ref filesFound, files.Count);
        if (fileTotal > 0)
        {
            for (Node? n = dir; n is not null; n = n.Parent)
                Interlocked.Add(ref n.size, fileTotal);
        }

        if (subDirs.Count > 0)
        {
            Interlocked.Add(ref dirsFound, subDirs.Count);
            Interlocked.Add(ref pending, subDirs.Count);
            foreach (DirNode sub in subDirs)
                queue.Add(sub);
        }
    }

    public void Dispose()
    {
        // Workers may still be observing the token; cancel only (the CTS holds no unmanaged timer).
        Cancel();
    }
}
