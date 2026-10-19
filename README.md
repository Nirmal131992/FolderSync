# Folder Synchronizer

A one-way Folder sync : ANy change in Source will be populated/synced to Output. Output-only files and folders are deleted. Uses only .NET's standard library; 


```sh
dotnet run --project FolderSynchronizer.csproj -- "/path/to/source" "/path/to/output" 10 "/path/to/logs/sync.txt"
```

Arguments: source directory, output directory, interval in seconds (0 < interval <= 86400), text log path. Quote paths containing spaces. The source must exist; the output and log's parent directory are created as needed. Keep the log outside both trees. Stop with Ctrl+C.
# FolderSync
