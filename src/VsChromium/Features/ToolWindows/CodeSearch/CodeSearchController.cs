﻿// Copyright 2014 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using Microsoft.VisualStudio.Text;
using VsChromium.Core.Files;
using VsChromium.Core.Ipc;
using VsChromium.Core.Ipc.TypedMessages;
using VsChromium.Core.Linq;
using VsChromium.Core.Threads;
using VsChromium.Features.SourceExplorerHierarchy;
using VsChromium.Package;
using VsChromium.Settings;
using VsChromium.Threads;
using VsChromium.Views;
using VsChromium.Wpf;

namespace VsChromium.Features.ToolWindows.CodeSearch {
  public class CodeSearchController : ICodeSearchController {
    private static class OperationsIds {
      public const string SearchCode = "files-contents-search";
      public const string SearchFilePaths = "file-names-search";
    }

    private readonly CodeSearchControl _control;
    private readonly IUIRequestProcessor _uiRequestProcessor;
    private readonly IProgressBarTracker _progressBarTracker;
    private readonly IStandarImageSourceFactory _standarImageSourceFactory;
    private readonly IWindowsExplorer _windowsExplorer;
    private readonly IClipboard _clipboard;
    private readonly ISynchronizationContextProvider _synchronizationContextProvider;
    private readonly IOpenDocumentHelper _openDocumentHelper;
    private readonly IEventBus _eventBus;
    private readonly IGlobalSettingsProvider _globalSettingsProvider;
    private readonly TaskCancellation _taskCancellation;

    /// <summary>
    /// For generating unique id n progress bar tracker.
    /// </summary>
    private int _operationSequenceId;

    public CodeSearchController(
      CodeSearchControl control,
      IUIRequestProcessor uiRequestProcessor,
      IProgressBarTracker progressBarTracker,
      IStandarImageSourceFactory standarImageSourceFactory,
      IWindowsExplorer windowsExplorer,
      IClipboard clipboard,
      ISynchronizationContextProvider synchronizationContextProvider,
      IOpenDocumentHelper openDocumentHelper,
      IEventBus eventBus,
      IGlobalSettingsProvider globalSettingsProvider) {
      _control = control;
      _uiRequestProcessor = uiRequestProcessor;
      _progressBarTracker = progressBarTracker;
      _standarImageSourceFactory = standarImageSourceFactory;
      _windowsExplorer = windowsExplorer;
      _clipboard = clipboard;
      _synchronizationContextProvider = synchronizationContextProvider;
      _openDocumentHelper = openDocumentHelper;
      _eventBus = eventBus;
      _globalSettingsProvider = globalSettingsProvider;
      _taskCancellation = new TaskCancellation();

      // Ensure initial values are in sync.
      GlobalSettingsOnPropertyChanged(null, null);

      // Ensure changes to ViewModel are synchronized to global settings
      ViewModel.PropertyChanged += ViewModelOnPropertyChanged;

      // Ensure changes to global settings are synchronized to ViewModel
      _globalSettingsProvider.GlobalSettings.PropertyChanged += GlobalSettingsOnPropertyChanged;
    }

    private void GlobalSettingsOnPropertyChanged(object sender, PropertyChangedEventArgs args) {
      var setting = _globalSettingsProvider.GlobalSettings;
      ViewModel.MatchCase = setting.SearchMatchCase;
      ViewModel.MatchWholeWord = setting.SearchMatchWholeWord;
      ViewModel.UseRegex = setting.SearchUseRegEx;
      ViewModel.IncludeSymLinks = setting.SearchIncludeSymLinks;
    }

    private void ViewModelOnPropertyChanged(object sender, PropertyChangedEventArgs args) {
      var settings = _globalSettingsProvider.GlobalSettings;
      var model = (CodeSearchViewModel)sender;
      settings.SearchMatchCase = model.MatchCase;
      settings.SearchMatchWholeWord = model.MatchWholeWord;
      settings.SearchUseRegEx = model.UseRegex;
      settings.SearchIncludeSymLinks = model.IncludeSymLinks;
    }

    public CodeSearchViewModel ViewModel { get { return _control.ViewModel; } }
    public IUIRequestProcessor UIRequestProcessor { get { return _uiRequestProcessor; } }
    public IStandarImageSourceFactory StandarImageSourceFactory { get { return _standarImageSourceFactory; } }
    public IClipboard Clipboard { get { return _clipboard; } }
    public IWindowsExplorer WindowsExplorer { get { return _windowsExplorer; } }
    public GlobalSettings Settings { get { return _globalSettingsProvider.GlobalSettings; } }
    public ISynchronizationContextProvider SynchronizationContextProvider { get { return _synchronizationContextProvider; } }
    public IOpenDocumentHelper OpenDocumentHelper { get { return _openDocumentHelper; } }

    public void OpenFileInEditor(FileEntryViewModel fileEntry, Span? span) {
      // Using "Post" is important: it allows the newly opened document to
      // receive the focus.
      SynchronizationContextProvider.UIContext.Post(() =>
        OpenDocumentHelper.OpenDocument(fileEntry.Path, _ => span));
    }

    public List<TreeViewItemViewModel> CreateFileSystemTreeViewModel(FileSystemTree tree) {
      var rootNode = new RootTreeViewItemViewModel(StandarImageSourceFactory);
      var messages = new List<TreeViewItemViewModel>();
      if (tree.Root.Entries.Count > 0) {
        var rootError = new TextItemViewModel(
          StandarImageSourceFactory,
          rootNode,
          "No search results available - Type text to search for in \"Search Code\" and/or \"File Path\"");
        messages.Add(rootError);
      }
      messages.ForAll(rootNode.AddChild);
      TreeViewItemViewModel.ExpandNodes(messages, false);
      return messages;
    }

    public List<TreeViewItemViewModel> CreateSearchFilePathsResult(DirectoryEntry fileResults, string description, bool expandAll) {
      var rootNode = new RootTreeViewItemViewModel(StandarImageSourceFactory);
      var result =
        new List<TreeViewItemViewModel> {
          new TextItemViewModel(StandarImageSourceFactory, rootNode, description)
        }.Concat(
          fileResults
            .Entries
            .Select(x => FileSystemEntryViewModel.Create(this, rootNode, x)))
          .ToList();
      result.ForAll(rootNode.AddChild);
      TreeViewItemViewModel.ExpandNodes(result, expandAll);
      return result;
    }

    public List<TreeViewItemViewModel> CreateSearchCodeResultViewModel(DirectoryEntry searchResults, string description, bool expandAll) {
      var rootNode = new RootTreeViewItemViewModel(StandarImageSourceFactory);
      var result =
        new List<TreeViewItemViewModel> {
            new TextItemViewModel(StandarImageSourceFactory, rootNode, description)
          }.Concat(
          searchResults
            .Entries
            .Select(x => FileSystemEntryViewModel.Create(this, rootNode, x)))
          .ToList();
      result.ForAll(rootNode.AddChild);
      TreeViewItemViewModel.ExpandNodes(result, expandAll);
      return result;
    }

    public List<TreeViewItemViewModel> CreateErrorResponseViewModel(ErrorResponse errorResponse) {
      var messages = new List<TreeViewItemViewModel>();
      if (errorResponse.IsRecoverable()) {
        // For a recoverable error, the deepest exception contains the 
        // "user friendly" error message.
        var rootError = new TextWarningItemViewModel(
          StandarImageSourceFactory,
          null,
          errorResponse.GetBaseError().Message);
        messages.Add(rootError);
      } else {
        // In case of non recoverable error, display a generic "user friendly"
        // message, with nested nodes for exception messages.
        var rootError = new TextErrorItemViewModel(
          StandarImageSourceFactory,
          null,
          "Error processing request. You may need to restart Visual Studio.");
        messages.Add(rootError);

        // Add all errors to the parent
        while (errorResponse != null) {
          rootError.Children.Add(new TextItemViewModel(StandarImageSourceFactory, rootError, errorResponse.Message));
          errorResponse = errorResponse.InnerError;
        }
      }
      return messages;
    }

    /// <summary>
    /// Find the directory entry in the FileSystemTree corresponding to a directory
    /// entry containing a relative path or a project root path.
    /// </summary>
    private static FileSystemEntryViewModel FindFileSystemEntryForRelativePath(
      List<TreeViewItemViewModel> fileSystemTreeNodes,
      FileSystemEntryViewModel relativePathEntry) {
      // Find the top level entry of the relative path
      var topLevelEntry = GetChromiumRoot(relativePathEntry);
      Debug.Assert(topLevelEntry != null);

      // Find the corresponding top level entry in the FileSystemTree nodes.
      var fileSystemTreeEntry = fileSystemTreeNodes
        .OfType<FileSystemEntryViewModel>()
        .FirstOrDefault(x => SystemPathComparer.Instance.StringComparer.Equals(x.Name, topLevelEntry.Name));
      if (fileSystemTreeEntry == null)
        return null;

      // Special case: "relativePath" is actually a Root entry.
      if (topLevelEntry == relativePathEntry) {
        return fileSystemTreeEntry;
      }

      // Descend the FileSystemTree nodes hierarchy as we split the directory name.
      foreach (var childName in relativePathEntry.Name.Split(Path.DirectorySeparatorChar)) {
        // First try without forcing loading the lazy loaded entries.
        var childViewModel = fileSystemTreeEntry
          .Children
          .OfType<FileSystemEntryViewModel>()
          .FirstOrDefault(x => SystemPathComparer.Instance.StringComparer.Equals(x.Name, childName));

        // Try again by forcing loading the lazy loaded entries.
        if (childViewModel == null) {
          fileSystemTreeEntry.EnsureAllChildrenLoaded();
          childViewModel = fileSystemTreeEntry
            .Children
            .OfType<FileSystemEntryViewModel>()
            .FirstOrDefault(x => SystemPathComparer.Instance.StringComparer.Equals(x.Name, childName));
          if (childViewModel == null)
            return null;
        }

        fileSystemTreeEntry = childViewModel;
      }
      return fileSystemTreeEntry;
    }

    /// <summary>
    /// Find the directory entry in the FileSystemTree corresponding to a directory
    /// entry containing a relative path or a project root path.
    /// </summary>
    private static FileSystemEntryViewModel FindFileSystemEntryForPath(
      List<TreeViewItemViewModel> fileSystemTreeNodes,
      FullPath path) {
      // Find the corresponding top level entry in the FileSystemTree nodes.
      var fileSystemTreeEntry = fileSystemTreeNodes
        .OfType<FileSystemEntryViewModel>()
        .FirstOrDefault(x => PathHelpers.IsPrefix(path.Value, x.Name));
      if (fileSystemTreeEntry == null)
        return null;

      var pair = PathHelpers.SplitPrefix(path.Value, fileSystemTreeEntry.Name);

      // Special case: "path" is actually a Root entry.
      if (string.IsNullOrEmpty(pair.Suffix)) {
        return fileSystemTreeEntry;
      }

      // Descend the FileSystemTree nodes hierarchy as we split the directory name.
      foreach (var childName in pair.Suffix.Split(Path.DirectorySeparatorChar)) {
        // First try without forcing loading the lazy loaded entries.
        var childViewModel = fileSystemTreeEntry
          .Children
          .OfType<FileSystemEntryViewModel>()
          .FirstOrDefault(x => SystemPathComparer.Instance.StringComparer.Equals(x.Name, childName));

        // Try again by forcing loading the lazy loaded entries.
        if (childViewModel == null) {
          fileSystemTreeEntry.EnsureAllChildrenLoaded();
          childViewModel = fileSystemTreeEntry
            .Children
            .OfType<FileSystemEntryViewModel>()
            .FirstOrDefault(x => SystemPathComparer.Instance.StringComparer.Equals(x.Name, childName));
          if (childViewModel == null)
            return null;
        }

        fileSystemTreeEntry = childViewModel;
      }
      return fileSystemTreeEntry;
    }

    /// <summary>
    /// Returns the node contained in <paramref name="fileSystemTreeNodes"/>
    /// that has the exact same path as <paramref name="node"/>. This method is
    /// used to find equivalent nodes between different versions of the file
    /// system tree.
    /// </summary>
    private static TreeViewItemViewModel FindSameNode(
      List<TreeViewItemViewModel> fileSystemTreeNodes,
      TreeViewItemViewModel node) {
      var root = GetChromiumRoot(node);
      if (root == null)
        return null;

      var newRoot = fileSystemTreeNodes.FirstOrDefault(x => x.DisplayText == root.DisplayText);
      if (newRoot == null)
        return null;

      // Create stack of parent -> child for DFS search
      var stack = new Stack<TreeViewItemViewModel>();
      var item = node;
      while (item != root) {
        stack.Push(item);
        item = item.ParentViewModel;
      }

      // Process all stack elements, looking for their equivalent in
      // "fileSystemTreeEntry"
      var fileSystemTreeEntry = newRoot;
      while (stack.Count > 0) {
        var child = stack.Pop();

        // First try without forcing loading the lazy loaded entries.
        var childViewModel = fileSystemTreeEntry
          .Children
          .FirstOrDefault(x => x.DisplayText == child.DisplayText);

        // Try again by forcing loading the lazy loaded entries.
        if (childViewModel == null) {
          fileSystemTreeEntry.EnsureAllChildrenLoaded();
          childViewModel = fileSystemTreeEntry
            .Children
            .FirstOrDefault(x => x.DisplayText == child.DisplayText);
          if (childViewModel == null)
            return null;
        }

        fileSystemTreeEntry = childViewModel;
      }
      return fileSystemTreeEntry;
    }

    /// <summary>
    /// Transfer the "IsExpanded" and "IsSelected" state of the nodes from an
    /// old file system tree to a new one.
    /// </summary>
    private static void TransferFileSystemTreeState(
      List<TreeViewItemViewModel> oldFileSystemTree,
      List<TreeViewItemViewModel> newFileSystemTree) {
      var state = new FileSystemTreeState();
      oldFileSystemTree.ForEach(x => state.ProcessNodes(x));
      state.ExpandedNodes.ForAll(
        x => {
          var y = FindSameNode(newFileSystemTree, x);
          if (y != null)
            y.IsExpanded = true;
        });
      state.CollapsedParentNodes.ForAll(
        x => {
          var y = FindSameNode(newFileSystemTree, x);
          if (y != null)
            y.IsExpanded = false;
        });
      state.SelectedNodes.ForAll(
        x => {
          var y = FindSameNode(newFileSystemTree, x);
          if (y != null)
            y.IsSelected = true;
        });
    }

    public class FileSystemTreeState {
      private readonly List<TreeViewItemViewModel> _expandedNodes = new List<TreeViewItemViewModel>();
      private readonly List<TreeViewItemViewModel> _selectedNodes = new List<TreeViewItemViewModel>();
      private readonly List<TreeViewItemViewModel> _collapsedParentNodes = new List<TreeViewItemViewModel>();

      public List<TreeViewItemViewModel> ExpandedNodes {
        get { return _expandedNodes; }
      }

      public List<TreeViewItemViewModel> SelectedNodes {
        get { return _selectedNodes; }
      }

      public List<TreeViewItemViewModel> CollapsedParentNodes {
        get { return _collapsedParentNodes; }
      }

      public bool ProcessNodes(TreeViewItemViewModel x) {
        if (x.IsSelected) {
          SelectedNodes.Add(x);
        }

        var anyChildExpanded = false;
        x.Children.ForAll(child => {
          if (ProcessNodes(child))
            anyChildExpanded = true;
        });

        var isExpanded = x.IsExpanded;
        if (anyChildExpanded) {
          if (!isExpanded) {
            _collapsedParentNodes.Add(x);
          }
        } else {
          if (isExpanded) {
            _expandedNodes.Add(x);
          }
        }
        return isExpanded;
      }
    }

    /// <summary>
    /// Return the top level entry parent of <paramref name="item"/>
    /// </summary>
    private static DirectoryEntryViewModel GetChromiumRoot(TreeViewItemViewModel item) {
      for (TreeViewItemViewModel current = item; current != null; current = current.ParentViewModel) {
        if (current.ParentViewModel is RootTreeViewItemViewModel) {
          // Maybe "null" if top level node is not a directory.
          return current as DirectoryEntryViewModel;
        }
      }
      return null;
    }

    /// <summary>
    /// Navigate to the FileSystemTree directory entry corresponding to
    /// <paramref name="relativePathEntry"/>. This is a no-op if the FileSystemTree
    /// is already the currently active ViewModel.
    /// </summary>
    public void ShowInSourceExplorer(FileSystemEntryViewModel relativePathEntry) {
      var path = relativePathEntry.GetFullPath();
      _eventBus.Fire("ShowInSolutionExplorer", relativePathEntry, new FilePathEventArgs {
        FilePath = path
      });
    }

    public void BringItemViewModelToView(TreeViewItemViewModel item) {
      // We look for the tree view item corresponding to "item", swallowing
      // the "BringIntoView" request to avoid flickering as we descend into
      // the virtual tree and realize the sub-panels at each level.
      _control.SwallowsRequestBringIntoView(true);
      var treeViewItem = SelectTreeViewItem(_control.FileTreeView, item);

      // If we found it, allow the "BringIntoView" requests to be handled
      // and ask the tree view item to bring itself into view.
      // Note: The "BrinIntoView" call is a no-op if the tree view item
      // is already visible.
      if (treeViewItem != null) {
        _control.SwallowsRequestBringIntoView(false);
        treeViewItem.BringIntoView();
        _control.SwallowsRequestBringIntoView(true);
      }
    }

    public TreeViewItem SelectTreeViewItem(TreeView treeView, TreeViewItemViewModel item) {
      return WpfUtilities.SelectTreeViewItem(treeView, item);
    }

    public bool ExecuteOpenCommandForItem(TreeViewItemViewModel tvi) {
      if (tvi == null)
        return false;

      if (!tvi.IsSelected)
        return false;

      {
        var filePosition = tvi as FilePositionViewModel;
        if (filePosition != null) {
          filePosition.OpenCommand.Execute(filePosition);
          return true;
        }
      }

      {
        var fileEntry = tvi as FileEntryViewModel;
        if (fileEntry != null) {
          fileEntry.OpenCommand.Execute(fileEntry);
          return true;
        }
      }

      {
        var directoryEntry = tvi as DirectoryEntryViewModel;
        if (directoryEntry != null) {
          directoryEntry.OpenCommand.Execute(directoryEntry);
          return true;
        }
      }

      return false;
    }

    private class SearchWorkerParams {
      /// <summary>
      /// Simple short name of the operation (for debugging only).
      /// </summary>
      public string OperationName { get; set; }
      /// <summary>
      /// Short description of the operation (for display in status bar
      /// progress)
      /// </summary>
      public string HintText { get; set; }
      /// <summary>
      /// The request to sent to the server
      /// </summary>
      public TypedRequest TypedRequest { get; set; }
      /// <summary>
      /// Amount of time to wait before sending the request to the server.
      /// </summary>
      public TimeSpan Delay { get; set; }
      /// <summary>
      /// Lambda invoked when the response to the request has been successfully
      /// received from the server.
      /// </summary>
      public Action<TypedResponse, Stopwatch> ProcessResponse { get; set; }
      /// <summary>
      /// Lambda invoked when the request resulted in an error from the server.
      /// </summary>
      public Action<ErrorResponse, Stopwatch> ProcessError { get; set; }
    }

    private void SearchWorker(SearchWorkerParams workerParams) {
      // Cancel all previously running tasks
      _taskCancellation.CancelAll();
      var cancellationToken = _taskCancellation.GetNewToken();

      var id = Interlocked.Increment(ref _operationSequenceId);
      var progressId = string.Format("{0}-{1}", workerParams.OperationName, id);
      var sw = new Stopwatch();
      var request = new UIRequest {
        // Note: Having a single ID for all searches ensures previous search
        // requests are superseeded.
        Id = "MetaSearch",
        Request = workerParams.TypedRequest,
        Delay = workerParams.Delay,
        OnSend = () => {
          sw.Start();
          _progressBarTracker.Start(progressId, workerParams.HintText);
        },
        OnReceive = () => {
          sw.Stop();
          _progressBarTracker.Stop(progressId);
        },
        OnSuccess = typedResponse => {
          if (cancellationToken.IsCancellationRequested)
            return;
          workerParams.ProcessResponse(typedResponse, sw);
        },
        OnError = errorResponse => {
          if (cancellationToken.IsCancellationRequested)
            return;
          workerParams.ProcessError(errorResponse, sw);
        }
      };

      _uiRequestProcessor.Post(request);
    }

    public void RefreshFileSystemTree() {
      var uiRequest = new UIRequest {
        Request = new RefreshFileSystemTreeRequest(),
        Id = "RefreshFileSystemTreeRequest",
        Delay = TimeSpan.FromSeconds(0.0),
      };

      _uiRequestProcessor.Post(uiRequest);
    }

    public void SetFileSystemTree(FileSystemTree tree) {
      var viewModel = CreateFileSystemTreeViewModel(tree);

      // Transfer expanded and selected nodes from the old tree to the new one.
      TransferFileSystemTreeState(ViewModel.FileSystemTreeNodes, viewModel);

      // Set tree as the new active tree.
      ViewModel.SetFileSystemTree(viewModel);
      FetchDatabaseStatistics();
    }

    public void FilesLoaded() {
      FetchDatabaseStatistics();
    }

    private void FetchDatabaseStatistics() {
      _uiRequestProcessor.Post(
        new UIRequest {
          Id = "GetDatabaseStatisticsRequest",
          Request = new GetDatabaseStatisticsRequest(),
          OnSuccess = r => {
            var response = (GetDatabaseStatisticsResponse)r;
            var message =
              String.Format(
                "Index: {0:n0} files - {1:n0} MB",
                response.IndexedFileCount,
                response.IndexedFileSize / 1024L / 1024L);
            ViewModel.StatusText = message;
          }
        });
    }

    public void SearchFilesPaths(string searchPattern, bool immediate) {
      SearchWorker(new SearchWorkerParams {
        OperationName = OperationsIds.SearchFilePaths,
        HintText = "Searching for matching file paths...",
        Delay = TimeSpan.FromMilliseconds(immediate ? 0 : Settings.AutoSearchDelayMsec),
        TypedRequest = new SearchFilePathsRequest {
          SearchParams = new SearchParams {
            SearchString = searchPattern,
            MaxResults = Settings.SearchFilePathsMaxResults,
            MatchCase = ViewModel.MatchCase,
            MatchWholeWord = ViewModel.MatchWholeWord,
            IncludeSymLinks = ViewModel.IncludeSymLinks,
            UseRe2Engine = true,
            Regex = ViewModel.UseRegex,
          }
        },
        ProcessError = (errorResponse, stopwatch) => {
          var viewModel = CreateErrorResponseViewModel(errorResponse);
          ViewModel.SetSearchFilePathsResult(viewModel);
        },
        ProcessResponse = (typedResponse, stopwatch) => {
          var response = ((SearchFilePathsResponse)typedResponse);
          var msg = string.Format("Found {0:n0} file names among {1:n0} ({2:0.00} seconds) matching pattern \"{3}\"",
            response.HitCount,
            response.TotalCount,
            stopwatch.Elapsed.TotalSeconds,
            searchPattern);
          var viewModel = CreateSearchFilePathsResult(response.SearchResult, msg, true);
          ViewModel.SetSearchFilePathsResult(viewModel);
        }
      });
    }

    public void SearchCode(string searchPattern, string filePathPattern, bool immediate) {
      SearchWorker(new SearchWorkerParams {
        OperationName = OperationsIds.SearchCode,
        HintText = "Searching for matching text in files...",
        Delay = TimeSpan.FromMilliseconds(immediate ? 0 : Settings.AutoSearchDelayMsec),
        TypedRequest = new SearchCodeRequest {
          SearchParams = new SearchParams {
            SearchString = searchPattern,
            FilePathPattern = filePathPattern,
            MaxResults = Settings.SearchCodeMaxResults,
            MatchCase = ViewModel.MatchCase,
            MatchWholeWord = ViewModel.MatchWholeWord,
            IncludeSymLinks = ViewModel.IncludeSymLinks,
            UseRe2Engine = true,
            Regex = ViewModel.UseRegex,
          }
        },
        ProcessError = (errorResponse, stopwatch) => {
          var viewModel = CreateErrorResponseViewModel(errorResponse);
          ViewModel.SetSearchCodeResult(viewModel);
        },
        ProcessResponse = (typedResponse, stopwatch) => {
          var response = ((SearchCodeResponse)typedResponse);
          var msg = string.Format("Found {0:n0} results among {1:n0} files ({2:0.00} seconds) matching text \"{3}\"",
            response.HitCount,
            response.SearchedFileCount,
            stopwatch.Elapsed.TotalSeconds,
            searchPattern);
          bool expandAll = response.HitCount < HardCodedSettings.SearchCodeExpandMaxResults;
          var viewModel = CreateSearchCodeResultViewModel(response.SearchResults, msg, expandAll);
          ViewModel.SetSearchCodeResult(viewModel);
        }
      });
    }

    public void SetFileSystemTreeError(ErrorResponse error) {
      var viewModel = CreateErrorResponseViewModel(error);
      ViewModel.SetFileSystemTree(viewModel);
    }

    enum Direction {
      Next,
      Previous
    }

    private T GetNextLocationEntry<T>(Direction direction) where T : class, IHierarchyObject {
      var item = _control.FileTreeView.SelectedItem;
      if (item == null) {
        if (ViewModel.ActiveRootNodes == null)
          return null;

        if (ViewModel.ActiveRootNodes.Count == 0)
          return null;

        item = ViewModel.ActiveRootNodes[0].ParentViewModel;
        if (item == null)
          return null;
      }

      var nextItem = (direction == Direction.Next)
        ? new HierarchyObjectNavigator().GetNextItemOfType<T>(item as IHierarchyObject)
        : new HierarchyObjectNavigator().GetPreviousItemOfType<T>(item as IHierarchyObject);

      return nextItem;
    }

    private TreeViewItemViewModel GetNextLocationEntry(Direction direction) {
      if (ViewModel.ActiveDisplay == CodeSearchViewModel.DisplayKind.SearchCodeResult) {
        return GetNextLocationEntry<FilePositionViewModel>(direction);
      }
      
      if (ViewModel.ActiveDisplay == CodeSearchViewModel.DisplayKind.SearchFilePathsResult) {
        return GetNextLocationEntry<FileEntryViewModel>(direction);
      }

      return null;
    }

    public bool HasNextLocation() {
      return GetNextLocationEntry(Direction.Next) != null;
    }

    public bool HasPreviousLocation() {
      return GetNextLocationEntry(Direction.Previous) != null;
    }

    public void NavigateToNextLocation() {
      var nextItem = GetNextLocationEntry(Direction.Next);
      NavigateToTreeViewItem(nextItem);
    }

    public void NavigateToPreviousLocation() {
      var previousItem = GetNextLocationEntry(Direction.Previous);
      NavigateToTreeViewItem(previousItem);
    }

    public void CancelSearch() {
      ViewModel.SwitchToFileSystemTree();
    }

    private void NavigateToTreeViewItem(TreeViewItemViewModel item) {
      if (item == null)
        return;
      BringItemViewModelToView(item);
      ExecuteOpenCommandForItem(item);
    }
  }
}