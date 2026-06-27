$files = @(
    "C:\Users\yc\Desktop\port_bime\install.bat",
    "C:\Users\yc\Desktop\port_bime\uninstall.bat", 
    "C:\Users\yc\Desktop\port_bime\start.bat"
)

foreach ($file in $files) {
    $content = [IO.File]::ReadAllText($file)
    $content = $content -replace "(?<!\r)\n", "`r`n"
    [IO.File]::WriteAllText($file, $content)
    Write-Host "Converted: $file"
}
