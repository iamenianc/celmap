namespace CelMap.App;

/// <summary>One selectable target template from the curated template folder: the file name
/// shown in the picker, and the full path used to load it.</summary>
public sealed record TargetChoice(string DisplayName, string FullPath)
{
    public override string ToString() => DisplayName;
}
