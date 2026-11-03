# A Windows notification about how the daily update ended (called by update.cmd).
# The result is a code, so the Russian texts stay here (cmd passes no Cyrillic):
#   ok | nochange | nonet | genfail | pushfail
param([string]$Result = 'ok')

$log = Join-Path $PSScriptRoot 'logs\update.log'

# This run's part of the log: from its last "=====" heading.
$run = @()
if (Test-Path $log) {
    $lines = Get-Content $log -Encoding UTF8 -Tail 400
    $start = 0
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -like '===== *') { $start = $i; break }
    }
    $run = $lines[$start..($lines.Count - 1)]
}

# "news.json: 155 записей (3 новых сегодня)" → 3
$newToday = $null
foreach ($line in $run) {
    if ($line -match 'news\.json: .*\((\d+) ') { $newToday = [int]$Matches[1] }
}
$news = if ($null -ne $newToday) { "Новых в топах сегодня: $newToday." } else { '' }

switch ($Result) {
    'ok'       { $title = 'MovieClassics: обновлено'; $text = "Данные опубликованы. $news" }
    'nochange' { $title = 'MovieClassics: без изменений'; $text = "Новых данных нет. $news" }
    'nonet'    { $title = 'MovieClassics: ошибка'; $text = 'Нет сети, ничего не опубликовано.' }
    'genfail'  { $title = 'MovieClassics: ошибка'; $text = 'Генератор упал, ничего не опубликовано. Подробности в логе.' }
    'pushfail' { $title = 'MovieClassics: ошибка'; $text = 'Не удалось отправить данные на GitHub. Подробности в логе.' }
    default    { $title = 'MovieClassics'; $text = $Result }
}

# Last error line of the run, for the failures.
if ($Result -in 'genfail', 'pushfail') {
    $err = $run | Where-Object { $_ -match 'error|fatal|exception|ошибк' } | Select-Object -Last 1
    if ($err) { $text += "`n" + $err.Trim() }
}

[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

$esc = [System.Security.SecurityElement]
$logUri = $esc::Escape(([Uri]$log).AbsoluteUri)
$xml = @"
<toast activationType="protocol" launch="$logUri">
  <visual>
    <binding template="ToastGeneric">
      <text>$($esc::Escape($title))</text>
      <text>$($esc::Escape($text.Trim()))</text>
    </binding>
  </visual>
</toast>
"@
$doc = New-Object Windows.Data.Xml.Dom.XmlDocument
$doc.LoadXml($xml)
# Windows PowerShell's own app id: registered on every system, so the
# notification lands in the notification center without any setup.
$appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show(
    [Windows.UI.Notifications.ToastNotification]::new($doc))
