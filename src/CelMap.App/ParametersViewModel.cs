using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CelMap.App;

/// <summary>
/// Manages the state, validation, and category overrides for Screen 0 (Insurance Parameters).
/// </summary>
public sealed partial class ParametersViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParametersValid))]
    private string _groupIdText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParametersValid))]
    private string _insurerIdText = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParametersValid))]
    private DateTime? _reviewStart = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParametersValid))]
    private DateTime? _reviewEnd = DateTime.Today.AddYears(1).AddDays(-1);

    [ObservableProperty]
    private bool _defaultCoverGSC;

    [ObservableProperty]
    private bool _defaultCoverGL;

    [ObservableProperty]
    private bool _defaultCoverTPD;

    [ObservableProperty]
    private bool _defaultCoverTrauma;

    [ObservableProperty]
    private bool _defaultCoverGLTPD;

    public ObservableCollection<CategoryCoverOverride> CategoryOverrides { get; } = new();

    public bool CanContinueToSetup =>
        int.TryParse(GroupIdText, out _)
        && int.TryParse(InsurerIdText, out _)
        && ReviewStart.HasValue
        && ReviewEnd.HasValue
        && CategoryOverrides.All(c => !c.IsEnabled || c.IsCategoryNameValid);

    /// <summary>
    /// Bindable indicator of whether the parameters are valid and ready to proceed.
    /// </summary>
    public bool IsParametersValid => CanContinueToSetup;

    public ParametersViewModel()
    {
        CategoryOverrides.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (CategoryCoverOverride item in e.NewItems)
                {
                    item.PropertyChanged += CategoryOverride_PropertyChanged;
                }
            }
            if (e.OldItems != null)
            {
                foreach (CategoryCoverOverride item in e.OldItems)
                {
                    item.PropertyChanged -= CategoryOverride_PropertyChanged;
                }
            }
        };

        // Add first blank row
        CategoryOverrides.Add(new CategoryCoverOverride(""));
    }

    private void CategoryOverride_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryCoverOverride.CategoryName) || e.PropertyName == nameof(CategoryCoverOverride.IsEnabled))
        {
            OnPropertyChanged(nameof(IsParametersValid));
        }

        if (e.PropertyName == nameof(CategoryCoverOverride.CategoryName) && sender is CategoryCoverOverride item)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => HandleCategoryOverrideChanged(item));
        }
    }

    private void HandleCategoryOverrideChanged(CategoryCoverOverride changedItem)
    {
        // Remove empty rows that are not the last row
        for (int i = CategoryOverrides.Count - 2; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(CategoryOverrides[i].CategoryName))
            {
                CategoryOverrides.RemoveAt(i);
            }
        }

        // If the last row is valid and has a value, add a new blank row
        var last = CategoryOverrides.LastOrDefault();
        if (last != null && last.IsCategoryNameValid)
        {
            CategoryOverrides.Add(new CategoryCoverOverride(""));
        }
    }

    partial void OnReviewStartChanged(DateTime? value)
    {
        if (value is DateTime start)
        {
            ReviewEnd = start.AddYears(1).AddDays(-1);
        }
    }
}
