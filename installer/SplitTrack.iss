; Installs the SplitTrack app into Program Files (needs admin once), ffmpeg in {app}\ffmpeg.
; Build it with tools\build-installer.ps1, which publishes the app and passes AppDir.

#define AppName "SplitTrack"
#define AppExe "SplitTrack.exe"
#ifndef AppDir
  #define AppDir "..\out\app"
#endif
#define AppVersion GetVersionNumbersString(AddBackslash(AppDir) + AppExe)

[Setup]
; Never change AppId: upgrades and the uninstaller find the installed copy by it.
AppId={{ECBE27F1-80F6-49F9-96AD-3437EE3CD33D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; The only per-user thing is removing the HKCU Run value at uninstall.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Process loopback needs build 20348.
MinVersion=10.0.20348
OutputDir=..\out\installer
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; The [Code] section closes a running copy with --exit instead.
CloseApplications=no

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; A newer FFmpeg has differently named DLLs; drop the old ones.
Type: filesandordirs; Name: "{app}\ffmpeg"

[Files]
Source: "{#AppDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\ffmpeg\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
; Inno's uninstaller is always unins000.exe; this gives it a clear name in the install folder.
Name: "{app}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
; Remove the "Start with Windows" entry the app may have written.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppName}"; Flags: uninsdeletevalue dontcreatekey

[Run]
; postinstall runs as the original user, so the app itself is never elevated.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  AppMutex = 'Local\SplitTrack.App';
  StillRunning = 'SplitTrack is still running. Exit it from its tray icon, then try again.';

// Asks a running SplitTrack to exit (it finishes a recording first) and waits up to a minute for it.
function CloseRunningApp(const Exe: String): Boolean;
var
  ResultCode, Waited: Integer;
begin
  if CheckForMutexes(AppMutex) and FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Waited := 0;
  while CheckForMutexes(AppMutex) and (Waited < 60000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  Result := not CheckForMutexes(AppMutex);
end;

// The new exe can close any running copy, including one that is not installed.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if CheckForMutexes(AppMutex) then
    ExtractTemporaryFile('{#AppExe}');
  if not CloseRunningApp(ExpandConstant('{tmp}\{#AppExe}')) then
    Result := StillRunning;
end;

function InitializeUninstall(): Boolean;
begin
  Result := CloseRunningApp(ExpandConstant('{app}\{#AppExe}'));
  if not Result then
    MsgBox(StillRunning, mbError, MB_OK);
end;
