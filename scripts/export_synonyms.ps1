# Reusable script to export synonym groups from synonyms.json to synonyms_review.md
$jsonPath = Join-Path $PSScriptRoot "..\src\CelMap.Core\synonyms.json"
$outputPath = Join-Path $PSScriptRoot "..\synonyms_review.md"

if (-not (Test-Path $jsonPath)) {
    Write-Error "Error: synonyms.json not found at $jsonPath"
    exit 1
}

try {
    $content = Get-Content -Raw -Path $jsonPath | ConvertFrom-Json
    $markdown = [System.Collections.Generic.List[string]]::new()
    $markdown.Add("# Synonym Groups Verification")
    $markdown.Add("")
    $markdown.Add("Below is the list of all synonym groups configured in CelMap. This document is generated for human verification to review the target fields, their matching strictness, and their mapped synonyms.")
    $markdown.Add("")
    $markdown.Add("| Target Field / Key | Strictness | Synonyms / Mapped Aliases |")
    $markdown.Add("| :--- | :--- | :--- |")

    foreach ($group in $content.groups) {
        $names = $null
        $strict = "Loose"
        
        # Check if group is an object (dict) or array (list)
        if ($group -is [System.Management.Automation.PSCustomObject] -and $group.names -ne $null) {
            $names = $group.names
            if ($group.strict -eq $true) {
                $strict = "**Strict** (Fuzzy fallback disabled)"
            }
        } elseif ($group -is [System.Array] -or $group -is [System.Collections.IList]) {
            $names = $group
        } else {
            continue
        }
        
        if ($names.Count -eq 0) {
            continue
        }
        
        $key = $names[0]
        $formattedNames = @()
        foreach ($name in $names) {
            $escaped = $name.Replace("|", "\|")
            $formattedNames += ('`' + $escaped + '`')
        }
        $synonymsStr = $formattedNames -join ", "
        $markdown.Add("| **$key** | $strict | $synonymsStr |")
    }

    $markdown -join "`r`n" | Out-File -FilePath $outputPath -Encoding utf8
    Write-Output "Successfully exported synonyms to: $outputPath"
} catch {
    Write-Error "Failed to export synonyms: $_"
    exit 1
}
