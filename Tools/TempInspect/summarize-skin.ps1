$d = Get-ChildItem 'Build\Logs\Debug_net10.0-windows7.0\windows_x64' -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$log = Join-Path $d.FullName 'log_general.log'
Write-Host "LOG: $($d.Name)"

Write-Host "=== PALETTE per-mesh ==="
$pal = Select-String -Path $log -Pattern '\[SkinPaletteGpu\]' | ForEach-Object {
  if ($_.Line -match 'verts=(\d+) dispatch#(-?\d+) bones=(\d+) badTrans\(>50\)=(\d+) nanBones=(\d+) worstBone=(-?\d+) worstTransMag=([\d.]+)') {
    [pscustomobject]@{Verts=[int]$matches[1];Disp=[int]$matches[2];Bones=[int]$matches[3];Bad=[int]$matches[4];Nan=[int]$matches[5];WorstBone=[int]$matches[6];WorstMag=[double]$matches[7]}
  }
}
$pal | Group-Object Verts | ForEach-Object {
  $g=$_.Group; $last=$g[-1]
  [pscustomobject]@{Verts=$_.Name;N=$g.Count;MaxBad=($g|Measure-Object Bad -Maximum).Maximum;MaxMag=[math]::Round(($g|Measure-Object WorstMag -Maximum).Maximum,1);LastBad=$last.Bad;LastMag=[math]::Round($last.WorstMag,1);LastWorstBone=$last.WorstBone}
} | Sort-Object {[int]$_.Verts} | Format-Table -AutoSize

Write-Host "=== READBACK per-mesh (first vs last bounds) ==="
$rb = Select-String -Path $log -Pattern '\[SkinReadback\] verts=' | ForEach-Object {
  if ($_.Line -match 'verts=(\d+).*Y\[(-?[\d.]+),(-?[\d.]+)\] Z\[(-?[\d.]+),(-?[\d.]+)\]') {
    [pscustomobject]@{Verts=[int]$matches[1];Ymin=[double]$matches[2];Ymax=[double]$matches[3];Zmin=[double]$matches[4];Zmax=[double]$matches[5];Changed=($_.Line -match 'OUTPUT CHANGED')}
  }
}
$rb | Group-Object Verts | ForEach-Object {
  $g=$_.Group; $f=$g[0]; $l=$g[-1]
  [pscustomobject]@{Verts=$_.Name;N=$g.Count;Changes=($g|Where-Object Changed).Count;FirstY="$($f.Ymin),$($f.Ymax)";FirstZ="$($f.Zmin),$($f.Zmax)";LastY="$($l.Ymin),$($l.Ymax)";LastZ="$($l.Zmin),$($l.Zmax)"}
} | Sort-Object {[int]$_.Verts} | Format-Table -AutoSize
