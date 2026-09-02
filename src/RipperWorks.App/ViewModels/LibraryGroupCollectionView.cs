using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Data;

namespace RipperWorks.App.ViewModels;

internal sealed class LibraryGroupCollectionView : ListCollectionView
{
    private readonly IList _source;
    private readonly Func<IReadOnlyList<LibraryGroupViewModel>> _groupSource;
    private readonly Func<LibraryGroupViewModel, bool> _showEmptyGroup;
    private readonly BulkObservableCollection<object> _projectedGroups = [];
    private readonly ReadOnlyObservableCollection<object> _readOnlyGroups;

    public LibraryGroupCollectionView(
        IList source,
        Func<IReadOnlyList<LibraryGroupViewModel>> groupSource,
        Func<LibraryGroupViewModel, bool> showEmptyGroup)
        : base(source)
    {
        _source = source;
        _groupSource = groupSource;
        _showEmptyGroup = showEmptyGroup;
        _readOnlyGroups = new(_projectedGroups);
        RebuildGroups();
    }

    public override ReadOnlyObservableCollection<object>? Groups =>
        _readOnlyGroups ?? base.Groups;

    protected override void RefreshOverride()
    {
        base.RefreshOverride();
        RebuildGroups();
    }

    protected override void ProcessCollectionChanged(
        NotifyCollectionChangedEventArgs args)
    {
        base.ProcessCollectionChanged(args);
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        if (_groupSource is null || _readOnlyGroups is null)
            return;

        var allRows = _source.Cast<object>()
            .OfType<PackageRowViewModel>()
            .ToArray();
        var projectedRows = this.Cast<object>()
            .OfType<PackageRowViewModel>()
            .ToArray();
        var sourceGroupIds = allRows
            .Select(row => row.Group.Id)
            .ToHashSet();
        var rowsByGroup = projectedRows.ToLookup(row => row.Group.Id);
        var desired = new List<object>();
        foreach (var group in _groupSource())
        {
            var rows = rowsByGroup[group.Id].Cast<object>().ToArray();
            var persistedEmpty = group.Id is not null &&
                !sourceGroupIds.Contains(group.Id);
            if (rows.Length == 0 &&
                (!persistedEmpty || !_showEmptyGroup(group)))
            {
                continue;
            }
            desired.Add(new LibraryCollectionViewGroup(group, rows));
        }
        _projectedGroups.ReplaceAll(desired);
    }

    private sealed class LibraryCollectionViewGroup : CollectionViewGroup
    {
        public LibraryCollectionViewGroup(
            LibraryGroupViewModel group,
            IReadOnlyList<object> rows)
            : base(group)
        {
            foreach (var row in rows)
                ProtectedItems.Add(row);
            ProtectedItemCount = rows.Count;
        }

        public override bool IsBottomLevel => true;
    }
}
