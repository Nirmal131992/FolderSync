using System.Globalization;
using System.Security.Cryptography;

internal static class Program
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;


    // Determines whether a path is equal to or located inside a root directory.
    private static bool Within(string path, string root) =>
        path.Equals(root, PathComparison) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);


    // Converts a path to an absolute path and removes its trailing separator,
    // while preserving filesystem roots.
    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));


    // Reads and validates command-line arguments, configures logging,
    // and repeatedly synchronizes the folders until Ctrl+C is pressed.
    // Returns 0 on normal shutdown or 1 if startup or a fatal operation fails.
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: FolderSynchronizer <source> <output> <interval-seconds> <log-file.txt>");
            return 1;
        }

        try
        {
            string source = FullPath(args[0]), output = FullPath(args[1]), logPath = FullPath(args[3]);
            if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || !double.IsFinite(seconds) || seconds <= 0 || seconds > 86400)
                throw new ArgumentException("Interval must be greater than zero and at most 86400 seconds.");
            if (!Directory.Exists(source)) throw new ArgumentException("Source directory does not exist.");
            if (Within(source, output) || Within(output, source))
                throw new ArgumentException("Source and output must be separate, non-overlapping directories.");
            if (Within(logPath, source) || Within(logPath, output))
                throw new ArgumentException("The log file must be outside both synchronized directories.");
            CheckAncestors(source);
            CheckAncestors(output);
            CheckAncestors(logPath);
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            using var writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
            void Log(string message)
            {
                string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
                Console.WriteLine(line);
                writer.WriteLine(line);
            }

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                Log($"START source=\"{source}\" output=\"{output}\" interval={seconds}s");
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        // Validate the complete trees before any deletion in this cycle.
                        CheckAncestors(source);
                        CheckAncestors(output);
                        CheckTree(source);
                        if (Directory.Exists(output)) CheckTree(output);
                        Log("SYNC START");
                        Sync(source, output, Log, cancellation.Token);
                        Log("SYNC COMPLETE");
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log($"ERROR {ex.Message}; cycle aborted, retrying after interval.");
                    }
                    try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancellation.Token); }
                    catch (OperationCanceledException) { break; }
                }
                Log("STOP");
            }
            finally { Console.CancelKeyPress -= handler; }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR {ex.Message}");
            return 1;
        }
    }

    
    // Checks a path and its existing parent directories for symbolic links
    // or reparse points. Ignores paths that do not exist yet.
    private static void CheckAncestors(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try { RejectLink(current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    // Throws an IOException if a path is a symbolic link or reparse point,
    // preventing synchronization from following it to another location.
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Symbolic links/reparse points are not supported: {path}");
    }

    // Recursively checks a directory and all its contents for symbolic links
    // or reparse points before synchronization begins.
    private static void CheckTree(string directory)
    {
        RejectLink(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(entry);
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }


    // Compares file lengths and SHA-256 hashes to determine whether
    // two files contain the same bytes.
    private static bool SameContent(string left, string right)
    {
        if (new FileInfo(left).Length != new FileInfo(right).Length) return false;
        using var a = File.OpenRead(left);
        using var b = File.OpenRead(right);
        return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }


    // Deletes a file or recursively deletes a directory and its contents.
    // Logs each successful removal and checks for cancellation.
    private static void Remove(string path, Action<string> log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectLink(path);
        if (Directory.Exists(path))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(path)) Remove(entry, log, token);
            Directory.Delete(path);
            log($"REMOVE DIRECTORY \"{path}\"");
        }
        else
        {
            File.Delete(path);
            log($"REMOVE FILE \"{path}\"");
        }
    }


    // Recursively mirrors the source directory into the output directory.
    // Creates missing folders, copies new or changed files, handles changes
    // between files and directories, and removes output-only entries.
    // Logs changes and checks for cancellation between operations.
    private static void Sync(string source, string output, Action<string> log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RejectLink(source);
        if (File.Exists(output)) Remove(output, log, token);
        if (!Directory.Exists(output))
        {
            Directory.CreateDirectory(output);
            log($"CREATE DIRECTORY \"{output}\"");
        }
        RejectLink(output);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            RejectLink(entry);
            string target = Path.Combine(output, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Sync(entry, target, log, token);
            else
            {
                if (Directory.Exists(target)) Remove(target, log, token);
                bool exists = File.Exists(target);
                if (exists) RejectLink(target);
                if (!exists || !SameContent(entry, target))
                {
                    File.Copy(entry, target, overwrite: true);
                    log($"{(exists ? "COPY UPDATE" : "CREATE FILE / COPY")} \"{entry}\" -> \"{target}\"");
                }
            }
        }
        
        // Re-check the source before removing output-only entries.
        if (!Directory.Exists(source)) throw new IOException("Source disappeared during synchronization.");
        foreach (string target in Directory.EnumerateFileSystemEntries(output))
        {
            string original = Path.Combine(source, Path.GetFileName(target));
            if (!File.Exists(original) && !Directory.Exists(original)) Remove(target, log, token);
        }
    }
}
