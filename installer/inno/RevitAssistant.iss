; ─────────────────────────────────────────────────────────────────────────────
; RevitAssistant — Inno Setup script
;
; STATUS: authored but NEVER COMPILED. Inno Setup is not installed on the machine
; this was written on, so nothing below has been run through ISCC or installed on
; a clean box. Treat it as a starting point that still needs a real build+install
; test (see installer/README.md for the checklist) before it ships to anyone.
;
; Build:  installer\build-installer.ps1 -Version 0.1.0
; Layout: the add-in's DLLs go in their own folder next to the manifest, so they
;         cannot collide with another add-in's copy of ClosedXML/YamlDotNet/etc:
;
;   %APPDATA%\Autodesk\Revit\Addins\<year>\
;       RevitAssistant.addin          -> <Assembly>RevitAssistant\RevitAssistant.dll</Assembly>
;       RevitAssistant\               -> all product + dependency DLLs
; ─────────────────────────────────────────────────────────────────────────────

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
; Payload staged by build-installer.ps1: one subfolder per supported Revit year.
#ifndef PayloadDir
  #define PayloadDir "..\..\artifacts\installer"
#endif

#define AppName "Revit Local Assistant"
#define AppPublisher "Le Phu"
#define AddinFolderName "RevitAssistant"

[Setup]
AppId={{7F2C9A14-5E3D-4B86-A1F0-9C2D4E6B8A31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={userappdata}\Autodesk\Revit\Addins
DisableDirPage=yes
DisableProgramGroupPage=yes
; Per-user: writing to %APPDATA% needs no elevation, and Revit loads per-user add-ins.
PrivilegesRequired=lowest
OutputDir={#PayloadDir}\..\
OutputBaseFilename=RevitAssistantSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Upgrades replace the previous install in place; UninstallDisplayName keeps
; Apps & Features readable when both Revit years are installed.
UninstallDisplayName={#AppName} {#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "vi"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Cài cho mọi phiên bản Revit tìm thấy"
Name: "custom"; Description: "Tuỳ chọn"; Flags: iscustom

[Components]
Name: "r2025"; Description: "Revit 2025"; Types: full custom; Check: RevitYearPresent('2025')
Name: "r2026"; Description: "Revit 2026"; Types: full custom; Check: RevitYearPresent('2026')

[Files]
; Revit 2025 (net8.0-windows)
Source: "{#PayloadDir}\2025\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\{#AddinFolderName}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Components: r2025
Source: "{#PayloadDir}\2025.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; \
    DestName: "RevitAssistant.addin"; Flags: ignoreversion; Components: r2025

; Revit 2026 (net8.0-windows)
Source: "{#PayloadDir}\2026\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\{#AddinFolderName}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs; Components: r2026
Source: "{#PayloadDir}\2026.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; \
    DestName: "RevitAssistant.addin"; Flags: ignoreversion; Components: r2026

[UninstallDelete]
; Remove the add-in folder itself; [Files] only tracks the files it installed.
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\{#AddinFolderName}"
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\{#AddinFolderName}"

[Code]
// Only offer a Revit year that is actually installed.
function RevitYearPresent(Year: String): Boolean;
begin
  Result :=
    DirExists(ExpandConstant('{commonpf}\Autodesk\Revit ') + Year) or
    DirExists(ExpandConstant('{userappdata}\Autodesk\Revit\Addins\') + Year);
end;

// Revit holds the DLLs open; installing over a running Revit yields a half-updated
// add-in that fails to load. Refuse instead, with an instruction the user can act on.
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if Exec('cmd.exe', '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      MsgBox('Revit đang chạy. Hãy đóng Revit rồi chạy lại trình cài đặt.',
             mbCriticalError, MB_OK);
      Result := False;
    end;
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if Exec('cmd.exe', '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      MsgBox('Revit đang chạy. Hãy đóng Revit rồi gỡ cài đặt.', mbCriticalError, MB_OK);
      Result := False;
    end;
  end;
end;
