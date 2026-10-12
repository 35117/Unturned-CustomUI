# -*- coding: utf-8 -*-
# 生成 build.bat（强制 CRLF），并写入所有源文件。
import io, os

here = os.path.dirname(os.path.abspath(__file__))

lines = []
A = lines.append

A("@echo off")
A("REM ============================================================")
A("REM  CustomUI (自定义交互界面) build script")
A("REM  Output: ..\\BepInEx\\Plugins\\CustomUIMod.dll")
A("REM  NOTE: CRLF + UTF-8 (no BOM). 由 _mkbuild.py 生成，勿手改。")
A("REM ============================================================")
A("setlocal")
A("set GAME=%~dp0..")
A("set CSC=C:\\Windows\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe")
A("")
A('if not exist "%CSC%" set CSC=C:\\Windows\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe')
A("")
A('if not exist "%CSC%" (')
A("    echo [ERROR] csc.exe not found. Please install .NET Framework 4.x")
A("    exit /b 1")
A(")")
A("")
A('"%CSC%" /nologo /t:library /codepage:65001 ^')
A(' /out:"%GAME%\\BepInEx\\Plugins\\CustomUIMod.dll" ^')
A(' /r:"%GAME%\\BepInEx\\core\\BepInEx.dll" ^')
A(' /r:"%GAME%\\BepInEx\\core\\0Harmony.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\UnityEngine.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\UnityEngine.CoreModule.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\UnityEngine.IMGUIModule.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\UnityEngine.TextRenderingModule.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\UnityEngine.InputLegacyModule.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\netstandard.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\SDG.Glazier.Runtime.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\com.rlabrecque.steamworks.net.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\Assembly-CSharp.dll" ^')
A(' /r:"%GAME%\\Unturned_Data\\Managed\\UnturnedDat.dll" ^')
# 源文件清单**扫目录**，不写死。写死过一次代价：新增 CustomUI.Relay.cs 时忘了加进来，
# 编译只报 CS0103「名称不存在」—— 看着像代码写错，其实是构建脚本漏文件。
import glob as _glob
_cs = sorted(_glob.glob(os.path.join(here, "CustomUI*.cs")))
if not _cs:
    raise SystemExit("no CustomUI*.cs found in " + here)
for _i, _p in enumerate(_cs):
    _tail = " ^" if _i < len(_cs) - 1 else ""
    A(' "%~dp0' + os.path.basename(_p) + '"' + _tail)
A("")
A("if errorlevel 1 (")
A("    echo [ERROR] Compilation failed. See messages above.")
A("    exit /b 1")
A(")")
A("")
A('echo [OK] Built: %GAME%\\BepInEx\\Plugins\\CustomUIMod.dll')
A("endlocal")
A("")

data = "\r\n".join(lines)
path = os.path.join(here, "build.bat")
with io.open(path, "w", encoding="utf-8", newline="") as f:
    f.write(data)

print("WROTE", path, len(data))