; The Windows setup wizard. It installs Velopack's portable build, which updates itself in place, into the folder the
; user picks, makes the shortcuts the user asks for, and registers an uninstaller. Velopack's own Setup.exe takes no
; choices, so this replaces it.
;
; Build with Inno Setup 7, from the extracted portable zip:
;   ISCC /DSourceDir=<portable> /DAppVersion=1.2.0 /DChannel=windows-x64 /O<output dir> Setup.iss

#ifndef SourceDir
  #error Pass /DSourceDir=<the extracted portable zip>
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Channel
  #define Channel "windows-x64"
#endif
; the Velopack app id of earlier installs; overridable to test the migration against a stand-in
#ifndef OldAppId
  #define OldAppId "KSPRetooled.Launcher"
#endif

#define AppName "KSP Retooled Launcher"
; Velopack's stub: it starts current\RetooledLauncher.exe, whichever version that is now
#define StubExe AppName + ".exe"
; version resources take only numbers, so drop any pre-release part
#define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)

[Setup]
AppId={{68B3C8C6-2AEB-4B71-A36E-3222BD44649E}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=KSP Retooled contributors
AppPublisherURL=https://github.com/KSP1Retooled/launcher
AppSupportURL=https://github.com/KSP1Retooled/launcher/issues
VersionInfoVersion={#NumericVersion}
; Only per user, under %LOCALAPPDATA%\Programs: the launcher replaces its own files when it updates, which would need
; an admin prompt in Program Files.
PrivilegesRequired=lowest
DefaultDirName={userpf}\{#AppName}
; the shortcut tasks replace the Start menu folder page
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile=..\Icons\icon.ico
UninstallDisplayIcon={app}\{#StubExe}
UninstallDisplayName={#AppName}
OutputBaseFilename=KSPRetooled.Launcher-{#Channel}-Setup
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: startmenuicon; Description: "Create a &Start menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; files an older version left behind, which Velopack would otherwise treat as part of this one
Type: filesandordirs; Name: "{app}\current"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; The same AppUserModelID that Velopack gives the running launcher, so the shortcut and the window share a taskbar button.
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#StubExe}"; AppUserModelID: "velopack.KSPRetooled.Launcher"; Tasks: desktopicon
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#StubExe}"; AppUserModelID: "velopack.KSPRetooled.Launcher"; Tasks: startmenuicon

[Run]
Filename: "{app}\{#StubExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The launcher's uninstall hook removes its settings and patch cache. Any version parses; the hook ignores it.
Filename: "{app}\current\RetooledLauncher.exe"; Parameters: "--veloapp-uninstall {#AppVersion}"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveData"

[UninstallDelete]
; what updates added: newer files in current, and the downloaded packages
Type: filesandordirs; Name: "{app}\current"
Type: filesandordirs; Name: "{app}\packages"
Type: dirifempty; Name: "{app}"

[Code]
// Earlier releases installed with Velopack's Setup.exe, into %LOCALAPPDATA%\KSPRetooled.Launcher. That copy keeps
// updating itself, so it is removed rather than left beside this one. Its uninstaller is not run, because the launcher's
// uninstall hook would delete the settings this install goes on using.
const
  OldUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#OldAppId}';

var
  OldInstallDir: String;

function FindOldInstall(): String;
var
  UninstallString: String;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, OldUninstallKey, 'UninstallString', UninstallString) then
    exit;
  if Pos('update.exe', Lowercase(UninstallString)) = 0 then
    exit;
  if RegQueryStringValue(HKCU, OldUninstallKey, 'InstallLocation', Result) and not DirExists(Result) then
    Result := '';
end;

function InitializeSetup(): Boolean;
begin
  OldInstallDir := FindOldInstall();
  if OldInstallDir <> '' then
    Log('Found an earlier installation in ' + OldInstallDir);
  Result := True;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
  if OldInstallDir <> '' then
    Result := Result + NewLine + NewLine + 'Remove the earlier installation:' + NewLine + Space + OldInstallDir;
end;

function RemoveOldInstall(): String;
var
  Doomed: String;
begin
  Result := '';
  // A running launcher holds its files open, so the folder can't be renamed; that is checked before anything is
  // deleted, so a failure leaves the earlier installation working.
  Doomed := RemoveBackslashUnlessRoot(OldInstallDir) + '.removing';
  if DirExists(Doomed) then
    DelTree(Doomed, True, True, True);
  if not RenameFile(OldInstallDir, Doomed) then
  begin
    Result := 'Close {#AppName} and try again. It is still running from ' + OldInstallDir + '.';
    exit;
  end;
  DelTree(Doomed, True, True, True);
  DeleteFile(ExpandConstant('{userdesktop}\{#AppName}.lnk'));
  DeleteFile(ExpandConstant('{userprograms}\{#AppName}.lnk'));
  RegDeleteKeyIncludingSubkeys(HKCU, OldUninstallKey);
  Log('Removed the earlier installation in ' + OldInstallDir);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if OldInstallDir <> '' then
    Result := RemoveOldInstall();
end;
