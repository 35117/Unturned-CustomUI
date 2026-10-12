@echo off
REM ============================================================
REM  CustomUI (自定义交互界面) build script
REM  Output: ..\BepInEx\Plugins\CustomUIMod.dll
REM  NOTE: CRLF + UTF-8 (no BOM). 由 _mkbuild.py 生成，勿手改。
REM ============================================================
setlocal
set GAME=%~dp0..
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please install .NET Framework 4.x
    exit /b 1
)

"%CSC%" /nologo /t:library /codepage:65001 ^
 /out:"%GAME%\BepInEx\Plugins\CustomUIMod.dll" ^
 /r:"%GAME%\BepInEx\core\BepInEx.dll" ^
 /r:"%GAME%\BepInEx\core\0Harmony.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\UnityEngine.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\UnityEngine.CoreModule.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\UnityEngine.IMGUIModule.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\UnityEngine.TextRenderingModule.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\UnityEngine.InputLegacyModule.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\netstandard.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\SDG.Glazier.Runtime.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\com.rlabrecque.steamworks.net.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\Assembly-CSharp.dll" ^
 /r:"%GAME%\Unturned_Data\Managed\UnturnedDat.dll" ^
 "%~dp0CustomUI.Bar.cs" ^
 "%~dp0CustomUI.BlueprintView.cs" ^
 "%~dp0CustomUI.Crafting.cs" ^
 "%~dp0CustomUI.Dropdown.cs" ^
 "%~dp0CustomUI.Fill.cs" ^
 "%~dp0CustomUI.FilterBar.cs" ^
 "%~dp0CustomUI.Form.cs" ^
 "%~dp0CustomUI.GridPanel.cs" ^
 "%~dp0CustomUI.GridView.cs" ^
 "%~dp0CustomUI.Host.cs" ^
 "%~dp0CustomUI.ItemIcon.cs" ^
 "%~dp0CustomUI.ListPanel.cs" ^
 "%~dp0CustomUI.ListView.cs" ^
 "%~dp0CustomUI.Menu.cs" ^
 "%~dp0CustomUI.Patches.cs" ^
 "%~dp0CustomUI.PinyinData.cs" ^
 "%~dp0CustomUI.Provider.cs" ^
 "%~dp0CustomUI.Region.cs" ^
 "%~dp0CustomUI.Relay.cs" ^
 "%~dp0CustomUI.Search.cs" ^
 "%~dp0CustomUI.SearchBox.cs" ^
 "%~dp0CustomUI.Shapes.cs" ^
 "%~dp0CustomUI.Shared.cs" ^
 "%~dp0CustomUI.Tooltip.cs" ^
 "%~dp0CustomUI.cs"

if errorlevel 1 (
    echo [ERROR] Compilation failed. See messages above.
    exit /b 1
)

echo [OK] Built: %GAME%\BepInEx\Plugins\CustomUIMod.dll
endlocal
