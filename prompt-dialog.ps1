Add-Type -AssemblyName System.Windows.Forms

$f = New-Object System.Windows.Forms.Form
$f.Text = '构建完成'
$f.Size = New-Object System.Drawing.Size(300, 130)
$f.StartPosition = 'CenterScreen'
$f.FormBorderStyle = 'FixedDialog'
$f.MaximizeBox = $false

$b1 = New-Object System.Windows.Forms.Button
$b1.Text = '重新执行'
$b1.Size = New-Object System.Drawing.Size(100, 36)
$b1.Location = New-Object System.Drawing.Point(30, 35)
$b1.DialogResult = [System.Windows.Forms.DialogResult]::Retry

$b2 = New-Object System.Windows.Forms.Button
$b2.Text = '关闭'
$b2.Size = New-Object System.Drawing.Size(100, 36)
$b2.Location = New-Object System.Drawing.Point(150, 35)
$b2.DialogResult = [System.Windows.Forms.DialogResult]::Cancel

$f.Controls.AddRange(@($b1, $b2))
$f.AcceptButton = $b1
$f.CancelButton = $b2

$r = $f.ShowDialog()
if ($r -eq [System.Windows.Forms.DialogResult]::Retry) { exit 1 } else { exit 0 }
