using Files.Shared.Enums;
using Files.App.Helpers;
using Files.Shared.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Files.App.Filesystem.FilesystemHistory
{
    public class StorageHistoryOperations : IStorageHistoryOperations
    {
        private IFilesystemHelpers helpers;
        private IFilesystemOperations operations;

        private readonly CancellationToken cancellationToken;

        public StorageHistoryOperations(IShellPage associatedInstance, CancellationToken cancellationToken)
        {
            this.cancellationToken = cancellationToken;
            helpers = associatedInstance.FilesystemHelpers;
            operations = new ShellFilesystemOperations(associatedInstance);
        }

        public async Task<ReturnResult> Undo(IStorageHistory history)
        {
            ReturnResult returnStatus = ReturnResult.InProgress;
            Progress<FileSystemStatusCode> errorCode = new();

            errorCode.ProgressChanged += (s, e) => returnStatus = e.ToStatus();

            switch (history.OperationType)
            {
                case FileOperationType.CreateNew: // Opposite: Delete created items
                    if (!IsHistoryNull(history.Source))
                    {
                        return await helpers.DeleteItemsAsync(history.Source, false, true, false);
                    }
                    break;
                case FileOperationType.CreateLink: // Opposite: Delete created items
                    if (!IsHistoryNull(history.Destination))
                    {
                        return await helpers.DeleteItemsAsync(history.Destination, false, true, false);
                    }
                    break;
                case FileOperationType.Rename: // Opposite: Restore original item names
                    if (!IsHistoryNull(history))
                    {
                        NameCollisionOption collision = NameCollisionOption.GenerateUniqueName;
                        for (int i = 0; i < history.Destination.Count(); i++)
                        {
                            string name = Path.GetFileName(history.Source[i].Path);
                            await operations.RenameAsync(history.Destination[i], name, collision, errorCode, cancellationToken);
                        }
                    }
                    break;
                case FileOperationType.Copy: // Opposite: Delete copied items
                    if (!IsHistoryNull(history.Destination))
                    {
                        return await helpers.DeleteItemsAsync(history.Destination, false, true, false);
                    }
                    break;
                case FileOperationType.Move: // Opposite: Move the items to original directory
                    if (!IsHistoryNull(history))
                    {
                        return await helpers.MoveItemsAsync(history.Destination, history.Source.Select(item => item.Path), false, false);
                    }
                    break;
                case FileOperationType.Extract: // Opposite: No opposite for archive extraction
                    returnStatus = ReturnResult.Success;
                    Debugger.Break();
                    break;
                case FileOperationType.Recycle: // Opposite: Restore recycled items
                    if (!IsHistoryNull(history))
                    {
                        returnStatus = await helpers.RestoreItemsFromTrashAsync(history.Destination, history.Source.Select(item => item.Path), false);
                        if (returnStatus is ReturnResult.IntegrityCheckFailed) // Not found, corrupted
                        {
                            App.HistoryWrapper.RemoveHistory(history, false);
                        }
                    }
                    break;
                case FileOperationType.Restore: // Opposite: Move restored items to Recycle Bin
                    if (!IsHistoryNull(history.Destination))
                    {
                        var newHistory = await operations.DeleteItemsAsync(history.Destination, null, errorCode, false, cancellationToken);
                        if (newHistory is null)
                        {
                            App.HistoryWrapper.RemoveHistory(history, false);
                        }
                        else
                        {
                            // We need to change the recycled item paths (since IDs are different) - for Redo() to work
                            App.HistoryWrapper.ModifyCurrentHistory(newHistory);
                        }
                    }
                    break;
                case FileOperationType.Delete: // Opposite: No opposite for pernament deletion
                    returnStatus = ReturnResult.Success;
                    break;
            }

            return returnStatus;
        }

        public async Task<ReturnResult> Redo(IStorageHistory history)
        {
            ReturnResult returnStatus = ReturnResult.InProgress;
            Progress<FileSystemStatusCode> errorCode = new();

            errorCode.ProgressChanged += (s, e) => { returnStatus = e.ToStatus(); };

            switch (history.OperationType)
            {
                case FileOperationType.CreateNew:
                    if (IsHistoryNull(history))
                    {
                        foreach (var source in history.Source)
                        {
                            await operations.CreateAsync(source, errorCode, cancellationToken);
                        }
                    }
                    break;
                case FileOperationType.CreateLink:
                    if (!IsHistoryNull(history))
                    {
                        await operations.CreateShortcutItemsAsync(history.Source,
                            await history.Destination.Select(item => item.Path).ToListAsync(), null, errorCode, cancellationToken);
                    }
                    break;
                case FileOperationType.Rename:
                    if (!IsHistoryNull(history))
                    {
                        NameCollisionOption collision = NameCollisionOption.GenerateUniqueName;
                        for (int i = 0; i < history.Source.Count; i++)
                        {
                            string name = Path.GetFileName(history.Destination[i].Path);
                            await operations.RenameAsync(history.Source[i], name, collision, errorCode, cancellationToken);
                        }
                    }
                    break;
                case FileOperationType.Copy:
                    if (!IsHistoryNull(history))
                    {
                        return await helpers.CopyItemsAsync(history.Source, history.Destination.Select(item => item.Path), false, false);
                    }
                    break;
                case FileOperationType.Move:
                    if (!IsHistoryNull(history))
                    {
                        return await helpers.MoveItemsAsync(history.Source, history.Destination.Select(item => item.Path), false, false);
                    }
                    break;
                case FileOperationType.Extract:
                    returnStatus = ReturnResult.Success;
                    Debugger.Break();
                    break;
                case FileOperationType.Recycle: // Recycle PASS
                    if (!IsHistoryNull(history.Destination))
                    {
                        var newHistory = await operations.DeleteItemsAsync(history.Source, null, errorCode, false, cancellationToken);
                        if (newHistory is null)
                        {
                            App.HistoryWrapper.RemoveHistory(history, true);
                        }
                        else
                        {
                            // We need to change the recycled item paths (since IDs are different) - for Undo() to work
                            App.HistoryWrapper.ModifyCurrentHistory(newHistory);
                        }
                    }
                    break;
                case FileOperationType.Restore:
                    if (!IsHistoryNull(history))
                    {
                        await helpers.RestoreItemsFromTrashAsync(history.Source, history.Destination.Select(item => item.Path), false);
                    }
                    break;
                case FileOperationType.Delete:
                    returnStatus = ReturnResult.Success;
                    break;
            }

            return returnStatus;
        }

        public void Dispose()
        {
            helpers?.Dispose();
            helpers = null;

            operations?.Dispose();
            operations = null;
        }

        private bool IsHistoryNull(IStorageHistory history) // history.Destination is null with CreateNew
            => IsHistoryNull(history.Source) || (history.Destination is not null && IsHistoryNull(history.Destination));
        private bool IsHistoryNull(IEnumerable<IStorageItemWithPath> source) => !source.All(HasPath);

        private static bool HasPath(IStorageItemWithPath item) => item is not null && !string.IsNullOrWhiteSpace(item.Path);
    }
}