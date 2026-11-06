# Lists the OWASP category briefs shipped next to this script.
$categories = Get-ChildItem -Path (Join-Path $PSScriptRoot '..' 'categories') -Filter '*.md'
"OWASP-HELPER-OK: $($categories.Count) categories"
