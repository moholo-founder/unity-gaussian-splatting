# Export Gaussian Splatting Package for Unity Asset Store
# Run this script from the package root directory

$packageName = "ai.moholo.gaussiansplatting"
$workspaceRoot = "D:\HalfABridge\Unity Gaussian Splatting"
$packagePath = "$workspaceRoot\Unity\Packages\$packageName"
$outputPath = "$workspaceRoot"

Write-Host "========================================"
Write-Host "Exporting Package: $packageName"
Write-Host "========================================"
Write-Host ""

# Navigate to Packages folder
Set-Location "$workspaceRoot\Unity\Packages"

# Check if package exists
if (-not (Test-Path $packageName)) {
    Write-Host "ERROR: Package folder not found at: $packagePath" -ForegroundColor Red
    exit 1
}

Write-Host "Package found: $packagePath" -ForegroundColor Green
Write-Host ""

# Create .tgz archive using tar (Windows 10+)
Write-Host "Creating .tgz archive..." -ForegroundColor Yellow

try {
    tar -czf "$outputPath\$packageName.tgz" $packageName
    
    if (Test-Path "$outputPath\$packageName.tgz") {
        $fileSize = (Get-Item "$outputPath\$packageName.tgz").Length / 1MB
        Write-Host ""
        Write-Host "========================================"
        Write-Host "SUCCESS!" -ForegroundColor Green
        Write-Host "========================================"
        Write-Host "Package exported to:" -ForegroundColor Green
        Write-Host "$outputPath\$packageName.tgz" -ForegroundColor Cyan
        Write-Host "File size: $([math]::Round($fileSize, 2)) MB" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Next steps:" -ForegroundColor Yellow
        Write-Host "1. Test the package in a clean Unity project"
        Write-Host "2. Prepare screenshots and promotional images"
        Write-Host "3. Submit via Unity Asset Store Publisher Portal"
        Write-Host "   https://publisher.assetstore.unity3d.com/"
    } else {
        Write-Host "ERROR: Archive file was not created" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "ERROR: Failed to create archive" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ""
    Write-Host "Alternative: Use Unity's Export Package feature:" -ForegroundColor Yellow
    Write-Host "1. Open Unity"
    Write-Host "2. Right-click Packages/$packageName folder"
    Write-Host "3. Select 'Export Package...'"
    Write-Host "4. Exclude internal docs, include Runtime/Editor/Samples~"
    exit 1
}

