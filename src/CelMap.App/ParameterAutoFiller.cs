namespace CelMap.App;

using System;
using System.Linq;
using CelMap.Core;

public static class ParameterAutoFiller
{
    public static void AutoFill(MappingRowViewModel row, ParametersViewModel vm, AliasRules aliases)
    {
        string label = row.TargetLabel;

        if (aliases.AreAliases(label, "GroupID") && !string.IsNullOrWhiteSpace(vm.GroupIdText))
            row.SetConstant(NormalizeInt(vm.GroupIdText), MainViewModel.SampleRowCount);
        else if (label.Equals("InsurerID", StringComparison.OrdinalIgnoreCase) || label.Equals("Insurer ID", StringComparison.OrdinalIgnoreCase) || label.Equals("Insurer", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(vm.InsurerIdText)) row.SetConstant(NormalizeInt(vm.InsurerIdText), MainViewModel.SampleRowCount);
        }
        else if (aliases.AreAliases(label, "ReviewDate") || label.Equals("Review Start Date", StringComparison.OrdinalIgnoreCase) || label.Equals("Review Start", StringComparison.OrdinalIgnoreCase) || label.Equals("ReviewStart", StringComparison.OrdinalIgnoreCase))
        {
            if (vm.ReviewStart.HasValue) row.SetConstant(vm.ReviewStart.Value.ToString("yyyy-MM-dd"), MainViewModel.SampleRowCount);
        }
        else if (label.Equals("Review End", StringComparison.OrdinalIgnoreCase) || label.Equals("Review End Date", StringComparison.OrdinalIgnoreCase) || label.Equals("Cover End Date", StringComparison.OrdinalIgnoreCase) || label.Equals("Coverage End Date", StringComparison.OrdinalIgnoreCase))
        {
            if (vm.ReviewEnd.HasValue) row.SetConstant(vm.ReviewEnd.Value.ToString("yyyy-MM-dd"), MainViewModel.SampleRowCount);
        }
        else if (label.Equals("GSC", StringComparison.OrdinalIgnoreCase) || label.Equals("GSC Cover", StringComparison.OrdinalIgnoreCase))
            row.SetConstant(vm.CategoryOverrides.Any(c => c.IsEnabled) ? "[Dynamic GSC]" : (vm.DefaultCoverGSC ? "Y" : "N"), MainViewModel.SampleRowCount);
        else if (label.Equals("GL", StringComparison.OrdinalIgnoreCase) || label.Equals("GL Cover", StringComparison.OrdinalIgnoreCase))
            row.SetConstant(vm.CategoryOverrides.Any(c => c.IsEnabled) ? "[Dynamic GL]" : (vm.DefaultCoverGL ? "Y" : "N"), MainViewModel.SampleRowCount);
        else if (label.Equals("TPD", StringComparison.OrdinalIgnoreCase) || label.Equals("TPD Cover", StringComparison.OrdinalIgnoreCase))
            row.SetConstant(vm.CategoryOverrides.Any(c => c.IsEnabled) ? "[Dynamic TPD]" : (vm.DefaultCoverTPD ? "Y" : "N"), MainViewModel.SampleRowCount);
    }

    /// <summary>Canonical integer form for GroupID/InsurerID so they always export as a Number,
    /// not text: strips surrounding whitespace and any leading zeros ("007 " → "7"). These fields
    /// are already gated as integers on the parameters screen, so a clean parse is expected; an
    /// unexpected non-integer falls back to the original text rather than throwing.</summary>
    private static string NormalizeInt(string text) =>
        long.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : text;
}
