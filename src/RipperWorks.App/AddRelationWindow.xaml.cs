using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using RipperWorks.App.Services;
using RipperWorks.App.ViewModels;
using RipperWorks.Core;

namespace RipperWorks.App;

public partial class AddRelationWindow : Window
{
    private readonly AddRelationDialogViewModel _viewModel;

    public AddRelationWindow(
        string currentModName,
        IReadOnlyList<RelationCandidateViewModel> candidates,
        LocalizationService localization)
    {
        InitializeComponent();
        _viewModel = new AddRelationDialogViewModel(
            currentModName,
            candidates,
            localization);
        DataContext = _viewModel;
    }

    public RelationSelection? Selection { get; private set; }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void AddButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCandidate is null)
            return;
        Selection = new RelationSelection(
            _viewModel.SelectedCandidate.PackageId,
            _viewModel.SelectedRelationType.Value);
        DialogResult = true;
    }
}

internal sealed class AddRelationDialogViewModel
    : ObservableObject
{
    private string _searchText = string.Empty;
    private RelationCandidateViewModel? _selectedCandidate;
    private RelationTypeChoice _selectedRelationType;

    public AddRelationDialogViewModel(
        string currentModName,
        IReadOnlyList<RelationCandidateViewModel> candidates,
        LocalizationService localization)
    {
        Title = string.Format(
            localization.Get("RelationAddTitle"),
            currentModName);
        SearchLabel = localization.Get("Search");
        CancelLabel = localization.Get("Cancel");
        AddLabel = localization.Get("RelationAdd");
        Candidates = candidates;
        CandidatesView = CollectionViewSource.GetDefaultView(Candidates);
        CandidatesView.Filter = Filter;
        RelationTypes =
        [
            new(PackageRelationType.Requires,
                localization.Get("RelationTypeRequires")),
            new(PackageRelationType.AddOnOf,
                localization.Get("RelationTypeAddOnOf"))
        ];
        _selectedRelationType = RelationTypes[0];
    }

    public string Title { get; }
    public string SearchLabel { get; }
    public string CancelLabel { get; }
    public string AddLabel { get; }
    public IReadOnlyList<RelationCandidateViewModel> Candidates { get; }
    public ICollectionView CandidatesView { get; }
    public IReadOnlyList<RelationTypeChoice> RelationTypes { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                CandidatesView.Refresh();
        }
    }

    public RelationCandidateViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetProperty(ref _selectedCandidate, value))
                OnPropertyChanged(nameof(CanAdd));
        }
    }

    public RelationTypeChoice SelectedRelationType
    {
        get => _selectedRelationType;
        set => SetProperty(ref _selectedRelationType, value);
    }

    public bool CanAdd => SelectedCandidate is not null;

    private bool Filter(object item) =>
        item is RelationCandidateViewModel candidate &&
        (string.IsNullOrWhiteSpace(SearchText) ||
         candidate.DisplayName.Contains(
             SearchText,
             StringComparison.CurrentCultureIgnoreCase) ||
         candidate.Subtitle.Contains(
             SearchText,
             StringComparison.CurrentCultureIgnoreCase));
}

internal sealed record RelationTypeChoice(
    PackageRelationType Value,
    string Display);
