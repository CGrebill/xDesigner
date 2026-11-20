; xDesigner installer (Inno Setup 6). Built by build\publish.ps1, which passes:
;   /DAppVersion=1.0.0  /DPublishDir=<app publish folder>  /DLicenseFile=<license text>  /DOutputDir=<folder>
;
; Per-user install (no administrator rights): %LOCALAPPDATA%\Programs\xDesigner.
; The uninstaller removes everything the installer put there, the shortcuts, the uninstall entry, and the folder the
; app writes to itself (%LOCALAPPDATA%\xDesigner: settings and error log). It never touches the user's projects.

#ifndef AppVersion
  #error AppVersion is required (build\publish.ps1 passes it).
#endif
#ifndef PublishDir
  #error PublishDir is required (build\publish.ps1 passes it).
#endif
#ifndef LicenseFile
  #error LicenseFile is required (build\publish.ps1 passes it).
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "xDesigner"
#define AppExe "xDesigner.exe"
#define RepoUrl "https://github.com/CGrebill/xDesigner"

[Setup]
; Never change AppId: upgrades and the uninstaller find the installed app by it.
AppId={{8082400E-0A67-4B67-B3D8-650B1C5AE23B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=CGrebill
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright 2026 CGrebill
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} setup
; Per-user: {autopf} is %LOCALAPPDATA%\Programs when running without administrator rights.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DefaultGroupName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004 or later (the Windows App SDK's minimum).
MinVersion=10.0.19041
LicenseFile={#LicenseFile}
SetupIconFile=..\src\XDesigner.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-x64-setup
; Held by the running designer: setup and uninstall ask to close it first.
AppMutex=xDesigner.Running
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
UninstalledAll=%1 was removed from your computer, together with its settings.%n%nYour own projects were not touched.

[Types]
Name: "full"; Description: "All renderers"
Name: "custom"; Description: "Choose which frameworks to design"; Flags: iscustom

; One renderer per framework version (renderhosts\{folder}, FrameworkProfile.HostFolder). Each carries its own runtime and
; framework libraries, so leaving out the ones you don't use keeps the install small.
[Components]
Name: "app"; Description: "xDesigner"; Types: full custom; Flags: fixed
Name: "avalonia12"; Description: "Avalonia 12 renderer"; Types: full custom
Name: "avalonia11"; Description: "Avalonia 11 renderer (projects on Avalonia 11.1 or later)"; Types: full
Name: "winui3"; Description: "WinUI 3 renderer (Windows App SDK projects)"; Types: full
Name: "maui11"; Description: ".NET MAUI 11 renderer"; Types: full
Name: "maui10"; Description: ".NET MAUI 10 renderer"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; An upgrade starts from an empty folder, so no file of an older version (or of a renderer no longer chosen) is left behind.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#PublishDir}\*"; Excludes: "\renderhosts\*"; DestDir: "{app}"; Components: app; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\renderhosts\avalonia12\*"; DestDir: "{app}\renderhosts\avalonia12"; Components: avalonia12; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\renderhosts\avalonia11\*"; DestDir: "{app}\renderhosts\avalonia11"; Components: avalonia11; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\renderhosts\winui3\*"; DestDir: "{app}\renderhosts\winui3"; Components: winui3; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\renderhosts\maui11\*"; DestDir: "{app}\renderhosts\maui11"; Components: maui11; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDir}\renderhosts\maui10\*"; DestDir: "{app}\renderhosts\maui10"; Components: maui10; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "A visual designer for Avalonia UI, WinUI 3 and .NET MAUI"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Everything in the install folder, including files the installer didn't list.
Type: filesandordirs; Name: "{app}"
; What the app writes for itself: settings.json and errors.log.
Type: filesandordirs; Name: "{localappdata}\xDesigner"

[Code]
var
  SdkNote: String;

{ True when "dotnet --list-sdks" reports a .NET 10 (or later) SDK. }
function HasDotNetSdk(): Boolean;
var
  Output: TExecOutput;
  ResultCode: Integer;
  I, Major: Integer;
  Line: String;
begin
  Result := False;
  if not ExecAndCaptureOutput('dotnet', '--list-sdks', '', SW_HIDE, ewWaitUntilTerminated, ResultCode, Output) then
    Exit;
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
  begin
    Line := Output.StdOut[I];
    if Pos('.', Line) > 1 then
    begin
      Major := StrToIntDef(Copy(Line, 1, Pos('.', Line) - 1), 0);
      if Major >= 10 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if HasDotNetSdk() then
      SdkNote := ''
    else
      SdkNote := #13#10#13#10 + 'Note: the .NET 10 SDK was not found. xDesigner opens and edits XAML files without it, ' +
        'but building and running projects needs it: https://dotnet.microsoft.com/download';
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and (SdkNote <> '') then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + SdkNote;
end;
