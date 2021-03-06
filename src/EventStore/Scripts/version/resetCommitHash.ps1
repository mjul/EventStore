param([string]$assemblyInfoFile)

if ($assemblyInfoFile -eq $null) {
	throw "No file passed. Usage: ./updateCommitHash.ps1 <filename>"
}

$path = Resolve-Path $assemblyInfoFile -Relative

& { git checkout $path }

Write-Host "Reset commit hash in $assemblyInfoFile"