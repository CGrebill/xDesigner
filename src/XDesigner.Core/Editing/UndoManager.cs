using XDesigner.Core.Xaml;

namespace XDesigner.Core.Editing;

/// <summary>
/// Undo/redo stacks for one document, plus dirty tracking against the last save (§6).
/// Every execute, undo and redo is one <see cref="XamlDocument.BatchChanges"/> scope, so the document
/// raises a single change (one re-render) per user action.
/// </summary>
public sealed class UndoManager(XamlDocument document, TimeProvider? clock = null)
{
    public const int MaxDepth = 500;

    /// <summary>Consecutive edits of the same property within this window merge into one step (typing).</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(1);

    private static readonly object Unsaved = new();

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly LinkedList<IUndoableCommand> _undo = new();
    private readonly Stack<IUndoableCommand> _redo = new();
    private DateTimeOffset _lastExecuted = DateTimeOffset.MinValue;
    private object? _savedTop;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoDescription => _undo.Last?.Value.Description;

    public string? RedoDescription => _redo.TryPeek(out var c) ? c.Description : null;

    /// <summary>True when the document differs from the last save (or from when it was opened).</summary>
    public bool IsDirty => !ReferenceEquals(_undo.Last?.Value, _savedTop);

    /// <summary>Raised after execute, undo, redo or save.</summary>
    public event EventHandler? StateChanged;

    public void Execute(IUndoableCommand command)
    {
        using (document.BatchChanges())
        {
            command.Execute(document);
        }

        var now = _clock.GetUtcNow();
        var top = _undo.Last?.Value;
        if (top is not null && now - _lastExecuted <= MergeWindow && _redo.Count == 0 && top.TryMerge(command))
        {
            if (ReferenceEquals(top, _savedTop))
            {
                _savedTop = Unsaved; // the saved step just changed
            }
        }
        else
        {
            _undo.AddLast(command);
            if (_undo.Count > MaxDepth)
            {
                // The saved state was the first command, or the state before any command (null): either way it can
                // no longer be reached by undoing.
                if (_savedTop is null || ReferenceEquals(_undo.First!.Value, _savedTop))
                {
                    _savedTop = Unsaved;
                }

                _undo.RemoveFirst();
            }
        }

        _redo.Clear();
        _lastExecuted = now;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_undo.Last?.Value is not { } command)
        {
            return;
        }

        using (document.BatchChanges())
        {
            command.Undo(document);
        }

        _undo.RemoveLast();
        _redo.Push(command);
        _lastExecuted = DateTimeOffset.MinValue; // never merge into a step that was undone past
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var command))
        {
            return;
        }

        using (document.BatchChanges())
        {
            command.Execute(document);
        }

        _undo.AddLast(command);
        _lastExecuted = DateTimeOffset.MinValue;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records the current state as saved.</summary>
    public void MarkSaved()
    {
        _savedTop = _undo.Last?.Value;
        _lastExecuted = DateTimeOffset.MinValue; // edits after a save start a new step
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
