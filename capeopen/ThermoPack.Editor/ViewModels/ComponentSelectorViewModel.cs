using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ThermoPack.Core.Models;
using Component = ThermoPack.Core.Models.Component;

namespace ThermoPack.Editor.ViewModels;

public class ComponentSelectorViewModel : INotifyPropertyChanged
{
    private string _searchText = "";
    private Component? _selectedAvailable;
    private Component? _selectedChosen;

    public ComponentSelectorViewModel(
        IReadOnlyList<Component> allComponents,
        IReadOnlyList<Component> currentSelection,
        EosType eosType)
    {
        EosType = eosType;
        AllAvailable = allComponents.OrderBy(c => c.Name).ToList();

        foreach (var c in currentSelection)
            SelectedComponents.Add(c);

        UpdateFilteredList();
    }

    public EosType EosType { get; }
    public List<Component> AllAvailable { get; }
    public ObservableCollection<Component> FilteredAvailable { get; } = new();
    public ObservableCollection<Component> SelectedComponents { get; } = new();

    public bool DialogResult { get; private set; }

    public string SearchText
    {
        get => _searchText;
        set { _searchText = value; OnPropertyChanged(); UpdateFilteredList(); }
    }

    public Component? SelectedAvailable
    {
        get => _selectedAvailable;
        set { _selectedAvailable = value; OnPropertyChanged(); }
    }

    public Component? SelectedChosen
    {
        get => _selectedChosen;
        set { _selectedChosen = value; OnPropertyChanged(); }
    }

    public void AddComponent()
    {
        if (_selectedAvailable == null) return;
        if (SelectedComponents.Contains(_selectedAvailable)) return;
        SelectedComponents.Add(_selectedAvailable);
        UpdateFilteredList();
    }

    public void RemoveComponent()
    {
        if (_selectedChosen == null) return;
        SelectedComponents.Remove(_selectedChosen);
        UpdateFilteredList();
    }

    public void Confirm()
    {
        DialogResult = true;
    }

    private void UpdateFilteredList()
    {
        FilteredAvailable.Clear();
        var search = _searchText.Trim();
        foreach (var c in AllAvailable)
        {
            if (SelectedComponents.Contains(c)) continue;

            if (string.IsNullOrEmpty(search) ||
                c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Ident.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Formula.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.CasNumber.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                FilteredAvailable.Add(c);
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
