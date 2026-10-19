<#
.SYNOPSIS
  Pulsa un control de LiveStream por su nombre accesible (UI Automation), sin mover el ratón.
  Sirve para verificar la interfaz de forma reproducible. Busca en todas las ventanas del proceso
  (principal, diálogos y menús emergentes).
.PARAMETER Name
  Nombre accesible exacto (AutomationProperties.Name o el texto del botón/elemento de menú).
.PARAMETER Index
  Si hay varios controles con ese nombre, cuál usar (0 = el primero).
.PARAMETER List
  Muestra los nombres de los controles que se pueden pulsar en vez de pulsar uno.
#>
param(
    [Parameter(Mandatory)] [int] $ProcessId,
    [string] $Name,
    [int] $Index = 0,
    [switch] $List
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$byProcess = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $byProcess)
if ($windows.Count -eq 0) { throw "El proceso $ProcessId no tiene ventanas." }

$all = foreach ($w in $windows) {
    $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

if ($List) {
    $all | Where-Object { $_.Current.Name } |
        ForEach-Object { '{0,-14} {1}' -f $_.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), $_.Current.Name } |
        Sort-Object -Unique
    return
}

$patterns = @(
    [System.Windows.Automation.InvokePattern]::Pattern,
    [System.Windows.Automation.TogglePattern]::Pattern,
    [System.Windows.Automation.SelectionItemPattern]::Pattern,
    [System.Windows.Automation.ExpandCollapsePattern]::Pattern)

# Solo cuentan los controles accionables: el texto interior de un botón o menú comparte su nombre.
$actionable = @(foreach ($e in $all) {
    if ($e.Current.Name -ne $Name -or -not $e.Current.IsEnabled) { continue }
    foreach ($pattern in $patterns) {
        $p = $null
        if ($e.TryGetCurrentPattern($pattern, [ref] $p)) { [pscustomobject]@{ Pattern = $pattern; Instance = $p }; break }
    }
})
if ($actionable.Count -le $Index) {
    # Avalonia no expone Invoke en los elementos de menú finales: se enfocan y se pulsa Intro.
    $menu = @($all | Where-Object { $_.Current.Name -eq $Name -and $_.Current.IsEnabled -and
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem })
    if ($menu.Count -gt $Index) {
        Add-Type -AssemblyName System.Windows.Forms
        $menu[$Index].SetFocus()
        Start-Sleep -Milliseconds 150
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        "Pulsado: $Name (menú: foco + Intro)"
        return
    }
    throw "No hay ningún control accionable llamado '$Name' (índice $Index; encontrados $($actionable.Count))."
}
$target = $actionable[$Index]
switch ($target.Pattern.ProgrammaticName) {
    'InvokePatternIdentifiers.Pattern' { $target.Instance.Invoke() }
    'TogglePatternIdentifiers.Pattern' { $target.Instance.Toggle() }
    'SelectionItemPatternIdentifiers.Pattern' { $target.Instance.Select() }
    'ExpandCollapsePatternIdentifiers.Pattern' { $target.Instance.Expand() }
}
"Pulsado: $Name ($($target.Pattern.ProgrammaticName.Split('.')[0]))"
