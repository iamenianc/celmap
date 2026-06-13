using CommunityToolkit.Mvvm.ComponentModel;

namespace CelMap.App;

public sealed partial class CategoryCoverOverride : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategoryNameValid))]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private string _categoryName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool _gsc;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool _gl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool _tpd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool _trauma;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnabled))]
    private bool _glTpd;

    public bool IsCategoryNameValid =>
        !string.IsNullOrWhiteSpace(CategoryName) &&
        (int.TryParse(CategoryName, out _) || CategoryName.Length == 1);

    public bool IsEnabled => IsCategoryNameValid && (Gsc || Gl || Tpd || Trauma || GlTpd);

    public CategoryCoverOverride(string categoryName)
    {
        CategoryName = categoryName;
    }
}
