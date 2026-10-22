$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$outputDir = $PSScriptRoot
$themes = @(
 @{id='A';name='雾白蓝';tag='轻量 · 清楚 · 接近系统工具';bg='#EDF1F7';surface='#FFFFFF';ink='#1D2A3C';muted='#637186';accent='#3567D6';soft='#EDF3FF';line='#DEE5EF';on='#FFFFFF';radius='16';font='Microsoft YaHei UI';mark='lens'},
 @{id='B';name='暖纸墨绿';tag='书页质感 · 安静 · 更像一本词典';bg='#EDEAE2';surface='#FFFCF5';ink='#2C3831';muted='#6C746B';accent='#356651';soft='#EDF0E5';line='#DFE2D6';on='#FFFFFF';radius='8';font='SimSun';mark='book'},
 @{id='C';name='石墨薄荷';tag='深色桌面 · 克制的亮色 · 工具感';bg='#101714';surface='#1C2521';ink='#EDF4EF';muted='#A3B4A9';accent='#98DFB8';soft='#293C31';line='#37473E';on='#153323';radius='14';font='Microsoft YaHei UI';mark='nodes'},
 @{id='D';name='黑白朱砂';tag='强对比 · 留白 · 鲜明的标记感';bg='#F0EFED';surface='#FFFFFF';ink='#222222';muted='#70706E';accent='#BD4537';soft='#FAEEEA';line='#E3E1DE';on='#FFFFFF';radius='3';font='Microsoft YaHei UI';mark='frame'}
)
function Esc([string]$s) { [Security.SecurityElement]::Escape($s) }
function Label([string]$value,[double]$x,[double]$y,[double]$size=14,[string]$color='', [double]$width=0,[string]$weight='Normal',[string]$font='Microsoft YaHei UI') {
 if (!$color) {$color=$t.ink}; $w=if($width -gt 0){"Width='$width'"}else{''}
 "<TextBlock Canvas.Left='$x' Canvas.Top='$y' $w FontFamily='$font' FontSize='$size' FontWeight='$weight' Foreground='$color' TextWrapping='Wrap' LineHeight='$($size*1.65)' Text='$(Esc $value)'/>"
}
function Box([double]$x,[double]$y,[double]$w,[double]$h,[string]$fill,[double]$r=10,[string]$stroke='Transparent') {
 $thickness=if($stroke -eq 'Transparent'){0}else{1}
 "<Border Canvas.Left='$x' Canvas.Top='$y' Width='$w' Height='$h' Background='$fill' BorderBrush='$stroke' BorderThickness='$thickness' CornerRadius='$r'/>"
}
function Rule([double]$x,[double]$y,[double]$w) { Box $x $y $w 1 $t.line 0 }
function Symbol([double]$x,[double]$y,[double]$size) {
 $shape=switch($t.mark){
 'lens' { "<Ellipse Width='13' Height='13' Canvas.Left='7' Canvas.Top='6' Stroke='$($t.on)' StrokeThickness='2.3'/><Path Data='M18,18 L25,25 M10,12 L16,12 M13,9 L13,15' Stroke='$($t.on)' StrokeThickness='2' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>" }
 'book' { "<Path Data='M5,7 Q11,5 16,9 Q21,5 27,7 L27,24 Q21,22 16,26 Q11,22 5,24 Z M16,9 L16,26' Stroke='$($t.on)' StrokeThickness='1.8' StrokeLineJoin='Round' Fill='Transparent'/>" }
 'nodes' { "<Path Data='M9,23 L16,10 L24,23 M9,23 L24,23' Stroke='$($t.on)' StrokeThickness='1.8'/><Ellipse Width='6' Height='6' Canvas.Left='13' Canvas.Top='6' Fill='$($t.on)'/><Ellipse Width='6' Height='6' Canvas.Left='5' Canvas.Top='20' Fill='$($t.on)'/><Ellipse Width='6' Height='6' Canvas.Left='21' Canvas.Top='20' Fill='$($t.on)'/>" }
 'frame' { "<Path Data='M11,6 L6,6 L6,26 L11,26 M21,6 L26,6 L26,26 L21,26 M12,14 L20,14 M12,19 L18,19' Stroke='$($t.on)' StrokeThickness='2' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>" }
 }
 "<Viewbox Canvas.Left='$x' Canvas.Top='$y' Width='$size' Height='$size'><Grid Width='32' Height='32'><Border Background='$($t.accent)' CornerRadius='$([Math]::Min([double]$t.radius,9))'/><Canvas>$shape</Canvas></Grid></Viewbox>"
}
function Action([string]$value,[double]$x,[double]$y,[double]$w,[bool]$primary=$false) {
 $fill=if($primary){$t.accent}else{$t.soft}; $fg=if($primary){$t.on}else{$t.accent}
 (Box $x $y $w 38 $fill ([Math]::Min([double]$t.radius,8))) + (Label $value ($x+14) ($y+7) 13 $fg)
}
function Render([string]$xaml,[string]$file,[int]$w,[int]$h) {
 $visual=[Windows.Markup.XamlReader]::Parse($xaml)
 $visual.Measure([Windows.Size]::new($w,$h)); $visual.Arrange([Windows.Rect]::new(0,0,$w,$h)); $visual.UpdateLayout()
 $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($w,$h,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($visual)
 $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream=[IO.File]::Create($file); try {$encoder.Save($stream)} finally {$stream.Dispose()}
}
foreach($t in $themes){
 $parts=[Collections.Generic.List[string]]::new()
 $parts.Add((Label ($t.id+'  /  '+$t.name) 36 24 28 $t.ink 650 'SemiBold'))
 $parts.Add((Label $t.tag 36 74 13 $t.muted))
 $parts.Add((Label 'TERMLENS / 视觉提案' 768 36 12 $t.muted))
 # Palette, desktop icon, and compact selection badge are intentionally visible.
 $parts.Add((Label '配色' 36 139 12 $t.muted))
 for($i=0;$i -lt 4;$i++) { $color=@($t.surface,$t.ink,$t.accent,$t.soft)[$i]; $parts.Add((Box (36+$i*32) 168 23 23 $color 11 $t.line)) }
 $parts.Add((Label '桌面图标' 36 220 12 $t.muted))
 $parts.Add((Symbol 36 254 72))
 $parts.Add((Label '划词入口' 36 359 12 $t.muted))
 $parts.Add((Symbol 36 394 32))
 $parts.Add((Symbol 84 400 20))
 $parts.Add((Label '32 px / 20 px' 36 446 11 $t.muted))
 $parts.Add((Label '设计预览' 36 554 12 $t.muted))
 $parts.Add((Label '选定后再应用' 36 580 12 $t.muted))
 # Main window, compact enough to support quick lookup without a marketing hero.
 $parts.Add((Label '主窗口' 210 112 12 $t.muted))
 $parts.Add((Box 210 140 360 406 $t.surface ([double]$t.radius) $t.line))
 $parts.Add((Symbol 232 162 28))
 $parts.Add((Label '术语镜' 270 160 16 $t.ink 160 'SemiBold'))
 $parts.Add((Label '—     ×' 512 163 13 $t.muted))
 $parts.Add((Rule 230 208 320))
 if($t.id -eq 'B'){
  $parts.Add((Label '把陌生概念，读明白。' 234 231 23 $t.ink 300 'Normal' $t.font))
  $parts.Add((Label '中文 / English' 234 277 12 $t.muted))
 }elseif($t.id -eq 'D'){
  $parts.Add((Box 234 239 5 31 $t.accent 0))
  $parts.Add((Label '查清一个概念。' 251 230 25 $t.ink 300 'Bold'))
  $parts.Add((Label 'TERM LOOKUP' 234 278 11 $t.muted))
 }else{
  $parts.Add((Label '今天想了解什么？' 234 234 22 $t.ink 300 'SemiBold'))
  $parts.Add((Label '输入中文或英文术语' 234 278 12 $t.muted))
 }
 $parts.Add((Box 234 311 312 51 $t.surface 8 $t.line))
 $parts.Add((Label '随机森林' 248 321 16 $t.ink))
 $parts.Add((Label '↵' 519 321 17 $t.muted))
 $parts.Add((Label '试试： 梯度下降  ·  Overfitting' 234 376 12 $t.muted))
 $parts.Add((Label '+ 补充原句' 234 416 13 $t.accent))
 $parts.Add((Action '查解释  →' 433 407 113 $true))
 $parts.Add((Rule 230 473 320))
 $parts.Add((Box 235 501 7 7 $t.accent 4))
 $parts.Add((Label '划词已开启' 251 491 12 $t.muted))
 $parts.Add((Label '连接设置' 489 491 12 $t.muted))
 $parts.Add((Label '复制文字后，也可按 Ctrl + Alt + D 查询' 215 566 11 $t.muted 350))
 # Explanation card: readable hierarchy, restrained accents, collapsed secondary actions.
 $parts.Add((Label '选词旁的解释卡片' 602 112 12 $t.muted))
 $parts.Add((Box 602 140 362 485 $t.surface ([double]$t.radius) $t.line))
 $parts.Add((Symbol 624 159 20))
 $parts.Add((Label '术语镜' 653 156 12 $t.muted))
 $parts.Add((Label '···' 903 153 17 $t.muted))
 $parts.Add((Label '×' 934 154 17 $t.muted))
 $parts.Add((Label '随机森林' 626 193 28 $t.ink 300 'SemiBold' $t.font))
 $parts.Add((Label 'Random Forest' 627 244 13 $t.muted))
 $parts.Add((Box 626 278 178 25 $t.soft 5))
 $parts.Add((Label '机器学习 · 当前解释含义' 634 281 11 $t.accent))
 $parts.Add((Label '一句话理解' 626 322 12 $t.accent 310 'SemiBold'))
 $parts.Add((Label '让多棵决策树共同判断，综合它们的结果，完成分类或预测。' 626 348 15 $t.ink 310))
 if($t.id -eq 'B' -or $t.id -eq 'D'){
  $parts.Add((Box 626 411 2 66 $t.accent 0))
  $parts.Add((Label '打个比方' 640 407 12 $t.accent 280 'SemiBold'))
  $parts.Add((Label '像请多位评委共同投票，而不是只听一位评委的意见。' 640 432 14 $t.ink 286))
 }else{
  $parts.Add((Box 626 408 314 78 $t.soft 8))
  $parts.Add((Label '打个比方' 640 417 12 $t.accent 280 'SemiBold'))
  $parts.Add((Label '像请多位评委共同投票，而不是只听一位评委的意见。' 640 441 13 $t.ink 280))
 }
 $parts.Add((Label '实际用途' 626 497 12 $t.accent 310 'SemiBold'))
 $parts.Add((Label '例如：垃圾邮件识别、房价预测。' 626 522 14 $t.ink 310))
 $parts.Add((Rule 626 560 314))
 $parts.Add((Label '+ 补充原句重查' 626 580 12 $t.accent))
 $parts.Add((Action '复制解释' 833 573 107 $false))
 $parts.Add((Label 'AI 生成 · 点击外部关闭' 606 633 10 $t.muted))
 $xaml="<Canvas xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='1000' Height='660' Background='$($t.bg)' TextOptions.TextFormattingMode='Display' TextOptions.TextRenderingMode='ClearType'>$($parts -join "`n")</Canvas>"
 [IO.File]::WriteAllText((Join-Path $outputDir ($t.id+'.xaml')),$xaml)
 Render $xaml (Join-Path $outputDir ($t.id+'.png')) 1000 660
 Write-Output ('Rendered '+$t.id+' '+$t.name)
}
$images=''
foreach($item in @(@('A',0,0),@('B',1000,0),@('C',0,660),@('D',1000,660))){
 $uri=[Uri]::new((Join-Path $outputDir ($item[0]+'.png'))).AbsoluteUri
 $images+="<Image Canvas.Left='$($item[1])' Canvas.Top='$($item[2])' Width='1000' Height='660' Source='$uri'/>"
}
Render "<Canvas xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Width='2000' Height='1320'>$images</Canvas>" (Join-Path $outputDir 'comparison.png') 2000 1320
